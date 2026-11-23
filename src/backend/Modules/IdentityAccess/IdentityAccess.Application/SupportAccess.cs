using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.IdentityAccess.Application;

/// <summary>IAM-008 / US-027: a support JIT request as the store owner sees it (status already accounts for time).</summary>
public sealed record SupportAccessView(Guid Id, string RequestedBy, string Reason, int DurationMinutes, string Status,
    DateTimeOffset RequestedAt, DateTimeOffset? DecidedAt, DateTimeOffset? GrantedUntil, DateTimeOffset? RevokedAt);

public enum SupportAccessOutcome
{
    Done,
    NotFound,
    NotOpen,
}

public interface ISupportAccess
{
    Task<IReadOnlyList<SupportAccessView>> ListAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);

    /// <summary>Approve or reject a pending, unexpired request. Only a TenantOwner (also enforced in the database, SoD-05).</summary>
    Task<(SupportAccessOutcome Outcome, SupportAccessView? View)> DecideAsync(IUnitOfWorkScope scope, Guid id, bool approve, Guid owner,
        CancellationToken cancellationToken);

    /// <summary>End an approved grant early (AB-17).</summary>
    Task<(SupportAccessOutcome Outcome, SupportAccessView? View)> RevokeAsync(IUnitOfWorkScope scope, Guid id, Guid owner, CancellationToken cancellationToken);
}
