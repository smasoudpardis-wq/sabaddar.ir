using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;

namespace RetailCloud.Api.Tests;

/// <summary>
/// US-021 (API side): token validation (AUTH-05, BR-004), user provisioning, tenant selection gated by membership
/// (BR-001), 404 for other tenants (BR-003), RLS-backed reads. Mirrors SEC-T-010/012 for the first endpoints.
/// </summary>
public sealed class AuthAndTenantTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenantA = Guid.CreateVersion7(), _tenantB = Guid.CreateVersion7(), _tenantC = Guid.CreateVersion7();
    private readonly string _ownerSub = "owner-" + Guid.NewGuid(), _strangerSub = "stranger-" + Guid.NewGuid(), _disabledSub = "disabled-" + Guid.NewGuid();

    public AuthAndTenantTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        string? cs = Environment.GetEnvironmentVariable("RC_TEST_PG");
        if (string.IsNullOrWhiteSpace(cs))
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach ((Guid id, string name) in new[] { (_tenantA, "فروشگاه الف"), (_tenantB, "فروشگاه ب"), (_tenantC, "فروشگاه ج") })
        {
            await uow.ExecuteAsync(new FixedTenantContext(new TenantId(id)), (s, ct) =>
                Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, $2, 'supermarket')", id, name));
        }

        Guid owner = Guid.CreateVersion7(), stranger = Guid.CreateVersion7(), disabled = Guid.CreateVersion7();
        await uow.ExecuteWithoutTenantAsync(null, async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مالک')", owner, ApiFactory.Issuer, _ownerSub);
            await Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'غریبه')", stranger, ApiFactory.Issuer, _strangerSub);
            return await Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name, status) VALUES ($1, $2, $3, 'غیرفعال', 'Disabled')", disabled, ApiFactory.Issuer, _disabledSub);
        });
        foreach (Guid t in new[] { _tenantA, _tenantB })
        {
            await uow.ExecuteAsync(new FixedTenantContext(new TenantId(t)), (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", t, owner));
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

    private HttpClient Client(string? token, Guid? tenant = null)
    {
        HttpClient c = _factory.CreateClient();
        if (token is not null)
        {
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (tenant is not null)
        {
            c.DefaultRequestHeaders.Add("X-RC-Tenant", tenant.Value.ToString());
        }

        return c;
    }

    private static async Task<string?> Code(HttpResponseMessage r)
    {
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("traceId", out _));
        return doc.RootElement.GetProperty("code").GetString();
    }

    [DbFact]
    public async Task Health_is_anonymous_but_api_requires_a_token()
    {
        Assert.Equal(HttpStatusCode.OK, (await Client(null).GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(null).GetAsync("/api/v1/me")).StatusCode);
    }

    [DbFact]
    public async Task Tokens_with_wrong_audience_issuer_expiry_or_signature_are_rejected()
    {
        string[] bad =
        [
            ApiFactory.Token(_ownerSub, audience: "platform-api"),                                       // BR-004
            ApiFactory.Token(_ownerSub, issuer: "https://id.test/realms/platform"),                     // other realm
            ApiFactory.Token(_ownerSub, expires: DateTime.UtcNow.AddMinutes(-5)),                       // AUTH-05
            ApiFactory.Token(_ownerSub, key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-key" }), // forged
        ];
        foreach (string t in bad)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Client(t).GetAsync("/api/v1/me")).StatusCode);
        }
    }

    [DbFact]
    public async Task Valid_token_for_an_unknown_or_disabled_user_is_forbidden()
    {
        HttpResponseMessage unknown = await Client(ApiFactory.Token("nobody-" + Guid.NewGuid())).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Equal("user-not-provisioned", await Code(unknown));

        HttpResponseMessage disabled = await Client(ApiFactory.Token(_disabledSub)).GetAsync("/api/v1/me"); // IAM-009
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
    }

    [DbFact]
    public async Task Me_lists_only_the_callers_own_memberships()
    {
        HttpResponseMessage r = await Client(ApiFactory.Token(_ownerSub)).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var ids = doc.RootElement.GetProperty("memberships").EnumerateArray()
            .Select(m => m.GetProperty("tenantId").GetGuid()).ToHashSet();
        Assert.Equal(new HashSet<Guid> { _tenantA, _tenantB }, ids);

        using JsonDocument none = JsonDocument.Parse(await (await Client(ApiFactory.Token(_strangerSub)).GetAsync("/api/v1/me")).Content.ReadAsStringAsync());
        Assert.Empty(none.RootElement.GetProperty("memberships").EnumerateArray());
    }

    [DbFact]
    public async Task Selecting_a_tenant_requires_membership_and_hides_existence_of_others()
    {
        HttpClient owner = Client(ApiFactory.Token(_ownerSub));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync("/api/v1/me/active-tenant", new { tenantId = _tenantA })).StatusCode);

        HttpResponseMessage existingNotMine = await owner.PostAsJsonAsync("/api/v1/me/active-tenant", new { tenantId = _tenantC });
        HttpResponseMessage nonExistent = await owner.PostAsJsonAsync("/api/v1/me/active-tenant", new { tenantId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, existingNotMine.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nonExistent.StatusCode);
        Assert.Equal(await Code(existingNotMine), await Code(nonExistent)); // BR-003: indistinguishable
    }

    [DbFact]
    public async Task Tenant_scoped_reads_use_the_selected_tenant_and_reject_foreign_tenant_headers()
    {
        HttpResponseMessage a = await Client(ApiFactory.Token(_ownerSub), _tenantA).GetAsync("/api/v1/tenants/me");
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        using (JsonDocument doc = JsonDocument.Parse(await a.Content.ReadAsStringAsync()))
        {
            Assert.Equal(_tenantA, doc.RootElement.GetProperty("id").GetGuid());
            Assert.Equal("فروشگاه الف", doc.RootElement.GetProperty("legalName").GetString());
        }

        HttpResponseMessage foreign = await Client(ApiFactory.Token(_ownerSub), _tenantC).GetAsync("/api/v1/tenants/me");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.DoesNotContain("فروشگاه ج", await foreign.Content.ReadAsStringAsync());

        HttpResponseMessage strangerOnA = await Client(ApiFactory.Token(_strangerSub), _tenantA).GetAsync("/api/v1/tenants/me");
        Assert.Equal(HttpStatusCode.NotFound, strangerOnA.StatusCode);

        HttpResponseMessage garbage = await Client(ApiFactory.Token(_ownerSub)).SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants/me")
        {
            Headers = { { "X-RC-Tenant", "not-a-guid" } },
        });
        Assert.Equal(HttpStatusCode.NotFound, garbage.StatusCode);
    }

    [DbFact]
    public async Task Tenant_scoped_endpoint_without_selection_is_refused()
    {
        HttpResponseMessage r = await Client(ApiFactory.Token(_ownerSub)).GetAsync("/api/v1/tenants/me");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("tenant-not-selected", await Code(r));
    }
}
