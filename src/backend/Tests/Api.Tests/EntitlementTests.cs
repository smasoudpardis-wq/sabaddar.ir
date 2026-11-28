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

/// <summary>US-014 / TEN-003 / AT-004: plan caps are enforced on the server and answered with 403 entitlement-exceeded.</summary>
public sealed class EntitlementTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _owner = Guid.CreateVersion7();
    private readonly string _ownerSub = "ent-owner-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public EntitlementTests(ApiFactory factory) => _factory = factory;

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
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'پلن', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک')", _tenant, _b1);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _tenant, _owner);
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

    private static async Task AssertExceeded(HttpResponseMessage r, string resource, int limit)
    {
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        JsonElement p = await Body(r);
        Assert.Equal("entitlement-exceeded", p.GetProperty("code").GetString());
        Assert.Equal(resource, p.GetProperty("resource").GetString());
        Assert.Equal(limit, p.GetProperty("limit").GetInt32());
    }

    private static object Invite(string name) => new
    {
        displayName = name,
        roleAssignments = new[] { new { roleId = RoleCatalog.IdOf("Cashier"), scopeType = "Tenant", scopeId = (Guid?)null } },
    };

    [DbFact]
    public async Task A_new_tenant_is_on_the_trial_plan_and_sees_its_caps()
    {
        JsonElement t = await Body(await As(_ownerSub, _tenant).GetAsync("/api/v1/tenants/me"));
        Assert.Equal("Trial", t.GetProperty("plan").GetString());
        DateTimeOffset ends = t.GetProperty("trialEndsAt").GetDateTimeOffset();
        Assert.InRange(ends, DateTimeOffset.UtcNow.AddDays(13), DateTimeOffset.UtcNow.AddDays(15));
        Assert.Equal(2, t.GetProperty("entitlements").GetProperty("maxBranches").GetInt32());
        await TestPlans.SetAsync(_tenant, "Enterprise");
        JsonElement e = await Body(await As(_ownerSub, _tenant).GetAsync("/api/v1/tenants/me"));
        Assert.Equal(JsonValueKind.Null, e.GetProperty("entitlements").GetProperty("maxBranches").ValueKind);
    }

    [DbFact]
    public async Task Branches_and_registers_beyond_the_plan_are_refused()
    {
        await TestPlans.SetAsync(_tenant, "Basic");   // 1 branch, 2 registers, 3 users
        HttpClient owner = As(_ownerSub, _tenant);
        await AssertExceeded(await owner.PostAsJsonAsync("/api/v1/branches", new { code = "B2", name = "دو" }), "branches", 1);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/v1/branches/{_b1}/registers", new { code = "R1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/v1/branches/{_b1}/registers", new { code = "R2" })).StatusCode);
        await AssertExceeded(await owner.PostAsJsonAsync($"/api/v1/branches/{_b1}/registers", new { code = "R3" }), "registers", 2);
        // upgrading lifts the cap immediately
        await TestPlans.SetAsync(_tenant, "Pro");
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/branches", new { code = "B2", name = "دو" })).StatusCode);
    }

    [DbFact]
    public async Task Concurrent_requests_cannot_both_take_the_last_slot()
    {
        // trial: 2 branches, 1 used -> of 6 simultaneous creations exactly one succeeds
        HttpResponseMessage[] rs = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            As(_ownerSub, _tenant).PostAsJsonAsync("/api/v1/branches", new { code = $"P{i}", name = $"هم‌زمان {i}" })));
        Assert.Equal(1, rs.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, rs.Count(r => r.StatusCode == HttpStatusCode.Forbidden));
    }

    [DbFact]
    public async Task Seats_count_members_and_open_invitations_and_acceptance_is_capped_too()
    {
        await TestPlans.SetAsync(_tenant, "Basic");   // 3 users: the owner + 2 invitations fill it
        HttpClient owner = As(_ownerSub, _tenant);
        string[] tokens = new string[2];
        for (int i = 0; i < 2; i++)
        {
            HttpResponseMessage r = await owner.PostAsJsonAsync("/api/v1/users/invitations", Invite($"همکار {i}"));
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            tokens[i] = (await Body(r)).GetProperty("inviteToken").GetString()!;
        }

        await AssertExceeded(await owner.PostAsJsonAsync("/api/v1/users/invitations", Invite("اضافه")), "users", 3);

        // a seat taken another way (here: directly) means the last invitation can no longer be accepted
        Guid extra = Guid.CreateVersion7();
        string adminCs = new NpgsqlConnectionStringBuilder(Cs) { Username = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres",
            Password = Environment.GetEnvironmentVariable("PGPASSWORD") }.ToString();
        await using (var admin = NpgsqlDataSource.Create(adminCs))
        {
            await using NpgsqlBatch b = admin.CreateBatch();
            b.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, 'https://other', $2, 'x')")
            {
                Parameters = { new NpgsqlParameter { Value = extra }, new NpgsqlParameter { Value = "extra-" + extra } },
            });
            b.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)")
            {
                Parameters = { new NpgsqlParameter { Value = _tenant }, new NpgsqlParameter { Value = extra } },
            });
            await b.ExecuteNonQueryAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await As("ent-a-" + Guid.NewGuid(), null).PostAsync($"/api/v1/invitations/{tokens[0]}/accept", null)).StatusCode);
        await AssertExceeded(await As("ent-b-" + Guid.NewGuid(), null).PostAsync($"/api/v1/invitations/{tokens[1]}/accept", null), "users", 3);
    }
}
