using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Inventory.Application;

/// <summary>D07 <c>Count</c>: a stock count of one warehouse (INV-009).</summary>
public sealed record CountView(Guid Id, Guid WarehouseId, string Scope, bool Blind, string Status, DateTimeOffset SnapshotAt, DateTimeOffset? ClosedAt,
    DateTimeOffset? ConfirmedAt, Guid? AdjustmentId, Guid CreatedBy, int Version, IReadOnlyList<Guid> Counters);

/// <summary>
/// One line of a count: the system quantity at the snapshot and (once closed) at close, and what people counted. Before the
/// close, <paramref name="SystemNow"/> is the live quantity.
/// </summary>
public sealed record CountLine(Guid VariantId, decimal SystemAtSnapshot, decimal? SystemAtClose, decimal SystemNow, decimal Counted, bool WasCounted)
{
    /// <summary>The movements between the snapshot and the close (or now): sales, receipts… made while counting.</summary>
    public decimal MovementsSince => (SystemAtClose ?? SystemNow) - SystemAtSnapshot;

    /// <summary>AT-280: counted − (snapshot + movements since) — what must be corrected.</summary>
    public decimal Difference => CountRules.Difference(SystemAtSnapshot, MovementsSince, Counted);
}

public enum CountOutcome
{
    Done,
    NotFound,
    AlreadyOpen,
    NotOpen,
    NotClosed,
    VersionConflict,
}

public sealed record CountResult(CountOutcome Outcome, CountView? Count);

public interface ICounts
{
    /// <summary>Opens a count and photographs every balance of the warehouse (count_line). AlreadyOpen: one count at a time.</summary>
    Task<CountResult> CreateAsync(IUnitOfWorkScope scope, Guid warehouseId, bool blind, Guid createdBy, CancellationToken cancellationToken);

    Task<CountView?> GetAsync(IUnitOfWorkScope scope, Guid countId, CancellationToken cancellationToken);

    Task<IReadOnlyList<CountView>> ListAsync(IUnitOfWorkScope scope, Guid? warehouseId, CancellationToken cancellationToken);

    /// <summary>A batch of counted quantities; the same batch id again (an offline device syncing twice) adds nothing. NotOpen after close.</summary>
    Task<CountOutcome> AddEntriesAsync(IUnitOfWorkScope scope, Guid countId, string clientBatchId, IReadOnlyList<(Guid VariantId, decimal Qty)> entries,
        Guid countedBy, CancellationToken cancellationToken);

    /// <summary>Ends counting: every line's system quantity is read under the balances' locks (INV-004), then the count is Closed.</summary>
    Task<CountResult> CloseAsync(IUnitOfWorkScope scope, Guid countId, int expectedVersion, Guid closedBy, CancellationToken cancellationToken);

    Task<IReadOnlyList<CountLine>> LinesAsync(IUnitOfWorkScope scope, Guid countId, CancellationToken cancellationToken);

    /// <summary>Locks the count for confirmation: NotClosed unless Closed; VersionConflict if it moved on.</summary>
    Task<CountResult> LockClosedAsync(IUnitOfWorkScope scope, Guid countId, int expectedVersion, CancellationToken cancellationToken);

    Task<CountView> ConfirmAsync(IUnitOfWorkScope scope, Guid countId, Guid? adjustmentId, Guid confirmedBy, CancellationToken cancellationToken);
}

/// <summary>INV-009 / UC-09 / SoD-03: the count's arithmetic and who may confirm it.</summary>
public static class CountRules
{
    public const int MaxEntries = 500;

    /// <summary>AT-280: snapshot 10, 2 sold while counting, 8 counted → 0.</summary>
    public static decimal Difference(decimal systemAtSnapshot, decimal movementsSince, decimal counted) => counted - (systemAtSnapshot + movementsSince);

    /// <summary>SoD-03: whoever counted does not confirm the result alone.</summary>
    public static bool IsCounter(CountView count, Guid user)
    {
        ArgumentNullException.ThrowIfNull(count);
        return count.Counters.Contains(user);
    }

    /// <summary>The approval binds this count and every correction it posts (D-61).</summary>
    public static string Binding(Guid countId, IEnumerable<AdjustmentLine> corrections)
    {
        ArgumentNullException.ThrowIfNull(corrections);
        var text = new StringBuilder(countId.ToString("N"));
        foreach (AdjustmentLine l in corrections.OrderBy(l => l.VariantId))
        {
            text.Append('|').Append(l.VariantId.ToString("N")).Append('=').Append(l.QtyDelta.ToString("0.######", CultureInfo.InvariantCulture));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }
}
