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

    public string DisplayName { get; internal set; } = string.Empty;

    public IReadOnlyList<MembershipInfo> Memberships { get; internal set; } = [];

    public TenantId? ActiveTenant { get; internal set; }

    TenantId ITenantContext.TenantId =>
        ActiveTenant ?? throw new InvalidOperationException("No active tenant for this request.");

    UserId? ITenantContext.UserId => User;
}
