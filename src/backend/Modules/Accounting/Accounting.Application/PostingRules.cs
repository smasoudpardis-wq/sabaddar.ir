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

/// <summary>
/// D02 §5.8.1 «فروش» (ACC-006, D-04, D-20): each payment method's account debited with what it brought in (cash: CashOnHand;
/// a standalone card terminal: CardInTransit until the bank settles, G-04), SalesRevenue credited with Σ net and OutputVat
/// with Σ tax. Discounts are not posted: the sale is booked at its net. Version 1 (the accountant's review is OQ-05).
/// </summary>
public static class SaleRule
{
    public const int Version = 1;

    public static PostingDraft For(Guid saleId, string invoiceNo, long cash, long card, long net, long tax, DateTimeOffset finalizedAt)
    {
        if (cash < 0 || card < 0 || net < 0 || tax < 0 || cash + card != net + tax || net + tax == 0)
        {
            throw new ArgumentException($"a sale's payments ({cash} + {card}) must equal its net + tax ({net} + {tax}) and be positive");
        }

        var lines = new List<RoleLine>();
        if (cash > 0)
        {
            lines.Add(new RoleLine("CashOnHand", cash, 0));
        }

        if (card > 0)
        {
            lines.Add(new RoleLine("CardInTransit", card, 0));
        }

        if (net > 0)
        {
            lines.Add(new RoleLine("SalesRevenue", 0, net));
        }

        if (tax > 0)
        {
            lines.Add(new RoleLine("OutputVat", 0, tax));
        }

        return new PostingDraft("Sale", saleId, "sale", Version, finalizedAt, $"فروش فاکتور {invoiceNo}", lines);
    }
}

/// <summary>D02 §5.8.1 «بهای تمام‌شده فروش»: COGS debited and Inventory credited with Σ qty × WAC of the moment (INV-006). Version 1.</summary>
public static class SaleCostRule
{
    public const int Version = 1;

    public static PostingDraft? For(Guid saleId, string invoiceNo, long cost, DateTimeOffset finalizedAt) => cost switch
    {
        < 0 => throw new ArgumentOutOfRangeException(nameof(cost)),
        0 => null,
        _ => new PostingDraft("Sale", saleId, "sale-cost", Version, finalizedAt, $"بهای تمام‌شده فاکتور {invoiceNo}",
            [new RoleLine("COGS", cost, 0), new RoleLine("Inventory", 0, cost)]),
    };
}

/// <summary>PRC-003: a payment to a supplier debits TradePayables (detail Supplier) and credits CashOnHand or Bank. Version 1.</summary>
public static class SupplierPaymentRule
{
    public const int Version = 1;

    public static PostingDraft For(Guid paymentId, Guid supplierId, long amount, bool bank, DateTimeOffset paidAt, string description)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        return new PostingDraft("SupplierPayment", paymentId, "supplier-payment", Version, paidAt, description,
            [new RoleLine("TradePayables", amount, 0, "Supplier", supplierId), new RoleLine(bank ? "Bank" : "CashOnHand", 0, amount)]);
    }
}

/// <summary>
/// PRC-004: goods sent back to a supplier. TradePayables (detail Supplier) is debited with the amount agreed with the
/// supplier and Inventory credited with what left stock at WAC; the gap goes to PurchaseReturnVariance (credited when the
/// supplier takes the goods back for more than their cost, debited when for less). Version 1.
/// </summary>
public static class PurchaseReturnRule
{
    public const int Version = 1;

    public static PostingDraft? For(Guid returnId, Guid supplierId, long cost, long agreed, DateTimeOffset at, string description)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cost);
        ArgumentOutOfRangeException.ThrowIfNegative(agreed);
        if (cost == 0 && agreed == 0)
        {
            return null;
        }

        var lines = new List<RoleLine>();
        if (agreed > 0)
        {
            lines.Add(new RoleLine("TradePayables", agreed, 0, "Supplier", supplierId));
        }

        if (cost > 0)
        {
            lines.Add(new RoleLine("Inventory", 0, cost));
        }

        long gap = agreed - cost;
        if (gap > 0)
        {
            lines.Add(new RoleLine("PurchaseReturnVariance", 0, gap));
        }
        else if (gap < 0)
        {
            lines.Add(new RoleLine("PurchaseReturnVariance", -gap, 0));
        }

        return new PostingDraft("PurchaseReturn", returnId, "purchase-return", Version, at, description, lines);
    }
}
