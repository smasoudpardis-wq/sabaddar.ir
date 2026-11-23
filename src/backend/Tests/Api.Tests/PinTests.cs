using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using RetailCloud.BuildingBlocks.Api;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.IdentityAccess.Infrastructure;

namespace RetailCloud.Api.Tests;

/// <summary>US-023 / IAM-004 / AUTH-08 / AT-013, and the on-device approval path of D08 F-14.</summary>
public sealed class PinTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _r1 = Guid.CreateVersion7();
    private readonly Dictionary<string, (Guid Id, string Sub)> _u = [];
    private string _device = "";
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public PinTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach (string n in new[] { "cashier", "cashier2", "manager" })
        {
            _u[n] = (Guid.CreateVersion7(), $"pin-{n}-{Guid.NewGuid()}");
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, $4)", _u[n].Id, ApiFactory.Issuer, _u[n].Sub, n));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'پین', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک')", _tenant, _b1);
            await Exec(s, ct, "INSERT INTO platform.register (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'R1')", _tenant, _r1, _b1);
            foreach ((string n, string role) in new[] { ("cashier", "Cashier"), ("cashier2", "Cashier"), ("manager", "BranchManager") })
            {
                await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _tenant, _u[n].Id);
                await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES ($1, $2, $3, $4, 'Branch', $5)",
                    _tenant, Guid.CreateVersion7(), _u[n].Id, role, _b1);
            }

            return 0;
        });

        HttpResponseMessage code = await As("manager").PostAsync($"/api/v1/branches/{_b1}/pairing-codes", null);
        using JsonDocument c = JsonDocument.Parse(await code.Content.ReadAsStringAsync());
        HttpResponseMessage paired = await As("manager").PostAsJsonAsync("/api/v1/terminals/pair", new { pairingCode = c.RootElement.GetProperty("code").GetString(), deviceName = "صندوق ۱" });
        using JsonDocument p = JsonDocument.Parse(await paired.Content.ReadAsStringAsync());
        _device = p.RootElement.GetProperty("deviceSecret").GetString()!;
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

    private HttpClient As(string user, bool onTerminal = false, DateTimeOffset? authTime = null)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(_u[user].Sub, acr: "pwd", authTime: authTime ?? DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, _tenant.ToString());
        if (onTerminal)
        {
            c.DefaultRequestHeaders.Add(RequestContextMiddleware.DeviceHeader, _device);
        }

        return c;
    }

    private static async Task<(HttpStatusCode Status, string? Code, int? Left)> Read(HttpResponseMessage r)
    {
        if (r.StatusCode == HttpStatusCode.NoContent)
        {
            return (r.StatusCode, null, null);
        }

        using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return (r.StatusCode, d.RootElement.TryGetProperty("code", out JsonElement c) ? c.GetString() : null,
            d.RootElement.TryGetProperty("attemptsLeft", out JsonElement l) ? l.GetInt32() : null);
    }

    private Task<HttpResponseMessage> SetPin(string user, string pin, DateTimeOffset? authTime = null) =>
        As(user, authTime: authTime).PutAsJsonAsync("/api/v1/me/pin", new { pin });

    private Task<HttpResponseMessage> Verify(string user, string pin, bool onTerminal = true) =>
        As(user, onTerminal).PostAsJsonAsync("/api/v1/me/pin/verify", new { pin });

    [DbFact]
    public async Task A_pin_is_set_after_a_recent_sign_in_and_unlocks_only_on_a_registered_terminal()
    {
        Assert.Equal("reauth-required", (await Read(await SetPin("cashier", "4821", DateTimeOffset.UtcNow.AddMinutes(-20)))).Code);
        Assert.Equal("validation", (await Read(await SetPin("cashier", "12a4"))).Code);
        Assert.Equal("validation", (await Read(await SetPin("cashier", "123"))).Code);
        Assert.Equal(HttpStatusCode.NoContent, (await SetPin("cashier", "4821")).StatusCode);

        Assert.Equal("terminal-not-registered", (await Read(await Verify("cashier", "4821", onTerminal: false))).Code);
        Assert.Equal(HttpStatusCode.NoContent, (await Verify("cashier", "4821")).StatusCode);
        (HttpStatusCode _, string? code, int? left) = await Read(await Verify("cashier", "0000"));
        Assert.Equal(("pin-invalid", 4), (code, left));
        Assert.Equal(HttpStatusCode.NoContent, (await Verify("cashier", "4821")).StatusCode); // success resets the counter
    }

    [DbFact]
    public async Task Five_failures_lock_the_pin_until_a_new_one_is_set_after_a_full_sign_in()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await SetPin("cashier2", "7391")).StatusCode);
        int?[] left = new int?[4];
        for (int i = 0; i < 4; i++)
        {
            left[i] = (await Read(await Verify("cashier2", "0000"))).Left;
        }

        Assert.Equal([4, 3, 2, 1], left);
        Assert.Equal("pin-locked", (await Read(await Verify("cashier2", "0000"))).Code);   // 5th failure (AT-013)
        Assert.Equal("pin-locked", (await Read(await Verify("cashier2", "7391"))).Code);   // even the right PIN
        Assert.Equal(HttpStatusCode.NoContent, (await SetPin("cashier2", "5502")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Verify("cashier2", "5502")).StatusCode);
    }

    [DbFact]
    public async Task Concurrent_guesses_cannot_get_more_than_five_tries()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await SetPin("manager", "2468")).StatusCode);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i => await Read(await Verify("manager", $"{1000 + i}"))));
        Assert.True(results.Count(r => r.Code == "pin-invalid") <= 4, string.Join(",", results.Select(r => r.Code)));
        Assert.Equal("pin-locked", (await Read(await Verify("manager", "2468"))).Code);
        Assert.Equal(HttpStatusCode.NoContent, (await SetPin("manager", "2468")).StatusCode); // reset for other tests
    }

    [DbFact]
    public async Task A_manager_approves_on_the_cashiers_terminal_with_their_own_pin()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await SetPin("manager", "2468")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SetPin("cashier", "4821")).StatusCode);
        var ask = new ApprovalAsk("pos.discount.apply", "SaleLine", "s/1", new ScopeRef(ScopeType.Register, _r1, _b1), "amount-limit", "15", "0", null);
        await using var ds = NpgsqlDataSource.Create(Cs);
        Guid id = await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant), new UserId(_u["cashier"].Id)),
            (s, ct) => new PostgresApprovals(TimeProvider.System).RequestAsync(s, ask, new UserId(_u["cashier"].Id), ct));

        Task<HttpResponseMessage> Decide(Guid approverId, string pin, bool onTerminal = true) =>
            As("cashier", onTerminal).PostAsJsonAsync($"/api/v1/approvals/{id}/decision", new { decision = "Approve", pin, approverId });

        Assert.Equal("terminal-not-registered", (await Read(await Decide(_u["manager"].Id, "2468", onTerminal: false))).Code);
        Assert.Equal("self-approval", (await Read(await Decide(_u["cashier"].Id, "4821"))).Code);
        Assert.Equal("forbidden", (await Read(await Decide(_u["cashier2"].Id, "5502"))).Code);          // capped role
        Assert.Equal("pin-invalid", (await Read(await Decide(_u["manager"].Id, "9999"))).Code);
        HttpResponseMessage ok = await Decide(_u["manager"].Id, "2468");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using JsonDocument a = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        Assert.Equal(_u["manager"].Id, a.RootElement.GetProperty("decidedBy").GetGuid());
    }

    [Fact]
    public void Pin_hash_is_argon2id_with_the_AUTH_08_parameters_and_salted()
    {
        string a = PinHasher.Hash("4821"), b = PinHasher.Hash("4821");
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", a, StringComparison.Ordinal);
        Assert.NotEqual(a, b);
        Assert.True(PinHasher.Verify("4821", a));
        Assert.False(PinHasher.Verify("4822", a));
        Assert.False(PinHasher.Verify("4821", a.Replace("t=3", "t=1", StringComparison.Ordinal)));
    }
}
