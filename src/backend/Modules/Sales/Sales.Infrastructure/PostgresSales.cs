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
        "finalized_at, version";

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
                    r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13), r.GetInt32(14), []));
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

        return [.. sales.Select(x => x with { Lines = lines.GetValueOrDefault(x.Id) ?? [] })];
    }
}
