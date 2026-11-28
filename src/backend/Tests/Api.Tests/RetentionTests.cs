using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using RetailCloud.BuildingBlocks.Api;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Platform.Infrastructure;

namespace RetailCloud.Api.Tests;

/// <summary>
/// US-173 / BR-087 / AT-007: the platform sets a floor and default per data category and may put a legal hold on a store;
/// the store keeps data longer, never shorter; the nightly job deletes past the store's retention and nothing under a hold.
/// </summary>
public sealed class RetentionTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _owner = Guid.CreateVersion7(), _cashier = Guid.CreateVersion7();
    private readonly string _ownerSub = "ret-owner-" + Guid.NewGuid(), _cashierSub = "ret-cashier-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public RetentionTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach ((Guid id, string sub) in new[] { (_owner, _ownerSub), (_cashier, _cashierSub) })
        {
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'کاربر')", id, ApiFactory.Issuer, sub));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'نگهداری آزمون', 'general')", _tenant);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2), ($1, $3)", _tenant, _owner, _cashier);
            return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES " +
                                     "($1, gen_random_uuid(), $2, 'TenantOwner', 'Tenant'), ($1, gen_random_uuid(), $3, 'Cashier', 'Tenant')", _tenant, _owner, _cashier);
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

    private HttpClient Merchant(string sub)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(sub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, _tenant.ToString());
        return c;
    }

    private HttpClient Platform(string? role = "PlatformSuperAdmin", string? acr = "mfa", string issuer = ApiFactory.PlatformIssuer,
        string audience = ApiFactory.PlatformAudience)
    {
        HttpClient c = _factory.CreateClient();
        var claims = new Dictionary<string, object> { ["realm_access"] = new Dictionary<string, object> { ["roles"] = role is null ? Array.Empty<string>() : new[] { role } } };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ApiFactory.Token("operator-1", issuer, audience, acr: acr, authTime: DateTimeOffset.UtcNow, extraClaims: claims));
        return c;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    private static async Task<JsonElement> Category(HttpClient owner, string category) =>
        (await Body(await owner.GetAsync("/api/v1/settings/retention"))).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("category").GetString() == category);

    private async Task<Guid> SeedNotificationAsync(int ageDays)
    {
        Guid id = Guid.CreateVersion7();
        await using var ds = NpgsqlDataSource.Create(Cs);
        await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), (s, ct) =>
            Exec(s, ct, "INSERT INTO notif.notification (tenant_id, id, purpose, channel, recipient_ref, template_code, template_version, status, created_at) " +
                        "VALUES ($1, $2, 'Internal', 'Sms', 'u1', 'x-code', 1, 'Failed', now() - make_interval(days => $3))", _tenant, id, ageDays));
        return id;
    }

    private async Task<bool> ExistsAsync(Guid notification)
    {
        await using var ds = NpgsqlDataSource.Create(Cs);
        return await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT count(*) FROM notif.notification WHERE id = $1");
            cmd.Parameters.Add(new NpgsqlParameter { Value = notification });
            return (long)(await cmd.ExecuteScalarAsync(ct))! == 1;
        });
    }

    /// <summary>The nightly job, as rc_worker, over every store (it only touches what is past its retention).</summary>
    private static async Task RunJobAsync()
    {
        await using var worker = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(Cs) { Username = "rc_worker_test", Password = "rc_worker_test" }.ToString());
        await new RetentionJob(new PostgresUnitOfWork(worker), TimeProvider.System).RunAsync(CancellationToken.None);
    }

    [DbFact]
    public async Task AT007_a_store_keeps_data_longer_than_the_default_but_never_shorter_than_the_floor()
    {
        HttpClient owner = Merchant(_ownerSub);
        JsonElement start = await Category(owner, "Notifications");
        Assert.Equal((90, 180, 180, false), (start.GetProperty("minimumDays").GetInt32(), start.GetProperty("defaultDays").GetInt32(),
            start.GetProperty("effectiveDays").GetInt32(), start.GetProperty("legalHold").GetBoolean()));
        Assert.Equal(8, (await Body(await owner.GetAsync("/api/v1/settings/retention"))).GetProperty("items").GetArrayLength());

        HttpResponseMessage shorter = await owner.PutAsJsonAsync("/api/v1/settings/retention/Notifications", new { days = 60 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, shorter.StatusCode);
        Assert.Equal("below-retention-floor", (await Body(shorter)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/settings/retention/Notifications", new { days = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync("/api/v1/settings/retention/Passwords", new { days = 400 })).StatusCode);

        HttpResponseMessage longer = await owner.PutAsJsonAsync("/api/v1/settings/retention/Notifications", new { days = 365 });
        Assert.Equal(HttpStatusCode.OK, longer.StatusCode);
        JsonElement set = await Body(longer);
        Assert.Equal((365, 365), (set.GetProperty("overrideDays").GetInt32(), set.GetProperty("effectiveDays").GetInt32()));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("/api/v1/settings/retention/Notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync("/api/v1/settings/retention/Notifications")).StatusCode);
        Assert.Equal(180, (await Category(owner, "Notifications")).GetProperty("effectiveDays").GetInt32());

        Assert.Equal(HttpStatusCode.Forbidden, (await Merchant(_cashierSub).GetAsync("/api/v1/settings/retention")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Merchant(_cashierSub).PutAsJsonAsync("/api/v1/settings/retention/Notifications", new { days = 400 })).StatusCode);
    }

    [DbFact]
    public async Task AT007_the_job_deletes_past_the_retention_only_and_a_legal_hold_stops_it()
    {
        HttpClient owner = Merchant(_ownerSub), platform = Platform();
        Guid old = await SeedNotificationAsync(400), recent = await SeedNotificationAsync(10), middle = await SeedNotificationAsync(300);

        // the store keeps notifications for 365 days: 300 stays, 400 goes
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/settings/retention/Notifications", new { days = 365 })).StatusCode);
        await RunJobAsync();
        Assert.False(await ExistsAsync(old));
        Assert.True(await ExistsAsync(middle) && await ExistsAsync(recent));
        JsonElement run = (await Body(await owner.GetAsync("/api/v1/settings/retention/runs"))).GetProperty("items")[0];
        Assert.Equal(("Notifications", "Purged", 1), (run.GetProperty("category").GetString(), run.GetProperty("outcome").GetString(), run.GetProperty("deletedRows").GetInt32()));

        // back to the 180-day default: 300 is due, but a legal hold stops it, and the run says so
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("/api/v1/settings/retention/Notifications")).StatusCode);
        HttpResponseMessage hold = await platform.PutAsJsonAsync($"/api/v1/platform/tenants/{_tenant}/legal-holds/Notifications", new { reason = "قرار دادگاه (آزمون)" });
        Assert.Equal(HttpStatusCode.OK, hold.StatusCode);
        Assert.True((await Category(owner, "Notifications")).GetProperty("legalHold").GetBoolean());
        await RunJobAsync();
        Assert.True(await ExistsAsync(middle));
        Assert.Equal("Held", (await Body(await owner.GetAsync("/api/v1/settings/retention/runs"))).GetProperty("items")[0].GetProperty("outcome").GetString());

        // lifted: the next run deletes it
        Assert.Equal(HttpStatusCode.NoContent, (await platform.DeleteAsync($"/api/v1/platform/tenants/{_tenant}/legal-holds/Notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.DeleteAsync($"/api/v1/platform/tenants/{_tenant}/legal-holds/Notifications")).StatusCode);
        await RunJobAsync();
        Assert.False(await ExistsAsync(middle));
        Assert.True(await ExistsAsync(recent));
    }

    [DbFact]
    public async Task BR087_the_platform_publishes_policy_versions_and_a_hold_needs_a_known_store_and_a_future_end()
    {
        HttpClient platform = Platform();
        JsonElement before = (await Body(await platform.GetAsync("/api/v1/platform/retention-policies"))).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("category").GetString() == "TechnicalLogs");

        // a default under the minimum, an unknown category and a missing reason are refused
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync("/api/v1/platform/retention-policies",
            new { category = "TechnicalLogs", minimumDays = 60, defaultDays = 30, reason = "آزمون" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync("/api/v1/platform/retention-policies",
            new { category = "Passwords", minimumDays = 60, defaultDays = 90, reason = "آزمون" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync("/api/v1/platform/retention-policies",
            new { category = "TechnicalLogs", minimumDays = 60, defaultDays = 90, reason = "" })).StatusCode);

        // scheduled for the future: the next version exists, the one in force does not change yet
        HttpResponseMessage published = await platform.PutAsJsonAsync("/api/v1/platform/retention-policies",
            new { category = "TechnicalLogs", minimumDays = 45, defaultDays = 120, validFrom = DateTimeOffset.UtcNow.AddDays(60), reason = "افزایش کف (آزمون)" });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        Assert.True((await Body(published)).GetProperty("version").GetInt32() > before.GetProperty("version").GetInt32());
        JsonElement after = (await Body(await platform.GetAsync("/api/v1/platform/retention-policies"))).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("category").GetString() == "TechnicalLogs");
        Assert.Equal(before.GetProperty("version").GetInt32(), after.GetProperty("version").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await platform.PutAsJsonAsync($"/api/v1/platform/tenants/{Guid.NewGuid()}/legal-holds/Audit", new { reason = "قرار" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PutAsJsonAsync($"/api/v1/platform/tenants/{_tenant}/legal-holds/Audit",
            new { reason = "قرار", until = DateTimeOffset.UtcNow.AddDays(-1) })).StatusCode);

        // only the platform realm: a store's owner and a support operator are refused
        Assert.Equal(HttpStatusCode.Unauthorized, (await Merchant(_ownerSub).GetAsync("/api/v1/platform/retention-policies")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Platform("PlatformSupport").PutAsJsonAsync("/api/v1/platform/retention-policies",
            new { category = "Audit", minimumDays = 1, defaultDays = 1, reason = "نباید" })).StatusCode);
    }
}
