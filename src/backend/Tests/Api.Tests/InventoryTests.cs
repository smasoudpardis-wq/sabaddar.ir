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

/// <summary>US-061 / INV-001..006 through the API: opening stock, balances (TM-014), the stock card and BR-040 policies.</summary>
public sealed class InventoryTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _w1 = Guid.CreateVersion7();
    private readonly Dictionary<string, (Guid Id, string Sub)> _u = [];
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public InventoryTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach (string n in new[] { "owner", "cashier" })
        {
            _u[n] = (Guid.CreateVersion7(), $"inv-{n}-{Guid.NewGuid()}");
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, $4)", _u[n].Id, ApiFactory.Issuer, _u[n].Sub, n));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'انبار', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک')", _tenant, _b1);
            await Exec(s, ct, "INSERT INTO inv.warehouse (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'W1')", _tenant, _w1, _b1);
            foreach ((string n, string role, string scopeType) in new[] { ("owner", "TenantOwner", "Tenant"), ("cashier", "Cashier", "Branch") })
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

    private static object Rice(string sku, object? openingStock, int variants = 1) => new
    {
        name = "برنج " + sku, baseUnit = "KG", confirmation = new { confirmed = true }, openingStock,
        variants = Enumerable.Range(1, variants).Select(i => new { sku = $"{sku}-{i}", axisValues = new { size = i.ToString(System.Globalization.CultureInfo.InvariantCulture) } }).ToArray(),
    };

    private async Task<HttpResponseMessage> Create(object body, string? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/products") { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await As("owner").SendAsync(req);
    }

    [DbFact]
    public async Task A_new_product_starts_with_its_opening_stock_once_and_only_the_report_reader_sees_its_value()
    {
        string key = Guid.NewGuid().ToString("N");
        object body = Rice("OPEN", new { warehouseId = _w1, qty = "12.5", unitCost = "250000" });
        HttpResponseMessage created = await Create(body, key);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Guid variant = (await Body(created)).GetProperty("variants")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await Create(body, key)).StatusCode);   // a retry replays: no second opening stock

        JsonElement b = (await Body(await As("owner").GetAsync($"/api/v1/inventory/balances?variantId={variant}"))).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(("12.5", "12.5", "250000", "3125000"),
            (b.GetProperty("onHand").GetString(), b.GetProperty("available").GetString(), b.GetProperty("wac").GetString(), b.GetProperty("value").GetString()));

        // TM-014: a cashier sees how much there is, not what it cost; nor the stock card
        JsonElement seen = (await Body(await As("cashier").GetAsync($"/api/v1/inventory/balances?variantId={variant}"))).GetProperty("items")[0];
        Assert.Equal("12.5", seen.GetProperty("onHand").GetString());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("wac").ValueKind);
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("value").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await As("cashier").GetAsync($"/api/v1/inventory/movements?variantId={variant}&warehouseId={_w1}")).StatusCode);

        JsonElement card = (await Body(await As("owner").GetAsync($"/api/v1/inventory/movements?variantId={variant}"))).GetProperty("items");
        JsonElement m = card.EnumerateArray().Single();
        Assert.Equal(("OpeningBalance", "12.5", "250000", "3125000", "Product"), (m.GetProperty("type").GetString(), m.GetProperty("qtyBase").GetString(),
            m.GetProperty("unitCost").GetString(), m.GetProperty("totalCost").GetString(), m.GetProperty("sourceType").GetString()));
        Assert.Empty((await Body(await As("owner").GetAsync($"/api/v1/inventory/movements?variantId={variant}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}")))
            .GetProperty("items").EnumerateArray());
    }

    [DbFact]
    public async Task Opening_stock_needs_one_variant_and_a_warehouse_of_this_store()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(Rice("TWO", new { warehouseId = _w1, qty = "1", unitCost = "1" }, variants: 2))).StatusCode);
        HttpResponseMessage foreign = await Create(Rice("FAR", new { warehouseId = Guid.NewGuid(), qty = "1", unitCost = "1" }));
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.True((await Body(foreign)).GetProperty("errors").TryGetProperty("openingStock.warehouseId", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(Rice("NEG", new { warehouseId = _w1, qty = "-1", unitCost = "1" }))).StatusCode);
        // nothing of a refused product stays behind
        Assert.Empty((await Body(await As("owner").GetAsync("/api/v1/products?q=FAR-1"))).GetProperty("items").EnumerateArray());
    }

    [DbFact]
    public async Task BR040_the_negative_stock_policy_is_set_per_level_and_audited()
    {
        HttpClient owner = As("owner");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/api/v1/inventory/negative-stock-policies",
            new { scopeType = "Tenant", policy = "AllowWithWarning" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/api/v1/inventory/negative-stock-policies",
            new { scopeType = "Branch", scopeId = _b1, policy = "Forbid" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync("/api/v1/inventory/negative-stock-policies",
            new { scopeType = "Branch", scopeId = Guid.NewGuid(), policy = "Forbid" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/inventory/negative-stock-policies",
            new { scopeType = "Tenant", policy = "Always" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As("cashier").PutAsJsonAsync("/api/v1/inventory/negative-stock-policies",
            new { scopeType = "Tenant", policy = "AllowWithWarning" })).StatusCode);

        await using var ds = NpgsqlDataSource.Create(Cs);
        (long policies, long audited) = await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)s).Command(
                "SELECT (SELECT count(*) FROM inv.negative_stock_policy), (SELECT count(*) FROM audit.audit_record WHERE action = 'inventory.negative-stock-policy.set')");
            await using NpgsqlDataReader r = await c.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return (r.GetInt64(0), r.GetInt64(1));
        });
        Assert.Equal((2L, 2L), (policies, audited));
    }

    [DbFact]
    public async Task The_store_lists_its_warehouses()
    {
        JsonElement items = (await Body(await As("cashier").GetAsync("/api/v1/warehouses"))).GetProperty("items");
        Assert.Equal(_w1, items.EnumerateArray().Single().GetProperty("id").GetGuid());
    }
}
