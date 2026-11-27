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

/// <summary>US-044 / CAT-006 (AT-028) merging a duplicate product, and CAT-005.1 (AT-027) an opt-in suggestion to the global catalog.</summary>
public sealed class CatalogMergeTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _owner = Guid.CreateVersion7(), _branch = Guid.CreateVersion7(), _warehouse = Guid.CreateVersion7();
    private readonly string _sub = "merge-owner-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public CatalogMergeTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
            Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مالک')", _owner, ApiFactory.Issuer, _sub));
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'ادغام', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک')", _tenant, _branch);
            await Exec(s, ct, "INSERT INTO inv.warehouse (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'W1')", _tenant, _warehouse, _branch);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _tenant, _owner);
            return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, gen_random_uuid(), $2, 'TenantOwner', 'Tenant')",
                _tenant, _owner);
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

    private HttpClient Owner()
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(_sub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, _tenant.ToString());
        return c;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    private static async Task<HttpResponseMessage> Idem(HttpClient c, string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await c.SendAsync(req);
    }

    private static string Ean()
    {
        string body = "626" + Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        int sum = body.Select((ch, i) => (ch - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return body + ((10 - sum % 10) % 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<JsonElement> Product(string name, string sku, object[] barcodes, string unit = "EA", object? openingStock = null)
    {
        HttpResponseMessage r = await Idem(Owner(), "/api/v1/products", new
        {
            name, baseUnit = unit, confirmation = new { confirmed = true }, openingStock, variants = new[] { new { sku, unitPrice = "50000", barcodes } },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await Body(r);
    }

    private Task<HttpResponseMessage> Merge(JsonElement source, JsonElement target, int? version = null) =>
        Idem(Owner(), $"/api/v1/products/{source.GetProperty("id").GetGuid()}/merge",
            new { targetProductId = target.GetProperty("id").GetGuid(), expectedVersion = version ?? source.GetProperty("version").GetInt32(), reason = "کالای تکراری" });

    [DbFact]
    public async Task AT028_a_duplicate_is_merged_its_barcodes_move_it_is_kept_as_Merged_and_the_merge_is_audited()
    {
        string a = Ean(), b = Ean();
        JsonElement keep = await Product("شیر پاستوریزه", "MG-KEEP-" + a, [new { code = a }]);
        JsonElement dup = await Product("شير پاستوريزه", "MG-DUP-" + b, [new { code = b }, new { code = "MG-INT-" + b[^4..], isInternal = true }]);

        HttpResponseMessage merged = await Merge(dup, keep);
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        JsonElement target = await Body(merged);
        string[] codes = [.. target.GetProperty("variants")[0].GetProperty("barcodes").EnumerateArray().Where(x => x.GetProperty("active").GetBoolean())
            .Select(x => x.GetProperty("code").GetString()!)];
        Assert.Equal(new[] { a, b, "MG-INT-" + b[^4..] }.Order(), codes.Order());

        // the duplicate scans as the kept product now, is not listed, and stays (Merged) for its history
        JsonElement resolved = await Body(await Owner().PostAsJsonAsync("/api/v1/catalog/barcodes/resolve", new { barcode = b, branchId = _branch }));
        Assert.Equal(keep.GetProperty("id").GetGuid(), resolved.GetProperty("tenantProduct").GetProperty("productId").GetGuid());
        JsonElement listed = await Body(await Owner().GetAsync("/api/v1/products?q=" + Uri.EscapeDataString("شیر پاستوریزه")));
        Assert.DoesNotContain(dup.GetProperty("id").GetGuid(), listed.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        JsonElement kept = await Body(await Owner().GetAsync($"/api/v1/products/{dup.GetProperty("id").GetGuid()}"));
        Assert.Equal("Merged", kept.GetProperty("status").GetString());

        Assert.Equal("product-merged", (await Body(await Merge(dup, keep, kept.GetProperty("version").GetInt32()))).GetProperty("code").GetString());
        await using var ds = NpgsqlDataSource.Create(Cs);
        long audited = await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT count(*) FROM audit.audit_record WHERE action = 'catalog.product.merged' AND resource_id = $1");
            cmd.Parameters.Add(new NpgsqlParameter { Value = dup.GetProperty("id").GetGuid().ToString() });
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        });
        Assert.Equal(1, audited);
    }

    [DbFact]
    public async Task CAT006_another_base_unit_a_duplicate_with_stock_or_a_stale_version_is_refused()
    {
        JsonElement keep = await Product("برنج", "MG-RICE-" + Ean(), [new { code = Ean() }]);
        JsonElement kilo = await Product("برنج فله", "MG-RICEKG-" + Ean(), [new { code = Ean() }], unit: "KG");
        Assert.Equal("base-unit-mismatch", (await Body(await Merge(kilo, keep))).GetProperty("code").GetString());

        JsonElement stocked = await Product("برنج کیسه", "MG-RICE2-" + Ean(), [new { code = Ean() }],
            openingStock: new { warehouseId = _warehouse, qty = "3", unitCost = "40000" });
        HttpResponseMessage held = await Merge(stocked, keep);
        Assert.Equal(HttpStatusCode.Conflict, held.StatusCode);
        Assert.Equal("merge-source-has-stock", (await Body(held)).GetProperty("code").GetString());

        JsonElement other = await Product("برنج دیگر", "MG-RICE3-" + Ean(), [new { code = Ean() }]);
        Assert.Equal("version-conflict", (await Body(await Merge(other, keep, version: 9))).GetProperty("code").GetString());
    }

    [DbFact]
    public async Task AT027_a_suggestion_is_opt_in_and_sends_only_descriptive_fields_and_public_barcodes()
    {
        string ean = Ean();
        JsonElement product = await Product("چای سبز ممتاز", "MG-TEA-" + ean, [new { code = ean }, new { code = "MG-TEAI-" + ean[^4..], isInternal = true }]);
        string path = $"/api/v1/products/{product.GetProperty("id").GetGuid()}/catalog-suggestions";
        Assert.Equal(HttpStatusCode.BadRequest, (await Owner().PostAsJsonAsync(path, new { consent = false })).StatusCode);

        HttpResponseMessage sent = await Owner().PostAsJsonAsync(path, new { consent = true, category = "نوشیدنی" });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        Assert.Equal(["0" + ean], (await Body(sent)).GetProperty("submitted").EnumerateArray().Select(x => x.GetString()));   // the internal code stays home
        JsonElement again = await Body(await Owner().PostAsJsonAsync(path, new { consent = true }));
        Assert.Equal((0, "0" + ean), (again.GetProperty("submitted").GetArrayLength(), again.GetProperty("alreadySubmitted")[0].GetString()));

        JsonElement internalOnly = await Product("نان محلی", "MG-BREAD-" + Ean(), [new { code = "MG-BRD-" + ean[^4..], isInternal = true }]);
        Assert.Equal("no-public-barcode", (await Body(await Owner().PostAsJsonAsync($"/api/v1/products/{internalOnly.GetProperty("id").GetGuid()}/catalog-suggestions",
            new { consent = true }))).GetProperty("code").GetString());

        // what reached the global catalog: the descriptive fields, and no trace of which store or its price
        string adminCs = new NpgsqlConnectionStringBuilder(Cs) { Username = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres",
            Password = Environment.GetEnvironmentVariable("PGPASSWORD") }.ToString();
        await using var admin = NpgsqlDataSource.Create(adminCs);
        await using NpgsqlCommand cmd = admin.CreateCommand("SELECT row_to_json(s)::text FROM catalog.catalog_suggestion s WHERE gtin14 = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = "0" + ean });
        string row = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.Contains("چای سبز ممتاز", row, StringComparison.Ordinal);
        Assert.DoesNotContain(_tenant.ToString(), row, StringComparison.Ordinal);
        Assert.DoesNotContain("50000", row, StringComparison.Ordinal);
    }
}
