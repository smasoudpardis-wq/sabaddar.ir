using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using RetailCloud.BuildingBlocks.Api;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Inventory.Application;
using RetailCloud.CustomerIdentity.Application;

namespace RetailCloud.Api.Tests;

/// <summary>US-094 / SAL-003 / CUS-006 / BR-073 / AT-126: a member (and the buyer's tax identifier) on a sale; a finalized sale takes them once.</summary>
public sealed class AttachCustomerTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly Guid _tenant = Guid.CreateVersion7(), _b1 = Guid.CreateVersion7(), _r1 = Guid.CreateVersion7(), _r2 = Guid.CreateVersion7(),
        _w1 = Guid.CreateVersion7();
    private readonly string _device = "dev-att-" + Guid.NewGuid();
    private readonly Dictionary<string, (Guid Id, string Sub)> _u = [];
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";

    public AttachCustomerTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach (string n in new[] { "owner", "cashier", "cashier2" })
        {
            _u[n] = (Guid.CreateVersion7(), $"att-{n}-{Guid.NewGuid()}");
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, $4)", _u[n].Id, ApiFactory.Issuer, _u[n].Sub, n));
        }

        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'نهایی', 'supermarket')", _tenant);
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک')", _tenant, _b1);
            await Exec(s, ct, "INSERT INTO platform.register (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'R1'), ($1, $4, $3, 'R2')", _tenant, _r1, _b1, _r2);
            await Exec(s, ct, "INSERT INTO inv.warehouse (tenant_id, id, branch_id, code, kind) VALUES ($1, $2, $3, 'W1', 'Sellable')", _tenant, _w1, _b1);
            await Exec(s, ct, "INSERT INTO platform.terminal (tenant_id, id, branch_id, device_name, secret_hash, paired_by) VALUES ($1, $2, $3, 'صندوق', $4, $5)",
                _tenant, Guid.CreateVersion7(), _b1, SHA256.HashData(Encoding.UTF8.GetBytes(_device)), _u["owner"].Id);
            foreach ((string n, string role, string scopeType) in new[]
                     { ("owner", "TenantOwner", "Tenant"), ("cashier", "Cashier", "Branch"), ("cashier2", "Cashier", "Branch") })
            {
                await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _tenant, _u[n].Id);
                await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES ($1, $2, $3, $4, $5, $6)",
                    _tenant, Guid.CreateVersion7(), _u[n].Id, role, scopeType, scopeType == "Tenant" ? DBNull.Value : _b1);
            }

            return 0;
        });
        await TestAccounting.SetUpAsync(Cs, _tenant);
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

    private async Task<T> Scalar<T>(string sql, params object[] args) =>
        await new PostgresUnitOfWork(NpgsqlDataSource.Create(Cs)).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(sql);
            foreach (object a in args)
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = a });
            }

            return (T)(await cmd.ExecuteScalarAsync(ct))!;
        });

    private HttpClient As(string user, bool onDevice = true)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(_u[user].Sub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, _tenant.ToString());
        if (onDevice)
        {
            c.DefaultRequestHeaders.Add(RequestContextMiddleware.DeviceHeader, _device);
        }

        return c;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    private static async Task<string?> Code(HttpResponseMessage r) => (await Body(r)).TryGetProperty("code", out JsonElement c) ? c.GetString() : null;

    private static async Task<HttpResponseMessage> Idem(HttpClient c, string path, object body, string? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await c.SendAsync(req);
    }

    /// <summary>A product at <paramref name="price"/> (10% tax) with <paramref name="stock"/> units at <paramref name="cost"/> in W1.</summary>
    private async Task<Guid> Product(string sku, string price, string stock, string cost)
    {
        HttpResponseMessage r = await Idem(As("owner", false), "/api/v1/products", new
        {
            name = "کالا " + sku, baseUnit = "EA", taxRate = 1000, confirmation = new { confirmed = true },
            openingStock = new { warehouseId = _w1, qty = stock, unitCost = cost },
            variants = new[] { new { sku, unitPrice = price, barcodes = new[] { new { code = sku, isInternal = true } } } },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Body(r)).GetProperty("variants")[0].GetProperty("id").GetGuid();
    }

    private async Task OpenShift(string user, Guid register) =>
        Assert.Equal(HttpStatusCode.Created, (await Idem(As(user), "/api/v1/shifts", new { registerId = register, openingCash = "0" })).StatusCode);

    /// <summary>A cart of <paramref name="qty"/> × <paramref name="sku"/>, paid in cash (handed over: the total rounded up to 50,000).</summary>
    private async Task<JsonElement> PaidCart(string user, Guid register, string sku, string qty)
    {
        HttpResponseMessage created = await As(user).PostAsJsonAsync("/api/v1/pos/sales", new { registerId = register, clientDraftId = "d-" + Guid.NewGuid().ToString("N") });
        Guid sale = (await Body(created)).GetProperty("id").GetGuid();
        JsonElement cart = await Body(await As(user).PostAsJsonAsync($"/api/v1/pos/sales/{sale}/lines", new { barcode = sku, qty }));
        string total = cart.GetProperty("totals").GetProperty("total").GetString()!;
        long handed = (long.Parse(total, CultureInfo.InvariantCulture) + 49_999) / 50_000 * 50_000;
        HttpResponseMessage paid = await Idem(As(user), $"/api/v1/pos/sales/{sale}/tenders", new { method = "Cash", amount = total, tendered = handed.ToString(CultureInfo.InvariantCulture) });
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        return await Body(paid);
    }

    private Task<HttpResponseMessage> Finalize(string user, JsonElement sale, string? key = null, string? total = null) =>
        Idem(As(user), $"/api/v1/sales/{sale.GetProperty("id").GetGuid()}/finalize",
            new { expectedVersion = sale.GetProperty("version").GetInt32(), expectedTotal = total ?? sale.GetProperty("totals").GetProperty("total").GetString() }, key);

    private async Task<Dictionary<string, (long Debit, long Credit)>> Entry(Guid saleId, string purpose)
    {
        JsonElement j = (await Body(await As("owner", false).GetAsync("/api/v1/accounting/journals?sourceType=Sale&limit=100"))).GetProperty("items").EnumerateArray()
            .Single(e => e.GetProperty("source").GetProperty("id").GetGuid() == saleId && e.GetProperty("source").GetProperty("purpose").GetString() == purpose);
        Assert.Equal("Posted", j.GetProperty("status").GetString());
        return j.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("accountCode").GetString()!,
            l => (long.Parse(l.GetProperty("debit").GetString()!, CultureInfo.InvariantCulture), long.Parse(l.GetProperty("credit").GetString()!, CultureInfo.InvariantCulture)));
    }


    private async Task<(HttpClient Client, string Phone, string Code)> NewPersonAsync()
    {
        HttpClient anon = _factory.CreateClient();
        anon.DefaultRequestHeaders.Add("X-Forwarded-For", $"10.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        string phone = "09" + Random.Shared.Next(100_000_000, 999_999_999);
        JsonElement start = await Body(await anon.PostAsJsonAsync("/api/v1/c/auth/otp", new { phone }));
        JsonElement session = await Body(await anon.PostAsJsonAsync("/api/v1/c/auth/otp/verify",
            new { challengeId = start.GetProperty("challengeId").GetGuid(), code = _factory.Otps.Latest["+98" + phone[1..]] }));
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        return (c, phone, session.GetProperty("publicCode").GetString()!);
    }

    private async Task<Guid> MemberAsync((HttpClient Client, string Phone, string Code) p)
    {
        HttpResponseMessage inv = await As("owner", false).PostAsJsonAsync("/api/v1/store-invites", new { branchId = _b1 });
        string url = (await Body(inv)).GetProperty("url").GetString()!;
        JsonElement joined = await Body(await p.Client.PostAsJsonAsync($"/api/v1/c/join/{url[(url.LastIndexOf("/join/", StringComparison.Ordinal) + 6)..]}",
            new { consents = Array.Empty<object>(), policyVersion = "1" }));
        JsonElement found = await Body(await As("cashier").PostAsJsonAsync("/api/v1/customers/identify", new { publicCode = p.Code }));
        return found.GetProperty("tenantCustomerId").GetGuid();
    }

    private async Task<JsonElement> CartAsync(string sku, string qty = "1")
    {
        HttpResponseMessage created = await As("cashier").PostAsJsonAsync("/api/v1/pos/sales", new { registerId = _r1, clientDraftId = "d-" + Guid.NewGuid().ToString("N") });
        Guid id = (await Body(created)).GetProperty("id").GetGuid();
        return await Body(await As("cashier").PostAsJsonAsync($"/api/v1/pos/sales/{id}/lines", new { barcode = sku, qty }));
    }

    [DbFact]
    public async Task AT126_a_cart_takes_a_member_by_id_by_code_or_by_the_customers_one_use_QR_and_only_a_member_of_this_store()
    {
        await Product("AT-TEA", "100000", "100", "50000");
        await OpenShift("cashier", _r1);
        var me = await NewPersonAsync();
        Guid member = await MemberAsync(me);
        var stranger = await NewPersonAsync();   // a customer who belongs to no store here

        JsonElement cart = await CartAsync("AT-TEA");
        Guid id = cart.GetProperty("id").GetGuid();
        string url = $"/api/v1/pos/sales/{id}/customer";

        // by id; the sale shows the customer masked (never the full number)
        HttpResponseMessage byId = await As("cashier").PostAsJsonAsync(url, new { expectedVersion = cart.GetProperty("version").GetInt32(), tenantCustomerId = member });
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        JsonElement linked = await Body(byId);
        Assert.Equal(member, linked.GetProperty("customer").GetProperty("tenantCustomerId").GetGuid());
        Assert.Matches(@"^09\d{2}\*\*\*\d{4}$", linked.GetProperty("customer").GetProperty("maskedName").GetString()!);
        Assert.DoesNotContain(me.Phone[3..], await (await As("cashier").GetAsync($"/api/v1/pos/sales/{id}")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // a stale version loses; a stranger by code is refused (not a member); a member by code works; a QR works once
        Assert.Equal(HttpStatusCode.Conflict, (await As("cashier").PostAsJsonAsync(url, new { expectedVersion = 1, tenantCustomerId = member })).StatusCode);
        HttpResponseMessage notMember = await As("cashier").PostAsJsonAsync(url, new { publicCode = stranger.Code });
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "not-a-member"), (notMember.StatusCode, await Code(notMember)));
        Assert.Equal(HttpStatusCode.OK, (await As("cashier").PostAsJsonAsync(url, new { publicCode = me.Code })).StatusCode);
        string qr = (await Body(await me.Client.PostAsync("/api/v1/c/me/qr", null))).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await As("cashier").PostAsJsonAsync(url, new { qrToken = qr })).StatusCode);
        HttpResponseMessage reused = await As("cashier").PostAsJsonAsync(url, new { qrToken = qr });
        Assert.Equal((HttpStatusCode.Unauthorized, "qr-invalid"), (reused.StatusCode, await Code(reused)));

        // exactly one way, and a customer of another store's id is not found here
        Assert.Equal(HttpStatusCode.BadRequest, (await As("cashier").PostAsJsonAsync(url, new { publicCode = me.Code, tenantCustomerId = member })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await As("cashier").PostAsJsonAsync(url, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As("cashier").PostAsJsonAsync(url, new { tenantCustomerId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As("cashier2").PostAsJsonAsync(url, new { tenantCustomerId = member })).StatusCode);   // not their cart
    }

    [DbFact]
    public async Task BR073_the_buyers_identifier_is_checked_stored_encrypted_and_never_shown()
    {
        await Product("AT-BUY", "100000", "100", "50000");
        await OpenShift("cashier", _r1);
        JsonElement cart = await CartAsync("AT-BUY");
        string url = $"/api/v1/pos/sales/{cart.GetProperty("id").GetGuid()}/customer";

        Assert.Equal(HttpStatusCode.BadRequest, (await As("cashier").PostAsJsonAsync(url, new { buyerTaxInfo = new { type = "Individual", nationalId = "1234567890" } })).StatusCode);   // wrong check digit
        Assert.Equal(HttpStatusCode.BadRequest, (await As("cashier").PostAsJsonAsync(url, new { buyerTaxInfo = new { type = "Individual", nationalId = "0013542419", economicCode = "123456789012" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await As("cashier").PostAsJsonAsync(url, new { buyerTaxInfo = new { type = "Legal", economicCode = "123" } })).StatusCode);
        HttpResponseMessage ok = await As("cashier").PostAsJsonAsync(url, new { buyerTaxInfo = new { type = "Individual", nationalId = "0013542419" } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.DoesNotContain("0013542419", await ok.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // in the database it is ciphertext: the plain id is nowhere in the row
        await using var ds = NpgsqlDataSource.Create(Cs);
        byte[] enc = await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(_tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT identifier_enc FROM sales.sale_buyer WHERE sale_id = $1");
            cmd.Parameters.Add(new NpgsqlParameter { Value = cart.GetProperty("id").GetGuid() });
            return (byte[])(await cmd.ExecuteScalarAsync(ct))!;
        });
        Assert.DoesNotContain("0013542419", Encoding.UTF8.GetString(enc), StringComparison.Ordinal);
        Assert.Equal("Individual|0013542419|", RetailCloud.CustomerIdentity.Infrastructure.AesContactProtector.FromBase64(ApiFactory.CustomerKey, ApiFactory.CustomerIndexKey).Unprotect(enc));
        // a legal entity may replace it while the cart is open
        Assert.Equal(HttpStatusCode.OK, (await As("cashier").PostAsJsonAsync(url, new { buyerTaxInfo = new { type = "Legal", economicCode = "123456789012", nationalId = "10100000001" } })).StatusCode);
    }

    [DbFact]
    public async Task AT126_a_finalized_sale_takes_its_member_once_and_a_cancelled_one_none()
    {
        await Product("AT-FIN", "100000", "100", "50000");
        await OpenShift("cashier", _r1);
        var me = await NewPersonAsync();
        Guid member = await MemberAsync(me);
        JsonElement paid = await PaidCart("cashier", _r1, "AT-FIN", "1");
        Guid id = paid.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Finalize("cashier", paid)).StatusCode);

        string url = $"/api/v1/pos/sales/{id}/customer";
        HttpResponseMessage first = await As("cashier").PostAsJsonAsync(url, new { tenantCustomerId = member });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("Finalized", (await Body(first)).GetProperty("status").GetString());
        HttpResponseMessage second = await As("cashier").PostAsJsonAsync(url, new { tenantCustomerId = member });
        Assert.Equal((HttpStatusCode.Conflict, "customer-already-linked"), (second.StatusCode, await Code(second)));

        // a cancelled cart takes no customer
        JsonElement cart = await CartAsync("AT-FIN");
        Guid cancelled = cart.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await As("cashier").PostAsJsonAsync($"/api/v1/pos/sales/{cancelled}/cancel", new { expectedVersion = cart.GetProperty("version").GetInt32(), reason = "انصراف مشتری" })).StatusCode);
        Assert.Equal("sale-cancelled", await Code(await As("cashier").PostAsJsonAsync($"/api/v1/pos/sales/{cancelled}/customer", new { tenantCustomerId = member })));
    }
}
