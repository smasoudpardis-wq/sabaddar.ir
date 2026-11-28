using System.Text.RegularExpressions;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Inventory.Application;

/// <summary>D07 <c>Supplier</c> without the balance, which lives in the ledger (TradePayables, detail Supplier).</summary>
public sealed record SupplierRecord(Guid Id, string Name, string? NationalId, string? EconomicCode, string? Phone, DateTimeOffset CreatedAt);

/// <summary>One receipt line as recorded: the quantity in the invoice's unit and its factor to the base unit (BR-044).</summary>
public sealed record ReceiptLine(int LineNo, Guid VariantId, decimal Qty, string Unit, decimal UnitFactor, long UnitCost, long Discount, long InputVat)
{
    /// <summary>What the line puts into stock: qty × unit cost, half up to the rial, less the discount (≥ 0).</summary>
    public long LineCost => ReceiptRules.LineCost(Qty, UnitCost, Discount);

    public decimal QtyBase => Qty * UnitFactor;
}

public sealed record ReceiptRecord(Guid Id, Guid SupplierId, Guid WarehouseId, string? SupplierInvoiceNo, DateTimeOffset EffectiveAt,
    IReadOnlyList<ReceiptLine> Lines, long NetTotal, long InputVat, long Total, Guid? JournalEntryId, Guid CreatedBy, DateTimeOffset PostedAt);

/// <summary>PRC-003: a payment to a supplier; the amount lives in the ledger, this is the document.</summary>
public sealed record SupplierPaymentRecord(Guid Id, Guid SupplierId, long Amount, string Method, string? Reference, string? Note, Guid JournalEntryId,
    Guid CreatedBy, DateTimeOffset PaidAt);

/// <summary>One returned line in the base unit, at the WAC in force when it left (PRC-004).</summary>
public sealed record PurchaseReturnLine(int LineNo, Guid VariantId, decimal Qty, long UnitCost, long LineCost);

public sealed record PurchaseReturnRecord(Guid Id, Guid SupplierId, Guid WarehouseId, IReadOnlyList<PurchaseReturnLine> Lines, long Cost, long AgreedAmount,
    string? Note, Guid? JournalEntryId, Guid CreatedBy, DateTimeOffset PostedAt)
{
    /// <summary>What goes to PurchaseReturnVariance: positive when the supplier takes the goods back for more than their cost.</summary>
    public long Variance => AgreedAmount - Cost;
}

public enum SupplierOutcome
{
    Done,
    DuplicateNationalId,
}

public interface IProcurement
{
    Task<(SupplierOutcome Outcome, SupplierRecord? Supplier)> CreateSupplierAsync(IUnitOfWorkScope scope, string name, string? nationalId,
        string? economicCode, string? phone, Guid createdBy, CancellationToken cancellationToken);

    /// <summary>By name, up to 200; <paramref name="query"/> matches part of the name or the national id.</summary>
    Task<IReadOnlyList<SupplierRecord>> SuppliersAsync(IUnitOfWorkScope scope, string? query, CancellationToken cancellationToken);

    Task<SupplierRecord?> SupplierAsync(IUnitOfWorkScope scope, Guid supplierId, CancellationToken cancellationToken);

    /// <summary>UC-05: true when this supplier's invoice number is already on a receipt (a warning, not an error).</summary>
    Task<bool> InvoiceSeenAsync(IUnitOfWorkScope scope, Guid supplierId, string invoiceNo, CancellationToken cancellationToken);

    Task<ReceiptRecord> RecordReceiptAsync(IUnitOfWorkScope scope, ReceiptRecord receipt, CancellationToken cancellationToken);

    Task<SupplierPaymentRecord> RecordPaymentAsync(IUnitOfWorkScope scope, SupplierPaymentRecord payment, CancellationToken cancellationToken);

    Task<PurchaseReturnRecord> RecordReturnAsync(IUnitOfWorkScope scope, PurchaseReturnRecord purchaseReturn, CancellationToken cancellationToken);

    /// <summary>Newest first, up to 100: of these warehouses (all, when null).</summary>
    Task<IReadOnlyList<PurchaseReturnRecord>> ReturnsAsync(IUnitOfWorkScope scope, IReadOnlyCollection<Guid>? warehouseIds, CancellationToken cancellationToken);

    /// <summary>Newest first, up to 100: of these warehouses (all, when null).</summary>
    Task<IReadOnlyList<ReceiptRecord>> ReceiptsAsync(IUnitOfWorkScope scope, IReadOnlyCollection<Guid>? warehouseIds, CancellationToken cancellationToken);
}

/// <summary>PRC-001 / PRC-002 / UC-05 input rules.</summary>
public static partial class ReceiptRules
{
    public const int MaxLines = 500;

    public static long LineCost(decimal qty, long unitCost, long discount) =>
        (long)Math.Round(qty * unitCost, MidpointRounding.AwayFromZero) - discount;

    public static bool ValidNationalId(string? v) => v is null || NationalId().IsMatch(v);

    public static bool ValidEconomicCode(string? v) => v is null || EconomicCode().IsMatch(v);

    public static bool ValidPhone(string? v) => v is null || Phone().IsMatch(v);

    /// <summary>
    /// UC-05 warnings the user must confirm before the receipt is posted: a line at zero cost (a gift or a bonus?) and
    /// an invoice number this supplier already used.
    /// </summary>
    public static IReadOnlyList<string> Warnings(IEnumerable<ReceiptLine> lines, bool invoiceSeen)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var w = new List<string>();
        if (lines.Any(l => l.UnitCost == 0))
        {
            w.Add("zero-cost");
        }

        if (invoiceSeen)
        {
            w.Add("duplicate-supplier-invoice");
        }

        return w;
    }

    /// <summary>
    /// PRC-003 / PRC-004: paying more than the store owes, or taking goods back for more than it owes, leaves the supplier
    /// owing the store: allowed (an advance, a refund to come) once confirmed.
    /// </summary>
    public static IReadOnlyList<string> SettlementWarnings(long owed, long settled) => settled > owed ? ["exceeds-balance"] : [];

    [GeneratedRegex("^[0-9]{10,11}$")]
    private static partial Regex NationalId();

    [GeneratedRegex("^[0-9]{12,14}$")]
    private static partial Regex EconomicCode();

    [GeneratedRegex(@"^\+?[0-9]{8,15}$")]
    private static partial Regex Phone();
}
