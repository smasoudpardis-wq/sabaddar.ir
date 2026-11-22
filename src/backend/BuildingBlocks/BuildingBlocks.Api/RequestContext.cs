using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.BuildingBlocks.Api;

/// <summary>
/// Per-request identity and tenant, filled only by <see cref="RequestContextMiddleware"/> from the validated token and a
/// server-side membership check (BR-001). Handlers read it; nothing else writes it.
/// </summary>
public sealed class RequestContext : ITenantContext
{
    public UserId? User { get; internal set; }

    /// <summary>Token issuer and subject (the IdP identity), set even before the user is provisioned.</summary>
    public string? Issuer { get; internal set; }

    public string? Subject { get; internal set; }

    /// <summary>Token <c>name</c> claim (display name proposal for self-service sign-up).</summary>
    public string? TokenName { get; internal set; }

    public string DisplayName { get; internal set; } = string.Empty;

    public IReadOnlyList<MembershipInfo> Memberships { get; internal set; } = [];

    public TenantId? ActiveTenant { get; internal set; }

    /// <summary>The membership of <see cref="ActiveTenant"/> (roles included), if a tenant is active.</summary>
    public MembershipInfo? ActiveMembership { get; internal set; }

    /// <summary>Authentication Context Class of the token: <c>pwd</c> or <c>mfa</c> (Keycloak level of authentication).</summary>
    public string? Acr { get; internal set; }

    /// <summary>Time of the last interactive authentication, including a step-up (token claim <c>auth_time</c>).</summary>
    public DateTimeOffset? AuthTime { get; internal set; }

    public bool MfaSatisfied => Acr == MfaPolicy.MfaAcr;

    TenantId ITenantContext.TenantId =>
        ActiveTenant ?? throw new InvalidOperationException("No active tenant for this request.");

    UserId? ITenantContext.UserId => User;
}
