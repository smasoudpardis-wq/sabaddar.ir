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
/// US-162 / INT-001 (AT-180): a store's integration key — scoped, expiring, revocable, shown once — and the partner API it opens:
/// one store only, the key's scopes only, quantities without cost, and a rate limit per key.
/// </summary>
public sealed class IntegrationKeyTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private sealed record Store(Guid Tenant, Guid Owner, Guid Warehouse, string Sub);

    private readonly ApiFactory _factory;
    private Store _a = null!, _b = null!;
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public IntegrationKeyTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        _a = await NewStore("کلید الف");
        _b = await NewStore("کلید ب");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<Store> NewStore(string name)
    {
        Guid tenant = Guid.CreateVersion7(), owner = Guid.CreateVersion7(), branch = Guid.CreateVersion7(), warehouse = Guid.CreateVersion7();
        string sub = "intkey-owner-" + Guid.NewGuid();
        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
            Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مالک')", owner, ApiFactory.Issuer, sub));
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, $2, 'supermarket')", tenant, name);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک')", tenant, branch);
            await Exec(s, ct, "INSERT INTO inv.warehouse (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'W1')", tenant, warehouse, branch);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", tenant, owner);
            return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, gen_random_uuid(), $2, 'TenantOwner', 'Tenant')",
                tenant, owner);
        });
        await TestAccounting.SetUpAsync(Cs, tenant);
        return new Store(tenant, owner, warehouse, sub);
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

    private HttpClient Owner(Store st, DateTimeOffset? authTime = null)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(st.Sub, acr: "mfa", authTime: authTime ?? DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, st.Tenant.ToString());
        return c;
    }

    private HttpClient Partner(string key)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", key);
        return c;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    private static async Task<HttpResponseMessage> Idem(HttpClient c, string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await c.SendAsync(req);
    }

    private static Task<HttpResponseMessage> CreateKey(HttpClient c, string[] scopes, int days = 30, string name = "فروشگاه اینترنتی") =>
        c.PostAsJsonAsync("/api/v1/integrations/keys", new { name, scopes, expiresAt = DateTimeOffset.UtcNow.AddDays(days) });

    private async Task<(Guid Id, string Key)> Key(Store st, params string[] scopes)
    {
        HttpResponseMessage r = await CreateKey(Owner(st), scopes);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        JsonElement b = await Body(r);
        return (b.GetProperty("id").GetGuid(), b.GetProperty("secret").GetString()!);
    }

    private static string Ean()
    {
        string body = "626" + Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        int sum = body.Select((ch, i) => (ch - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return body + ((10 - sum % 10) % 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<Guid> Product(Store st, string name)
    {
        HttpResponseMessage r = await Idem(Owner(st), "/api/v1/products", new
        {
            name, baseUnit = "EA", confirmation = new { confirmed = true },
            openingStock = new { warehouseId = st.Warehouse, qty = "3", unitCost = "40000" },
            variants = new[] { new { sku = "IK-" + Ean(), unitPrice = "50000", barcodes = new[] { new { code = Ean() } } } },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Body(r)).GetProperty("id").GetGuid();
    }

    [DbFact]
    public async Task A_key_is_shown_once_and_opens_only_its_store_for_its_scopes()
    {
        Guid mine = await Product(_a, "چای کلید");
        Guid theirs = await Product(_b, "قهوه دیگری");
        HttpResponseMessage created = await CreateKey(Owner(_a), ["catalog.read", "inventory.read"]);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonElement k = await Body(created);
        string key = k.GetProperty("secret").GetString()!;
        Assert.StartsWith("rck_" + k.GetProperty("prefix").GetString() + "_", key, StringComparison.Ordinal);
        Assert.Equal("Active", k.GetProperty("status").GetString());

        // the list never carries the secret, and the database keeps only its hash
        JsonElement listed = (await Body(await Owner(_a).GetAsync("/api/v1/integrations/keys"))).GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("secret").ValueKind);
        Assert.DoesNotContain(key.Split('_')[2], await Owner(_a).GetStringAsync("/api/v1/integrations/keys"), StringComparison.Ordinal);

        JsonElement products = await Body(await Partner(key).GetAsync("/api/v1/partner/catalog/products"));
        var ids = products.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(mine, ids);
        Assert.DoesNotContain(theirs, ids);

        HttpResponseMessage balances = await Partner(key).GetAsync("/api/v1/partner/inventory/balances");
        Assert.Equal(HttpStatusCode.OK, balances.StatusCode);
        string raw = await balances.Content.ReadAsStringAsync();
        JsonElement row = JsonElement.Parse(raw).GetProperty("items").EnumerateArray().Single();
        Assert.Equal("3", row.GetProperty("onHand").GetString());
        Assert.Equal("3", row.GetProperty("available").GetString());
        Assert.DoesNotContain("wac", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("value", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("40000", raw, StringComparison.Ordinal);

        // last use is recorded
        JsonElement used = (await Body(await Owner(_a).GetAsync("/api/v1/integrations/keys"))).GetProperty("items")[0];
        Assert.NotEqual(JsonValueKind.Null, used.GetProperty("lastUsedAt").ValueKind);
    }

    [DbFact]
    public async Task A_key_without_the_scope_is_refused_and_a_wrong_or_missing_key_is_not_told_apart()
    {
        var (_, inventoryOnly) = await Key(_a, "inventory.read");
        HttpResponseMessage noScope = await Partner(inventoryOnly).GetAsync("/api/v1/partner/catalog/products");
        Assert.Equal(HttpStatusCode.Forbidden, noScope.StatusCode);
        Assert.Equal("scope-missing", (await Body(noScope)).GetProperty("code").GetString());

        string wrong = inventoryOnly[..^4] + (inventoryOnly.EndsWith("AAAA", StringComparison.Ordinal) ? "BBBB" : "AAAA");
        foreach (HttpClient c in new[] { Partner(wrong), Partner("rck_0000000000_" + new string('x', 43)), Partner("not-a-key"), _factory.CreateClient() })
        {
            HttpResponseMessage r = await c.GetAsync("/api/v1/partner/inventory/balances");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Equal("invalid-api-key", (await Body(r)).GetProperty("code").GetString());
        }

        // a merchant session is not a partner key (TM-071)
        Assert.Equal(HttpStatusCode.Unauthorized, (await Owner(_a).GetAsync("/api/v1/partner/inventory/balances")).StatusCode);
    }

    [DbFact]
    public async Task A_revoked_or_expired_key_stops_at_once()
    {
        var (id, key) = await Key(_a, "inventory.read");
        Assert.Equal(HttpStatusCode.OK, (await Partner(key).GetAsync("/api/v1/partner/inventory/balances")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Owner(_a).DeleteAsync($"/api/v1/integrations/keys/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Partner(key).GetAsync("/api/v1/partner/inventory/balances")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Owner(_a).DeleteAsync($"/api/v1/integrations/keys/{id}")).StatusCode);   // once
        JsonElement listed = (await Body(await Owner(_a).GetAsync("/api/v1/integrations/keys"))).GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == id);
        Assert.Equal("Revoked", listed.GetProperty("status").GetString());

        // another store cannot revoke it
        var (other, otherKey) = await Key(_a, "inventory.read");
        Assert.Equal(HttpStatusCode.NotFound, (await Owner(_b).DeleteAsync($"/api/v1/integrations/keys/{other}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Partner(otherKey).GetAsync("/api/v1/partner/inventory/balances")).StatusCode);

        await using var ds = NpgsqlDataSource.Create(Cs);
        await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_a.Tenant)), (s, ct) =>
            Exec(s, ct, "UPDATE platform.integration_key SET created_at = now() - interval '40 days', expires_at = now() - interval '1 second' WHERE id = $1", other));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Partner(otherKey).GetAsync("/api/v1/partner/inventory/balances")).StatusCode);
        JsonElement expired = (await Body(await Owner(_a).GetAsync("/api/v1/integrations/keys"))).GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == other);
        Assert.Equal("Expired", expired.GetProperty("status").GetString());
    }

    [DbFact]
    public async Task Creating_a_key_is_validated_needs_a_recent_second_factor_and_is_audited()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateKey(Owner(_a), ["catalog.read"], days: 400)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateKey(Owner(_a), ["catalog.write"])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateKey(Owner(_a), [])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateKey(Owner(_a), ["catalog.read"], name: " ")).StatusCode);

        HttpResponseMessage stale = await CreateKey(Owner(_a, DateTimeOffset.UtcNow.AddHours(-1)), ["catalog.read"]);
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        Assert.Equal("step-up-required", (await Body(stale)).GetProperty("code").GetString());

        var (id, key) = await Key(_a, "catalog.read");
        await using var ds = NpgsqlDataSource.Create(Cs);
        long audited = await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_a.Tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(
                "SELECT count(*) FROM audit.audit_record WHERE action = 'integrations.key.created' AND resource_id = $1 AND position($2 in details::text) = 0");
            cmd.Parameters.Add(new NpgsqlParameter { Value = id.ToString() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = key.Split('_')[2] });
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        });
        Assert.Equal(1, audited);   // audited, without the secret
    }

    [DbFact]
    public async Task A_key_is_limited_to_120_calls_a_minute()
    {
        var (_, key) = await Key(_a, "inventory.read");
        HttpClient c = Partner(key);
        var codes = new List<HttpStatusCode>();
        for (int i = 0; i < 121; i++)
        {
            codes.Add((await c.GetAsync("/api/v1/partner/inventory/balances")).StatusCode);
        }

        Assert.All(codes.Take(120), code => Assert.Equal(HttpStatusCode.OK, code));
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[120]);
        var (_, fresh) = await Key(_a, "inventory.read");   // per key, not for the whole store
        Assert.Equal(HttpStatusCode.OK, (await Partner(fresh).GetAsync("/api/v1/partner/inventory/balances")).StatusCode);
    }
}
