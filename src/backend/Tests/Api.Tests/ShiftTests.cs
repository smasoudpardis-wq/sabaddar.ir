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

/// <summary>US-081 / UC-06 / SAL-009 / AT-250, AT-251: shifts, cash in and out, blind close and the variance decision.</summary>
public sealed class ShiftTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _b2 = Guid.CreateVersion7(), _r1 = Guid.CreateVersion7(),
        _r2 = Guid.CreateVersion7(), _cashier = Guid.CreateVersion7(), _other = Guid.CreateVersion7(), _manager = Guid.CreateVersion7();
    private readonly string _cashierSub = "sh-cashier-" + Guid.NewGuid(), _otherSub = "sh-other-" + Guid.NewGuid(), _managerSub = "sh-manager-" + Guid.NewGuid();
    private readonly string _deviceB1 = "dev-b1-" + Guid.NewGuid(), _deviceB2 = "dev-b2-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public ShiftTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        await uow.ExecuteWithoutTenantAsync(null, async (s, ct) =>
        {
            foreach ((Guid id, string sub) in new[] { (_cashier, _cashierSub), (_other, _otherSub), (_manager, _managerSub) })
            {
                await Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'کاربر')", id, ApiFactory.Issuer, sub);
            }

            return 0;
        });
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'صندوق', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک'), ($1, $3, 'B2', 'دو')", _tenant, _b1, _b2);
            await Exec(s, ct, "INSERT INTO platform.register (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'R1'), ($1, $4, $5, 'R1')", _tenant, _r1, _b1, _r2, _b2);
            await Exec(s, ct, "INSERT INTO platform.terminal (tenant_id, id, branch_id, device_name, secret_hash, paired_by) VALUES ($1, $2, $3, 'صندوق ۱', $4, $5), ($1, $6, $7, 'صندوق ۲', $8, $5)",
                _tenant, Guid.CreateVersion7(), _b1, SHA256.HashData(Encoding.UTF8.GetBytes(_deviceB1)), _manager, Guid.CreateVersion7(), _b2,
                SHA256.HashData(Encoding.UTF8.GetBytes(_deviceB2)));
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2), ($1, $3), ($1, $4)", _tenant, _cashier, _other, _manager);
            await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES " +
                              "($1, gen_random_uuid(), $2, 'Cashier', 'Branch', $5), ($1, gen_random_uuid(), $3, 'Cashier', 'Branch', $5), ($1, gen_random_uuid(), $4, 'BranchManager', 'Branch', $5)",
                _tenant, _cashier, _other, _manager, _b1);
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

    private HttpClient As(string sub, string? device)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(sub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, _tenant.ToString());
        if (device is not null)
        {
            c.DefaultRequestHeaders.Add(RequestContextMiddleware.DeviceHeader, device);
        }

        return c;
    }

    private static async Task<HttpResponseMessage> Idem(HttpClient c, string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await c.SendAsync(req);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    private static async Task<string> Code(HttpResponseMessage r) => (await Body(r)).GetProperty("code").GetString()!;

    /// <summary>A finalised cash sale of the shift (the sales flow arrives with its own story; here written directly).</summary>
    private async Task CashSale(Guid shift, long amount)
    {
        string admin = new NpgsqlConnectionStringBuilder(Cs) { Username = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres",
            Password = Environment.GetEnvironmentVariable("PGPASSWORD") }.ToString();
        await using var ds = NpgsqlDataSource.Create(admin);
        Guid sale = Guid.CreateVersion7();
        await using NpgsqlBatch b = ds.CreateBatch();
        b.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO sales.sale (tenant_id, id, branch_id, register_id, shift_id, cashier_id, terminal_id, client_draft_id, pricing_mode, total) " +
                                                   "VALUES ($1, $2, $3, $4, $5, $6, gen_random_uuid(), $7, 'TaxExclusive', $8)")
        {
            Parameters = { new() { Value = _tenant }, new() { Value = sale }, new() { Value = _b1 }, new() { Value = _r1 }, new() { Value = shift },
                new() { Value = _cashier }, new() { Value = "d-" + sale }, new() { Value = amount } },
        });
        b.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO sales.sale_tender (tenant_id, sale_id, seq, method, amount, payment_id) VALUES ($1, $2, 1, 'Cash', $3, gen_random_uuid())")
        {
            Parameters = { new() { Value = _tenant }, new() { Value = sale }, new() { Value = amount } },
        });
        b.BatchCommands.Add(new NpgsqlBatchCommand("UPDATE sales.sale SET status = 'Finalized', invoice_no = $2, finalized_at = now(), fiscal_year = 1405 WHERE id = $1")
        {
            Parameters = { new() { Value = sale }, new() { Value = "B1-1405-" + sale.ToString("N")[..6] } },
        });
        await b.ExecuteNonQueryAsync();
    }

    [DbFact]
    public async Task AT_250_the_count_against_opening_cash_sales_and_cash_out_gives_the_variance_blind()
    {
        HttpClient cashier = As(_cashierSub, _deviceB1);
        HttpResponseMessage opened = await Idem(cashier, "/api/v1/shifts", new { registerId = _r1, openingCash = "1000000" });
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        JsonElement shift = await Body(opened);
        Guid id = shift.GetProperty("id").GetGuid();
        Assert.Equal(_b1, shift.GetProperty("branchId").GetGuid());

        await CashSale(id, 5_000_000);
        Assert.Equal(HttpStatusCode.Created, (await Idem(cashier, $"/api/v1/shifts/{id}/cash-movements", new { direction = "Out", amount = "200000", reason = "بازپرداخت نقدی" })).StatusCode);

        // blind close (D-24): before the count the cashier does not see what the drawer should hold
        Assert.Equal(JsonValueKind.Null, (await Body(await cashier.GetAsync($"/api/v1/shifts/{id}"))).GetProperty("expectedCash").ValueKind);

        HttpResponseMessage closed = await Idem(cashier, $"/api/v1/shifts/{id}/close", new
        {
            countedCash = "5700000", expectedVersion = 1,
            denominations = new[] { new { value = "1000000", count = 5 }, new { value = "500000", count = 1 }, new { value = "100000", count = 2 } },
        });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        JsonElement c = await Body(closed);
        Assert.Equal(("5800000", "-100000", "None"), (c.GetProperty("expectedCash").GetString(), c.GetProperty("variance").GetString(), c.GetProperty("varianceStatus").GetString()));

        Assert.Equal("shift-not-open", await Code(await Idem(cashier, $"/api/v1/shifts/{id}/cash-movements", new { direction = "In", amount = "1", reason = "پس از بستن" })));
        Assert.Equal("shift-not-open", await Code(await Idem(cashier, $"/api/v1/shifts/{id}/close", new { countedCash = "0", expectedVersion = 2 })));
    }

    [DbFact]
    public async Task AT_251_one_open_shift_per_register_on_a_device_of_its_branch()
    {
        HttpClient cashier = As(_cashierSub, _deviceB1);
        Assert.Equal("terminal-not-registered", await Code(await Idem(As(_cashierSub, null), "/api/v1/shifts", new { registerId = _r1, openingCash = "0" })));
        Assert.Equal("terminal-not-registered", await Code(await Idem(As(_cashierSub, _deviceB2), "/api/v1/shifts", new { registerId = _r1, openingCash = "0" })));
        Assert.Equal("forbidden", await Code(await Idem(As(_cashierSub, _deviceB2), "/api/v1/shifts", new { registerId = _r2, openingCash = "0" })));   // not their branch

        Assert.Equal(HttpStatusCode.Created, (await Idem(cashier, "/api/v1/shifts", new { registerId = _r1, openingCash = "0" })).StatusCode);
        Assert.Equal("shift-already-open", await Code(await Idem(As(_otherSub, _deviceB1), "/api/v1/shifts", new { registerId = _r1, openingCash = "0" })));
        Assert.Equal("validation", await Code(await Idem(cashier, "/api/v1/shifts", new { registerId = _r1, openingCash = "-5" })));
    }

    [DbFact]
    public async Task A_large_variance_waits_for_a_supervisor_who_is_not_the_cashier()
    {
        HttpClient cashier = As(_cashierSub, _deviceB1);
        Guid id = (await Body(await Idem(cashier, "/api/v1/shifts", new { registerId = _r1, openingCash = "1000000" }))).GetProperty("id").GetGuid();

        // another cashier of the branch cannot see or close it; the branch manager can see it
        Assert.Equal(HttpStatusCode.NotFound, (await As(_otherSub, _deviceB1).GetAsync($"/api/v1/shifts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(_managerSub, null).GetAsync($"/api/v1/shifts/{id}")).StatusCode);
        Assert.Equal("version-conflict", await Code(await Idem(cashier, $"/api/v1/shifts/{id}/close", new { countedCash = "0", expectedVersion = 7 })));
        Assert.Equal("validation", await Code(await Idem(cashier, $"/api/v1/shifts/{id}/close",
            new { countedCash = "300000", expectedVersion = 1, denominations = new[] { new { value = "100000", count = 2 } } })));   // adds up to 200 000

        JsonElement closed = await Body(await Idem(cashier, $"/api/v1/shifts/{id}/close", new { countedCash = "300000", expectedVersion = 1, varianceReason = "نامشخص" }));
        Assert.Equal(("-700000", "PendingApproval"), (closed.GetProperty("variance").GetString(), closed.GetProperty("varianceStatus").GetString()));

        Assert.Equal("forbidden", await Code(await cashier.PostAsJsonAsync($"/api/v1/shifts/{id}/variance-approval", new { decision = "Approve" })));
        JsonElement approved = await Body(await As(_managerSub, null).PostAsJsonAsync($"/api/v1/shifts/{id}/variance-approval", new { decision = "Approve", comment = "بررسی شد" }));
        Assert.Equal("Approved", approved.GetProperty("varianceStatus").GetString());
        Assert.Equal("variance-not-pending", await Code(await As(_managerSub, null).PostAsJsonAsync($"/api/v1/shifts/{id}/variance-approval", new { decision = "Reject" })));
    }
}
