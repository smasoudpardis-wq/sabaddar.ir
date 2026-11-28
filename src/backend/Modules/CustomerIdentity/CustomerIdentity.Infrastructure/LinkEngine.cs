using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.CustomerIdentity.Application;

namespace RetailCloud.CustomerIdentity.Infrastructure;

/// <summary>US-143 on cid.qr_token and the global customer tables.</summary>
public sealed class PostgresCustomerLinks(IContactProtector protector) : ICustomerLinks
{
    public async Task<(Guid Jti, DateTimeOffset ExpiresAt)> IssueQrAsync(IUnitOfWorkScope scope, Guid customerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid jti = Guid.CreateVersion7();
        DateTimeOffset expires = now + CustomerQrToken.Lifetime;
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("INSERT INTO cid.qr_token (jti, global_customer_id, created_at, expires_at) VALUES ($1, $2, $3, $4)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = jti });
        cmd.Parameters.Add(new NpgsqlParameter { Value = customerId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = now.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = expires.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return (jti, expires);
    }

    public async Task<Guid?> ConsumeQrAsync(IUnitOfWorkScope scope, Guid jti, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // one statement: only the first of two simultaneous scans gets the row
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "UPDATE cid.qr_token SET used_at = $2 WHERE jti = $1 AND used_at IS NULL AND expires_at > $2 RETURNING global_customer_id");
        cmd.Parameters.Add(new NpgsqlParameter { Value = jti });
        cmd.Parameters.Add(new NpgsqlParameter { Value = now.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        return await cmd.ExecuteScalarAsync(cancellationToken) as Guid?;
    }

    public async Task<CustomerView?> FindByCodeAsync(IUnitOfWorkScope scope, string publicCode, CancellationToken cancellationToken) =>
        await FindAsync(scope, "c.public_code = $1", publicCode, cancellationToken);

    public async Task<CustomerView?> FindByPhoneHashAsync(IUnitOfWorkScope scope, byte[] phoneHash, CancellationToken cancellationToken) =>
        await FindAsync(scope, "v.lookup_hash = $1", phoneHash, cancellationToken);

    public async Task<string?> PhoneOfAsync(IUnitOfWorkScope scope, Guid customerId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("SELECT value_enc FROM cid.verified_contact WHERE global_customer_id = $1 AND status = 'Active' AND contact_type = 'Mobile'");
        cmd.Parameters.Add(new NpgsqlParameter { Value = customerId });
        return await cmd.ExecuteScalarAsync(cancellationToken) is byte[] enc ? protector.Unprotect(enc) : null;
    }

    private async Task<CustomerView?> FindAsync(IUnitOfWorkScope scope, string where, object value, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT c.id, c.public_code, v.value_enc FROM cid.global_customer c JOIN cid.verified_contact v ON v.global_customer_id = c.id AND v.status = 'Active' " +
            $"WHERE c.status = 'Active' AND {where}");
        cmd.Parameters.Add(new NpgsqlParameter { Value = value });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new CustomerView(r.GetGuid(0), r.GetString(1), false, PhoneNumber.Mask(protector.Unprotect(r.GetFieldValue<byte[]>(2)))) : null;
    }
}

/// <summary>US-143: the store's own members (current tenant only) and the join requests that wait for the customer's consent.</summary>
public sealed class PostgresStoreMembers(IContactProtector protector) : IStoreMembers
{
    public async Task<TenantCustomerView?> FindAsync(IUnitOfWorkScope scope, Guid customerId, CancellationToken cancellationToken) =>
        await ReadAsync(scope, "m.global_customer_id = $1", customerId, cancellationToken);

    public async Task<TenantCustomerView?> GetAsync(IUnitOfWorkScope scope, Guid tenantCustomerId, CancellationToken cancellationToken) =>
        await ReadAsync(scope, "m.id = $1", tenantCustomerId, cancellationToken);

    public async Task<Guid> CreateJoinRequestAsync(IUnitOfWorkScope scope, Guid customerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid id = Guid.CreateVersion7();
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "INSERT INTO cid.join_request (tenant_id, id, global_customer_id, created_at, expires_at) VALUES (core.current_tenant(), $1, $2, $3, $4)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { Value = customerId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = now.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = now.AddMinutes(10).UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    public async Task<Guid?> JoinRequestTenantAsync(IUnitOfWorkScope scope, Guid requestId, Guid customerId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("SELECT cid.pending_join_request_tenant($1, $2)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = requestId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = customerId });
        return await cmd.ExecuteScalarAsync(cancellationToken) as Guid?;
    }

    public async Task<bool> AcceptJoinRequestAsync(IUnitOfWorkScope scope, Guid requestId, Guid customerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "UPDATE cid.join_request SET accepted_at = $3 WHERE id = $1 AND global_customer_id = $2 AND accepted_at IS NULL AND expires_at > $3");
        cmd.Parameters.Add(new NpgsqlParameter { Value = requestId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = customerId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = now.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<TenantCustomerView?> ReadAsync(IUnitOfWorkScope scope, string where, Guid value, CancellationToken ct)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        Guid id, customer;
        DateTimeOffset joined;
        byte[] enc;
        await using (NpgsqlCommand cmd = s.Command(
                         "SELECT m.id, m.global_customer_id, m.joined_at, v.value_enc FROM cid.tenant_customer_membership m " +
                         "JOIN cid.verified_contact v ON v.global_customer_id = m.global_customer_id AND v.status = 'Active' " +
                         $"WHERE m.status = 'Active' AND {where}"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = value });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
            {
                return null;
            }

            (id, customer, joined, enc) = (r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2), r.GetFieldValue<byte[]>(3));
        }

        var consents = new List<ConsentView>();
        await using (NpgsqlCommand cmd = s.Command(
                         "SELECT DISTINCT ON (purpose, channel) purpose, channel, granted, recorded_at, policy_version FROM cid.consent_record " +
                         "WHERE membership_id = $1 ORDER BY purpose, channel, recorded_at DESC, id DESC"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = id });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                consents.Add(new ConsentView(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetFieldValue<DateTimeOffset>(3), r.GetString(4)));
            }
        }

        return new TenantCustomerView(id, customer, PhoneNumber.Mask(protector.Unprotect(enc)), joined, consents);
    }
}
