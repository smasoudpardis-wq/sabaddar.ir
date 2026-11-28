using System.Net;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.BuildingBlocks.Infrastructure;

/// <summary>audit.audit_record writer; seq/prev_hash/hash come from the chain trigger (callers cannot choose them).</summary>
public sealed class PostgresAuditLog : IAuditLog, IAuditSearch
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

    public async Task<IReadOnlyList<AuditRecordView>> SearchAsync(IUnitOfWorkScope scope, AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var where = new List<string>();
        var args = new List<(object Value, NpgsqlDbType Type)>();
        void Add(string condition, object value, NpgsqlDbType type)
        {
            args.Add((value, type));
            where.Add(condition.Replace("@", "$" + args.Count, StringComparison.Ordinal));
        }

        if (query.BeforeSeq is { } before) Add("seq < @", before, NpgsqlDbType.Bigint);
        if (query.From is { } from) Add("occurred_at >= @", from, NpgsqlDbType.TimestampTz);
        if (query.To is { } to) Add("occurred_at < @", to, NpgsqlDbType.TimestampTz);
        if (query.ActorId is { } actor) Add("actor_id = @", actor, NpgsqlDbType.Uuid);
        if (query.ResourceType is { } rt) Add("resource_type = @", rt, NpgsqlDbType.Text);
        if (query.ResourceId is { } rid) Add("resource_id = @", rid, NpgsqlDbType.Text);
        if (query.Action is { } action)
        {
            if (action.EndsWith('.'))
            {
                string escaped = action.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal)
                    .Replace("_", @"\_", StringComparison.Ordinal);
                Add("action LIKE @", escaped + "%", NpgsqlDbType.Text);
            }
            else
            {
                Add("action = @", action, NpgsqlDbType.Text);
            }
        }

        args.Add((query.Limit, NpgsqlDbType.Integer));
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT seq, occurred_at, actor_kind, actor_id, actor_roles, impersonated_by, action, resource_type, resource_id, reason, " +
            "branch_id, terminal_id, host(client_ip), details::text FROM audit.audit_record" +
            (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
            $" ORDER BY seq DESC LIMIT ${args.Count}");
        foreach ((object value, NpgsqlDbType type) in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = value, NpgsqlDbType = type });
        }

        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        var rows = new List<AuditRecordView>();
        while (await r.ReadAsync(cancellationToken))
        {
            rows.Add(new AuditRecordView(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), N<Guid>(r, 3), r.GetFieldValue<string[]>(4),
                N<Guid>(r, 5), r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9),
                N<Guid>(r, 10), N<Guid>(r, 11), r.IsDBNull(12) ? null : r.GetString(12), r.GetString(13)));
        }

        return rows;
    }

    private static T? N<T>(NpgsqlDataReader r, int i) where T : struct => r.IsDBNull(i) ? null : r.GetFieldValue<T>(i);
}
