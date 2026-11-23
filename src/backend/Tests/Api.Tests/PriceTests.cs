using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using RetailCloud.BuildingBlocks.Api;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Merchandising.Application;

namespace RetailCloud.Api.Tests;

/// <summary>US-052 / CAT-009 / BR-041 / AB-06 (AT-032, SEC-T-105) and CAT-007.1 (AT-030): versioned prices and approvals.</summary>
public sealed class PriceTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7();
    private readonly Dictionary<string, (Guid Id, string Sub)> _u = [];
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public PriceTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach (string n in new[] { "owner", "admin", "manager" })
        {
            _u[n] = (Guid.CreateVersion7(), $"pr-{n}-{Guid.NewGuid()}");
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, $4)", _u[n].Id, ApiFactory.Issuer, _u[n].Sub, n));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'قیمت', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک')", _tenant, _b1);
            foreach ((string n, string role, string scopeType) in new[] { ("owner", "TenantOwner", "Tenant"), ("admin", "TenantAdmin", "Tenant"), ("manager", "BranchManager", "Branch") })
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

    private static async Task<HttpResponseMessage> Post(HttpClient c, string path, object body, string? key = null, Guid? approval = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        if (approval is { } a) req.Headers.Add("X-Approval-Id", a.ToString());
        return await c.SendAsync(req);
    }

    private async Task<Guid> DefaultList() =>
        (await Body(await As("owner").GetAsync("/api/v1/price-lists"))).GetProperty("items").EnumerateArray()
            .Single(l => l.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();

    private async Task<(Guid Product, Guid[] Variants)> Product(string sku, int variants = 1, string unit = "EA", object[]? barcodes = null)
    {
        JsonElement p = await Body(await Post(As("owner"), "/api/v1/products", new
        {
            name = "کالا " + sku, baseUnit = unit, confirmation = new { confirmed = true },
            variants = Enumerable.Range(1, variants).Select(i => new
            {
                sku = variants > 1 ? $"{sku}-{i}" : sku, axisValues = variants > 1 ? new { size = i.ToString(System.Globalization.CultureInfo.InvariantCulture) } : null,
                barcodes = barcodes ?? [],
            }).ToArray(),
        }));
        return (p.GetProperty("id").GetGuid(), [.. p.GetProperty("variants").EnumerateArray().Select(v => v.GetProperty("id").GetGuid())]);
    }

    private static object Set(Guid variant, string price, DateTimeOffset from, string? reason = null) =>
        new { items = new[] { new { variantId = variant, unitPrice = price } }, validFrom = from, reason };

    private async Task<string?> PriceOnProduct(Guid product) =>
        (await Body(await As("owner").GetAsync($"/api/v1/products/{product}"))).GetProperty("variants")[0].GetProperty("unitPrice").GetString();

    private async Task<JsonElement[]> History(Guid list, Guid variant) =>
        [.. (await Body(await As("admin").GetAsync($"/api/v1/price-lists/{list}/prices?variantId={variant}"))).GetProperty("items").EnumerateArray()];

    [DbFact]
    public async Task AT032_prices_are_versioned_from_validFrom_and_the_product_shows_the_price_in_force()
    {
        Guid list = await DefaultList();
        Assert.Equal(list, await DefaultList());   // created once
        (Guid product, Guid[] v) = await Product("AT32-TEA");
        Assert.Null(await PriceOnProduct(product));
        HttpClient admin = As("admin");
        string path = $"/api/v1/price-lists/{list}/prices";

        string key = Guid.NewGuid().ToString("N");
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        HttpResponseMessage first = await Post(admin, path, Set(v[0], "500000", t0, "قیمت اولیه"), key);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        HttpResponseMessage replay = await Post(admin, path, Set(v[0], "500000", t0, "قیمت اولیه"), key);   // a retry: no second version
        Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        Assert.True(replay.Headers.Contains(Idempotency.ReplayedHeader));
        Assert.Equal("500000", await PriceOnProduct(product));

        // a +20% change scheduled for tomorrow (no approval), then corrected before it starts
        DateTimeOffset tomorrow = DateTimeOffset.UtcNow.AddDays(1);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(admin, path, Set(v[0], "600000", tomorrow))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(admin, path, Set(v[0], "620000", tomorrow))).StatusCode);
        Assert.Equal("500000", await PriceOnProduct(product));

        // -10% from now: the current version ends now and the new one runs until tomorrow's scheduled change
        Assert.Equal(HttpStatusCode.NoContent, (await Post(admin, path, Set(v[0], "450000", DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal("450000", await PriceOnProduct(product));
        JsonElement[] h = await History(list, v[0]);
        Assert.Equal(["620000", "450000", "500000"], h.Select(x => x.GetProperty("unitPrice").GetString()));
        Assert.Equal(JsonValueKind.Null, h[0].GetProperty("validTo").ValueKind);
        Assert.Equal(h[0].GetProperty("validFrom").GetDateTimeOffset(), h[1].GetProperty("validTo").GetDateTimeOffset());
        Assert.Equal(h[1].GetProperty("validFrom").GetDateTimeOffset(), h[2].GetProperty("validTo").GetDateTimeOffset());
        Assert.Equal("قیمت اولیه", h[2].GetProperty("reason").GetString());
        Assert.Equal(_u["admin"].Id, h[2].GetProperty("createdBy").GetGuid());

        // never backwards in time, never for an unknown variant, never a zero price
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(admin, path, Set(v[0], "1000", DateTimeOffset.UtcNow.AddHours(-1)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(admin, path, Set(Guid.NewGuid(), "1000", DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(admin, path, Set(v[0], "0", DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(admin, $"/api/v1/price-lists/{Guid.NewGuid()}/prices", Set(v[0], "1000", DateTimeOffset.UtcNow))).StatusCode);

        // the change is audited with the old and new price
        await using var ds = NpgsqlDataSource.Create(Cs);
        long audited = await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)s).Command(
                "SELECT count(*) FROM audit.audit_record WHERE action = 'catalog.prices.set' AND details::text LIKE '%450000%' AND details::text LIKE '%500000%'");
            return (long)(await c.ExecuteScalarAsync(ct))!;
        });
        Assert.Equal(1, audited);
    }

    [DbFact]
    public async Task BR041_a_big_change_waits_for_another_manager_and_the_approval_fits_only_that_change()
    {
        Guid list = await DefaultList();
        (Guid product, Guid[] v) = await Product("BR41-OIL");
        HttpClient admin = As("admin");
        string path = $"/api/v1/price-lists/{list}/prices";
        Assert.Equal(HttpStatusCode.NoContent, (await Post(admin, path, Set(v[0], "500000", DateTimeOffset.UtcNow))).StatusCode);

        // +40%: refused until approved; nothing changes meanwhile
        DateTimeOffset from = DateTimeOffset.UtcNow;
        string key = Guid.NewGuid().ToString("N");
        HttpResponseMessage asked = await Post(admin, path, Set(v[0], "700000", from), key);
        Assert.Equal(HttpStatusCode.Forbidden, asked.StatusCode);
        JsonElement problem = await Body(asked);
        Assert.Equal("approval-required", problem.GetProperty("code").GetString());
        Assert.Equal("change-limit", problem.GetProperty("context").GetProperty("condition").GetString());
        Guid approval = problem.GetProperty("approvalRequestId").GetGuid();
        Assert.Equal("500000", await PriceOnProduct(product));

        // retrying before the decision points at the same request, not a new one
        HttpResponseMessage early = await Post(admin, path, Set(v[0], "700000", from), key, approval);
        Assert.Equal(approval, (await Body(early)).GetProperty("approvalRequestId").GetGuid());

        // the requester cannot approve it; the owner can
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/api/v1/approvals/{approval}/decision", new { decision = "Approve" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As("owner").PostAsJsonAsync($"/api/v1/approvals/{approval}/decision", new { decision = "Approve" })).StatusCode);

        // the approval does not fit a different price
        HttpResponseMessage other = await Post(admin, path, Set(v[0], "900000", from), approval: approval);
        Assert.Equal("approval-required", (await Body(other)).GetProperty("code").GetString());

        // the same request with the approval goes through (validFrom is now in the past: moved to now), once
        Assert.Equal(HttpStatusCode.NoContent, (await Post(admin, path, Set(v[0], "700000", from), key, approval)).StatusCode);
        Assert.Equal("700000", await PriceOnProduct(product));
        HttpResponseMessage again = await Post(admin, path, Set(v[0], "700000", from), approval: approval);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);   // no change in price now: 700000 -> 700000 needs no approval

        // SEC-T-105: the history keeps the short-lived price for review
        Assert.Equal(["700000", "700000", "500000"], (await History(list, v[0])).Select(x => x.GetProperty("unitPrice").GetString()));
    }

    [DbFact]
    public async Task More_than_twenty_variants_is_a_bulk_change_and_a_branch_manager_cannot_change_the_store_wide_list()
    {
        Guid list = await DefaultList();
        (_, Guid[] v) = await Product("BULK-SOCK", variants: 21);
        string path = $"/api/v1/price-lists/{list}/prices";
        object all = new { items = v.Select(id => new { variantId = id, unitPrice = "100000" }).ToArray(), validFrom = DateTimeOffset.UtcNow };
        HttpResponseMessage bulk = await Post(As("admin"), path, all);
        Assert.Equal(HttpStatusCode.Forbidden, bulk.StatusCode);
        Assert.Equal("bulk", (await Body(bulk)).GetProperty("context").GetProperty("condition").GetString());

        HttpResponseMessage manager = await Post(As("manager"), path, Set(v[0], "100000", DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.Forbidden, manager.StatusCode);
        Assert.Equal("forbidden", (await Body(manager)).GetProperty("code").GetString());
    }

    [DbFact]
    public async Task AT030_a_price_label_sells_the_rounded_quantity_for_exactly_the_printed_amount()
    {
        HttpClient owner = As("owner");
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/weighted-barcode-profiles", new
        {
            expectedVersion = 1,
            profiles = new object[]
            {
                new { prefix = "21", itemCodeLength = 4, valueType = "Weight", valueLength = 6, decimals = 3, hasInnerCheckDigit = false },
                new { prefix = "20", itemCodeLength = 4, valueType = "Price", valueLength = 5, decimals = 0, hasInnerCheckDigit = true },
            },
        })).StatusCode);
        (_, Guid[] cheese) = await Product("AT30-CHEESE", unit: "KG", barcodes: [new { code = "0789", isInternal = true }]);
        Guid list = await DefaultList();
        Assert.Equal(HttpStatusCode.NoContent, (await Post(owner, $"/api/v1/price-lists/{list}/prices", Set(cheese[0], "30000", DateTimeOffset.UtcNow))).StatusCode);

        // 10,000 rials of a 30,000/kg item: 0.333 kg, and the line is 10,000 (not 0.333 x 30,000 = 9,990)
        string label = "20" + "0789" + WeightedBarcodes.ValueCheckDigit("10000") + "10000";   // prefix, item, price check, 10000
        JsonElement r = await Body(await owner.PostAsJsonAsync("/api/v1/catalog/barcodes/resolve", new { barcode = Ean(label), branchId = _b1 }));
        JsonElement w = r.GetProperty("normalized").GetProperty("weighted");
        Assert.Equal("Price", w.GetProperty("valueType").GetString());
        Assert.Equal("0.333", w.GetProperty("quantity").GetString());
        Assert.Equal("10000", w.GetProperty("amount").GetString());
        Assert.Equal("30000", r.GetProperty("tenantProduct").GetProperty("unitPrice").GetString());

        // a weight label: 1.250 kg x 30,000 = 37,500
        JsonElement k = (await Body(await owner.PostAsJsonAsync("/api/v1/catalog/barcodes/resolve",
            new { barcode = Ean("21" + "0789" + "001250"), branchId = _b1 }))).GetProperty("normalized").GetProperty("weighted");
        Assert.Equal("1.250", k.GetProperty("quantity").GetString());
        Assert.Equal("37500", k.GetProperty("amount").GetString());
    }

    private static string Ean(string data12) => data12 + Gtin.ComputeCheckDigit(data12);
}
