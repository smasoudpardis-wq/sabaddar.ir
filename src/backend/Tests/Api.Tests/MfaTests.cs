using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RetailCloud.BuildingBlocks.Api;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;

namespace RetailCloud.Api.Tests;

/// <summary>
/// US-022 / IAM-003 / AT-012 / AT-290: a sensitive role in a tenant needs an MFA token (acr=mfa) for that tenant only;
/// step-up (ABAC-05) needs a recent second factor.
/// </summary>
public sealed class MfaTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _owned = Guid.CreateVersion7(), _cashierAt = Guid.CreateVersion7(), _accountantAt = Guid.CreateVersion7();
    private readonly string _sub = "mfa-user-" + Guid.NewGuid();

    public MfaTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        string? cs = Environment.GetEnvironmentVariable("RC_TEST_PG");
        if (string.IsNullOrWhiteSpace(cs))
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(cs);
        var uow = new PostgresUnitOfWork(ds);
        Guid user = Guid.CreateVersion7();
        await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
            Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'کاربر چندنقشی')", user, ApiFactory.Issuer, _sub));
        foreach ((Guid t, string role) in new[] { (_owned, "TenantOwner"), (_cashierAt, "Cashier"), (_accountantAt, "Accountant") })
        {
            await uow.ExecuteAsync(new FixedTenantContext(new TenantId(t)), async (s, ct) =>
            {
                await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, $2, 'supermarket')", t, role);
                return await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id, roles) VALUES ($1, $2, $3)", t, user, new[] { role });
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

    private HttpClient Client(string token, Guid? tenant = null)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (tenant is not null)
        {
            c.DefaultRequestHeaders.Add("X-RC-Tenant", tenant.Value.ToString());
        }

        return c;
    }

    private static async Task<string?> Code(HttpResponseMessage r)
    {
        using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString();
    }

    private string Pwd => ApiFactory.Token(_sub, acr: "pwd", authTime: DateTimeOffset.UtcNow);

    private string Mfa => ApiFactory.Token(_sub, acr: "mfa", authTime: DateTimeOffset.UtcNow);

    [DbFact]
    public async Task Selecting_a_tenant_where_the_role_needs_mfa_requires_an_mfa_token()
    {
        foreach (Guid sensitive in new[] { _owned, _accountantAt })
        {
            HttpResponseMessage r = await Client(Pwd).PostAsJsonAsync("/api/v1/me/active-tenant", new { tenantId = sensitive });
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("mfa-required", await Code(r));
            Assert.Equal(HttpStatusCode.NoContent, (await Client(Mfa).PostAsJsonAsync("/api/v1/me/active-tenant", new { tenantId = sensitive })).StatusCode);
        }

        // the same user as Cashier elsewhere is not forced to use MFA (IAM-003 applies per role)
        Assert.Equal(HttpStatusCode.NoContent, (await Client(Pwd).PostAsJsonAsync("/api/v1/me/active-tenant", new { tenantId = _cashierAt })).StatusCode);
    }

    [DbFact]
    public async Task Every_tenant_scoped_call_enforces_mfa_not_only_the_selection()
    {
        HttpResponseMessage r = await Client(Pwd, _owned).GetAsync("/api/v1/tenants/me"); // AT-290
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("mfa-required", await Code(r));
        Assert.DoesNotContain("TenantOwner", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await Client(Mfa, _owned).GetAsync("/api/v1/tenants/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(Pwd, _cashierAt).GetAsync("/api/v1/tenants/me")).StatusCode);
    }

    [DbFact]
    public async Task Only_the_exact_acr_mfa_counts()
    {
        foreach (string? acr in new[] { null, "MFA", "pwd", "mfa ", "2" })
        {
            HttpResponseMessage r = await Client(ApiFactory.Token(_sub, acr: acr, authTime: DateTimeOffset.UtcNow), _owned).GetAsync("/api/v1/tenants/me");
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }
    }

    [DbFact]
    public async Task Me_reports_roles_which_memberships_need_mfa_and_the_token_state()
    {
        DateTimeOffset at = DateTimeOffset.UtcNow.AddMinutes(-2);
        using JsonDocument doc = JsonDocument.Parse(await (await Client(ApiFactory.Token(_sub, acr: "mfa", authTime: at)).GetAsync("/api/v1/me")).Content.ReadAsStringAsync());
        Dictionary<Guid, (string Role, bool Mfa)> m = doc.RootElement.GetProperty("memberships").EnumerateArray().ToDictionary(
            e => e.GetProperty("tenantId").GetGuid(),
            e => (e.GetProperty("roles")[0].GetString()!, e.GetProperty("mfaRequired").GetBoolean()));
        Assert.Equal(("TenantOwner", true), m[_owned]);
        Assert.Equal(("Accountant", true), m[_accountantAt]);
        Assert.Equal(("Cashier", false), m[_cashierAt]);
        Assert.True(doc.RootElement.GetProperty("mfaSatisfied").GetBoolean());
        Assert.Equal(at.ToUnixTimeSeconds() + 900, doc.RootElement.GetProperty("mfaFreshUntil").GetDateTimeOffset().ToUnixTimeSeconds());

        using JsonDocument pwd = JsonDocument.Parse(await (await Client(Pwd).GetAsync("/api/v1/me")).Content.ReadAsStringAsync());
        Assert.False(pwd.RootElement.GetProperty("mfaSatisfied").GetBoolean());
        Assert.Equal(JsonValueKind.Null, pwd.RootElement.GetProperty("mfaFreshUntil").ValueKind);
    }

    // ABAC-05 step-up filter: unit-level, because no sensitive endpoint exists yet (tax.manage etc. arrive later).
    [Theory]
    [InlineData("mfa", -60, true)]       // MFA one minute ago
    [InlineData("mfa", -14 * 60, true)]  // within 15 minutes
    [InlineData("mfa", -16 * 60, false)] // stale
    [InlineData("pwd", -10, false)]      // fresh but no second factor
    [InlineData("mfa", 5 * 60, false)]   // auth_time in the future (clock games)
    [InlineData(null, null, false)]
    public async Task Step_up_filter_requires_recent_mfa(string? acr, int? authOffsetSeconds, bool allowed)
    {
        var now = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
        var ctx = new RequestContext { Acr = acr, AuthTime = authOffsetSeconds is { } o ? now.AddSeconds(o) : null };
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(ctx).AddSingleton<TimeProvider>(new FixedTime(now)).BuildServiceProvider(),
        };
        var filter = new RequireRecentMfaFilter(MfaPolicy.SensitiveFreshness);
        object? result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http), _ => ValueTask.FromResult<object?>("handler ran"));
        if (allowed)
        {
            Assert.Equal("handler ran", result);
        }
        else
        {
            var problem = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
            var details = Assert.IsAssignableFrom<IValueHttpResult>(result).Value as Microsoft.AspNetCore.Mvc.ProblemDetails;
            Assert.Equal("step-up-required", details!.Extensions["code"]);
            Assert.Equal(900, details.Extensions["maxAgeSeconds"]);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
