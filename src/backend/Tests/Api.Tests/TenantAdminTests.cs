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

/// <summary>US-171 / TEN-004: an operator lists stores (metadata only) and suspends, reactivates or closes one, with a reason on record.</summary>
public sealed class TenantAdminTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _owner = Guid.CreateVersion7(), _cashier = Guid.CreateVersion7();
    private readonly string _ownerSub = "adm-owner-" + Guid.NewGuid(), _cashierSub = "adm-cashier-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public TenantAdminTests(ApiFactory factory) => _factory = factory;

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
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'مدیریت آزمون', 'general')", _tenant);
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

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    private HttpClient Platform(string? role = "PlatformSuperAdmin")
    {
        HttpClient c = _factory.CreateClient();
        var claims = new Dictionary<string, object> { ["realm_access"] = new Dictionary<string, object> { ["roles"] = role is null ? Array.Empty<string>() : new[] { role } } };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ApiFactory.Token("operator-1", ApiFactory.PlatformIssuer, ApiFactory.PlatformAudience, acr: "mfa", authTime: DateTimeOffset.UtcNow, extraClaims: claims));
        return c;
    }


    private async Task<JsonElement?> FindAsync(HttpClient platform)
    {
        string? cursor = null;
        do
        {
            JsonElement page = await Body(await platform.GetAsync("/api/v1/platform/tenants?limit=200" + (cursor is null ? "" : "&cursor=" + cursor)));
            foreach (JsonElement t in page.GetProperty("items").EnumerateArray())
            {
                if (t.GetProperty("id").GetGuid() == _tenant) return t;
            }

            cursor = page.GetProperty("page").GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);
        return null;
    }

    [DbFact]
    public async Task TEN004_an_operator_lists_stores_and_suspends_reactivates_and_closes_one_with_a_reason()
    {
        HttpClient platform = Platform(), owner = Merchant(_ownerSub);
        JsonElement mine = (await FindAsync(platform))!.Value;
        Assert.Equal(("مدیریت آزمون", "Trial"), (mine.GetProperty("legalName").GetString(), mine.GetProperty("status").GetString()));
        Assert.Equal(JsonValueKind.Number, mine.GetProperty("branches").ValueKind);
        Assert.False(mine.TryGetProperty("tax", out _));   // metadata only

        // a page of one: the cursor leads on
        JsonElement first = await Body(await platform.GetAsync("/api/v1/platform/tenants?limit=1"));
        Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.GetAsync("/api/v1/platform/tenants?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.GetAsync("/api/v1/platform/tenants?cursor=zzz")).StatusCode);

        string url = $"/api/v1/platform/tenants/{_tenant}/status";
        HttpResponseMessage suspended = await platform.PostAsJsonAsync(url, new { status = "Suspended", reason = "عدم پرداخت (آزمون)" });
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        Assert.Equal("Suspended", (await Body(suspended)).GetProperty("status").GetString());
        // the store is read-only at once: a write is refused with the reason, reading still works
        HttpResponseMessage write = await owner.PostAsJsonAsync("/api/v1/branches", new { code = "RO1", name = "شعبه" });
        Assert.Equal("tenant-read-only", (await Body(write)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/branches")).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await platform.PostAsJsonAsync(url, new { status = "Suspended", reason = "دوباره" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await platform.PostAsJsonAsync(url, new { status = "Active", reason = "پرداخت شد (آزمون)" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/branches", new { code = "RO2", name = "شعبه ۲" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await platform.PostAsJsonAsync(url, new { status = "Closed", reason = "درخواست مشتری (آزمون)" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await platform.PostAsJsonAsync(url, new { status = "Active", reason = "بازگشایی" })).StatusCode);   // final
        Assert.Equal("Closed", (await FindAsync(platform))!.Value.GetProperty("status").GetString());
    }

    [DbFact]
    public async Task A_status_change_needs_a_known_status_a_reason_a_known_store_and_the_platform_realm()
    {
        HttpClient platform = Platform();
        string url = $"/api/v1/platform/tenants/{_tenant}/status";
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync(url, new { status = "Trial", reason = "نامعتبر" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.PostAsJsonAsync(url, new { status = "Suspended", reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await platform.PostAsJsonAsync($"/api/v1/platform/tenants/{Guid.NewGuid()}/status", new { status = "Suspended", reason = "ناشناس" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Platform("PlatformSupport").PostAsJsonAsync(url, new { status = "Suspended", reason = "نباید" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Merchant(_ownerSub).PostAsJsonAsync(url, new { status = "Suspended", reason = "مالک" })).StatusCode);   // a store cannot suspend itself
        Assert.Equal("Trial", (await FindAsync(platform))!.Value.GetProperty("status").GetString());
    }
}
