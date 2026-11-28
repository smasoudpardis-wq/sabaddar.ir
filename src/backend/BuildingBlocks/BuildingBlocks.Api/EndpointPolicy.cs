using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.BuildingBlocks.Api;

/// <summary>
/// IAM-006 / AT-015: the explicit policy of an endpoint — a permission of D02 §3.2, or the pseudo-permissions
/// <see cref="Permissions.TenantMember"/> / <see cref="Permissions.Authenticated"/>. It must equal the operation's
/// <c>x-permission</c> in D07 (checked by a test). An endpoint without it is denied at runtime (<see cref="DefaultDenyFilter"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class EndpointPolicyAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}

public static class EndpointPolicies
{
    /// <summary>Requires an active tenant and a role granting <paramref name="permission"/> somewhere in it. Resource
    /// scope (which branch/warehouse/register) is checked by the handler with <see cref="AccessPolicy.Allows"/>.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission) where TBuilder : IEndpointConventionBuilder
    {
        if (!Permissions.All.Contains(permission))
        {
            throw new ArgumentException($"Unknown permission '{permission}'", nameof(permission));
        }

        builder.WithMetadata(new EndpointPolicyAttribute(permission));
        builder.AddEndpointFilter(new RequirePermissionFilter(permission));
        return builder;
    }

    public static TBuilder RequireTenantMember<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new EndpointPolicyAttribute(Permissions.TenantMember));
        builder.AddEndpointFilter(new RequireActiveTenantFilter());
        return builder;
    }

    /// <summary>Any validated identity: the endpoint itself must not reveal tenant data (e.g. /me, sign-up).</summary>
    public static TBuilder AllowAnyAuthenticated<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new EndpointPolicyAttribute(Permissions.Authenticated));

    /// <summary>TEN-004 / US-015: this write stays open while the tenant is read-only. Must equal D07
    /// <c>x-allowed-when-read-only</c> (EndpointPolicyTests); every other write of a read-only tenant gets 403.</summary>
    public static TBuilder AllowWhenReadOnly<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AllowedWhenReadOnlyAttribute());

    internal static IResult Forbidden(HttpContext http) =>
        Problems.Create(http, StatusCodes.Status403Forbidden, "forbidden", "Your role does not allow this operation");
}

internal sealed class RequirePermissionFilter(string permission) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext http = context.HttpContext;
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        if (ctx.ActiveMembership is null)
        {
            return Problems.Create(http, StatusCodes.Status403Forbidden, "tenant-not-selected", "Select an active tenant first");
        }

        return AccessPolicy.HasAnywhere(ctx.ActiveMembership.Assignments, permission)
            ? await next(context)
            : EndpointPolicies.Forbidden(http);
    }
}

/// <summary>Group filter on /api/v1: any endpoint that forgot to declare a policy is refused (Default Deny, IAM-006).</summary>
public sealed class DefaultDenyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        return context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EndpointPolicyAttribute>() is null
            ? EndpointPolicies.Forbidden(context.HttpContext)
            : await next(context);
    }
}

/// <summary>ABAC-04 / D-46: POS operations only from an Active registered terminal of the resource's branch.</summary>
public sealed class RequireTerminalFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        RequestContext ctx = context.HttpContext.RequestServices.GetRequiredService<RequestContext>();
        return ctx.Terminal is null
            ? Problems.Create(context.HttpContext, StatusCodes.Status403Forbidden, "terminal-not-registered", "This device is not a registered terminal")
            : await next(context);
    }
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowedWhenReadOnlyAttribute : Attribute;

/// <summary>
/// TEN-004 / US-015: a suspended, closed or trial-expired tenant can read everything but write only what is marked
/// <see cref="EndpointPolicies.AllowWhenReadOnly{TBuilder}"/>. Runs for every /api/v1 endpoint; reads pass untouched.
/// </summary>
public sealed class ReadOnlyTenantFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        HttpContext http = context.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method)
            || http.GetEndpoint()?.Metadata.GetMetadata<AllowedWhenReadOnlyAttribute>() is not null)
        {
            return await next(context);
        }

        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        if (ctx.ActiveTenant is not { } tenant)
        {
            return await next(context);   // no tenant yet (sign-up) or a tenant from the URL, checked by that handler
        }

        string? reason = await http.RequestServices.GetRequiredService<ITenantReadOnlyQuery>().ReasonAsync(tenant, http.RequestAborted);
        return reason is null ? await next(context) : Problems.TenantReadOnly(http, reason);
    }
}
