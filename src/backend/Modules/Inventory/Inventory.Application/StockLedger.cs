using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Inventory.Application;

/// <summary>INV-002: the movement types of the ledger (D05 inv.stock_movement.movement_type).</summary>
public enum MovementType
{
    GoodsReceipt,
    SaleIssue,
    SaleReturnReceipt,
    AdjustmentIn,
    AdjustmentOut,
    TransferOut,
    TransferIn,
    CountAdjustment,
    PurchaseReturn,
    OpeningBalance,
}

/// <summary>BR-040: the default is Forbid.</summary>
public enum NegativeStockPolicy
{
    Forbid,
    AllowWithWarning,
}

/// <summary>BR-044: the unit the document used and its factor to the base unit at that moment (snapshot).</summary>
public sealed record EnteredUnit(string Code, decimal Factor);

/// <summary>
/// One line of a stock document. <paramref name="Qty"/> is signed (in <paramref name="Unit"/> when given, else in the
/// base unit). <paramref name="Cost"/> is the line's total cost in rials for an inbound line that brings its own cost
/// (goods receipt, opening balance, return at original cost); without it an inbound line is valued at the current WAC.
/// </summary>
public sealed record StockLine(int Line, Guid WarehouseId, Guid VariantId, MovementType Type, decimal Qty, long? Cost = null, EnteredUnit? Unit = null)
{
    public decimal QtyBase => Unit is null ? Qty : Qty * Unit.Factor;
}

/// <summary>A source document (BR-032): posted at most once per (type, id, purpose).</summary>
public sealed record StockDocument(string SourceType, Guid SourceId, DateTimeOffset EffectiveAt, IReadOnlyList<StockLine> Lines, string Purpose = "stock");

public sealed record PostedMovement(int Line, Guid WarehouseId, Guid VariantId, MovementType Type, decimal QtyBase, long UnitCost, long TotalCost,
    decimal OnHandAfter, long WacAfter);

public sealed record Shortage(Guid WarehouseId, Guid VariantId, decimal Available, decimal Requested);

public enum StockOutcome
{
    Done,
    AlreadyPosted,
    InsufficientStock,
    UnknownWarehouse,
}

public sealed record StockResult(StockOutcome Outcome, IReadOnlyList<PostedMovement> Movements, IReadOnlyList<Shortage> Shortages,
    IReadOnlyList<string> Warnings)
{
    public static StockResult Of(StockOutcome outcome) => new(outcome, [], [], []);
}

/// <summary><paramref name="ReorderPoint"/>: INV-010, when set for this variant in this warehouse.</summary>
public sealed record StockBalanceView(Guid WarehouseId, Guid VariantId, decimal OnHand, decimal Reserved, long Wac, long Value, decimal? ReorderPoint = null)
{
    public bool BelowReorderPoint => ReorderPoint is decimal rp && StockMath.IsLow(OnHand - Reserved, rp);
}

public sealed record StockMovementView(Guid Id, DateTimeOffset RecordedAt, DateTimeOffset EffectiveAt, string Type, decimal QtyBase, long UnitCost,
    long TotalCost, string SourceType, Guid SourceId, int SourceLine, decimal? EnteredQty, string? EnteredUnit, decimal? UnitFactor);

/// <summary>
/// INV-001: the only way stock changes. Every posting writes the movements and updates stock_balance in the caller's
/// transaction, locking the balances in variant order (INV-004) and applying the negative-stock policy (INV-003).
/// </summary>
public interface IStockLedger
{
    Task<StockResult> PostAsync(IUnitOfWorkScope scope, StockDocument document, CancellationToken cancellationToken);

    /// <summary><paramref name="lowOnly"/>: only balances at or below their reorder point (INV-010).</summary>
    Task<IReadOnlyList<StockBalanceView>> BalancesAsync(IUnitOfWorkScope scope, Guid? warehouseId, Guid? variantId, CancellationToken cancellationToken,
        bool lowOnly = false);

    /// <summary>INV-010: sets (or, with null, removes) the reorder point of a variant in a warehouse, in the base unit.</summary>
    Task SetReorderPointAsync(IUnitOfWorkScope scope, Guid warehouseId, Guid variantId, decimal? reorderPoint, Guid changedBy, CancellationToken cancellationToken);

    /// <summary>Newest first, up to <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<StockMovementView>> MovementsAsync(IUnitOfWorkScope scope, Guid variantId, Guid? warehouseId, int limit,
        CancellationToken cancellationToken, DateTimeOffset? effectiveFrom = null, DateTimeOffset? effectiveBefore = null);

    Task SetPolicyAsync(IUnitOfWorkScope scope, string scopeType, Guid scopeId, NegativeStockPolicy policy, Guid changedBy,
        CancellationToken cancellationToken);
}

/// <summary>A balance as the ledger sees it: quantity, weighted average cost and the running value (Σ movement cost).</summary>
public readonly record struct StockState(decimal OnHand, long Wac, long Value);

/// <summary>BR-042 / BR-043 / INV-006: weighted average cost and the value of each movement, in whole rials (half up).</summary>
public static class StockMath
{
    /// <summary>
    /// Applies one movement. An inbound line with <paramref name="inboundCost"/> (its total) re-averages the WAC:
    /// <c>round((onHand × WAC + cost) ÷ (onHand + qty))</c>, or <c>round(cost ÷ qty)</c> when nothing (or less than nothing)
    /// was on hand (BR-043). Every other movement is valued at the current WAC and leaves it unchanged.
    /// </summary>
    public static (StockState After, long UnitCost, long TotalCost) Apply(StockState before, decimal qtyBase, long? inboundCost)
    {
        if (qtyBase == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(qtyBase), "A movement moves something.");
        }

        if (qtyBase > 0 && inboundCost is long cost)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(cost, nameof(inboundCost));
            long unit = Round(cost / qtyBase);
            long wac = before.OnHand <= 0 ? unit : Round((before.OnHand * before.Wac + cost) / (before.OnHand + qtyBase));
            return (new StockState(before.OnHand + qtyBase, wac, before.Value + cost), unit, cost);
        }

        long total = Round(Math.Abs(qtyBase) * before.Wac) * Math.Sign(qtyBase);
        return (new StockState(before.OnHand + qtyBase, before.Wac, before.Value + total), before.Wac, total);
    }

    /// <summary>INV-001 rebuild: the state a sequence of movements (oldest first) leads to, from nothing.</summary>
    public static StockState Replay(IEnumerable<(decimal QtyBase, long TotalCost, bool CarriesCost)> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);
        var s = new StockState(0, 0, 0);
        foreach ((decimal qty, long total, bool carries) in movements)
        {
            s = carries && qty > 0 ? Apply(s, qty, total).After : new StockState(s.OnHand + qty, s.Wac, s.Value + total);
        }

        return s;
    }

    /// <summary>Inbound types that bring their own cost and re-average the WAC; every other type moves at the current WAC.</summary>
    public static bool CarriesCost(MovementType type) =>
        type is MovementType.GoodsReceipt or MovementType.OpeningBalance or MovementType.SaleReturnReceipt or MovementType.TransferIn;

    /// <summary>The negative-stock policy that applies: variant, then branch, then tenant, then Forbid (BR-040).</summary>
    public static NegativeStockPolicy Resolve(NegativeStockPolicy? variant, NegativeStockPolicy? branch, NegativeStockPolicy? tenant) =>
        variant ?? branch ?? tenant ?? NegativeStockPolicy.Forbid;

    public static long Round(decimal value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>INV-010: low means what is available has come down to the reorder point (at it, or below).</summary>
    public static bool IsLow(decimal available, decimal reorderPoint) => available <= reorderPoint;

    /// <summary>DE-INV-002 fires once, when a posting takes an item from above its reorder point to at or below it.</summary>
    public static bool CrossesReorderPoint(decimal availableBefore, decimal availableAfter, decimal reorderPoint) =>
        !IsLow(availableBefore, reorderPoint) && IsLow(availableAfter, reorderPoint);
}
