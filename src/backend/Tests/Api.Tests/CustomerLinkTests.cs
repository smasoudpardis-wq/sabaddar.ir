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

/// <summary>US-143 / CUS-004 / CUS-005 / BR-061 / BR-062 / AT-124 / AT-220 / AT-221: the cashier identifies a customer; a new membership needs the customer's own proof.</summary>
public sealed class CustomerLinkTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";
    private readonly Guid _tenantA = Guid.CreateVersion7(), _tenantB = Guid.CreateVersion7(), _branchA = Guid.CreateVersion7(), _branchB = Guid.CreateVersion7();
    private readonly Guid _ownerA = Guid.CreateVersion7(), _ownerB = Guid.CreateVersion7(), _cashierA = Guid.CreateVersion7();
    private readonly string _ownerSubA = "lnk-ownerA-" + Guid.NewGuid(), _ownerSubB = "lnk-ownerB-" + Guid.NewGuid(), _cashierSubA = "lnk-cashierA-" + Guid.NewGuid();

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

    private sealed record Person(HttpClient Client, string Phone, string PublicCode);

    private async Task<Person> NewPersonAsync()
    {
        HttpClient anon = factory.CreateClient();
        anon.DefaultRequestHeaders.Add("X-Forwarded-For", $"10.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        string phone = "09" + Random.Shared.Next(100_000_000, 999_999_999);
        JsonElement start = await Body(await anon.PostAsJsonAsync("/api/v1/c/auth/otp", new { phone }));
        JsonElement session = await Body(await anon.PostAsJsonAsync("/api/v1/c/auth/otp/verify",
            new { challengeId = start.GetProperty("challengeId").GetGuid(), code = factory.Otps.Latest["+98" + phone[1..]] }));
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        return new Person(c, "+98" + phone[1..], session.GetProperty("publicCode").GetString()!);
    }

    private static async Task<string> QrAsync(Person p) => (await Body(await p.Client.PostAsync("/api/v1/c/me/qr", null))).GetProperty("token").GetString()!;

    private static async Task JoinAsync(HttpClient owner, Guid branch, Person p)
    {
        HttpResponseMessage inv = await owner.PostAsJsonAsync("/api/v1/store-invites", new { branchId = branch });
        string url = (await Body(inv)).GetProperty("url").GetString()!;
        Assert.Equal(HttpStatusCode.Created, (await p.Client.PostAsJsonAsync($"/api/v1/c/join/{url[(url.LastIndexOf("/join/", StringComparison.Ordinal) + 6)..]}",
            new { consents = new[] { new { purpose = "Operational", channel = "Sms", granted = true } }, policyVersion = "1" })).StatusCode);
    }

    /// <summary>Test setup only: pushes every earlier challenge a few minutes back (the guard trigger is bypassed as the superuser).</summary>
    private static async Task AgeChallengesAsync()
    {
        string admin = new NpgsqlConnectionStringBuilder(Cs)
        {
            Username = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres", Password = Environment.GetEnvironmentVariable("PGPASSWORD") ?? "postgres",
        }.ToString();
        await using var ds = NpgsqlDataSource.Create(admin);
        await using NpgsqlConnection c = await ds.OpenConnectionAsync();
        await using NpgsqlCommand cmd = new("SET session_replication_role = replica; UPDATE cid.otp_challenge SET created_at = created_at - interval '5 minutes', expires_at = expires_at - interval '5 minutes' WHERE created_at > now() - interval '5 minutes'", c);
        await cmd.ExecuteNonQueryAsync();
    }

    [DbFact]
    public async Task AT220_221_a_customer_QR_works_once_and_a_lookup_only_ever_answers_about_this_store()
    {
        HttpClient ownerA = Store(_ownerSubA, _tenantA), ownerB = Store(_ownerSubB, _tenantB), cashier = Store(_cashierSubA, _tenantA);
        Person member = await NewPersonAsync(), ofB = await NewPersonAsync();
        await JoinAsync(ownerA, _branchA, member);
        await JoinAsync(ownerB, _branchB, ofB);

        // the QR names no one, shows once: the first scan finds the member, the same QR again is 401 (AT-220)
        string qr = await QrAsync(member);
        Assert.Matches(@"^q1\.[0-9a-f]{32}\.", qr);
        JsonElement found = await Body(await cashier.PostAsJsonAsync("/api/v1/customers/identify", new { qrToken = qr }));
        Assert.Equal("member", found.GetProperty("result").GetString());
        Guid tenantCustomerId = found.GetProperty("tenantCustomerId").GetGuid();
        Assert.Matches(@"^09\d{2}\*\*\*\d{4}$", found.GetProperty("maskedName").GetString()!);
        HttpResponseMessage again = await cashier.PostAsJsonAsync("/api/v1/customers/identify", new { qrToken = qr });
        Assert.Equal((HttpStatusCode.Unauthorized, "qr-invalid"), (again.StatusCode, (await Body(again)).GetProperty("code").GetString()));
        string[] parts = (await QrAsync(member)).Split('.');
        Assert.Equal(HttpStatusCode.Unauthorized, (await cashier.PostAsJsonAsync("/api/v1/customers/identify", new { qrToken = $"q1.{Guid.NewGuid():N}.{parts[2]}" })).StatusCode);   // another jti, same mac: forged

        // AT-221: a customer of store B, a made-up code and a valid-looking one all answer "not a member here" and nothing more
        foreach (string code in new[] { ofB.PublicCode, "CU-23456789" })
        {
            JsonElement r = await Body(await cashier.PostAsJsonAsync("/api/v1/customers/identify", new { publicCode = code }));
            Assert.Equal(["joinRequestId", "result"], r.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
            Assert.Equal(("not_member", JsonValueKind.Null), (r.GetProperty("result").GetString(), r.GetProperty("joinRequestId").ValueKind));
        }

        Assert.Equal("invalid", (await Body(await cashier.PostAsJsonAsync("/api/v1/customers/identify", new { publicCode = "nonsense" }))).GetProperty("result").GetString());
        Assert.Equal("member", (await Body(await cashier.PostAsJsonAsync("/api/v1/customers/identify", new { publicCode = member.PublicCode }))).GetProperty("result").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await cashier.PostAsJsonAsync("/api/v1/customers/identify", new { })).StatusCode);   // neither
        Assert.Equal(HttpStatusCode.BadRequest, (await cashier.PostAsJsonAsync("/api/v1/customers/identify", new { qrToken = qr, publicCode = member.PublicCode })).StatusCode);   // both

        // the member's record: masked for the cashier; the full number only for the owner with a fresh second factor, and it is another store's 404
        JsonElement masked = await Body(await cashier.GetAsync($"/api/v1/customers/{tenantCustomerId}"));
        Assert.Equal(JsonValueKind.Null, masked.GetProperty("phone").ValueKind);
        Assert.Equal(member.Phone, (await Body(await ownerA.GetAsync($"/api/v1/customers/{tenantCustomerId}"))).GetProperty("phone").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.GetAsync($"/api/v1/customers/{tenantCustomerId}")).StatusCode);
    }

    [DbFact]
    public async Task UC03_a_customer_who_shows_their_own_QR_but_is_not_a_member_is_asked_on_their_device_and_only_they_can_accept()
    {
        HttpClient cashier = Store(_cashierSubA, _tenantA);
        Person newcomer = await NewPersonAsync(), other = await NewPersonAsync();
        JsonElement result = await Body(await cashier.PostAsJsonAsync("/api/v1/customers/identify", new { qrToken = await QrAsync(newcomer) }));
        Assert.Equal("not_member", result.GetProperty("result").GetString());
        Guid request = result.GetProperty("joinRequestId").GetGuid();

        var accept = new { consents = new[] { new { purpose = "Operational", channel = "Sms", granted = true } }, policyVersion = "2026-1" };
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsJsonAsync($"/api/v1/c/join-requests/{request}/accept", accept)).StatusCode);   // not theirs
        Assert.Equal(HttpStatusCode.Created, (await newcomer.Client.PostAsJsonAsync($"/api/v1/c/join-requests/{request}/accept", accept)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await newcomer.Client.PostAsJsonAsync($"/api/v1/c/join-requests/{request}/accept", accept)).StatusCode);   // once
        Assert.Equal("member", (await Body(await cashier.PostAsJsonAsync("/api/v1/customers/identify", new { publicCode = newcomer.PublicCode }))).GetProperty("result").GetString());
    }

    [DbFact]
    public async Task AT124_a_spoken_public_code_alone_never_makes_a_membership_the_customer_must_say_the_code_sent_to_their_own_phone()
    {
        HttpClient cashier = Store(_cashierSubA, _tenantA);
        Person p = await NewPersonAsync();
        await AgeChallengesAsync();   // the sign-in code just sent would block a second send for a minute (CUS-001)
        // the code goes to the number the customer verified, not to one the cashier names; the answer for an unknown code looks the same
        HttpResponseMessage started = await cashier.PostAsJsonAsync("/api/v1/customers/otp-challenges", new { purpose = "LinkMembership", publicCode = p.PublicCode });
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        Guid challenge = (await Body(started)).GetProperty("challengeId").GetGuid();
        string sent = factory.Otps.Latest[p.Phone];
        string wrong = sent == "000000" ? "111111" : "000000";
        var consents = new[] { new { purpose = "Operational", channel = "Sms", granted = true } };
        HttpResponseMessage bad = await cashier.PostAsJsonAsync("/api/v1/customer-memberships/link", new { challengeId = challenge, code = wrong, consents });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, 4), (bad.StatusCode, (await Body(bad)).GetProperty("context").GetProperty("attemptsRemaining").GetInt32()));   // the wrong try was counted though the call failed

        HttpResponseMessage linked = await cashier.PostAsJsonAsync("/api/v1/customer-memberships/link", new { challengeId = challenge, code = sent, consents });
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);
        JsonElement view = await Body(linked);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("phone").ValueKind);
        Assert.Equal("member", (await Body(await cashier.PostAsJsonAsync("/api/v1/customers/identify", new { publicCode = p.PublicCode }))).GetProperty("result").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await cashier.PostAsJsonAsync("/api/v1/customer-memberships/link", new { challengeId = challenge, code = sent, consents })).StatusCode);   // a code opens once

        // an unknown code: a challenge, nothing sent, nothing opens it
        string before = factory.Otps.Latest.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        HttpResponseMessage ghost = await cashier.PostAsJsonAsync("/api/v1/customers/otp-challenges", new { purpose = "LinkMembership", publicCode = "CU-23456789" });
        Assert.Equal(HttpStatusCode.Created, ghost.StatusCode);
        Assert.Equal(before, factory.Otps.Latest.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await cashier.PostAsJsonAsync("/api/v1/customer-memberships/link",
            new { challengeId = (await Body(ghost)).GetProperty("challengeId").GetGuid(), code = "123456", consents })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await cashier.PostAsJsonAsync("/api/v1/customers/otp-challenges", new { purpose = "GuestReceipt", phone = "09120000000" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cashier.PostAsJsonAsync("/api/v1/customers/otp-challenges", new { purpose = "LinkMembership", publicCode = "x" })).StatusCode);
    }
}
