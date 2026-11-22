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

        var user = await directory.FindActiveUserAsync(issuer, subject, http.RequestAborted);
        if (user is null)
        {
            await Problems.WriteAsync(http, Problems.Create(http, StatusCodes.Status403Forbidden, "user-not-provisioned",
                "The identity is valid but not an active RetailCloud user"));
            return;
        }

        IReadOnlyList<MembershipInfo> memberships = await cache.GetOrCreateAsync(
            $"memberships:{user.Value.Id.Value}",
            e =>
            {
                e.AbsoluteExpirationRelativeToNow = MembershipCacheTtl;
                return directory.ActiveMembershipsAsync(user.Value.Id, http.RequestAborted);
            }) ?? [];

        context.User = user.Value.Id;
        context.DisplayName = user.Value.DisplayName;
        context.Memberships = memberships;

        if (http.Request.Headers.TryGetValue(TenantHeader, out var raw))
        {
            if (!Guid.TryParse(raw.ToString(), out Guid requested) ||
                memberships.All(m => m.TenantId.Value != requested))
            {
                await Problems.WriteAsync(http, Problems.NotFound(http)); // never reveal whether the tenant exists
                return;
            }

            context.ActiveTenant = new TenantId(requested);
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
