using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.BuildingBlocks.Infrastructure;

/// <summary>
/// core.idempotency_record (01_core.sql). The InProgress row is written in the command's transaction, so it is never
/// visible to others: a duplicate blocks on the primary key until the first transaction ends (lock order L1, D03 §6.2).
/// </summary>
public sealed class PostgresIdempotencyStore : IIdempotencyStore
{
    /// <summary>Upper bound for waiting on a concurrent duplicate before answering 409 in-progress.</summary>
    public static readonly TimeSpan WaitForConcurrent = TimeSpan.FromSeconds(5);

    public async Task<IdempotencyClaim> ClaimAsync(IUnitOfWorkScope scope, string key, string operation, byte[] payloadHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadHash);
        var s = (PostgresUnitOfWorkScope)scope;

        // Savepoint: a lock timeout must not abort the whole transaction (we still answer within it).
        await s.Transaction.SaveAsync("idem_claim", cancellationToken);
        await using (NpgsqlCommand t = s.Command($"SET LOCAL lock_timeout = '{(int)WaitForConcurrent.TotalMilliseconds}ms'"))
        {
            await t.ExecuteNonQueryAsync(cancellationToken);
        }

        int inserted;
        try
        {
            await using NpgsqlCommand ins = s.Command(
                "INSERT INTO core.idempotency_record (tenant_id, key, operation, payload_hash, status) " +
                "VALUES (core.current_tenant(), $1, $2, $3, 'InProgress') ON CONFLICT (tenant_id, key) DO NOTHING");
            ins.Parameters.Add(new NpgsqlParameter { Value = key });
            ins.Parameters.Add(new NpgsqlParameter { Value = operation });
            ins.Parameters.Add(new NpgsqlParameter { Value = payloadHash, NpgsqlDbType = NpgsqlDbType.Bytea });
            inserted = await ins.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            await s.Transaction.RollbackAsync("idem_claim", cancellationToken);
            return new IdempotencyClaim.Busy();
        }

        await using (NpgsqlCommand reset = s.Command("SET LOCAL lock_timeout = DEFAULT"))
        {
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }

        if (inserted == 1)
        {
            return new IdempotencyClaim.Claimed();
        }

        await using NpgsqlCommand sel = s.Command(
            "SELECT operation, payload_hash, status, response_status, response FROM core.idempotency_record WHERE key = $1");
        sel.Parameters.Add(new NpgsqlParameter { Value = key });
        await using NpgsqlDataReader r = await sel.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken))
        {
            return new IdempotencyClaim.Busy(); // purged between the two statements; let the client retry
        }

        bool same = r.GetString(0) == operation && CryptographicOperations.FixedTimeEquals(r.GetFieldValue<byte[]>(1), payloadHash);
        if (!same)
        {
            return new IdempotencyClaim.Reused();
        }

        return r.GetString(2) == "Completed"
            ? new IdempotencyClaim.Replay(r.GetInt32(3), r.GetString(4))
            : new IdempotencyClaim.Busy();
    }

    public async Task CompleteAsync(IUnitOfWorkScope scope, string key, int responseStatus, string responseJson,
        CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using NpgsqlCommand cmd = s.Command(
            "UPDATE core.idempotency_record SET status = 'Completed', response_status = $2, response = $3, completed_at = now() " +
            "WHERE key = $1 AND status = 'InProgress'");
        cmd.Parameters.Add(new NpgsqlParameter { Value = key });
        cmd.Parameters.Add(new NpgsqlParameter { Value = responseStatus });
        cmd.Parameters.Add(new NpgsqlParameter { Value = responseJson });
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Idempotency record was not claimed in this unit of work.");
        }
    }
}
