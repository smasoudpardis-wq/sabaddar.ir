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

/// <summary>US-016 / TEN-005 / AT-006 / SEC-T-066: requesting an export and getting its short-lived link.</summary>
public sealed class ExportTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _owner = Guid.CreateVersion7(), _auditor = Guid.CreateVersion7();
    private readonly string _ownerSub = "exp-owner-" + Guid.NewGuid(), _auditorSub = "exp-auditor-" + Guid.NewGuid(), _cashierSub = "exp-cashier-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public ExportTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        Guid cashier = Guid.CreateVersion7();
        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        await uow.ExecuteWithoutTenantAsync(null, async (s, ct) =>
        {
            foreach ((Guid id, string sub) in new[] { (_owner, _ownerSub), (_auditor, _auditorSub), (cashier, _cashierSub) })
            {
                await Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'کاربر')", id, ApiFactory.Issuer, sub);
            }

            return 0;
        });
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'خروجی', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک')", _tenant, _b1);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2), ($1, $3), ($1, $4)", _tenant, _owner, _auditor, cashier);
            await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, $2, $3, 'Auditor', 'Tenant')",
                _tenant, Guid.CreateVersion7(), _auditor);
            await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES ($1, $2, $3, 'Cashier', 'Branch', $4)",
                _tenant, Guid.CreateVersion7(), cashier, _b1);
            return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, $2, $3, 'TenantOwner', 'Tenant')",
                _tenant, Guid.CreateVersion7(), _owner);
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

    private HttpClient As(string sub, Guid tenant, DateTimeOffset? authTime = null)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ApiFactory.Token(sub, acr: "mfa", authTime: authTime ?? DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, tenant.ToString());
        return c;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    private static async Task<string> Code(HttpResponseMessage r) => (await Body(r)).GetProperty("code").GetString()!;

    /// <summary>What the Workers do on completion (tested in DataExportTests), done here as the platform.</summary>
    private static async Task Complete(Guid export, Guid tenant, TimeSpan expiresIn)
    {
        string admin = new NpgsqlConnectionStringBuilder(Cs) { Username = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres",
            Password = Environment.GetEnvironmentVariable("PGPASSWORD") }.ToString();
        await using var ds = NpgsqlDataSource.Create(admin);
        await using NpgsqlCommand c = ds.CreateCommand(
            "UPDATE platform.export_job SET status = 'Completed', object_key = $2, row_count = 3, completed_at = now(), expires_at = now() + $3 WHERE id = $1");
        c.Parameters.Add(new NpgsqlParameter { Value = export });
        c.Parameters.Add(new NpgsqlParameter { Value = ObjectKeys.NewExportKey(tenant) });
        c.Parameters.Add(new NpgsqlParameter { Value = expiresIn });
        await c.ExecuteNonQueryAsync();
    }

    private async Task<long> AuditCount(string action)
    {
        await using var ds = NpgsqlDataSource.Create(Cs);
        return await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)s).Command("SELECT count(*) FROM audit.audit_record WHERE action = $1");
            c.Parameters.Add(new NpgsqlParameter { Value = action });
            return (long)(await c.ExecuteScalarAsync(ct))!;
        });
    }

    [DbFact]
    public async Task Request_then_link_each_audited_and_one_export_at_a_time()
    {
        HttpClient owner = As(_ownerSub, _tenant);
        HttpResponseMessage created = await owner.PostAsJsonAsync("/api/v1/exports", new { categories = new[] { "Products", "Sales" } });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        JsonElement job = await Body(created);
        Guid id = job.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/exports/{id}", created.Headers.Location!.OriginalString);
        Assert.Equal("Queued", job.GetProperty("status").GetString());
        Assert.Equal(1, await AuditCount("platform.export.requested"));

        Assert.Equal("export-in-progress", await Code(await owner.PostAsJsonAsync("/api/v1/exports", new { categories = new[] { "Journals" } })));

        JsonElement queued = await Body(await owner.GetAsync($"/api/v1/exports/{id}"));
        Assert.Equal(JsonValueKind.Null, queued.GetProperty("downloadUrl").ValueKind);

        await Complete(id, _tenant, TimeSpan.FromHours(24));
        JsonElement done = await Body(await As(_auditorSub, _tenant).GetAsync($"/api/v1/exports/{id}"));   // the Auditor may export too
        Assert.Equal("Completed", done.GetProperty("status").GetString());
        string url = done.GetProperty("downloadUrl").GetString()!;
        Assert.StartsWith($"https://storage.test/tenants/{_tenant}/exports/", url, StringComparison.Ordinal);
        Assert.Contains("expires=300", url, StringComparison.Ordinal);                                 // D-49: five minutes
        Assert.InRange(done.GetProperty("downloadUrlExpiresAt").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddMinutes(4), DateTimeOffset.UtcNow.AddMinutes(6));
        Assert.Equal(1, await AuditCount("platform.export.link-issued"));

        // the next export is allowed once this one finished
        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsJsonAsync("/api/v1/exports", new { categories = new[] { "Journals" } })).StatusCode);
    }

    [DbFact]
    public async Task An_expired_export_gives_no_link()
    {
        HttpClient owner = As(_ownerSub, _tenant);
        Guid id = (await Body(await owner.PostAsJsonAsync("/api/v1/exports", new { categories = new[] { "Products" } }))).GetProperty("id").GetGuid();
        await Complete(id, _tenant, TimeSpan.FromSeconds(-1));
        JsonElement e = await Body(await owner.GetAsync($"/api/v1/exports/{id}"));
        Assert.Equal("Expired", e.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, e.GetProperty("downloadUrl").ValueKind);
        Assert.Equal(0, await AuditCount("platform.export.link-issued"));
    }

    [DbFact]
    public async Task Who_may_export_and_what_is_refused()
    {
        Assert.Equal("forbidden", await Code(await As(_cashierSub, _tenant).PostAsJsonAsync("/api/v1/exports", new { categories = new[] { "Products" } })));
        // ABAC-05: an export needs a second factor from the last 15 minutes
        HttpResponseMessage stale = await As(_ownerSub, _tenant, DateTimeOffset.UtcNow.AddMinutes(-30)).PostAsJsonAsync("/api/v1/exports", new { categories = new[] { "Products" } });
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        // the Auditor has data.export but not customers.pii.view; the owner has both, but decryption is not available yet
        Assert.Equal("forbidden", await Code(await As(_auditorSub, _tenant).PostAsJsonAsync("/api/v1/exports", new { categories = new[] { "Customers" }, includeFullPhone = true })));
        HttpResponseMessage phone = await As(_ownerSub, _tenant).PostAsJsonAsync("/api/v1/exports", new { categories = new[] { "Customers" }, includeFullPhone = true });
        Assert.Equal("not-yet-supported", (await Body(phone)).GetProperty("errors").GetProperty("includeFullPhone")[0].GetString());
        foreach (object bad in new object[]
                 {
                     new { categories = Array.Empty<string>() }, new { categories = new[] { "Passwords" } }, new { categories = new[] { "Sales", "Sales" } },
                     new { categories = new[] { "Sales" }, from = "2026-02-01T00:00:00Z", to = "2026-01-01T00:00:00Z" },
                 })
        {
            Assert.Equal("validation", await Code(await As(_ownerSub, _tenant).PostAsJsonAsync("/api/v1/exports", bad)));
        }
    }

    [DbFact]
    public async Task Another_stores_export_is_not_found_and_a_read_only_store_can_still_export()
    {
        HttpClient owner = As(_ownerSub, _tenant);
        Guid id = (await Body(await owner.PostAsJsonAsync("/api/v1/exports", new { categories = new[] { "Products" } }))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/exports/{Guid.CreateVersion7()}")).StatusCode);

        // an owner of a second store cannot read this export through their own store (RLS, 404 like "unknown")
        Guid other = Guid.CreateVersion7();
        await using (var ds = NpgsqlDataSource.Create(Cs))
        {
            await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(other)), async (s, ct) =>
            {
                await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'دیگر', 'general')", other);
                await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", other, _owner);
                return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, $2, $3, 'TenantOwner', 'Tenant')",
                    other, Guid.CreateVersion7(), _owner);
            });
        }

        Assert.Equal(HttpStatusCode.NotFound, (await As(_ownerSub, other).GetAsync($"/api/v1/exports/{id}")).StatusCode);

        await Complete(id, _tenant, TimeSpan.FromHours(1));
        await TestPlans.SetStatusAsync(_tenant, "Suspended");
        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsJsonAsync("/api/v1/exports", new { categories = new[] { "Sales" } })).StatusCode);   // TEN-004
    }
}
