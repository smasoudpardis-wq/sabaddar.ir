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

/// <summary>
/// US-047 / CAT-005 / DI-093: the CatalogModerator's queue. Stores' opt-in suggestions are reviewed, then published into the
/// global catalog (where every store then finds them) or rejected with a reason; only the moderator may decide, and only once.
/// </summary>
public sealed class CatalogModerationTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private sealed record Store(Guid Tenant, string Sub);

    private readonly ApiFactory _factory;
    private Store _a = null!, _b = null!, _c = null!;
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public CatalogModerationTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        _a = await NewStore("ناظر الف");
        _b = await NewStore("ناظر ب");
        _c = await NewStore("ناظر ج");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<Store> NewStore(string name)
    {
        Guid tenant = Guid.CreateVersion7(), owner = Guid.CreateVersion7(), branch = Guid.CreateVersion7();
        string sub = "moderation-owner-" + Guid.NewGuid();
        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
            Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مالک')", owner, ApiFactory.Issuer, sub));
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, $2, 'supermarket')", tenant, name);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک')", tenant, branch);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", tenant, owner);
            return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, gen_random_uuid(), $2, 'TenantOwner', 'Tenant')",
                tenant, owner);
        });
        await TestAccounting.SetUpAsync(Cs, tenant);
        return new Store(tenant, sub);
    }

    private static async Task<int> Exec(IUnitOfWorkScope s, CancellationToken ct, string sql, params object[] args)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private HttpClient Owner(Store st)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(st.Sub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, st.Tenant.ToString());
        return c;
    }

    private HttpClient Platform(string? role = "CatalogModerator", string? acr = "mfa")
    {
        HttpClient c = _factory.CreateClient();
        var claims = new Dictionary<string, object> { ["realm_access"] = new Dictionary<string, object> { ["roles"] = role is null ? Array.Empty<string>() : new[] { role } } };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ApiFactory.Token("moderator-1", ApiFactory.PlatformIssuer, ApiFactory.PlatformAudience, acr: acr, authTime: DateTimeOffset.UtcNow, extraClaims: claims));
        return c;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    private static string Ean()
    {
        string body = "626" + Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        int sum = body.Select((ch, i) => (ch - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return body + ((10 - sum % 10) % 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The store makes the product and opts in to suggest it (CAT-005.1).</summary>
    private async Task Suggest(Store st, string name, string ean, string? category = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/products")
        {
            Content = JsonContent.Create(new
            {
                name, baseUnit = "EA", confirmation = new { confirmed = true },
                variants = new[] { new { sku = "MOD-" + Guid.NewGuid().ToString("N")[..10], unitPrice = "75000", barcodes = new[] { new { code = ean } } } },
            }),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        HttpResponseMessage created = await Owner(st).SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Guid id = (await Body(created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Accepted,
            (await Owner(st).PostAsJsonAsync($"/api/v1/products/{id}/catalog-suggestions", new { consent = true, category })).StatusCode);
    }

    private async Task<List<JsonElement>> Queue(string gtin14)
    {
        var found = new List<JsonElement>();
        string? cursor = null;
        do
        {
            HttpResponseMessage r = await Platform().GetAsync("/api/v1/platform/catalog/suggestions?limit=200" + (cursor is null ? "" : "&cursor=" + cursor));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            JsonElement b = await Body(r);
            found.AddRange(b.GetProperty("items").EnumerateArray().Where(x => x.GetProperty("gtin14").GetString() == gtin14).Select(x => x.Clone()));
            cursor = b.GetProperty("page").GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        return found;
    }

    private Task<HttpResponseMessage> Decide(Guid id, object body, HttpClient? as_ = null) =>
        (as_ ?? Platform()).PostAsJsonAsync($"/api/v1/platform/catalog/suggestions/{id}/decision", body);

    [DbFact]
    public async Task A_published_suggestion_becomes_a_global_candidate_for_every_store()
    {
        string ean = Ean();
        await Suggest(_a, "روغن آفتابگردان ۸۱۰ گرمی", ean, "روغن");
        await Suggest(_b, "روغن آفتاب گردان", ean);

        List<JsonElement> pending = await Queue("0" + ean);
        Assert.Equal(2, pending.Count);
        Assert.All(pending, x => Assert.Equal(2, x.GetProperty("storesForBarcode").GetInt32()));
        Assert.All(pending, x => Assert.Equal(JsonValueKind.Null, x.GetProperty("existing").ValueKind));
        string queueText = string.Join(" ", pending.Select(x => x.GetRawText()));
        Assert.DoesNotContain(_a.Tenant.ToString(), queueText, StringComparison.Ordinal);   // which store sent it is never shown
        Assert.DoesNotContain("75000", queueText, StringComparison.Ordinal);

        Guid first = pending[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await Decide(first, new
        {
            decision = "Publish", reason = "با برچسب کالا تطبیق داده شد", edits = new { name = "روغن آفتابگردان لادن ۸۱۰ گرمی", brand = "لادن" },
        })).StatusCode);
        Assert.Empty(await Queue("0" + ean));   // both stores' suggestions are settled by the one decision

        // a third store scanning the code now gets the moderated text as a pre-selected global candidate
        JsonElement r = await Body(await Owner(_c).PostAsJsonAsync("/api/v1/catalog/barcodes/resolve", new { barcode = ean, branchId = Guid.NewGuid() }));
        Assert.Equal("candidates", r.GetProperty("kind").GetString());
        JsonElement c = r.GetProperty("candidates").EnumerateArray().Single(x => x.GetProperty("source").GetString() == "global");
        Assert.Equal(("روغن آفتابگردان لادن ۸۱۰ گرمی", "لادن", "روغن"),
            (c.GetProperty("fields").GetProperty("name").GetString(), c.GetProperty("fields").GetProperty("brand").GetString(),
             c.GetProperty("fields").GetProperty("category").GetString()));

        HttpResponseMessage again = await Decide(pending[1].GetProperty("id").GetGuid(), new { decision = "Reject", reason = "دیر رسید" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("already-decided", (await Body(again)).GetProperty("code").GetString());
    }

    [DbFact]
    public async Task A_later_suggestion_for_a_published_code_shows_what_the_catalog_holds_and_re_verifies_it()
    {
        string ean = Ean();
        await Suggest(_a, "پنیر سفید ۴۰۰ گرمی", ean);
        Guid id = (await Queue("0" + ean)).Single().GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await Decide(id, new { decision = "Publish", reason = "درست است" })).StatusCode);

        await Suggest(_b, "پنیر سفید کم‌چرب ۴۰۰ گرمی", ean);
        JsonElement later = (await Queue("0" + ean)).Single();
        JsonElement existing = later.GetProperty("existing");
        Assert.Equal("پنیر سفید ۴۰۰ گرمی", existing.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await Decide(later.GetProperty("id").GetGuid(), new { decision = "Publish", reason = "کم‌چرب درست‌تر است" })).StatusCode);

        JsonElement r = await Body(await Owner(_c).PostAsJsonAsync("/api/v1/catalog/barcodes/resolve", new { barcode = ean, branchId = Guid.NewGuid() }));
        JsonElement[] global = [.. r.GetProperty("candidates").EnumerateArray().Where(x => x.GetProperty("source").GetString() == "global")];
        Assert.Single(global);   // still one product for the code, now with the corrected text
        Assert.Equal(existing.GetProperty("catalogProductId").GetGuid(), global[0].GetProperty("catalogProductId").GetGuid());
        Assert.Equal("پنیر سفید کم‌چرب ۴۰۰ گرمی", global[0].GetProperty("fields").GetProperty("name").GetString());
    }

    [DbFact]
    public async Task A_rejected_suggestion_needs_a_reason_and_never_reaches_the_catalog()
    {
        string ean = Ean();
        await Suggest(_a, "کالای نامعلوم", ean);
        Guid id = (await Queue("0" + ean)).Single().GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await Decide(id, new { decision = "Reject" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Decide(id, new { decision = "Reject", reason = "نام ناقص", edits = new { name = "x" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Decide(id, new { decision = "Maybe", reason = "نمی‌دانم" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Decide(id, new { decision = "Reject", reason = "نام کالا ناقص است" })).StatusCode);
        Assert.Empty(await Queue("0" + ean));
        Assert.Equal(HttpStatusCode.NotFound, (await Decide(Guid.NewGuid(), new { decision = "Reject", reason = "ناموجود" })).StatusCode);

        JsonElement r = await Body(await Owner(_c).PostAsJsonAsync("/api/v1/catalog/barcodes/resolve", new { barcode = ean, branchId = Guid.NewGuid() }));
        Assert.DoesNotContain(r.GetProperty("candidates").EnumerateArray(), x => x.GetProperty("source").GetString() == "global");
    }

    [DbFact]
    public async Task Only_a_catalog_moderator_with_a_second_factor_sees_or_decides_the_queue()
    {
        string ean = Ean();
        await Suggest(_a, "ماکارونی ۵۰۰ گرمی", ean);
        Guid id = (await Queue("0" + ean)).Single().GetProperty("id").GetGuid();
        object body = new { decision = "Publish", reason = "بدون ناظر" };

        foreach (HttpClient c in new[] { Platform("PlatformSupport"), Platform("PlatformSuperAdmin"), Platform(acr: "pwd"), Platform(role: null) })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/platform/catalog/suggestions")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Decide(id, body, c)).StatusCode);
        }

        // a store owner (merchant token) is not a moderator either
        Assert.Equal(HttpStatusCode.Unauthorized, (await Owner(_a).GetAsync("/api/v1/platform/catalog/suggestions")).StatusCode);
        Assert.Single(await Queue("0" + ean));   // still pending
    }
}
