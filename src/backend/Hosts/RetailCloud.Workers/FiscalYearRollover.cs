using Npgsql;
using RetailCloud.Accounting.Application;
using RetailCloud.Accounting.Infrastructure;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;

namespace RetailCloud.Workers;

/// <summary>
/// ACC-009 / TEN-001.1: every store keeps the current and the next fiscal year's periods, so that a sale on 1 Farvardin
/// always has an open period. Run nightly (idempotent); only stores whose accounting is set up (they have periods)
/// are touched. A store whose periods do not fit its time zone any more is reported, not changed.
/// </summary>
public sealed class FiscalYearRollover(IUnitOfWork unitOfWork, TimeProvider time)
{
    public sealed record Result(int Created, IReadOnlyList<(Guid Tenant, string Error)> Failed);

    public async Task<Result> RunAsync(CancellationToken cancellationToken)
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

        return await RunForAsync(tenants, cancellationToken);
    }

    /// <summary>The same for the given stores only.</summary>
    public async Task<Result> RunForAsync(IReadOnlyCollection<Guid> tenants, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        int created = 0;
        var failed = new List<(Guid, string)>();
        DateTimeOffset now = time.GetUtcNow();
        foreach (Guid tenant in tenants)
        {
            try
            {
                created += await unitOfWork.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
                {
                    var scope = (PostgresUnitOfWorkScope)s;
                    await using NpgsqlCommand cmd = scope.Command(
                        "SELECT t.time_zone FROM platform.tenant t WHERE EXISTS (SELECT 1 FROM acc.fiscal_period)");
                    if (await cmd.ExecuteScalarAsync(ct) is not string zone)
                    {
                        return 0;   // accounting not set up yet: the tenant-setup consumer creates the first year
                    }

                    TimeZoneInfo tz = TimeZoneInfo.FindSystemTimeZoneById(zone);
                    int year = FiscalCalendar.FiscalYearOf(now, tz);
                    return await FiscalPeriods.EnsureYearAsync(s, year, tz, ct) + await FiscalPeriods.EnsureYearAsync(s, year + 1, tz, ct);
                }, cancellationToken);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ExclusionViolation)
            {
                failed.Add((tenant, "periods overlap the computed ones (time zone changed?)"));
            }
        }

        return new Result(created, failed);
    }
}
