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
/// US-025 / IAM-001 / IAM-006 / AT-015 / SEC-T-012: invitations with role and scope, acceptance by a new identity,
/// anti-escalation, branch-scoped administration, BFLA, last-owner rule.
/// </summary>
public sealed class UserManagementTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _b2 = Guid.CreateVersion7(), _r1 = Guid.CreateVersion7();
    private readonly string _ownerSub = "um-owner-" + Guid.NewGuid(), _managerSub = "um-manager-" + Guid.NewGuid();
    private Guid _ownerId, _ownerAssignment;

    public UserManagementTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        string? cs = Environment.GetEnvironmentVariable("RC_TEST_PG");
        if (string.IsNullOrWhiteSpace(cs))
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(cs);
        var uow = new PostgresUnitOfWork(ds);
        _ownerId = Guid.CreateVersion7();
        Guid manager = Guid.CreateVersion7();
        _ownerAssignment = Guid.CreateVersion7();
        await uow.ExecuteWithoutTenantAsync(null, async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مالک')", _ownerId, ApiFactory.Issuer, _ownerSub);
            return await Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مدیر شعبه')", manager, ApiFactory.Issuer, _managerSub);
        });
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'فروشگاه کاربران', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک'), ($1, $3, 'B2', 'دو')", _tenant, _b1, _b2);
            await Exec(s, ct, "INSERT INTO platform.register (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'R1')", _tenant, _r1, _b1);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2), ($1, $3)", _tenant, _ownerId, manager);
            await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, $2, $3, 'TenantOwner', 'Tenant')", _tenant, _ownerAssignment, _ownerId);
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

    private HttpClient As(string sub, bool tenant = true, string acr = "mfa")
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(sub, acr: acr, authTime: DateTimeOffset.UtcNow,
            extraClaims: new Dictionary<string, object> { ["name"] = "کاربر " + sub[..8] }));
        if (tenant)
        {
            c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, _tenant.ToString());
        }

        return c;
    }

    private static object Assign(string role, string scopeType, Guid? scopeId) => new { roleId = RoleCatalog.IdOf(role), scopeType, scopeId };

    private static Task<HttpResponseMessage> Invite(HttpClient c, params object[] assignments) =>
        c.PostAsJsonAsync("/api/v1/users/invitations", new { displayName = "همکار تازه", roleAssignments = assignments });

    private static async Task<string?> Code(HttpResponseMessage r)
    {
        using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return d.RootElement.GetProperty("code").GetString();
    }

    private static async Task<string> Token(HttpResponseMessage created)
    {
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument d = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return d.RootElement.GetProperty("inviteToken").GetString()!;
    }

    [DbFact]
    public async Task Owner_invites_a_cashier_and_a_new_identity_accepts_once()
    {
        string token = await Token(await Invite(As(_ownerSub), Assign("Cashier", "Branch", _b1)));
        string newbie = "um-new-" + Guid.NewGuid();

        HttpResponseMessage accepted = await As(newbie, tenant: false, acr: "pwd").PostAsync($"/api/v1/invitations/{token}/accept", null);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using (JsonDocument a = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()))
        {
            Assert.Equal(_tenant, a.RootElement.GetProperty("tenantId").GetGuid());
            Assert.Equal("Cashier", a.RootElement.GetProperty("roles")[0].GetString());
        }

        // single use, for anyone (including the same person)
        Assert.Equal(HttpStatusCode.NotFound, (await As(newbie, tenant: false).PostAsync($"/api/v1/invitations/{token}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As("um-other-" + Guid.NewGuid(), tenant: false).PostAsync($"/api/v1/invitations/{token}/accept", null)).StatusCode);

        // the cashier works in the store with password only (no MFA for Cashier) and sees branch-scoped permissions
        HttpResponseMessage me = await As(newbie, acr: "pwd").GetAsync("/api/v1/me");
        using (JsonDocument m = JsonDocument.Parse(await me.Content.ReadAsStringAsync()))
        {
            var perms = m.RootElement.GetProperty("permissions").EnumerateArray().Select(p => (p.GetProperty("permission").GetString(), p.GetProperty("scopeType").GetString())).ToList();
            Assert.Contains(("pos.sell", "Branch"), perms);
            Assert.DoesNotContain(perms, p => p.Item1 == "iam.users.manage");
        }

        // SEC-T-012 (BFLA): the cashier cannot reach administration
        Assert.Equal("forbidden", await Code(await As(newbie, acr: "pwd").GetAsync("/api/v1/users")));
        Assert.Equal("forbidden", await Code(await Invite(As(newbie, acr: "pwd"), Assign("Cashier", "Branch", _b1))));
        Assert.Equal("forbidden", await Code(await As(newbie, acr: "pwd").GetAsync("/api/v1/roles")));
    }

    [DbFact]
    public async Task Unknown_or_expired_invitations_all_look_the_same()
    {
        string token = await Token(await Invite(As(_ownerSub), Assign("Cashier", "Register", _r1)));
        await using (var ds = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("RC_TEST_PG")!))
        {
            await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), (s, ct) =>
                Exec(s, ct, "UPDATE iam.invitation SET expires_at = now() - interval '1 second' WHERE display_name = 'همکار تازه' AND assignments->0->>'scopeType' = 'Register'"));
        }

        HttpResponseMessage expired = await As("um-late-" + Guid.NewGuid(), tenant: false).PostAsync($"/api/v1/invitations/{token}/accept", null);
        HttpResponseMessage unknown = await As("um-late-" + Guid.NewGuid(), tenant: false).PostAsync($"/api/v1/invitations/{new string('x', 43)}/accept", null);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await Code(expired), await Code(unknown));
    }

    private async Task<(Guid Id, string Sub)> MemberAsync(string role, Guid branch)
    {
        Guid id = Guid.CreateVersion7();
        string sub = "um-" + role.ToLowerInvariant() + "-" + Guid.NewGuid();
        await using var ds = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("RC_TEST_PG")!);
        var uow = new PostgresUnitOfWork(ds);
        await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
            Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'همکار')", id, ApiFactory.Issuer, sub));
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _tenant, id);
            return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES ($1, $2, $3, $4, 'Branch', $5)",
                _tenant, Guid.CreateVersion7(), id, role, branch);
        });
        return (id, sub);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return d.RootElement.Clone();
    }

    [DbFact]
    public async Task AT018_disabling_a_member_cuts_their_access_to_the_store_at_once_and_is_reversible()
    {
        var (cashier, cashierSub) = await MemberAsync("Cashier", _b1);
        var (other, _) = await MemberAsync("Cashier", _b2);
        HttpClient manager = As(_managerSub, acr: "pwd"), owner = As(_ownerSub), victim = As(cashierSub, acr: "pwd");
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync("/api/v1/products")).StatusCode);

        HttpResponseMessage disabled = await manager.PatchAsJsonAsync($"/api/v1/users/{cashier}", new { expectedVersion = 1, status = "Disabled" });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        JsonElement view = await Json(disabled);
        Assert.Equal(("Disabled", 2), (view.GetProperty("status").GetString(), view.GetProperty("version").GetInt32()));
        Assert.Equal(HttpStatusCode.NotFound, (await victim.GetAsync("/api/v1/products")).StatusCode);   // IAM-009: the very next request
        Assert.Equal(0, (await Json(await As(cashierSub, tenant: false, acr: "pwd").GetAsync("/api/v1/me"))).GetProperty("memberships").GetArrayLength());

        Assert.Equal("version-conflict", await Code(await manager.PatchAsJsonAsync($"/api/v1/users/{cashier}", new { expectedVersion = 1, status = "Active" })));
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PatchAsJsonAsync($"/api/v1/users/{other}", new { expectedVersion = 1, status = "Disabled" })).StatusCode);   // other branch
        Assert.Equal(HttpStatusCode.NotFound, (await manager.PatchAsJsonAsync($"/api/v1/users/{_ownerId}", new { expectedVersion = 1, status = "Disabled" })).StatusCode);
        Assert.Equal("cannot-disable-self", await Code(await owner.PatchAsJsonAsync($"/api/v1/users/{_ownerId}", new { expectedVersion = 1, status = "Disabled" })));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync($"/api/v1/users/{cashier}", new { expectedVersion = 2, status = "Active", displayName = "x" })).StatusCode);

        JsonElement listed = (await Json(await owner.GetAsync("/api/v1/users"))).EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == cashier);
        Assert.Equal("Disabled", listed.GetProperty("status").GetString());   // D07 status, not the database's Suspended

        HttpResponseMessage enabled = await manager.PatchAsJsonAsync($"/api/v1/users/{cashier}", new { expectedVersion = 2, status = "Active" });
        Assert.Equal(("Active", 3), ((await Json(enabled)).GetProperty("status").GetString(), (await Json(enabled)).GetProperty("version").GetInt32()));
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync("/api/v1/products")).StatusCode);   // the roles were kept
    }

    [DbFact]
    public async Task A_branch_manager_cannot_disable_a_user_holding_a_role_beyond_their_power()
    {
        var (accountantLike, _) = await MemberAsync("Cashier", _b1);
        await using var ds = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("RC_TEST_PG")!);
        await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), (s, ct) =>
            Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, $2, $3, 'Auditor', 'Tenant')",
                _tenant, Guid.CreateVersion7(), accountantLike));
        // visible through the branch role, but the tenant-wide Auditor role is outside the manager's power
        Assert.Equal("forbidden", await Code(await As(_managerSub, acr: "pwd").PatchAsJsonAsync($"/api/v1/users/{accountantLike}",
            new { expectedVersion = 1, status = "Disabled" })));
    }

    [DbFact]
    public async Task A_branch_manager_administers_only_their_branch_and_cannot_escalate()
    {
        HttpClient manager = As(_managerSub, acr: "pwd");
        Assert.Equal(HttpStatusCode.Created, (await Invite(manager, Assign("Cashier", "Branch", _b1))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Invite(manager, Assign("Cashier", "Register", _r1))).StatusCode);
        Assert.Equal("forbidden", await Code(await Invite(manager, Assign("Cashier", "Branch", _b2))));          // other branch
        Assert.Equal("forbidden", await Code(await Invite(manager, Assign("Cashier", "Tenant", null))));          // wider than own scope
        Assert.Equal("forbidden", await Code(await Invite(manager, Assign("TenantAdmin", "Tenant", null))));      // escalation
        Assert.Equal("forbidden", await Code(await Invite(manager, Assign("Cashier", "Branch", _b1), Assign("TenantOwner", "Tenant", null))));

        // the owner (tenant scope) is invisible to a branch-scoped manager; the manager sees themself
        using JsonDocument users = JsonDocument.Parse(await (await manager.GetAsync("/api/v1/users")).Content.ReadAsStringAsync());
        Guid[] ids = [.. users.RootElement.EnumerateArray().Select(u => u.GetProperty("id").GetGuid())];
        Assert.DoesNotContain(_ownerId, ids);
        Assert.NotEmpty(ids);

        // and cannot take the owner's role away
        Assert.Equal("forbidden", await Code(await manager.DeleteAsync($"/api/v1/users/{_ownerId}/role-assignments/{_ownerAssignment}")));
    }

    [DbFact]
    public async Task Bad_roles_and_scopes_are_validation_errors()
    {
        HttpClient owner = As(_ownerSub);
        Assert.Equal("validation", await Code(await Invite(owner, new { roleId = Guid.NewGuid(), scopeType = "Tenant", scopeId = (Guid?)null })));
        Assert.Equal("validation", await Code(await Invite(owner, Assign("Cashier", "Branch", Guid.NewGuid())))); // branch of no tenant of ours
        Assert.Equal("forbidden", await Code(await Invite(owner, Assign("Accountant", "Branch", _b1))));         // Accountant is tenant-wide only
        Assert.Equal("validation", await Code(await owner.PostAsJsonAsync("/api/v1/users/invitations", new { displayName = "", roleAssignments = Array.Empty<object>() })));
    }

    [DbFact]
    public async Task The_last_owner_cannot_be_removed_and_duplicates_are_refused()
    {
        HttpClient owner = As(_ownerSub);
        HttpResponseMessage last = await owner.DeleteAsync($"/api/v1/users/{_ownerId}/role-assignments/{_ownerAssignment}");
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
        Assert.Equal("last-owner", await Code(last));

        HttpResponseMessage dup = await owner.PostAsJsonAsync($"/api/v1/users/{_ownerId}/role-assignments", Assign("TenantOwner", "Tenant", null));
        Assert.Equal("duplicate-assignment", await Code(dup));

        HttpResponseMessage added = await owner.PostAsJsonAsync($"/api/v1/users/{_ownerId}/role-assignments", Assign("Cashier", "Register", _r1));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        using JsonDocument a = JsonDocument.Parse(await added.Content.ReadAsStringAsync());
        Guid id = a.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/users/{_ownerId}/role-assignments/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/v1/users/{_ownerId}/role-assignments/{id}")).StatusCode);
    }

    [DbFact]
    public async Task Roles_endpoint_lists_the_nine_system_roles_with_stable_ids()
    {
        using JsonDocument roles = JsonDocument.Parse(await (await As(_ownerSub).GetAsync("/api/v1/roles")).Content.ReadAsStringAsync());
        Assert.Equal(9, roles.RootElement.GetArrayLength());
        Assert.All(roles.RootElement.EnumerateArray(), r =>
            Assert.Equal(RoleCatalog.IdOf(r.GetProperty("name").GetString()!), r.GetProperty("id").GetGuid()));
    }
}
