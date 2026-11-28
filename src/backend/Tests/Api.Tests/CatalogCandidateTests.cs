using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using RetailCloud.BuildingBlocks.Api;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Merchandising.Application;

namespace RetailCloud.Api.Tests;

/// <summary>
/// US-043 / CAT-004 / CAT-004.1 / CAT-005 (AT-025, AT-026, AT-200..203): an unknown public barcode gets candidates from the
/// global catalog and licensed providers, with source and confidence; saving one is the user's confirmation.
/// </summary>
public sealed class CatalogCandidateTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private sealed class FakeProvider : ICatalogProvider
    {
        public Func<string, Task<ProviderLookup>> Answer { get; set; } = _ => Task.FromResult(ProviderLookup.Miss);

        public int Calls;

        public string Source => "open_food_facts";

        public bool Enabled => true;

        public string License => "ODbL-1.0";

        public string Attribution => "Open Food Facts contributors";

        public async Task<ProviderLookup> LookupAsync(string gtin14, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Task<ProviderLookup> answer = Answer(gtin14);
            return await answer.WaitAsync(cancellationToken);
        }
    }

    private readonly ApiFactory _factory;
    private readonly FakeProvider _provider = new();
    private readonly Guid _a = Guid.CreateVersion7(), _b = Guid.CreateVersion7(), _ownerA = Guid.CreateVersion7(), _ownerB = Guid.CreateVersion7();
    private readonly string _subA = "cat-a-" + Guid.NewGuid(), _subB = "cat-b-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _app = null!;

    public CatalogCandidateTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        _app = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ICatalogProvider>();
            s.AddSingleton<ICatalogProvider>(_provider);
        }));
        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach ((Guid tenant, Guid owner, string sub) in new[] { (_a, _ownerA, _subA), (_b, _ownerB, _subB) })
        {
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مالک')", owner, ApiFactory.Issuer, sub));
            await uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
            {
                await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'کاتالوگ', 'supermarket')", tenant);
                await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", tenant, owner);
                return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, gen_random_uuid(), $2, 'TenantOwner', 'Tenant')",
                    tenant, owner);
            });
        }
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

    private HttpClient As(string sub, Guid tenant)
    {
        HttpClient c = _app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(sub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, tenant.ToString());
        return c;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    /// <summary>A fresh valid EAN-13 (prefix 626) per test, since the global catalog is shared by every run.</summary>
    private static string Ean()
    {
        string body = "626" + Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        int sum = body.Select((ch, i) => (ch - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return body + ((10 - sum % 10) % 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>What moderation (US-047) publishes; the application roles cannot write the global catalog (T244).</summary>
    private static async Task<Guid> Publish(string ean, string name, string? brand)
    {
        Guid id = Guid.CreateVersion7();
        string adminCs = new NpgsqlConnectionStringBuilder(Cs) { Username = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres",
            Password = Environment.GetEnvironmentVariable("PGPASSWORD") }.ToString();
        await using var admin = NpgsqlDataSource.Create(adminCs);
        await using NpgsqlBatch b = admin.CreateBatch();
        b.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO catalog.catalog_product (id, name, brand) VALUES ($1, $2, $3)")
        {
            Parameters = { new NpgsqlParameter { Value = id }, new NpgsqlParameter { Value = name }, new NpgsqlParameter { Value = (object?)brand ?? DBNull.Value } },
        });
        b.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO catalog.global_barcode (gtin14, catalog_product_id) VALUES ($1, $2)")
        {
            Parameters = { new NpgsqlParameter { Value = "0" + ean }, new NpgsqlParameter { Value = id } },
        });
        await b.ExecuteNonQueryAsync();
        return id;
    }

    private Task<HttpResponseMessage> Resolve(string sub, Guid tenant, string barcode, string[]? sources = null) =>
        As(sub, tenant).PostAsJsonAsync("/api/v1/catalog/barcodes/resolve", new { barcode, branchId = Guid.NewGuid(), preferredSources = sources });

    [DbFact]
    public async Task AT025_a_moderated_global_product_is_a_preselected_candidate_and_is_adopted_only_with_confirmation()
    {
        string ean = Ean();
        Guid catalogId = await Publish(ean, "شیر پرچرب ۱ لیتری", "پگاه");
        JsonElement r = await Body(await Resolve(_subA, _a, ean, ["tenant", "global"]));
        Assert.Equal("candidates", r.GetProperty("kind").GetString());
        JsonElement c = r.GetProperty("candidates").EnumerateArray().Single();
        Assert.Equal(("global", "0.95", true, catalogId), (c.GetProperty("source").GetString(), c.GetProperty("confidence").GetString(),
            c.GetProperty("preselect").GetBoolean(), c.GetProperty("catalogProductId").GetGuid()));
        Assert.Equal(0, _provider.Calls);   // not asked: the caller left providers out

        // AT-201: saving the suggestion without the explicit confirmation is refused, with it the product is the store's
        object Product(bool confirmed) => new
        {
            name = "شیر پرچرب ۱ لیتری", baseUnit = "EA", catalogProductId = catalogId, confirmation = new { confirmed, sourceCandidate = 0 },
            variants = new[] { new { sku = "MILK-" + ean, barcodes = new[] { new { code = ean } } } },
        };
        var unconfirmed = new HttpRequestMessage(HttpMethod.Post, "/api/v1/products") { Content = JsonContent.Create(Product(false)) };
        unconfirmed.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        HttpResponseMessage refused = await As(_subA, _a).SendAsync(unconfirmed);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("confirmation-required", (await Body(refused)).GetProperty("code").GetString());

        var confirmed = new HttpRequestMessage(HttpMethod.Post, "/api/v1/products") { Content = JsonContent.Create(Product(true)) };
        confirmed.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Created, (await As(_subA, _a).SendAsync(confirmed)).StatusCode);
        Assert.Equal("known", (await Body(await Resolve(_subA, _a, ean))).GetProperty("kind").GetString());

        await using var ds = NpgsqlDataSource.Create(Cs);
        Guid? linked = await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_a)), async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT catalog_product_id FROM merch.tenant_product WHERE name = 'شیر پرچرب ۱ لیتری'");
            return (Guid?)await cmd.ExecuteScalarAsync(ct);
        });
        Assert.Equal(catalogId, linked);
    }

    [DbFact]
    public async Task AT202_another_stores_product_price_and_ids_never_appear_in_a_resolve()
    {
        string ean = Ean();
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/products")
        {
            Content = JsonContent.Create(new
            {
                name = "کالای خصوصی الف", baseUnit = "EA", confirmation = new { confirmed = true },
                variants = new[] { new { sku = "PRIV-" + ean, unitPrice = "100000", barcodes = new[] { new { code = ean } } } },
            }),
        };
        create.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        HttpResponseMessage created = await As(_subA, _a).SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string productId = (await Body(created)).GetProperty("id").GetString()!;

        string raw = await (await Resolve(_subB, _b, ean)).Content.ReadAsStringAsync();
        Assert.Contains("\"not_found\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("100000", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(productId, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("کالای خصوصی", raw, StringComparison.Ordinal);
    }

    [DbFact]
    public async Task A_providers_suggestion_is_below_the_threshold_conflicts_with_another_name_and_is_cached()
    {
        string ean = Ean();
        await Publish(ean, "آب معدنی", "دماوند");
        _provider.Answer = _ => Task.FromResult(new ProviderLookup(true, new CandidateFields("نوشابه", "دماوند", null, "1.5 L", null), false));
        JsonElement r = await Body(await Resolve(_subA, _a, ean));
        JsonElement[] c = [.. r.GetProperty("candidates").EnumerateArray()];
        Assert.Equal(["global", "open_food_facts"], c.Select(x => x.GetProperty("source").GetString()));
        Assert.Equal(("0.70", "ODbL-1.0"), (c[1].GetProperty("confidence").GetString(), c[1].GetProperty("license").GetString()));
        Assert.Equal([1], c[0].GetProperty("conflictsWith").EnumerateArray().Select(x => x.GetInt32()));
        Assert.All(c, x => Assert.False(x.GetProperty("preselect").GetBoolean()));   // UC-01 6a: conflicting, none pre-selected
        int calls = _provider.Calls;
        await Resolve(_subB, _b, ean);
        Assert.Equal(calls, _provider.Calls);   // the provider's answer is cached for every store
    }

    [DbFact]
    public async Task AT200_AT203_a_bad_code_asks_no_provider_and_a_hung_provider_answers_degraded_within_5_seconds()
    {
        _provider.Answer = async _ => { await Task.Delay(TimeSpan.FromSeconds(30)); return ProviderLookup.Miss; };
        int before = _provider.Calls;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Resolve(_subA, _a, "6260000000014")).StatusCode);   // wrong check digit
        Assert.Equal(before, _provider.Calls);

        var watch = Stopwatch.StartNew();
        JsonElement r = await Body(await Resolve(_subA, _a, Ean()));
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{watch.Elapsed}");
        Assert.Equal(("not_found", true, 0), (r.GetProperty("kind").GetString(), r.GetProperty("degraded").GetBoolean(), r.GetProperty("candidates").GetArrayLength()));
    }
}
