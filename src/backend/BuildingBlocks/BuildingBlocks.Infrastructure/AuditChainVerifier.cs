using System.Diagnostics.Metrics;
using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.BuildingBlocks.Infrastructure;

public sealed record ChainCheck(Guid TenantId, long LastSeq, bool Intact, long? BrokenSeq);

/// <summary>
/// AUD-002 / PM-09: verifies every tenant's audit hash chain (audit.verify_chain, with the tenant's context) and
/// anchors the chain head (seq + hash) in audit.chain_anchor, insert-only, so a later rewrite contradicts an earlier
/// anchor. The anchor moves to the WORM object store (MinIO Object Lock) when the infrastructure exists (X-07).
/// </summary>
public sealed class AuditChainVerifier(IUnitOfWork unitOfWork)
{
    public static readonly Meter Meter = new("RetailCloud.Audit");
    private static readonly Counter<long> Broken = Meter.CreateCounter<long>("rc_audit_chain_broken_total");

    public async Task<IReadOnlyList<ChainCheck>> VerifyAllAsync(CancellationToken cancellationToken, IReadOnlySet<Guid>? only = null)
    {
        IReadOnlyList<Guid> tenants = await unitOfWork.ExecuteWithoutTenantAsync<IReadOnlyList<Guid>>(null, async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT core.list_tenant_ids()");
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            var list = new List<Guid>();
            while (await r.ReadAsync(ct))
            {
                list.Add(r.GetGuid(0));
            }

            return list;
        }, cancellationToken);

        var results = new List<ChainCheck>();
        foreach (Guid t in tenants.Where(t => only is null || only.Contains(t)))
        {
            results.Add(await unitOfWork.ExecuteAsync(new FixedTenantContext(new TenantId(t)), async (s, ct) =>
            {
                var scope = (PostgresUnitOfWorkScope)s;
                long? broken;
                await using (NpgsqlCommand v = scope.Command("SELECT audit.verify_chain()"))
                {
                    broken = await v.ExecuteScalarAsync(ct) as long?;
                }

                long lastSeq = 0;
                byte[] lastHash = [0];
                await using (NpgsqlCommand h = scope.Command("SELECT seq, hash FROM audit.audit_record ORDER BY seq DESC LIMIT 1"))
                await using (NpgsqlDataReader r = await h.ExecuteReaderAsync(ct))
                {
                    if (await r.ReadAsync(ct))
                    {
                        (lastSeq, lastHash) = (r.GetInt64(0), r.GetFieldValue<byte[]>(1));
                    }
                }

                if (lastSeq == 0)
                {
                    return new ChainCheck(t, 0, true, null); // nothing audited yet: nothing to anchor
                }

                await using NpgsqlCommand a = scope.Command(
                    "INSERT INTO audit.chain_anchor (tenant_id, last_seq, last_hash, intact, broken_seq) VALUES (core.current_tenant(), $1, $2, $3, $4)");
                a.Parameters.Add(new NpgsqlParameter { Value = lastSeq });
                a.Parameters.Add(new NpgsqlParameter { Value = lastHash });
                a.Parameters.Add(new NpgsqlParameter { Value = broken is null });
                a.Parameters.Add(new NpgsqlParameter { Value = (object?)broken ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
                await a.ExecuteNonQueryAsync(ct);
                if (broken is not null)
                {
                    Broken.Add(1);
                }

                return new ChainCheck(t, lastSeq, broken is null, broken);
            }, cancellationToken));
        }

        return results;
    }
}
