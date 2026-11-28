using Npgsql;
using RetailCloud.Accounting.Application;
using RetailCloud.Accounting.Infrastructure;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.Workers;

namespace RetailCloud.BuildingBlocks.Infrastructure.Tests;

/// <summary>
/// ACC-009 / TEN-001.1: the nightly rollover keeps the current and the next fiscal year's periods for every store whose
/// accounting is set up, as the Workers run it (rc_worker), so that 1 Farvardin always has an open period.
/// </summary>
public sealed class FiscalYearRolloverTests
{
    private static string App => Environment.GetEnvironmentVariable("RC_TEST_PG")!;
    private static string Worker => new NpgsqlConnectionStringBuilder(App) { Username = "rc_worker_test", Password = "rc_worker_test" }.ToString();
    private static readonly TimeZoneInfo Tehran = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tehran");

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task<Guid> Store(PostgresUnitOfWork uow, Func<IUnitOfWorkScope, CancellationToken, Task>? accounting = null)
    {
        Guid tenant = Guid.CreateVersion7();
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'سال مالی', 'general')");
            cmd.Parameters.Add(new NpgsqlParameter { Value = tenant });
            await cmd.ExecuteNonQueryAsync(ct);
            if (accounting is not null)
            {
                await accounting(s, ct);
            }

            return 0;
        });
        return tenant;
    }

    private static Task<List<(int Year, int No, string Status)>> Periods(PostgresUnitOfWork uow, Guid tenant) =>
        uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT fiscal_year, period_no, status FROM acc.fiscal_period ORDER BY lower(span)");
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            var list = new List<(int, int, string)>();
            while (await r.ReadAsync(ct))
            {
                list.Add((r.GetInt32(0), r.GetInt32(1), r.GetString(2)));
            }

            return list;
        });

    [DbFact]
    public async Task Late_in_Esfand_next_years_twelve_periods_exist_and_1_Farvardin_has_an_open_period()
    {
        await using var appDs = NpgsqlDataSource.Create(App);
        var app = new PostgresUnitOfWork(appDs);
        Guid store = await Store(app, (s, ct) => FiscalPeriods.EnsureYearAsync(s, 1405, Tehran, ct));   // as the tenant-setup consumer does
        Guid noAccounting = await Store(app);

        await using var workerDs = NpgsqlDataSource.Create(Worker);
        var worker = new PostgresUnitOfWork(workerDs);
        DateTimeOffset esfand = new(2027, 3, 6, 12, 0, 0, TimeSpan.Zero);   // 1405-12-15
        var rollover = new FiscalYearRollover(worker, new FixedTime(esfand));
        FiscalYearRollover.Result first = await rollover.RunForAsync([store, noAccounting], default);
        Assert.Equal((12, 0), (first.Created, first.Failed.Count));

        List<(int Year, int No, string Status)> periods = await Periods(app, store);
        Assert.Equal(24, periods.Count);
        Assert.Equal((1406, 1, "Open"), periods[12]);
        DateTimeOffset nowruz = new(2027, 3, 20, 21, 0, 0, TimeSpan.Zero);   // 1406-01-01 00:00 Tehran
        Assert.Equal(1406, FiscalCalendar.FiscalYearOf(nowruz, Tehran));
        Assert.Empty(await Periods(app, noAccounting));   // not set up yet: left to the tenant-setup consumer

        Assert.Equal(0, (await rollover.RunForAsync([store], default)).Created);   // idempotent
    }

    [DbFact]
    public async Task A_store_whose_periods_do_not_match_the_calendar_is_reported_and_left_unchanged()
    {
        await using var appDs = NpgsqlDataSource.Create(App);
        var app = new PostgresUnitOfWork(appDs);
        Guid odd = await Store(app, async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(
                "INSERT INTO acc.fiscal_period (tenant_id, id, fiscal_year, period_no, span) VALUES " +
                "(core.current_tenant(), gen_random_uuid(), 1405, 9, tstzrange('2026-11-01 00:00+00', '2026-12-10 00:00+00'))");
            await cmd.ExecuteNonQueryAsync(ct);
        });

        await using var workerDs = NpgsqlDataSource.Create(Worker);
        FiscalYearRollover.Result r = await new FiscalYearRollover(new PostgresUnitOfWork(workerDs), new FixedTime(new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero)))
            .RunForAsync([odd], default);
        Assert.Equal(odd, Assert.Single(r.Failed).Tenant);
        Assert.Single(await Periods(app, odd));
    }
}
