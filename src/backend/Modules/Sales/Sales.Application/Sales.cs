using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Sales.Application;

public enum PricingMode
{
    TaxExclusive,
    TaxInclusive,
}

/// <summary>The amounts of one line (BR-013 / BR-016), in whole rials.</summary>
public readonly record struct LineAmounts(long Gross, long Discount, long Net, long Tax, long LineTotal);

/// <summary>D07 <c>SaleLine</c> as stored for a cart: the server's preview until Finalize recomputes and snapshots it (SAL-005).</summary>
public sealed record SaleLineView(int LineNo, Guid VariantId, string Name, string Sku, decimal Qty, string Unit, decimal UnitFactor, string? WeightedCode,
    long UnitPrice, long Gross, long Discount, string? DiscountSource, int? DiscountBp, Guid? DiscountRuleId, long Net, int TaxRateBp, long Tax,
    long LineTotal, decimal ReturnedQty);

/// <summary>A payment applied to the bill (SAL-006); the payment itself lives in Payments (<paramref name="PaymentId"/>).</summary>
public sealed record SaleTenderView(int Seq, string Method, long Amount, Guid? PaymentId);

public sealed record SaleView(Guid Id, string Status, string ReturnStatus, Guid BranchId, Guid RegisterId, Guid ShiftId, Guid CashierId, Guid TerminalId,
    string Channel, string ClientDraftId, PricingMode PricingMode, string? InvoiceNo, long Total, DateTimeOffset? FinalizedAt, int Version,
    IReadOnlyList<SaleLineView> Lines, IReadOnlyList<SaleTenderView>? Tenders = null)
{
    public long Paid => Tenders?.Sum(t => t.Amount) ?? 0;

    public long Remaining => Total - Paid;
}

/// <summary>A priced line to write: everything but its number, which the cart assigns.</summary>
public sealed record SaleLineSpec(Guid VariantId, string Name, string Sku, decimal Qty, string Unit, decimal UnitFactor, string? WeightedCode,
    long UnitPrice, LineAmounts Amounts, string? DiscountSource, int? DiscountBp, Guid? DiscountRuleId, int TaxRateBp);

/// <summary>A line as finalized: its cost (Σ qty × WAC of the moment) and its snapshot (SAL-005), as JSON.</summary>
public sealed record FinalizedLine(int LineNo, long CostAtSale, string SnapshotJson);

public enum SaleOutcome
{
    Done,
    NotFound,
    NotDraft,
    VersionConflict,
    LineNotFound,
    DraftOfAnother,
}

public sealed record SaleResult(SaleOutcome Outcome, SaleView? Sale);

/// <summary>Who starts a cart, where (SAL-010): every sale carries its cashier, branch, register, shift, device and channel.</summary>
public sealed record SaleOrigin(Guid BranchId, Guid RegisterId, Guid ShiftId, Guid CashierId, Guid TerminalId, string Channel, PricingMode PricingMode);

public interface ISales
{
    /// <summary>DI-010: one sale per clientDraftId. Created = false: the draft already existed (returned as it is, if it is the caller's).</summary>
    Task<(SaleResult Result, bool Created)> CreateAsync(IUnitOfWorkScope scope, string clientDraftId, SaleOrigin origin, CancellationToken cancellationToken);

    Task<SaleView?> GetAsync(IUnitOfWorkScope scope, Guid saleId, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the cart (FOR UPDATE) and returns it if it is a Draft; a later write in the same transaction then sees no
    /// concurrent change. <paramref name="expectedVersion"/>: refuse if the cart moved on (BR-034).
    /// </summary>
    Task<SaleResult> LockDraftAsync(IUnitOfWorkScope scope, Guid saleId, int? expectedVersion, CancellationToken cancellationToken);

    /// <summary>Adds a line (next number) or, with <paramref name="replaceLineNo"/>, rewrites that line; bumps the version and the total.</summary>
    Task<SaleView> WriteLineAsync(IUnitOfWorkScope scope, Guid saleId, int? replaceLineNo, SaleLineSpec line, CancellationToken cancellationToken);

    Task<SaleView> RemoveLineAsync(IUnitOfWorkScope scope, Guid saleId, int lineNo, CancellationToken cancellationToken);

    /// <summary>
    /// BR-056 / D-16: the next invoice number of (branch, fiscal year), from a row locked until the transaction ends: a
    /// rolled-back finalize gives its number back, so the sequence has no gaps.
    /// </summary>
    Task<long> NextInvoiceSequenceAsync(IUnitOfWorkScope scope, Guid branchId, int fiscalYear, CancellationToken cancellationToken);

    /// <summary>
    /// SAL-005 / SAL-007: writes each line's snapshot and cost at sale, then makes the sale Finalized with its number
    /// (immutable from then on, DI-003).
    /// </summary>
    Task<SaleView> FinalizeAsync(IUnitOfWorkScope scope, Guid saleId, string invoiceNo, int fiscalYear, IReadOnlyList<FinalizedLine> lines,
        CancellationToken cancellationToken);

    /// <summary>SAL-006: applies a payment to the locked bill (the database refuses more than the total, RC027); bumps the version.</summary>
    Task<SaleView> AddTenderAsync(IUnitOfWorkScope scope, Guid saleId, string method, long amount, Guid? paymentId, CancellationToken cancellationToken);
}

/// <summary>BR-013 / BR-016 / D-13: the amounts of a line, rounded half up to the rial at each step.</summary>
public static class SaleMath
{
    public const int MaxLines = 300;

    public static long Round(decimal v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>The price of one entered unit: the base-unit price × the unit's factor (a carton of 24).</summary>
    public static long UnitPrice(long basePrice, decimal unitFactor) => Round(basePrice * unitFactor);

    /// <summary>gross = round(qty × unitPrice) — unless a price label states the amount (CAT-007.1).</summary>
    public static long Gross(decimal qty, long unitPrice) => Round(qty * unitPrice);

    /// <summary>
    /// net and tax from gross less discount: exclusive, tax = round(net × rate); inclusive, net = round(after ÷ (1 + rate))
    /// and tax = after − net. lineTotal = net + tax (DI-001).
    /// </summary>
    public static LineAmounts Line(long gross, long discount, int taxRateBp, PricingMode mode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gross);
        if (discount < 0 || discount > gross)
        {
            throw new ArgumentOutOfRangeException(nameof(discount), "0 to gross");
        }

        long after = gross - discount;
        if (mode == PricingMode.TaxInclusive)
        {
            long net = Round(after / (1 + taxRateBp / 10_000m));
            return new LineAmounts(gross, discount, net, after - net, after);
        }

        long tax = Round(after * (decimal)taxRateBp / 10_000m);
        return new LineAmounts(gross, discount, after, tax, after + tax);
    }

    /// <summary>BR-015: the cart's totals are sums of the lines' rounded amounts, never recomputed from unrounded ones.</summary>
    public static (long Gross, long Discount, long Net, long Tax, long Total) Totals(IEnumerable<SaleLineView> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        long g = 0, d = 0, n = 0, t = 0, tot = 0;
        foreach (SaleLineView l in lines)
        {
            g += l.Gross;
            d += l.Discount;
            n += l.Net;
            t += l.Tax;
            tot += l.LineTotal;
        }

        return (g, d, n, t, tot);
    }

    /// <summary>SAL-005.1: does a line priced now give exactly what the cart showed? Otherwise the cashier confirms again.</summary>
    public static bool SameAmounts(SaleLineView shown, SaleLineSpec now)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(now);
        return shown.UnitPrice == now.UnitPrice && shown.Gross == now.Amounts.Gross && shown.Discount == now.Amounts.Discount
               && shown.Net == now.Amounts.Net && shown.Tax == now.Amounts.Tax && shown.LineTotal == now.Amounts.LineTotal
               && shown.TaxRateBp == now.TaxRateBp && shown.DiscountRuleId == now.DiscountRuleId;
    }
}

/// <summary>BR-056: an invoice number reads branch, Jalali fiscal year and a six-digit sequence, e.g. B1-1405-000124 (D07).</summary>
public static class InvoiceNumbers
{
    public static string Format(string branchCode, int fiscalYear, long sequence) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{branchCode}-{fiscalYear}-{sequence:000000}");
}
