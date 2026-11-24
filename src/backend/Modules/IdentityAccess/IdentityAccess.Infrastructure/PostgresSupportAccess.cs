using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.IdentityAccess.Application;

namespace RetailCloud.IdentityAccess.Infrastructure;

public sealed class PostgresSupportAccess : ISupportAccess
{
    // the status the owner sees: time turns Pending (24 h) and Approved (grantedUntil) into Expired without a job
    private const string Select =
        "SELECT id, requested_by, reason, duration_minutes, " +
        "CASE WHEN status = 'Pending' AND expires_at <= now() THEN 'Expired' " +
        "     WHEN status = 'Approved' AND granted_until <= now() THEN 'Expired' ELSE status END, " +
        "requested_at, decided_at, granted_until, revoked_at FROM iam.support_access_request";

    public async Task<IReadOnlyList<SupportAccessView>> ListAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(Select + " ORDER BY requested_at DESC LIMIT 200");
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<SupportAccessView>();
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(Read(r));
        }

        return list;
    }

    public Task<(SupportAccessOutcome, SupportAccessView?)> DecideAsync(IUnitOfWorkScope scope, Guid id, bool approve, Guid owner,
        CancellationToken cancellationToken) =>
        ChangeAsync(scope, id, owner, approve
            ? "UPDATE iam.support_access_request SET status = 'Approved', decided_by = $2, decided_at = now(), " +
              "granted_until = now() + make_interval(mins => duration_minutes) WHERE id = $1 AND status = 'Pending' AND expires_at > now()"
            : "UPDATE iam.support_access_request SET status = 'Rejected', decided_by = $2, decided_at = now() " +
              "WHERE id = $1 AND status = 'Pending' AND expires_at > now()", cancellationToken);

    public async Task<SupportAccessView> RequestAsync(IUnitOfWorkScope scope, string requestedBy, string reason, int durationMinutes,
        CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        Guid id = Guid.CreateVersion7();
        await using (NpgsqlCommand cmd = s.Command(
            "INSERT INTO iam.support_access_request (tenant_id, id, requested_by, reason, duration_minutes) VALUES (core.current_tenant(), $1, $2, $3, $4)"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = id });
            cmd.Parameters.Add(new NpgsqlParameter { Value = requestedBy });
            cmd.Parameters.Add(new NpgsqlParameter { Value = reason });
            cmd.Parameters.Add(new NpgsqlParameter { Value = durationMinutes });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return (await GetAsync(s, id, cancellationToken))!;
    }

    public async Task<SupportAccessView?> ActiveGrantAsync(IUnitOfWorkScope scope, string requestedBy, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            Select + " WHERE requested_by = $1 AND status = 'Approved' AND granted_until > now() ORDER BY granted_until DESC LIMIT 1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = requestedBy });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? Read(r) : null;
    }

    public Task<(SupportAccessOutcome, SupportAccessView?)> RevokeAsync(IUnitOfWorkScope scope, Guid id, Guid owner, CancellationToken cancellationToken) =>
        ChangeAsync(scope, id, owner,
            "UPDATE iam.support_access_request SET status = 'Revoked', revoked_by = $2, revoked_at = now() " +
            "WHERE id = $1 AND status = 'Approved' AND granted_until > now()", cancellationToken);

    private static async Task<(SupportAccessOutcome, SupportAccessView?)> ChangeAsync(IUnitOfWorkScope scope, Guid id, Guid owner, string sql,
        CancellationToken ct)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using (NpgsqlCommand cmd = s.Command(sql))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = id });
            cmd.Parameters.Add(new NpgsqlParameter { Value = owner });
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            {
                return (await GetAsync(s, id, ct) is null ? SupportAccessOutcome.NotFound : SupportAccessOutcome.NotOpen, null);
            }
        }

        return (SupportAccessOutcome.Done, await GetAsync(s, id, ct));
    }

    private static async Task<SupportAccessView?> GetAsync(PostgresUnitOfWorkScope s, Guid id, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = s.Command(Select + " WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    private static SupportAccessView Read(NpgsqlDataReader r)
    {
        DateTimeOffset? T(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
        return new SupportAccessView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4),
            r.GetFieldValue<DateTimeOffset>(5), T(6), T(7), T(8));
    }
}
