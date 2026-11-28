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

/// <summary>US-013 / TEN-002.1 / AT-003 / ABAC-04: pairing codes, device recognition, revocation, branch scope, rate limit.</summary>
public sealed class TerminalTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _other = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _b2 = Guid.CreateVersion7();
    private readonly string _ownerSub = "tm-owner-" + Guid.NewGuid(), _managerSub = "tm-manager-" + Guid.NewGuid();

    public TerminalTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        string? cs = Environment.GetEnvironmentVariable("RC_TEST_PG");
        if (cs is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(cs);
        var uow = new PostgresUnitOfWork(ds);
        Guid owner = Guid.CreateVersion7(), manager = Guid.CreateVersion7();
        await uow.ExecuteWithoutTenantAsync(null, async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مالک')", owner, ApiFactory.Issuer, _ownerSub);
            return await Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مدیر')", manager, ApiFactory.Issuer, _managerSub);
        });
        foreach (Guid t in new[] { _tenant, _other })
        {
            await uow.ExecuteAsync(new FixedTenantContext(new TenantId(t)), async (s, ct) =>
            {
                await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'دستگاه‌ها', 'supermarket')", t);
                await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", t, owner);
                return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, $2, $3, 'TenantOwner', 'Tenant')", t, Guid.CreateVersion7(), owner);
            });
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک'), ($1, $3, 'B2', 'دو')", _tenant, _b1, _b2);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _tenant, manager);
            return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES ($1, $2, $3, 'BranchManager', 'Branch', $4)",
                _tenant, Guid.CreateVersion7(), manager, _b1);
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

    private HttpClient As(string sub, Guid? tenant = null, string? device = null, string acr = "mfa")
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(sub, acr: acr, authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, (tenant ?? _tenant).ToString());
        if (device is not null)
        {
            c.DefaultRequestHeaders.Add(RequestContextMiddleware.DeviceHeader, device);
        }

        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return d.RootElement.Clone();
    }

    private async Task<string> IssueCode(string sub, Guid branch)
    {
        HttpResponseMessage r = await As(sub, acr: "mfa").PostAsync($"/api/v1/branches/{branch}/pairing-codes", null);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("code").GetString()!;
    }

    private async Task<JsonElement> Pair(string sub, string code, string name = "صندوق جلو") =>
        await Json(await As(sub, acr: "mfa").PostAsJsonAsync("/api/v1/terminals/pair", new { pairingCode = code, deviceName = name }));

    private async Task<JsonElement> Me(string sub, string? device, Guid? tenant = null) => await Json(await As(sub, tenant, device, "mfa").GetAsync("/api/v1/me"));

    [DbFact]
    public async Task Branch_manager_pairs_a_device_which_the_api_then_recognises_until_revoked()
    {
        string code = await IssueCode(_managerSub, _b1);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", code);

        JsonElement terminal = await Pair(_managerSub, code.ToLowerInvariant().Replace("-", "", StringComparison.Ordinal)); // normalised input
        string secret = terminal.GetProperty("deviceSecret").GetString()!;
        Guid id = terminal.GetProperty("id").GetGuid();
        Assert.Equal(_b1, terminal.GetProperty("branchId").GetGuid());

        JsonElement me = await Me(_ownerSub, secret);
        Assert.Equal(id, me.GetProperty("terminal").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, (await Me(_ownerSub, null)).GetProperty("terminal").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await Me(_ownerSub, secret, _other)).GetProperty("terminal").ValueKind); // other tenant

        // single use
        Assert.Equal(HttpStatusCode.NotFound, (await As(_managerSub, acr: "pwd").PostAsJsonAsync("/api/v1/terminals/pair", new { pairingCode = code, deviceName = "دوم" })).StatusCode);

        // the manager sees and revokes it; afterwards the device is no longer a terminal (AT-003)
        JsonElement list = await Json(await As(_managerSub, acr: "pwd").GetAsync("/api/v1/terminals"));
        JsonElement listed = Assert.Single(list.EnumerateArray(), t => t.GetProperty("id").GetGuid() == id);
        Assert.True(!listed.TryGetProperty("deviceSecret", out JsonElement leaked) || leaked.ValueKind == JsonValueKind.Null); // never listed again
        Assert.Equal(HttpStatusCode.NoContent, (await As(_managerSub, acr: "pwd").DeleteAsync($"/api/v1/terminals/{id}")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Me(_ownerSub, secret)).GetProperty("terminal").ValueKind);
    }

    [DbFact]
    public async Task A_branch_manager_cannot_issue_for_or_revoke_in_another_branch()
    {
        HttpResponseMessage r = await As(_managerSub, acr: "pwd").PostAsync($"/api/v1/branches/{_b2}/pairing-codes", null);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

        Guid b2Terminal = (await Pair(_ownerSub, await IssueCode(_ownerSub, _b2), "شعبه دو")).GetProperty("id").GetGuid();
        Assert.DoesNotContain((await Json(await As(_managerSub, acr: "pwd").GetAsync("/api/v1/terminals"))).EnumerateArray(),
            t => t.GetProperty("id").GetGuid() == b2Terminal);
        Assert.Equal(HttpStatusCode.NotFound, (await As(_managerSub, acr: "pwd").DeleteAsync($"/api/v1/terminals/{b2Terminal}")).StatusCode);
    }

    [DbFact]
    public async Task Expired_and_malformed_codes_are_not_found()
    {
        string code = await IssueCode(_ownerSub, _b1);
        await using (var ds = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("RC_TEST_PG")!))
        {
            await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), (s, ct) =>
                Exec(s, ct, "UPDATE platform.pairing_code SET expires_at = now() - interval '1 second' WHERE used_at IS NULL"));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await As(_ownerSub).PostAsJsonAsync("/api/v1/terminals/pair", new { pairingCode = code, deviceName = "دیر" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As(_ownerSub).PostAsJsonAsync("/api/v1/terminals/pair", new { pairingCode = "UUUU-1234", deviceName = "بد" })).StatusCode);
    }

    [DbFact]
    public async Task Guessing_codes_is_rate_limited_per_identity()
    {
        string guesser = "tm-guesser-" + Guid.NewGuid();
        await using (var ds = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("RC_TEST_PG")!))
        {
            Guid u = Guid.CreateVersion7();
            var uow = new PostgresUnitOfWork(ds);
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'حدس')", u, ApiFactory.Issuer, guesser));
            await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
            {
                await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _tenant, u);
                return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, $2, $3, 'Cashier', 'Tenant')", _tenant, Guid.CreateVersion7(), u);
            });
        }

        HttpStatusCode[] codes = new HttpStatusCode[12];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (await As(guesser, acr: "pwd").PostAsJsonAsync("/api/v1/terminals/pair", new { pairingCode = $"ABCD-{i:0000}", deviceName = "x" })).StatusCode;
        }

        Assert.All(codes[..10], c => Assert.Equal(HttpStatusCode.NotFound, c));
        Assert.All(codes[10..], c => Assert.Equal(HttpStatusCode.TooManyRequests, c));
    }

    [Fact]
    public async Task Pos_operations_require_a_registered_terminal()
    {
        var ctx = new RequestContext();
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(ctx).BuildServiceProvider() };
        object? denied = await new RequireTerminalFilter().InvokeAsync(new DefaultEndpointFilterInvocationContext(http), _ => ValueTask.FromResult<object?>("sold"));
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(denied).StatusCode);

        ctx.Terminal = new TerminalInfo(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal("sold", await new RequireTerminalFilter().InvokeAsync(new DefaultEndpointFilterInvocationContext(http), _ => ValueTask.FromResult<object?>("sold")));
    }
}
