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

/// <summary>US-142 / CUS-003 / CUS-005 / CUS-007 / AT-123 / AT-125 / AT-127 / AT-222: a store's counter QR, joining it with separate consents, and taking them back.</summary>
public sealed class StoreInviteTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";
    private readonly Guid _tenantA = Guid.CreateVersion7(), _tenantB = Guid.CreateVersion7(), _branchA = Guid.CreateVersion7(), _branchB = Guid.CreateVersion7();
    private readonly Guid _ownerA = Guid.CreateVersion7(), _ownerB = Guid.CreateVersion7(), _cashierA = Guid.CreateVersion7();
    private readonly string _ownerSubA = "inv-ownerA-" + Guid.NewGuid(), _ownerSubB = "inv-ownerB-" + Guid.NewGuid(), _cashierSubA = "inv-cashierA-" + Guid.NewGuid();

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null) return;
        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach ((Guid id, string sub) in new[] { (_ownerA, _ownerSubA), (_ownerB, _ownerSubB), (_cashierA, _cashierSubA) })
        {
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) => Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'کاربر')", id, ApiFactory.Issuer, sub));
        }

        foreach ((Guid tenant, Guid branch, Guid owner, string name) in new[] { (_tenantA, _branchA, _ownerA, "نان‌آور"), (_tenantB, _branchB, _ownerB, "دیگری") })
        {
            await uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
            {
                await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, $2, 'supermarket')", tenant, name);
                await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'شعبه مرکز')", tenant, branch);
                await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", tenant, owner);
                return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, gen_random_uuid(), $2, 'TenantOwner', 'Tenant')", tenant, owner);
            });
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenantA)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _tenantA, _cashierA);
            return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES ($1, gen_random_uuid(), $2, 'Cashier', 'Branch', $3)", _tenantA, _cashierA, _branchA);
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<int> Exec(IUnitOfWorkScope s, CancellationToken ct, string sql, params object[] args)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(sql);
        foreach (object a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    private HttpClient Store(string sub, Guid tenant)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(sub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, tenant.ToString());
        return c;
    }

    private async Task<HttpClient> CustomerAsync()
    {
        HttpClient anon = factory.CreateClient();
        anon.DefaultRequestHeaders.Add("X-Forwarded-For", $"10.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        string phone = "09" + Random.Shared.Next(100_000_000, 999_999_999);
        JsonElement start = await Body(await anon.PostAsJsonAsync("/api/v1/c/auth/otp", new { phone }));
        JsonElement session = await Body(await anon.PostAsJsonAsync("/api/v1/c/auth/otp/verify",
            new { challengeId = start.GetProperty("challengeId").GetGuid(), code = factory.Otps.Latest["+98" + phone[1..]] }));
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        return c;
    }

    private static async Task<(Guid Id, string Token)> InviteAsync(HttpClient owner, Guid branch)
    {
        HttpResponseMessage r = await owner.PostAsJsonAsync("/api/v1/store-invites", new { branchId = branch });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        JsonElement b = await Body(r);
        string url = b.GetProperty("url").GetString()!;
        Assert.Contains("/join/v1.", url, StringComparison.Ordinal);
        return (b.GetProperty("id").GetGuid(), url[(url.LastIndexOf("/join/", StringComparison.Ordinal) + 6)..]);
    }

    [DbFact]
    public async Task AT125_the_counter_QR_shows_only_the_store_and_branch_name_and_a_forged_or_revoked_one_is_refused_the_same_way()
    {
        HttpClient owner = Store(_ownerSubA, _tenantA);
        (Guid id, string token) = await InviteAsync(owner, _branchA);
        HttpClient anon = factory.CreateClient();
        JsonElement shown = await Body(await anon.GetAsync($"/api/v1/c/join/{token}"));
        Assert.Equal(("نان‌آور", "شعبه مرکز"), (shown.GetProperty("storeName").GetString(), shown.GetProperty("branchName").GetString()));
        Assert.Equal(["branchName", "expiresAt", "storeName"], shown.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));   // nothing else leaks

        // AT-222: the store id swapped for store B's: the signature fails, 400
        string[] parts = token.Split('.');
        string payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/') + new string('=', (4 - parts[1].Length % 4) % 4))).Replace(_tenantA.ToString("N"), _tenantB.ToString("N"));
        string forged = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        HttpResponseMessage bad = await anon.GetAsync($"/api/v1/c/join/v1.{forged}.{parts[2]}");
        Assert.Equal((HttpStatusCode.BadRequest, "invalid-invite"), (bad.StatusCode, (await Body(bad)).GetProperty("code").GetString()));

        // another store's owner cannot revoke it (404); its own owner can, and the QR stops working at once
        Assert.Equal(HttpStatusCode.NotFound, (await Store(_ownerSubB, _tenantB).DeleteAsync($"/api/v1/store-invites/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/store-invites/{id}")).StatusCode);
        HttpResponseMessage revoked = await anon.GetAsync($"/api/v1/c/join/{token}");
        Assert.Equal((HttpStatusCode.BadRequest, "invalid-invite"), (revoked.StatusCode, (await Body(revoked)).GetProperty("code").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.GetAsync("/api/v1/c/join/not-a-token")).StatusCode);

        // who may issue one: a branch of the caller's own store, by someone who manages settings there
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync("/api/v1/store-invites", new { branchId = _branchB })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Store(_cashierSubA, _tenantA).PostAsJsonAsync("/api/v1/store-invites", new { branchId = _branchA })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/store-invites", new { branchId = _branchA, expiresInDays = 400 })).StatusCode);
    }

    [DbFact]
    public async Task AT123_127_a_customer_joins_with_separate_consents_changes_them_at_once_and_leaves_and_each_store_sees_only_its_own_membership()
    {
        HttpClient ownerA = Store(_ownerSubA, _tenantA), ownerB = Store(_ownerSubB, _tenantB);
        (_, string tokenA) = await InviteAsync(ownerA, _branchA);
        (_, string tokenB) = await InviteAsync(ownerB, _branchB);
        HttpClient me = await CustomerAsync();

        // joining without a customer token is refused; with one, the consents are recorded per purpose and channel
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync($"/api/v1/c/join/{tokenA}", new { consents = Array.Empty<object>(), policyVersion = "1" })).StatusCode);
        HttpResponseMessage joined = await me.PostAsJsonAsync($"/api/v1/c/join/{tokenA}", new
        {
            consents = new[] { new { purpose = "Operational", channel = "Sms", granted = true }, new { purpose = "Marketing", channel = "Sms", granted = false } },
            policyVersion = "2026-1",
        });
        Assert.Equal(HttpStatusCode.Created, joined.StatusCode);
        JsonElement membership = await Body(joined);
        Guid membershipId = membership.GetProperty("membershipId").GetGuid();
        Assert.Equal("نان‌آور", membership.GetProperty("storeName").GetString());
        Assert.Equal(new[] { ("Marketing", false), ("Operational", true) },
            membership.GetProperty("consents").EnumerateArray().Select(c => (c.GetProperty("purpose").GetString()!, c.GetProperty("granted").GetBoolean())).OrderBy(c => c.Item1, StringComparer.Ordinal));

        // the same QR again is a conflict; a bad consent is a validation error
        Assert.Equal(HttpStatusCode.Conflict, (await me.PostAsJsonAsync($"/api/v1/c/join/{tokenA}", new { consents = Array.Empty<object>(), policyVersion = "1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync($"/api/v1/c/join/{tokenB}", new { consents = new[] { new { purpose = "Spam", channel = "Sms", granted = true } }, policyVersion = "1" })).StatusCode);

        // a second store: the customer is one person with two independent memberships (DI-062)
        Assert.Equal(HttpStatusCode.Created, (await me.PostAsJsonAsync($"/api/v1/c/join/{tokenB}", new { consents = Array.Empty<object>(), policyVersion = "1" })).StatusCode);
        JsonElement mine = await Body(await me.GetAsync("/api/v1/c/memberships"));
        Assert.Equal(new[] { "دیگری", "نان‌آور" }, mine.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("storeName").GetString()!).Order(StringComparer.Ordinal));

        // AT-127: marketing is switched on and the operational one off; the newest record is in force at once and history is kept
        HttpResponseMessage changed = await me.PutAsJsonAsync($"/api/v1/c/memberships/{membershipId}/consents", new
        {
            consents = new[] { new { purpose = "Operational", channel = "Sms", granted = false }, new { purpose = "Marketing", channel = "Sms", granted = true } },
            policyVersion = "2026-2",
        });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var now = (await Body(changed)).GetProperty("consents").EnumerateArray().ToDictionary(c => c.GetProperty("purpose").GetString()!, c => (c.GetProperty("granted").GetBoolean(), c.GetProperty("policyVersion").GetString()));
        Assert.Equal((false, "2026-2"), now["Operational"]);
        Assert.Equal((true, "2026-2"), now["Marketing"]);
        await using var ds = NpgsqlDataSource.Create(Cs);
        long records = await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenantA)), async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT count(*) FROM cid.consent_record WHERE membership_id = $1");
            cmd.Parameters.Add(new NpgsqlParameter { Value = membershipId });
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        });
        Assert.Equal(4, records);   // two at joining, two at the change: nothing was rewritten (T53)

        // another customer cannot touch this membership; leaving withdraws everything and ends it
        HttpClient stranger = await CustomerAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync($"/api/v1/c/memberships/{membershipId}/consents", new { consents = new[] { new { purpose = "Marketing", channel = "Sms", granted = false } }, policyVersion = "1" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/v1/c/memberships/{membershipId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await me.DeleteAsync($"/api/v1/c/memberships/{membershipId}")).StatusCode);
        Assert.Equal(new[] { "دیگری" }, (await Body(await me.GetAsync("/api/v1/c/memberships"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("storeName").GetString()!));

        // the customer may come back with the same QR: reactivated, not duplicated
        Assert.Equal(HttpStatusCode.Created, (await me.PostAsJsonAsync($"/api/v1/c/join/{tokenA}", new { consents = Array.Empty<object>(), policyVersion = "3" })).StatusCode);
    }
}
