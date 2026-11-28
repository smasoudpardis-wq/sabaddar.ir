using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Platform.Application;

namespace RetailCloud.Platform.Infrastructure;

/// <summary>BR-087 over platform.retention_policy (global, append-only), tenant_retention, legal_hold and retention_run.</summary>
public sealed class PostgresRetention : IRetention
{
    private const string InForce =
        "SELECT DISTINCT ON (category) category, version, minimum_days, default_days, valid_from, reason FROM platform.retention_policy " +
        "WHERE valid_from <= now() ORDER BY category, valid_from DESC, version DESC";

    public async Task<IReadOnlyList<RetentionPolicyView>> PoliciesAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(InForce);
        var list = new List<RetentionPolicyView>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new RetentionPolicyView(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetFieldValue<DateTimeOffset>(4), r.GetString(5)));
        }

        return list;
    }

    public async Task<(RetentionChange Outcome, RetentionPolicyView? Policy)> PublishAsync(IUnitOfWorkScope scope, string category, int minimumDays,
        int defaultDays, DateTimeOffset validFrom, string reason, string operatorId, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await s.Transaction.SaveAsync("retention", cancellationToken);
        try
        {
            await using NpgsqlCommand cmd = s.Command(
                "INSERT INTO platform.retention_policy (category, version, minimum_days, default_days, valid_from, reason, created_by) " +
                "SELECT $1, coalesce(max(version), 0) + 1, $2, $3, $4, $5, $6 FROM platform.retention_policy WHERE category = $1 RETURNING version");
            cmd.Parameters.Add(new NpgsqlParameter { Value = category });
            cmd.Parameters.Add(new NpgsqlParameter { Value = minimumDays });
            cmd.Parameters.Add(new NpgsqlParameter { Value = defaultDays });
            cmd.Parameters.Add(new NpgsqlParameter { Value = validFrom.ToUniversalTime(), NpgsqlDbType = NpgsqlDbType.TimestampTz });
            cmd.Parameters.Add(new NpgsqlParameter { Value = reason });
            cmd.Parameters.Add(new NpgsqlParameter { Value = operatorId });
            int version = (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
            await s.Transaction.ReleaseAsync("retention", cancellationToken);
            return (RetentionChange.Done, new RetentionPolicyView(category, version, minimumDays, defaultDays, validFrom, reason));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await s.Transaction.RollbackAsync("retention", cancellationToken);
            return (RetentionChange.Conflict, null);
        }
    }

    public async Task<bool> HoldAsync(IUnitOfWorkScope scope, Guid tenantId, string category, string reason, DateTimeOffset? until, string operatorId,
        CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await s.Transaction.SaveAsync("hold", cancellationToken);
        try
        {
            await using NpgsqlCommand cmd = s.Command("SELECT platform.set_legal_hold($1, $2, $3, $4, $5)");
            cmd.Parameters.Add(new NpgsqlParameter { Value = tenantId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = category });
            cmd.Parameters.Add(new NpgsqlParameter { Value = reason });
            cmd.Parameters.Add(new NpgsqlParameter { Value = until is { } u ? u.ToUniversalTime() : DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            cmd.Parameters.Add(new NpgsqlParameter { Value = operatorId });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await s.Transaction.ReleaseAsync("hold", cancellationToken);
            return true;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            await s.Transaction.RollbackAsync("hold", cancellationToken);
            return false;
        }
    }

    public async Task<bool> ReleaseAsync(IUnitOfWorkScope scope, Guid tenantId, string category, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("SELECT platform.release_legal_hold($1, $2)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = tenantId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = category });
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyList<StoreRetentionView>> StoreAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        // tenant_retention and legal_hold are row-level secured: only the current store's rows join
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT p.category, p.minimum_days, p.default_days, o.days, platform.effective_retention_days(p.category), h.category IS NOT NULL, h.reason, h.until " +
            $"FROM ({InForce}) p LEFT JOIN platform.tenant_retention o ON o.category = p.category " +
            "LEFT JOIN platform.legal_hold h ON h.category = p.category AND (h.until IS NULL OR h.until > now()) ORDER BY p.category");
        var list = new List<StoreRetentionView>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new StoreRetentionView(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.GetInt32(4), r.GetBoolean(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7)));
        }

        return list;
    }

    public async Task<RetentionChange> SetOverrideAsync(IUnitOfWorkScope scope, string category, int days, Guid userId, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await s.Transaction.SaveAsync("override", cancellationToken);
        try
        {
            await using NpgsqlCommand cmd = s.Command(
                "INSERT INTO platform.tenant_retention (tenant_id, category, days, set_by) VALUES (core.current_tenant(), $1, $2, $3) " +
                "ON CONFLICT (tenant_id, category) DO UPDATE SET days = EXCLUDED.days, set_by = EXCLUDED.set_by, set_at = now()");
            cmd.Parameters.Add(new NpgsqlParameter { Value = category });
            cmd.Parameters.Add(new NpgsqlParameter { Value = days });
            cmd.Parameters.Add(new NpgsqlParameter { Value = userId });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await s.Transaction.ReleaseAsync("override", cancellationToken);
            return RetentionChange.Done;
        }
        catch (PostgresException e) when (e.SqlState == "RC036")
        {
            await s.Transaction.RollbackAsync("override", cancellationToken);
            return RetentionChange.BelowFloor;
        }
    }

    public async Task<bool> ClearOverrideAsync(IUnitOfWorkScope scope, string category, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("DELETE FROM platform.tenant_retention WHERE category = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = category });
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<RetentionRunView>> RunsAsync(IUnitOfWorkScope scope, int limit, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT category, outcome, cutoff, deleted_rows, ran_at FROM platform.retention_run ORDER BY ran_at DESC, id LIMIT $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = limit });
        var list = new List<RetentionRunView>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new RetentionRunView(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetInt32(3), r.GetFieldValue<DateTimeOffset>(4)));
        }

        return list;
    }
}

/// <summary>
/// TEN-005.1: the nightly job. For each store it deletes the notifications (the one category with rows to delete in the MVP:
/// financial and audit data are archived, logs live in Loki) older than the store's effective retention, unless a legal hold is in
/// force; each run is recorded. The database refuses an early or held deletion anyway (RC036).
/// </summary>
public sealed class RetentionJob(IUnitOfWork unitOfWork, TimeProvider time)
{
    private static readonly string[] Terminal = ["Delivered", "Failed", "Undeliverable", "DeadLettered"];

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> tenants = await unitOfWork.ExecuteWithoutTenantAsync<IReadOnlyList<Guid>>(null, async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT * FROM core.list_tenant_ids()");
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            var list = new List<Guid>();
            while (await r.ReadAsync(ct))
            {
                list.Add(r.GetGuid(0));
            }

            return list;
        }, cancellationToken);

        int deleted = 0;
        foreach (Guid tenant in tenants)
        {
            deleted += await unitOfWork.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), (s, ct) => PurgeNotificationsAsync(s, ct), cancellationToken);
        }

        return deleted;
    }

    private async Task<int> PurgeNotificationsAsync(IUnitOfWorkScope scope, CancellationToken ct)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using NpgsqlCommand look = s.Command("SELECT platform.effective_retention_days('Notifications'), platform.legal_hold_active('Notifications')");
        int days;
        bool held;
        await using (NpgsqlDataReader r = await look.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            (days, held) = (r.GetInt32(0), r.GetBoolean(1));
        }

        DateTimeOffset cutoff = time.GetUtcNow().AddDays(-days);
        if (held)
        {
            await using NpgsqlCommand any = s.Command("SELECT EXISTS (SELECT 1 FROM notif.notification WHERE created_at < $1)");
            any.Parameters.Add(new NpgsqlParameter { Value = cutoff.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            if ((bool)(await any.ExecuteScalarAsync(ct))!)
            {
                await RecordAsync(s, "Held", cutoff, 0, ct);   // something was due and the hold stopped it
            }

            return 0;
        }

        int rows;
        await using (NpgsqlCommand attempts = s.Command(
                         "DELETE FROM notif.notification_attempt a USING notif.notification n WHERE a.tenant_id = n.tenant_id AND a.notification_id = n.id " +
                         "AND n.status = ANY($2) AND n.created_at < $1 AND a.started_at < $1"))
        {
            attempts.Parameters.Add(new NpgsqlParameter { Value = cutoff.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            attempts.Parameters.Add(new NpgsqlParameter { Value = Terminal });
            await attempts.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlCommand notes = s.Command(
                         "DELETE FROM notif.notification n WHERE n.status = ANY($2) AND n.created_at < $1 " +
                         "AND NOT EXISTS (SELECT 1 FROM notif.notification_attempt a WHERE a.tenant_id = n.tenant_id AND a.notification_id = n.id)"))
        {
            notes.Parameters.Add(new NpgsqlParameter { Value = cutoff.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            notes.Parameters.Add(new NpgsqlParameter { Value = Terminal });
            rows = await notes.ExecuteNonQueryAsync(ct);
        }

        if (rows > 0)
        {
            await RecordAsync(s, "Purged", cutoff, rows, ct);
        }

        return rows;
    }

    private static async Task RecordAsync(PostgresUnitOfWorkScope s, string outcome, DateTimeOffset cutoff, int rows, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = s.Command(
            "INSERT INTO platform.retention_run (tenant_id, id, category, outcome, cutoff, deleted_rows) VALUES (core.current_tenant(), $1, 'Notifications', $2, $3, $4)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = outcome });
        cmd.Parameters.Add(new NpgsqlParameter { Value = cutoff.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = rows });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
