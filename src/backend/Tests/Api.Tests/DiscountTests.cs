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

/// <summary>US-053 / CAT-010 / D-18 / BR-021 (AT-033, SEC-T-101): automatic rules (largest wins) and manual discount caps.</summary>
public sealed class DiscountTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _b2 = Guid.CreateVersion7();
    private readonly Dictionary<string, (Guid Id, string Sub)> _u = [];
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public DiscountTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach (string n in new[] { "owner", "manager" })
        {
            _u[n] = (Guid.CreateVersion7(), $"disc-{n}-{Guid.NewGuid()}");
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, $4)", _u[n].Id, ApiFactory.Issuer, _u[n].Sub, n));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'تخفیف', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک'), ($1, $3, 'B2', 'دو')", _tenant, _b1, _b2);
            foreach ((string n, string role, string scopeType) in new[] { ("owner", "TenantOwner", "Tenant"), ("manager", "BranchManager", "Branch") })
            {
                await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _tenant, _u[n].Id);
                await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES ($1, $2, $3, $4, $5, $6)",
                    _tenant, Guid.CreateVersion7(), _u[n].Id, role, scopeType, scopeType == "Tenant" ? DBNull.Value : _b1);
            }

            return 0;
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

    private HttpClient As(string user)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(_u[user].Sub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, _tenant.ToString());
        return c;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    private async Task<Guid> Product(string sku)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/products")
        {
            Content = JsonContent.Create(new
            {
                name = "کالا " + sku, baseUnit = "EA", confirmation = new { confirmed = true },
                variants = new[] { new { sku, barcodes = new[] { new { code = sku, isInternal = true } } } },
            }),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return (await Body(await As("owner").SendAsync(req))).GetProperty("id").GetGuid();
    }

    private static object Rule(Guid product, int percent, DateTimeOffset? from = null, Guid[]? branches = null, string kind = "Product") => new
    {
        name = $"{percent / 100}٪", target = new { kind, id = product }, percent, validFrom = from ?? DateTimeOffset.UtcNow, branchIds = branches,
    };

    private async Task<int?> DiscountAt(string code, Guid branch)
    {
        JsonElement p = (await Body(await As("owner").PostAsJsonAsync("/api/v1/catalog/barcodes/resolve", new { barcode = code, branchId = branch })))
            .GetProperty("tenantProduct");
        return p.GetProperty("discount").ValueKind == JsonValueKind.Null ? null : p.GetProperty("discount").GetProperty("percent").GetInt32();
    }

    [DbFact]
    public async Task AT033_of_the_rules_in_force_only_the_largest_applies_and_a_branch_rule_only_in_its_branch()
    {
        Guid tea = await Product("DSC-TEA");
        HttpClient owner = As("owner");
        Assert.Null(await DiscountAt("DSC-TEA", _b1));
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/discount-rules", Rule(tea, 1000))).StatusCode);
        Guid fifteen = (await Body(await owner.PostAsJsonAsync("/api/v1/discount-rules", Rule(tea, 1500)))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/discount-rules", Rule(tea, 3000, branches: [_b2]))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/discount-rules", Rule(tea, 5000, DateTimeOffset.UtcNow.AddDays(1)))).StatusCode);

        Assert.Equal(1500, await DiscountAt("DSC-TEA", _b1));   // 15%, not 10 + 15
        Assert.Equal(3000, await DiscountAt("DSC-TEA", _b2));   // the branch's own rule is larger there

        // ending a rule keeps it in the history; the next largest applies
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/discount-rules/{fifteen}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/v1/discount-rules/{fifteen}")).StatusCode);
        Assert.Equal(1000, await DiscountAt("DSC-TEA", _b1));
        Assert.Equal(3, (await Body(await owner.GetAsync("/api/v1/discount-rules"))).GetProperty("items").GetArrayLength());
        Assert.Equal(4, (await Body(await owner.GetAsync("/api/v1/discount-rules?includeEnded=true"))).GetProperty("items").GetArrayLength());
    }

    [DbFact]
    public async Task A_branch_manager_makes_rules_only_for_their_own_branch_and_bad_rules_are_refused()
    {
        Guid oil = await Product("DSC-OIL");
        HttpClient manager = As("manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/v1/discount-rules", Rule(oil, 1000))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/v1/discount-rules", Rule(oil, 1000, branches: [_b2]))).StatusCode);
        Guid mine = (await Body(await manager.PostAsJsonAsync("/api/v1/discount-rules", Rule(oil, 1000, branches: [_b1])))).GetProperty("id").GetGuid();
        Guid storeWide = (await Body(await As("owner").PostAsJsonAsync("/api/v1/discount-rules", Rule(oil, 500)))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.DeleteAsync($"/api/v1/discount-rules/{storeWide}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/v1/discount-rules/{mine}")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await As("owner").PostAsJsonAsync("/api/v1/discount-rules", Rule(oil, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await As("owner").PostAsJsonAsync("/api/v1/discount-rules", Rule(oil, 1000, kind: "Category"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await As("owner").PostAsJsonAsync("/api/v1/discount-rules", Rule(Guid.NewGuid(), 1000))).StatusCode);
    }

    [DbFact]
    public async Task SEC_T_101_manual_discount_caps_default_to_zero_for_cashiers_and_only_the_owner_side_changes_them()
    {
        JsonElement caps = (await Body(await As("manager").GetAsync("/api/v1/discount-caps"))).GetProperty("caps");
        Assert.Equal((0, 10_000), (caps.GetProperty("Cashier").GetInt32(), caps.GetProperty("BranchManager").GetInt32()));
        Assert.Equal(HttpStatusCode.Forbidden, (await As("manager").PutAsJsonAsync("/api/v1/discount-caps", new { caps = new Dictionary<string, int> { ["Cashier"] = 10_000 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await As("owner").PutAsJsonAsync("/api/v1/discount-caps", new { caps = new Dictionary<string, int> { ["Auditor"] = 100 } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await As("owner").PutAsJsonAsync("/api/v1/discount-caps", new { caps = new Dictionary<string, int> { ["Cashier"] = 500 } })).StatusCode);
        Assert.Equal(500, (await Body(await As("manager").GetAsync("/api/v1/discount-caps"))).GetProperty("caps").GetProperty("Cashier").GetInt32());
    }
}
