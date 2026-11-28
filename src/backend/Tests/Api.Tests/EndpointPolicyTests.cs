using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RetailCloud.BuildingBlocks.Api;

namespace RetailCloud.Api.Tests;

/// <summary>
/// AT-015 / IAM-006 / SEC-T-010..012: every endpoint of the real API is documented in D07 and declares exactly the
/// policy D07 states (x-permission); an endpoint without a policy is refused at runtime (Default Deny).
/// </summary>
public sealed class EndpointPolicyTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly ApiFactory _factory;

    public EndpointPolicyTests(ApiFactory factory) => _factory = factory;

    private sealed record Operation(string OperationId, string Method, string Path, string? Permission, bool AllowedWhenReadOnly);

    private static List<Operation> D07()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "docs", "04-api", "D07-operations.json")))
        {
            dir = dir.Parent;
        }

        return JsonSerializer.Deserialize<List<Operation>>(File.ReadAllText(Path.Combine(dir.FullName, "docs", "04-api", "D07-operations.json")),
            Web)!;
    }

    private static string Normalize(string pattern) =>
        System.Text.RegularExpressions.Regex.Replace(pattern, @"\{(\w+):[^}]+\}", "{$1}");

    [Fact]
    public void Every_api_endpoint_is_in_D07_and_declares_the_D07_permission()
    {
        List<Operation> d07 = D07();
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/v1/", StringComparison.Ordinal))
            .ToList();
        Assert.True(endpoints.Count >= 11, $"only {endpoints.Count} endpoints found");

        var problems = new List<string>();
        foreach (RouteEndpoint e in endpoints)
        {
            string path = Normalize(e.RoutePattern.RawText!["/api/v1".Length..]);
            foreach (string method in e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
            {
                Operation? op = d07.SingleOrDefault(o => o.Method == method && o.Path == path);
                string? declared = e.Metadata.GetMetadata<EndpointPolicyAttribute>()?.Permission;
                if (op is not null && path.StartsWith("/platform/", StringComparison.Ordinal))
                {
                    // the platform audience: no tenant permission, the platform realm's admin policy instead (D06 §6.2, TM-071)
                    if (!e.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
                            .Any(a => a.Policy is RetailCloud.BuildingBlocks.Api.ServiceCollectionExtensions.PlatformAdminPolicy
                                     or RetailCloud.BuildingBlocks.Api.ServiceCollectionExtensions.PlatformSupportPolicy
                                     or RetailCloud.BuildingBlocks.Api.ServiceCollectionExtensions.PlatformCatalogPolicy))
                    {
                        problems.Add($"{method} {path} ({op.OperationId}): a platform operation without a platform-realm policy");
                    }

                    continue;
                }

                if (op is not null && path.StartsWith("/c/", StringComparison.Ordinal))
                {
                    // the customer portal (US-141): anonymous for the phone sign-in, the customer policy for everything after it
                    bool anonymous = e.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null;
                    bool customer = e.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
                        .Any(a => a.Policy == RetailCloud.BuildingBlocks.Api.ServiceCollectionExtensions.CustomerPolicy);
                    if (!anonymous && !customer)
                    {
                        problems.Add($"{method} {path} ({op.OperationId}): a portal operation that is neither anonymous nor under the customer policy");
                    }

                    continue;
                }

                if (op is { Permission: null } && (path.StartsWith("/r/", StringComparison.Ordinal) || path.StartsWith("/partner/", StringComparison.Ordinal)))
                {
                    // the public audience (CUS-009): explicitly anonymous and rate-limited, nothing else
                    if (e.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is null
                        || (path.StartsWith("/partner/", StringComparison.Ordinal) && !e.Metadata.Any(m => m.GetType().Name == "PartnerScope")))
                    {
                        problems.Add($"{method} {path} ({op.OperationId}): a public operation that is not explicitly anonymous");
                    }

                    continue;
                }

                if (op is null)
                {
                    problems.Add($"{method} {path}: not in D07 (undocumented endpoint)");
                }
                else if (declared is null)
                {
                    problems.Add($"{method} {path} ({op.OperationId}): no policy (Default Deny would refuse it)");
                }
                else if (declared != op.Permission)
                {
                    problems.Add($"{method} {path} ({op.OperationId}): policy '{declared}' but D07 x-permission '{op.Permission}'");
                }

                bool readOnlyOk = e.Metadata.GetMetadata<AllowedWhenReadOnlyAttribute>() is not null;
                if (op is not null && method != "GET" && readOnlyOk != op.AllowedWhenReadOnly)
                {
                    problems.Add($"{method} {path} ({op.OperationId}): AllowWhenReadOnly={readOnlyOk} but D07 x-allowed-when-read-only={op.AllowedWhenReadOnly} (TEN-004)");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task An_endpoint_that_forgot_its_policy_is_refused()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddProblemDetails();
        WebApplication app = builder.Build();
        RouteGroupBuilder g = app.MapGroup("/api/v1").AddEndpointFilter<DefaultDenyFilter>();
        g.MapGet("/forgotten", () => "secret");
        g.MapGet("/declared", () => "ok").AllowAnyAuthenticated();
        await app.StartAsync();
        try
        {
            HttpClient c = app.GetTestClient();
            HttpResponseMessage forgotten = await c.GetAsync("/api/v1/forgotten");
            Assert.Equal(HttpStatusCode.Forbidden, forgotten.StatusCode);
            Assert.DoesNotContain("secret", await forgotten.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/declared")).StatusCode);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}
