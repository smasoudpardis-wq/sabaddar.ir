using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Platform.Application;

/// <summary>TEN-004 / US-171: what a platform operator sees of a store: metadata only, never its business data.</summary>
public sealed record PlatformTenantView(Guid Id, string LegalName, string Status, string? Plan, DateTimeOffset CreatedAt, int Branches);

public enum StatusChange
{
    Done,
    NotFound,
    InvalidTransition,
}

public interface ITenantDirectory
{
    /// <summary>Stores in id order after <paramref name="after"/>; one more than <paramref name="limit"/> is read to tell whether another page follows.</summary>
    Task<IReadOnlyList<PlatformTenantView>> ListAsync(IUnitOfWorkScope scope, Guid? after, int limit, CancellationToken cancellationToken);

    Task<PlatformTenantView?> GetAsync(IUnitOfWorkScope scope, Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Active, Suspended or Closed; the history (who, why, from, to) is kept. Closed is final.</summary>
    Task<StatusChange> ChangeStatusAsync(IUnitOfWorkScope scope, Guid tenantId, string status, string reason, string operatorId, CancellationToken cancellationToken);
}

public static class TenantStatusRules
{
    public static readonly IReadOnlyList<string> Settable = ["Active", "Suspended", "Closed"];
}
