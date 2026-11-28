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

/// <summary>US-063 / INV-002 / BR-033 / SEC-T-104 (AB-05): adjustments with a reason, an approval above the threshold and their entry.</summary>
public sealed class AdjustmentTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _b2 = Guid.CreateVersion7(), _w1 = Guid.CreateVersion7(),
        _w2 = Guid.CreateVersion7();
    private readonly Dictionary<string, (Guid Id, string Sub)> _u = [];
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public AdjustmentTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach (string n in new[] { "owner", "manager", "manager2", "clerk", "cashier" })
        {
            _u[n] = (Guid.CreateVersion7(), $"adj-{n}-{Guid.NewGuid()}");
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, $4)", _u[n].Id, ApiFactory.Issuer, _u[n].Sub, n));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'تعدیل', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک'), ($1, $3, 'B2', 'دو')", _tenant, _b1, _b2);
            await Exec(s, ct, "INSERT INTO inv.warehouse (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'W1'), ($1, $4, $5, 'W2')", _tenant, _w1, _b1, _w2, _b2);
            foreach ((string n, string role, string scopeType) in new[]
                     {
                         ("owner", "TenantOwner", "Tenant"), ("manager", "BranchManager", "Branch"), ("manager2", "BranchManager", "Branch"),
                         ("clerk", "WarehouseClerk", "Branch"), ("cashier", "Cashier", "Branch"),
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

    /// <summary>A product with <paramref name="qty"/> on hand in W1 at <paramref name="unitCost"/> rials each.</summary>
    private async Task<Guid> Stocked(string sku, string qty, string unitCost)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/products")
        {
            Content = JsonContent.Create(new
            {
                name = "کالا " + sku, baseUnit = "EA", confirmation = new { confirmed = true }, openingStock = new { warehouseId = _w1, qty, unitCost },
                variants = new[] { new { sku } },
            }),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        HttpResponseMessage r = await As("owner").SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Body(r)).GetProperty("variants")[0].GetProperty("id").GetGuid();
    }

    private static object Adjust(Guid warehouse, string reason, (Guid Variant, string Delta)[] lines, string? note = null) => new
    {
        warehouseId = warehouse, reason, note, lines = lines.Select(l => new { variantId = l.Variant, qtyDelta = l.Delta }).ToArray(),
    };

    private async Task<HttpResponseMessage> Post(string user, object body, string? key = null, Guid? approval = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/adjustments") { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        if (approval is { } a)
        {
            req.Headers.Add("X-Approval-Id", a.ToString());
        }

        return await As(user).SendAsync(req);
    }

    private async Task<string> OnHand(Guid variant) =>
        (await Body(await As("owner").GetAsync($"/api/v1/inventory/balances?variantId={variant}&warehouseId={_w1}"))).GetProperty("items")[0]
        .GetProperty("onHand").GetString()!;

    private async Task<Dictionary<string, (long Debit, long Credit)>> Entry(Guid journalId)
    {
        JsonElement j = (await Body(await As("owner").GetAsync("/api/v1/accounting/journals?sourceType=StockAdjustment"))).GetProperty("items")
            .EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == journalId);
        Assert.Equal("Posted", j.GetProperty("status").GetString());
        return j.GetProperty("lines").EnumerateArray().GroupBy(l => l.GetProperty("accountCode").GetString()!).ToDictionary(g => g.Key,
            g => (g.Sum(l => long.Parse(l.GetProperty("debit").GetString()!, System.Globalization.CultureInfo.InvariantCulture)),
                  g.Sum(l => long.Parse(l.GetProperty("credit").GetString()!, System.Globalization.CultureInfo.InvariantCulture))));
    }

    [DbFact]
    public async Task An_adjustment_moves_stock_at_the_WAC_and_posts_its_shrinkage_and_overage_in_the_same_transaction()
    {
        Guid tea = await Stocked("ADJ-TEA", "100", "120000"), rice = await Stocked("ADJ-RICE", "10", "800000");
        string key = Guid.NewGuid().ToString("N");
        object body = Adjust(_w1, "CountError", [(tea, "-3"), (rice, "2.5")]);
        HttpResponseMessage r = await Post("manager", body, key);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        JsonElement a = await Body(r);
        // 3 × 120,000 out, 2.5 × 800,000 in: both valued at the WAC of the moment (INV-006)
        Assert.Equal(("2360000", "360000", "2000000", 2), (a.GetProperty("value").GetString(), a.GetProperty("shrinkage").GetString(),
            a.GetProperty("overage").GetString(), a.GetProperty("lineCount").GetInt32()));
        Assert.Equal(JsonValueKind.Null, a.GetProperty("approvalId").ValueKind);
        Assert.Equal(("97", "12.5"), (await OnHand(tea), await OnHand(rice)));

        // D02 §5.8.1: shrinkage expense / inventory, inventory / overage
        Dictionary<string, (long Debit, long Credit)> lines = await Entry(a.GetProperty("journalEntryId").GetGuid());
        Assert.Equal((360_000L, 0L), lines["5102"]);
        Assert.Equal((2_000_000L, 360_000L), lines["1106"]);
        Assert.Equal((0L, 2_000_000L), lines["4102"]);

        // a retry replays the same adjustment: no second movement, no second entry
        Assert.Equal(a.GetProperty("id").GetGuid(), (await Body(await Post("manager", body, key))).GetProperty("id").GetGuid());
        Assert.Equal("97", await OnHand(tea));

        JsonElement listed = (await Body(await As("owner").GetAsync("/api/v1/inventory/adjustments"))).GetProperty("items");
        Assert.Contains(listed.EnumerateArray(), x => x.GetProperty("id").GetGuid() == a.GetProperty("id").GetGuid()
                                                     && x.GetProperty("reason").GetString() == "CountError"
                                                     && x.GetProperty("createdBy").GetGuid() == _u["manager"].Id);
    }

    [DbFact]
    public async Task SEC_T_104_above_the_threshold_an_adjustment_waits_for_another_managers_approval_bound_to_it()
    {
        Guid tv = await Stocked("ADJ-TV", "10", "6000000");
        object theft = Adjust(_w1, "Theft", [(tv, "-9")], "گم شد");   // 54,000,000 > 50,000,000
        string key = Guid.NewGuid().ToString("N");
        HttpResponseMessage asked = await Post("manager", theft, key);
        Assert.Equal(HttpStatusCode.Forbidden, asked.StatusCode);
        JsonElement problem = await Body(asked);
        Assert.Equal("approval-required", problem.GetProperty("code").GetString());
        Assert.Equal(("value-threshold", "54000000", "50000000"), (problem.GetProperty("context").GetProperty("condition").GetString(),
            problem.GetProperty("context").GetProperty("value").GetString(), problem.GetProperty("context").GetProperty("limit").GetString()));
        Guid approval = problem.GetProperty("approvalRequestId").GetGuid();
        Assert.Equal("10", await OnHand(tv));   // nothing moved meanwhile

        // a retry before the decision points at the same request
        Assert.Equal(approval, (await Body(await Post("manager", theft, key, approval))).GetProperty("approvalRequestId").GetGuid());

        // SoD: not the requester; another manager of the branch may
        Assert.Equal(HttpStatusCode.Forbidden, (await As("manager").PostAsJsonAsync($"/api/v1/approvals/{approval}/decision", new { decision = "Approve" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As("manager2").PostAsJsonAsync($"/api/v1/approvals/{approval}/decision", new { decision = "Approve" })).StatusCode);

        // the approval does not fit another adjustment
        Assert.Equal("approval-required", (await Body(await Post("manager", Adjust(_w1, "Theft", [(tv, "-10")], "گم شد"), approval: approval)))
            .GetProperty("code").GetString());

        HttpResponseMessage done = await Post("manager", theft, key, approval);
        Assert.Equal(HttpStatusCode.Created, done.StatusCode);
        Assert.Equal(approval, (await Body(done)).GetProperty("approvalId").GetGuid());
        Assert.Equal("1", await OnHand(tv));

        // exactly the threshold needs none (BR-033: "more than")
        Guid fridge = await Stocked("ADJ-FRIDGE", "10", "10000000");
        Assert.Equal(HttpStatusCode.Created, (await Post("manager", Adjust(_w1, "Damage", [(fridge, "-5")]))).StatusCode);
    }

    [DbFact]
    public async Task Bad_or_unauthorised_adjustments_are_refused_and_leave_nothing_behind()
    {
        Guid salt = await Stocked("ADJ-SALT", "5", "50000");
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("manager", Adjust(_w1, "Other", [(salt, "-1")]))).StatusCode);          // «سایر» needs a note
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("manager", Adjust(_w1, "Lost", [(salt, "-1")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("manager", Adjust(_w1, "Damage", [(salt, "0")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("manager", Adjust(_w1, "Damage", [(salt, "-1"), (salt, "-1")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("manager", Adjust(_w1, "Damage", [(Guid.NewGuid(), "-1")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("manager", Adjust(Guid.NewGuid(), "Damage", [(salt, "-1")]))).StatusCode);

        // a warehouse clerk counts but does not adjust; a manager only in their own branch (SEC-T-104)
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("clerk", Adjust(_w1, "Damage", [(salt, "-1")]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("manager", Adjust(_w2, "Damage", [(salt, "1")]))).StatusCode);

        // more than there is: every shortage listed, nothing posted (BR-040 Forbid)
        HttpResponseMessage shortage = await Post("manager", Adjust(_w1, "Expiry", [(salt, "-6")]));
        Assert.Equal(HttpStatusCode.Conflict, shortage.StatusCode);
        JsonElement s = (await Body(shortage)).GetProperty("context").GetProperty("shortages")[0];
        Assert.Equal((salt, "5", "6"), (s.GetProperty("variantId").GetGuid(), s.GetProperty("available").GetString(), s.GetProperty("requested").GetString()));
        Assert.Equal("5", await OnHand(salt));

        Assert.Equal(HttpStatusCode.Forbidden, (await As("cashier").GetAsync("/api/v1/inventory/adjustments")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post("manager", Adjust(_w1, "Other", [(salt, "-1")], "نمونه برای آزمایشگاه"))).StatusCode);
        Assert.Equal("4", await OnHand(salt));
    }
}
