namespace RetailCloud.Accounting.Application;

/// <summary>
/// A line of a posting rule: an account role (D-30), mapped to the tenant's account when posted, with the detail
/// (e.g. Supplier) a detail-required account needs (DI-043).
/// </summary>
public sealed record RoleLine(string Role, long Debit, long Credit, string? DetailType = null, Guid? DetailId = null);

/// <summary>What a posting rule produces for one business event (ACC-004); the version is stored on the entry.</summary>
public sealed record PostingDraft(string SourceType, Guid SourceId, string Purpose, int RuleVersion, DateTimeOffset EffectiveAt, string Description,
    IReadOnlyList<RoleLine> Lines);

/// <summary>
/// D02 §5.8.1 «مغایرت صندوق»: a shortage debits CashShortage and credits CashOnHand; an overage debits CashOnHand and
/// credits CashOverage; no variance, no entry. Version 1 (the accountant's review is OQ-05).
/// </summary>
public static class CashVarianceRule
{
    public const int Version = 1;

    public static PostingDraft? For(Guid shiftId, long variance, DateTimeOffset closedAt) => variance switch
    {
        0 => null,
        < 0 => new PostingDraft("Shift", shiftId, "cash-variance", Version, closedAt, "کسری صندوق",
            [new RoleLine("CashShortage", -variance, 0), new RoleLine("CashOnHand", 0, -variance)]),
        _ => new PostingDraft("Shift", shiftId, "cash-variance", Version, closedAt, "اضافه صندوق",
            [new RoleLine("CashOnHand", variance, 0), new RoleLine("CashOverage", 0, variance)]),
    };
}

/// <summary>
/// D02 §5.8.1 «تعدیل موجودی»: a shortage debits InventoryShrinkage and credits Inventory; an overage debits Inventory and
/// credits InventoryOverage. One entry per adjustment carries both sides when lines go both ways; no value, no entry.
/// Version 1 (the accountant's review is OQ-05).
/// </summary>
public static class InventoryAdjustmentRule
{
    public const int Version = 1;

    public static PostingDraft? For(Guid adjustmentId, long shrinkage, long overage, DateTimeOffset postedAt, string reasonText)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(shrinkage);
        ArgumentOutOfRangeException.ThrowIfNegative(overage);
        var lines = new List<RoleLine>();
        if (shrinkage > 0)
        {
            lines.Add(new RoleLine("InventoryShrinkage", shrinkage, 0));
            lines.Add(new RoleLine("Inventory", 0, shrinkage));
        }

        if (overage > 0)
        {
            lines.Add(new RoleLine("Inventory", overage, 0));
            lines.Add(new RoleLine("InventoryOverage", 0, overage));
        }

        return lines.Count == 0 ? null : new PostingDraft("StockAdjustment", adjustmentId, "inventory-adjustment", Version, postedAt,
            "تعدیل موجودی: " + reasonText, lines);
    }
}

/// <summary>
/// D02 §5.8.1 «رسید خرید» (PRC-002): Inventory (the net cost) and InputVat debit, the supplier's payable credit.
/// Version 1 (the accountant's review is OQ-05). Nothing to post when the receipt is worth nothing (a gift).
/// </summary>
public static class GoodsReceiptRule
{
    public const int Version = 1;

    public static PostingDraft? For(Guid receiptId, Guid supplierId, long netCost, long inputVat, DateTimeOffset effectiveAt, string description)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(netCost);
        ArgumentOutOfRangeException.ThrowIfNegative(inputVat);
        long total = checked(netCost + inputVat);
        if (total == 0)
        {
            return null;
        }

        var lines = new List<RoleLine>();
        if (netCost > 0)
        {
            lines.Add(new RoleLine("Inventory", netCost, 0));
        }

        if (inputVat > 0)
        {
            lines.Add(new RoleLine("InputVat", inputVat, 0));
        }

        lines.Add(new RoleLine("TradePayables", 0, total, "Supplier", supplierId));
        return new PostingDraft("GoodsReceipt", receiptId, "goods-receipt", Version, effectiveAt, description, lines);
    }
}

/// <summary>D02 UC-01 step 9: a new product's opening stock debits Inventory and credits OpeningBalanceEquity. Version 1.</summary>
public static class OpeningStockRule
{
    public const int Version = 1;

    public static PostingDraft? For(Guid productId, long cost, DateTimeOffset at, string productName) => cost switch
    {
        < 0 => throw new ArgumentOutOfRangeException(nameof(cost)),
        0 => null,
        _ => new PostingDraft("Product", productId, "opening-stock", Version, at, "موجودی افتتاحیه: " + productName,
            [new RoleLine("Inventory", cost, 0), new RoleLine("OpeningBalanceEquity", 0, cost)]),
    };
}
