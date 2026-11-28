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

/// <summary>US-161 / RPT-001..003 (AT-170, AT-171, AT-173, AT-174, AT-175): sales, stock and gross profit for the branches a role reaches, CSV with data.export.</summary>
public sealed class SalesReportTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _b2 = Guid.CreateVersion7(), _r1 = Guid.CreateVersion7(), _r2 = Guid.CreateVersion7(),
        _w1 = Guid.CreateVersion7(), _w2 = Guid.CreateVersion7();
    private readonly string _dev1 = "dev-rp1-" + Guid.NewGuid(), _dev2 = "dev-rp2-" + Guid.NewGuid();
    private readonly Dictionary<string, (Guid Id, string Sub)> _u = [];
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";
    private bool _sold;

    public SalesReportTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach (string n in new[] { "owner", "cashier1", "cashier2", "manager", "accountant" })
        {
            _u[n] = (Guid.CreateVersion7(), $"rpt-{n}-{Guid.NewGuid()}");
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, $4)", _u[n].Id, ApiFactory.Issuer, _u[n].Sub, n));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'گزارش فروش', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک'), ($1, $3, 'B2', 'دو')", _tenant, _b1, _b2);
            await Exec(s, ct, "INSERT INTO platform.register (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'R1'), ($1, $4, $5, 'R2')", _tenant, _r1, _b1, _r2, _b2);
            await Exec(s, ct, "INSERT INTO inv.warehouse (tenant_id, id, branch_id, code, kind) VALUES ($1, $2, $3, 'W1', 'Sellable'), ($1, $4, $5, 'W2', 'Sellable')",
                _tenant, _w1, _b1, _w2, _b2);
            await Exec(s, ct, "INSERT INTO platform.terminal (tenant_id, id, branch_id, device_name, secret_hash, paired_by) VALUES ($1, $2, $3, 'صندوق ۱', $4, $5), ($1, $6, $7, 'صندوق ۲', $8, $5)",
                _tenant, Guid.CreateVersion7(), _b1, SHA256.HashData(Encoding.UTF8.GetBytes(_dev1)), _u["owner"].Id, Guid.CreateVersion7(), _b2, SHA256.HashData(Encoding.UTF8.GetBytes(_dev2)));
            foreach ((string n, string role, string scopeType, Guid? scope) in new (string, string, string, Guid?)[]
                     { ("owner", "TenantOwner", "Tenant", null), ("cashier1", "Cashier", "Branch", _b1), ("cashier2", "Cashier", "Branch", _b2),
                       ("manager", "BranchManager", "Branch", _b1), ("accountant", "Accountant", "Tenant", null) })
            {
                await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _tenant, _u[n].Id);
                await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES ($1, $2, $3, $4, $5, $6)",
                    _tenant, Guid.CreateVersion7(), _u[n].Id, role, scopeType, (object?)scope ?? DBNull.Value);
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

    private HttpClient As(string user, string? device = null)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(_u[user].Sub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, _tenant.ToString());
        if (device is not null)
        {
            c.DefaultRequestHeaders.Add(RequestContextMiddleware.DeviceHeader, device);
        }

        return c;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    private static async Task<HttpResponseMessage> Idem(HttpClient c, string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await c.SendAsync(req);
    }

    private async Task Product(string sku, string price, string stock, string cost, Guid warehouse)
    {
        HttpResponseMessage r = await Idem(As("owner"), "/api/v1/products", new
        {
            name = "کالا " + sku, baseUnit = "EA", taxRate = 1000, confirmation = new { confirmed = true },
            openingStock = new { warehouseId = warehouse, qty = stock, unitCost = cost },
            variants = new[] { new { sku, unitPrice = price, barcodes = new[] { new { code = sku, isInternal = true } } } },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
    }

    /// <summary>One bill of <paramref name="qty"/> × <paramref name="sku"/>, paid in cash and finalized by <paramref name="user"/> on <paramref name="device"/>.</summary>
    private async Task Sell(string user, string device, Guid register, string sku, string qty)
    {
        HttpClient c = As(user, device);
        HttpResponseMessage created = await c.PostAsJsonAsync("/api/v1/pos/sales", new { registerId = register, clientDraftId = "d-" + Guid.NewGuid().ToString("N") });
        Guid sale = (await Body(created)).GetProperty("id").GetGuid();
        JsonElement cart = await Body(await c.PostAsJsonAsync($"/api/v1/pos/sales/{sale}/lines", new { barcode = sku, qty }));
        string total = cart.GetProperty("totals").GetProperty("total").GetString()!;
        long handed = (long.Parse(total, CultureInfo.InvariantCulture) + 49_999) / 50_000 * 50_000;
        JsonElement paid = await Body(await Idem(c, $"/api/v1/pos/sales/{sale}/tenders", new { method = "Cash", amount = total, tendered = handed.ToString(CultureInfo.InvariantCulture) }));
        HttpResponseMessage done = await Idem(c, $"/api/v1/sales/{sale}/finalize", new { expectedVersion = paid.GetProperty("version").GetInt32(), expectedTotal = total });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
    }

    /// <summary>Branch 1: 3 × A (net 300,000, tax 30,000, cost 180,000). Branch 2: 2 × B (net 100,000, tax 10,000, cost 60,000).</summary>
    private async Task Seed()
    {
        if (_sold)
        {
            return;
        }

        await Product("RP-A", "100000", "100", "60000", _w1);
        await Product("RP-B", "50000", "100", "30000", _w2);
        foreach ((string user, string device, Guid register) in new[] { ("cashier1", _dev1, _r1), ("cashier2", _dev2, _r2) })
        {
            Assert.Equal(HttpStatusCode.Created, (await Idem(As(user, device), "/api/v1/shifts", new { registerId = register, openingCash = "0" })).StatusCode);
        }

        await Sell("cashier1", _dev1, _r1, "RP-A", "3");
        await Sell("cashier2", _dev2, _r2, "RP-B", "2");
        _sold = true;
    }

    private static string Range()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return $"from={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddDays(1).ToString("O"))}";
    }

    private static string[] Fields(JsonElement row, params string[] names) => [.. names.Select(n => row.GetProperty(n).ToString())];

    [DbFact]
    public async Task AT170_daily_sales_by_branch_register_and_cashier_with_the_stores_day_and_a_Jalali_date()
    {
        await Seed();
        JsonElement report = await Body(await As("owner").GetAsync($"/api/v1/reports/sales-daily?{Range()}"));
        JsonElement[] rows = [.. report.GetProperty("items").EnumerateArray().OrderBy(r => r.GetProperty("net").GetString())];
        Assert.Equal(2, rows.Length);
        Assert.Equal(new[] { "1", "100000", "0", "10000", "100000" }, Fields(rows[0], "invoices", "gross", "discount", "tax", "net"));
        Assert.Equal(new[] { "1", "300000", "0", "30000", "300000" }, Fields(rows[1], "invoices", "gross", "discount", "tax", "net"));
        Assert.Equal(new[] { _b2, _b1 }, rows.Select(r => r.GetProperty("branchId").GetGuid()));
        Assert.All(rows, r => Assert.Equal(JsonValueKind.Null, r.GetProperty("registerId").ValueKind));   // not grouped by register or cashier
        var day = DateOnly.Parse(rows[0].GetProperty("businessDate").GetString()!, CultureInfo.InvariantCulture);
        Assert.True(Math.Abs(day.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber) <= 1);
        var p = new PersianCalendar();
        DateTime d = day.ToDateTime(TimeOnly.MinValue);
        Assert.Equal($"{p.GetYear(d):D4}-{p.GetMonth(d):D2}-{p.GetDayOfMonth(d):D2}", rows[0].GetProperty("jalaliDate").GetString());
        Assert.True(report.GetProperty("meta").TryGetProperty("dataAsOf", out _));

        JsonElement byCashier = await Body(await As("owner").GetAsync($"/api/v1/reports/sales-daily?{Range()}&groupBy=register,cashier"));
        JsonElement b1Row = byCashier.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("branchId").GetGuid() == _b1);
        Assert.Equal((_r1, _u["cashier1"].Id), (b1Row.GetProperty("registerId").GetGuid(), b1Row.GetProperty("cashierId").GetGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, (await As("owner").GetAsync($"/api/v1/reports/sales-daily?{Range()}&groupBy=color")).StatusCode);
    }

    [DbFact]
    public async Task RPT001_a_role_sees_only_the_branches_it_reaches_and_a_cashier_no_reports()
    {
        await Seed();
        JsonElement mine = await Body(await As("manager").GetAsync($"/api/v1/reports/sales-daily?{Range()}"));
        Assert.Equal(new[] { _b1 }, mine.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("branchId").GetGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, (await As("manager").GetAsync($"/api/v1/reports/sales-daily?{Range()}&branchId={_b2}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As("manager").GetAsync($"/api/v1/reports/sales-daily?{Range()}&branchId={Guid.CreateVersion7()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As("owner").GetAsync($"/api/v1/reports/sales-daily?{Range()}&branchId={_b2}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As("cashier1", _dev1).GetAsync($"/api/v1/reports/sales-daily?{Range()}")).StatusCode);

        string tooLong = $"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-400).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}";
        Assert.Equal(HttpStatusCode.BadRequest, (await As("owner").GetAsync($"/api/v1/reports/sales-daily?{tooLong}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await As("owner").GetAsync($"/api/v1/reports/sales-daily?{Range()}&format=xlsx")).StatusCode);
    }

    [DbFact]
    public async Task AT171_stock_with_its_value_per_warehouse_only_for_the_branches_in_reach()
    {
        await Seed();
        JsonElement all = await Body(await As("owner").GetAsync("/api/v1/reports/stock"));
        var rows = all.GetProperty("items").EnumerateArray().ToDictionary(r => r.GetProperty("sku").GetString()!);
        Assert.Equal(("97", "5820000", _w1), (rows["RP-A"].GetProperty("onHand").GetString(), rows["RP-A"].GetProperty("value").GetString(), rows["RP-A"].GetProperty("warehouseId").GetGuid()));
        Assert.Equal(("98", "2940000", _w2), (rows["RP-B"].GetProperty("onHand").GetString(), rows["RP-B"].GetProperty("value").GetString(), rows["RP-B"].GetProperty("warehouseId").GetGuid()));
        Assert.False(rows["RP-A"].GetProperty("belowReorderPoint").GetBoolean());

        JsonElement mine = await Body(await As("manager").GetAsync("/api/v1/reports/stock"));
        Assert.Equal(new[] { "RP-A" }, mine.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("sku").GetString()));
    }

    [DbFact]
    public async Task AT173_gross_profit_is_net_sales_less_the_cost_at_sale_by_day_branch_or_product()
    {
        await Seed();
        JsonElement day = Assert.Single((await Body(await As("owner").GetAsync($"/api/v1/reports/gross-profit?{Range()}"))).GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "400000", "240000", "160000", "4000", "MovingWeightedAverageAtSale", "0" },
            Fields(day, "netSales", "cost", "grossProfit", "marginBp", "costBasis", "itemsWithZeroCost"));

        JsonElement[] byBranch = [.. (await Body(await As("owner").GetAsync($"/api/v1/reports/gross-profit?{Range()}&groupBy=branch"))).GetProperty("items").EnumerateArray()];
        JsonElement one = byBranch.Single(r => r.GetProperty("key").GetString() == _b1.ToString());
        Assert.Equal(new[] { "300000", "180000", "120000", "4000" }, Fields(one, "netSales", "cost", "grossProfit", "marginBp"));

        JsonElement[] byProduct = [.. (await Body(await As("owner").GetAsync($"/api/v1/reports/gross-profit?{Range()}&groupBy=product"))).GetProperty("items").EnumerateArray()];
        Assert.Equal(2, byProduct.Length);
        Assert.Contains(byProduct, r => r.GetProperty("name").GetString()!.StartsWith("RP-A ", StringComparison.Ordinal) && r.GetProperty("grossProfit").GetString() == "120000");
        Assert.Equal(HttpStatusCode.BadRequest, (await As("owner").GetAsync($"/api/v1/reports/gross-profit?{Range()}&groupBy=color")).StatusCode);
    }

    [DbFact]
    public async Task RPT001_3_a_line_sold_with_no_known_cost_is_flagged_so_its_profit_is_not_trusted()
    {
        await Seed();
        await Product("RP-Z", "20000", "10", "0", _w1);
        await Sell("cashier1", _dev1, _r1, "RP-Z", "1");
        JsonElement[] byProduct = [.. (await Body(await As("owner").GetAsync($"/api/v1/reports/gross-profit?{Range()}&groupBy=product"))).GetProperty("items").EnumerateArray()];
        JsonElement z = byProduct.Single(r => r.GetProperty("name").GetString()!.StartsWith("RP-Z ", StringComparison.Ordinal));
        Assert.Equal(new[] { "20000", "0", "20000", "1" }, Fields(z, "netSales", "cost", "grossProfit", "itemsWithZeroCost"));
        Assert.All(byProduct.Where(r => r.GetProperty("key").GetString() != z.GetProperty("key").GetString()), r => Assert.Equal("0", r.GetProperty("itemsWithZeroCost").ToString()));
        JsonElement day = Assert.Single((await Body(await As("owner").GetAsync($"/api/v1/reports/gross-profit?{Range()}"))).GetProperty("items").EnumerateArray());
        Assert.Equal("1", day.GetProperty("itemsWithZeroCost").ToString());
    }

    [DbFact]
    public async Task AT175_csv_needs_data_export_opens_in_Excel_and_every_export_is_audited()
    {
        await Seed();
        Assert.Equal(HttpStatusCode.Forbidden, (await As("manager").GetAsync($"/api/v1/reports/sales-daily?{Range()}&format=csv")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As("manager").GetAsync($"/api/v1/reports/sales-daily?{Range()}")).StatusCode);   // viewing needs no export right

        HttpResponseMessage csv = await As("accountant").GetAsync($"/api/v1/reports/gross-profit?{Range()}&format=csv&groupBy=branch");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        byte[] bytes = await csv.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);   // the BOM Excel needs for Persian
        string[] lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Key,Name,NetSales,Cost,GrossProfit,MarginBp,CostBasis,ItemsWithZeroCost", lines[0]);
        Assert.Contains(lines, l => l.StartsWith($"{_b1},,300000,180000,120000,4000,", StringComparison.Ordinal));

        long audited = await new PostgresUnitOfWork(NpgsqlDataSource.Create(Cs)).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(
                "SELECT count(*) FROM audit.audit_record WHERE action = 'reports.exported' AND resource_id = 'gross-profit' AND details ->> 'rows' = '2'");
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        });
        Assert.Equal(1, audited);
    }

    [DbFact]
    public async Task AT174_a_report_never_waits_for_a_locked_sale()
    {
        await Seed();
        await new PostgresUnitOfWork(NpgsqlDataSource.Create(Cs)).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await using (NpgsqlCommand lockAll = ((PostgresUnitOfWorkScope)s).Command("SELECT id FROM sales.sale FOR UPDATE"))
            {
                await lockAll.ExecuteNonQueryAsync(ct);
            }

            Task<HttpResponseMessage> report = As("owner").GetAsync($"/api/v1/reports/sales-daily?{Range()}", ct);
            Assert.Same(report, await Task.WhenAny(report, Task.Delay(TimeSpan.FromSeconds(20), ct)));   // answered while the sales stay locked
            Assert.Equal(HttpStatusCode.OK, (await report).StatusCode);
            return 0;
        });
    }
}
