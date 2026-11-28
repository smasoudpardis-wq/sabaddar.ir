using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Platform.Application;

/// <summary>BR-087: the platform's floor and default for a data category, as in force now. A version never changes.</summary>
public sealed record RetentionPolicyView(string Category, int Version, int MinimumDays, int DefaultDays, DateTimeOffset ValidFrom, string Reason);

/// <summary>What one category means for the current store: the policy, its own longer choice, the days that apply and any hold.</summary>
public sealed record StoreRetentionView(string Category, int MinimumDays, int DefaultDays, int? OverrideDays, int EffectiveDays, bool LegalHold,
    string? HoldReason, DateTimeOffset? HoldUntil);

/// <summary>One run of the nightly job for a store and category (the evidence of a deletion, or of a hold that stopped it).</summary>
public sealed record RetentionRunView(string Category, string Outcome, DateTimeOffset Cutoff, int DeletedRows, DateTimeOffset RanAt);

public enum RetentionChange
{
    Done,
    BelowFloor,
    NotFound,
    Conflict,
}

public interface IRetention
{
    /// <summary>The policy in force for every category (global data).</summary>
    Task<IReadOnlyList<RetentionPolicyView>> PoliciesAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);

    /// <summary>Publishes the next version of a category's policy; a concurrent publish of the same number is <see cref="RetentionChange.Conflict"/>.</summary>
    Task<(RetentionChange Outcome, RetentionPolicyView? Policy)> PublishAsync(IUnitOfWorkScope scope, string category, int minimumDays, int defaultDays,
        DateTimeOffset validFrom, string reason, string operatorId, CancellationToken cancellationToken);

    /// <summary>Puts a store's category under legal hold (replacing an earlier hold); false when the store does not exist.</summary>
    Task<bool> HoldAsync(IUnitOfWorkScope scope, Guid tenantId, string category, string reason, DateTimeOffset? until, string operatorId,
        CancellationToken cancellationToken);

    Task<bool> ReleaseAsync(IUnitOfWorkScope scope, Guid tenantId, string category, CancellationToken cancellationToken);

    /// <summary>The current store's view of every category.</summary>
    Task<IReadOnlyList<StoreRetentionView>> StoreAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);

    /// <summary>Sets the store's own period for a category; shorter than the floor is <see cref="RetentionChange.BelowFloor"/>.</summary>
    Task<RetentionChange> SetOverrideAsync(IUnitOfWorkScope scope, string category, int days, Guid userId, CancellationToken cancellationToken);

    /// <summary>Back to the platform default; false when there was no choice.</summary>
    Task<bool> ClearOverrideAsync(IUnitOfWorkScope scope, string category, CancellationToken cancellationToken);

    Task<IReadOnlyList<RetentionRunView>> RunsAsync(IUnitOfWorkScope scope, int limit, CancellationToken cancellationToken);
}

public static class RetentionRules
{
    public static readonly IReadOnlyList<string> Categories =
    [
        "FinancialDocuments", "TaxInvoices", "InventoryLedger", "Audit", "CustomerCrm", "CustomerIdentity", "Notifications", "TechnicalLogs",
    ];

    public const int MaxDays = 36500;

    public static bool IsCategory(string? category) => category is not null && Categories.Contains(category, StringComparer.Ordinal);

    /// <summary>The first problem of a platform policy, as <c>field: message</c>, or null.</summary>
    public static string? PolicyProblem(string? category, int? minimumDays, int? defaultDays, string? reason)
    {
        if (!IsCategory(category))
        {
            return "category: unknown category";
        }

        if (minimumDays is not (>= 1 and <= MaxDays))
        {
            return $"minimumDays: 1-{MaxDays}";
        }

        if (defaultDays is not (>= 1 and <= MaxDays) || defaultDays < minimumDays)
        {
            return $"defaultDays: from minimumDays to {MaxDays}";
        }

        return reason is null || reason.Trim().Length is < 3 or > 500 ? "reason: 3-500 characters" : null;
    }
}
