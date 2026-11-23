using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Merchandising.Application;

/// <summary>D07 <c>PriceList</c>. Until per-branch lists exist every branch sells from the default list (CAT-009).</summary>
public sealed record PriceListView(Guid Id, string Name, bool IsDefault, IReadOnlyList<Guid> BranchIds);

public sealed record PriceItem(Guid VariantId, long UnitPrice);

/// <summary>One price version: <paramref name="ValidTo"/> is exclusive; null = until changed.</summary>
public sealed record PriceVersion(Guid VariantId, long UnitPrice, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, string? Reason, Guid? CreatedBy,
    DateTimeOffset CreatedAt);

/// <summary>A requested price next to the one that would otherwise apply at validFrom (null: the variant had no price).</summary>
public sealed record PriceChange(Guid VariantId, long? Current, long New);

public enum PriceOutcome
{
    Done,
    NotFound,
    UnknownVariant,
    /// <summary>A version already starts at this exact instant and has applied: pick another validFrom.</summary>
    Taken,
}

public interface IPriceLists
{
    /// <summary>The tenant's lists; the default list is created on first use (tenants registered before US-052).</summary>
    Task<IReadOnlyList<PriceListView>> ListAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the list (price changes on one list are serial) and returns, per item, the price that would apply at
    /// <paramref name="validFrom"/>; null when the list does not exist; <see cref="PriceOutcome.UnknownVariant"/> with the
    /// first variant that is not an active variant of a live product.
    /// </summary>
    Task<(PriceOutcome Outcome, IReadOnlyList<PriceChange>? Changes, Guid? Variant)> PreviewAsync(IUnitOfWorkScope scope, Guid priceListId,
        IReadOnlyList<PriceItem> items, DateTimeOffset validFrom, CancellationToken cancellationToken);

    /// <summary>
    /// Each item becomes a version from <paramref name="validFrom"/> until the next scheduled version (or open-ended);
    /// the version covering validFrom ends there. Call after <see cref="PreviewAsync"/> in the same transaction.
    /// </summary>
    Task<PriceOutcome> SetAsync(IUnitOfWorkScope scope, Guid priceListId, IReadOnlyList<PriceItem> items, DateTimeOffset validFrom, string? reason,
        Guid createdBy, CancellationToken cancellationToken);

    /// <summary>Newest first (by start, then by entry), up to 100 versions: of one variant, or of the whole list. Null: no such list.</summary>
    Task<IReadOnlyList<PriceVersion>?> HistoryAsync(IUnitOfWorkScope scope, Guid priceListId, Guid? variantId, CancellationToken cancellationToken);
}

/// <summary>BR-041 [ASM]: when a price change needs a manager's approval, and what exactly is approved (D-61).</summary>
public static class PriceRules
{
    public const int MaxItems = 500;

    /// <summary>
    /// "bulk" for more than <paramref name="bulkLimit"/> variants in one change; "change-limit" when any known price
    /// moves by more than <paramref name="percentLimit"/>% up or down; otherwise null.
    /// </summary>
    public static (string Condition, string Limit)? NeedsApproval(IReadOnlyList<PriceChange> changes, int bulkLimit, int percentLimit)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count > bulkLimit)
        {
            return ("bulk", bulkLimit.ToString(CultureInfo.InvariantCulture));
        }

        // |new - current| * 100 > current * limit, in integers: no rounding at the boundary (exactly 30% needs no approval)
        bool big = changes.Any(c => c.Current is long cur && cur > 0
            && (decimal)Math.Abs(c.New - cur) * 100 > (decimal)cur * percentLimit);
        return big ? ("change-limit", percentLimit.ToString(CultureInfo.InvariantCulture) + "%") : null;
    }

    /// <summary>The approval binds this exact change: list, start and every variant's new price, order-independent.</summary>
    public static string Binding(Guid priceListId, IReadOnlyList<PriceItem> items, DateTimeOffset validFrom)
    {
        ArgumentNullException.ThrowIfNull(items);
        var text = new StringBuilder(priceListId.ToString("N")).Append('|').Append(validFrom.ToUnixTimeMilliseconds());
        foreach (PriceItem i in items.OrderBy(i => i.VariantId))
        {
            text.Append('|').Append(i.VariantId.ToString("N")).Append('=').Append(i.UnitPrice);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }
}
