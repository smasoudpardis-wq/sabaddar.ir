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

/// <summary>US-082 / SAL-001 / SAL-010 (AT-068) / BR-013 / BR-016 / CAT-007: the POS cart priced on the server.</summary>
public sealed class SaleTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _r1 = Guid.CreateVersion7(), _r2 = Guid.CreateVersion7(),
        _terminal = Guid.CreateVersion7();
    private readonly string _device = "dev-sale-" + Guid.NewGuid();
    private readonly Dictionary<string, (Guid Id, string Sub)> _u = [];
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public SaleTests(ApiFactory factory) => _factory = factory;

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
            _u[n] = (Guid.CreateVersion7(), $"sale-{n}-{Guid.NewGuid()}");
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, $4)", _u[n].Id, ApiFactory.Issuer, _u[n].Sub, n));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'فروش', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک')", _tenant, _b1);
            await Exec(s, ct, "INSERT INTO platform.register (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'R1'), ($1, $4, $3, 'R2')", _tenant, _r1, _b1, _r2);
            await Exec(s, ct, "INSERT INTO platform.terminal (tenant_id, id, branch_id, device_name, secret_hash, paired_by) VALUES ($1, $2, $3, 'صندوق', $4, $5)",
                _tenant, _terminal, _b1, SHA256.HashData(Encoding.UTF8.GetBytes(_device)), _u["owner"].Id);
            foreach ((string n, string role, string scopeType) in new[]
                     { ("owner", "TenantOwner", "Tenant"), ("cashier", "Cashier", "Branch"), ("cashier2", "Cashier", "Branch") })
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

    private static async Task<string?> Code(HttpResponseMessage r) =>
        (await Body(r)).TryGetProperty("code", out JsonElement c) ? c.GetString() : null;

    private static async Task<HttpResponseMessage> Idem(HttpClient c, string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await c.SendAsync(req);
    }

    /// <summary>A product with one variant at <paramref name="price"/> rials per base unit and its tax rate; barcodes as given.</summary>
    private async Task<Guid> Product(string sku, string? price, int taxRate, params object[] barcodes)
    {
        HttpResponseMessage r = await Idem(As("owner", false), "/api/v1/products", new
        {
            name = "کالا " + sku, baseUnit = "EA", taxRate, confirmation = new { confirmed = true }, variants = new[] { new { sku, unitPrice = price, barcodes } },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Body(r)).GetProperty("id").GetGuid();
    }

    private async Task OpenShift(string user, Guid register) =>
        Assert.Equal(HttpStatusCode.Created, (await Idem(As(user), "/api/v1/shifts", new { registerId = register, openingCash = "0" })).StatusCode);

    private async Task<JsonElement> Cart(string user, Guid register)
    {
        HttpResponseMessage r = await As(user).PostAsJsonAsync("/api/v1/pos/sales", new { registerId = register, clientDraftId = "d-" + Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await Body(r);
    }

    private async Task<HttpResponseMessage> Scan(Guid sale, string barcode, string qty = "1", string user = "cashier") =>
        await As(user).PostAsJsonAsync($"/api/v1/pos/sales/{sale}/lines", new { barcode, qty });

    private static (string Gross, string Discount, string Net, string Tax, string Total) Totals(JsonElement sale)
    {
        JsonElement t = sale.GetProperty("totals");
        return (t.GetProperty("gross").GetString()!, t.GetProperty("discount").GetString()!, t.GetProperty("net").GetString()!, t.GetProperty("tax").GetString()!,
            t.GetProperty("total").GetString()!);
    }

    [DbFact]
    public async Task AT068_a_cart_carries_its_cashier_branch_register_shift_device_and_channel_and_its_draft_id_once()
    {
        // no device, then no open shift: refused (ABAC-04, BR-050)
        string draftId = "draft-at068-" + Guid.NewGuid().ToString("N")[..8];
        object body = new { registerId = _r1, clientDraftId = draftId };
        Assert.Equal("terminal-not-registered", await Code(await As("cashier", onDevice: false).PostAsJsonAsync("/api/v1/pos/sales", body)));
        Assert.Equal("shift-not-open", await Code(await As("cashier").PostAsJsonAsync("/api/v1/pos/sales", body)));

        await OpenShift("cashier", _r1);
        JsonElement context = await Body(await As("cashier").GetAsync("/api/v1/pos/context"));
        JsonElement r1 = context.GetProperty("registers").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == _r1);
        Assert.True(r1.GetProperty("shift").GetProperty("mine").GetBoolean());
        Assert.Equal(_terminal, context.GetProperty("terminalId").GetGuid());

        HttpResponseMessage created = await As("cashier").PostAsJsonAsync("/api/v1/pos/sales", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonElement sale = await Body(created);
        Assert.Equal(("Draft", "POS", _b1, _r1, _u["cashier"].Id, _terminal, "TaxExclusive"), (sale.GetProperty("status").GetString(), sale.GetProperty("channel").GetString(),
            sale.GetProperty("branchId").GetGuid(), sale.GetProperty("registerId").GetGuid(), sale.GetProperty("cashierId").GetGuid(),
            sale.GetProperty("terminalId").GetGuid(), sale.GetProperty("pricingMode").GetString()));
        Assert.Equal(r1.GetProperty("shift").GetProperty("id").GetGuid(), sale.GetProperty("shiftId").GetGuid());

        // DI-010: the same draft id again (an offline retry) is the same cart; another cashier cannot take it
        HttpResponseMessage again = await As("cashier").PostAsJsonAsync("/api/v1/pos/sales", body);
        Assert.Equal((HttpStatusCode.OK, sale.GetProperty("id").GetGuid()), (again.StatusCode, (await Body(again)).GetProperty("id").GetGuid()));
        await OpenShift("cashier2", _r2);
        Assert.Equal("client-draft-id-taken", await Code(await As("cashier2").PostAsJsonAsync("/api/v1/pos/sales", new { registerId = _r2, clientDraftId = draftId })));
        Assert.Equal("shift-not-open", await Code(await As("cashier2").PostAsJsonAsync("/api/v1/pos/sales", new { registerId = _r1, clientDraftId = "draft-other-0001" })));
    }

    [DbFact]
    public async Task AT071_a_cart_built_offline_syncs_once_with_its_lines_at_server_prices_and_reports_what_it_could_not_add()
    {
        await OpenShift("cashier", _r1);
        await Product("SL-OFF", "50000", 0, new { code = "SL-OFF", isInternal = true });
        object offline = new
        {
            registerId = _r1, clientDraftId = "offline-" + Guid.NewGuid().ToString("N")[..12],
            lines = new object[] { new { barcode = "SL-OFF", qty = "2" }, new { barcode = "SL-NONE", qty = "1" }, new { barcode = "SL-OFF", qty = "1" } },
        };

        HttpResponseMessage first = await As("cashier").PostAsJsonAsync("/api/v1/pos/sales", offline);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        JsonElement synced = await Body(first);
        JsonElement line = synced.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal(("3", "50000"), (line.GetProperty("qty").GetString(), line.GetProperty("unitPrice").GetString()));   // the server's price, added up
        JsonElement rejected = synced.GetProperty("rejectedLines").EnumerateArray().Single();
        Assert.Equal((1, "barcode-not-found"), (rejected.GetProperty("index").GetInt32(), rejected.GetProperty("code").GetString()));
        Assert.Contains("offline-lines-rejected", synced.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));

        // the connection dropped before the answer arrived and the device sends the same cart again: nothing is added twice
        HttpResponseMessage again = await As("cashier").PostAsJsonAsync("/api/v1/pos/sales", offline);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        JsonElement same = await Body(again);
        Assert.Equal(synced.GetProperty("id").GetGuid(), same.GetProperty("id").GetGuid());
        Assert.Equal("3", same.GetProperty("lines").EnumerateArray().Single().GetProperty("qty").GetString());
        Assert.Equal("150000", same.GetProperty("totals").GetProperty("total").GetString());

        HttpResponseMessage tooMany = await As("cashier").PostAsJsonAsync("/api/v1/pos/sales", new
        {
            registerId = _r1, clientDraftId = "offline-too-many-lines",
            lines = Enumerable.Range(0, 301).Select(_ => new { barcode = "SL-OFF", qty = "1" }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [DbFact]
    public async Task BR013_lines_are_priced_on_the_server_a_rescan_adds_up_a_carton_is_its_own_line_and_the_largest_rule_applies()
    {
        await OpenShift("cashier", _r1);
        Guid tea = await Product("SL-TEA", "100000", 1000, new { code = "SL-TEA", isInternal = true },
            new { code = "SL-TEA-CT", packagingUnit = "CT", unitFactor = "24", isInternal = true });
        Guid sale = (await Cart("cashier", _r1)).GetProperty("id").GetGuid();

        JsonElement s = await Body(await Scan(sale, "SL-TEA", "2"));
        s = await Body(await Scan(sale, "SL-TEA"));
        JsonElement line = s.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal(("3", "EA", "100000", "330000"), (line.GetProperty("qty").GetString(), line.GetProperty("unit").GetString(),
            line.GetProperty("unitPrice").GetString(), line.GetProperty("lineTotal").GetString()));

        // a carton's barcode sells a carton: 24 × 100,000; with a 10% rule now in force on the product (D-18: the largest)
        HttpClient owner = As("owner", false);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/discount-rules",
            new { name = "۵٪", target = new { kind = "Product", id = tea }, percent = 500, validFrom = DateTimeOffset.UtcNow.AddMinutes(-1) })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/discount-rules",
            new { name = "۱۰٪", target = new { kind = "Product", id = tea }, percent = 1000, validFrom = DateTimeOffset.UtcNow.AddMinutes(-1) })).StatusCode);
        s = await Body(await Scan(sale, "SL-TEA-CT"));
        JsonElement carton = s.GetProperty("lines")[1];
        Assert.Equal(("CT", "24", "2400000", "240000", "Rule"), (carton.GetProperty("unit").GetString(), carton.GetProperty("unitFactor").GetString(),
            carton.GetProperty("unitPrice").GetString(), carton.GetProperty("discounts")[0].GetProperty("amount").GetString(),
            carton.GetProperty("discounts")[0].GetProperty("source").GetString()));
        // BR-013: (2,400,000 − 240,000) × 10% tax; BR-015: totals are sums of the rounded lines
        Assert.Equal(("2376000", "330000"), (carton.GetProperty("lineTotal").GetString(), s.GetProperty("lines")[0].GetProperty("lineTotal").GetString()));
        Assert.Equal(("2700000", "240000", "2460000", "246000", "2706000"), Totals(s));

        // BR-016: in a tax-inclusive store the price contains the tax: net = round(1,000,000 ÷ 1.09) = 917,431
        Guid juice = await Product("SL-JUICE", "1000000", 900, new { code = "SL-JUICE", isInternal = true });
        await new PostgresUnitOfWork(NpgsqlDataSource.Create(Cs)).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)),
            (x, ct) => Exec(x, ct, "UPDATE platform.tenant SET pricing_mode = 'TaxInclusive' WHERE id = $1", _tenant));
        Guid inclusive = (await Cart("cashier", _r1)).GetProperty("id").GetGuid();
        JsonElement j = (await Body(await Scan(inclusive, "SL-JUICE"))).GetProperty("lines")[0];
        Assert.Equal(("917431", "82569", "1000000"), (j.GetProperty("net").GetString(), j.GetProperty("tax").GetString(), j.GetProperty("lineTotal").GetString()));
        await new PostgresUnitOfWork(NpgsqlDataSource.Create(Cs)).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)),
            (x, ct) => Exec(x, ct, "UPDATE platform.tenant SET pricing_mode = 'TaxExclusive' WHERE id = $1", _tenant));
        Assert.NotEqual(Guid.Empty, juice);
    }

    [DbFact]
    public async Task Quantity_and_unit_change_with_the_carts_version_a_weighed_line_keeps_its_label_and_a_removal_is_audited()
    {
        await OpenShift("cashier", _r1);
        await Product("SL-RICE", "50000", 0, new { code = "SL-RICE", isInternal = true }, new { code = "SL-RICE-PK", packagingUnit = "PK", unitFactor = "10", isInternal = true });
        await Product("SL-CHEESE", "800000", 0, new { code = "00777", isInternal = true });
        Assert.Equal(HttpStatusCode.OK, (await As("owner", false).PutAsJsonAsync("/api/v1/weighted-barcode-profiles", new
        {
            expectedVersion = 1,
            profiles = new[] { new { prefix = "21", itemCodeLength = 5, valueType = "Weight", valueLength = 5, decimals = 3, hasInnerCheckDigit = false } },
        })).StatusCode);
        Guid sale = (await Cart("cashier", _r1)).GetProperty("id").GetGuid();
        JsonElement s = await Body(await Scan(sale, "SL-RICE", "4"));

        // CAT-007: 1.250 kg at 800,000 — the label sets the quantity
        string label = "2100777012508";
        s = await Body(await Scan(sale, label, "9"));
        JsonElement weighed = s.GetProperty("lines")[1];
        Assert.Equal(("1.25", "1000000", true), (weighed.GetProperty("qty").GetString(), weighed.GetProperty("gross").GetString(), weighed.GetProperty("weighted").GetBoolean()));
        int version = s.GetProperty("version").GetInt32();
        string path = $"/api/v1/pos/sales/{sale}/lines";
        Assert.Equal("weighted-line", await Code(await As("cashier").PatchAsJsonAsync($"{path}/2", new { expectedVersion = version, qty = "2" })));

        // BR-034: a stale version changes nothing; the right one does, also to another unit
        Assert.Equal("version-conflict", await Code(await As("cashier").PatchAsJsonAsync($"{path}/1", new { expectedVersion = version - 1, qty = "6" })));
        s = await Body(await As("cashier").PatchAsJsonAsync($"{path}/1", new { expectedVersion = version, qty = "2", unit = "PK" }));
        Assert.Equal(("2", "PK", "1000000"), (s.GetProperty("lines")[0].GetProperty("qty").GetString(), s.GetProperty("lines")[0].GetProperty("unit").GetString(),
            s.GetProperty("lines")[0].GetProperty("lineTotal").GetString()));
        Assert.Equal("unknown-unit", await Code(await As("cashier").PatchAsJsonAsync($"{path}/1", new { expectedVersion = s.GetProperty("version").GetInt32(), unit = "CT" })));

        // only the cart's cashier works on it; a removal is audited (D-44)
        Assert.Equal(HttpStatusCode.NotFound, (await As("cashier2").DeleteAsync($"{path}/1?expectedVersion={s.GetProperty("version").GetInt32()}")).StatusCode);
        s = await Body(await As("cashier").DeleteAsync($"{path}/1?expectedVersion={s.GetProperty("version").GetInt32()}"));
        Assert.Equal(("1000000", 1), (Totals(s).Total, s.GetProperty("lines").GetArrayLength()));
        long audited = await new PostgresUnitOfWork(NpgsqlDataSource.Create(Cs)).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (x, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)x).Command("SELECT count(*) FROM audit.audit_record WHERE action = 'sales.line.removed' AND resource_id = $1");
            cmd.Parameters.Add(new NpgsqlParameter { Value = sale.ToString() });
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        });
        Assert.Equal(1, audited);
    }

    [DbFact]
    public async Task UC02_an_unknown_code_an_item_without_price_and_a_foreign_cart_are_refused()
    {
        await OpenShift("cashier", _r1);
        await Product("SL-NOPRICE", null, 0, new { code = "SL-NOPRICE", isInternal = true });
        Guid sale = (await Cart("cashier", _r1)).GetProperty("id").GetGuid();
        HttpResponseMessage unknown = await Scan(sale, "SL-NOTHING");
        Assert.Equal((HttpStatusCode.NotFound, "barcode-not-found"), (unknown.StatusCode, await Code(unknown)));
        Assert.Equal("invalid-barcode", await Code(await Scan(sale, "6260000000018")));   // wrong check digit
        Assert.Equal("price-missing", await Code(await Scan(sale, "SL-NOPRICE")));
        Assert.Equal(HttpStatusCode.NotFound, (await Scan(sale, "SL-NOPRICE", user: "cashier2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await As("cashier").PostAsJsonAsync($"/api/v1/pos/sales/{sale}/lines", new { barcode = "SL-NOPRICE", qty = "0" })).StatusCode);
        Assert.Equal("terminal-not-registered", await Code(await As("cashier", onDevice: false).PostAsJsonAsync($"/api/v1/pos/sales/{sale}/lines",
            new { barcode = "SL-NOPRICE", qty = "1" })));
    }

    [DbFact]
    public async Task Twelve_scans_at_once_on_one_cart_all_count_and_the_version_moves_with_each()
    {
        await OpenShift("cashier", _r1);
        await Product("SL-GUM", "20000", 0, new { code = "SL-GUM", isInternal = true });
        Guid sale = (await Cart("cashier", _r1)).GetProperty("id").GetGuid();
        HttpResponseMessage[] all = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Scan(sale, "SL-GUM")));
        Assert.All(all, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        JsonElement s = await Body(await As("cashier").GetAsync($"/api/v1/pos/sales/{sale}"));
        Assert.Equal(("12", "240000", 13), (s.GetProperty("lines").EnumerateArray().Single().GetProperty("qty").GetString(), Totals(s).Total,
            s.GetProperty("version").GetInt32()));
    }

    [DbFact]
    public async Task A_cart_is_locked_while_it_changes_so_a_second_change_sees_the_first()
    {
        await OpenShift("cashier", _r1);
        Guid sale = (await Cart("cashier", _r1)).GetProperty("id").GetGuid();
        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        var sales = new RetailCloud.Sales.Infrastructure.PostgresSales();
        var ctx = new FixedTenantContext(new TenantId(_tenant));
        var line = new RetailCloud.Sales.Application.SaleLineSpec(Guid.CreateVersion7(), "x", "X", 1, "EA", 1, null, 1000,
            new RetailCloud.Sales.Application.LineAmounts(1000, 0, 1000, 0, 1000), null, null, null, 0);
        var locked = new TaskCompletionSource();
        Task first = uow.ExecuteAsync(ctx, async (s, ct) =>
        {
            await sales.LockDraftAsync(s, sale, null, ct);
            locked.SetResult();
            await Task.Delay(500, ct);
            return await sales.WriteLineAsync(s, sale, null, line, ct);
        });
        await locked.Task;
        int seen = await uow.ExecuteAsync(ctx, async (s, ct) => (await sales.LockDraftAsync(s, sale, null, ct)).Sale!.Lines.Count);
        await first;
        Assert.Equal(1, seen);   // waited for the first change to commit, instead of reading the cart from before it
    }

    private async Task<HttpResponseMessage> Tender(Guid sale, object body, string? key = null, string user = "cashier")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/pos/sales/{sale}/tenders") { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await As(user).SendAsync(req);
    }

    [DbFact]
    public async Task AT080_AT066_a_bill_is_paid_by_card_and_cash_with_change_and_is_then_fixed()
    {
        await OpenShift("cashier", _r1);
        await Product("SL-OIL", "450000", 0, new { code = "SL-OIL", isInternal = true });
        Guid sale = (await Cart("cashier", _r1)).GetProperty("id").GetGuid();
        await Scan(sale, "SL-OIL", "2");   // 900,000

        // PAY-002: the card slip's reference confirms it (ConfirmedManual)
        HttpResponseMessage card = await Tender(sale, new { method = "ExternalCardTerminal", amount = "600000", referenceNo = "558812345678", terminalNo = "8899", cardLast4 = "1234" });
        Assert.Equal(HttpStatusCode.OK, card.StatusCode);
        Assert.Equal("300000", Totals2(await Body(card)).Remaining);
        Assert.Equal("amount-exceeds-remaining", await Code(await Tender(sale, new { method = "Cash", amount = "300001", tendered = "500000" })));

        // SAL-006.1: 500,000 handed over for 300,000: change 200,000; a retry with the same key takes nothing twice
        string key = Guid.NewGuid().ToString("N");
        JsonElement paid = await Body(await Tender(sale, new { method = "Cash", amount = "300000", tendered = "500000" }, key));
        Assert.Equal(("900000", "0", "200000"), Totals2(paid));
        Assert.Equal(2, (await Body(await Tender(sale, new { method = "Cash", amount = "300000", tendered = "500000" }, key))).GetProperty("tenders").GetArrayLength());
        JsonElement cash = paid.GetProperty("tenders")[1];
        Assert.Equal(("Cash", "300000", "500000", "200000"), (cash.GetProperty("method").GetString(), cash.GetProperty("amount").GetString(),
            cash.GetProperty("tendered").GetString(), cash.GetProperty("change").GetString()));
        Assert.Equal("558812345678", paid.GetProperty("tenders")[0].GetProperty("reference").GetString());

        // what was paid for does not change underneath the payments
        Assert.Equal("sale-has-payments", await Code(await Scan(sale, "SL-OIL")));
        Assert.Equal("sale-has-payments", await Code(await As("cashier").DeleteAsync($"/api/v1/pos/sales/{sale}/lines/1?expectedVersion={paid.GetProperty("version").GetInt32()}")));
        Assert.Equal(HttpStatusCode.BadRequest, (await Tender(sale, new { method = "Cash", amount = "1", tendered = "0" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Tender(sale, new { method = "CustomerCredit", amount = "1" })).StatusCode);
    }

    [DbFact]
    public async Task AT082_a_card_reference_is_taken_once_a_day_unless_a_manager_overrides_it()
    {
        await OpenShift("cashier", _r1);
        await Product("SL-SOAP", "100000", 0, new { code = "SL-SOAP", isInternal = true });
        Guid first = (await Cart("cashier", _r1)).GetProperty("id").GetGuid(), second = (await Cart("cashier", _r1)).GetProperty("id").GetGuid();
        await Scan(first, "SL-SOAP");
        await Scan(second, "SL-SOAP");
        object slip = new { method = "ExternalCardTerminal", amount = "100000", referenceNo = "777000111222", terminalNo = "41" };
        Assert.Equal(HttpStatusCode.OK, (await Tender(first, slip)).StatusCode);

        HttpResponseMessage again = await Tender(second, slip);
        JsonElement problem = await Body(again);
        Assert.Equal((HttpStatusCode.Conflict, "duplicate-reference"), (again.StatusCode, problem.GetProperty("code").GetString()));
        Guid approval = problem.GetProperty("context").GetProperty("approvalRequestId").GetGuid();
        // another terminal's slip with the same number is a different payment
        Assert.Equal(HttpStatusCode.OK, (await Tender(second, new { method = "ExternalCardTerminal", amount = "50000", referenceNo = "777000111222", terminalNo = "42" })).StatusCode);

        // not the cashier (a cashier cannot supervise), the owner may; the override fits this bill and amount only
        Assert.Equal(HttpStatusCode.NotFound, (await As("cashier2").PostAsJsonAsync($"/api/v1/approvals/{approval}/decision", new { decision = "Approve" })).StatusCode);   // not shown to who cannot decide
        Assert.Equal(HttpStatusCode.OK, (await As("owner", false).PostAsJsonAsync($"/api/v1/approvals/{approval}/decision", new { decision = "Approve" })).StatusCode);
        Assert.Equal("duplicate-reference", await Code(await Tender(second, new { method = "ExternalCardTerminal", amount = "50000", referenceNo = "777000111222", terminalNo = "41", approvalId = approval })));
        approval = (await Body(await Tender(second, new { method = "ExternalCardTerminal", amount = "50000", referenceNo = "777000111222", terminalNo = "41" })))
            .GetProperty("context").GetProperty("approvalRequestId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await As("owner", false).PostAsJsonAsync($"/api/v1/approvals/{approval}/decision", new { decision = "Approve" })).StatusCode);
        HttpResponseMessage allowed = await Tender(second, new { method = "ExternalCardTerminal", amount = "50000", referenceNo = "777000111222", terminalNo = "41", approvalId = approval });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("0", Totals2(await Body(allowed)).Remaining);

        long overrides = await new PostgresUnitOfWork(NpgsqlDataSource.Create(Cs)).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (x, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)x).Command(
                "SELECT count(*) FROM audit.audit_record WHERE action = 'payments.reference.override' AND resource_id = $1");
            cmd.Parameters.Add(new NpgsqlParameter { Value = second.ToString() });
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        });
        Assert.Equal(1, overrides);
    }

    private static (string Paid, string Remaining, string Change) Totals2(JsonElement sale)
    {
        JsonElement t = sale.GetProperty("totals");
        return (t.GetProperty("paid").GetString()!, t.GetProperty("remaining").GetString()!, t.GetProperty("change").GetString()!);
    }

    [DbFact]
    public async Task AT069_a_held_cart_is_resumed_on_another_register_of_the_branch_and_an_idle_one_is_cancelled()
    {
        await OpenShift("cashier", _r1);
        await OpenShift("cashier2", _r2);
        await Product("SL-BREAD", "30000", 0, new { code = "SL-BREAD", isInternal = true });
        Guid sale = (await Cart("cashier", _r1)).GetProperty("id").GetGuid();
        JsonElement s = await Body(await Scan(sale, "SL-BREAD", "2"));
        Assert.Equal("version-conflict", await Code(await As("cashier").PostAsJsonAsync($"/api/v1/pos/sales/{sale}/hold", new { expectedVersion = 1 })));
        JsonElement held = await Body(await As("cashier").PostAsJsonAsync($"/api/v1/pos/sales/{sale}/hold",
            new { expectedVersion = s.GetProperty("version").GetInt32(), note = "خانم احمدی" }));
        Assert.Equal(("Held", "خانم احمدی"), (held.GetProperty("status").GetString(), held.GetProperty("holdNote").GetString()));
        Assert.Equal("sale-not-draft", await Code(await Scan(sale, "SL-BREAD")));

        // the branch sees it; another cashier resumes it on their own register and carries on
        JsonElement list = (await Body(await As("cashier2").GetAsync("/api/v1/pos/held-sales"))).GetProperty("items");
        JsonElement mine = list.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == sale);
        Assert.Equal(("خانم احمدی", "60000"), (mine.GetProperty("holdNote").GetString(), mine.GetProperty("total").GetString()));
        Assert.Equal("shift-not-open", await Code(await As("cashier2").PostAsJsonAsync($"/api/v1/pos/sales/{sale}/resume",
            new { expectedVersion = held.GetProperty("version").GetInt32() })));   // the cart's own register R1 is not their shift
        JsonElement back = await Body(await As("cashier2").PostAsJsonAsync($"/api/v1/pos/sales/{sale}/resume",
            new { expectedVersion = held.GetProperty("version").GetInt32(), registerId = _r2 }));
        Assert.Equal(("Draft", _r2, _u["cashier2"].Id), (back.GetProperty("status").GetString(), back.GetProperty("registerId").GetGuid(), back.GetProperty("cashierId").GetGuid()));
        Assert.Equal("3", (await Body(await Scan(sale, "SL-BREAD", user: "cashier2"))).GetProperty("lines")[0].GetProperty("qty").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Scan(sale, "SL-BREAD")).StatusCode);   // no longer the first cashier's
        int now = (await Body(await As("cashier2").GetAsync($"/api/v1/pos/sales/{sale}"))).GetProperty("version").GetInt32();
        Assert.Equal("sale-not-held", await Code(await As("cashier2").PostAsJsonAsync($"/api/v1/pos/sales/{sale}/resume", new { expectedVersion = now, registerId = _r2 })));

        // SAL-011 [24h]: a cart left idle for a day is cancelled by the workers
        Guid idle = (await Cart("cashier", _r1)).GetProperty("id").GetGuid();
        await Scan(idle, "SL-BREAD");
        await new PostgresUnitOfWork(NpgsqlDataSource.Create(Cs)).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)),
            (x, ct) => Exec(x, ct, "UPDATE sales.sale SET last_activity_at = now() - interval '25 hours' WHERE id = $1", idle));
        await using var worker = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(Cs) { Username = "rc_worker_test", Password = "rc_worker_test" }.ToString());
        Assert.True(await new PostgresUnitOfWork(worker).ExecuteWithoutTenantAsync(null, async (x, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)x).Command("SELECT sales.expire_idle_carts()");
            return (int)(await cmd.ExecuteScalarAsync(ct))!;
        }) >= 1);
        JsonElement gone = await Body(await As("cashier").GetAsync($"/api/v1/pos/sales/{idle}"));
        Assert.Equal(("Cancelled", "auto-expired after inactivity"), (gone.GetProperty("status").GetString(), gone.GetProperty("cancelReason").GetString()));
        Assert.Equal("Draft", (await Body(await As("cashier2").GetAsync($"/api/v1/pos/sales/{sale}"))).GetProperty("status").GetString());   // active one untouched
    }

    [DbFact]
    public async Task A_cart_without_payments_is_cancelled_with_a_reason_by_its_cashier_or_a_supervisor()
    {
        await OpenShift("cashier", _r1);
        await Product("SL-JAM", "40000", 0, new { code = "SL-JAM", isInternal = true });
        Guid one = (await Cart("cashier", _r1)).GetProperty("id").GetGuid(), two = (await Cart("cashier", _r1)).GetProperty("id").GetGuid();
        JsonElement a = await Body(await Scan(one, "SL-JAM"));
        JsonElement b = await Body(await Scan(two, "SL-JAM"));
        Assert.Equal(HttpStatusCode.BadRequest, (await As("cashier").PostAsJsonAsync($"/api/v1/pos/sales/{one}/cancel", new { expectedVersion = a.GetProperty("version").GetInt32(), reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As("cashier2").PostAsJsonAsync($"/api/v1/pos/sales/{one}/cancel", new { expectedVersion = a.GetProperty("version").GetInt32(), reason = "مشتری پشیمان شد" })).StatusCode);
        JsonElement c = await Body(await As("cashier").PostAsJsonAsync($"/api/v1/pos/sales/{one}/cancel", new { expectedVersion = a.GetProperty("version").GetInt32(), reason = "مشتری پشیمان شد" }));
        Assert.Equal(("Cancelled", "مشتری پشیمان شد"), (c.GetProperty("status").GetString(), c.GetProperty("cancelReason").GetString()));
        Assert.Equal("sale-not-draft", await Code(await As("cashier").PostAsJsonAsync($"/api/v1/pos/sales/{one}/cancel", new { expectedVersion = c.GetProperty("version").GetInt32(), reason = "دوباره" })));

        // with a payment in force it is not cancelled (void first); a supervisor (the owner) may cancel the cashier's cart
        JsonElement paid = await Body(await Tender(two, new { method = "Cash", amount = "10000", tendered = "10000" }));
        Assert.Equal("sale-has-payments", await Code(await As("owner", false).PostAsJsonAsync($"/api/v1/pos/sales/{two}/cancel", new { expectedVersion = paid.GetProperty("version").GetInt32(), reason = "اشتباه" })));
        JsonElement voided = await Body(await Tender2Void(two, paid.GetProperty("version").GetInt32()));
        Assert.Equal(HttpStatusCode.OK, (await As("owner", false).PostAsJsonAsync($"/api/v1/pos/sales/{two}/cancel", new { expectedVersion = voided.GetProperty("version").GetInt32(), reason = "اشتباه صندوق‌دار" })).StatusCode);
        Assert.NotEqual(b.GetProperty("id").GetGuid(), Guid.Empty);
    }

    private async Task<HttpResponseMessage> Tender2Void(Guid sale, int version)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/pos/sales/{sale}/tenders/1/void") { Content = JsonContent.Create(new { expectedVersion = version, reason = "پس دادن پول" }) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await As("cashier").SendAsync(req);
    }
}
