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

/// <summary>US-064 / INV-008 (AT-047): a single-step transfer moves stock and its value at the source's WAC.</summary>
public sealed class TransferTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _b2 = Guid.CreateVersion7(), _w1 = Guid.CreateVersion7(),
        _w1b = Guid.CreateVersion7(), _w2 = Guid.CreateVersion7();
    private readonly Dictionary<string, (Guid Id, string Sub)> _u = [];
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public TransferTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach (string n in new[] { "owner", "clerk", "cashier" })
        {
            _u[n] = (Guid.CreateVersion7(), $"trf-{n}-{Guid.NewGuid()}");
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, $4)", _u[n].Id, ApiFactory.Issuer, _u[n].Sub, n));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'انتقال', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک'), ($1, $3, 'B2', 'دو')", _tenant, _b1, _b2);
            await Exec(s, ct, "INSERT INTO inv.warehouse (tenant_id, id, branch_id, code, kind) VALUES ($1, $2, $3, 'W1', 'Sellable'), ($1, $4, $3, 'W1-DMG', 'ReturnsDamaged'), " +
                              "($1, $5, $6, 'W2', 'Sellable')", _tenant, _w1, _b1, _w1b, _w2, _b2);
            foreach ((string n, string role, string scopeType) in new[] { ("owner", "TenantOwner", "Tenant"), ("clerk", "WarehouseClerk", "Branch"), ("cashier", "Cashier", "Branch") })
            {
                await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _tenant, _u[n].Id);
                await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES ($1, $2, $3, $4, $5, $6)",
                    _tenant, Guid.CreateVersion7(), _u[n].Id, role, scopeType, scopeType == "Tenant" ? DBNull.Value : _b1);
            }

            return 0;
        });
        await TestAccounting.SetUpAsync(Cs, _tenant);
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

    private async Task<HttpResponseMessage> Post(string user, string path, object body, string? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await As(user).SendAsync(req);
    }

    private async Task<Guid> Stocked(string sku, Guid warehouse, string qty, string cost)
    {
        HttpResponseMessage r = await Post("owner", "/api/v1/products", new
        {
            name = "کالا " + sku, baseUnit = "EA", confirmation = new { confirmed = true }, openingStock = new { warehouseId = warehouse, qty, unitCost = cost },
            variants = new[] { new { sku } },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Body(r)).GetProperty("variants")[0].GetProperty("id").GetGuid();
    }

    private async Task<(string OnHand, string Wac, string Value)> Balance(Guid variant, Guid warehouse)
    {
        JsonElement b = (await Body(await As("owner").GetAsync($"/api/v1/inventory/balances?variantId={variant}&warehouseId={warehouse}"))).GetProperty("items")[0];
        return (b.GetProperty("onHand").GetString()!, b.GetProperty("wac").GetString()!, b.GetProperty("value").GetString()!);
    }

    private static object Transfer(Guid from, Guid to, params (Guid Variant, string Qty)[] lines) =>
        new { fromWarehouseId = from, toWarehouseId = to, lines = lines.Select(l => new { variantId = l.Variant, qty = l.Qty }).ToArray() };

    [DbFact]
    public async Task AT047_what_leaves_the_source_at_its_WAC_arrives_with_the_same_value_and_re_averages_the_destination()
    {
        Guid rice = await Stocked("TR-RICE", _w1, "10", "60000");
        // W2 already holds 1 bought at 90,000
        Guid supplier = (await Body(await As("owner").PostAsJsonAsync("/api/v1/suppliers", new { name = "برنج‌فروش" }))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await Post("owner", "/api/v1/inventory/receipts", new
        {
            supplierId = supplier, warehouseId = _w2, lines = new[] { new { variantId = rice, qty = "1", unit = "EA", unitCost = "90000" } },
        })).StatusCode);

        string key = Guid.NewGuid().ToString("N");
        HttpResponseMessage r = await Post("owner", "/api/v1/inventory/transfers", Transfer(_w1, _w2, (rice, "3")), key);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        JsonElement t = await Body(r);
        Assert.Equal(("Completed", "180000", "60000"), (t.GetProperty("status").GetString(), t.GetProperty("value").GetString(),
            t.GetProperty("lines")[0].GetProperty("unitCost").GetString()));
        Assert.Equal(("7", "60000", "420000"), await Balance(rice, _w1));
        // the destination re-averages with what arrived at the source's cost: (90,000 + 3 × 60,000) ÷ 4 = 67,500 (BR-043)
        Assert.Equal(("4", "67500", "270000"), await Balance(rice, _w2));
        Assert.Equal(t.GetProperty("id").GetGuid(), (await Body(await Post("owner", "/api/v1/inventory/transfers", Transfer(_w1, _w2, (rice, "3")), key))).GetProperty("id").GetGuid());
        Assert.Equal(("7", "60000", "420000"), await Balance(rice, _w1));   // a replay moved nothing twice

        JsonElement card = (await Body(await As("owner").GetAsync($"/api/v1/inventory/movements?variantId={rice}"))).GetProperty("items");
        Assert.Equal(["TransferIn", "TransferOut", "GoodsReceipt", "OpeningBalance"], card.EnumerateArray().Select(m => m.GetProperty("type").GetString()));
        Assert.Single((await Body(await As("owner").GetAsync("/api/v1/inventory/transfers"))).GetProperty("items").EnumerateArray(),
            x => x.GetProperty("id").GetGuid() == t.GetProperty("id").GetGuid());
    }

    [DbFact]
    public async Task A_transfer_needs_stock_two_warehouses_and_the_right_in_both()
    {
        Guid oil = await Stocked("TR-OIL", _w1, "2", "100000");
        HttpResponseMessage shortage = await Post("owner", "/api/v1/inventory/transfers", Transfer(_w1, _w1b, (oil, "3")));
        Assert.Equal(HttpStatusCode.Conflict, shortage.StatusCode);
        Assert.Equal("insufficient-stock", (await Body(shortage)).GetProperty("code").GetString());
        Assert.Equal(("2", "100000", "200000"), await Balance(oil, _w1));
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("owner", "/api/v1/inventory/transfers", Transfer(_w1, _w1, (oil, "1")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("owner", "/api/v1/inventory/transfers", Transfer(_w1, _w1b, (oil, "0")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("owner", "/api/v1/inventory/transfers", Transfer(_w1, _w1b, (Guid.NewGuid(), "1")))).StatusCode);

        // a branch's clerk moves stock within the branch, not out of it (another branch's warehouse), and a cashier not at all
        Assert.Equal(HttpStatusCode.Created, (await Post("clerk", "/api/v1/inventory/transfers", Transfer(_w1, _w1b, (oil, "1")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("clerk", "/api/v1/inventory/transfers", Transfer(_w1, _w2, (oil, "1")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("cashier", "/api/v1/inventory/transfers", Transfer(_w1, _w1b, (oil, "1")))).StatusCode);
        Assert.Equal(("1", "100000", "100000"), await Balance(oil, _w1b));
    }
}
