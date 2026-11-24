using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using RetailCloud.BuildingBlocks.Api;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;

namespace RetailCloud.Api.Tests;

/// <summary>US-091 / SAL-005 / SAL-007 / BR-056 (AT-210, AT-211, AT-213, AT-214): the atomic, idempotent Finalize.</summary>
public sealed class FinalizeTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _r1 = Guid.CreateVersion7(), _r2 = Guid.CreateVersion7(),
        _w1 = Guid.CreateVersion7();
    private readonly string _device = "dev-fin-" + Guid.NewGuid();
    private readonly Dictionary<string, (Guid Id, string Sub)> _u = [];
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public FinalizeTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach (string n in new[] { "owner", "cashier", "cashier2" })
        {
            _u[n] = (Guid.CreateVersion7(), $"fin-{n}-{Guid.NewGuid()}");
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, $4)", _u[n].Id, ApiFactory.Issuer, _u[n].Sub, n));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'نهایی', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک')", _tenant, _b1);
            await Exec(s, ct, "INSERT INTO platform.register (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'R1'), ($1, $4, $3, 'R2')", _tenant, _r1, _b1, _r2);
            await Exec(s, ct, "INSERT INTO inv.warehouse (tenant_id, id, branch_id, code, kind) VALUES ($1, $2, $3, 'W1', 'Sellable')", _tenant, _w1, _b1);
            await Exec(s, ct, "INSERT INTO platform.terminal (tenant_id, id, branch_id, device_name, secret_hash, paired_by) VALUES ($1, $2, $3, 'صندوق', $4, $5)",
                _tenant, Guid.CreateVersion7(), _b1, SHA256.HashData(Encoding.UTF8.GetBytes(_device)), _u["owner"].Id);
            foreach ((string n, string role, string scopeType) in new[]
                     { ("owner", "TenantOwner", "Tenant"), ("cashier", "Cashier", "Branch"), ("cashier2", "Cashier", "Branch") })
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

    private async Task<T> Scalar<T>(string sql, params object[] args) =>
        await new PostgresUnitOfWork(NpgsqlDataSource.Create(Cs)).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(sql);
            foreach (object a in args)
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = a });
            }

            return (T)(await cmd.ExecuteScalarAsync(ct))!;
        });

    private HttpClient As(string user, bool onDevice = true)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(_u[user].Sub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, _tenant.ToString());
        if (onDevice)
        {
            c.DefaultRequestHeaders.Add(RequestContextMiddleware.DeviceHeader, _device);
        }

        return c;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    private static async Task<string?> Code(HttpResponseMessage r) => (await Body(r)).TryGetProperty("code", out JsonElement c) ? c.GetString() : null;

    private static async Task<HttpResponseMessage> Idem(HttpClient c, string path, object body, string? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await c.SendAsync(req);
    }

    /// <summary>A product at <paramref name="price"/> (10% tax) with <paramref name="stock"/> units at <paramref name="cost"/> in W1.</summary>
    private async Task<Guid> Product(string sku, string price, string stock, string cost)
    {
        HttpResponseMessage r = await Idem(As("owner", false), "/api/v1/products", new
        {
            name = "کالا " + sku, baseUnit = "EA", taxRate = 1000, confirmation = new { confirmed = true },
            openingStock = new { warehouseId = _w1, qty = stock, unitCost = cost },
            variants = new[] { new { sku, unitPrice = price, barcodes = new[] { new { code = sku, isInternal = true } } } },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Body(r)).GetProperty("variants")[0].GetProperty("id").GetGuid();
    }

    private async Task OpenShift(string user, Guid register) =>
        Assert.Equal(HttpStatusCode.Created, (await Idem(As(user), "/api/v1/shifts", new { registerId = register, openingCash = "0" })).StatusCode);

    /// <summary>A cart of <paramref name="qty"/> × <paramref name="sku"/>, paid in cash (handed over: the total rounded up to 50,000).</summary>
    private async Task<JsonElement> PaidCart(string user, Guid register, string sku, string qty)
    {
        HttpResponseMessage created = await As(user).PostAsJsonAsync("/api/v1/pos/sales", new { registerId = register, clientDraftId = "d-" + Guid.NewGuid().ToString("N") });
        Guid sale = (await Body(created)).GetProperty("id").GetGuid();
        JsonElement cart = await Body(await As(user).PostAsJsonAsync($"/api/v1/pos/sales/{sale}/lines", new { barcode = sku, qty }));
        string total = cart.GetProperty("totals").GetProperty("total").GetString()!;
        long handed = (long.Parse(total, CultureInfo.InvariantCulture) + 49_999) / 50_000 * 50_000;
        HttpResponseMessage paid = await Idem(As(user), $"/api/v1/pos/sales/{sale}/tenders", new { method = "Cash", amount = total, tendered = handed.ToString(CultureInfo.InvariantCulture) });
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        return await Body(paid);
    }

    private Task<HttpResponseMessage> Finalize(string user, JsonElement sale, string? key = null, string? total = null) =>
        Idem(As(user), $"/api/v1/sales/{sale.GetProperty("id").GetGuid()}/finalize",
            new { expectedVersion = sale.GetProperty("version").GetInt32(), expectedTotal = total ?? sale.GetProperty("totals").GetProperty("total").GetString() }, key);

    private async Task<Dictionary<string, (long Debit, long Credit)>> Entry(Guid saleId, string purpose)
    {
        JsonElement j = (await Body(await As("owner", false).GetAsync("/api/v1/accounting/journals?sourceType=Sale&limit=100"))).GetProperty("items").EnumerateArray()
            .Single(e => e.GetProperty("source").GetProperty("id").GetGuid() == saleId && e.GetProperty("source").GetProperty("purpose").GetString() == purpose);
        Assert.Equal("Posted", j.GetProperty("status").GetString());
        return j.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("accountCode").GetString()!,
            l => (long.Parse(l.GetProperty("debit").GetString()!, CultureInfo.InvariantCulture), long.Parse(l.GetProperty("credit").GetString()!, CultureInfo.InvariantCulture)));
    }

    [DbFact]
    public async Task AT213_a_finalized_sale_has_its_number_its_stock_issue_its_entries_and_its_snapshot_and_keeps_its_amounts()
    {
        Guid tea = await Product("FN-TEA", "100000", "10", "60000");
        await OpenShift("cashier", _r1);
        HttpResponseMessage created = await As("cashier").PostAsJsonAsync("/api/v1/pos/sales", new { registerId = _r1, clientDraftId = "d-" + Guid.NewGuid().ToString("N") });
        Guid saleId = (await Body(created)).GetProperty("id").GetGuid();
        await As("cashier").PostAsJsonAsync($"/api/v1/pos/sales/{saleId}/lines", new { barcode = "FN-TEA", qty = "3" });   // 330,000
        JsonElement part = await Body(await Idem(As("cashier"), $"/api/v1/pos/sales/{saleId}/tenders",
            new { method = "ExternalCardTerminal", amount = "100000", referenceNo = "990011223344" }));
        HttpResponseMessage incomplete = await Finalize("cashier", part);
        Assert.Equal(("payment-incomplete", "230000"), (await Code(incomplete), (await Body(incomplete)).GetProperty("context").GetProperty("remaining").GetString()));
        JsonElement sale = await Body(await Idem(As("cashier"), $"/api/v1/pos/sales/{saleId}/tenders", new { method = "Cash", amount = "230000", tendered = "250000" }));

        HttpResponseMessage done = await Finalize("cashier", sale);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        JsonElement f = await Body(done);
        int year = f.GetProperty("invoiceNo").GetString()!.Split('-')[1] is var y ? int.Parse(y, CultureInfo.InvariantCulture) : 0;
        Assert.Equal(("Finalized", $"B1-{year}-000001"), (f.GetProperty("status").GetString(), f.GetProperty("invoiceNo").GetString()));
        Assert.True(year >= 1405);

        // INV-006: 3 issued at WAC 60,000; SAL-005: the line keeps what it was, with its cost
        JsonElement b = (await Body(await As("owner", false).GetAsync($"/api/v1/inventory/balances?variantId={tea}"))).GetProperty("items")[0];
        Assert.Equal("7", b.GetProperty("onHand").GetString());
        Assert.Equal(180_000L, await Scalar<long>("SELECT cost_at_sale FROM sales.sale_line WHERE sale_id = $1", saleId));
        Assert.Equal("کالا FN-TEA", await Scalar<string>("SELECT snapshot ->> 'productName' FROM sales.sale_line WHERE sale_id = $1", saleId));

        // ACC-006: cash and card in, revenue and VAT out; COGS against inventory (AT-213: balanced)
        var sold = await Entry(saleId, "sale");
        Assert.Equal((230_000L, 0L), sold["1101"]);
        Assert.Equal((100_000L, 0L), sold["1104"]);
        Assert.Equal((0L, 300_000L), sold["4101"]);
        Assert.Equal((0L, 30_000L), sold["2103"]);
        var cost = await Entry(saleId, "sale-cost");
        Assert.Equal(((180_000L, 0L), (0L, 180_000L)), (cost["5101"], cost["1106"]));

        // SAL-006.1: the drawer expects what was applied in cash, not what was handed over
        Guid shift = f.GetProperty("shiftId").GetGuid();
        Assert.Equal(230_000L, await Scalar<long>("SELECT sales.shift_expected_cash($1)", shift));

        // BR-056: the next sale of the branch is the next number; AT-214: a later price does not touch a finalized sale
        Assert.Equal($"B1-{year}-000002", (await Body(await Finalize("cashier", await PaidCart("cashier", _r1, "FN-TEA", "1")))).GetProperty("invoiceNo").GetString());
        Guid list = (await Body(await As("owner", false).GetAsync("/api/v1/price-lists"))).GetProperty("items").EnumerateArray().Single(l => l.GetProperty("isDefault").GetBoolean())
            .GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await Idem(As("owner", false), $"/api/v1/price-lists/{list}/prices",
            new { items = new[] { new { variantId = tea, unitPrice = "120000" } }, validFrom = DateTimeOffset.UtcNow })).StatusCode);
        JsonElement again = await Body(await As("cashier").GetAsync($"/api/v1/pos/sales/{saleId}"));
        Assert.Equal(("330000", "100000"), (again.GetProperty("totals").GetProperty("total").GetString(), again.GetProperty("lines")[0].GetProperty("unitPrice").GetString()));
        Assert.Equal("sale-not-draft", await Code(await Idem(As("cashier"), $"/api/v1/pos/sales/{saleId}/tenders", new { method = "Cash", amount = "1", tendered = "1" })));
    }

    [DbFact]
    public async Task AT210_twenty_finalizes_with_one_key_make_one_invoice_one_issue_and_one_set_of_entries()
    {
        await Product("FN-OIL", "200000", "50", "120000");
        await OpenShift("cashier", _r1);
        JsonElement sale = await PaidCart("cashier", _r1, "FN-OIL", "2");
        string key = Guid.NewGuid().ToString("N");
        HttpResponseMessage[] all = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Finalize("cashier", sale, key)));
        var numbers = new HashSet<string>();
        foreach (HttpResponseMessage r in all)
        {
            if (r.StatusCode == HttpStatusCode.OK)
            {
                numbers.Add((await Body(r)).GetProperty("invoiceNo").GetString()!);
            }
            else
            {
                Assert.Equal("in-progress", await Code(r));   // still running: the client retries with the same key
            }
        }

        Assert.Single(numbers);
        Assert.Equal(numbers.Single(), (await Body(await Finalize("cashier", sale, key))).GetProperty("invoiceNo").GetString());
        Guid id = sale.GetProperty("id").GetGuid();
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM inv.stock_movement WHERE source_type = 'Sale' AND source_id = $1", id));
        Assert.Equal(2L, await Scalar<long>("SELECT count(*) FROM acc.journal_entry WHERE source_type = 'Sale' AND source_id = $1", id));
        Assert.Equal(2L, await Scalar<long>("SELECT next_value FROM sales.invoice_sequence WHERE branch_id = $1", _b1));
    }

    [DbFact]
    public async Task AT211_two_registers_selling_the_last_unit_at_once_one_sells_and_the_other_is_told_there_is_none()
    {
        await Product("FN-LAST", "500000", "1", "300000");
        await OpenShift("cashier", _r1);
        await OpenShift("cashier2", _r2);
        JsonElement a = await PaidCart("cashier", _r1, "FN-LAST", "1"), b = await PaidCart("cashier2", _r2, "FN-LAST", "1");
        HttpResponseMessage[] both = await Task.WhenAll(Finalize("cashier", a), Finalize("cashier2", b));
        Assert.Single(both, r => r.StatusCode == HttpStatusCode.OK);
        HttpResponseMessage refused = both.Single(r => r.StatusCode != HttpStatusCode.OK);
        Assert.Equal((HttpStatusCode.Conflict, "insufficient-stock"), (refused.StatusCode, await Code(refused)));
        // the refused sale is untouched: still a paid draft, no number used (BR-056: no gap)
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM sales.sale WHERE status = 'Finalized'"));
        Assert.Equal(2L, await Scalar<long>("SELECT next_value FROM sales.invoice_sequence WHERE branch_id = $1", _b1));
    }

    [DbFact]
    public async Task SAL005_1_a_price_that_changed_is_confirmed_again_at_the_first_payment_and_checked_again_at_finalize()
    {
        Guid soap = await Product("FN-SOAP", "100000", "20", "50000");
        await OpenShift("cashier", _r1);
        HttpResponseMessage created = await As("cashier").PostAsJsonAsync("/api/v1/pos/sales", new { registerId = _r1, clientDraftId = "d-" + Guid.NewGuid().ToString("N") });
        Guid saleId = (await Body(created)).GetProperty("id").GetGuid();
        await As("cashier").PostAsJsonAsync($"/api/v1/pos/sales/{saleId}/lines", new { barcode = "FN-SOAP", qty = "1" });   // shown: 110,000
        Guid list = (await Body(await As("owner", false).GetAsync("/api/v1/price-lists"))).GetProperty("items").EnumerateArray().Single(l => l.GetProperty("isDefault").GetBoolean())
            .GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await Idem(As("owner", false), $"/api/v1/price-lists/{list}/prices",
            new { items = new[] { new { variantId = soap, unitPrice = "120000" } }, validFrom = DateTimeOffset.UtcNow })).StatusCode);

        // the first payment finds the new price: refused, the cart now shows 132,000
        HttpResponseMessage first = await Idem(As("cashier"), $"/api/v1/pos/sales/{saleId}/tenders", new { method = "Cash", amount = "110000", tendered = "110000" });
        JsonElement problem = await Body(first);
        Assert.Equal(("price-changed", "132000"), (problem.GetProperty("code").GetString(), problem.GetProperty("context").GetProperty("total").GetString()));
        JsonElement cart = await Body(await As("cashier").GetAsync($"/api/v1/pos/sales/{saleId}"));
        Assert.Equal("132000", cart.GetProperty("totals").GetProperty("total").GetString());
        JsonElement paid = await Body(await Idem(As("cashier"), $"/api/v1/pos/sales/{saleId}/tenders", new { method = "Cash", amount = "132000", tendered = "150000" }));

        // what the cashier confirms must be the server's total; a stale version is refused
        Assert.Equal("price-changed", await Code(await Finalize("cashier", paid, total: "110000")));
        Assert.Equal("version-conflict", await Code(await Idem(As("cashier"), $"/api/v1/sales/{saleId}/finalize", new { expectedVersion = 1, expectedTotal = "132000" })));
        Assert.Equal(HttpStatusCode.NotFound, (await Finalize("cashier2", paid)).StatusCode);   // not their cart
        Assert.Equal(HttpStatusCode.OK, (await Finalize("cashier", paid)).StatusCode);

        // a price that moves after the payment is still caught at Finalize, even with the total the cart showed
        JsonElement later = await PaidCart("cashier", _r1, "FN-SOAP", "1");   // 132,000
        Assert.Equal(HttpStatusCode.NoContent, (await Idem(As("owner", false), $"/api/v1/price-lists/{list}/prices",
            new { items = new[] { new { variantId = soap, unitPrice = "125000" } }, validFrom = DateTimeOffset.UtcNow })).StatusCode);
        HttpResponseMessage moved = await Finalize("cashier", later);
        Assert.Equal(("price-changed", "137500"), (await Code(moved), (await Body(moved)).GetProperty("context").GetProperty("total").GetString()));
    }

    [DbFact]
    public async Task A_payment_taken_back_before_finalize_stops_counting_and_lets_the_bill_be_paid_at_the_new_price()
    {
        Guid gum = await Product("FN-GUM", "100000", "20", "40000");
        await OpenShift("cashier", _r1);
        HttpResponseMessage created = await As("cashier").PostAsJsonAsync("/api/v1/pos/sales", new { registerId = _r1, clientDraftId = "d-" + Guid.NewGuid().ToString("N") });
        Guid saleId = (await Body(created)).GetProperty("id").GetGuid();
        await As("cashier").PostAsJsonAsync($"/api/v1/pos/sales/{saleId}/lines", new { barcode = "FN-GUM", qty = "1" });   // 110,000
        await Idem(As("cashier"), $"/api/v1/pos/sales/{saleId}/tenders", new { method = "ExternalCardTerminal", amount = "10000", referenceNo = "123123123123" });
        JsonElement paid = await Body(await Idem(As("cashier"), $"/api/v1/pos/sales/{saleId}/tenders", new { method = "Cash", amount = "100000", tendered = "100000" }));

        // the price moves after payment: Finalize refuses; the payments are taken back (with a reason, by the cart's cashier)
        Guid list = (await Body(await As("owner", false).GetAsync("/api/v1/price-lists"))).GetProperty("items").EnumerateArray().Single(l => l.GetProperty("isDefault").GetBoolean())
            .GetProperty("id").GetGuid();
        await Idem(As("owner", false), $"/api/v1/price-lists/{list}/prices", new { items = new[] { new { variantId = gum, unitPrice = "120000" } }, validFrom = DateTimeOffset.UtcNow });
        Assert.Equal("price-changed", await Code(await Finalize("cashier", paid)));
        string path = $"/api/v1/pos/sales/{saleId}/tenders";
        int v = paid.GetProperty("version").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await Idem(As("cashier"), $"{path}/2/void", new { expectedVersion = v, reason = "" })).StatusCode);
        Assert.Equal("version-conflict", await Code(await Idem(As("cashier"), $"{path}/2/void", new { expectedVersion = v - 1, reason = "قیمت عوض شد" })));
        Assert.Equal(HttpStatusCode.NotFound, (await Idem(As("cashier2"), $"{path}/2/void", new { expectedVersion = v, reason = "قیمت عوض شد" })).StatusCode);
        JsonElement one = await Body(await Idem(As("cashier"), $"{path}/2/void", new { expectedVersion = v, reason = "قیمت عوض شد" }));
        Assert.Equal(("Voided", "10000"), (one.GetProperty("tenders")[1].GetProperty("status").GetString(), one.GetProperty("totals").GetProperty("paid").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await Idem(As("cashier"), $"{path}/2/void", new { expectedVersion = one.GetProperty("version").GetInt32(), reason = "دوباره" })).StatusCode);
        Assert.Equal("sale-has-payments", await Code(await As("cashier").PostAsJsonAsync($"/api/v1/pos/sales/{saleId}/lines", new { barcode = "FN-GUM", qty = "1" })));
        JsonElement none = await Body(await Idem(As("cashier"), $"{path}/1/void", new { expectedVersion = one.GetProperty("version").GetInt32(), reason = "قیمت عوض شد" }));
        Assert.Equal("0", none.GetProperty("totals").GetProperty("paid").GetString());

        // the slip's reference stays taken (AB-14); paying again reprices the bill first, then it is finalized at 132,000
        Assert.Equal("price-changed", await Code(await Idem(As("cashier"), path, new { method = "Cash", amount = "110000", tendered = "110000" })));
        Assert.Equal("duplicate-reference", await Code(await Idem(As("cashier"), path, new { method = "ExternalCardTerminal", amount = "10000", referenceNo = "123123123123" })));
        JsonElement repaid = await Body(await Idem(As("cashier"), path, new { method = "Cash", amount = "132000", tendered = "150000" }));
        JsonElement done = await Body(await Finalize("cashier", repaid));
        Assert.Equal(("Finalized", "132000", "18000"), (done.GetProperty("status").GetString(), done.GetProperty("totals").GetProperty("paid").GetString(),
            done.GetProperty("totals").GetProperty("change").GetString()));

        // the drawer and the entry count only what was kept: 132,000 cash, no card
        Assert.Equal(132_000L, await Scalar<long>("SELECT sales.shift_expected_cash($1)", done.GetProperty("shiftId").GetGuid()));
        var sold = await Entry(saleId, "sale");
        Assert.Equal((132_000L, false), (sold["1101"].Debit, sold.ContainsKey("1104")));
        Assert.Equal(2L, await Scalar<long>("SELECT count(*) FROM pay.payment WHERE sale_id = $1 AND status = 'Voided'", saleId));
        Assert.Equal(2L, await Scalar<long>("SELECT count(*) FROM audit.audit_record WHERE action = 'sales.tender.voided' AND resource_id = $1", saleId.ToString()));
    }
}
