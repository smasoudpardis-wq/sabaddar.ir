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

/// <summary>US-026 / IAM-007 / AT-016 / SoD-01 / D-61: request, decide, consume once with the exact binding.</summary>
public sealed class ApprovalTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _b2 = Guid.CreateVersion7(), _r1 = Guid.CreateVersion7();
    private readonly Dictionary<string, (Guid Id, string Sub)> _u = [];
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";
    private static readonly PostgresApprovals Approvals = new(TimeProvider.System);

    public ApprovalTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach (string n in new[] { "cashierA", "cashierB", "managerB1", "managerB2" })
        {
            _u[n] = (Guid.CreateVersion7(), $"ap-{n}-{Guid.NewGuid()}");
            (Guid id, string sub) = _u[n];
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, $4)", id, ApiFactory.Issuer, sub, n));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'تأییدها', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک'), ($1, $3, 'B2', 'دو')", _tenant, _b1, _b2);
            foreach ((string n, string role, Guid scope) in new[] { ("cashierA", "Cashier", _b1), ("cashierB", "Cashier", _b1), ("managerB1", "BranchManager", _b1), ("managerB2", "BranchManager", _b2) })
            {
                await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _tenant, _u[n].Id);
                await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES ($1, $2, $3, $4, 'Branch', $5)",
                    _tenant, Guid.CreateVersion7(), _u[n].Id, role, scope);
            }

            return 0;
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

    private ApprovalAsk Discount(string percent = "20") =>
        new("pos.discount.apply", "SaleLine", "sale-1/1", new ScopeRef(ScopeType.Register, _r1, _b1), "amount-limit", percent, "0", "مشتری ثابت");

    private async Task<T> InTenant<T>(string user, Func<IUnitOfWorkScope, CancellationToken, Task<T>> work)
    {
        await using var ds = NpgsqlDataSource.Create(Cs);
        return await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant), new UserId(_u[user].Id)), work);
    }

    private Task<Guid> Request(string user, ApprovalAsk ask) => InTenant(user, (s, ct) => Approvals.RequestAsync(s, ask, new UserId(_u[user].Id), ct));

    private Task<bool> Consume(string user, Guid id, ApprovalAsk ask) => InTenant(user, (s, ct) => Approvals.ConsumeAsync(s, id, ask, new UserId(_u[user].Id), ct));

    private HttpClient As(string user, string acr = "mfa", DateTimeOffset? authTime = null)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(_u[user].Sub, acr: acr, authTime: authTime ?? DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, _tenant.ToString());
        return c;
    }

    private Task<HttpResponseMessage> Decide(string user, Guid id, string decision = "Approve", string acr = "mfa", DateTimeOffset? authTime = null) =>
        As(user, acr, authTime).PostAsJsonAsync($"/api/v1/approvals/{id}/decision", new { decision });

    private static async Task<string?> Code(HttpResponseMessage r)
    {
        using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return d.RootElement.GetProperty("code").GetString();
    }

    [DbFact]
    public async Task Cashier_asks_manager_approves_and_the_approval_works_exactly_once()
    {
        Guid id = await Request("cashierA", Discount());
        using (JsonDocument inbox = JsonDocument.Parse(await (await As("managerB1", "pwd").GetAsync("/api/v1/approvals")).Content.ReadAsStringAsync()))
        {
            Assert.Contains(inbox.RootElement.EnumerateArray(), a => a.GetProperty("id").GetGuid() == id);
        }

        HttpResponseMessage approved = await Decide("managerB1", id);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        using (JsonDocument a = JsonDocument.Parse(await approved.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Approved", a.RootElement.GetProperty("status").GetString());
            Assert.Equal(_u["managerB1"].Id, a.RootElement.GetProperty("decidedBy").GetGuid());   // AT-016: approver recorded
            Assert.Equal("مشتری ثابت", a.RootElement.GetProperty("reason").GetString());
        }

        Assert.False(await Consume("cashierA", id, Discount("25")));   // other value: D-61 binding
        Assert.False(await Consume("cashierB", id, Discount()));       // other requester
        Assert.True(await Consume("cashierA", id, Discount()));        // the exact action, once
        Assert.False(await Consume("cashierA", id, Discount()));       // single use
    }

    [DbFact]
    public async Task Nobody_approves_their_own_request_and_capped_or_out_of_scope_roles_cannot_approve()
    {
        Guid own = await Request("managerB1", Discount());
        Assert.Equal("self-approval", await Code(await Decide("managerB1", own)));

        Guid id = await Request("cashierA", Discount());
        Assert.Equal(HttpStatusCode.NotFound, (await Decide("cashierB", id)).StatusCode);     // a capped cashier is not involved at all
        Assert.Equal(HttpStatusCode.NotFound, (await Decide("managerB2", id)).StatusCode);    // other branch: not involved
        Assert.Equal(HttpStatusCode.NotFound, (await As("managerB2", "pwd").GetAsync($"/api/v1/approvals/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As("cashierA", "pwd").GetAsync($"/api/v1/approvals/{id}")).StatusCode); // requester sees it
    }

    [DbFact]
    public async Task Deciding_needs_a_recent_second_factor()
    {
        Guid id = await Request("cashierA", Discount());
        Assert.Equal("step-up-required", await Code(await Decide("managerB1", id, acr: "pwd")));
        Assert.Equal("step-up-required", await Code(await Decide("managerB1", id, authTime: DateTimeOffset.UtcNow.AddMinutes(-20))));
        Assert.Equal(HttpStatusCode.OK, (await Decide("managerB1", id)).StatusCode);
        Assert.Equal("approval-not-pending", await Code(await Decide("managerB1", id, "Reject")));
    }

    [DbFact]
    public async Task Rejected_expired_and_stale_approvals_cannot_be_used()
    {
        Guid rejected = await Request("cashierA", Discount());
        Assert.Equal(HttpStatusCode.OK, (await Decide("managerB1", rejected, "Reject")).StatusCode);
        Assert.False(await Consume("cashierA", rejected, Discount()));

        // a request created 11 minutes ago (clock in the past) is past its 10-minute decision window
        var past = new PostgresApprovals(new Shifted(TimeSpan.FromMinutes(-11)));
        Guid late = await InTenant("cashierA", (s, ct) => past.RequestAsync(s, Discount(), new UserId(_u["cashierA"].Id), ct));
        Assert.Equal("approval-expired", await Code(await Decide("managerB1", late)));

        // an approval used 6 minutes after the decision is stale (5-minute validity, D-61)
        Guid stale = await Request("cashierA", Discount());
        Assert.Equal(HttpStatusCode.OK, (await Decide("managerB1", stale)).StatusCode);
        var future = new PostgresApprovals(new Shifted(TimeSpan.FromMinutes(6)));
        Assert.False(await InTenant("cashierA", (s, ct) => future.ConsumeAsync(s, stale, Discount(), new UserId(_u["cashierA"].Id), ct)));
        Assert.True(await Consume("cashierA", stale, Discount())); // still valid right now
    }

    private sealed class Shifted(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + offset;
    }
}
