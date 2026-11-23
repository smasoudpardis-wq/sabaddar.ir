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

/// <summary>US-031 / AUD-001..003 / AT-190..193: real operations leave complete, secret-free, hash-chained records.</summary>
public sealed class AuditTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _owner = Guid.CreateVersion7();
    private readonly string _ownerSub = "aud-owner-" + Guid.NewGuid();
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public AuditTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
            Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مالک')", _owner, ApiFactory.Issuer, _ownerSub));
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'ممیزی', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک')", _tenant, _b1);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _tenant, _owner);
            return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, $2, $3, 'TenantOwner', 'Tenant')",
                _tenant, Guid.CreateVersion7(), _owner);
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

    private HttpClient As(string sub, Guid? tenant, string? device = null)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(sub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");
        if (tenant is not null)
        {
            c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, tenant.Value.ToString());
        }

        if (device is not null)
        {
            c.DefaultRequestHeaders.Add(RequestContextMiddleware.DeviceHeader, device);
        }

        return c;
    }

    private sealed record Row(string Action, Guid? Actor, string[] Roles, Guid? Terminal, string? Ip, string Details, long Seq);

    private async Task<List<Row>> Records()
    {
        await using var ds = NpgsqlDataSource.Create(Cs);
        return await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)s).Command(
                "SELECT action, actor_id, actor_roles, terminal_id, host(client_ip), details::text, seq FROM audit.audit_record ORDER BY seq");
            await using NpgsqlDataReader r = await c.ExecuteReaderAsync(ct);
            var rows = new List<Row>();
            while (await r.ReadAsync(ct))
            {
                rows.Add(new Row(r.GetString(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetFieldValue<string[]>(2), r.IsDBNull(3) ? null : r.GetGuid(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetInt64(6)));
            }

            return rows;
        });
    }

    [DbFact]
    public async Task Operations_leave_complete_secret_free_records_in_an_intact_chain()
    {
        HttpClient owner = As(_ownerSub, _tenant);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/v1/branches", new { code = "B2", name = "دو" })).StatusCode);
        using JsonDocument code = JsonDocument.Parse(await (await owner.PostAsync($"/api/v1/branches/{_b1}/pairing-codes", null)).Content.ReadAsStringAsync());
        string pairingCode = code.RootElement.GetProperty("code").GetString()!;
        using JsonDocument paired = JsonDocument.Parse(await (await owner.PostAsJsonAsync("/api/v1/terminals/pair", new { pairingCode, deviceName = "صندوق" })).Content.ReadAsStringAsync());
        string secret = paired.RootElement.GetProperty("deviceSecret").GetString()!;
        Guid terminal = paired.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/api/v1/me/pin", new { pin = "4821" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(_ownerSub, _tenant, secret).PostAsJsonAsync("/api/v1/me/pin/verify", new { pin = "9999" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await As(_ownerSub, _tenant, secret).PostAsJsonAsync("/api/v1/me/pin/verify", new { pin = "4821" })).StatusCode);
        using JsonDocument inv = JsonDocument.Parse(await (await owner.PostAsJsonAsync("/api/v1/users/invitations", new
        {
            displayName = "همکار", mobile = "09121234567",
            roleAssignments = new[] { new { roleId = RoleCatalog.IdOf("Cashier"), scopeType = "Branch", scopeId = (Guid?)_b1 } },
        })).Content.ReadAsStringAsync());
        string inviteToken = inv.RootElement.GetProperty("inviteToken").GetString()!;
        string newcomer = "aud-new-" + Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await As(newcomer, null).PostAsync($"/api/v1/invitations/{inviteToken}/accept", null)).StatusCode);

        List<Row> rows = await Records();
        string[] actions = [.. rows.Select(r => r.Action)];
        Assert.Equal(["platform.branch.created", "platform.pairing-code.issued", "platform.terminal.paired", "iam.pin.set", "iam.pin.failed",
            "iam.register.unlocked", "iam.invitation.created", "iam.invitation.accepted"], actions);   // AT-190

        // AT-191: who (user + roles), where (ip; terminal for requests from the paired device)
        Assert.All(rows, r => Assert.NotNull(r.Actor));
        Assert.All(rows, r => Assert.NotEmpty(r.Roles));
        Assert.All(rows, r => Assert.Equal("203.0.113.7", r.Ip));
        Assert.Equal(terminal, rows.Single(r => r.Action == "iam.pin.failed").Terminal);
        Assert.Equal(["Cashier"], rows.Single(r => r.Action == "iam.invitation.accepted").Roles);

        // AT-193: no credentials or contact PII anywhere in the trail
        string all = string.Join("\n", rows.Select(r => r.Details));
        foreach (string leak in new[] { pairingCode, pairingCode.Replace("-", "", StringComparison.Ordinal), secret, inviteToken, "4821", "9999", "09121234567", "همکار" })
        {
            Assert.DoesNotContain(leak, all, StringComparison.Ordinal);
        }

        // AT-192: the chain verifies and is anchored by the daily job
        var workerCs = new NpgsqlConnectionStringBuilder(Cs) { Username = "rc_worker_test", Password = "rc_worker_test" }.ToString();
        await using var wds = NpgsqlDataSource.Create(workerCs);
        ChainCheck check = Assert.Single(await new AuditChainVerifier(new PostgresUnitOfWork(wds)).VerifyAllAsync(CancellationToken.None, new HashSet<Guid> { _tenant }));
        Assert.True(check.Intact);
        Assert.Equal(rows.Count, check.LastSeq);
    }

    [DbFact]
    public async Task Concurrent_audited_operations_keep_one_gapless_chain()
    {
        HttpResponseMessage[] created = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            As(_ownerSub, _tenant).PostAsJsonAsync("/api/v1/branches", new { code = $"C{i}", name = $"شعبه {i}" })));
        Assert.All(created, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        List<Row> rows = await Records();
        Assert.Equal(Enumerable.Range(1, rows.Count).Select(i => (long)i), rows.Select(r => r.Seq));
        var workerCs = new NpgsqlConnectionStringBuilder(Cs) { Username = "rc_worker_test", Password = "rc_worker_test" }.ToString();
        await using var wds = NpgsqlDataSource.Create(workerCs);
        Assert.True(Assert.Single(await new AuditChainVerifier(new PostgresUnitOfWork(wds)).VerifyAllAsync(CancellationToken.None, new HashSet<Guid> { _tenant })).Intact);
    }
}
