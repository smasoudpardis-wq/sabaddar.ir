using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using RetailCloud.BuildingBlocks.Api;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.TestSupport;

namespace RetailCloud.Api.Tests;

/// <summary>US-027 / IAM-008 / AT-017 / SoD-05: the store owner decides and ends platform support access.</summary>
public sealed class SupportAccessTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _owner = Guid.CreateVersion7(), _admin = Guid.CreateVersion7();
    private readonly string _ownerSub = "sup-owner-" + Guid.NewGuid(), _adminSub = "sup-admin-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public SupportAccessTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        await uow.ExecuteWithoutTenantAsync(null, async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مالک')", _owner, ApiFactory.Issuer, _ownerSub);
            return await Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مدیر')", _admin, ApiFactory.Issuer, _adminSub);
        });
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'پشتیبانی', 'general')", _tenant);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2), ($1, $3)", _tenant, _owner, _admin);
            await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, $2, $3, 'TenantAdmin', 'Tenant')", _tenant, Guid.CreateVersion7(), _admin);
            return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, $2, $3, 'TenantOwner', 'Tenant')", _tenant, Guid.CreateVersion7(), _owner);
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

    /// <summary>A request as the platform side files it, here with a chosen age (the API path is tested in AT017_...).</summary>
    private async Task<Guid> Request(int minutes = 60, TimeSpan? age = null)
    {
        Guid id = Guid.CreateVersion7();
        await using var ds = NpgsqlDataSource.Create(Cs);
        await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), (s, ct) =>
            Exec(s, ct, "INSERT INTO iam.support_access_request (tenant_id, id, requested_by, reason, duration_minutes, requested_at, expires_at) " +
                        "VALUES ($1, $2, 'https://id.test/realms/platform|support-7', 'بررسی خطای گزارش فروش', $3, now() - $4, now() - $4 + interval '24 hours')",
                _tenant, id, minutes, age ?? TimeSpan.Zero));
        return id;
    }

    private HttpClient As(string sub)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(sub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, _tenant.ToString());
        return c;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    [DbFact]
    public async Task The_owner_approves_a_window_of_exactly_the_requested_length_and_can_end_it_early()
    {
        Guid id = await Request(90);
        HttpClient owner = As(_ownerSub);
        JsonElement listed = (await Body(await owner.GetAsync("/api/v1/support-access-requests"))).EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);
        Assert.Equal("Pending", listed.GetProperty("status").GetString());
        Assert.Equal("https://id.test/realms/platform|support-7", listed.GetProperty("requestedBy").GetString());

        HttpResponseMessage approved = await owner.PostAsJsonAsync($"/api/v1/support-access-requests/{id}/decision", new { decision = "Approve", comment = "برای رفع خطا" });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        JsonElement a = await Body(approved);
        Assert.Equal("Approved", a.GetProperty("status").GetString());
        Assert.Equal(TimeSpan.FromMinutes(90), a.GetProperty("grantedUntil").GetDateTimeOffset() - a.GetProperty("decidedAt").GetDateTimeOffset());

        Assert.Equal("support-access-not-open", (await Body(await owner.PostAsJsonAsync($"/api/v1/support-access-requests/{id}/decision", new { decision = "Reject" })))
            .GetProperty("code").GetString());

        JsonElement revoked = await Body(await owner.DeleteAsync($"/api/v1/support-access-requests/{id}"));
        Assert.Equal("Revoked", revoked.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"/api/v1/support-access-requests/{id}")).StatusCode);
    }

    [DbFact]
    public async Task Only_the_owner_decides_and_old_requests_lapse()
    {
        Guid id = await Request();
        HttpClient admin = As(_adminSub);   // TenantAdmin holds tenant.settings.manage, but SoD-05 says owner only
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/support-access-requests")).StatusCode);
        Assert.Equal("owner-only", (await Body(await admin.PostAsJsonAsync($"/api/v1/support-access-requests/{id}/decision", new { decision = "Approve" })))
            .GetProperty("code").GetString());
        Assert.Equal("validation", (await Body(await As(_ownerSub).PostAsJsonAsync($"/api/v1/support-access-requests/{id}/decision", new { decision = "Maybe" })))
            .GetProperty("code").GetString());
        Assert.Equal("Rejected", (await Body(await As(_ownerSub).PostAsJsonAsync($"/api/v1/support-access-requests/{id}/decision", new { decision = "Reject" })))
            .GetProperty("status").GetString());

        Guid old = await Request(age: TimeSpan.FromHours(25));   // never decided within 24 h
        JsonElement lapsed = (await Body(await As(_ownerSub).GetAsync("/api/v1/support-access-requests"))).EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == old);
        Assert.Equal("Expired", lapsed.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await As(_ownerSub).PostAsJsonAsync($"/api/v1/support-access-requests/{old}/decision", new { decision = "Approve" })).StatusCode);
    }

    [DbFact]
    public async Task Decisions_are_audited_and_a_read_only_store_can_still_end_access()
    {
        Guid id = await Request();
        HttpClient owner = As(_ownerSub);
        await owner.PostAsJsonAsync($"/api/v1/support-access-requests/{id}/decision", new { decision = "Approve" });
        await TestPlans.SetStatusAsync(_tenant, "Suspended");
        Assert.Equal("Revoked", (await Body(await owner.DeleteAsync($"/api/v1/support-access-requests/{id}"))).GetProperty("status").GetString());

        JsonElement trail = await Body(await owner.GetAsync("/api/v1/audit?action=iam.support-access."));
        string[] actions = [.. trail.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("action").GetString()!)];
        Assert.Equal(["iam.support-access.revoked", "iam.support-access.approved"], actions);
    }

    private HttpClient Platform(string sub, string role = "PlatformSupport")
    {
        HttpClient c = _factory.CreateClient();
        var claims = new Dictionary<string, object> { ["realm_access"] = new Dictionary<string, object> { ["roles"] = new[] { role } } };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(sub, ApiFactory.PlatformIssuer, ApiFactory.PlatformAudience,
            acr: "mfa", authTime: DateTimeOffset.UtcNow, extraClaims: claims));
        return c;
    }

    [DbFact]
    public async Task AT017_support_asks_the_owner_approves_and_support_sees_a_masked_audited_view_only_while_the_grant_lasts()
    {
        HttpClient support = Platform("support-9");
        string sales = $"/api/v1/platform/support/tenants/{_tenant}/sales";
        HttpResponseMessage before = await support.GetAsync(sales);
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);
        Assert.Equal("support-access-required", (await Body(before)).GetProperty("code").GetString());

        string ask = $"/api/v1/platform/tenants/{_tenant}/support-access-requests";
        Assert.Equal(HttpStatusCode.BadRequest, (await support.PostAsJsonAsync(ask, new { reason = "کوتاه", durationMinutes = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await support.PostAsJsonAsync(ask, new { reason = "بررسی خطای گزارش فروش شعبه", durationMinutes = 300 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await support.PostAsJsonAsync($"/api/v1/platform/tenants/{Guid.CreateVersion7()}/support-access-requests",
            new { reason = "بررسی خطای گزارش فروش شعبه", durationMinutes = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Platform("admin-1", "PlatformSuperAdmin").PostAsJsonAsync(ask,
            new { reason = "بررسی خطای گزارش فروش شعبه", durationMinutes = 30 })).StatusCode);   // JIT is PlatformSupport's (D06 §6.2)
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(_ownerSub).PostAsJsonAsync(ask,
            new { reason = "بررسی خطای گزارش فروش شعبه", durationMinutes = 30 })).StatusCode);   // a merchant token is not a platform token

        HttpResponseMessage asked = await support.PostAsJsonAsync(ask, new { reason = "بررسی خطای گزارش فروش شعبه", durationMinutes = 30 });
        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);
        JsonElement request = await Body(asked);
        Assert.Equal(("Pending", "https://id.test/realms/platform|support-9"), (request.GetProperty("status").GetString(), request.GetProperty("requestedBy").GetString()));
        Guid id = request.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await support.GetAsync(sales)).StatusCode);   // pending is not granted

        Assert.Equal(HttpStatusCode.OK, (await As(_ownerSub).PostAsJsonAsync($"/api/v1/support-access-requests/{id}/decision", new { decision = "Approve" })).StatusCode);
        HttpResponseMessage view = await support.GetAsync(sales);
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        JsonElement body = await Body(view);
        Assert.Equal(JsonValueKind.String, body.GetProperty("grantedUntil").ValueKind);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("items").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await Platform("support-10").GetAsync(sales)).StatusCode);   // the grant is this operator's alone

        await using var ds = NpgsqlDataSource.Create(Cs);
        var kinds = await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(
                "SELECT action FROM audit.audit_record WHERE actor_kind = 'Support' ORDER BY seq");
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            var list = new List<string>();
            while (await r.ReadAsync(ct))
            {
                list.Add(r.GetString(0));
            }

            return list;
        });
        Assert.Equal(["support.access.requested", "support.sales.viewed"], kinds);

        Assert.Equal(HttpStatusCode.OK, (await As(_ownerSub).DeleteAsync($"/api/v1/support-access-requests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.GetAsync(sales)).StatusCode);   // ended early by the owner (AB-17)
    }
}
