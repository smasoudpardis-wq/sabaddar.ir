using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.CustomerIdentity.Application;

namespace RetailCloud.CustomerIdentity.Infrastructure;

/// <summary>CUS-005 on cid.store_invite (tenant-scoped, revocable).</summary>
public sealed class PostgresStoreInvites : IStoreInvites
{
    public async Task<InviteView?> CreateAsync(IUnitOfWorkScope scope, Guid inviteId, Guid branchId, DateTimeOffset expiresAt, Guid createdBy, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using (NpgsqlCommand own = s.Command("SELECT 1 FROM platform.branch WHERE id = $1"))
        {
            own.Parameters.Add(new NpgsqlParameter { Value = branchId });
            if (await own.ExecuteScalarAsync(cancellationToken) is null)
            {
                return null;
            }
        }

        await using NpgsqlCommand cmd = s.Command("INSERT INTO cid.store_invite (tenant_id, id, branch_id, created_by, expires_at) VALUES (core.current_tenant(), $1, $2, $3, $4)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = inviteId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = branchId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = createdBy });
        cmd.Parameters.Add(new NpgsqlParameter { Value = expiresAt.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return await ActiveAsync(scope, inviteId, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<bool> RevokeAsync(IUnitOfWorkScope scope, Guid inviteId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("UPDATE cid.store_invite SET revoked_at = now() WHERE id = $1 AND revoked_at IS NULL");
        cmd.Parameters.Add(new NpgsqlParameter { Value = inviteId });
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<InviteView?> ActiveAsync(IUnitOfWorkScope scope, Guid inviteId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT i.branch_id, t.legal_name, b.name, i.expires_at FROM cid.store_invite i JOIN platform.tenant t ON t.id = i.tenant_id " +
            "JOIN platform.branch b ON b.tenant_id = i.tenant_id AND b.id = i.branch_id " +
            "WHERE i.id = $1 AND i.revoked_at IS NULL AND i.expires_at > $2 AND t.status NOT IN ('Suspended','Closed')");
        cmd.Parameters.Add(new NpgsqlParameter { Value = inviteId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = now.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? new InviteView(inviteId, r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)) : null;
    }
}

/// <summary>CUS-003 / CUS-007 / DI-062 / DI-065 on cid.tenant_customer_membership and the append-only cid.consent_record.</summary>
public sealed class PostgresMemberships : IMemberships
{
    public async Task<(JoinOutcome Outcome, MembershipView Membership)> JoinAsync(IUnitOfWorkScope scope, Guid customerId, string source, IReadOnlyList<ConsentChoice> choices,
        string policyVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        Guid id;
        JoinOutcome outcome = JoinOutcome.Joined;
        await using (NpgsqlCommand find = s.Command("SELECT id, status FROM cid.tenant_customer_membership WHERE global_customer_id = $1 FOR UPDATE"))
        {
            find.Parameters.Add(new NpgsqlParameter { Value = customerId });
            await using NpgsqlDataReader r = await find.ExecuteReaderAsync(cancellationToken);
            if (await r.ReadAsync(cancellationToken))
            {
                id = r.GetGuid(0);
                if (r.GetString(1) == "Active")
                {
                    outcome = JoinOutcome.AlreadyMember;
                }
            }
            else
            {
                id = Guid.Empty;
            }
        }

        if (outcome == JoinOutcome.AlreadyMember)
        {
            return (outcome, (await GetAsync(scope, id, customerId, cancellationToken))!);
        }

        if (id == Guid.Empty)
        {
            id = Guid.CreateVersion7();
            await Exec(s, "INSERT INTO cid.tenant_customer_membership (tenant_id, id, global_customer_id, join_source, joined_at) VALUES (core.current_tenant(), $1, $2, $3, $4)",
                cancellationToken, id, customerId, source, now.UtcDateTime);
        }
        else
        {
            await Exec(s, "UPDATE cid.tenant_customer_membership SET status = 'Active', join_source = $2, joined_at = $3 WHERE id = $1", cancellationToken, id, source, now.UtcDateTime);
        }

        await RecordAsync(s, id, choices, policyVersion, source, now, cancellationToken);
        return (JoinOutcome.Joined, (await GetAsync(scope, id, customerId, cancellationToken))!);
    }

    public async Task<IReadOnlyList<MembershipRef>> ListAsync(IUnitOfWorkScope scope, Guid customerId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("SELECT tenant_id, membership_id, store_name, joined_at FROM cid.my_memberships($1)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = customerId });
        var list = new List<MembershipRef>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new MembershipRef(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)));
        }

        return list;
    }

    public async Task<MembershipView?> GetAsync(IUnitOfWorkScope scope, Guid membershipId, Guid customerId, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        string? store;
        DateTimeOffset joined;
        await using (NpgsqlCommand cmd = s.Command(
                         "SELECT t.legal_name, m.joined_at FROM cid.tenant_customer_membership m JOIN platform.tenant t ON t.id = m.tenant_id " +
                         "WHERE m.id = $1 AND m.global_customer_id = $2 AND m.status = 'Active'"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = membershipId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = customerId });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await r.ReadAsync(cancellationToken))
            {
                return null;
            }

            (store, joined) = (r.GetString(0), r.GetFieldValue<DateTimeOffset>(1));
        }

        var consents = new List<ConsentView>();
        await using (NpgsqlCommand cmd = s.Command(
                         "SELECT DISTINCT ON (purpose, channel) purpose, channel, granted, recorded_at, policy_version FROM cid.consent_record " +
                         "WHERE membership_id = $1 ORDER BY purpose, channel, recorded_at DESC, id DESC"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = membershipId });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
            {
                consents.Add(new ConsentView(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetFieldValue<DateTimeOffset>(3), r.GetString(4)));
            }
        }

        return new MembershipView(membershipId, store, joined, consents);
    }

    public async Task<MembershipView?> UpdateConsentsAsync(IUnitOfWorkScope scope, Guid membershipId, Guid customerId, IReadOnlyList<ConsentChoice> choices, string policyVersion,
        string source, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await GetAsync(scope, membershipId, customerId, cancellationToken) is null)
        {
            return null;
        }

        await RecordAsync((PostgresUnitOfWorkScope)scope, membershipId, choices, policyVersion, source, now, cancellationToken);
        return await GetAsync(scope, membershipId, customerId, cancellationToken);
    }

    public async Task<bool> LeaveAsync(IUnitOfWorkScope scope, Guid membershipId, Guid customerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await GetAsync(scope, membershipId, customerId, cancellationToken) is not { } current)
        {
            return false;
        }

        var s = (PostgresUnitOfWorkScope)scope;
        await RecordAsync(s, membershipId, [.. current.Consents.Where(c => c.Granted).Select(c => new ConsentChoice(c.Purpose, c.Channel, false))], "leave", "Portal", now, cancellationToken);
        await Exec(s, "UPDATE cid.tenant_customer_membership SET status = 'Left' WHERE id = $1", cancellationToken, membershipId);
        return true;
    }

    private static async Task RecordAsync(PostgresUnitOfWorkScope s, Guid membershipId, IReadOnlyList<ConsentChoice> choices, string policyVersion, string source, DateTimeOffset now,
        CancellationToken ct)
    {
        foreach (ConsentChoice c in choices)
        {
            await Exec(s, "INSERT INTO cid.consent_record (tenant_id, id, membership_id, purpose, channel, granted, policy_version, source, recorded_at) VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7, $8)",
                ct, Guid.CreateVersion7(), membershipId, c.Purpose, c.Channel, c.Granted, policyVersion, source, now.UtcDateTime);
        }
    }

    private static async Task Exec(PostgresUnitOfWorkScope s, string sql, CancellationToken ct, params object[] args)
    {
        await using NpgsqlCommand cmd = s.Command(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(a is DateTime d ? new NpgsqlParameter { Value = d, NpgsqlDbType = NpgsqlDbType.TimestampTz } : new NpgsqlParameter { Value = a });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
