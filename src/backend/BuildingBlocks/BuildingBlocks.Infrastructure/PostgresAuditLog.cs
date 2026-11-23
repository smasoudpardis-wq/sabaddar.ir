using System.Net;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.BuildingBlocks.Infrastructure;

/// <summary>audit.audit_record writer; seq/prev_hash/hash come from the chain trigger (callers cannot choose them).</summary>
public sealed class PostgresAuditLog : IAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public async Task WriteAsync(IUnitOfWorkScope scope, AuditEntry entry, AuditActor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(actor);
        if (scope.TenantId is null)
        {
            throw new InvalidOperationException("Audit records are tenant-scoped.");
        }

        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "INSERT INTO audit.audit_record (tenant_id, id, actor_id, actor_kind, actor_roles, impersonated_by, branch_id, terminal_id, client_ip, " +
            "action, resource_type, resource_id, reason, details, seq, prev_hash, hash) " +
            "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, 0, '\\x00', '\\x00')");
        (object? Value, NpgsqlDbType Type)[] values =
        [
            (Guid.CreateVersion7(), NpgsqlDbType.Uuid),
            (actor.UserId, NpgsqlDbType.Uuid),
            (actor.Kind, NpgsqlDbType.Text),
            (actor.Roles.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text),
            (actor.ImpersonatedBy, NpgsqlDbType.Uuid),
            (actor.BranchId, NpgsqlDbType.Uuid),
            (actor.TerminalId, NpgsqlDbType.Uuid),
            (IPAddress.TryParse(actor.ClientIp, out IPAddress? ip) ? ip : null, NpgsqlDbType.Inet),
            (entry.Action, NpgsqlDbType.Text),
            (entry.ResourceType, NpgsqlDbType.Text),
            (entry.ResourceId, NpgsqlDbType.Text),
            (entry.Reason, NpgsqlDbType.Text),
            (AuditSanitizer.ToJson(entry.Details, Json), NpgsqlDbType.Jsonb), // AUD-003
        ];
        foreach ((object? value, NpgsqlDbType type) in values)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value, NpgsqlDbType = type });
        }

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
