using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Inventory.Application;

/// <summary>One line of a manual adjustment: a signed change in the base unit.</summary>
public sealed record AdjustmentLine(Guid VariantId, decimal QtyDelta);

/// <summary>The header of a posted adjustment (inv.stock_adjustment); its lines are the movements it is the source of.</summary>
public sealed record AdjustmentRecord(Guid Id, Guid WarehouseId, string Reason, string? Note, int LineCount, long Value, long Shrinkage, long Overage,
    Guid? JournalEntryId, Guid? ApprovalId, Guid CreatedBy, DateTimeOffset PostedAt);

public interface IStockAdjustments
{
    Task<AdjustmentRecord> RecordAsync(IUnitOfWorkScope scope, AdjustmentRecord adjustment, CancellationToken cancellationToken);

    /// <summary>AB-05 detection: the adjustments of these warehouses (all, when null), newest first, up to 200.</summary>
    Task<IReadOnlyList<AdjustmentRecord>> ListAsync(IUnitOfWorkScope scope, IReadOnlyCollection<Guid>? warehouseIds, CancellationToken cancellationToken);
}

/// <summary>INV-002 / BR-033 (US-063): what a manual adjustment is, what it is worth and when it needs an approval.</summary>
public static class AdjustmentRules
{
    public const int MaxLines = 200;
    public const int MaxNote = 500;

    /// <summary>BR-033 [ASM] default threshold: 50,000,000 rials.</summary>
    public const long DefaultApprovalThreshold = 50_000_000;

    public static readonly IReadOnlySet<string> Reasons = new HashSet<string>(StringComparer.Ordinal) { "Damage", "Expiry", "Theft", "CountError", "Other" };

    /// <summary>A signed, non-zero quantity in the base unit, up to 6 decimal places.</summary>
    public static bool TryDelta(string? text, out decimal delta)
    {
        delta = 0;
        return text is { Length: <= 24 }
               && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out delta)
               && delta != 0 && Math.Abs(delta) < 1_000_000_000 && decimal.Round(delta, 6) == delta;
    }

    public static MovementType TypeOf(decimal qtyDelta) => qtyDelta > 0 ? MovementType.AdjustmentIn : MovementType.AdjustmentOut;

    /// <summary>
    /// The value moved, split by direction: what went out (shrinkage, an expense) and what came in (overage). Each
    /// movement is valued at the WAC of that moment (INV-006); value = shrinkage + overage = Σ |Δqty| × WAC (BR-033).
    /// </summary>
    public static (long Value, long Shrinkage, long Overage) ValueOf(IEnumerable<PostedMovement> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);
        long shrinkage = 0, overage = 0;
        foreach (PostedMovement m in movements)
        {
            if (m.TotalCost < 0)
            {
                shrinkage = checked(shrinkage - m.TotalCost);
            }
            else
            {
                overage = checked(overage + m.TotalCost);
            }
        }

        return (checked(shrinkage + overage), shrinkage, overage);
    }

    /// <summary>BR-033: strictly above the threshold needs an approval (exactly the threshold does not).</summary>
    public static bool NeedsApproval(long value, long threshold) => value > threshold;

    /// <summary>
    /// D-61: the approval binds this exact adjustment (warehouse, reason and every line), order-independent. The value
    /// is bound separately: if the WAC moves in between (a receipt), the manager approves the new value again.
    /// </summary>
    public static string Binding(Guid warehouseId, string reason, IEnumerable<AdjustmentLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var text = new StringBuilder(warehouseId.ToString("N")).Append('|').Append(reason);
        foreach (AdjustmentLine l in lines.OrderBy(l => l.VariantId))
        {
            text.Append('|').Append(l.VariantId.ToString("N")).Append('=').Append(l.QtyDelta.ToString("0.######", CultureInfo.InvariantCulture));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }
}
