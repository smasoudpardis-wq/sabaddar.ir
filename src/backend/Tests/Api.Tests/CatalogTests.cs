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

/// <summary>US-041 / CAT-001, CAT-002, CAT-002.1, CAT-003 / AT-020..022: products, variants and barcodes.</summary>
public sealed class CatalogTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _owner = Guid.CreateVersion7();
    private readonly string _ownerSub = "cat-owner-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public CatalogTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
            Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مالک')", _owner, ApiFactory.Issuer, _ownerSub));
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'کاتالوگ', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _tenant, _owner);
            return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, $2, $3, 'TenantOwner', 'Tenant')",
                _tenant, Guid.CreateVersion7(), _owner);
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

    private HttpClient Owner()
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(_ownerSub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, _tenant.ToString());
        return c;
    }

    private static async Task<HttpResponseMessage> Create(HttpClient c, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/products") { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await c.SendAsync(req);
    }

    private static object Milk(string sku, params object[] barcodes) => new
    {
        name = "شیر کم‌چرب ۱ لیتری", baseUnit = "EA", brand = "دامداران", taxRate = 1000,
        variants = new[] { new { sku, barcodes } },
        confirmation = new { confirmed = true },
    };

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    [DbFact]
    public async Task A_product_with_variants_and_packaged_barcodes_is_stored_with_GTIN_14()
    {
        HttpClient owner = Owner();
        HttpResponseMessage created = await Create(owner, new
        {
            name = "نوشابه", baseUnit = "EA", taxRate = 1000, confirmation = new { confirmed = true },
            variants = new object[]
            {
                new { sku = "COLA-330", axisValues = new { size = "330ml" }, barcodes = new object[]
                {
                    new { code = "036000291452" },                                         // UPC-A
                    new { code = "4006381333931", packagingUnit = "CT", unitFactor = "24" }, // a carton of 24
                } },
                new { sku = "COLA-1500", axisValues = new { size = "1.5L" }, barcodes = new object[] { new { code = "SHOP-15", isInternal = true } } },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonElement p = await Body(created);
        Assert.Equal(1, p.GetProperty("version").GetInt32());
        JsonElement cola = p.GetProperty("variants").EnumerateArray().Single(v => v.GetProperty("sku").GetString() == "COLA-330");
        Assert.Equal("330ml", cola.GetProperty("axisValues").GetProperty("size").GetString());
        JsonElement[] codes = [.. cola.GetProperty("barcodes").EnumerateArray()];
        Assert.Equal("00036000291452", codes[0].GetProperty("codeNormalized").GetString());
        Assert.Equal("036000291452", codes[0].GetProperty("code").GetString());
        Assert.Equal(("CT", "24"), (codes[1].GetProperty("packagingUnit").GetString(), codes[1].GetProperty("unitFactor").GetString()));

        // the same item scanned as EAN-13 (leading zero) finds it: one normalised code (CAT-003)
        JsonElement byScan = await Body(await owner.GetAsync("/api/v1/products?barcode=0036000291452"));
        Assert.Equal(p.GetProperty("id").GetGuid(), Assert.Single(byScan.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        // the internal code is found as typed
        Assert.Single((await Body(await owner.GetAsync("/api/v1/products?barcode=SHOP-15"))).GetProperty("items").EnumerateArray());
    }

    [DbFact]
    public async Task Restricted_circulation_codes_are_valid_but_stay_internal_to_the_store()
    {
        // AT-024 / CAT-003.1: GS1 prefixes 20-29 (and 02) are in-store codes, e.g. a shelf label printed by the store
        JsonElement p = await Body(await Create(Owner(), Milk("SHELF-1", new { code = "2012345000001" }, new { code = "4006381333931" })));
        JsonElement[] codes = [.. p.GetProperty("variants")[0].GetProperty("barcodes").EnumerateArray()];
        Assert.Equal(("02012345000001", true), (codes[0].GetProperty("codeNormalized").GetString(), codes[0].GetProperty("isInternal").GetBoolean()));
        Assert.Equal(("04006381333931", false), (codes[1].GetProperty("codeNormalized").GetString(), codes[1].GetProperty("isInternal").GetBoolean()));
    }

    [DbFact]
    public async Task One_active_owner_per_barcode_and_the_history_is_kept()
    {
        HttpClient owner = Owner();
        JsonElement first = await Body(await Create(owner, Milk("MILK-A", new { code = "96385074" })));
        Guid firstId = first.GetProperty("id").GetGuid();

        HttpResponseMessage clash = await Create(owner, Milk("MILK-B", new { code = "96385074" }));   // AT-022 / CAT-002.1
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        JsonElement problem = await Body(clash);
        Assert.Equal("barcode-already-assigned", problem.GetProperty("code").GetString());
        Assert.Equal(firstId, problem.GetProperty("context").GetProperty("productId").GetGuid());

        // remove it from the first product, then the second may take it; the first keeps the removed row (AT-021)
        JsonElement v = first.GetProperty("variants")[0];
        Guid barcodeId = v.GetProperty("barcodes")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.DeleteAsync($"/api/v1/products/{firstId}/variants/{v.GetProperty("id").GetGuid()}/barcodes/{barcodeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Create(owner, Milk("MILK-B", new { code = "96385074" }))).StatusCode);
        JsonElement history = (await Body(await owner.GetAsync($"/api/v1/products/{firstId}"))).GetProperty("variants")[0].GetProperty("barcodes")[0];
        Assert.False(history.GetProperty("active").GetBoolean());
        Assert.Equal(2, (await Body(await owner.GetAsync($"/api/v1/products/{firstId}"))).GetProperty("version").GetInt32());
    }

    [DbFact]
    public async Task Bad_input_is_refused_precisely()
    {
        HttpClient owner = Owner();
        HttpResponseMessage checkDigit = await Create(owner, Milk("MILK-X", new { code = "4006381333932" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, checkDigit.StatusCode);
        Assert.Equal("invalid-barcode", (await Body(checkDigit)).GetProperty("code").GetString());

        var noConfirmation = new { name = "کالا", baseUnit = "EA", variants = new[] { new { sku = "N-1" } } };
        Assert.Equal("required: true (CAT-004.1)", (await Body(await Create(owner, noConfirmation))).GetProperty("errors").GetProperty("confirmation.confirmed")[0].GetString());

        var later = new { name = "کالا", baseUnit = "EA", categoryId = Guid.NewGuid(), confirmation = new { confirmed = true },
            variants = new[] { new { sku = "N-2", unitPrice = "0" } } };
        JsonElement errors = (await Body(await Create(owner, later))).GetProperty("errors");
        Assert.Equal("not-yet-supported", errors.GetProperty("categoryId")[0].GetString());
        Assert.True(errors.TryGetProperty("variants[0].unitPrice", out _));   // a first price is positive (US-051)

        await Create(owner, Milk("DUP-1"));
        Assert.Equal("duplicate-code", (await Body(await Create(owner, Milk("DUP-1")))).GetProperty("code").GetString());
    }

    [DbFact]
    public async Task AT034_search_by_word_prefix_in_any_order_and_exact_sku_or_barcode()
    {
        HttpClient owner = Owner();
        async Task<Guid> Make(string name, string sku, string? barcode = null) =>
            (await Body(await Create(owner, new
            {
                name, baseUnit = "EA", confirmation = new { confirmed = true },
                variants = new[] { new { sku, barcodes = barcode is null ? Array.Empty<object>() : new object[] { new { code = barcode } } } },
            }))).GetProperty("id").GetGuid();
        async Task<Guid[]> Find(string q) => [.. (await Body(await owner.GetAsync("/api/v1/products?q=" + Uri.EscapeDataString(q))))
            .GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid())];

        Guid milk = await Make("شیرِ کم‌چرب پاستوریزه", "AT34-MILK", "6260100000018");
        Guid yogurt = await Make("ماست پرچرب", "AT34-YOG");
        Guid sugar = await Make("شکر ۱ کیلویی", "AT34-SUGAR");

        Assert.Equal([milk], await Find("پاستو شير"));          // two word prefixes, any order, Arabic yeh, no kasra
        Assert.Equal([milk], await Find("كمچرب"));               // half-space removed, Arabic kaf
        Assert.Empty(await Find("چرب"));                          // not the start of a word
        Assert.Equal([sugar], await Find("شکر 1"));               // Latin digit for ۱
        Assert.Equal([yogurt], await Find("AT34-YOG"));           // exact SKU
        Assert.Empty(await Find("AT34-YO"));                      // a partial SKU is not a match
        Assert.Equal([milk], await Find("6260100000018"));        // a scanned barcode typed into the box
        Assert.Empty(await Find("100%"));                         // LIKE wildcards are literal

        await using var ds = NpgsqlDataSource.Create(Cs);
        await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), (sc, ct) =>
            Exec(sc, ct, "UPDATE merch.tenant_product SET status = 'Merged', merged_into_id = $2 WHERE id = $1", yogurt, milk));
        Assert.Empty(await Find("ماست"));                         // a merged product is not offered again
    }

    [DbFact]
    public async Task Persian_search_finds_names_typed_with_Arabic_letters_and_edits_are_versioned()
    {
        HttpClient owner = Owner();
        Guid id = (await Body(await Create(owner, Milk("MILK-S")))).GetProperty("id").GetGuid();
        // Arabic yeh/kaf, no half-space, Latin digit: still the same product (CAT-011)
        JsonElement found = await Body(await owner.GetAsync("/api/v1/products?q=" + Uri.EscapeDataString("كمچرب 1")));
        Assert.Contains(found.GetProperty("items").EnumerateArray(), e => e.GetProperty("id").GetGuid() == id);
        Assert.Single((await Body(await owner.GetAsync("/api/v1/products?q=MILK-S"))).GetProperty("items").EnumerateArray());

        HttpResponseMessage edited = await owner.PatchAsJsonAsync($"/api/v1/products/{id}", new { expectedVersion = 1, name = "شیر پرچرب", status = "Inactive" });
        Assert.Equal(2, (await Body(edited)).GetProperty("version").GetInt32());
        HttpResponseMessage stale = await owner.PatchAsJsonAsync($"/api/v1/products/{id}", new { expectedVersion = 1, name = "دیرهنگام" });
        Assert.Equal("version-conflict", (await Body(stale)).GetProperty("code").GetString());

        Assert.Equal("duplicate-code", (await Body(await owner.PostAsJsonAsync($"/api/v1/products/{id}/variants", new { sku = "MILK-S-2" })))
            .GetProperty("code").GetString());   // same (empty) axis values as the first variant (DI-081)
        HttpResponseMessage variant = await owner.PostAsJsonAsync($"/api/v1/products/{id}/variants",
            new { sku = "MILK-S-2", axisValues = new { size = "2L" }, barcodes = new[] { new { code = "5901234123457" } } });
        Assert.Equal(HttpStatusCode.Created, variant.StatusCode);
        Assert.Equal("05901234123457", (await Body(variant)).GetProperty("barcodes")[0].GetProperty("codeNormalized").GetString());
    }
    private static string Label(string data12) => data12 + Gtin.ComputeCheckDigit(data12);

    private static async Task<HttpResponseMessage> Resolve(HttpClient c, string barcode) =>
        await c.PostAsJsonAsync("/api/v1/catalog/barcodes/resolve", new { barcode, branchId = Guid.NewGuid() });

    [DbFact]
    public async Task AT029_weighted_profiles_decode_a_label_to_the_variant_and_its_weight_or_price()
    {
        HttpClient owner = Owner();
        JsonElement initial = await Body(await owner.GetAsync("/api/v1/weighted-barcode-profiles"));
        Assert.Equal(1, initial.GetProperty("version").GetInt32());
        Assert.Empty(initial.GetProperty("profiles").EnumerateArray());

        object weight = new { prefix = "21", itemCodeLength = 5, valueType = "Weight", valueLength = 5, decimals = 3, hasInnerCheckDigit = false };
        object price = new { prefix = "22", itemCodeLength = 5, valueType = "Price", valueLength = 4, decimals = 0, hasInnerCheckDigit = true };
        HttpResponseMessage set = await owner.PutAsJsonAsync("/api/v1/weighted-barcode-profiles", new { expectedVersion = 1, profiles = new[] { weight, price } });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(2, (await Body(set)).GetProperty("version").GetInt32());

        // a stale version and an overlapping prefix (DI-084) change nothing
        HttpResponseMessage stale = await owner.PutAsJsonAsync("/api/v1/weighted-barcode-profiles", new { expectedVersion = 1, profiles = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("version-conflict", (await Body(stale)).GetProperty("code").GetString());
        object overlapping = new { prefix = "2", itemCodeLength = 6, valueType = "Price", valueLength = 5, decimals = 0, hasInnerCheckDigit = false };
        HttpResponseMessage overlap = await owner.PutAsJsonAsync("/api/v1/weighted-barcode-profiles", new { expectedVersion = 2, profiles = new[] { weight, overlapping } });
        Assert.Equal(HttpStatusCode.BadRequest, overlap.StatusCode);
        JsonElement now = await Body(await owner.GetAsync("/api/v1/weighted-barcode-profiles"));
        Assert.Equal(2, now.GetProperty("version").GetInt32());
        Assert.Equal(["21", "22"], now.GetProperty("profiles").EnumerateArray().Select(p => p.GetProperty("prefix").GetString()));

        async Task<Guid> Make(string name, string sku, string unit, object barcode) =>
            (await Body(await Create(owner, new
            {
                name, baseUnit = unit, confirmation = new { confirmed = true }, variants = new[] { new { sku, barcodes = new[] { barcode } } },
            }))).GetProperty("variants")[0].GetProperty("id").GetGuid();
        Guid cheese = await Make("پنیر لیقوان فله", "AT29-CHEESE", "KG", new { code = "00123", isInternal = true });
        Guid meat = await Make("گوشت چرخ‌کرده", "AT29-MEAT", "KG", new { code = "00456", isInternal = true });
        await Make("شیر", "AT29-MILK", "EA", new { code = "6260100000018" });

        JsonElement w = await Body(await Resolve(owner, "2100123012503"));   // 1.250 kg of item 00123
        Assert.Equal("known", w.GetProperty("kind").GetString());
        Assert.Equal(cheese, w.GetProperty("tenantProduct").GetProperty("variantId").GetGuid());
        JsonElement wn = w.GetProperty("normalized");
        Assert.True(wn.GetProperty("isWeighted").GetBoolean());
        Assert.True(wn.GetProperty("isInternal").GetBoolean());
        Assert.Equal("02100123012503", wn.GetProperty("gtin14").GetString());
        Assert.Equal(cheese, wn.GetProperty("weighted").GetProperty("variantId").GetGuid());
        Assert.Equal("1.250", wn.GetProperty("weighted").GetProperty("value").GetString());
        Assert.Equal("Weight", wn.GetProperty("weighted").GetProperty("valueType").GetString());

        JsonElement p = (await Body(await Resolve(owner, "2200456928752"))).GetProperty("normalized").GetProperty("weighted");   // 2,875 rials of 00456
        Assert.Equal(meat, p.GetProperty("variantId").GetGuid());
        Assert.Equal("2875", p.GetProperty("value").GetString());
        Assert.Equal("Price", p.GetProperty("valueType").GetString());

        // a misprinted price (inner check digit) is refused, never sold at a wrong amount
        HttpResponseMessage misprint = await Resolve(owner, "2200456028759");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, misprint.StatusCode);
        Assert.Equal("invalid-barcode", (await Body(misprint)).GetProperty("code").GetString());

        JsonElement unknown = await Body(await Resolve(owner, Label("219999901000")));
        Assert.Equal("not_found", unknown.GetProperty("kind").GetString());
        Assert.True(unknown.GetProperty("normalized").GetProperty("isWeighted").GetBoolean());

        JsonElement milk = await Body(await Resolve(owner, "۶۲۶۰۱۰۰۰۰۰۰۱۸"));   // Persian digits from a keyboard wedge
        Assert.Equal("known", milk.GetProperty("kind").GetString());
        Assert.False(milk.GetProperty("normalized").GetProperty("isWeighted").GetBoolean());
        Assert.Equal("AT29-MILK", milk.GetProperty("tenantProduct").GetProperty("sku").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Resolve(owner, "6260100000017")).StatusCode);   // bad check digit

        JsonElement plu = await Body(await Resolve(owner, "00123"));   // the internal code itself
        Assert.Equal(cheese, plu.GetProperty("tenantProduct").GetProperty("variantId").GetGuid());
        Assert.False(plu.GetProperty("normalized").GetProperty("isWeighted").GetBoolean());
    }

    [DbFact]
    public async Task A_profile_set_that_does_not_fill_an_EAN_13_is_refused_field_by_field()
    {
        HttpResponseMessage r = await Owner().PutAsJsonAsync("/api/v1/weighted-barcode-profiles", new
        {
            expectedVersion = 1,
            profiles = new[] { new { prefix = "12", itemCodeLength = 5, valueType = "Price", valueLength = 4, decimals = 2, hasInnerCheckDigit = false } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        JsonElement errors = (await Body(r)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("profiles[0].prefix", out _));
        Assert.True(errors.TryGetProperty("profiles[0].decimals", out _));
        Assert.True(errors.TryGetProperty("profiles[0]", out _));
    }
    [DbFact]
    public async Task AT031_a_colour_by_size_matrix_with_one_combination_left_out_and_one_switched_off_later()
    {
        HttpClient owner = Owner();
        object V(string sku, string color, string size, string? price = null, string? code = null) => new
        {
            sku, axisValues = new Dictionary<string, string> { ["color"] = color, ["size"] = size }, unitPrice = price,
            barcodes = code is null ? Array.Empty<object>() : [new { code, isInternal = true }],
        };
        HttpResponseMessage created = await Create(owner, new
        {
            name = "تی‌شرت نخی", baseUnit = "EA", axes = new[] { "color", "size" }, confirmation = new { confirmed = true },
            variants = new[] { V("TEE-R-M", "قرمز", "M", "900000", "TEE-RM"), V("TEE-R-L", "قرمز", "L", null, "TEE-RL"), V("TEE-B-M", "آبی", "M") },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonElement p = await Body(created);
        Assert.Equal(["color", "size"], p.GetProperty("axes").EnumerateArray().Select(a => a.GetString()));
        Dictionary<string, JsonElement> bySku = p.GetProperty("variants").EnumerateArray().ToDictionary(v => v.GetProperty("sku").GetString()!);
        Assert.Equal(3, bySku.Count);
        Assert.Equal("900000", bySku["TEE-R-M"].GetProperty("unitPrice").GetString());   // each variant its own price
        Assert.Equal(JsonValueKind.Null, bySku["TEE-B-M"].GetProperty("unitPrice").ValueKind);
        Guid id = p.GetProperty("id").GetGuid();

        // every variant of a matrix names exactly its axes
        HttpResponseMessage partial = await Create(owner, new
        {
            name = "شلوار", baseUnit = "EA", axes = new[] { "color", "size" }, confirmation = new { confirmed = true },
            variants = new[] { new { sku = "PANT-1", axisValues = new Dictionary<string, string> { ["color"] = "مشکی" } } },
        });
        Assert.True((await Body(partial)).GetProperty("errors").TryGetProperty("variants[0].axisValues", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/v1/products/{id}/variants",
            new { sku = "TEE-B", axisValues = new { color = "آبی" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/v1/products/{id}/variants",
            new { sku = "TEE-B-L", axisValues = new { color = "آبی", size = "L" } })).StatusCode);   // the left-out combination, later

        // switch red/L off: versioned on the product, and its barcode no longer sells
        int version = (await Body(await owner.GetAsync($"/api/v1/products/{id}"))).GetProperty("version").GetInt32();
        Guid redL = bySku["TEE-R-L"].GetProperty("id").GetGuid();
        HttpResponseMessage off = await owner.PatchAsJsonAsync($"/api/v1/products/{id}/variants/{redL}", new { expectedVersion = version, status = "Inactive" });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal("Inactive", (await Body(off)).GetProperty("variants").EnumerateArray().Single(v => v.GetProperty("id").GetGuid() == redL).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PatchAsJsonAsync($"/api/v1/products/{id}/variants/{redL}", new { expectedVersion = version, status = "Active" })).StatusCode);
        JsonElement resolved = await Body(await owner.PostAsJsonAsync("/api/v1/catalog/barcodes/resolve", new { barcode = "TEE-RL", branchId = Guid.NewGuid() }));
        Assert.Equal("not_found", resolved.GetProperty("kind").GetString());
        Assert.Equal("known", (await Body(await owner.PostAsJsonAsync("/api/v1/catalog/barcodes/resolve", new { barcode = "TEE-RM", branchId = Guid.NewGuid() })))
            .GetProperty("kind").GetString());
    }
}
