using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.CustomerIdentity.Application;

namespace RetailCloud.CustomerIdentity.Infrastructure;

/// <summary>DI-064: AES-256-GCM for the phone (nonce || tag || cipher) and HMAC-SHA256 for blind indexes. Keys come from the environment; the KEK moves to OpenBao Transit with the secrets story.</summary>
public sealed class AesContactProtector(byte[] encryptionKey, byte[] indexKey) : IContactProtector
{
    public static AesContactProtector FromBase64(string encryptionKey, string indexKey) => new(Convert.FromBase64String(encryptionKey), Convert.FromBase64String(indexKey));

    public byte[] Hash(string purpose, string value) => HMACSHA256.HashData(indexKey, Encoding.UTF8.GetBytes(purpose + "\0" + value));

    public byte[] Protect(string value)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12), plain = Encoding.UTF8.GetBytes(value), cipher = new byte[plain.Length], tag = new byte[16];
        using var aes = new AesGcm(encryptionKey, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    public string Unprotect(byte[] protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        byte[] plain = new byte[protectedValue.Length - 28];
        using var aes = new AesGcm(encryptionKey, 16);
        aes.Decrypt(protectedValue.AsSpan(0, 12), protectedValue.AsSpan(28), protectedValue.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>CUS-001 on cid.otp_challenge: the limits are counted and the challenge stored under one advisory lock per phone.</summary>
public sealed class PostgresOtpChallenges : IOtpChallenges
{
    public async Task<OtpStart> StartAsync(IUnitOfWorkScope scope, byte[] phoneHash, byte[] ipHash, byte[] codeHash, byte[] protectedPhone, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using (NpgsqlCommand lck = s.Command("SELECT pg_advisory_xact_lock(hashtextextended(encode($1, 'hex'), 0))"))
        {
            lck.Parameters.Add(new NpgsqlParameter { Value = phoneHash });
            await lck.ExecuteNonQueryAsync(cancellationToken);
        }

        DateTimeOffset latest = DateTimeOffset.MinValue, firstInHour = DateTimeOffset.MaxValue, firstInDay = DateTimeOffset.MaxValue;
        int hour = 0, day = 0, address;
        await using (NpgsqlCommand cmd = s.Command(
                         "SELECT created_at FROM cid.otp_challenge WHERE phone_hash = $1 AND created_at > $2 - interval '1 day' ORDER BY created_at"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = phoneHash });
            cmd.Parameters.Add(new NpgsqlParameter { Value = now.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
            {
                var at = r.GetFieldValue<DateTimeOffset>(0);
                day++;
                firstInDay = at < firstInDay ? at : firstInDay;
                latest = at > latest ? at : latest;
                if (at > now.AddHours(-1))
                {
                    hour++;
                    firstInHour = at < firstInHour ? at : firstInHour;
                }
            }
        }

        await using (NpgsqlCommand cmd = s.Command("SELECT count(*) FROM cid.otp_challenge WHERE ip_hash = $1 AND created_at > $2 - interval '1 hour'"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = ipHash });
            cmd.Parameters.Add(new NpgsqlParameter { Value = now.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            address = (int)(long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        }

        if (day > 0 && now - latest < OtpPolicy.ResendAfter)
        {
            return new OtpStart(OtpStartOutcome.TooSoon, Guid.Empty, default, default, latest + OtpPolicy.ResendAfter - now);
        }

        if (hour >= OtpPolicy.PerPhonePerHour)
        {
            return new OtpStart(OtpStartOutcome.PhoneLimit, Guid.Empty, default, default, firstInHour.AddHours(1) - now);
        }

        if (day >= OtpPolicy.PerPhonePerDay)
        {
            return new OtpStart(OtpStartOutcome.PhoneLimit, Guid.Empty, default, default, firstInDay.AddDays(1) - now);
        }

        if (address >= OtpPolicy.PerAddressPerHour)
        {
            return new OtpStart(OtpStartOutcome.AddressLimit, Guid.Empty, default, default, TimeSpan.FromMinutes(10));
        }

        Guid id = Guid.CreateVersion7();
        DateTimeOffset expires = now + OtpPolicy.Validity;
        await using NpgsqlCommand ins = s.Command("INSERT INTO cid.otp_challenge (id, phone_hash, ip_hash, code_hash, phone_enc, created_at, expires_at) VALUES ($1, $2, $3, $4, $5, $6, $7)");
        ins.Parameters.Add(new NpgsqlParameter { Value = id });
        ins.Parameters.Add(new NpgsqlParameter { Value = phoneHash });
        ins.Parameters.Add(new NpgsqlParameter { Value = ipHash });
        ins.Parameters.Add(new NpgsqlParameter { Value = codeHash });
        ins.Parameters.Add(new NpgsqlParameter { Value = protectedPhone });
        ins.Parameters.Add(new NpgsqlParameter { Value = now.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        ins.Parameters.Add(new NpgsqlParameter { Value = expires.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        await ins.ExecuteNonQueryAsync(cancellationToken);
        return new OtpStart(OtpStartOutcome.Started, id, expires, now + OtpPolicy.ResendAfter, TimeSpan.Zero);
    }

    public async Task<OtpChallengeState?> LockAsync(IUnitOfWorkScope scope, Guid challengeId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT phone_hash, code_hash, phone_enc, attempts, status, expires_at FROM cid.otp_challenge WHERE id = $1 FOR UPDATE");
        cmd.Parameters.Add(new NpgsqlParameter { Value = challengeId });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken)
            ? new OtpChallengeState(challengeId, r.GetFieldValue<byte[]>(0), r.GetFieldValue<byte[]>(1), r.GetFieldValue<byte[]>(2), r.GetInt32(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5))
            : null;
    }

    public async Task<int> FailAttemptAsync(IUnitOfWorkScope scope, Guid challengeId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "UPDATE cid.otp_challenge SET attempts = attempts + 1, status = CASE WHEN attempts + 1 >= $2 THEN 'Failed' ELSE status END WHERE id = $1 RETURNING attempts");
        cmd.Parameters.Add(new NpgsqlParameter { Value = challengeId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = OtpPolicy.MaxAttempts });
        return OtpPolicy.MaxAttempts - (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task VerifiedAsync(IUnitOfWorkScope scope, Guid challengeId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("UPDATE cid.otp_challenge SET status = 'Verified' WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = challengeId });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>CUS-002 / DI-060/061 on cid.global_customer, public_code_registry and verified_contact.</summary>
public sealed class PostgresCustomers(IContactProtector protector) : ICustomers
{
    public async Task<CustomerView> FindOrCreateAsync(IUnitOfWorkScope scope, string e164, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        byte[] hash = protector.Hash("phone", e164);
        await using (NpgsqlCommand lck = s.Command("SELECT pg_advisory_xact_lock(hashtextextended(encode($1, 'hex'), 1))"))
        {
            lck.Parameters.Add(new NpgsqlParameter { Value = hash });
            await lck.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (NpgsqlCommand find = s.Command(
                         "SELECT c.id FROM cid.verified_contact v JOIN cid.global_customer c ON c.id = v.global_customer_id " +
                         "WHERE v.lookup_hash = $1 AND v.status = 'Active' AND c.status = 'Active'"))
        {
            find.Parameters.Add(new NpgsqlParameter { Value = hash });
            if (await find.ExecuteScalarAsync(cancellationToken) is Guid existing)
            {
                return (await GetAsync(scope, existing, cancellationToken))! with { IsNew = false };
            }
        }

        Guid id = Guid.CreateVersion7();
        string code = await NewCodeAsync(s, cancellationToken);
        await Exec(s, "INSERT INTO cid.global_customer (id, public_code) VALUES ($1, $2)", cancellationToken, id, code);
        await Exec(s, "INSERT INTO cid.public_code_registry (public_code, global_customer_id) VALUES ($1, $2)", cancellationToken, code, id);
        await Exec(s, "INSERT INTO cid.verified_contact (id, global_customer_id, contact_type, value_enc, lookup_hash, verified_at) VALUES ($1, $2, 'Mobile', $3, $4, $5)",
            cancellationToken, Guid.CreateVersion7(), id, protector.Protect(e164), hash, now.UtcDateTime);
        return new CustomerView(id, code, true, PhoneNumber.Mask(e164));
    }

    public async Task<CustomerView?> GetAsync(IUnitOfWorkScope scope, Guid customerId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT c.public_code, v.value_enc FROM cid.global_customer c JOIN cid.verified_contact v ON v.global_customer_id = c.id AND v.status = 'Active' " +
            "WHERE c.id = $1 AND c.status = 'Active'");
        cmd.Parameters.Add(new NpgsqlParameter { Value = customerId });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken)
            ? new CustomerView(customerId, r.GetString(0), false, PhoneNumber.Mask(protector.Unprotect(r.GetFieldValue<byte[]>(1))))
            : null;
    }

    public async Task<CustomerView?> RotateCodeAsync(IUnitOfWorkScope scope, Guid customerId, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        if (await GetAsync(scope, customerId, cancellationToken) is not { } current)
        {
            return null;
        }

        string code = await NewCodeAsync(s, cancellationToken);
        await Exec(s, "UPDATE cid.public_code_registry SET retired_at = now() WHERE global_customer_id = $1 AND retired_at IS NULL", cancellationToken, customerId);
        await Exec(s, "INSERT INTO cid.public_code_registry (public_code, global_customer_id) VALUES ($1, $2)", cancellationToken, code, customerId);
        await Exec(s, "UPDATE cid.global_customer SET public_code = $2 WHERE id = $1", cancellationToken, customerId, code);
        return current with { PublicCode = code };
    }

    /// <summary>A code never issued before, to anyone (DI-061): the registry keeps every code ever used.</summary>
    private static async Task<string> NewCodeAsync(PostgresUnitOfWorkScope s, CancellationToken ct)
    {
        for (int i = 0; i < 10; i++)
        {
            string code = PublicCode.Generate();
            await using NpgsqlCommand cmd = s.Command("SELECT 1 FROM cid.public_code_registry WHERE public_code = $1");
            cmd.Parameters.Add(new NpgsqlParameter { Value = code });
            if (await cmd.ExecuteScalarAsync(ct) is null)
            {
                return code;
            }
        }

        throw new InvalidOperationException("no free public code");
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

/// <summary>Development only: no SMS goes out; the masked number and key are logged, never the code.</summary>
public sealed partial class LogOtpSender(ILogger<LogOtpSender> logger) : IOtpSender
{
    public Task<bool> SendAsync(string e164, string code, string idempotencyKey, CancellationToken cancellationToken)
    {
        string masked = PhoneNumber.Mask(e164);
        LogSent(logger, masked, idempotencyKey);
        return Task.FromResult(true);
    }

    [LoggerMessage(EventId = 5301, Level = LogLevel.Information, Message = "dev-otp to {Phone} key={Key}")]
    private static partial void LogSent(ILogger logger, string phone, string key);
}
