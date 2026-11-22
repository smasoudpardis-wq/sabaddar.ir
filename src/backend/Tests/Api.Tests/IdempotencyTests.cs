using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;
using RetailCloud.BuildingBlocks.Api;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.IdentityAccess.Infrastructure;

namespace RetailCloud.Api.Tests;

/// <summary>
/// US-006 / BR-030 / BR-031 / D-34 / SEC-T-016. No BR-030 operation exists yet, so a host built from the real building
/// blocks maps a test command that increments platform.tenant.version: the version counts real executions.
/// </summary>
public sealed class IdempotencyTests : IAsyncLifetime
{
    private readonly Guid _a = Guid.CreateVersion7(), _b = Guid.CreateVersion7();
    private readonly string _sub = "idem-" + Guid.NewGuid();
    private WebApplication? _app;
    private static volatile bool _failNext;

    public sealed record Bump(string Note, int DelayMs = 0);

    public async Task InitializeAsync()
    {
        string? cs = Environment.GetEnvironmentVariable("RC_TEST_PG");
        if (string.IsNullOrWhiteSpace(cs))
        {
            return;
        }

        await using (var ds = NpgsqlDataSource.Create(cs))
        {
            var uow = new PostgresUnitOfWork(ds);
            Guid user = Guid.CreateVersion7();
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'صندوق‌دار')", user, ApiFactory.Issuer, _sub));
            foreach (Guid t in new[] { _a, _b })
            {
                await uow.ExecuteAsync(new FixedTenantContext(new TenantId(t)), async (s, ct) =>
                {
                    await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'idem', 'general')", t);
                    await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", t, user);
                    return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, $2, $3, 'Cashier', 'Tenant')",
                        t, Guid.CreateVersion7(), user);
                });
            }
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration["Auth:Merchant:Authority"] = ApiFactory.Issuer;
        builder.Configuration["Auth:Merchant:Audience"] = ApiFactory.Audience;
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(cs));
        builder.Services.AddSingleton<IUnitOfWork, PostgresUnitOfWork>();
        builder.Services.AddSingleton<IUserDirectory, PostgresUserDirectory>();
        builder.Services.AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>();
        builder.Services.AddRetailCloudApi(builder.Configuration);
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            o.Authority = null;
            var config = new OpenIdConnectConfiguration { Issuer = ApiFactory.Issuer };
            config.SigningKeys.Add(ApiFactory.SigningKey);
            o.Configuration = config;
            o.TokenValidationParameters.IssuerSigningKey = ApiFactory.SigningKey;
        });
        _app = builder.Build();
        _app.UseRetailCloudApi();
        RouteGroupBuilder g = _app.MapGroup("/t").RequireAuthorization(ServiceCollectionExtensions.MerchantPolicy);
        g.MapIdempotentPost<Bump>("/bump/{n}", "test.bump", async (cmd, scope, http, ct) =>
        {
            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)scope).Command(
                "UPDATE platform.tenant SET version = version + 1 RETURNING version, pg_sleep($1 / 1000.0)::text");
            c.Parameters.Add(new NpgsqlParameter { Value = cmd.DelayMs });
            int version = (int)(await c.ExecuteScalarAsync(ct))!;
            if (_failNext)
            {
                return CommandResult.Fail(Problems.Create(http, StatusCodes.Status409Conflict, "insufficient-stock", "Not enough stock"));
            }

            return CommandResult.Created(new { version, note = cmd.Note, at = Guid.CreateVersion7() });
        });
        g.MapGet("/boom", (Func<IResult>)(() => throw new InvalidOperationException("secret detail: SELECT * FROM core.idempotency_record")));
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
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

    private HttpClient Client(Guid? tenant)
    {
        HttpClient c = _app!.GetTestClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(_sub));
        if (tenant is not null)
        {
            c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, tenant.Value.ToString());
        }

        return c;
    }

    private static Task<HttpResponseMessage> Post(HttpClient c, string key, string json, string path = "/t/bump/1")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (key.Length > 0)
        {
            req.Headers.Add(Idempotency.Header, key);
        }

        return c.SendAsync(req);
    }

    private static async Task<string?> Code(HttpResponseMessage r)
    {
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.True(d.RootElement.TryGetProperty("traceId", out _));
        return d.RootElement.GetProperty("code").GetString();
    }

    private static async Task<int> Version(Guid tenant)
    {
        await using var ds = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("RC_TEST_PG")!);
        return await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)s).Command("SELECT version FROM platform.tenant");
            return (int)(await c.ExecuteScalarAsync(ct))!;
        });
    }

    private static string NewKey() => "k-" + Guid.NewGuid().ToString("N");

    [DbFact]
    public async Task Missing_or_malformed_key_is_rejected_before_anything_runs()
    {
        int before = await Version(_a);
        foreach (string key in new[] { "", "short", "has space in the key 123", new string('x', 65), "key-with-ünicode-0000" })
        {
            HttpResponseMessage r = await Post(Client(_a), key, """{"note":"x"}""");
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("idempotency-key-required", await Code(r));
        }

        Assert.Equal(before, await Version(_a));
    }

    [DbFact]
    public async Task Replay_returns_the_stored_response_byte_for_byte_and_runs_once()
    {
        int before = await Version(_a);
        string key = NewKey();
        HttpResponseMessage first = await Post(Client(_a), key, """{"note":"first"}""");
        HttpResponseMessage again = await Post(Client(_a), key, """{"note":"first"}""");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode); // BR-031: same status
        Assert.Equal(await first.Content.ReadAsStringAsync(), await again.Content.ReadAsStringAsync()); // includes a fresh Guid
        Assert.False(first.Headers.Contains(Idempotency.ReplayedHeader));
        Assert.Equal("true", again.Headers.GetValues(Idempotency.ReplayedHeader).Single());
        Assert.Equal(before + 1, await Version(_a));
    }

    [DbFact]
    public async Task Same_key_with_another_body_or_path_is_409_reused()
    {
        string key = NewKey();
        Assert.Equal(HttpStatusCode.Created, (await Post(Client(_a), key, """{"note":"a"}""")).StatusCode);
        int after = await Version(_a);

        HttpResponseMessage body = await Post(Client(_a), key, """{"note":"b"}""");
        HttpResponseMessage path = await Post(Client(_a), key, """{"note":"a"}""", "/t/bump/2");
        Assert.Equal(HttpStatusCode.Conflict, body.StatusCode);
        Assert.Equal("idempotency-key-reused", await Code(body));
        Assert.Equal("idempotency-key-reused", await Code(path));
        Assert.Equal(after, await Version(_a));
    }

    [DbFact]
    public async Task Business_failure_is_not_stored_and_the_same_key_can_be_retried()
    {
        int before = await Version(_a);
        string key = NewKey();
        _failNext = true;
        HttpResponseMessage failed = await Post(Client(_a), key, """{"note":"retry me"}""");
        _failNext = false;
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Equal("insufficient-stock", await Code(failed));
        Assert.Equal(before, await Version(_a)); // the handler's write was rolled back with the claim (D-34)

        HttpResponseMessage retried = await Post(Client(_a), key, """{"note":"retry me"}""");
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.Equal(before + 1, await Version(_a));
    }

    [DbFact]
    public async Task Concurrent_duplicates_execute_exactly_once_and_all_get_the_same_answer()
    {
        int before = await Version(_a);
        string key = NewKey();
        HttpResponseMessage[] all = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Post(Client(_a), key, """{"note":"race","delayMs":400}""")));
        Assert.All(all, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        string[] bodies = await Task.WhenAll(all.Select(r => r.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        Assert.Equal(11, all.Count(r => r.Headers.Contains(Idempotency.ReplayedHeader)));
        Assert.Equal(before + 1, await Version(_a));
    }

    [DbFact]
    public async Task A_duplicate_waiting_longer_than_the_bound_gets_409_in_progress()
    {
        string key = NewKey();
        Task<HttpResponseMessage> slow = Post(Client(_a), key, """{"note":"slow","delayMs":7000}""");
        await Task.Delay(800);
        HttpResponseMessage waiting = await Post(Client(_a), key, """{"note":"slow","delayMs":7000}""");
        Assert.Equal(HttpStatusCode.Conflict, waiting.StatusCode);
        Assert.Equal("in-progress", await Code(waiting));
        Assert.Equal("1", waiting.Headers.GetValues("Retry-After").Single());
        Assert.Equal(HttpStatusCode.Created, (await slow).StatusCode);
    }

    [DbFact]
    public async Task Keys_are_per_tenant_and_a_tenant_is_required()
    {
        string key = NewKey();
        int a = await Version(_a), b = await Version(_b);
        Assert.Equal(HttpStatusCode.Created, (await Post(Client(_a), key, """{"note":"x"}""")).StatusCode);
        HttpResponseMessage inB = await Post(Client(_b), key, """{"note":"x"}""");
        Assert.Equal(HttpStatusCode.Created, inB.StatusCode);
        Assert.False(inB.Headers.Contains(Idempotency.ReplayedHeader)); // BR-031: unique per (tenant, key)
        Assert.Equal((a + 1, b + 1), (await Version(_a), await Version(_b)));

        HttpResponseMessage none = await Post(Client(null), NewKey(), """{"note":"x"}""");
        Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);
        Assert.Equal("tenant-not-selected", await Code(none));
    }

    [DbFact]
    public async Task Invalid_json_is_400_validation_and_claims_nothing()
    {
        string key = NewKey();
        HttpResponseMessage bad = await Post(Client(_a), key, "{not json");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("validation", await Code(bad));
        Assert.Equal(HttpStatusCode.Created, (await Post(Client(_a), key, """{"note":"ok"}""")).StatusCode);
    }

    [DbFact]
    public async Task Unhandled_exceptions_leave_as_problem_json_without_details_even_in_development()
    {
        HttpResponseMessage r = await Client(_a).GetAsync("/t/boom");
        string text = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.Equal("internal", await Code(r));
        Assert.DoesNotContain("secret detail", text, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
    }

    [DbFact]
    public async Task Unknown_route_is_problem_json_not_found()
    {
        HttpResponseMessage r = await Client(_a).GetAsync("/t/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("not-found", await Code(r));
    }
}
