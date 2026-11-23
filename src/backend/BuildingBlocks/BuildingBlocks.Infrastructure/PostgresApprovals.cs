using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.BuildingBlocks.Infrastructure;

/// <summary>iam.approval_request (15_iam.sql). Lives in BuildingBlocks because every module's commands may need it.</summary>
public sealed class PostgresApprovals(TimeProvider time) : IApprovals
{
    private const string Columns =
        "id, action, resource_type, resource_id, scope_type, scope_id, scope_branch, reason, requested_by, status, condition, value, limit_value, " +
        "expires_at, decided_by, decided_at";

    public static byte[] Binding(ApprovalAsk ask)
    {
        ArgumentNullException.ThrowIfNull(ask);
        string s = string.Join('\u001f', ask.Action, ask.ResourceType, ask.ResourceId ?? "", ask.Scope.Type, ask.Scope.Id?.ToString() ?? "", ask.Value ?? "");
        return SHA256.HashData(Encoding.UTF8.GetBytes(s));
    }

    public async Task<Guid> RequestAsync(IUnitOfWorkScope scope, ApprovalAsk ask, UserId requester, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ask);
        Guid id = Guid.CreateVersion7();
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "INSERT INTO iam.approval_request (tenant_id, id, action, resource_type, resource_id, scope_type, scope_id, scope_branch, condition, value, " +
            "limit_value, reason, binding_hash, requested_by, expires_at) VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)");
        object?[] args =
        [
            id, ask.Action, ask.ResourceType, ask.ResourceId, ask.Scope.Type.ToString(), ask.Scope.Id, ask.Scope.BranchId, ask.Condition,
            ask.Value, ask.Limit, ask.Reason, Binding(ask), requester.Value, time.GetUtcNow() + IApprovals.PendingLifetime,
        ];
        foreach (object? a in args)
        {
            cmd.Parameters.Add(a is byte[] b ? new NpgsqlParameter { Value = b, NpgsqlDbType = NpgsqlDbType.Bytea }
                : a is null ? new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Unknown } : new NpgsqlParameter { Value = a });
        }

        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    public async Task<ApprovalView?> GetAsync(IUnitOfWorkScope scope, Guid id, CancellationToken cancellationToken)
    {
        IReadOnlyList<ApprovalView> found = await Query(scope, $"SELECT {Columns} FROM iam.approval_request WHERE id = $1", cancellationToken, id);
        return found.Count == 0 ? null : found[0];
    }

    public Task<IReadOnlyList<ApprovalView>> ListOpenAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken) =>
        Query(scope, $"SELECT {Columns} FROM iam.approval_request WHERE status = 'Pending' AND expires_at > now() ORDER BY requested_at", cancellationToken);

    public async Task<DecisionOutcome> DecideAsync(IUnitOfWorkScope scope, Guid id, UserId approver, bool approve, string? note,
        CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using (NpgsqlCommand sel = s.Command("SELECT status, expires_at <= $2, requested_by FROM iam.approval_request WHERE id = $1 FOR UPDATE"))
        {
            sel.Parameters.Add(new NpgsqlParameter { Value = id });
            sel.Parameters.Add(new NpgsqlParameter { Value = time.GetUtcNow() });
            await using NpgsqlDataReader r = await sel.ExecuteReaderAsync(cancellationToken);
            if (!await r.ReadAsync(cancellationToken))
            {
                return DecisionOutcome.NotFound;
            }

            if (r.GetGuid(2) == approver.Value)
            {
                return DecisionOutcome.SelfApproval;
            }

            if (r.GetString(0) != "Pending")
            {
                return DecisionOutcome.NotPending;
            }

            if (r.GetBoolean(1))
            {
                await r.DisposeAsync();
                await Exec(s, "UPDATE iam.approval_request SET status = 'Expired' WHERE id = $1", cancellationToken, id);
                return DecisionOutcome.Expired;
            }
        }

        await using NpgsqlCommand upd = s.Command(
            "UPDATE iam.approval_request SET status = $2, decided_by = $3, decided_at = now(), decision_note = $4 WHERE id = $1");
        upd.Parameters.Add(new NpgsqlParameter { Value = id });
        upd.Parameters.Add(new NpgsqlParameter { Value = approve ? "Approved" : "Rejected" });
        upd.Parameters.Add(new NpgsqlParameter { Value = approver.Value });
        upd.Parameters.Add(new NpgsqlParameter { Value = (object?)note ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await upd.ExecuteNonQueryAsync(cancellationToken);
        return DecisionOutcome.Decided;
    }

    public async Task<bool> ConsumeAsync(IUnitOfWorkScope scope, Guid approvalId, ApprovalAsk ask, UserId requester, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "UPDATE iam.approval_request SET status = 'Consumed', consumed_at = now() " +
            "WHERE id = $1 AND status = 'Approved' AND binding_hash = $2 AND requested_by = $3 AND decided_at > $4");
        cmd.Parameters.Add(new NpgsqlParameter { Value = approvalId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = Binding(ask), NpgsqlDbType = NpgsqlDbType.Bytea });
        cmd.Parameters.Add(new NpgsqlParameter { Value = requester.Value });
        cmd.Parameters.Add(new NpgsqlParameter { Value = time.GetUtcNow() - IApprovals.ApprovalLifetime });
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<IReadOnlyList<ApprovalView>> Query(IUnitOfWorkScope scope, string sql, CancellationToken ct, params object[] args)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ApprovalView>();
        while (await r.ReadAsync(ct))
        {
            Guid? Gid(int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
            string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            DateTimeOffset? decidedAt = r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15);
            string status = r.GetString(9);
            list.Add(new ApprovalView(r.GetGuid(0), r.GetString(1), r.GetString(2), Str(3),
                new ScopeRef(Enum.Parse<ScopeType>(r.GetString(4)), Gid(5), Gid(6)), Str(7), r.GetGuid(8),
                status == "Pending" && r.GetFieldValue<DateTimeOffset>(13) <= DateTimeOffset.UtcNow ? "Expired" : status,
                r.GetString(10), Str(11), Str(12), r.GetFieldValue<DateTimeOffset>(13), Gid(14), decidedAt,
                status == "Approved" && decidedAt is { } d ? d + IApprovals.ApprovalLifetime : null));
        }

        return list;
    }

    private static async Task Exec(PostgresUnitOfWorkScope s, string sql, CancellationToken ct, params object[] args)
    {
        await using NpgsqlCommand cmd = s.Command(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
