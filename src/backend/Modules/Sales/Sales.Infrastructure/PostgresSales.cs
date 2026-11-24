using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Sales.Application;

namespace RetailCloud.Sales.Infrastructure;

/// <summary>
/// SAL-001 carts on sales.sale / sale_line. The cart row is locked for every change, so concurrent scans on one cart
/// queue up; the total is the sum of the lines' totals (BR-015) and the version moves with every change (BR-034).
/// The database refuses line changes outside Draft (RC026) and after Finalize (RC002).
/// </summary>
public sealed class PostgresSales : ISales
{
    private const string SaleColumns =
        "id, status, return_status, branch_id, register_id, shift_id, cashier_id, terminal_id, channel, client_draft_id, pricing_mode, invoice_no, total, " +
        "finalized_at, version, hold_note, cancel_reason, last_activity_at";

    private const string LineColumns =
        "line_no, variant_id, name, sku, qty, unit, unit_factor, weighted_code, unit_price, gross, discount, discount_source, discount_bp, discount_rule_id, " +
        "net, tax_rate_bp, tax, line_total, returned_qty";

    public async Task<(SaleResult Result, bool Created)> CreateAsync(IUnitOfWorkScope scope, string clientDraftId, SaleOrigin origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var s = (PostgresUnitOfWorkScope)scope;
        Guid id = Guid.CreateVersion7();
        await using (NpgsqlCommand cmd = s.Command(
            "INSERT INTO sales.sale (tenant_id, id, branch_id, register_id, shift_id, cashier_id, terminal_id, client_draft_id, channel, pricing_mode) " +
            "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7, $8, $9) ON CONFLICT (tenant_id, client_draft_id) DO NOTHING"))
        {
            foreach (object v in new object[]
                     {
                         id, origin.BranchId, origin.RegisterId, origin.ShiftId, origin.CashierId, origin.TerminalId, clientDraftId, origin.Channel,
                         origin.PricingMode.ToString(),
                     })
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = v });
            }

            if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                return (new SaleResult(SaleOutcome.Done, await GetAsync(scope, id, cancellationToken)), true);
            }
        }

        // DI-010: an offline client retrying its draft gets the same sale back; someone else's draft id is not theirs to see
        SaleView existing = (await ReadSalesAsync(s, "client_draft_id = $1", clientDraftId, cancellationToken)).Single();
        return existing.CashierId == origin.CashierId
            ? (new SaleResult(SaleOutcome.Done, existing), false)
            : (new SaleResult(SaleOutcome.DraftOfAnother, null), false);
    }

    public async Task<SaleView?> GetAsync(IUnitOfWorkScope scope, Guid saleId, CancellationToken cancellationToken) =>
        (await ReadSalesAsync((PostgresUnitOfWorkScope)scope, "id = $1", saleId, cancellationToken)).SingleOrDefault();

    public async Task<SaleResult> LockDraftAsync(IUnitOfWorkScope scope, Guid saleId, int? expectedVersion, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using (NpgsqlCommand cmd = s.Command("SELECT status, version FROM sales.sale WHERE id = $1 FOR UPDATE"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = saleId });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await r.ReadAsync(cancellationToken))
            {
                return new SaleResult(SaleOutcome.NotFound, null);
            }

            if (r.GetString(0) != "Draft")
            {
                await r.DisposeAsync();
                return new SaleResult(SaleOutcome.NotDraft, await GetAsync(scope, saleId, cancellationToken));
            }

            if (expectedVersion is { } v && r.GetInt32(1) != v)
            {
                await r.DisposeAsync();
                return new SaleResult(SaleOutcome.VersionConflict, await GetAsync(scope, saleId, cancellationToken));
            }
        }

        return new SaleResult(SaleOutcome.Done, await GetAsync(scope, saleId, cancellationToken));
    }

    public async Task<SaleView> WriteLineAsync(IUnitOfWorkScope scope, Guid saleId, int? replaceLineNo, SaleLineSpec line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        var s = (PostgresUnitOfWorkScope)scope;
        string sql = replaceLineNo is null
            ? $"INSERT INTO sales.sale_line (tenant_id, sale_id, {LineColumns}) SELECT core.current_tenant(), $1, " +
              "coalesce((SELECT max(line_no) FROM sales.sale_line WHERE sale_id = $1), 0) + 1, " +
              "$2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, 0"
            : "UPDATE sales.sale_line SET variant_id = $2, name = $3, sku = $4, qty = $5, unit = $6, unit_factor = $7, weighted_code = $8, unit_price = $9, " +
              "gross = $10, discount = $11, discount_source = $12, discount_bp = $13, discount_rule_id = $14, net = $15, tax_rate_bp = $16, tax = $17, " +
              "line_total = $18 WHERE sale_id = $1 AND line_no = $19";
        await using (NpgsqlCommand cmd = s.Command(sql))
        {
            LineAmounts a = line.Amounts;
            object?[] values =
            [
                saleId, line.VariantId, line.Name, line.Sku, line.Qty, line.Unit, line.UnitFactor, line.WeightedCode, line.UnitPrice, a.Gross, a.Discount,
                line.DiscountSource, line.DiscountBp, line.DiscountRuleId, a.Net, line.TaxRateBp, a.Tax, a.LineTotal,
            ];
            NpgsqlDbType?[] types =
            [
                null, null, null, null, null, null, null, NpgsqlDbType.Text, null, null, null, NpgsqlDbType.Text, NpgsqlDbType.Integer, NpgsqlDbType.Uuid,
                null, null, null, null,
            ];
            for (int i = 0; i < values.Length; i++)
            {
                cmd.Parameters.Add(types[i] is { } t ? new NpgsqlParameter { Value = values[i] ?? DBNull.Value, NpgsqlDbType = t } : new NpgsqlParameter { Value = values[i] });
            }

            if (replaceLineNo is { } no)
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = no });
            }

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return await TouchAsync(s, saleId, cancellationToken);
    }

    public async Task<SaleView> RemoveLineAsync(IUnitOfWorkScope scope, Guid saleId, int lineNo, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using (NpgsqlCommand cmd = s.Command("DELETE FROM sales.sale_line WHERE sale_id = $1 AND line_no = $2"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = saleId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = lineNo });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return await TouchAsync(s, saleId, cancellationToken);
    }

    public async Task<SaleResult> LockAsync(IUnitOfWorkScope scope, Guid saleId, int? expectedVersion, CancellationToken cancellationToken)
    {
        await using (NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("SELECT version FROM sales.sale WHERE id = $1 FOR UPDATE"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = saleId });
            if (await cmd.ExecuteScalarAsync(cancellationToken) is not int version)
            {
                return new SaleResult(SaleOutcome.NotFound, null);
            }

            if (expectedVersion is { } v && v != version)
            {
                return new SaleResult(SaleOutcome.VersionConflict, await GetAsync(scope, saleId, cancellationToken));
            }
        }

        return new SaleResult(SaleOutcome.Done, await GetAsync(scope, saleId, cancellationToken));
    }

    public Task<SaleView> HoldAsync(IUnitOfWorkScope scope, Guid saleId, string? note, CancellationToken cancellationToken) =>
        SetAsync((PostgresUnitOfWorkScope)scope, saleId, "status = 'Held', hold_note = $2", [note], cancellationToken);

    public Task<SaleView> ResumeAsync(IUnitOfWorkScope scope, Guid saleId, SaleOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return SetAsync((PostgresUnitOfWorkScope)scope, saleId,
            "status = 'Draft', hold_note = NULL, register_id = $2, shift_id = $3, cashier_id = $4, terminal_id = $5",
            [origin.RegisterId, origin.ShiftId, origin.CashierId, origin.TerminalId], cancellationToken);
    }

    public Task<SaleView> CancelAsync(IUnitOfWorkScope scope, Guid saleId, string reason, CancellationToken cancellationToken) =>
        SetAsync((PostgresUnitOfWorkScope)scope, saleId, "status = 'Cancelled', cancelled_at = now(), cancel_reason = $2", [reason], cancellationToken);

    public async Task<IReadOnlyList<SaleView>> HeldAsync(IUnitOfWorkScope scope, Guid branchId, CancellationToken cancellationToken) =>
        [.. (await ReadSalesAsync((PostgresUnitOfWorkScope)scope, "status = 'Held' AND branch_id = $1", branchId, cancellationToken))
            .OrderByDescending(x => x.LastActivityAt).Take(100)];

    public async Task<IReadOnlyList<SaleView>> RecentAsync(IUnitOfWorkScope scope, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return await ReadSalesAsync((PostgresUnitOfWorkScope)scope, "true ORDER BY last_activity_at DESC, id DESC LIMIT $1", limit, cancellationToken);
    }

    private async Task<SaleView> SetAsync(PostgresUnitOfWorkScope s, Guid saleId, string set, object?[] args, CancellationToken ct)
    {
        await using (NpgsqlCommand cmd = s.Command($"UPDATE sales.sale SET {set}, version = version + 1, last_activity_at = now() WHERE id = $1"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = saleId });
            foreach (object? a in args)
            {
                cmd.Parameters.Add(a is null ? new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text } : new NpgsqlParameter { Value = a });
            }

            await cmd.ExecuteNonQueryAsync(ct);
        }

        return (await GetAsync(s, saleId, ct))!;
    }

    public async Task<long> NextInvoiceSequenceAsync(IUnitOfWorkScope scope, Guid branchId, int fiscalYear, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "INSERT INTO sales.invoice_sequence (tenant_id, branch_id, fiscal_year, next_value) VALUES (core.current_tenant(), $1, $2, 2) " +
            "ON CONFLICT (tenant_id, branch_id, fiscal_year) DO UPDATE SET next_value = sales.invoice_sequence.next_value + 1 RETURNING next_value - 1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = branchId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = fiscalYear });
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<SaleView> FinalizeAsync(IUnitOfWorkScope scope, Guid saleId, string invoiceNo, int fiscalYear, IReadOnlyList<FinalizedLine> lines,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var s = (PostgresUnitOfWorkScope)scope;
        foreach (FinalizedLine l in lines)
        {
            await using NpgsqlCommand line = s.Command("UPDATE sales.sale_line SET cost_at_sale = $3, snapshot = $4::jsonb WHERE sale_id = $1 AND line_no = $2");
            line.Parameters.Add(new NpgsqlParameter { Value = saleId });
            line.Parameters.Add(new NpgsqlParameter { Value = l.LineNo });
            line.Parameters.Add(new NpgsqlParameter { Value = l.CostAtSale });
            line.Parameters.Add(new NpgsqlParameter { Value = l.SnapshotJson });
            await line.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (NpgsqlCommand sale = s.Command(
            "UPDATE sales.sale SET status = 'Finalized', invoice_no = $2, fiscal_year = $3, finalized_at = now(), " +
            "total = coalesce((SELECT sum(line_total) FROM sales.sale_line WHERE sale_id = $1), 0), version = version + 1, last_activity_at = now() WHERE id = $1"))
        {
            sale.Parameters.Add(new NpgsqlParameter { Value = saleId });
            sale.Parameters.Add(new NpgsqlParameter { Value = invoiceNo });
            sale.Parameters.Add(new NpgsqlParameter { Value = fiscalYear });
            await sale.ExecuteNonQueryAsync(cancellationToken);
        }

        return (await GetAsync(s, saleId, cancellationToken))!;
    }

    public async Task<SaleView?> VoidTenderAsync(IUnitOfWorkScope scope, Guid saleId, int seq, Guid by, string reason, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using (NpgsqlCommand cmd = s.Command(
            "UPDATE sales.sale_tender SET voided_at = now(), voided_by = $3, void_reason = $4 WHERE sale_id = $1 AND seq = $2 AND voided_at IS NULL"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = saleId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = seq });
            cmd.Parameters.Add(new NpgsqlParameter { Value = by });
            cmd.Parameters.Add(new NpgsqlParameter { Value = reason });
            if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return null;
            }
        }

        await using (NpgsqlCommand touch = s.Command("UPDATE sales.sale SET version = version + 1, last_activity_at = now() WHERE id = $1"))
        {
            touch.Parameters.Add(new NpgsqlParameter { Value = saleId });
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }

        return await GetAsync(s, saleId, cancellationToken);
    }

    public async Task<SaleView> AddTenderAsync(IUnitOfWorkScope scope, Guid saleId, string method, long amount, Guid? paymentId, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using (NpgsqlCommand cmd = s.Command(
            "INSERT INTO sales.sale_tender (tenant_id, sale_id, seq, method, amount, payment_id) SELECT core.current_tenant(), $1, " +
            "coalesce((SELECT max(seq) FROM sales.sale_tender WHERE sale_id = $1), 0) + 1, $2, $3, $4"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = saleId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = method });
            cmd.Parameters.Add(new NpgsqlParameter { Value = amount });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)paymentId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (NpgsqlCommand touch = s.Command("UPDATE sales.sale SET version = version + 1, last_activity_at = now() WHERE id = $1"))
        {
            touch.Parameters.Add(new NpgsqlParameter { Value = saleId });
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }

        return (await GetAsync(s, saleId, cancellationToken))!;
    }

    /// <summary>The total follows the lines (BR-015); the version and the last activity (SAL-011) move with each change.</summary>
    private async Task<SaleView> TouchAsync(PostgresUnitOfWorkScope s, Guid saleId, CancellationToken ct)
    {
        await using (NpgsqlCommand cmd = s.Command(
            "UPDATE sales.sale SET total = coalesce((SELECT sum(line_total) FROM sales.sale_line WHERE sale_id = $1), 0), " +
            "version = version + 1, last_activity_at = now() WHERE id = $1"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = saleId });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return (await GetAsync(s, saleId, ct))!;
    }

    private static async Task<IReadOnlyList<SaleView>> ReadSalesAsync(PostgresUnitOfWorkScope s, string where, object arg, CancellationToken ct)
    {
        var sales = new List<SaleView>();
        await using (NpgsqlCommand cmd = s.Command($"SELECT {SaleColumns} FROM sales.sale WHERE {where}"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                sales.Add(new SaleView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4), r.GetGuid(5), r.GetGuid(6), r.GetGuid(7),
                    r.GetString(8), r.GetString(9), Enum.Parse<PricingMode>(r.GetString(10)), r.IsDBNull(11) ? null : r.GetString(11), r.GetInt64(12),
                    r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13), r.GetInt32(14), [], null, r.IsDBNull(15) ? null : r.GetString(15),
                    r.IsDBNull(16) ? null : r.GetString(16), r.GetFieldValue<DateTimeOffset>(17)));
            }
        }

        if (sales.Count == 0)
        {
            return sales;
        }

        var lines = new Dictionary<Guid, List<SaleLineView>>();
        await using (NpgsqlCommand cmd = s.Command($"SELECT sale_id, {LineColumns} FROM sales.sale_line WHERE sale_id = ANY($1) ORDER BY sale_id, line_no"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = sales.Select(x => x.Id).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                Guid sale = r.GetGuid(0);
                if (!lines.TryGetValue(sale, out List<SaleLineView>? list))
                {
                    lines[sale] = list = [];
                }

                list.Add(new SaleLineView(r.GetInt32(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetString(6), r.GetDecimal(7),
                    r.IsDBNull(8) ? null : r.GetString(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.IsDBNull(12) ? null : r.GetString(12),
                    r.IsDBNull(13) ? null : r.GetInt32(13), r.IsDBNull(14) ? null : r.GetGuid(14), r.GetInt64(15), r.GetInt32(16), r.GetInt64(17),
                    r.GetInt64(18), r.GetDecimal(19)));
            }
        }

        var tenders = new Dictionary<Guid, List<SaleTenderView>>();
        await using (NpgsqlCommand cmd = s.Command("SELECT sale_id, seq, method, amount, payment_id, voided_at, void_reason FROM sales.sale_tender WHERE sale_id = ANY($1) ORDER BY sale_id, seq"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = sales.Select(x => x.Id).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                Guid sale = r.GetGuid(0);
                if (!tenders.TryGetValue(sale, out List<SaleTenderView>? list))
                {
                    tenders[sale] = list = [];
                }

                list.Add(new SaleTenderView(r.GetInt32(1), r.GetString(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetGuid(4),
                    r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetString(6)));
            }
        }

        return [.. sales.Select(x => x with { Lines = lines.GetValueOrDefault(x.Id) ?? [], Tenders = tenders.GetValueOrDefault(x.Id) ?? [] })];
    }
}
