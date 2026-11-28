using System.Collections.Frozen;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.BuildingBlocks.Api;

/// <summary>
/// US-022. IAM-003: TenantOwner, TenantAdmin and Accountant must authenticate with a second factor (TOTP or WebAuthn).
/// Keycloak reports the level as <c>acr</c> (<c>pwd</c> = 1, <c>mfa</c> = 2); the role is per tenant, so the API decides.
/// ABAC-05: sensitive operations additionally require an authentication no older than <see cref="SensitiveFreshness"/>.
/// </summary>
public static class MfaPolicy
{
    public const string MfaAcr = "mfa";

    public static readonly FrozenSet<string> RolesRequiringMfa =
        new[] { "TenantOwner", "TenantAdmin", "Accountant" }.ToFrozenSet(StringComparer.Ordinal);

    public static readonly TimeSpan SensitiveFreshness = TimeSpan.FromMinutes(15);

    public static bool Requires(MembershipInfo membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        return membership.Roles.Any(RolesRequiringMfa.Contains);
    }

    public static IResult MfaRequired(HttpContext http) =>
        Problems.Create(http, StatusCodes.Status403Forbidden, "mfa-required", "This role requires two-factor authentication");

    /// <summary>Marks an endpoint as sensitive (customers.pii.view, integrations.manage, tax.manage, exports — ABAC-05).</summary>
    public static TBuilder RequireRecentMfa<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new RequireRecentMfaFilter(SensitiveFreshness));
}

/// <summary>Step-up: <c>403 step-up-required</c> unless the token is MFA and <c>auth_time</c> is within the window.</summary>
public sealed class RequireRecentMfaFilter(TimeSpan maxAge) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        HttpContext http = context.HttpContext;
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        DateTimeOffset now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        bool fresh = ctx.MfaSatisfied && ctx.AuthTime is { } at && now - at <= maxAge && at <= now.AddMinutes(1);
        if (fresh)
        {
            return await next(context);
        }

        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Re-authenticate with your second factor",
            type: "https://errors.retailcloud.example/step-up-required",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "step-up-required",
                ["maxAgeSeconds"] = (int)maxAge.TotalSeconds,
                ["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
            });
    }
}
