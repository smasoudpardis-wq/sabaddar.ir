using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using RetailCloud.BuildingBlocks.Api;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;

namespace RetailCloud.Api.Tests;

/// <summary>US-114 / ACC-007 (AT-107): the trial balance and the ledger (کل، معین، تفصیلی) over posted entries.</summary>
public sealed class AccountingReportTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _owner = Guid.CreateVersion7(), _cash = Guid.CreateVersion7(), _sales = Guid.CreateVersion7(),
        _customers = Guid.CreateVersion7(), _open = Guid.CreateVersion7(), _closed = Guid.CreateVersion7(), _group = Guid.CreateVersion7(),
        _x = Guid.CreateVersion7(), _y = Guid.CreateVersion7();
    private readonly string _ownerSub = "rpt-owner-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public AccountingReportTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
            Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مالک')", _owner, ApiFactory.Issuer, _ownerSub));
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'گزارش مالی', 'general')", _tenant);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _tenant, _owner);
            await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, gen_random_uuid(), $2, 'TenantOwner', 'Tenant')", _tenant, _owner);
            await Exec(s, ct, "INSERT INTO acc.account (tenant_id, id, code, name, account_type, is_system, requires_detail, is_postable) " +
                              "VALUES ($1, $2, '1', 'دارایی‌ها', 'Asset', true, false, false)", _tenant, _group);
            await Exec(s, ct, "INSERT INTO acc.account (tenant_id, id, code, name, account_type, is_system, requires_detail, parent_id) VALUES " +
                              "($1, $2, '1101', 'صندوق', 'Asset', true, false, $5), ($1, $3, '4101', 'فروش', 'Revenue', true, false, NULL), " +
                              "($1, $4, '1201', 'دریافتنی', 'Asset', false, true, $5)",
                _tenant, _cash, _sales, _customers, _group);
            return await Exec(s, ct, "INSERT INTO acc.fiscal_period (tenant_id, id, fiscal_year, period_no, span, status) VALUES " +
                                     "($1, $2, 1405, 8, tstzrange(now() - interval '40 days', now() - interval '10 days'), 'Closed'), " +
                                     "($1, $3, 1405, 9, tstzrange(now() - interval '10 days', now() + interval '20 days'), 'Open')",
                _tenant, _closed, _open);
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

    private HttpClient Owner()
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(_ownerSub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
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

    private static string R(long v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<Guid> Post(DateTimeOffset at, params (Guid Account, long Debit, long Credit, Guid? Detail)[] lines)
    {
        HttpResponseMessage drafted = await Idem(Owner(), "/api/v1/accounting/journals", new
        {
            effectiveAt = at, description = "آزمون گزارش",
            lines = lines.Select(l => new
            {
                accountId = l.Account, debit = l.Debit > 0 ? R(l.Debit) : null, credit = l.Credit > 0 ? R(l.Credit) : null,
                detailType = l.Detail is null ? null : "Customer", detailId = l.Detail,
            }),
        });
        Assert.Equal(HttpStatusCode.Created, drafted.StatusCode);
        return (await Body(drafted)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> PostNow(DateTimeOffset at, params (Guid, long, long, Guid?)[] lines)
    {
        Guid id = await Post(at, lines);
        Assert.Equal(HttpStatusCode.OK, (await Idem(Owner(), $"/api/v1/accounting/journals/{id}/post", null)).StatusCode);
        return id;
    }

    private static (string, string, string, string, string, string) Row(JsonElement r) => (r.GetProperty("openingDebit").GetString()!,
        r.GetProperty("openingCredit").GetString()!, r.GetProperty("periodDebit").GetString()!, r.GetProperty("periodCredit").GetString()!,
        r.GetProperty("closingDebit").GetString()!, r.GetProperty("closingCredit").GetString()!);

    [DbFact]
    public async Task AT107_the_trial_balance_and_ledgers_count_posted_entries_only_with_openings_groups_details_and_running_balances()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await PostNow(now.AddDays(-5), (_cash, 1000, 0, null), (_sales, 0, 1000, null));            // before the range: the opening
        await PostNow(now.AddDays(-1), (_customers, 400, 0, _x), (_sales, 0, 400, null));
        await PostNow(now.AddDays(-1), (_customers, 100, 0, _y), (_sales, 0, 100, null));
        await Post(now.AddDays(-1), (_cash, 999, 0, null), (_sales, 0, 999, null));                   // a draft: not in any report
        Guid wrong = await PostNow(now.AddHours(-1), (_cash, 50, 0, null), (_customers, 0, 50, _x));
        Assert.Equal(HttpStatusCode.Created, (await Idem(Owner(), $"/api/v1/accounting/journals/{wrong}/reverse", new { reason = "اشتباه" })).StatusCode);

        string range = $"from={Uri.EscapeDataString(now.AddDays(-2).ToString("O"))}&to={Uri.EscapeDataString(now.AddDays(1).ToString("O"))}";
        JsonElement tb = await Body(await Owner().GetAsync($"/api/v1/accounting/reports/trial-balance?{range}"));
        var rows = tb.GetProperty("items").EnumerateArray().ToDictionary(r => r.GetProperty("accountCode").GetString()!);
        Assert.Equal(new[] { "1", "1101", "1201", "4101" }, rows.Keys);
        Assert.Equal(("1000", "0", "50", "50", "1000", "0"), Row(rows["1101"]));          // the mistake and its mirror cancel out
        Assert.Equal(("0", "0", "550", "50", "500", "0"), Row(rows["1201"]));
        Assert.Equal(("0", "1000", "0", "500", "0", "1500"), Row(rows["4101"]));
        Assert.Equal(("1000", "0", "600", "100", "1500", "0"), Row(rows["1"]));            // the group sums its accounts
        Assert.False(rows["1"].GetProperty("postable").GetBoolean());
        JsonElement totals = tb.GetProperty("totals");
        Assert.Equal(("1000", "1000", "600", "600", "1500", "1500", true), (Row(totals).Item1, Row(totals).Item2, Row(totals).Item3, Row(totals).Item4, Row(totals).Item5,
            Row(totals).Item6, totals.GetProperty("balanced").GetBoolean()));

        // گردش تفصیلی: one customer on the receivable, with the running balance, page by page
        JsonElement first = await Body(await Owner().GetAsync($"/api/v1/accounting/reports/ledger/{_customers}?detailId={_x}&limit=2&{range}"));
        Assert.Equal(new[] { "400", "350" }, first.GetProperty("items").EnumerateArray().Select(l => l.GetProperty("runningBalance").GetString()));
        Assert.True(first.GetProperty("page").GetProperty("hasMore").GetBoolean());
        JsonElement rest = await Body(await Owner().GetAsync(
            $"/api/v1/accounting/reports/ledger/{_customers}?detailId={_x}&limit=2&cursor={first.GetProperty("page").GetProperty("nextCursor").GetString()}&{range}"));
        Assert.Equal("400", Assert.Single(rest.GetProperty("items").EnumerateArray()).GetProperty("runningBalance").GetString());
        Assert.Equal(("400", "0"), (rest.GetProperty("closing").GetProperty("debit").GetString(), rest.GetProperty("closing").GetProperty("credit").GetString()));

        // دفتر کل: a group's ledger carries every account under it, from its opening
        JsonElement group = await Body(await Owner().GetAsync($"/api/v1/accounting/reports/ledger/{_group}?{range}"));
        Assert.Equal(("1000", "1500"), (group.GetProperty("opening").GetProperty("debit").GetString(), group.GetProperty("closing").GetProperty("debit").GetString()));
        Assert.Equal(6, group.GetProperty("items").GetArrayLength());
        JsonElement sales = await Body(await Owner().GetAsync($"/api/v1/accounting/reports/ledger/{_sales}?{range}"));
        Assert.Equal(("1000", "1500"), (sales.GetProperty("opening").GetProperty("credit").GetString(), sales.GetProperty("closing").GetProperty("credit").GetString()));
        Assert.Equal("-1500", sales.GetProperty("items").EnumerateArray().Last().GetProperty("runningBalance").GetString());
    }

    [DbFact]
    public async Task ACC007_without_a_range_the_fiscal_year_so_far_and_a_bad_range_or_account_is_refused()
    {
        await PostNow(DateTimeOffset.UtcNow.AddDays(-3), (_cash, 70, 0, null), (_sales, 0, 70, null));
        JsonElement tb = await Body(await Owner().GetAsync("/api/v1/accounting/reports/trial-balance?includeEmpty=true"));
        Assert.True(tb.GetProperty("from").GetDateTimeOffset() < DateTimeOffset.UtcNow.AddDays(-3));
        var rows = tb.GetProperty("items").EnumerateArray().ToDictionary(r => r.GetProperty("accountCode").GetString()!);
        Assert.Equal(("0", "0", "70", "0", "70", "0"), Row(rows["1101"]));
        Assert.Equal(("0", "0", "0", "0", "0", "0"), Row(rows["1201"]));                  // shown, being asked for

        string backwards = $"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}";
        Assert.Equal(HttpStatusCode.BadRequest, (await Owner().GetAsync($"/api/v1/accounting/reports/trial-balance?{backwards}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Owner().GetAsync($"/api/v1/accounting/reports/ledger/{_cash}?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Owner().GetAsync($"/api/v1/accounting/reports/ledger/{Guid.CreateVersion7()}")).StatusCode);
    }
}
