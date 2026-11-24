using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RetailCloud.Accounting.Application;
using RetailCloud.BuildingBlocks.Api;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;

namespace RetailCloud.Api.Tests;

/// <summary>US-113 / ACC-009 / D-45 (AT-109): closing a period in order, and a late event posted today in the open period.</summary>
public sealed class PeriodCloseTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _owner = Guid.CreateVersion7(), _accountant = Guid.CreateVersion7(), _cash = Guid.CreateVersion7(),
        _sales = Guid.CreateVersion7(), _p6 = Guid.CreateVersion7(), _p7 = Guid.CreateVersion7(), _p8 = Guid.CreateVersion7();
    private readonly string _ownerSub = "close-owner-" + Guid.NewGuid(), _accountantSub = "close-accountant-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public PeriodCloseTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach ((Guid id, string sub) in new[] { (_owner, _ownerSub), (_accountant, _accountantSub) })
        {
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'کاربر')", id, ApiFactory.Issuer, sub));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'بستن دوره', 'general')", _tenant);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2), ($1, $3)", _tenant, _owner, _accountant);
            await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, gen_random_uuid(), $2, 'TenantOwner', 'Tenant'), " +
                              "($1, gen_random_uuid(), $3, 'Accountant', 'Tenant')", _tenant, _owner, _accountant);
            await Exec(s, ct, "INSERT INTO acc.account (tenant_id, id, code, name, account_type, is_system) VALUES ($1, $2, '1101', 'صندوق', 'Asset', true), " +
                              "($1, $3, '4101', 'فروش', 'Revenue', true)", _tenant, _cash, _sales);
            await Exec(s, ct, "INSERT INTO acc.account_role (tenant_id, role, account_id) VALUES ($1, 'CashOnHand', $2), ($1, 'SalesRevenue', $3)", _tenant, _cash, _sales);
            return await Exec(s, ct, "INSERT INTO acc.fiscal_period (tenant_id, id, fiscal_year, period_no, span) VALUES " +
                                     "($1, $2, 1405, 6, tstzrange(now() - interval '90 days', now() - interval '60 days')), " +
                                     "($1, $3, 1405, 7, tstzrange(now() - interval '60 days', now() - interval '30 days')), " +
                                     "($1, $4, 1405, 8, tstzrange(now() - interval '30 days', now() + interval '10 days'))",
                _tenant, _p6, _p7, _p8);
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<int> Exec(IUnitOfWorkScope s, CancellationToken ct, string sql, params object[] args)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private HttpClient As(string sub)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(sub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, _tenant.ToString());
        return c;
    }

    private static async Task<HttpResponseMessage> Idem(HttpClient c, string path, object? body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await c.SendAsync(req);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    private static async Task<string> Code(HttpResponseMessage r) => (await Body(r)).GetProperty("code").GetString()!;

    private object Sale(long amount, DateTimeOffset at) => new
    {
        effectiveAt = at, description = "فروش نقدی",
        lines = new object[] { new { accountId = _cash, debit = amount.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                               new { accountId = _sales, credit = amount.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
    };

    private static Task<HttpResponseMessage> Close(HttpClient c, Guid period, int version) =>
        Idem(c, $"/api/v1/accounting/periods/{period}/close", new { expectedVersion = version });

    [DbFact]
    public async Task AT109_a_period_closes_once_it_has_ended_in_order_and_without_drafts_and_never_reopens()
    {
        HttpClient owner = As(_ownerSub);
        Assert.Equal("period-not-ended", await Code(await Close(owner, _p8, 1)));   // today's documents need an open period
        Assert.Equal("earlier-period-open", await Code(await Close(owner, _p7, 1)));

        Guid draft = (await Body(await Idem(owner, "/api/v1/accounting/journals", Sale(5_000, DateTimeOffset.UtcNow.AddDays(-75))))).GetProperty("id").GetGuid();
        HttpResponseMessage blocked = await Close(owner, _p6, 1);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        JsonElement problem = await Body(blocked);
        Assert.Equal(("period-has-drafts", 1), (problem.GetProperty("code").GetString(), problem.GetProperty("context").GetProperty("drafts").GetInt32()));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/accounting/journals/{draft}")).StatusCode);

        Assert.Equal("version-conflict", await Code(await Close(owner, _p6, 7)));
        HttpResponseMessage closed = await Close(owner, _p6, 1);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        JsonElement p6 = await Body(closed);
        Assert.Equal(("Closed", 2), (p6.GetProperty("status").GetString(), p6.GetProperty("version").GetInt32()));
        Assert.Equal(JsonValueKind.String, p6.GetProperty("closedAt").ValueKind);
        Assert.Equal("period-closed", await Code(await Close(owner, _p6, 2)));

        // after closing nothing is dated in it, a draft included; the accountant (not only the owner) closes the next one
        Assert.Equal("period-closed", await Code(await Idem(owner, "/api/v1/accounting/journals", Sale(5_000, DateTimeOffset.UtcNow.AddDays(-75)))));
        Assert.Equal(HttpStatusCode.OK, (await Close(As(_accountantSub), _p7, 1)).StatusCode);

        JsonElement[] periods = [.. (await Body(await owner.GetAsync("/api/v1/accounting/periods"))).EnumerateArray()];
        Assert.Equal(["Closed", "Closed", "Open"], periods.Select(p => p.GetProperty("status").GetString()));

        await using var worker = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(Cs) { Username = "rc_worker_test", Password = "rc_worker_test" }.ToString());
        long events = await new PostgresUnitOfWork(worker).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(
                "SELECT count(*) FROM core.outbox WHERE event_type = 'accounting.period-closed' AND aggregate_id = ANY($1)");
            cmd.Parameters.Add(new NpgsqlParameter { Value = new[] { _p6, _p7 } });
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        });
        Assert.Equal(2, events);
    }

    [DbFact]
    public async Task ACC009_a_late_event_of_a_closed_period_is_posted_today_in_the_open_period_and_refers_to_its_source()
    {
        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        Guid closedEarly = Guid.CreateVersion7(), open = Guid.CreateVersion7();
        Guid tenant = Guid.CreateVersion7();
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'دیررس', 'general')", tenant);
            await Exec(s, ct, "INSERT INTO acc.account (tenant_id, id, code, name, account_type, is_system) VALUES ($1, $2, '1101', 'صندوق', 'Asset', true), " +
                              "($1, $3, '4101', 'فروش', 'Revenue', true)", tenant, _cash, _sales);
            await Exec(s, ct, "INSERT INTO acc.account_role (tenant_id, role, account_id) VALUES ($1, 'CashOnHand', $2), ($1, 'SalesRevenue', $3)", tenant, _cash, _sales);
            return await Exec(s, ct, "INSERT INTO acc.fiscal_period (tenant_id, id, fiscal_year, period_no, span, status) VALUES " +
                                     "($1, $2, 1405, 7, tstzrange(now() - interval '60 days', now() - interval '30 days'), 'Closed'), " +
                                     "($1, $3, 1405, 8, tstzrange(now() - interval '30 days', now() + interval '10 days'), 'Open')", tenant, closedEarly, open);
        });

        Guid sale = Guid.CreateVersion7();
        DateTimeOffset happened = DateTimeOffset.UtcNow.AddDays(-45);
        IJournals journals = _factory.Services.GetRequiredService<IJournals>();
        AccountingResult<JournalView> posted = await uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), (s, ct) =>
            journals.PostAutomaticAsync(s, SaleRule.For(sale, "B1-1405-000077", 1_000, 0, 1_000, 0, happened), ct));

        Assert.Equal(AccountingOutcome.Done, posted.Outcome);
        JournalView entry = posted.Value!;
        Assert.Equal(("Posted", "Sale", (Guid?)sale), (entry.Status, entry.SourceType, entry.SourceId));
        Assert.True(entry.EffectiveAt > DateTimeOffset.UtcNow.AddMinutes(-5), $"dated today, not {entry.EffectiveAt}");
        Assert.Contains("دیررس", entry.Description, StringComparison.Ordinal);
        Assert.Contains(happened.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), entry.Description, StringComparison.Ordinal);
    }
}
