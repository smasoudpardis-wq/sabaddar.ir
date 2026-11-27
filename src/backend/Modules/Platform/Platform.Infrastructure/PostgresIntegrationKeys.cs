using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Platform.Application;

namespace RetailCloud.Platform.Infrastructure;

/// <summary>INT-001 on platform.integration_key (RLS); the lookup by prefix goes through platform.integration_key_target.</summary>
public sealed class PostgresIntegrationKeys : IIntegrationKeys
{
    private const string Columns = "id, name, prefix, scopes, created_at, expires_at, revoked_at, last_used_at";

    public async Task<IntegrationKeyView> CreateAsync(IUnitOfWorkScope scope, string prefix, byte[] secretHash, string name, IReadOnlyList<string> scopes,
        DateTimeOffset expiresAt, Guid createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "INSERT INTO platform.integration_key (tenant_id, id, prefix, secret_hash, name, scopes, created_by, expires_at) " +
            $"VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7) RETURNING {Columns}");
        cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = prefix });
        cmd.Parameters.Add(new NpgsqlParameter { Value = secretHash });
        cmd.Parameters.Add(new NpgsqlParameter { Value = name });
        cmd.Parameters.Add(new NpgsqlParameter { Value = scopes.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = createdBy });
        cmd.Parameters.Add(new NpgsqlParameter { Value = expiresAt.UtcDateTime, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        return (await ReadAsync(cmd, cancellationToken)).Single();
    }

    public async Task<IReadOnlyList<IntegrationKeyView>> ListAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command($"SELECT {Columns} FROM platform.integration_key ORDER BY created_at DESC LIMIT 100");
        return await ReadAsync(cmd, cancellationToken);
    }

    public async Task<bool> RevokeAsync(IUnitOfWorkScope scope, Guid keyId, Guid revokedBy, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "UPDATE platform.integration_key SET revoked_at = now(), revoked_by = $2 WHERE id = $1 AND revoked_at IS NULL");
        cmd.Parameters.Add(new NpgsqlParameter { Value = keyId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = revokedBy });
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IntegrationKeyTarget?> FindAsync(IUnitOfWorkScope scope, string prefix, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT tenant_id, key_id, secret_hash, scopes FROM platform.integration_key_target($1)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = prefix });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken)
            ? new IntegrationKeyTarget(r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<byte[]>(2), r.GetFieldValue<string[]>(3))
            : null;
    }

    public async Task TouchAsync(IUnitOfWorkScope scope, Guid keyId, CancellationToken cancellationToken)
    {
        // at most once a minute per key: the last use is for the store's overview, not an access log
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "UPDATE platform.integration_key SET last_used_at = now() WHERE id = $1 AND (last_used_at IS NULL OR last_used_at < now() - interval '1 minute')");
        cmd.Parameters.Add(new NpgsqlParameter { Value = keyId });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<IntegrationKeyView>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<IntegrationKeyView>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new IntegrationKeyView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3), r.GetFieldValue<DateTimeOffset>(4),
                r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7)));
        }

        return list;
    }
}
