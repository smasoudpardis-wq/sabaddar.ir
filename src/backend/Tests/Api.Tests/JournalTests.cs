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

/// <summary>US-111 / ACC-001..004, ACC-009 / AT-100..104: chart, mappings, drafts, posting and reversal.</summary>
public sealed class JournalTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _owner = Guid.CreateVersion7(), _cash = Guid.CreateVersion7(), _sales = Guid.CreateVersion7(),
        _customers = Guid.CreateVersion7(), _open = Guid.CreateVersion7(), _closed = Guid.CreateVersion7();
    private readonly string _ownerSub = "acc-owner-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public JournalTests(ApiFactory factory) => _factory = factory;

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
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'حسابداری', 'general')", _tenant);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _tenant, _owner);
            await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, gen_random_uuid(), $2, 'TenantOwner', 'Tenant')", _tenant, _owner);
            await Exec(s, ct, "INSERT INTO acc.account (tenant_id, id, code, name, account_type, is_system, requires_detail) VALUES " +
                              "($1, $2, '1101', 'صندوق', 'Asset', true, false), ($1, $3, '4101', 'فروش', 'Revenue', true, false), ($1, $4, '1201', 'دریافتنی', 'Asset', false, true)",
                _tenant, _cash, _sales, _customers);
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

    private object Sale(long amount, DateTimeOffset? at = null) => new
    {
        effectiveAt = at ?? DateTimeOffset.UtcNow, description = "فروش نقدی",
        lines = new object[] { new { accountId = _cash, debit = amount.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                               new { accountId = _sales, credit = amount.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
    };

    [DbFact]
    public async Task A_draft_is_posted_once_and_corrected_only_by_its_mirror()
    {
        HttpClient owner = Owner();
        HttpResponseMessage drafted = await Idem(owner, "/api/v1/accounting/journals", Sale(1_100_000));
        Assert.Equal(HttpStatusCode.Created, drafted.StatusCode);
        Guid id = (await Body(drafted)).GetProperty("id").GetGuid();

        JsonElement posted = await Body(await Idem(owner, $"/api/v1/accounting/journals/{id}/post", null));
        Assert.Equal("Posted", posted.GetProperty("status").GetString());
        Assert.Equal("journal-not-draft", await Code(await Idem(owner, $"/api/v1/accounting/journals/{id}/post", null)));

        JsonElement mirror = await Body(await Idem(owner, $"/api/v1/accounting/journals/{id}/reverse", new { reason = "ثبت اشتباه" }));
        Assert.Equal(id, mirror.GetProperty("reversalOf").GetGuid());
        JsonElement[] lines = [.. mirror.GetProperty("lines").EnumerateArray()];
        Assert.Equal(("0", "1100000"), (lines[0].GetProperty("debit").GetString(), lines[0].GetProperty("credit").GetString()));   // debit became credit
        Assert.Equal("journal-already-reversed", await Code(await Idem(owner, $"/api/v1/accounting/journals/{id}/reverse", new { reason = "دوباره" })));

        JsonElement listed = await Body(await owner.GetAsync("/api/v1/accounting/journals?limit=1"));
        Assert.True(listed.GetProperty("page").GetProperty("hasMore").GetBoolean());
        string cursor = listed.GetProperty("page").GetProperty("nextCursor").GetString()!;
        JsonElement second = await Body(await owner.GetAsync($"/api/v1/accounting/journals?limit=1&cursor={cursor}"));
        Assert.Equal(id, second.GetProperty("items")[0].GetProperty("id").GetGuid());    // newest first: the mirror, then the original
        Assert.Equal("Reversed", second.GetProperty("items")[0].GetProperty("status").GetString());
    }

    [DbFact]
    public async Task Unbalanced_detail_less_and_closed_period_entries_are_refused()
    {
        HttpClient owner = Owner();
        var unbalanced = new { effectiveAt = DateTimeOffset.UtcNow, description = "نامتوازن",
            lines = new object[] { new { accountId = _cash, debit = "1000" }, new { accountId = _sales, credit = "900" } } };
        Assert.Contains("unbalanced", (await Body(await Idem(owner, "/api/v1/accounting/journals", unbalanced))).GetProperty("errors").GetProperty("lines")[0].GetString(),
            StringComparison.Ordinal);

        var noDetail = new { effectiveAt = DateTimeOffset.UtcNow, description = "فروش نسیه",
            lines = new object[] { new { accountId = _customers, debit = "500" }, new { accountId = _sales, credit = "500" } } };
        Assert.Contains("requires a detail", (await Body(await Idem(owner, "/api/v1/accounting/journals", noDetail))).GetProperty("errors").GetProperty("lines")[0].GetString(),
            StringComparison.Ordinal);

        // a draft dated in the closed month cannot be posted (DI-042); a date outside every period has no period at all
        Guid old = (await Body(await Idem(owner, "/api/v1/accounting/journals", Sale(200, DateTimeOffset.UtcNow.AddDays(-20))))).GetProperty("id").GetGuid();
        Assert.Equal("period-closed", await Code(await Idem(owner, $"/api/v1/accounting/journals/{old}/post", null)));
        Assert.Equal("no-open-period", await Code(await Idem(owner, "/api/v1/accounting/journals", Sale(200, DateTimeOffset.UtcNow.AddYears(-3)))));
    }

    [DbFact]
    public async Task The_chart_and_the_role_mappings()
    {
        HttpClient owner = Owner();
        HttpResponseMessage created = await owner.PostAsJsonAsync("/api/v1/accounting/accounts", new { code = "1102", name = "تنخواه", accountType = "Asset" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Guid petty = (await Body(created)).GetProperty("id").GetGuid();
        Assert.Equal("duplicate-code", await Code(await owner.PostAsJsonAsync("/api/v1/accounting/accounts", new { code = "1102", name = "تکراری", accountType = "Asset" })));
        Assert.Equal("validation", await Code(await owner.PostAsJsonAsync("/api/v1/accounting/accounts", new { code = "1103", name = "بد", accountType = "Asset", parentId = _sales })));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/api/v1/accounting/account-mappings",
            new { mappings = new[] { new { role = "CashOnHand", accountId = petty } } })).StatusCode);
        Assert.Equal("validation", await Code(await owner.PutAsJsonAsync("/api/v1/accounting/account-mappings",
            new { mappings = new[] { new { role = "Salaries", accountId = petty } } })));

        JsonElement[] accounts = [.. (await Body(await owner.GetAsync("/api/v1/accounting/accounts"))).EnumerateArray()];
        Assert.Equal(["1101", "1102", "1201", "4101"], accounts.Select(a => a.GetProperty("code").GetString()));
        Assert.Equal(2, (await Body(await owner.GetAsync("/api/v1/accounting/periods"))).GetArrayLength());
    }
}
