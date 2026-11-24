using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Inventory.Application;

namespace RetailCloud.Inventory.Infrastructure;

/// <summary>US-063 headers on inv.stock_adjustment (append-only; the database checks the note, the value split and the entry).</summary>
public sealed class PostgresStockAdjustments : IStockAdjustments
{
    private const string Columns =
        "id, warehouse_id, reason, note, line_count, value, shrinkage, overage, journal_entry_id, approval_id, created_by, posted_at";

    public async Task<AdjustmentRecord> RecordAsync(IUnitOfWorkScope scope, AdjustmentRecord adjustment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            $"INSERT INTO inv.stock_adjustment (tenant_id, {Columns}) " +
            "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, statement_timestamp()) RETURNING posted_at");
        cmd.Parameters.Add(new NpgsqlParameter { Value = adjustment.Id });
        cmd.Parameters.Add(new NpgsqlParameter { Value = adjustment.WarehouseId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = adjustment.Reason });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)adjustment.Note ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = adjustment.LineCount });
        cmd.Parameters.Add(new NpgsqlParameter { Value = adjustment.Value });
        cmd.Parameters.Add(new NpgsqlParameter { Value = adjustment.Shrinkage });
        cmd.Parameters.Add(new NpgsqlParameter { Value = adjustment.Overage });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)adjustment.JournalEntryId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)adjustment.ApprovalId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = adjustment.CreatedBy });
        var postedAt = (DateTime)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        return adjustment with { PostedAt = new DateTimeOffset(postedAt, TimeSpan.Zero) };
    }

    public async Task<IReadOnlyList<AdjustmentRecord>> ListAsync(IUnitOfWorkScope scope, IReadOnlyCollection<Guid>? warehouseIds,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            $"SELECT {Columns} FROM inv.stock_adjustment WHERE $1::uuid[] IS NULL OR warehouse_id = ANY($1) ORDER BY posted_at DESC, id DESC LIMIT 200");
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)warehouseIds?.ToArray() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid,
        });
        var list = new List<AdjustmentRecord>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new AdjustmentRecord(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4), r.GetInt64(5),
                r.GetInt64(6), r.GetInt64(7), r.IsDBNull(8) ? null : r.GetGuid(8), r.IsDBNull(9) ? null : r.GetGuid(9), r.GetGuid(10),
                r.GetFieldValue<DateTimeOffset>(11)));
        }

        return list;
    }
}
