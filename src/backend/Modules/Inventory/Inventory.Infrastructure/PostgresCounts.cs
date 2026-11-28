using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Inventory.Application;

namespace RetailCloud.Inventory.Infrastructure;

/// <summary>
/// INV-009 on inv.inventory_count / count_line / count_entry. The database keeps the count's facts (RC032) and one open
/// count per warehouse; the close reads every line's system quantity under the balances' locks, in posting order.
/// </summary>
public sealed class PostgresCounts : ICounts
{
    private const string Columns =
        "c.id, c.warehouse_id, c.scope, c.blind, c.status, c.snapshot_at, c.closed_at, c.confirmed_at, c.adjustment_id, c.created_by, c.version, " +
        "ARRAY(SELECT DISTINCT e.counted_by FROM inv.count_entry e WHERE e.tenant_id = c.tenant_id AND e.count_id = c.id)";

    public async Task<CountResult> CreateAsync(IUnitOfWorkScope scope, Guid warehouseId, bool blind, Guid createdBy, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        Guid id = Guid.CreateVersion7();
        await s.Transaction.SaveAsync("count_open", cancellationToken);
        try
        {
            await using NpgsqlCommand cmd = s.Command(
                "INSERT INTO inv.inventory_count (tenant_id, id, warehouse_id, blind, snapshot_at, created_by) VALUES (core.current_tenant(), $1, $2, $3, statement_timestamp(), $4)");
            cmd.Parameters.Add(new NpgsqlParameter { Value = id });
            cmd.Parameters.Add(new NpgsqlParameter { Value = warehouseId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = blind });
            cmd.Parameters.Add(new NpgsqlParameter { Value = createdBy });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await s.Transaction.ReleaseAsync("count_open", cancellationToken);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "ux_count_open")
        {
            await s.Transaction.RollbackAsync("count_open", cancellationToken);
            return new CountResult(CountOutcome.AlreadyOpen, null);
        }

        // the snapshot: what the system holds in this warehouse at the start (UC-09)
        await using (NpgsqlCommand snap = s.Command(
            "INSERT INTO inv.count_line (tenant_id, count_id, variant_id, system_at_snapshot) " +
            "SELECT core.current_tenant(), $1, variant_id, on_hand FROM inv.stock_balance WHERE warehouse_id = $2"))
        {
            snap.Parameters.Add(new NpgsqlParameter { Value = id });
            snap.Parameters.Add(new NpgsqlParameter { Value = warehouseId });
            await snap.ExecuteNonQueryAsync(cancellationToken);
        }

        return new CountResult(CountOutcome.Done, await GetAsync(scope, id, cancellationToken));
    }

    public async Task<CountView?> GetAsync(IUnitOfWorkScope scope, Guid countId, CancellationToken cancellationToken) =>
        (await ReadAsync((PostgresUnitOfWorkScope)scope, "c.id = $1", countId, cancellationToken)).SingleOrDefault();

    public Task<IReadOnlyList<CountView>> ListAsync(IUnitOfWorkScope scope, Guid? warehouseId, CancellationToken cancellationToken) =>
        ReadAsync((PostgresUnitOfWorkScope)scope, "($1::uuid IS NULL OR c.warehouse_id = $1)", warehouseId, cancellationToken);

    public async Task<CountOutcome> AddEntriesAsync(IUnitOfWorkScope scope, Guid countId, string clientBatchId, IReadOnlyList<(Guid VariantId, decimal Qty)> entries,
        Guid countedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var s = (PostgresUnitOfWorkScope)scope;
        // shares the count row with other counters (they add up), and waits for a close in progress
        await using (NpgsqlCommand open = s.Command("SELECT status FROM inv.inventory_count WHERE id = $1 FOR SHARE"))
        {
            open.Parameters.Add(new NpgsqlParameter { Value = countId });
            switch (await open.ExecuteScalarAsync(cancellationToken))
            {
                case null:
                    return CountOutcome.NotFound;
                case string status when status != "Open":
                    return CountOutcome.NotOpen;
            }
        }

        foreach (var g in entries.GroupBy(e => e.VariantId))
        {
            decimal qty = g.Sum(e => e.Qty);
            if (qty == 0)
            {
                continue;
            }

            await using NpgsqlCommand cmd = s.Command(
                "INSERT INTO inv.count_entry (tenant_id, count_id, client_batch_id, variant_id, qty, counted_by) VALUES (core.current_tenant(), $1, $2, $3, $4, $5) " +
                "ON CONFLICT (tenant_id, count_id, client_batch_id, variant_id) DO NOTHING");
            cmd.Parameters.Add(new NpgsqlParameter { Value = countId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = clientBatchId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = g.Key });
            cmd.Parameters.Add(new NpgsqlParameter { Value = qty });
            cmd.Parameters.Add(new NpgsqlParameter { Value = countedBy });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return CountOutcome.Done;
    }

    public async Task<CountResult> CloseAsync(IUnitOfWorkScope scope, Guid countId, int expectedVersion, Guid closedBy, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        CountView? count = await LockAsync(s, countId, cancellationToken);
        if (count is null)
        {
            return new CountResult(CountOutcome.NotFound, null);
        }

        if (count.Status != "Open")
        {
            return new CountResult(CountOutcome.NotOpen, count);
        }

        if (count.Version != expectedVersion)
        {
            return new CountResult(CountOutcome.VersionConflict, count);
        }

        // an item counted that had no stock at the snapshot gets its line (snapshot 0)
        await using (NpgsqlCommand missing = s.Command(
            "INSERT INTO inv.count_line (tenant_id, count_id, variant_id, system_at_snapshot) SELECT DISTINCT core.current_tenant(), $1, e.variant_id, 0 " +
            "FROM inv.count_entry e WHERE e.count_id = $1 ON CONFLICT DO NOTHING"))
        {
            missing.Parameters.Add(new NpgsqlParameter { Value = countId });
            await missing.ExecuteNonQueryAsync(cancellationToken);
        }

        // INV-004: the same lock order as every posting; what is on hand now = snapshot + every movement since (INV-001)
        await using (NpgsqlCommand lockRows = s.Command(
            "SELECT b.variant_id FROM inv.stock_balance b JOIN inv.count_line l ON l.tenant_id = b.tenant_id AND l.variant_id = b.variant_id " +
            "WHERE l.count_id = $1 AND b.warehouse_id = $2 ORDER BY b.variant_id, b.warehouse_id FOR UPDATE OF b"))
        {
            lockRows.Parameters.Add(new NpgsqlParameter { Value = countId });
            lockRows.Parameters.Add(new NpgsqlParameter { Value = count.WarehouseId });
            await using NpgsqlDataReader r = await lockRows.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
            {
            }
        }

        await using (NpgsqlCommand close = s.Command(
            "UPDATE inv.count_line l SET system_at_close = coalesce((SELECT b.on_hand FROM inv.stock_balance b WHERE b.warehouse_id = $2 AND b.variant_id = l.variant_id), 0) " +
            "WHERE l.count_id = $1"))
        {
            close.Parameters.Add(new NpgsqlParameter { Value = countId });
            close.Parameters.Add(new NpgsqlParameter { Value = count.WarehouseId });
            await close.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (NpgsqlCommand status = s.Command(
            "UPDATE inv.inventory_count SET status = 'Closed', closed_at = statement_timestamp(), closed_by = $2, version = version + 1 WHERE id = $1"))
        {
            status.Parameters.Add(new NpgsqlParameter { Value = countId });
            status.Parameters.Add(new NpgsqlParameter { Value = closedBy });
            await status.ExecuteNonQueryAsync(cancellationToken);
        }

        return new CountResult(CountOutcome.Done, await GetAsync(scope, countId, cancellationToken));
    }

    public async Task<IReadOnlyList<CountLine>> LinesAsync(IUnitOfWorkScope scope, Guid countId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT v.variant_id, coalesce(l.system_at_snapshot, 0), l.system_at_close, " +
            "coalesce((SELECT b.on_hand FROM inv.stock_balance b WHERE b.warehouse_id = c.warehouse_id AND b.variant_id = v.variant_id), 0), " +
            "coalesce((SELECT sum(e.qty) FROM inv.count_entry e WHERE e.count_id = c.id AND e.variant_id = v.variant_id), 0), " +
            "EXISTS (SELECT 1 FROM inv.count_entry e WHERE e.count_id = c.id AND e.variant_id = v.variant_id) " +
            "FROM inv.inventory_count c " +
            "JOIN (SELECT variant_id FROM inv.count_line WHERE count_id = $1 UNION SELECT variant_id FROM inv.count_entry WHERE count_id = $1) v ON true " +
            "LEFT JOIN inv.count_line l ON l.count_id = c.id AND l.variant_id = v.variant_id WHERE c.id = $1 ORDER BY v.variant_id");
        cmd.Parameters.Add(new NpgsqlParameter { Value = countId });
        var lines = new List<CountLine>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            lines.Add(new CountLine(r.GetGuid(0), r.GetDecimal(1), r.IsDBNull(2) ? null : r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetBoolean(5)));
        }

        return lines;
    }

    public async Task<CountResult> LockClosedAsync(IUnitOfWorkScope scope, Guid countId, int expectedVersion, CancellationToken cancellationToken)
    {
        CountView? count = await LockAsync((PostgresUnitOfWorkScope)scope, countId, cancellationToken);
        return count switch
        {
            null => new CountResult(CountOutcome.NotFound, null),
            { Status: not "Closed" } => new CountResult(CountOutcome.NotClosed, count),
            _ when count.Version != expectedVersion => new CountResult(CountOutcome.VersionConflict, count),
            _ => new CountResult(CountOutcome.Done, count),
        };
    }

    public async Task<CountView> ConfirmAsync(IUnitOfWorkScope scope, Guid countId, Guid? adjustmentId, Guid confirmedBy, CancellationToken cancellationToken)
    {
        await using (NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "UPDATE inv.inventory_count SET status = 'Confirmed', confirmed_at = statement_timestamp(), confirmed_by = $2, adjustment_id = $3, version = version + 1 WHERE id = $1"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = countId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = confirmedBy });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)adjustmentId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return (await GetAsync(scope, countId, cancellationToken))!;
    }

    private static async Task<CountView?> LockAsync(PostgresUnitOfWorkScope s, Guid countId, CancellationToken ct)
    {
        await using (NpgsqlCommand cmd = s.Command("SELECT 1 FROM inv.inventory_count WHERE id = $1 FOR UPDATE"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = countId });
            if (await cmd.ExecuteScalarAsync(ct) is null)
            {
                return null;
            }
        }

        return (await ReadAsync(s, "c.id = $1", countId, ct)).Single();
    }

    private static async Task<IReadOnlyList<CountView>> ReadAsync(PostgresUnitOfWorkScope s, string where, object? arg, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = s.Command($"SELECT {Columns} FROM inv.inventory_count c WHERE {where} ORDER BY c.snapshot_at DESC LIMIT 100");
        cmd.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        var list = new List<CountView>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new CountView(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetBoolean(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5),
                r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7),
                r.IsDBNull(8) ? null : r.GetGuid(8), r.GetGuid(9), r.GetInt32(10), r.GetFieldValue<Guid[]>(11)));
        }

        return list;
    }
}
