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
/// US-172 / BR-086 / DI-085 / D-32: the platform publishes versions of an industry's receipt template (platform realm,
/// PlatformSuperAdmin with MFA; TM-071 cross-realm tokens refused); a store sees its industry's newest version and changes
/// only its footer and signature.
/// </summary>
public sealed class PlatformTemplateTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _owner = Guid.CreateVersion7(), _cashier = Guid.CreateVersion7();
    private readonly string _ownerSub = "tpl-owner-" + Guid.NewGuid(), _cashierSub = "tpl-cashier-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public PlatformTemplateTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach ((Guid id, string sub) in new[] { (_owner, _ownerSub), (_cashier, _cashierSub) })
        {
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'کاربر')", id, ApiFactory.Issuer, sub));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'پوشاک آزمون', 'apparel')", _tenant);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2), ($1, $3)", _tenant, _owner, _cashier);
            return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES " +
                                     "($1, gen_random_uuid(), $2, 'TenantOwner', 'Tenant'), ($1, gen_random_uuid(), $3, 'Cashier', 'Tenant')", _tenant, _owner, _cashier);
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

    private HttpClient Merchant(string sub)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(sub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, _tenant.ToString());
        return c;
    }

    private HttpClient Platform(string? role = "PlatformSuperAdmin", string? acr = "mfa", string issuer = ApiFactory.PlatformIssuer,
        string audience = ApiFactory.PlatformAudience)
    {
        HttpClient c = _factory.CreateClient();
        var claims = new Dictionary<string, object> { ["realm_access"] = new Dictionary<string, object> { ["roles"] = role is null ? Array.Empty<string>() : new[] { role } } };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ApiFactory.Token("operator-1", issuer, audience, acr: acr, authTime: DateTimeOffset.UtcNow, extraClaims: claims));
        return c;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    private static async Task<JsonElement> Apparel(HttpClient platform) =>
        (await Body(await platform.GetAsync("/api/v1/platform/industry-profiles"))).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("code").GetString() == "apparel");

    private static object Publish(int expected, string legalText, string[] locked) =>
        new { expectedVersion = expected, legalText, lockedFields = locked, reason = "متن تأییدشده مشاور (آزمون)" };

    private static readonly string[] Locked = ["legalName", "economicCode", "businessLicenseNo", "invoiceNo", "issuedAt", "lines", "totals", "tax", "qrCode"];

    [DbFact]
    public async Task BR086_the_platform_publishes_a_new_version_and_a_store_of_that_industry_prints_with_it()
    {
        HttpClient platform = Platform();
        int current = (await Apparel(platform)).GetProperty("version").GetInt32();
        string text = "متن قانونی پوشاک نسخه " + Guid.NewGuid().ToString("N")[..6];

        HttpResponseMessage published = await platform.PutAsJsonAsync("/api/v1/platform/industry-profiles/apparel", Publish(current, text, Locked));
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        JsonElement v = await Body(published);
        Assert.Equal(($"apparel@{current + 1}", text), (v.GetProperty("receiptTemplateVersion").GetString(), v.GetProperty("legalText").GetString()));

        // the same expected version again lost the race: a version is published once
        HttpResponseMessage stale = await platform.PutAsJsonAsync("/api/v1/platform/industry-profiles/apparel", Publish(current, "دیگر", Locked));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await platform.PutAsJsonAsync("/api/v1/platform/industry-profiles/apparel", Publish(current + 1, "متن", ["invoiceNo", "favouriteColour"]))).StatusCode);
        HttpResponseMessage unlocked = await platform.PutAsJsonAsync("/api/v1/platform/industry-profiles/apparel", Publish(current + 1, "متن", ["invoiceNo", "lines"]));
        Assert.Equal(HttpStatusCode.BadRequest, unlocked.StatusCode);   // legalName, issuedAt and totals are always on a receipt
        Assert.Equal(HttpStatusCode.NotFound,
            (await platform.PutAsJsonAsync("/api/v1/platform/industry-profiles/bakery", Publish(1, "متن", Locked))).StatusCode);

        JsonElement mine = await Body(await Merchant(_cashierSub).GetAsync("/api/v1/templates/receipt"));
        Assert.StartsWith("apparel@", mine.GetProperty("industryTemplateVersion").GetString(), StringComparison.Ordinal);
        Assert.True(int.Parse(mine.GetProperty("industryTemplateVersion").GetString()!["apparel@".Length..], System.Globalization.CultureInfo.InvariantCulture) >= current + 1);
        Assert.Contains("businessLicenseNo", mine.GetProperty("lockedFields").EnumerateArray().Select(f => f.GetString()));
    }

    [DbFact]
    public async Task TM071_only_a_platform_admin_with_a_second_factor_of_the_platform_realm_reaches_the_platform_API()
    {
        Assert.Equal(HttpStatusCode.OK, (await Platform().GetAsync("/api/v1/platform/industry-profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Merchant(_ownerSub).GetAsync("/api/v1/platform/industry-profiles")).StatusCode);   // another realm
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Platform(issuer: ApiFactory.Issuer).GetAsync("/api/v1/platform/industry-profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Platform(audience: ApiFactory.Audience).GetAsync("/api/v1/platform/industry-profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Platform(role: "PlatformSupport").GetAsync("/api/v1/platform/industry-profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Platform(acr: null).GetAsync("/api/v1/platform/industry-profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Platform().GetAsync("/api/v1/me")).StatusCode);   // and a platform token is no merchant
    }

    [DbFact]
    public async Task DI085_a_store_changes_only_its_footer_and_signature_never_the_legal_text_or_the_locked_fields()
    {
        HttpClient owner = Merchant(_ownerSub);
        JsonElement before = await Body(await owner.GetAsync("/api/v1/templates/receipt"));
        Assert.Equal(0, before.GetProperty("version").GetInt32());
        string legal = before.GetProperty("legalText").GetString()!;

        HttpResponseMessage saved = await owner.PutAsJsonAsync("/api/v1/templates/receipt",
            new { expectedVersion = 0, overrides = new { footerText = "  با سپاس از خرید شما  ", signature = "فروشگاه نمونه" } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        JsonElement after = await Body(saved);
        Assert.Equal((1, "با سپاس از خرید شما", legal), (after.GetProperty("version").GetInt32(),
            after.GetProperty("overrides").GetProperty("footerText").GetString(), after.GetProperty("legalText").GetString()));

        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync("/api/v1/templates/receipt",
            new { expectedVersion = 0, overrides = new { footerText = "دیگر" } })).StatusCode);
        // there is nowhere to put the legal text or the locked fields: such a body is refused by name (SEC-T-013)
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/templates/receipt",
            new { expectedVersion = 1, overrides = new { legalText = "متن من" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/templates/receipt",
            new { expectedVersion = 1, lockedFields = Array.Empty<string>(), overrides = new { footerText = "x" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/templates/receipt",
            new { expectedVersion = 1, overrides = new { logoObjectKey = $"tenants/{_tenant}/logos/a.png" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Merchant(_cashierSub).PutAsJsonAsync("/api/v1/templates/receipt",
            new { expectedVersion = 1, overrides = new { footerText = "صندوق‌دار" } })).StatusCode);

        JsonElement now = await Body(await Merchant(_cashierSub).GetAsync("/api/v1/templates/receipt"));
        Assert.Equal("فروشگاه نمونه", now.GetProperty("overrides").GetProperty("signature").GetString());
    }
}
