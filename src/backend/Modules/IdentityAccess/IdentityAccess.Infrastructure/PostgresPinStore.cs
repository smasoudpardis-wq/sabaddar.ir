using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.IdentityAccess.Application;

namespace RetailCloud.IdentityAccess.Infrastructure;

/// <summary>Argon2id in PHC format with the AUTH-08 parameters (m=64 MiB, t=3, p=1, 16-byte salt, 32-byte hash).</summary>
public static class PinHasher
{
    private const int MemoryKiB = 65536, Iterations = 3, Parallelism = 1;
    private const string Prefix = "$argon2id$v=19$m=65536,t=3,p=1$";

    public static string Hash(string pin)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return Prefix + B64(salt) + "$" + B64(Derive(pin, salt));
    }

    public static bool Verify(string pin, string phc)
    {
        ArgumentNullException.ThrowIfNull(phc);
        if (!phc.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = phc[Prefix.Length..].Split('$');
        if (parts.Length != 2)
        {
            return false;
        }

        byte[] expected = Unb64(parts[1]);
        return CryptographicOperations.FixedTimeEquals(Derive(pin, Unb64(parts[0])), expected);
    }

    private static byte[] Derive(string pin, byte[] salt)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(pin)) { Salt = salt, MemorySize = MemoryKiB, Iterations = Iterations, DegreeOfParallelism = Parallelism };
        return argon.GetBytes(32);
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=');

    private static byte[] Unb64(string s) => Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
}

public sealed class PostgresPinStore : IPinStore
{
    public async Task SetAsync(IUnitOfWorkScope scope, UserId user, string pin, CancellationToken cancellationToken)
    {
        if (!PinRules.IsValid(pin))
        {
            throw new ArgumentException("PIN must be 4-6 digits.", nameof(pin));
        }

        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "INSERT INTO iam.user_pin (tenant_id, user_id, pin_hash) VALUES (core.current_tenant(), $1, $2) " +
            "ON CONFLICT (tenant_id, user_id) DO UPDATE SET pin_hash = EXCLUDED.pin_hash, failed_attempts = 0, locked_at = NULL, updated_at = now()");
        cmd.Parameters.Add(new NpgsqlParameter { Value = user.Value });
        cmd.Parameters.Add(new NpgsqlParameter { Value = PinHasher.Hash(pin) });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(PinCheck Result, int AttemptsLeft)> VerifyAsync(IUnitOfWorkScope scope, UserId user, string pin, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        string hash;
        int failed;
        // FOR UPDATE: concurrent guesses are serialised, so five failures can never become more attempts
        await using (NpgsqlCommand sel = s.Command("SELECT pin_hash, failed_attempts, locked_at IS NOT NULL FROM iam.user_pin WHERE user_id = $1 FOR UPDATE"))
        {
            sel.Parameters.Add(new NpgsqlParameter { Value = user.Value });
            await using NpgsqlDataReader r = await sel.ExecuteReaderAsync(cancellationToken);
            if (!await r.ReadAsync(cancellationToken))
            {
                return (PinCheck.NotSet, 0);
            }

            if (r.GetBoolean(2))
            {
                return (PinCheck.Locked, 0);
            }

            (hash, failed) = (r.GetString(0), r.GetInt32(1));
        }

        bool ok = PinRules.IsValid(pin) && PinHasher.Verify(pin, hash);
        int nowFailed = ok ? 0 : failed + 1;
        await using NpgsqlCommand upd = s.Command(
            "UPDATE iam.user_pin SET failed_attempts = $2, locked_at = CASE WHEN $2 >= 5 THEN now() END WHERE user_id = $1");
        upd.Parameters.Add(new NpgsqlParameter { Value = user.Value });
        upd.Parameters.Add(new NpgsqlParameter { Value = nowFailed });
        await upd.ExecuteNonQueryAsync(cancellationToken);
        return ok ? (PinCheck.Ok, PinRules.MaxAttempts)
            : nowFailed >= PinRules.MaxAttempts ? (PinCheck.Locked, 0)
            : (PinCheck.Wrong, PinRules.MaxAttempts - nowFailed);
    }
}
