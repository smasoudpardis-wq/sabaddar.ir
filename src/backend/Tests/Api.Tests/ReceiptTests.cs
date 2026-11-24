using System.Globalization;
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

/// <summary>US-071 / PRC-001 / PRC-002 / UC-05 (AT-240): suppliers and goods receipts with WAC and their entry in one transaction.</summary>
public sealed class ReceiptTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _b2 = Guid.CreateVersion7(), _w1 = Guid.CreateVersion7(),
        _w2 = Guid.CreateVersion7();
    private readonly Dictionary<string, (Guid Id, string Sub)> _u = [];
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public ReceiptTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach (string n in new[] { "owner", "purchaser", "cashier" })
        {
            _u[n] = (Guid.CreateVersion7(), $"rcpt-{n}-{Guid.NewGuid()}");
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, $4)", _u[n].Id, ApiFactory.Issuer, _u[n].Sub, n));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'خرید', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک'), ($1, $3, 'B2', 'دو')", _tenant, _b1, _b2);
            await Exec(s, ct, "INSERT INTO inv.warehouse (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'W1'), ($1, $4, $5, 'W2')", _tenant, _w1, _b1, _w2, _b2);
            foreach ((string n, string role, string scopeType) in new[]
                     { ("owner", "TenantOwner", "Tenant"), ("purchaser", "PurchasingOfficer", "Branch"), ("cashier", "Cashier", "Branch") })
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

    private static long L(JsonElement e) => long.Parse(e.GetString()!, CultureInfo.InvariantCulture);

    private async Task<HttpResponseMessage> Send(string user, string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await As(user).SendAsync(req);
    }

    /// <summary>A product of one variant; <paramref name="carton"/>: also sold by the carton of 24 (a CT barcode).</summary>
    private async Task<Guid> Product(string sku, bool carton = false, object? openingStock = null)
    {
        object[] barcodes = carton ? [new { code = sku + "-CT", packagingUnit = "CT", unitFactor = "24", isInternal = true }] : [];
        HttpResponseMessage r = await Send("owner", "/api/v1/products", new
        {
            name = "کالا " + sku, baseUnit = "EA", confirmation = new { confirmed = true }, openingStock, variants = new[] { new { sku, barcodes } },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Body(r)).GetProperty("variants")[0].GetProperty("id").GetGuid();
    }

    private async Task<Guid> Supplier(string name, string? nationalId = null)
    {
        HttpResponseMessage r = await As("owner").PostAsJsonAsync("/api/v1/suppliers", new { name, nationalId });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Body(r)).GetProperty("id").GetGuid();
    }

    private static object Line(Guid variant, string qty, string unitCost, string unit = "EA", string? discount = null, string? inputVat = null) =>
        new { variantId = variant, qty, unit, unitCost, discount, inputVat };

    private static object Receipt(Guid supplier, Guid warehouse, object[] lines, string? invoice = null, string[]? confirmed = null) =>
        new { supplierId = supplier, warehouseId = warehouse, lines, supplierInvoiceNo = invoice, confirmedWarnings = confirmed };

    private async Task<Dictionary<string, (long Debit, long Credit, Guid? Detail)>> Entry(Guid journalId)
    {
        JsonElement j = (await Body(await As("owner").GetAsync("/api/v1/accounting/journals?limit=100"))).GetProperty("items")
            .EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == journalId);
        Assert.Equal("Posted", j.GetProperty("status").GetString());
        return j.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("accountCode").GetString()!,
            l => (L(l.GetProperty("debit")), L(l.GetProperty("credit")),
                  l.GetProperty("detailId").ValueKind == JsonValueKind.Null ? (Guid?)null : l.GetProperty("detailId").GetGuid()));
    }

    private async Task<long> SupplierBalance(Guid supplier) =>
        L((await Body(await As("owner").GetAsync("/api/v1/suppliers"))).GetProperty("items").EnumerateArray()
            .Single(s => s.GetProperty("id").GetGuid() == supplier).GetProperty("balance"));

    [DbFact]
    public async Task AT240_a_receipt_re_averages_the_cost_and_owes_the_supplier_in_the_same_transaction()
    {
        Guid tea = await Product("RC-TEA"), alborz = await Supplier("پخش البرز");
        Assert.Equal(HttpStatusCode.Created, (await Send("owner", "/api/v1/inventory/receipts", Receipt(alborz, _w1, [Line(tea, "10", "100")]))).StatusCode);

        HttpResponseMessage r = await Send("purchaser", "/api/v1/inventory/receipts", Receipt(alborz, _w1, [Line(tea, "10", "200")], "F-1001"));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        JsonElement receipt = await Body(r);
        Assert.Equal(("2000", "150"), (receipt.GetProperty("total").GetString(), receipt.GetProperty("lines")[0].GetProperty("newWac").GetString()));

        // Inventory 2,000 debit / the supplier's payable 2,000 credit, with the supplier as its detail (DI-043)
        var lines = await Entry(receipt.GetProperty("journalEntryId").GetGuid());
        Assert.Equal((2000L, 0L, (Guid?)null), lines["1106"]);
        Assert.Equal((0L, 2000L, (Guid?)alborz), lines["2101"]);
        Assert.Equal(3000, await SupplierBalance(alborz));

        JsonElement b = (await Body(await As("owner").GetAsync($"/api/v1/inventory/balances?variantId={tea}"))).GetProperty("items")[0];
        Assert.Equal(("20", "150", "3000"), (b.GetProperty("onHand").GetString(), b.GetProperty("wac").GetString(), b.GetProperty("value").GetString()));
        Assert.Contains((await Body(await As("owner").GetAsync("/api/v1/inventory/receipts"))).GetProperty("items").EnumerateArray(),
            x => x.GetProperty("id").GetGuid() == receipt.GetProperty("id").GetGuid() && x.GetProperty("supplierInvoiceNo").GetString() == "F-1001");
    }

    [DbFact]
    public async Task Cartons_discount_and_input_VAT_are_received_at_the_net_cost_per_base_unit()
    {
        Guid cola = await Product("RC-COLA", carton: true), s = await Supplier("نوشابه‌سازی");
        HttpResponseMessage r = await Send("owner", "/api/v1/inventory/receipts",
            Receipt(s, _w1, [Line(cola, "2", "2400000", "CT", discount: "100000", inputVat: "470000")]));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        JsonElement l = (await Body(r)).GetProperty("lines")[0];
        // 2 × 2,400,000 − 100,000 = 4,700,000 for 48 bottles: 97,916.67 → 97,917 (BR-043)
        Assert.Equal(("48", "4700000", "97917"), (l.GetProperty("qtyBase").GetString(), l.GetProperty("lineCost").GetString(), l.GetProperty("newWac").GetString()));
        var lines = await Entry((await Body(r)).GetProperty("journalEntryId").GetGuid());
        Assert.Equal(4_700_000, lines["1106"].Debit);
        Assert.Equal(470_000, lines["1107"].Debit);
        Assert.Equal((0L, 5_170_000L, (Guid?)s), lines["2101"]);

        // BR-044: the stock card keeps what the invoice said
        JsonElement m = (await Body(await As("owner").GetAsync($"/api/v1/inventory/movements?variantId={cola}"))).GetProperty("items")[0];
        Assert.Equal(("2", "CT", "24"), (m.GetProperty("enteredQty").GetString(), m.GetProperty("enteredUnit").GetString(), m.GetProperty("unitFactor").GetString()));

        HttpResponseMessage unknown = await Send("owner", "/api/v1/inventory/receipts", Receipt(s, _w1, [Line(cola, "1", "100", "PK")]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal("unknown-unit", (await Body(unknown)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send("owner", "/api/v1/inventory/receipts",
            Receipt(s, _w1, [Line(cola, "1", "100", discount: "101")]))).StatusCode);
    }

    [DbFact]
    public async Task UC05_a_zero_cost_line_and_a_repeated_supplier_invoice_are_posted_only_once_confirmed()
    {
        Guid oil = await Product("RC-OIL"), s = await Supplier("روغن نباتی");
        object gift = Receipt(s, _w1, [Line(oil, "10", "500000"), Line(oil, "2", "0")], "INV-7");
        HttpResponseMessage asked = await Send("owner", "/api/v1/inventory/receipts", gift);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, asked.StatusCode);
        JsonElement p = await Body(asked);
        Assert.Equal("confirmation-required", p.GetProperty("code").GetString());
        Assert.Equal(["zero-cost"], p.GetProperty("context").GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
        Assert.Empty((await Body(await As("owner").GetAsync($"/api/v1/inventory/balances?variantId={oil}"))).GetProperty("items").EnumerateArray());

        HttpResponseMessage ok = await Send("owner", "/api/v1/inventory/receipts",
            Receipt(s, _w1, [Line(oil, "10", "500000"), Line(oil, "2", "0")], "INV-7", ["zero-cost"]));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal("416667", (await Body(ok)).GetProperty("lines")[1].GetProperty("newWac").GetString());   // the bonus lowers the WAC: 5,000,000 ÷ 12

        HttpResponseMessage again = await Send("owner", "/api/v1/inventory/receipts", Receipt(s, _w1, [Line(oil, "1", "500000")], "INV-7"));
        Assert.Equal(["duplicate-supplier-invoice"], (await Body(again)).GetProperty("context").GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
        Assert.Equal(HttpStatusCode.Created, (await Send("owner", "/api/v1/inventory/receipts",
            Receipt(s, _w1, [Line(oil, "1", "500000")], "INV-7", ["duplicate-supplier-invoice"]))).StatusCode);
    }

    [DbFact]
    public async Task Receipts_are_for_purchasing_in_their_own_branch_and_suppliers_are_kept_store_wide()
    {
        Guid rice = await Product("RC-RICE"), s = await Supplier("برنج شمال", "10101010102");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("cashier", "/api/v1/inventory/receipts", Receipt(s, _w1, [Line(rice, "1", "100")]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("purchaser", "/api/v1/inventory/receipts", Receipt(s, _w2, [Line(rice, "1", "100")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send("owner", "/api/v1/inventory/receipts", Receipt(Guid.NewGuid(), _w1, [Line(rice, "1", "100")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send("owner", "/api/v1/inventory/receipts", Receipt(s, _w1, [Line(Guid.NewGuid(), "1", "100")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send("owner", "/api/v1/inventory/receipts", Receipt(s, _w1, [Line(rice, "0", "100")]))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await As("purchaser").PostAsJsonAsync("/api/v1/suppliers", new { name = "شعبه‌ای" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await As("owner").PostAsJsonAsync("/api/v1/suppliers", new { name = "تکراری", nationalId = "10101010102" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await As("owner").PostAsJsonAsync("/api/v1/suppliers", new { name = "بد", nationalId = "123" })).StatusCode);
        Assert.Equal(0, await SupplierBalance(s));
    }

    [DbFact]
    public async Task UC01_opening_stock_posts_inventory_against_opening_balance_equity()
    {
        Guid sugar = await Product("RC-SUGAR", openingStock: new { warehouseId = _w1, qty = "40", unitCost = "350000" });
        JsonElement card = (await Body(await As("owner").GetAsync($"/api/v1/inventory/movements?variantId={sugar}"))).GetProperty("items")[0];
        JsonElement entry = (await Body(await As("owner").GetAsync("/api/v1/accounting/journals?sourceType=Product&limit=100"))).GetProperty("items")
            .EnumerateArray().Single(e => e.GetProperty("source").GetProperty("id").GetGuid() == card.GetProperty("sourceId").GetGuid());
        var lines = await Entry(entry.GetProperty("id").GetGuid());
        Assert.Equal((14_000_000L, 0L, (Guid?)null), lines["1106"]);
        Assert.Equal((0L, 14_000_000L, (Guid?)null), lines["3102"]);
    }
}
