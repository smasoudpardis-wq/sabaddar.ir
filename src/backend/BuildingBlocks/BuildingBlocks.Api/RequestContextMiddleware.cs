using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.BuildingBlocks.Api;

/// <summary>
/// After JWT validation: maps (iss, sub) to an internal user, loads the user's active memberships, and accepts the
/// active tenant chosen by the BFF (header <see cref="TenantHeader"/>) only if the user is a member of it (BR-001).
/// The browser cannot reach the API directly (D04 §3.3); the BFF sets the header from its server-side session.
/// </summary>
public sealed class RequestContextMiddleware(RequestDelegate next)
{
    public const string TenantHeader = "X-RC-Tenant";
    private static readonly TimeSpan MembershipCacheTtl = TimeSpan.FromSeconds(30); // IAM-009: revocation visible ≤ 60 s

    public async Task InvokeAsync(HttpContext http, RequestContext context, IUserDirectory directory, IMemoryCache cache)
    {
        if (http.User.Identity?.IsAuthenticated != true)
        {
            await next(http);
            return;
        }

        string? issuer = http.User.FindFirst("iss")?.Value;
        string? subject = http.User.FindFirst("sub")?.Value;
        if (issuer is null || subject is null)
        {
            await Problems.WriteAsync(http, Problems.Create(http, StatusCodes.Status401Unauthorized, "invalid-token", "Token has no subject"));
            return;
        }

        context.Issuer = issuer;
        context.Subject = subject;
        context.TokenName = http.User.FindFirst("name")?.Value;
        context.Acr = http.User.FindFirst("acr")?.Value;
        context.AuthTime = long.TryParse(http.User.FindFirst("auth_time")?.Value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out long authTime)
            ? DateTimeOffset.FromUnixTimeSeconds(authTime)
            : null;

        var user = await directory.FindActiveUserAsync(issuer, subject, http.RequestAborted);
        if (user is null && http.GetEndpoint()?.Metadata.GetMetadata<AllowUnprovisionedUserAttribute>() is not null)
        {
            await next(http); // self-service sign-up: the handler provisions the user (UC-10)
            return;
        }

        if (user is null)
        {
            await Problems.WriteAsync(http, Problems.Create(http, StatusCodes.Status403Forbidden, "user-not-provisioned",
                "The identity is valid but not an active RetailCloud user"));
            return;
        }

        IReadOnlyList<MembershipInfo> memberships = await cache.GetOrCreateAsync(
            $"memberships:{user.Value.Id.Value}",
            async e =>
            {
                IReadOnlyList<MembershipInfo> loaded = await directory.ActiveMembershipsAsync(user.Value.Id, http.RequestAborted);
                // An empty list is typical right after sign-up, while the owner membership is being provisioned (US-011).
                e.AbsoluteExpirationRelativeToNow = loaded.Count == 0 ? TimeSpan.FromSeconds(1) : MembershipCacheTtl;
                return loaded;
            }) ?? [];

        context.User = user.Value.Id;
        context.DisplayName = user.Value.DisplayName;
        context.Memberships = memberships;

        if (http.Request.Headers.TryGetValue(TenantHeader, out var raw))
        {
            MembershipInfo? membership = Guid.TryParse(raw.ToString(), out Guid requested)
                ? memberships.FirstOrDefault(m => m.TenantId.Value == requested)
                : null;
            if (membership is null)
            {
                await Problems.WriteAsync(http, Problems.NotFound(http)); // never reveal whether the tenant exists
                return;
            }

            // IAM-003 / US-022: a sensitive role in this tenant needs an MFA token for every tenant-scoped call.
            if (MfaPolicy.Requires(membership) && !context.MfaSatisfied)
            {
                await Problems.WriteAsync(http, MfaPolicy.MfaRequired(http));
                return;
            }

            context.ActiveTenant = membership.TenantId;
            context.ActiveMembership = membership;
        }

        await next(http);
    }
}

/// <summary>Endpoint filter for tenant-scoped operations (all except identity and tenant selection).</summary>
public sealed class RequireActiveTenantFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        RequestContext ctx = context.HttpContext.RequestServices.GetRequiredService<RequestContext>();
        return ctx.ActiveTenant is null
            ? Problems.Create(context.HttpContext, StatusCodes.Status403Forbidden, "tenant-not-selected", "Select an active tenant first")
            : await next(context);
    }
}

/// <summary>
/// Marks the one endpoint a valid identity may call before it is a RetailCloud user: tenant registration (UC-10).
/// Every other endpoint answers <c>403 user-not-provisioned</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowUnprovisionedUserAttribute : Attribute;

