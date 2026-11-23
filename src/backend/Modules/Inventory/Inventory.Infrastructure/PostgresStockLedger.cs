using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Inventory.Application;

namespace RetailCloud.Inventory.Infrastructure;

/// <summary>
/// INV-001..006 on inv.*: one posting = one posted_source row (BR-032), the balances locked in variant order
/// (INV-004), the movements appended and the balances updated, all in the caller's transaction. A refused posting
/// (shortage, unknown warehouse, already posted) leaves nothing behind: everything runs under a savepoint.
/// The database backs every rule: DI-031 CHECK on available stock, the source registry key, append-only movements.
/// </summary>
public sealed class PostgresStockLedger : IStockLedger
{
    private const string Savepoint = "stock_post";

    public async Task<StockResult> PostAsync(IUnitOfWorkScope scope, StockDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Lines.Count == 0 || document.Lines.Any(l => l.QtyBase == 0 || l.Unit is { Factor: <= 0 }
                || (l.Cost is not null) != (StockMath.CarriesCost(l.Type) && l.QtyBase > 0) || l.Cost < 0))
        {
            throw new ArgumentException("Every line moves something; exactly the inbound lines of a costed type carry a cost.", nameof(document));
        }

        var s = (PostgresUnitOfWorkScope)scope;
        await s.Transaction.SaveAsync(Savepoint, cancellationToken);
        StockResult result = await PostUnderSavepointAsync(s, document, cancellationToken);
        if (result.Outcome == StockOutcome.Done)
        {
            await s.Transaction.ReleaseAsync(Savepoint, cancellationToken);
        }
        else
        {
            await s.Transaction.RollbackAsync(Savepoint, cancellationToken);
        }

        return result;
    }

    private static async Task<StockResult> PostUnderSavepointAsync(PostgresUnitOfWorkScope s, StockDocument d, CancellationToken ct)
    {
        await using (NpgsqlCommand claim = s.Command(
            "INSERT INTO inv.posted_source (tenant_id, source_type, source_id, purpose) VALUES (core.current_tenant(), $1, $2, $3) ON CONFLICT DO NOTHING"))
        {
            claim.Parameters.Add(new NpgsqlParameter { Value = d.SourceType });
            claim.Parameters.Add(new NpgsqlParameter { Value = d.SourceId });
            claim.Parameters.Add(new NpgsqlParameter { Value = d.Purpose });
            if (await claim.ExecuteNonQueryAsync(ct) == 0)
            {
                return StockResult.Of(StockOutcome.AlreadyPosted);
            }
        }

        Guid[] warehouseIds = [.. d.Lines.Select(l => l.WarehouseId).Distinct()];
        var branchOf = new Dictionary<Guid, Guid>();
        await using (NpgsqlCommand wh = s.Command("SELECT id, branch_id FROM inv.warehouse WHERE id = ANY($1)"))
        {
            wh.Parameters.Add(new NpgsqlParameter { Value = warehouseIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
            await using NpgsqlDataReader r = await wh.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                branchOf[r.GetGuid(0)] = r.GetGuid(1);
            }
        }

        if (branchOf.Count != warehouseIds.Length)
        {
            return StockResult.Of(StockOutcome.UnknownWarehouse);
        }

        var policies = new Dictionary<(string, Guid), NegativeStockPolicy>();
        await using (NpgsqlCommand pol = s.Command("SELECT scope_type, scope_id, policy FROM inv.negative_stock_policy"))
        {
            await using NpgsqlDataReader r = await pol.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                policies[(r.GetString(0), r.GetGuid(1))] = Enum.Parse<NegativeStockPolicy>(r.GetString(2));
            }
        }

        // INV-004: every balance this document touches, created if new, then locked in (variant, warehouse) order
        (Guid Variant, Guid Warehouse)[] keys = [.. d.Lines.Select(l => (l.VariantId, l.WarehouseId)).Distinct().OrderBy(k => k.VariantId).ThenBy(k => k.WarehouseId)];
        Guid[] kv = [.. keys.Select(k => k.Variant)], kw = [.. keys.Select(k => k.Warehouse)];
        await using (NpgsqlCommand create = s.Command(
            "INSERT INTO inv.stock_balance (tenant_id, warehouse_id, variant_id) " +
            "SELECT core.current_tenant(), w, v FROM unnest($1::uuid[], $2::uuid[]) WITH ORDINALITY AS k(v, w, n) ORDER BY n ON CONFLICT DO NOTHING"))
        {
            create.Parameters.Add(new NpgsqlParameter { Value = kv, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
            create.Parameters.Add(new NpgsqlParameter { Value = kw, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
            await create.ExecuteNonQueryAsync(ct);
        }

        var state = new Dictionary<(Guid Variant, Guid Warehouse), (StockState State, decimal Reserved)>();
        await using (NpgsqlCommand lockRows = s.Command(
            "SELECT b.variant_id, b.warehouse_id, b.on_hand, b.reserved, b.wac, b.value_on_hand FROM inv.stock_balance b " +
            "JOIN unnest($1::uuid[], $2::uuid[]) AS k(v, w) ON b.variant_id = k.v AND b.warehouse_id = k.w " +
            "ORDER BY b.variant_id, b.warehouse_id FOR UPDATE OF b"))
        {
            lockRows.Parameters.Add(new NpgsqlParameter { Value = kv, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
            lockRows.Parameters.Add(new NpgsqlParameter { Value = kw, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
            await using NpgsqlDataReader r = await lockRows.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                state[(r.GetGuid(0), r.GetGuid(1))] = (new StockState(r.GetDecimal(2), r.GetInt64(4), r.GetInt64(5)), r.GetDecimal(3));
            }
        }

        var movements = new List<PostedMovement>();
        var net = new Dictionary<(Guid, Guid), decimal>();
        foreach (StockLine line in d.Lines)
        {
            var key = (line.VariantId, line.WarehouseId);
            (StockState before, decimal reserved) = state[key];
            (StockState after, long unit, long total) = StockMath.Apply(before, line.QtyBase, line.Cost);
            state[key] = (after, reserved);
            net[key] = net.GetValueOrDefault(key) + line.QtyBase;
            movements.Add(new PostedMovement(line.Line, line.WarehouseId, line.VariantId, line.Type, line.QtyBase, unit, total, after.OnHand, after.Wac));
        }

        // INV-003: a balance this document lowers below zero available is refused under Forbid, with every shortage listed
        var shortages = new List<Shortage>();
        var warnings = new List<string>();
        var allow = new Dictionary<(Guid, Guid), bool>();
        foreach (((Guid variant, Guid warehouse) key, (StockState after, decimal reserved)) in state.Select(x => (x.Key, x.Value)))
        {
            NegativeStockPolicy policy = StockMath.Resolve(Policy(policies, "Variant", key.variant), Policy(policies, "Branch", branchOf[key.warehouse]),
                Policy(policies, "Tenant", s.TenantId!.Value.Value));
            bool lowers = net[key] < 0;
            bool below = after.OnHand - reserved < 0;
            if (below && lowers && policy == NegativeStockPolicy.Forbid)
            {
                shortages.Add(new Shortage(key.warehouse, key.variant, after.OnHand - net[key] - reserved, -net[key]));
            }
            else if (below && lowers)
            {
                warnings.Add("negative-stock-allowed");
            }

            // an inbound movement onto a balance that is already short stays possible whatever the policy now says
            allow[key] = policy == NegativeStockPolicy.AllowWithWarning || (below && !lowers);
        }

        if (shortages.Count > 0)
        {
            return new StockResult(StockOutcome.InsufficientStock, [], shortages, []);
        }

        foreach (var ((variant, warehouse), (after, _)) in state)
        {
            await using NpgsqlCommand upd = s.Command(
                "UPDATE inv.stock_balance SET on_hand = $3, wac = $4, value_on_hand = $5, allow_negative = $6, version = version + 1 " +
                "WHERE warehouse_id = $1 AND variant_id = $2");
            foreach (object v in new object[] { warehouse, variant, after.OnHand, after.Wac, after.Value, allow[(variant, warehouse)] })
            {
                upd.Parameters.Add(new NpgsqlParameter { Value = v });
            }

            await upd.ExecuteNonQueryAsync(ct);
        }

        for (int i = 0; i < movements.Count; i++)
        {
            PostedMovement m = movements[i];
            StockLine line = d.Lines[i];
            await using NpgsqlCommand ins = s.Command(
                "INSERT INTO inv.stock_movement (tenant_id, id, effective_at, warehouse_id, variant_id, movement_type, qty_base, unit_cost, total_cost, " +
                "source_type, source_id, source_line, entered_qty, entered_unit, unit_factor) " +
                "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)");
            foreach (object? v in new object?[]
                     {
                         Guid.CreateVersion7(), d.EffectiveAt.UtcDateTime, m.WarehouseId, m.VariantId, m.Type.ToString(), m.QtyBase, m.UnitCost, m.TotalCost,
                         d.SourceType, d.SourceId, m.Line, line.Unit is null ? null : line.Qty, line.Unit?.Code, line.Unit?.Factor,
                     })
            {
                ins.Parameters.Add(v switch
                {
                    null => new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Unknown },
                    DateTime t => new NpgsqlParameter { Value = t, NpgsqlDbType = NpgsqlDbType.TimestampTz },
                    _ => new NpgsqlParameter { Value = v },
                });
            }

            await ins.ExecuteNonQueryAsync(ct);
        }

        return new StockResult(StockOutcome.Done, movements, [], [.. warnings.Distinct()]);
    }

    private static NegativeStockPolicy? Policy(Dictionary<(string, Guid), NegativeStockPolicy> all, string type, Guid id) =>
        all.TryGetValue((type, id), out NegativeStockPolicy p) ? p : null;

    public async Task<IReadOnlyList<StockBalanceView>> BalancesAsync(IUnitOfWorkScope scope, Guid? warehouseId, Guid? variantId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT warehouse_id, variant_id, on_hand, reserved, wac, value_on_hand FROM inv.stock_balance " +
            "WHERE ($1::uuid IS NULL OR warehouse_id = $1) AND ($2::uuid IS NULL OR variant_id = $2) ORDER BY warehouse_id, variant_id LIMIT 500");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)warehouseId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)variantId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        var list = new List<StockBalanceView>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new StockBalanceView(r.GetGuid(0), r.GetGuid(1), r.GetDecimal(2), r.GetDecimal(3), r.GetInt64(4), r.GetInt64(5)));
        }

        return list;
    }

    public async Task<IReadOnlyList<StockMovementView>> MovementsAsync(IUnitOfWorkScope scope, Guid variantId, Guid? warehouseId, int limit,
        CancellationToken cancellationToken, DateTimeOffset? effectiveFrom = null, DateTimeOffset? effectiveBefore = null)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT id, recorded_at, effective_at, movement_type, qty_base, unit_cost, total_cost, source_type, source_id, source_line, " +
            "entered_qty, entered_unit, unit_factor FROM inv.stock_movement WHERE variant_id = $1 AND ($2::uuid IS NULL OR warehouse_id = $2) " +
            "AND ($4::timestamptz IS NULL OR effective_at >= $4) AND ($5::timestamptz IS NULL OR effective_at < $5) " +
            "ORDER BY recorded_at DESC, id DESC LIMIT $3");
        cmd.Parameters.Add(new NpgsqlParameter { Value = variantId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)warehouseId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = limit });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)effectiveFrom?.UtcDateTime ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)effectiveBefore?.UtcDateTime ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        var list = new List<StockMovementView>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new StockMovementView(r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.GetFieldValue<DateTimeOffset>(2), r.GetString(3), r.GetDecimal(4),
                r.GetInt64(5), r.GetInt64(6), r.GetString(7), r.GetGuid(8), r.GetInt32(9), r.IsDBNull(10) ? null : r.GetDecimal(10),
                r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetDecimal(12)));
        }

        return list;
    }

    public async Task SetPolicyAsync(IUnitOfWorkScope scope, string scopeType, Guid scopeId, NegativeStockPolicy policy, Guid changedBy,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "INSERT INTO inv.negative_stock_policy (tenant_id, scope_type, scope_id, policy, changed_by) VALUES (core.current_tenant(), $1, $2, $3, $4) " +
            "ON CONFLICT (tenant_id, scope_type, scope_id) DO UPDATE SET policy = EXCLUDED.policy, changed_by = EXCLUDED.changed_by, changed_at = now()");
        cmd.Parameters.Add(new NpgsqlParameter { Value = scopeType });
        cmd.Parameters.Add(new NpgsqlParameter { Value = scopeId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = policy.ToString() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = changedBy });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
