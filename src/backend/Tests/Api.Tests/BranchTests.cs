using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RetailCloud.BuildingBlocks.Api;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Inventory.Infrastructure;

namespace RetailCloud.Api.Tests;

/// <summary>US-012 / TEN-002 / AT-002: more branches and registers; every branch gets a register and a warehouse.</summary>
public sealed class BranchTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7();
    private readonly string _ownerSub = "br-owner-" + Guid.NewGuid(), _managerSub = "br-manager-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public BranchTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        Guid owner = Guid.CreateVersion7(), manager = Guid.CreateVersion7();
        await uow.ExecuteWithoutTenantAsync(null, async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مالک')", owner, ApiFactory.Issuer, _ownerSub);
            return await Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مدیر')", manager, ApiFactory.Issuer, _managerSub);
        });
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'چندشعبه', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'مرکزی')", _tenant, _b1);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2), ($1, $3)", _tenant, owner, manager);
            await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, $2, $3, 'TenantOwner', 'Tenant')", _tenant, Guid.CreateVersion7(), owner);
            return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES ($1, $2, $3, 'BranchManager', 'Branch', $4)",
                _tenant, Guid.CreateVersion7(), manager, _b1);
        });
        await RetailCloud.TestSupport.TestPlans.SetAsync(_tenant, "Enterprise");   // several branches per test; caps are tested in EntitlementTests
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

    private HttpClient As(string sub, string acr = "mfa")
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(sub, acr: acr, authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, _tenant.ToString());
        return c;
    }

    private static async Task<string?> Code(HttpResponseMessage r)
    {
        using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return d.RootElement.GetProperty("code").GetString();
    }

    private async Task<T> InTenant<T>(string sql)
    {
        await using var ds = NpgsqlDataSource.Create(Cs);
        return await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)s).Command(sql);
            return (T)(await c.ExecuteScalarAsync(ct))!;
        });
    }

    [DbFact]
    public async Task A_new_branch_gets_register_R1_now_and_its_warehouse_from_the_event()
    {
        HttpResponseMessage r = await As(_ownerSub).PostAsJsonAsync("/api/v1/branches", new { code = "B2", name = "شعبه دو" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        using JsonDocument b = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Guid branch = b.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(1L, await InTenant<long>($"SELECT count(*) FROM platform.register WHERE branch_id = '{branch}' AND code = 'R1'"));

        var workerCs = new NpgsqlConnectionStringBuilder(Cs) { Username = "rc_worker_test", Password = "rc_worker_test" }.ToString();
        await using (var ds = NpgsqlDataSource.Create(workerCs))
        {
            await new OutboxRelay(new PostgresUnitOfWork(ds), [new DefaultWarehouseConsumer()], NullLogger<OutboxRelay>.Instance,
                new OutboxRelayOptions { Tenants = new HashSet<Guid> { _tenant } }).RunOnceAsync(CancellationToken.None);
        }

        Assert.Equal("W-B2", await InTenant<string>($"SELECT code FROM inv.warehouse WHERE branch_id = '{branch}'"));

        using JsonDocument list = JsonDocument.Parse(await (await As(_ownerSub).GetAsync("/api/v1/branches")).Content.ReadAsStringAsync());
        Assert.Equal(["B1", "B2"], list.RootElement.EnumerateArray().Select(x => x.GetProperty("code").GetString()!).Order().ToArray());

        // the branch manager of B1 does not see B2 (TEN-002: users reach branches through their scope)
        using JsonDocument mine = JsonDocument.Parse(await (await As(_managerSub, "pwd").GetAsync("/api/v1/branches")).Content.ReadAsStringAsync());
        Assert.Equal(["B1"], mine.RootElement.EnumerateArray().Select(x => x.GetProperty("code").GetString()!).ToArray());
    }

    [DbFact]
    public async Task Codes_are_unique_and_validated()
    {
        HttpClient owner = As(_ownerSub);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/branches", new { code = "B9", name = "نه" })).StatusCode);
        Assert.Equal("duplicate-code", await Code(await owner.PostAsJsonAsync("/api/v1/branches", new { code = "B9", name = "تکراری" })));
        Assert.Equal("validation", await Code(await owner.PostAsJsonAsync("/api/v1/branches", new { code = "b 9", name = "بد" })));
        Assert.Equal("validation", await Code(await owner.PostAsJsonAsync("/api/v1/branches", new { code = "B10", name = "" })));

        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/v1/branches/{_b1}/registers", new { code = "R2" })).StatusCode);
        Assert.Equal("duplicate-code", await Code(await owner.PostAsJsonAsync($"/api/v1/branches/{_b1}/registers", new { code = "R2" })));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/v1/branches/{Guid.NewGuid()}/registers", new { code = "R1" })).StatusCode);
    }

    [DbFact]
    public async Task Only_tenant_settings_managers_create_branches_and_registers()
    {
        HttpClient manager = As(_managerSub, "pwd");
        Assert.Equal("forbidden", await Code(await manager.PostAsJsonAsync("/api/v1/branches", new { code = "BX", name = "ممنوع" })));
        Assert.Equal("forbidden", await Code(await manager.PostAsJsonAsync($"/api/v1/branches/{_b1}/registers", new { code = "RX" })));
    }
}
