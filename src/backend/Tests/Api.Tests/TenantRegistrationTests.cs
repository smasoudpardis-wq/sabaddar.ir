using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RetailCloud.Accounting.Application;
using RetailCloud.Accounting.Infrastructure;
using RetailCloud.BuildingBlocks.Api;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.IdentityAccess.Infrastructure;
using RetailCloud.Inventory.Infrastructure;

namespace RetailCloud.Api.Tests;

/// <summary>
/// US-011 / UC-10 / TEN-001 / TEN-001.1 / AT-001 / AT-290: self-service sign-up of a brand-new identity, provisioning
/// through the outbox (real relay, real consumers, rc_worker role), wizard status, idempotent double submit.
/// </summary>
public sealed class TenantRegistrationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public TenantRegistrationTests(ApiFactory factory) => _factory = factory;

    private HttpClient Client(string sub, string acr = "mfa", Guid? tenant = null, string name = "مالک تازه")
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ApiFactory.Token(sub, acr: acr, authTime: DateTimeOffset.UtcNow, extraClaims: new Dictionary<string, object> { ["name"] = name }));
        if (tenant is not null)
        {
            c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, tenant.Value.ToString());
        }

        return c;
    }

    private static Task<HttpResponseMessage> Register(HttpClient c, string key, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenants") { Content = JsonContent.Create(body) };
        req.Headers.Add(Idempotency.Header, key);
        return c.SendAsync(req);
    }

    private static object Body(string name = "فروشگاه تازه") =>
        new { legalName = name, industryCode = "supermarket", firstBranchName = "شعبه مرکزی" };

    private static async Task<string?> Code(HttpResponseMessage r)
    {
        using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return d.RootElement.GetProperty("code").GetString();
    }

    /// <summary>Runs the Workers relay for exactly this tenant with the production consumers, as rc_worker.</summary>
    private static async Task Provision(Guid tenant)
    {
        var workerCs = new NpgsqlConnectionStringBuilder(Cs) { Username = "rc_worker_test", Password = "rc_worker_test" }.ToString();
        await using var ds = NpgsqlDataSource.Create(workerCs);
        var relay = new OutboxRelay(new PostgresUnitOfWork(ds),
            [new OwnerMembershipConsumer(), new DefaultWarehouseConsumer(), new TenantAccountingSetupConsumer()],
            NullLogger<OutboxRelay>.Instance, new OutboxRelayOptions { Tenants = new HashSet<Guid> { tenant } });
        for (int i = 0; i < 5 && await relay.RunOnceAsync(CancellationToken.None) > 0; i++)
        {
        }
    }

    private static async Task<T> InTenant<T>(Guid tenant, string sql)
    {
        await using var ds = NpgsqlDataSource.Create(Cs);
        return await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)s).Command(sql);
            return (T)(await c.ExecuteScalarAsync(ct))!;
        });
    }

    [DbFact]
    public async Task A_new_identity_registers_and_every_module_provisions_the_tenant()
    {
        string sub = "signup-" + Guid.NewGuid();
        HttpResponseMessage created = await Register(Client(sub), "reg-" + Guid.NewGuid().ToString("N"), Body());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument t = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Guid tenant = t.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(("Trial", "IRR", "Asia/Tehran", "TaxExclusive"), (t.RootElement.GetProperty("status").GetString(),
            t.RootElement.GetProperty("baseCurrency").GetString(), t.RootElement.GetProperty("timeZone").GetString(),
            t.RootElement.GetProperty("pricingMode").GetString()));

        // before the relay ran, the user exists but has no membership yet (eventual, TEN-001.1)
        using (JsonDocument me0 = JsonDocument.Parse(await (await Client(sub).GetAsync("/api/v1/me")).Content.ReadAsStringAsync()))
        {
            Assert.Equal("مالک تازه", me0.RootElement.GetProperty("displayName").GetString());
            Assert.Empty(me0.RootElement.GetProperty("memberships").EnumerateArray());
        }

        await Provision(tenant);
        await Task.Delay(1100); // empty membership lists are cached for 1 s only

        using (JsonDocument me = JsonDocument.Parse(await (await Client(sub).GetAsync("/api/v1/me")).Content.ReadAsStringAsync()))
        {
            JsonElement m = Assert.Single(me.RootElement.GetProperty("memberships").EnumerateArray());
            Assert.Equal(tenant, m.GetProperty("tenantId").GetGuid());
            Assert.Equal("TenantOwner", m.GetProperty("roles")[0].GetString());
            Assert.True(m.GetProperty("mfaRequired").GetBoolean());
        }

        HttpResponseMessage setup = await Client(sub, tenant: tenant).GetAsync("/api/v1/tenants/me/setup");
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        using (JsonDocument st = JsonDocument.Parse(await setup.Content.ReadAsStringAsync()))
        {
            Assert.True(st.RootElement.GetProperty("complete").GetBoolean(), st.RootElement.ToString());
            Assert.Equal(["branch-and-register", "warehouse", "chart-of-accounts", "fiscal-periods"],
                st.RootElement.GetProperty("steps").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToArray());
        }

        // AT-001 in numbers
        int year = FiscalCalendar.FiscalYearOf(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Tehran"));
        Assert.Equal(1L, await InTenant<long>(tenant, "SELECT count(*) FROM platform.branch"));
        Assert.Equal(1L, await InTenant<long>(tenant, "SELECT count(*) FROM platform.register"));
        Assert.Equal(1L, await InTenant<long>(tenant, "SELECT count(*) FROM inv.warehouse WHERE code = 'W1'"));
        Assert.Equal((long)ChartOfAccountsTemplate.For("supermarket").Count, await InTenant<long>(tenant, "SELECT count(*) FROM acc.account"));
        Assert.Equal((long)ChartOfAccountsTemplate.Roles.Count, await InTenant<long>(tenant, "SELECT count(*) FROM acc.account_role"));
        Assert.Equal(12L, await InTenant<long>(tenant, $"SELECT count(*) FROM acc.fiscal_period WHERE fiscal_year = {year} AND status = 'Open'"));
        Assert.Equal("1101", await InTenant<string>(tenant,
            "SELECT a.code FROM acc.account_role r JOIN acc.account a ON a.tenant_id = r.tenant_id AND a.id = r.account_id WHERE r.role = 'CashOnHand'"));

        // re-delivery of the event changes nothing (inbox + ON CONFLICT)
        await InTenant<int>(tenant, "SELECT 1"); // keep the app connection warm
        await Provision(tenant);
        Assert.Equal((long)ChartOfAccountsTemplate.Roles.Count, await InTenant<long>(tenant, "SELECT count(*) FROM acc.account_role"));
    }

    [DbFact]
    public async Task Registration_requires_mfa_and_creates_nothing_without_it()
    {
        string sub = "signup-nomfa-" + Guid.NewGuid();
        string key = "reg-" + Guid.NewGuid().ToString("N");
        HttpResponseMessage r = await Register(Client(sub, acr: "pwd"), key, Body());
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode); // AT-290 / IAM-003: the owner is a sensitive role
        Assert.Equal("mfa-required", await Code(r));
        Guid would = Idempotency.DerivedTenantId(ApiFactory.Issuer, sub, key);
        Assert.Equal(0L, await InTenant<long>(would, "SELECT count(*) FROM platform.tenant"));

        // the same key works once MFA is done (failures are not stored, D-34)
        Assert.Equal(HttpStatusCode.Created, (await Register(Client(sub), key, Body())).StatusCode);
    }

    [DbFact]
    public async Task Double_submit_creates_one_tenant_and_replays_the_same_answer()
    {
        string sub = "signup-double-" + Guid.NewGuid();
        string key = "reg-" + Guid.NewGuid().ToString("N");
        HttpResponseMessage[] both = await Task.WhenAll(Register(Client(sub), key, Body()), Register(Client(sub), key, Body()));
        Assert.All(both, x => Assert.Equal(HttpStatusCode.Created, x.StatusCode));
        Assert.Equal(await both[0].Content.ReadAsStringAsync(), await both[1].Content.ReadAsStringAsync());
        Assert.Single(both, x => x.Headers.Contains(Idempotency.ReplayedHeader));

        HttpResponseMessage other = await Register(Client(sub), key, Body("نام دیگر"));
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal("idempotency-key-reused", await Code(other));

        // a different key is a deliberate second business
        Assert.Equal(HttpStatusCode.Created, (await Register(Client(sub), "reg-" + Guid.NewGuid().ToString("N"), Body("کسب‌وکار دوم"))).StatusCode);
    }

    [DbFact]
    public async Task Invalid_registration_reports_each_field()
    {
        HttpResponseMessage r = await Register(Client("signup-invalid-" + Guid.NewGuid()), "reg-" + Guid.NewGuid().ToString("N"),
            new { legalName = "x", industryCode = "casino", firstBranchName = "", timeZone = "Mars/Olympus", pricingMode = "Whatever" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal("validation", d.RootElement.GetProperty("code").GetString());
        string[] fields = [.. d.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order()];
        Assert.Equal(["firstBranchName", "industryCode", "legalName", "pricingMode", "timeZone"], fields);
    }

    [DbFact]
    public async Task Unprovisioned_identities_can_only_register_and_nothing_else()
    {
        HttpResponseMessage me = await Client("nobody-" + Guid.NewGuid()).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Forbidden, me.StatusCode);
        Assert.Equal("user-not-provisioned", await Code(me));
    }
}
