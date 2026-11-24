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

/// <summary>US-065 / INV-009 / UC-09 (AT-280) / SoD-03 / BR-033: a stock count with a snapshot, several counters and its correction.</summary>
public sealed class CountTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _w1 = Guid.CreateVersion7();
    private readonly Dictionary<string, (Guid Id, string Sub)> _u = [];
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public CountTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach (string n in new[] { "owner", "manager", "clerk", "clerk2", "cashier" })
        {
            _u[n] = (Guid.CreateVersion7(), $"cnt-{n}-{Guid.NewGuid()}");
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, $4)", _u[n].Id, ApiFactory.Issuer, _u[n].Sub, n));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'شمارش', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک')", _tenant, _b1);
            await Exec(s, ct, "INSERT INTO inv.warehouse (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'W1')", _tenant, _w1, _b1);
            foreach ((string n, string role, string scopeType) in new[]
                     {
                         ("owner", "TenantOwner", "Tenant"), ("manager", "BranchManager", "Branch"), ("clerk", "WarehouseClerk", "Branch"),
                         ("clerk2", "WarehouseClerk", "Branch"), ("cashier", "Cashier", "Branch"),
                     })
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

    private static async Task<string?> Code(HttpResponseMessage r) => (await Body(r)).TryGetProperty("code", out JsonElement c) ? c.GetString() : null;

    private async Task<HttpResponseMessage> Post(string user, string path, object body, Guid? approval = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        if (approval is { } a)
        {
            req.Headers.Add("X-Approval-Id", a.ToString());
        }

        return await As(user).SendAsync(req);
    }

    private async Task<Guid> Stocked(string sku, string qty, string cost)
    {
        HttpResponseMessage r = await Post("owner", "/api/v1/products", new
        {
            name = "کالا " + sku, baseUnit = "EA", confirmation = new { confirmed = true }, openingStock = new { warehouseId = _w1, qty, unitCost = cost },
            variants = new[] { new { sku } },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Body(r)).GetProperty("variants")[0].GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> Count(string user, Guid count, string batch, params (Guid Variant, string Qty)[] entries) =>
        As(user).PostAsJsonAsync($"/api/v1/inventory/counts/{count}/entries", new { clientBatchId = batch, entries = entries.Select(e => new { variantId = e.Variant, qty = e.Qty }).ToArray() });

    private async Task<string> OnHand(Guid variant) =>
        (await Body(await As("owner").GetAsync($"/api/v1/inventory/balances?variantId={variant}"))).GetProperty("items")[0].GetProperty("onHand").GetString()!;

    private async Task<JsonElement> Close(string user, JsonElement count)
    {
        HttpResponseMessage r = await As(user).PostAsJsonAsync($"/api/v1/inventory/counts/{count.GetProperty("id").GetGuid()}/close",
            new { expectedVersion = count.GetProperty("version").GetInt32() });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Body(r);
    }

    [DbFact]
    public async Task AT280_two_counters_and_a_sale_while_counting_leave_no_difference()
    {
        Guid rice = await Stocked("CN-RICE", "10", "50000");
        HttpResponseMessage opened = await Post("clerk", "/api/v1/inventory/counts", new { warehouseId = _w1, blind = true });
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        JsonElement count = await Body(opened);
        Guid id = count.GetProperty("id").GetGuid();
        Assert.Equal("count-open", await Code(await Post("clerk2", "/api/v1/inventory/counts", new { warehouseId = _w1 })));   // one at a time

        // two people count at once; an offline batch synced twice counts once
        Assert.Equal(HttpStatusCode.NoContent, (await Count("clerk", id, "batch-clerk-1", (rice, "5"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Count("clerk", id, "batch-clerk-1", (rice, "5"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Count("clerk2", id, "batch-clerk2-1", (rice, "4"), (rice, "-1"))).StatusCode);   // a wrong scan corrected

        // 2 are sold while counting (a movement after the snapshot)
        Assert.Equal(HttpStatusCode.Created, (await Post("owner", "/api/v1/inventory/adjustments",
            new { warehouseId = _w1, reason = "Damage", lines = new[] { new { variantId = rice, qtyDelta = "-2" } } })).StatusCode);

        // blind: the counters do not see the system quantity while counting; who confirms does
        Assert.Equal(HttpStatusCode.Forbidden, (await As("clerk").GetAsync($"/api/v1/inventory/counts/{id}/result")).StatusCode);
        JsonElement closed = await Close("clerk", count);
        Assert.Equal(("Closed", 2), (closed.GetProperty("status").GetString(), closed.GetProperty("counters").GetInt32()));
        Assert.Equal("count-not-open", await Code(await Count("clerk", id, "batch-late-01", (rice, "1"))));

        JsonElement line = (await Body(await As("manager").GetAsync($"/api/v1/inventory/counts/{id}/result"))).GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("variantId").GetGuid() == rice);
        Assert.Equal(("10", "-2", "8", "0"), (line.GetProperty("systemAtSnapshot").GetString(), line.GetProperty("movementsSince").GetString(),
            line.GetProperty("counted").GetString(), line.GetProperty("difference").GetString()));

        // nothing differs: confirmed without any correction
        JsonElement confirmed = await Body(await Post("manager", $"/api/v1/inventory/counts/{id}/confirm", new { expectedVersion = closed.GetProperty("version").GetInt32() }));
        Assert.Equal(("Confirmed", JsonValueKind.Null), (confirmed.GetProperty("count").GetProperty("status").GetString(), confirmed.GetProperty("adjustmentId").ValueKind));
        Assert.Equal("8", await OnHand(rice));
        Assert.Equal(HttpStatusCode.Created, (await Post("clerk", "/api/v1/inventory/counts", new { warehouseId = _w1 })).StatusCode);   // a new count may start
    }

    [DbFact]
    public async Task SoD03_who_counted_needs_another_managers_approval_and_the_correction_posts_its_entry()
    {
        Guid oil = await Stocked("CN-OIL", "6", "100000");
        Guid salt = await Stocked("CN-SALT", "2", "20000");   // in the warehouse but not counted: none was found
        JsonElement count = await Body(await Post("manager", "/api/v1/inventory/counts", new { warehouseId = _w1, blind = false }));
        Guid id = count.GetProperty("id").GetGuid();
        await Count("clerk", id, "batch-oil-001", (oil, "4"));
        await Count("manager", id, "batch-oil-002", (oil, "1"));
        JsonElement closed = await Close("manager", count);

        JsonElement result = await Body(await As("manager").GetAsync($"/api/v1/inventory/counts/{id}/result"));
        Assert.Equal(("-140000", true), (result.GetProperty("totalValueDifference").GetString(), result.GetProperty("approvalRequired").GetBoolean()));

        // the manager counted too: an approval from someone else (SoD-03) — then oil −1 and salt −2 are corrected
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("clerk", $"/api/v1/inventory/counts/{id}/confirm", new { expectedVersion = closed.GetProperty("version").GetInt32() })).StatusCode);
        HttpResponseMessage asked = await Post("manager", $"/api/v1/inventory/counts/{id}/confirm", new { expectedVersion = closed.GetProperty("version").GetInt32() });
        JsonElement problem = await Body(asked);
        Assert.Equal(("approval-required", "sod-03"), (problem.GetProperty("code").GetString(), problem.GetProperty("context").GetProperty("condition").GetString()));
        Guid approval = problem.GetProperty("approvalRequestId").GetGuid();
        Assert.Equal("6", await OnHand(oil));   // nothing moved meanwhile
        Assert.Equal(HttpStatusCode.OK, (await As("owner").PostAsJsonAsync($"/api/v1/approvals/{approval}/decision", new { decision = "Approve" })).StatusCode);
        JsonElement done = await Body(await Post("manager", $"/api/v1/inventory/counts/{id}/confirm", new { expectedVersion = closed.GetProperty("version").GetInt32() }, approval));
        Guid adjustment = done.GetProperty("adjustmentId").GetGuid();
        Assert.Equal(("5", "0"), (await OnHand(oil), await OnHand(salt)));

        // the correction is a CountAdjustment with its shrinkage entry (D02 §5.8.1) and is listed with the adjustments (AB-05)
        JsonElement card = (await Body(await As("owner").GetAsync($"/api/v1/inventory/movements?variantId={oil}"))).GetProperty("items")[0];
        Assert.Equal(("CountAdjustment", "-1"), (card.GetProperty("type").GetString(), card.GetProperty("qtyBase").GetString()));
        JsonElement adj = (await Body(await As("owner").GetAsync("/api/v1/inventory/adjustments"))).GetProperty("items").EnumerateArray()
            .Single(a => a.GetProperty("id").GetGuid() == adjustment);
        Assert.Equal(("CountError", "140000", approval), (adj.GetProperty("reason").GetString(), adj.GetProperty("shrinkage").GetString(), adj.GetProperty("approvalId").GetGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, (await As("cashier").GetAsync("/api/v1/inventory/counts")).StatusCode);
    }
}
