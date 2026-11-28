using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Inventory.Application;

/// <summary>The header of a posted transfer (inv.stock_transfer); its lines are the TransferOut / TransferIn movements.</summary>
public sealed record TransferRecord(Guid Id, Guid FromWarehouseId, Guid ToWarehouseId, string Status, int LineCount, long Value, string? Note, Guid CreatedBy,
    DateTimeOffset PostedAt);

public interface IStockTransfers
{
    Task<TransferRecord> RecordAsync(IUnitOfWorkScope scope, TransferRecord transfer, CancellationToken cancellationToken);

    /// <summary>Newest first, up to 200.</summary>
    Task<IReadOnlyList<TransferRecord>> ListAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);
}

/// <summary>INV-008 (US-064): what leaves the source at its WAC arrives at the destination carrying exactly that value.</summary>
public static class TransferRules
{
    public const int MaxLines = 200;

    /// <summary>The inbound lines: the same variants and quantities, each bringing the value its outbound line took away.</summary>
    public static IReadOnlyList<StockLine> Inbound(Guid toWarehouse, IReadOnlyList<StockLine> outbound, IReadOnlyList<PostedMovement> issued)
    {
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(issued);
        return [.. outbound.Select(o => new StockLine(o.Line, toWarehouse, o.VariantId, MovementType.TransferIn, -o.Qty,
            -issued.Single(m => m.Line == o.Line).TotalCost, o.Unit))];
    }
}
