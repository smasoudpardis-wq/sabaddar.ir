using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Merchandising.Application;

namespace RetailCloud.Merchandising.Infrastructure;

/// <summary>BR-086 on merch.receipt_template_override: one row per store, created on its first change.</summary>
public sealed class PostgresReceiptTemplateOverrides : IReceiptTemplateOverrides
{
    public async Task<ReceiptOverride> GetAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT footer_text, signature, logo_object_key, version FROM merch.receipt_template_override");
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken)
            ? new ReceiptOverride(r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt32(3))
            : new ReceiptOverride(null, null, null, 0);
    }

    public async Task<ReceiptOverride?> SetAsync(IUnitOfWorkScope scope, int expectedVersion, string? footerText, string? signature, Guid updatedBy,
        CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using NpgsqlCommand cmd = s.Command(expectedVersion == 0
            ? "INSERT INTO merch.receipt_template_override (tenant_id, footer_text, signature, updated_by) VALUES (core.current_tenant(), $1, $2, $3) " +
              "ON CONFLICT (tenant_id) DO NOTHING RETURNING version"
            : "UPDATE merch.receipt_template_override SET footer_text = $1, signature = $2, updated_by = $3, updated_at = now(), version = version + 1 " +
              "WHERE version = $4 RETURNING version");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)footerText ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)signature ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = updatedBy });
        if (expectedVersion != 0)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = expectedVersion });
        }

        return await cmd.ExecuteScalarAsync(cancellationToken) is int version ? await GetAsync(scope, cancellationToken) with { Version = version } : null;
    }
}
