using System.Text.Json;
using Npgsql;
using RetailCloud.Accounting.Infrastructure;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.BuildingBlocks.Infrastructure.Tests;

/// <summary>ACC-004 / AT-104 / UC-06: a closed shift's cash variance is posted once, as the Workers do it (rc_worker).</summary>
public sealed class ShiftPostingTests
{
    private static string App => Environment.GetEnvironmentVariable("RC_TEST_PG")!;

    [DbFact]
    public async Task A_shortage_is_posted_once_to_the_mapped_accounts()
    {
        Guid tenant = Guid.CreateVersion7(), cash = Guid.CreateVersion7(), shortage = Guid.CreateVersion7(), shift = Guid.CreateVersion7();
        await using var appDs = NpgsqlDataSource.Create(App);
        await new PostgresUnitOfWork(appDs).ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
        {
            await using NpgsqlBatch b = ((PostgresUnitOfWorkScope)s).Connection.CreateBatch();
            b.Transaction = ((PostgresUnitOfWorkScope)s).Transaction;
            b.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'شیفت', 'general')") { Parameters = { new() { Value = tenant } } });
            b.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO acc.account (tenant_id, id, code, name, account_type) VALUES ($1, $2, '1101', 'صندوق', 'Asset'), ($1, $3, '5103', 'کسری صندوق', 'Expense')")
                { Parameters = { new() { Value = tenant }, new() { Value = cash }, new() { Value = shortage } } });
            b.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO acc.account_role (tenant_id, role, account_id) VALUES ($1, 'CashOnHand', $2), ($1, 'CashShortage', $3)")
                { Parameters = { new() { Value = tenant }, new() { Value = cash }, new() { Value = shortage } } });
            b.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO acc.fiscal_period (tenant_id, id, fiscal_year, period_no, span) VALUES ($1, gen_random_uuid(), 1405, 9, tstzrange(now() - interval '5 days', now() + interval '5 days'))")
                { Parameters = { new() { Value = tenant } } });
            return await b.ExecuteNonQueryAsync(ct);
        });

        string worker = new NpgsqlConnectionStringBuilder(App) { Username = "rc_worker_test", Password = "rc_worker_test" }.ToString();
        await using var workerDs = NpgsqlDataSource.Create(worker);
        var uow = new PostgresUnitOfWork(workerDs);
        var consumer = new ShiftCashVarianceConsumer(new PostgresJournals());
        var evt = new IntegrationEvent(Guid.CreateVersion7(), "sales.shift-closed", 1, new TenantId(tenant), DateTimeOffset.UtcNow, null, null, "Sales", shift,
            JsonSerializer.SerializeToElement(new { shiftId = shift, variance = -100_000L, closedAt = DateTimeOffset.UtcNow }));
        for (int i = 0; i < 2; i++)   // redelivery
        {
            await uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) => { await consumer.HandleAsync(s, evt, ct); return 0; });
        }

        var lines = await uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)s).Command(
                "SELECT e.status, e.purpose, e.rule_version, l.account_id, l.debit, l.credit FROM acc.journal_entry e JOIN acc.journal_line l ON l.entry_id = e.id ORDER BY l.line_no");
            await using NpgsqlDataReader r = await c.ExecuteReaderAsync(ct);
            var rows = new List<(string, string, int, Guid, long, long)>();
            while (await r.ReadAsync(ct))
            {
                rows.Add((r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetGuid(3), r.GetInt64(4), r.GetInt64(5)));
            }

            return rows;
        });
        Assert.Equal([("Posted", "cash-variance", 1, shortage, 100_000L, 0L), ("Posted", "cash-variance", 1, cash, 0L, 100_000L)], lines);
    }
}
