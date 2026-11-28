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

/// <summary>US-105 / PAY-002.2 / AB-14 (AT-083): the bank's settlement file matched to the terminal's payments, J4 and the unreconciled report.</summary>
public sealed class SettlementTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _r1 = Guid.CreateVersion7(), _r2 = Guid.CreateVersion7(),
        _w1 = Guid.CreateVersion7();
    private readonly string _device = "dev-set-" + Guid.NewGuid();
    private readonly Dictionary<string, (Guid Id, string Sub)> _u = [];
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public SettlementTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach (string n in new[] { "owner", "cashier", "accountant" })
        {
            _u[n] = (Guid.CreateVersion7(), $"set-{n}-{Guid.NewGuid()}");
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, $4)", _u[n].Id, ApiFactory.Issuer, _u[n].Sub, n));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'تسویه', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک')", _tenant, _b1);
            await Exec(s, ct, "INSERT INTO platform.register (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'R1'), ($1, $4, $3, 'R2')", _tenant, _r1, _b1, _r2);
            await Exec(s, ct, "INSERT INTO inv.warehouse (tenant_id, id, branch_id, code, kind) VALUES ($1, $2, $3, 'W1', 'Sellable')", _tenant, _w1, _b1);
            await Exec(s, ct, "INSERT INTO platform.terminal (tenant_id, id, branch_id, device_name, secret_hash, paired_by) VALUES ($1, $2, $3, 'صندوق', $4, $5)",
                _tenant, Guid.CreateVersion7(), _b1, SHA256.HashData(Encoding.UTF8.GetBytes(_device)), _u["owner"].Id);
            foreach ((string n, string role, string scopeType) in new[]
                     { ("owner", "TenantOwner", "Tenant"), ("cashier", "Cashier", "Branch"), ("accountant", "Accountant", "Tenant") })
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

    private static string Ref() => Random.Shared.NextInt64(100_000_000_000, 999_999_999_999).ToString(CultureInfo.InvariantCulture);

    /// <summary>One unit (110,000 with tax) paid on the bank terminal with <paramref name="reference"/>; finalized unless told otherwise.</summary>
    private async Task<(Guid Sale, DateOnly Day)> CardSale(string reference, bool finalize = true)
    {
        HttpResponseMessage created = await As("cashier").PostAsJsonAsync("/api/v1/pos/sales", new { registerId = _r1, clientDraftId = "d-" + Guid.NewGuid().ToString("N") });
        Guid sale = (await Body(created)).GetProperty("id").GetGuid();
        await As("cashier").PostAsJsonAsync($"/api/v1/pos/sales/{sale}/lines", new { barcode = "ST-TEA", qty = "1" });
        HttpResponseMessage paid = await Idem(As("cashier"), $"/api/v1/pos/sales/{sale}/tenders",
            new { method = "ExternalCardTerminal", amount = "110000", referenceNo = reference, terminalNo = "8899" });
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        if (finalize)
        {
            Assert.Equal(HttpStatusCode.OK, (await Finalize("cashier", await Body(paid))).StatusCode);
        }

        return (sale, DateOnly.Parse(await Scalar<string>("SELECT business_date::text FROM pay.payment WHERE sale_id = $1", sale), CultureInfo.InvariantCulture));
    }

    private Task<HttpResponseMessage> Import(string fileName, string csv, string user = "accountant") =>
        Idem(As(user, false), "/api/v1/settlements/imports", new { fileName, content = csv });

    private bool _stocked;

    private async Task Ready()
    {
        if (!_stocked)
        {
            await Product("ST-TEA", "100000", "50", "60000");
            await OpenShift("cashier", _r1);
            _stocked = true;
        }
    }

    [DbFact]
    public async Task AT083_a_settlement_file_settles_matching_card_payments_posts_J4_and_leaves_the_rest_on_the_unreconciled_report()
    {
        await Ready();
        string r1 = Ref(), r2 = Ref(), r3 = Ref(), r9 = Ref();
        (Guid s1, DateOnly day) = await CardSale(r1);
        (Guid s2, _) = await CardSale(r2);
        (Guid s3, _) = await CardSale(r3, finalize: false);
        string d = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string csv = $"terminal_no,reference_no,transaction_date,amount,fee\n8899,{r1},{d},110000,1000\n8899,{r2},{d},109000,0\n8899,{r3},{d},110000,0\n" +
            $"8899,{r9},{d},5000,0\n8899,{r1},{d},110000,1000\n";

        HttpResponseMessage imported = await Import("settle-1.csv", csv);
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        JsonElement i = await Body(imported);
        Assert.Equal((5, 1, "110000", "1000", "109000"), (i.GetProperty("lineCount").GetInt32(), i.GetProperty("matchedCount").GetInt32(),
            i.GetProperty("matchedAmount").GetString(), i.GetProperty("fees").GetString(), i.GetProperty("deposited").GetString()));
        Assert.Equal(new[] { "Matched", "AmountMismatch", "Unfinalized", "NotFound", "DuplicateInFile" },
            i.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("outcome").GetString()));

        // D08 J4: Bank and the bank's fee against CardInTransit
        Guid journal = i.GetProperty("journalEntryId").GetGuid();
        JsonElement entry = (await Body(await As("accountant", false).GetAsync("/api/v1/accounting/journals?sourceType=SettlementImport&limit=50"))).GetProperty("items")
            .EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == journal);
        var lines = entry.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("accountCode").GetString()!,
            l => (l.GetProperty("debit").GetString(), l.GetProperty("credit").GetString()));
        Assert.Equal(("109000", "0"), lines["1102"]);
        Assert.Equal(("1000", "0"), lines["5106"]);
        Assert.Equal(("0", "110000"), lines["1104"]);

        // the same file again is refused; the import is kept with its lines and audited
        HttpResponseMessage again = await Import("copy.csv", csv);
        Assert.Equal((HttpStatusCode.Conflict, "already-imported"), (again.StatusCode, await Code(again)));
        Guid importId = i.GetProperty("id").GetGuid();
        Assert.Equal(importId, (await Body(again)).GetProperty("context").GetProperty("importId").GetGuid());
        Assert.Equal(5, (await Body(await As("accountant", false).GetAsync($"/api/v1/settlements/imports/{importId}"))).GetProperty("lines").GetArrayLength());
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM audit.audit_record WHERE action = 'payments.settlement.imported' AND resource_id = $1", importId.ToString()));

        // AB-14: the booked slip no file settled, per cashier; the unfinalized one is not booked yet
        JsonElement open = await Body(await As("accountant", false).GetAsync($"/api/v1/settlements/unreconciled-payments?from={d}&to={d}"));
        Assert.Equal(new[] { r2 }, open.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("referenceNo").GetString()));
        JsonElement cashier = Assert.Single(open.GetProperty("byCashier").EnumerateArray());
        Assert.Equal(("cashier", 1, "110000"), (cashier.GetProperty("displayName").GetString(), cashier.GetProperty("count").GetInt32(), cashier.GetProperty("amount").GetString()));
        Assert.Equal(s2, open.GetProperty("items")[0].GetProperty("saleId").GetGuid());

        // a later file settles the rest once the bill is finalized; what was settled stays settled
        JsonElement third = (await Body(await As("cashier").GetAsync($"/api/v1/pos/sales/{s3}")));
        Assert.Equal(HttpStatusCode.OK, (await Finalize("cashier", third)).StatusCode);
        JsonElement later = await Body(await Import("settle-2.csv", $"reference,date,amount\n{r1},{d},110000\n{r2},{d},110000\n{r3},{d},110000\n"));
        Assert.Equal(new[] { "AlreadySettled", "Matched", "Matched" }, later.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("outcome").GetString()));
        Assert.Equal(0, (await Body(await As("accountant", false).GetAsync($"/api/v1/settlements/unreconciled-payments?from={d}&to={d}"))).GetProperty("items").GetArrayLength());
        Assert.Equal(2, (await Body(await As("accountant", false).GetAsync("/api/v1/settlements/imports"))).GetProperty("items").GetArrayLength());
        _ = s1;
    }

    [DbFact]
    public async Task PAY002_2_a_bad_file_imports_nothing_and_only_the_accountant_side_may_import_or_read()
    {
        HttpResponseMessage bad = await Import("bad.csv", "reference,date,amount\n12,2026-11-24,abc\n");
        Assert.Equal((HttpStatusCode.BadRequest, "invalid-settlement-file"), (bad.StatusCode, await Code(bad)));
        Assert.Equal(new[] { "reference", "amount" }, (await Body(bad)).GetProperty("context").GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("field").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await Import("", "reference,date,amount\n")).StatusCode);

        string csv = $"reference,date,amount\n{Ref()},2026-11-24,1000\n";
        Assert.Equal(HttpStatusCode.Forbidden, (await Import("x.csv", csv, "cashier")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As("cashier", false).GetAsync("/api/v1/settlements/unreconciled-payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As("cashier", false).GetAsync("/api/v1/settlements/imports")).StatusCode);

        // nothing matched: imported, kept, no entry
        JsonElement none = await Body(await Import("none.csv", csv, "owner"));
        Assert.Equal((0, JsonValueKind.Null), (none.GetProperty("matchedCount").GetInt32(), none.GetProperty("journalEntryId").ValueKind));
        Assert.Equal(HttpStatusCode.NotFound, (await As("accountant", false).GetAsync($"/api/v1/settlements/imports/{Guid.CreateVersion7()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await As("accountant", false).GetAsync("/api/v1/settlements/unreconciled-payments?from=2026-01-01&to=2026-12-01")).StatusCode);
    }
}
