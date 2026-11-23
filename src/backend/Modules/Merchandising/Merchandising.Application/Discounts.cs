using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Merchandising.Application;

/// <summary>D07 <c>DiscountRule</c> (CAT-010): a percentage off a product (or, later, a category) over a time window.</summary>
public sealed record DiscountRuleView(Guid Id, string Name, string TargetKind, Guid TargetId, int PercentBp, DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo, IReadOnlyList<Guid> BranchIds);

public sealed record DiscountRuleSpec(string Name, string TargetKind, Guid TargetId, int PercentBp, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo,
    IReadOnlyList<Guid> BranchIds);

public interface IDiscounts
{
    Task<DiscountRuleView> CreateRuleAsync(IUnitOfWorkScope scope, DiscountRuleSpec spec, Guid createdBy, CancellationToken cancellationToken);

    /// <summary>Rules still in force or yet to start (<paramref name="includeEnded"/>: all), newest first, up to 200.</summary>
    Task<IReadOnlyList<DiscountRuleView>> ListRulesAsync(IUnitOfWorkScope scope, bool includeEnded, CancellationToken cancellationToken);

    Task<DiscountRuleView?> GetRuleAsync(IUnitOfWorkScope scope, Guid ruleId, CancellationToken cancellationToken);

    /// <summary>Ends a rule now (or at its scheduled start, so it never applies). False: no such rule, or already ended.</summary>
    Task<bool> EndRuleAsync(IUnitOfWorkScope scope, Guid ruleId, CancellationToken cancellationToken);

    /// <summary>Rules that could apply to these products at <paramref name="at"/> (any branch); pick with <see cref="DiscountPolicy.Best"/>.</summary>
    Task<IReadOnlyList<DiscountRuleView>> RulesForAsync(IUnitOfWorkScope scope, IReadOnlyCollection<Guid> productIds, DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>BR-021 caps set by the store (role → basis points); roles not listed use <see cref="DiscountPolicy.DefaultCaps"/>.</summary>
    Task<IReadOnlyDictionary<string, int>> CapsAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);

    Task SetCapsAsync(IUnitOfWorkScope scope, IReadOnlyDictionary<string, int> caps, Guid changedBy, CancellationToken cancellationToken);
}

/// <summary>CAT-010 / D-18 / BR-021: which automatic rule applies and how far a person may discount by hand.</summary>
public static class DiscountPolicy
{
    /// <summary>BR-021 defaults: a cashier gives nothing without approval; a manager anything.</summary>
    public static readonly IReadOnlyDictionary<string, int> DefaultCaps = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Cashier"] = 0, ["BranchManager"] = 10_000, ["TenantAdmin"] = 10_000, ["TenantOwner"] = 10_000,
    };

    /// <summary>
    /// D-18: of the rules for this product that are in force at <paramref name="at"/> in this branch, only the largest
    /// applies (they never add up). Ties go to the rule created first, so the choice is stable.
    /// </summary>
    public static DiscountRuleView? Best(IEnumerable<DiscountRuleView> rules, Guid productId, Guid branchId, DateTimeOffset at) =>
        rules.Where(r => r.TargetKind == "Product" && r.TargetId == productId && r.ValidFrom <= at && (r.ValidTo is null || at < r.ValidTo)
                         && (r.BranchIds.Count == 0 || r.BranchIds.Contains(branchId)))
            .OrderByDescending(r => r.PercentBp).ThenBy(r => r.ValidFrom).ThenBy(r => r.Id)
            .FirstOrDefault();

    /// <summary>
    /// BR-021: the largest manual discount (basis points) this person may give at <paramref name="scope"/> without
    /// approval: the highest cap among their roles that hold <c>pos.discount.apply</c> there; 0 if none does.
    /// </summary>
    public static int CapOf(IEnumerable<RoleGrant> grants, ScopeRef scope, IReadOnlyDictionary<string, int> storeCaps)
    {
        ArgumentNullException.ThrowIfNull(storeCaps);
        return grants.Where(g => AccessPolicy.Allows([g], "pos.discount.apply", scope))
            .Select(g => storeCaps.TryGetValue(g.Role, out int c) ? c : DefaultCaps.GetValueOrDefault(g.Role, 0))
            .DefaultIfEmpty(0).Max();
    }

    /// <summary>A manual discount as basis points of what it is taken from (an amount is converted, rounded up: never under-counted).</summary>
    public static int PercentOf(string kind, long value, long baseAmount)
    {
        if (kind == "Percent")
        {
            return (int)value;
        }

        if (baseAmount <= 0)
        {
            return 10_000;
        }

        return (int)Math.Min(10_000, (value * 10_000 + baseAmount - 1) / baseAmount);
    }

    /// <summary>The amount off <paramref name="gross"/> at <paramref name="percentBp"/>, half up to the rial (BR-013).</summary>
    public static long AmountOff(long gross, int percentBp) => (long)Math.Round(gross * (decimal)percentBp / 10_000m, MidpointRounding.AwayFromZero);
}
