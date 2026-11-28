using System.Diagnostics.Metrics;
using Npgsql;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.BuildingBlocks.Infrastructure;

public sealed record OracleFinding(string Oracle, Guid TenantId, string Detail);

/// <summary>
/// D10 §5.13 / PM-09 / INV-001: runs the consistency oracles (core.run_oracles, 91_oracles.sql) for every tenant.
/// Each finding is a bug or tampering: counted in <c>rc_oracle_findings_total{oracle}</c> and logged by the caller.
/// </summary>
public sealed class OracleRunner(IUnitOfWork unitOfWork)
{
    public static readonly Meter Meter = new("RetailCloud.Oracles");
    private static readonly Counter<long> Findings = Meter.CreateCounter<long>("rc_oracle_findings_total");

    public async Task<IReadOnlyList<OracleFinding>> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<OracleFinding> found = await unitOfWork.ExecuteWithoutTenantAsync<IReadOnlyList<OracleFinding>>(null, async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT oracle, tenant_id, detail FROM core.run_oracles()");
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            var list = new List<OracleFinding>();
            while (await r.ReadAsync(ct))
            {
                list.Add(new OracleFinding(r.GetString(0), r.GetGuid(1), r.GetString(2)));
            }

            return list;
        }, cancellationToken);
        foreach (OracleFinding f in found)
        {
            Findings.Add(1, new KeyValuePair<string, object?>("oracle", f.Oracle));
        }

        return found;
    }
}
