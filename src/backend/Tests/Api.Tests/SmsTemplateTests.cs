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

/// <summary>US-152 / NOT-009 / AT-147: a store changes only its signed name and a signature of its SMS texts, in versions.</summary>
public sealed class SmsTemplateTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _owner = Guid.CreateVersion7(), _cashier = Guid.CreateVersion7();
    private readonly string _ownerSub = "sms-owner-" + Guid.NewGuid(), _cashierSub = "sms-cashier-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public SmsTemplateTests(ApiFactory factory) => _factory = factory;

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
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'پیامک آزمون', 'general')", _tenant);
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

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());



    [DbFact]
    public async Task AT147_a_store_sets_its_name_and_signature_version_by_version_and_the_platform_text_stays()
    {
        HttpClient owner = Merchant(_ownerSub);
        JsonElement[] start = [.. (await Body(await owner.GetAsync("/api/v1/settings/notification-templates"))).GetProperty("items").EnumerateArray()];
        Assert.Equal(["receipt", "due-reminder"], start.Select(t => t.GetProperty("code").GetString()));
        Assert.All(start, t => Assert.Equal(0, t.GetProperty("version").GetInt32()));
        string platformText = start[0].GetProperty("platformText").GetString()!;
        Assert.Contains("{storeName}", platformText, StringComparison.Ordinal);

        HttpResponseMessage saved = await owner.PutAsJsonAsync("/api/v1/settings/notification-templates/receipt",
            new { expectedVersion = 0, storeName = "نان‌آور", signature = "با تشکر" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        JsonElement v1 = await Body(saved);
        Assert.Equal((1, "نان‌آور", platformText), (v1.GetProperty("version").GetInt32(), v1.GetProperty("storeName").GetString(), v1.GetProperty("platformText").GetString()));
        Assert.StartsWith("نان‌آور:", v1.GetProperty("preview").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("با تشکر", v1.GetProperty("preview").GetString(), StringComparison.Ordinal);

        // the same version again lost the race; links, newlines and too-long names are refused; an unknown template is not found
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync("/api/v1/settings/notification-templates/receipt", new { expectedVersion = 0, storeName = "دیگر" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/settings/notification-templates/receipt", new { expectedVersion = 1, storeName = "http://x.ir" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/settings/notification-templates/receipt", new { expectedVersion = 1, signature = "ا\nب" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/settings/notification-templates/receipt", new { expectedVersion = 1, storeName = new string('ا', 31) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/settings/notification-templates/receipt", new { expectedVersion = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync("/api/v1/settings/notification-templates/internal", new { expectedVersion = 0, storeName = "x" })).StatusCode);

        // the cashier may not
        Assert.Equal(HttpStatusCode.Forbidden, (await Merchant(_cashierSub).GetAsync("/api/v1/settings/notification-templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Merchant(_cashierSub).PutAsJsonAsync("/api/v1/settings/notification-templates/receipt", new { expectedVersion = 1, storeName = "x" })).StatusCode);
    }
}
