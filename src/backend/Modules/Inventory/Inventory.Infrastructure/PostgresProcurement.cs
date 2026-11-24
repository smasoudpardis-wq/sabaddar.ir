using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Inventory.Application;

namespace RetailCloud.Inventory.Infrastructure;

/// <summary>PRC-001 / PRC-002 on inv.supplier and inv.goods_receipt(_line); receipts are append-only (DB checks the totals).</summary>
public sealed class PostgresProcurement : IProcurement
{
    private const string SupplierColumns = "id, name, national_id, economic_code, phone, created_at";
    private const string ReceiptColumns =
        "id, supplier_id, warehouse_id, supplier_invoice_no, effective_at, net_total, input_vat, total, journal_entry_id, created_by, posted_at";

    public async Task<(SupplierOutcome Outcome, SupplierRecord? Supplier)> CreateSupplierAsync(IUnitOfWorkScope scope, string name, string? nationalId,
        string? economicCode, string? phone, Guid createdBy, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        Guid id = Guid.CreateVersion7();
        await s.Transaction.SaveAsync("supplier", cancellationToken);
        try
        {
            await using NpgsqlCommand cmd = s.Command(
                $"INSERT INTO inv.supplier (tenant_id, id, name, national_id, economic_code, phone, created_by) " +
                $"VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6) RETURNING {SupplierColumns}");
            cmd.Parameters.Add(new NpgsqlParameter { Value = id });
            cmd.Parameters.Add(new NpgsqlParameter { Value = name });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)nationalId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)economicCode ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)phone ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = createdBy });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
            await r.ReadAsync(cancellationToken);
            SupplierRecord created = ReadSupplier(r);
            await r.DisposeAsync();
            await s.Transaction.ReleaseAsync("supplier", cancellationToken);
            return (SupplierOutcome.Done, created);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await s.Transaction.RollbackAsync("supplier", cancellationToken);
            return (SupplierOutcome.DuplicateNationalId, null);
        }
    }

    public async Task<IReadOnlyList<SupplierRecord>> SuppliersAsync(IUnitOfWorkScope scope, string? query, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            $"SELECT {SupplierColumns} FROM inv.supplier WHERE $1::text IS NULL OR strpos(name, $1) > 0 OR national_id = $1 ORDER BY name, id LIMIT 200");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)query ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        return await ReadSuppliersAsync(cmd, cancellationToken);
    }

    public async Task<SupplierRecord?> SupplierAsync(IUnitOfWorkScope scope, Guid supplierId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command($"SELECT {SupplierColumns} FROM inv.supplier WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = supplierId });
        return (await ReadSuppliersAsync(cmd, cancellationToken)).SingleOrDefault();
    }

    public async Task<bool> InvoiceSeenAsync(IUnitOfWorkScope scope, Guid supplierId, string invoiceNo, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT EXISTS (SELECT 1 FROM inv.goods_receipt WHERE supplier_id = $1 AND supplier_invoice_no = $2)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = supplierId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = invoiceNo });
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<ReceiptRecord> RecordReceiptAsync(IUnitOfWorkScope scope, ReceiptRecord receipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var s = (PostgresUnitOfWorkScope)scope;
        DateTimeOffset postedAt;
        await using (NpgsqlCommand cmd = s.Command(
            "INSERT INTO inv.goods_receipt (tenant_id, id, supplier_id, warehouse_id, supplier_invoice_no, effective_at, net_total, input_vat, total, " +
            "journal_entry_id, created_by) VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7, $8, $9, $10) RETURNING posted_at"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = receipt.Id });
            cmd.Parameters.Add(new NpgsqlParameter { Value = receipt.SupplierId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = receipt.WarehouseId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)receipt.SupplierInvoiceNo ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = receipt.EffectiveAt.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            cmd.Parameters.Add(new NpgsqlParameter { Value = receipt.NetTotal });
            cmd.Parameters.Add(new NpgsqlParameter { Value = receipt.InputVat });
            cmd.Parameters.Add(new NpgsqlParameter { Value = receipt.Total });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)receipt.JournalEntryId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            cmd.Parameters.Add(new NpgsqlParameter { Value = receipt.CreatedBy });
            postedAt = new DateTimeOffset((DateTime)(await cmd.ExecuteScalarAsync(cancellationToken))!, TimeSpan.Zero);
        }

        foreach (ReceiptLine l in receipt.Lines)
        {
            await using NpgsqlCommand line = s.Command(
                "INSERT INTO inv.goods_receipt_line (tenant_id, receipt_id, line_no, variant_id, qty, unit, unit_factor, unit_cost, discount, input_vat, line_cost) " +
                "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7, $8, $9, $10)");
            line.Parameters.Add(new NpgsqlParameter { Value = receipt.Id });
            line.Parameters.Add(new NpgsqlParameter { Value = l.LineNo });
            line.Parameters.Add(new NpgsqlParameter { Value = l.VariantId });
            line.Parameters.Add(new NpgsqlParameter { Value = l.Qty });
            line.Parameters.Add(new NpgsqlParameter { Value = l.Unit });
            line.Parameters.Add(new NpgsqlParameter { Value = l.UnitFactor });
            line.Parameters.Add(new NpgsqlParameter { Value = l.UnitCost });
            line.Parameters.Add(new NpgsqlParameter { Value = l.Discount });
            line.Parameters.Add(new NpgsqlParameter { Value = l.InputVat });
            line.Parameters.Add(new NpgsqlParameter { Value = l.LineCost });
            await line.ExecuteNonQueryAsync(cancellationToken);
        }

        return receipt with { PostedAt = postedAt };
    }

    public async Task<IReadOnlyList<ReceiptRecord>> ReceiptsAsync(IUnitOfWorkScope scope, IReadOnlyCollection<Guid>? warehouseIds,
        CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        var headers = new List<ReceiptRecord>();
        await using (NpgsqlCommand cmd = s.Command(
            $"SELECT {ReceiptColumns} FROM inv.goods_receipt WHERE $1::uuid[] IS NULL OR warehouse_id = ANY($1) ORDER BY posted_at DESC, id DESC LIMIT 100"))
        {
            cmd.Parameters.Add(new NpgsqlParameter
            {
                Value = (object?)warehouseIds?.ToArray() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
            {
                headers.Add(new ReceiptRecord(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), [],
                    r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.IsDBNull(8) ? null : r.GetGuid(8), r.GetGuid(9), r.GetFieldValue<DateTimeOffset>(10)));
            }
        }

        var lines = new Dictionary<Guid, List<ReceiptLine>>();
        await using (NpgsqlCommand cmd = s.Command(
            "SELECT receipt_id, line_no, variant_id, qty, unit, unit_factor, unit_cost, discount, input_vat FROM inv.goods_receipt_line " +
            "WHERE receipt_id = ANY($1) ORDER BY receipt_id, line_no"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = headers.Select(h => h.Id).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
            {
                Guid receiptId = r.GetGuid(0);
                if (!lines.TryGetValue(receiptId, out List<ReceiptLine>? list))
                {
                    lines[receiptId] = list = [];
                }

                list.Add(new ReceiptLine(r.GetInt32(1), r.GetGuid(2), r.GetDecimal(3), r.GetString(4), r.GetDecimal(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8)));
            }
        }

        return [.. headers.Select(h => h with { Lines = lines.GetValueOrDefault(h.Id) ?? [] })];
    }

    private static SupplierRecord ReadSupplier(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
            r.GetFieldValue<DateTimeOffset>(5));

    private static async Task<IReadOnlyList<SupplierRecord>> ReadSuppliersAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<SupplierRecord>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(ReadSupplier(r));
        }

        return list;
    }
}
