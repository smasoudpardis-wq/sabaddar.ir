using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Platform.Application;

namespace RetailCloud.Platform.Infrastructure;

/// <summary>US-171 over platform.list_tenants (tenant directory) and platform.set_tenant_status (operator functions).</summary>
public sealed class PostgresTenantDirectory : ITenantDirectory
{
    public async Task<IReadOnlyList<PlatformTenantView>> ListAsync(IUnitOfWorkScope scope, Guid? after, int limit, CancellationToken cancellationToken) =>
        await ReadAsync(scope, after, limit + 1, null, cancellationToken);

    public async Task<PlatformTenantView?> GetAsync(IUnitOfWorkScope scope, Guid tenantId, CancellationToken cancellationToken) =>
        (await ReadAsync(scope, null, 1, tenantId, cancellationToken)).SingleOrDefault();

    public async Task<StatusChange> ChangeStatusAsync(IUnitOfWorkScope scope, Guid tenantId, string status, string reason, string operatorId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("SELECT platform.set_tenant_status($1, $2, $3, $4)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = tenantId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = status });
        cmd.Parameters.Add(new NpgsqlParameter { Value = reason });
        cmd.Parameters.Add(new NpgsqlParameter { Value = operatorId });
        return (string)(await cmd.ExecuteScalarAsync(cancellationToken))! switch
        {
            "ok" => StatusChange.Done,
            "not-found" => StatusChange.NotFound,
            _ => StatusChange.InvalidTransition,
        };
    }

    private static async Task<IReadOnlyList<PlatformTenantView>> ReadAsync(IUnitOfWorkScope scope, Guid? after, int limit, Guid? only, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT id, legal_name, status, plan_code, created_at, branches FROM platform.list_tenants($1, $2, $3)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)after ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = limit });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)only ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        var list = new List<PlatformTenantView>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new PlatformTenantView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetFieldValue<DateTimeOffset>(4),
                (int)r.GetInt64(5)));
        }

        return list;
    }
}
