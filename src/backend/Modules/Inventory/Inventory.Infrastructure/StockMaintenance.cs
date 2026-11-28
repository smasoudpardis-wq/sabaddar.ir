using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Inventory.Application;

namespace RetailCloud.Inventory.Infrastructure;

/// <summary>
/// Workers-side inventory upkeep: movement partitions ahead of time (D05 §6) and, as an operator command, rebuilding
/// the balances of a tenant from its ledger when an oracle reports drift (INV-001).
/// </summary>
public sealed class StockMaintenance(IUnitOfWork unitOfWork)
{
    public Task<int> EnsurePartitionsAsync(int months, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteWithoutTenantAsync(null, async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT inv.ensure_movement_partitions($1)");
            cmd.Parameters.Add(new NpgsqlParameter { Value = months });
            return (int)(await cmd.ExecuteScalarAsync(ct))!;
        }, cancellationToken);

    /// <summary>
    /// INV-004.1 / D-19: in every store, the reservations whose 15 minutes have passed give their stock back (Expired).
    /// A payment confirmed after that finds the stock as it is then (Finalize checks it under the locks).
    /// </summary>
    public Task<int> ExpireReservationsAsync(CancellationToken cancellationToken) =>
        unitOfWork.ExecuteWithoutTenantAsync(null, async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT inv.expire_reservations()");
            return (int)(await cmd.ExecuteScalarAsync(ct))!;
        }, cancellationToken);

    /// <summary>Replays every (warehouse, variant) ledger of the tenant and corrects the balances that differ; returns how many.</summary>
    public Task<int> RebuildAsync(Guid tenantId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(new FixedTenantContext(new RetailCloud.BuildingBlocks.Domain.TenantId(tenantId)), async (scope, ct) =>
        {
            var s = (PostgresUnitOfWorkScope)scope;
            var ledgers = new Dictionary<(Guid Warehouse, Guid Variant), List<(decimal, long, bool)>>();
            await using (NpgsqlCommand read = s.Command(
                "SELECT warehouse_id, variant_id, qty_base, total_cost, movement_type FROM inv.stock_movement ORDER BY recorded_at, id"))
            {
                await using NpgsqlDataReader r = await read.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var key = (r.GetGuid(0), r.GetGuid(1));
                    if (!ledgers.TryGetValue(key, out var list)) ledgers[key] = list = [];
                    list.Add((r.GetDecimal(2), r.GetInt64(3), StockMath.CarriesCost(Enum.Parse<MovementType>(r.GetString(4)))));
                }
            }

            int fixedCount = 0;
            foreach (var ((warehouse, variant), movements) in ledgers.OrderBy(l => l.Key.Variant).ThenBy(l => l.Key.Warehouse))
            {
                StockState truth = StockMath.Replay(movements);
                await using NpgsqlCommand upd = s.Command(
                    "INSERT INTO inv.stock_balance AS b (tenant_id, warehouse_id, variant_id, on_hand, wac, value_on_hand, allow_negative) " +
                    "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $3 < 0) ON CONFLICT (tenant_id, warehouse_id, variant_id) DO UPDATE " +
                    "SET on_hand = $3, wac = $4, value_on_hand = $5, allow_negative = b.allow_negative OR $3 - b.reserved < 0, version = b.version + 1 " +
                    "WHERE (b.on_hand, b.wac, b.value_on_hand) IS DISTINCT FROM ($3, $4, $5)");
                upd.Parameters.Add(new NpgsqlParameter { Value = warehouse });
                upd.Parameters.Add(new NpgsqlParameter { Value = variant });
                upd.Parameters.Add(new NpgsqlParameter { Value = truth.OnHand, NpgsqlDbType = NpgsqlDbType.Numeric });
                upd.Parameters.Add(new NpgsqlParameter { Value = truth.Wac });
                upd.Parameters.Add(new NpgsqlParameter { Value = truth.Value });
                fixedCount += await upd.ExecuteNonQueryAsync(ct);
            }

            return fixedCount;
        }, cancellationToken);
}
