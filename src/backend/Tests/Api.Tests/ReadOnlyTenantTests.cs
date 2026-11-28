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

/// <summary>US-015 / TEN-004 / AT-005: a suspended or trial-expired store reads everything and writes only the allow-list.</summary>
public sealed class ReadOnlyTenantTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _owner = Guid.CreateVersion7(),
        _cashier = Guid.CreateVersion7(), _cashierRole = Guid.CreateVersion7();
    private readonly string _ownerSub = "ro-owner-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public ReadOnlyTenantTests(ApiFactory factory) => _factory = factory;

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
            return await Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'صندوق‌دار')",
                _cashier, ApiFactory.Issuer, "ro-cashier-" + Guid.NewGuid());
        });
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'فقط‌خواندنی', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک')", _tenant, _b1);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2), ($1, $3)", _tenant, _owner, _cashier);
            await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES ($1, $2, $3, 'Cashier', 'Branch', $4)",
                _tenant, _cashierRole, _cashier, _b1);
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

    private HttpClient As(string sub, Guid? tenant)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(sub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        if (tenant is not null)
        {
            c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, tenant.Value.ToString());
        }

        return c;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    private static async Task AssertReadOnly(HttpResponseMessage r, string reason)
    {
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        JsonElement p = await Body(r);
        Assert.Equal("tenant-read-only", p.GetProperty("code").GetString());
        Assert.Equal(reason, p.GetProperty("reason").GetString());
    }

    private static object Invite() => new
    {
        displayName = "همکار",
        roleAssignments = new[] { new { roleId = RoleCatalog.IdOf("Cashier"), scopeType = "Tenant", scopeId = (Guid?)null } },
    };

    [DbFact]
    public async Task A_suspended_store_reads_everything_but_new_writes_are_refused()
    {
        HttpClient owner = As(_ownerSub, _tenant);
        HttpResponseMessage inv = await owner.PostAsJsonAsync("/api/v1/users/invitations", Invite());   // before suspension
        string token = (await Body(inv)).GetProperty("inviteToken").GetString()!;
        await TestPlans.SetStatusAsync(_tenant, "Suspended");

        JsonElement me = await Body(await owner.GetAsync("/api/v1/tenants/me"));
        Assert.Equal("suspended", me.GetProperty("readOnlyReason").GetString());
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/branches")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/audit")).StatusCode);

        await AssertReadOnly(await owner.PostAsJsonAsync("/api/v1/branches", new { code = "B2", name = "دو" }), "suspended");
        await AssertReadOnly(await owner.PostAsJsonAsync($"/api/v1/branches/{_b1}/registers", new { code = "R1" }), "suspended");
        await AssertReadOnly(await owner.PostAsJsonAsync("/api/v1/users/invitations", Invite()), "suspended");
        await AssertReadOnly(await owner.PutAsJsonAsync("/api/v1/me/pin", new { pin = "4821" }), "suspended");
        await AssertReadOnly(await owner.PostAsync($"/api/v1/branches/{_b1}/pairing-codes", null), "suspended");
        // an invitation issued before the suspension cannot add a member now (the tenant comes from the token)
        await AssertReadOnly(await As("ro-new-" + Guid.NewGuid(), null).PostAsync($"/api/v1/invitations/{token}/accept", null), "suspended");

        // access can still be reduced, and the store can still be selected
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/users/{_cashier}/role-assignments/{_cashierRole}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await As(_ownerSub, null).PostAsJsonAsync("/api/v1/me/active-tenant", new { tenantId = _tenant })).StatusCode);

        // reactivation (a platform operation) restores writes; nothing was lost meanwhile
        await TestPlans.SetStatusAsync(_tenant, "Active");
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/v1/branches/{_b1}/registers", new { code = "R1" })).StatusCode);
        JsonElement branches = await Body(await owner.GetAsync("/api/v1/branches"));
        Assert.Equal(1, branches.GetArrayLength());
    }

    [DbFact]
    public async Task An_expired_trial_is_read_only_until_a_paid_plan()
    {
        HttpClient owner = As(_ownerSub, _tenant);
        await TestPlans.ExpireTrialAsync(_tenant);
        Assert.Equal("trial-expired", (await Body(await owner.GetAsync("/api/v1/tenants/me"))).GetProperty("readOnlyReason").GetString());
        await AssertReadOnly(await owner.PostAsJsonAsync($"/api/v1/branches/{_b1}/registers", new { code = "R1" }), "trial-expired");

        await TestPlans.SetAsync(_tenant, "Basic");   // the trial end date no longer applies to a paid plan
        Assert.Equal(JsonValueKind.Null, (await Body(await owner.GetAsync("/api/v1/tenants/me"))).GetProperty("readOnlyReason").ValueKind);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/v1/branches/{_b1}/registers", new { code = "R1" })).StatusCode);
    }
}
