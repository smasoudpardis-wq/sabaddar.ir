using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Inventory.Application;

namespace RetailCloud.Inventory.Infrastructure;

/// <summary>INV-008 headers on inv.stock_transfer (append-only; source and destination differ).</summary>
public sealed class PostgresStockTransfers : IStockTransfers
{
    private const string Columns = "id, from_warehouse_id, to_warehouse_id, status, line_count, value, note, created_by, posted_at";

    public async Task<TransferRecord> RecordAsync(IUnitOfWorkScope scope, TransferRecord transfer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "INSERT INTO inv.stock_transfer (tenant_id, id, from_warehouse_id, to_warehouse_id, status, line_count, value, note, created_by) " +
            "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7, $8) RETURNING posted_at");
        cmd.Parameters.Add(new NpgsqlParameter { Value = transfer.Id });
        cmd.Parameters.Add(new NpgsqlParameter { Value = transfer.FromWarehouseId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = transfer.ToWarehouseId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = transfer.Status });
        cmd.Parameters.Add(new NpgsqlParameter { Value = transfer.LineCount });
        cmd.Parameters.Add(new NpgsqlParameter { Value = transfer.Value });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)transfer.Note ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = transfer.CreatedBy });
        return transfer with { PostedAt = new DateTimeOffset((DateTime)(await cmd.ExecuteScalarAsync(cancellationToken))!, TimeSpan.Zero) };
    }

    public async Task<IReadOnlyList<TransferRecord>> ListAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command($"SELECT {Columns} FROM inv.stock_transfer ORDER BY posted_at DESC, id DESC LIMIT 200");
        var list = new List<TransferRecord>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new TransferRecord(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetInt32(4), r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.GetGuid(7), r.GetFieldValue<DateTimeOffset>(8)));
        }

        return list;
    }
}
