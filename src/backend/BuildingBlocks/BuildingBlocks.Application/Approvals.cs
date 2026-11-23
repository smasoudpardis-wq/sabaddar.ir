using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.BuildingBlocks.Application;

/// <summary>What needs approval: an action on a resource in a scope, under a condition (ABAC-02/03/06).</summary>
public sealed record ApprovalAsk(string Action, string ResourceType, string? ResourceId, ScopeRef Scope, string Condition,
    string? Value, string? Limit, string? Reason);

/// <summary>D07 <c>ApprovalRequest</c>.</summary>
public sealed record ApprovalView(Guid Id, string Action, string ResourceType, string? ResourceId, ScopeRef Scope, string? Reason,
    Guid RequestedBy, string Status, string Condition, string? Value, string? Limit, DateTimeOffset ExpiresAt,
    Guid? DecidedBy, DateTimeOffset? DecidedAt, DateTimeOffset? ValidUntil);

public enum DecisionOutcome
{
    Decided,
    NotFound,
    SelfApproval,
    NotPending,
    Expired,
}

/// <summary>
/// IAM-007 / D08 F-14 / D-61. Pending requests live <see cref="PendingLifetime"/>; an approval is valid
/// <see cref="ApprovalLifetime"/> after the decision, once, for the exact action + resource + scope + value.
/// </summary>
public interface IApprovals
{
    static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);
    static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(5);

    Task<Guid> RequestAsync(IUnitOfWorkScope scope, ApprovalAsk ask, UserId requester, CancellationToken cancellationToken);

    Task<ApprovalView?> GetAsync(IUnitOfWorkScope scope, Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApprovalView>> ListOpenAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);

    Task<DecisionOutcome> DecideAsync(IUnitOfWorkScope scope, Guid id, UserId approver, bool approve, string? note, CancellationToken cancellationToken);

    /// <summary>True exactly once for an Approved, unexpired approval whose binding equals <paramref name="ask"/> and whose requester is the caller.</summary>
    Task<bool> ConsumeAsync(IUnitOfWorkScope scope, Guid approvalId, ApprovalAsk ask, UserId requester, CancellationToken cancellationToken);
}
