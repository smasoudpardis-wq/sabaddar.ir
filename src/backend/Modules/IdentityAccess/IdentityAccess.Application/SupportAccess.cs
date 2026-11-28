using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.IdentityAccess.Application;

/// <summary>IAM-008 / US-027: a support JIT request as the store owner sees it (status already accounts for time).</summary>
public sealed record SupportAccessView(Guid Id, string RequestedBy, string Reason, int DurationMinutes, string Status,
    DateTimeOffset RequestedAt, DateTimeOffset? DecidedAt, DateTimeOffset? GrantedUntil, DateTimeOffset? RevokedAt);

/// <summary>IAM-008 input rules: a reason a store owner can judge, and at most 4 hours.</summary>
public static class SupportAccessRules
{
    public static string? Problem(string? reason, int? durationMinutes) =>
        reason is null || reason.Trim().Length is < 10 or > 500 ? "reason: 10-500 characters"
        : durationMinutes is not (>= 15 and <= 240) ? "durationMinutes: 15-240"
        : null;
}

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

    /// <summary>IAM-008: a platform support operator asks for time-limited access to this store, with a reason (Pending).</summary>
    Task<SupportAccessView> RequestAsync(IUnitOfWorkScope scope, string requestedBy, string reason, int durationMinutes, CancellationToken cancellationToken);

    /// <summary>The operator's grant in force now in this store, if the owner approved one and it has not ended.</summary>
    Task<SupportAccessView?> ActiveGrantAsync(IUnitOfWorkScope scope, string requestedBy, CancellationToken cancellationToken);

    /// <summary>End an approved grant early (AB-17).</summary>
    Task<(SupportAccessOutcome Outcome, SupportAccessView? View)> RevokeAsync(IUnitOfWorkScope scope, Guid id, Guid owner, CancellationToken cancellationToken);
}
