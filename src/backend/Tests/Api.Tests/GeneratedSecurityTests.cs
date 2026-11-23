using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RetailCloud.BuildingBlocks.Api;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.TestSupport;

namespace RetailCloud.Api.Tests;

/// <summary>
/// US-010 / NFR-SEC-002: security tests generated from the D07 manifest (docs/04-api/D07-operations.json) for every
/// operation the API implements — so a new endpoint is covered the day it is mapped:
/// SEC-T-012 role matrix (BFLA), SEC-T-010 cross-tenant ids (BOLA), SEC-T-011 branch scope, SEC-T-013 mass assignment.
/// A new path parameter or an operation with path and body needs an entry below, otherwise the tests fail and say so.
/// </summary>
public sealed class GeneratedSecurityTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private sealed record Operation(string OperationId, string Method, string Path, string? Permission, string? Scope,
        string[] PathParams, string[]? BodyProps, bool Idempotent);

    /// <summary>Path parameters that are not tenant resources, with the reason.</summary>
    private static readonly Dictionary<string, string> NotTenantResources = new()
    {
        ["token"] = "an invitation token is a bearer capability; it is not tied to the caller's tenant (AT-011 covers it)",
    };

    /// <summary>Valid bodies for operations that have both a path id and a body, so BOLA is decided by the id, not by validation.</summary>
    private static readonly Dictionary<string, Func<Fixture, object>> ValidBodies = new()
    {
        ["createRegister"] = _ => new { code = "R9" },
        ["decideApproval"] = _ => new { decision = "Approve" },
        ["addRoleAssignment"] = f => new { roleId = RoleCatalog.IdOf("Cashier"), scopeType = "Branch", scopeId = f.Branch },
        ["decideSupportAccess"] = _ => new { decision = "Reject" },
        ["updateProduct"] = _ => new { expectedVersion = 1, name = "تغییر" },
        ["updateUser"] = _ => new { expectedVersion = 1, status = "Disabled" },
        ["addVariant"] = _ => new { sku = "GEN-" + Guid.NewGuid().ToString("N")[..8], axisValues = new { size = Guid.NewGuid().ToString("N")[..8] } },
        ["addBarcode"] = _ => new { code = "96385074" },
        ["updateVariant"] = _ => new { expectedVersion = 1, status = "Inactive" },
        ["recordCashMovement"] = _ => new { direction = "In", amount = "1000", reason = "آزمون" },
        ["closeShift"] = _ => new { countedCash = "0", expectedVersion = 1 },
        ["approveShiftVariance"] = _ => new { decision = "Approve" },
        ["reverseJournal"] = _ => new { reason = "آزمون" },
        ["setPrices"] = f => new { items = new[] { new { variantId = f.Variant, unitPrice = "1000" } }, validFrom = DateTimeOffset.UtcNow.AddDays(1) },
    };

    private sealed record Fixture(Guid Tenant, Guid Branch, Guid Branch2, Guid Terminal, Guid Terminal2, Guid User, Guid Assignment,
        Guid Approval, Guid Export, Guid SupportRequest, Guid Product, Guid Variant, Guid Barcode, Guid Shift, Guid Journal,
        Guid PriceList)
    {
        public Guid Id(string param) => param switch
        {
            "branchId" => Branch,
            "terminalId" => Terminal,
            "userId" => User,
            "assignmentId" => Assignment,
            "approvalId" => Approval,
            "exportId" => Export,
            "requestId" => SupportRequest,
            "productId" => Product,
            "variantId" => Variant,
            "barcodeId" => Barcode,
            "shiftId" => Shift,
            "journalId" => Journal,
            "priceListId" => PriceList,
            _ => throw new InvalidOperationException($"No fixture for path parameter '{param}': add one to GeneratedSecurityTests (US-010)"),
        };
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly ApiFactory _factory;
    private static readonly string Cs = Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused";
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private Fixture _a = null!, _b = null!;
    private readonly Dictionary<string, string> _roleSubjects = [];   // role -> subject of a user holding only that role in A

    public GeneratedSecurityTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is null)
        {
            return;
        }

        _a = await Store("الف");
        _b = await Store("ب");
        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        foreach (string role in RoleCatalog.Roles)
        {
            Guid user = Guid.CreateVersion7();
            string sub = $"gen-{_run}-{role}";
            _roleSubjects[role] = sub;
            bool tenantScope = RoleCatalog.AllowedScopes(role).Contains(ScopeType.Tenant);
            await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
                Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'نقش')", user, ApiFactory.Issuer, sub));
            await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_a.Tenant)), async (s, ct) =>
            {
                await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _a.Tenant, user);
                return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES ($1, $2, $3, $4, $5, $6)",
                    _a.Tenant, Guid.CreateVersion7(), user, role, tenantScope ? "Tenant" : "Branch", tenantScope ? DBNull.Value : _a.Branch);
            });
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A BranchManager limited to branch B1 of store A (the role may also be granted tenant-wide, D06 §6.2).</summary>
    private async Task<string> BranchScopedManager()
    {
        Guid user = Guid.CreateVersion7();
        string sub = $"gen-{_run}-bm-b1";
        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        await uow.ExecuteWithoutTenantAsync(null, (s, ct) =>
            Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مدیر شعبه')", user, ApiFactory.Issuer, sub));
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(_a.Tenant)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2)", _a.Tenant, user);
            return await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES ($1, $2, $3, 'BranchManager', 'Branch', $4)",
                _a.Tenant, Guid.CreateVersion7(), user, _a.Branch);
        });
        return sub;
    }

    private async Task<Fixture> Store(string name)
    {
        Guid t = Guid.CreateVersion7(), b1 = Guid.CreateVersion7(), b2 = Guid.CreateVersion7(), term = Guid.CreateVersion7(), term2 = Guid.CreateVersion7(),
            owner = Guid.CreateVersion7(), member = Guid.CreateVersion7(), assignment = Guid.CreateVersion7(), approval = Guid.CreateVersion7(),
            export = Guid.CreateVersion7(), support = Guid.CreateVersion7(), product = Guid.CreateVersion7(), variant = Guid.CreateVersion7(),
            barcode = Guid.CreateVersion7(), register = Guid.CreateVersion7(), shift = Guid.CreateVersion7(), journal = Guid.CreateVersion7(), priceList = Guid.CreateVersion7(),
            cash = Guid.CreateVersion7(), sales = Guid.CreateVersion7(), period = Guid.CreateVersion7();
        await using var ds = NpgsqlDataSource.Create(Cs);
        var uow = new PostgresUnitOfWork(ds);
        await uow.ExecuteWithoutTenantAsync(null, async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'مالک')", owner, ApiFactory.Issuer, $"gen-{_run}-owner-{t}");
            return await Exec(s, ct, "INSERT INTO iam.merchant_user (id, idp_issuer, idp_subject, display_name) VALUES ($1, $2, $3, 'عضو')", member, ApiFactory.Issuer, $"gen-{_run}-member-{t}");
        });
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(t)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, $2, 'general')", t, name);
            return await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B1', 'یک')", t, b1);
        });
        await TestPlans.SetAsync(t, "Enterprise");   // a member per system role exceeds the trial
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(t)), async (s, ct) =>
        {
            await Exec(s, ct, "INSERT INTO platform.branch (tenant_id, id, code, name) VALUES ($1, $2, 'B2', 'دو')", t, b2);
            await Exec(s, ct, "INSERT INTO platform.terminal (tenant_id, id, branch_id, device_name, secret_hash, paired_by) VALUES ($1, $2, $3, 'پایانه', $4, $5)",
                t, term, b1, Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")), owner);
            await Exec(s, ct, "INSERT INTO platform.terminal (tenant_id, id, branch_id, device_name, secret_hash, paired_by) VALUES ($1, $2, $3, 'پایانه ۲', $4, $5)",
                t, term2, b2, Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")), owner);
            await Exec(s, ct, "INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES ($1, $2), ($1, $3)", t, owner, member);
            await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) VALUES ($1, $2, $3, 'TenantOwner', 'Tenant')", t, Guid.CreateVersion7(), owner);
            await Exec(s, ct, "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type, scope_id) VALUES ($1, $2, $3, 'Cashier', 'Branch', $4)", t, assignment, member, b1);
            await Exec(s, ct, "INSERT INTO iam.approval_request (tenant_id, id, action, resource_type, resource_id, scope_type, condition, value, limit_value, reason, binding_hash, requested_by, expires_at) " +
                              "VALUES ($1, $2, 'pos.discount.apply', 'SaleLine', 'gen/1', 'Tenant', 'amount-limit', '20', '0', 'آزمون', sha256('gen'::bytea), $3, now() + interval '30 minutes')",
                t, approval, member);
            await Exec(s, ct, "INSERT INTO iam.support_access_request (tenant_id, id, requested_by, reason, duration_minutes) VALUES ($1, $2, 'https://p|sup', 'بررسی گزارش فروش', 60)", t, support);
            await Exec(s, ct, "INSERT INTO platform.register (tenant_id, id, branch_id, code) VALUES ($1, $2, $3, 'R1')", t, register, b1);
            await Exec(s, ct, "INSERT INTO sales.shift (tenant_id, id, register_id, branch_id, cashier_id, opening_cash) VALUES ($1, $2, $3, $4, $5, 0)",
                t, shift, register, b1, member);
            await Exec(s, ct, "INSERT INTO acc.account (tenant_id, id, code, name, account_type) VALUES ($1, $2, '1101', 'صندوق', 'Asset'), ($1, $3, '4101', 'فروش', 'Revenue')",
                t, cash, sales);
            await Exec(s, ct, "INSERT INTO acc.fiscal_period (tenant_id, id, fiscal_year, period_no, span) VALUES ($1, $2, 1405, 9, tstzrange(now() - interval '10 days', now() + interval '10 days'))",
                t, period);
            await Exec(s, ct, "INSERT INTO acc.journal_entry (tenant_id, id, effective_at, period_id, description) VALUES ($1, $2, now(), $3, 'آزمون')", t, journal, period);
            await Exec(s, ct, "INSERT INTO acc.journal_line (tenant_id, entry_id, line_no, account_id, debit, credit) VALUES ($1, $2, 1, $3, 1000, 0), ($1, $2, 2, $4, 0, 1000)",
                t, journal, cash, sales);
            await Exec(s, ct, "INSERT INTO merch.tenant_product (tenant_id, id, name, base_unit_code) VALUES ($1, $2, 'کالا', 'EA')", t, product);
            await Exec(s, ct, "INSERT INTO merch.tenant_variant (tenant_id, id, product_id, sku) VALUES ($1, $2, $3, 'GEN-1')", t, variant, product);
            await Exec(s, ct, "INSERT INTO merch.tenant_product_barcode (tenant_id, id, variant_id, code_normalized, code_original, is_internal) VALUES ($1, $2, $3, $4, $4, true)",
                t, barcode, variant, "GEN-" + Guid.NewGuid().ToString("N")[..12]);
            await Exec(s, ct, "INSERT INTO merch.price_list (tenant_id, id, name, is_default) VALUES ($1, $2, 'پایه', true)", t, priceList);
            return await Exec(s, ct, "INSERT INTO platform.export_job (tenant_id, id, requested_by, categories) VALUES ($1, $2, $3, '{Products}')", t, export, owner);
        });
        _roleSubjects[$"owner-{t}"] = $"gen-{_run}-owner-{t}";
        return new Fixture(t, b1, b2, term, term2, member, assignment, approval, export, support, product, variant, barcode, shift, journal, priceList);
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

    private List<Operation> Implemented()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "docs", "04-api", "D07-operations.json")))
        {
            dir = dir.Parent;
        }

        List<Operation> d07 = JsonSerializer.Deserialize<List<Operation>>(File.ReadAllText(Path.Combine(dir.FullName, "docs", "04-api", "D07-operations.json")),
            Web)!;
        var mapped = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/v1/", StringComparison.Ordinal))
            .SelectMany(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Select(m =>
                (m, System.Text.RegularExpressions.Regex.Replace(e.RoutePattern.RawText!["/api/v1".Length..], @"\{(\w+):[^}]+\}", "{$1}"))))
            .ToHashSet();
        List<Operation> ops = [.. d07.Where(o => mapped.Contains((o.Method, o.Path)))];
        Assert.True(ops.Count >= 20, $"only {ops.Count} implemented operations found");
        return ops;
    }

    private HttpClient As(string sub, Guid? tenant)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(sub, acr: "mfa", authTime: DateTimeOffset.UtcNow));
        if (tenant is not null)
        {
            c.DefaultRequestHeaders.Add(RequestContextMiddleware.TenantHeader, tenant.Value.ToString());
        }

        return c;
    }

    private static string PathWith(Operation op, Func<string, string> value) =>
        "/api/v1" + System.Text.RegularExpressions.Regex.Replace(op.Path, @"\{(\w+)\}", m => value(m.Groups[1].Value));

    private static Task<HttpResponseMessage> Send(HttpClient c, Operation op, string path, object? body)
    {
        var req = new HttpRequestMessage(new HttpMethod(op.Method), path);
        if (op.Idempotent)
        {
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        }
        if (op.BodyProps is not null)
        {
            req.Content = new StringContent(JsonSerializer.Serialize(body ?? new { }), Encoding.UTF8, "application/json");
        }

        return c.SendAsync(req);
    }

    private static async Task<string?> Code(HttpResponseMessage r)
    {
        string text = await r.Content.ReadAsStringAsync();
        try
        {
            return JsonElement.Parse(text).TryGetProperty("code", out JsonElement c) ? c.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [DbFact]
    public async Task SEC_T_012_every_operation_answers_every_role_as_the_D06_matrix_says()
    {
        var problems = new List<string>();
        foreach (Operation op in Implemented().Where(o => o.Permission is not null && Permissions.All.Contains(o.Permission)))
        {
            foreach (string role in RoleCatalog.Roles)
            {
                RoleGrant grant = RoleCatalog.AllowedScopes(role).Contains(ScopeType.Tenant)
                    ? new RoleGrant(role, ScopeType.Tenant, null) : new RoleGrant(role, ScopeType.Branch, _a.Branch);
                bool allowed = AccessPolicy.HasAnywhere([grant], op.Permission!);
                string path = PathWith(op, p => NotTenantResources.ContainsKey(p) ? new string('x', 40) : _a.Id(p).ToString());
                HttpResponseMessage r = await Send(As(_roleSubjects[role], _a.Tenant), op, path, null);
                bool forbidden = r.StatusCode == HttpStatusCode.Forbidden && await Code(r) == "forbidden";
                if (allowed == forbidden)
                {
                    problems.Add($"{op.OperationId} as {role}: expected {(allowed ? "not forbidden" : "403 forbidden")}, got {(int)r.StatusCode} {await Code(r)}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [DbFact]
    public async Task SEC_T_010_another_stores_ids_are_not_found_on_every_operation()
    {
        var problems = new List<string>();
        HttpClient owner = As(_roleSubjects[$"owner-{_a.Tenant}"], _a.Tenant);
        foreach (Operation op in Implemented().Where(o => o.PathParams.Any(p => !NotTenantResources.ContainsKey(p))))
        {
            if (op.BodyProps is not null && !ValidBodies.ContainsKey(op.OperationId))
            {
                problems.Add($"{op.OperationId}: has a path id and a body; add a valid body to GeneratedSecurityTests.ValidBodies");
                continue;
            }

            foreach (string param in op.PathParams.Where(p => !NotTenantResources.ContainsKey(p)))
            {
                // only this parameter points into store B; the rest stay valid ids of store A
                string path = PathWith(op, p => (p == param ? _b : _a).Id(p).ToString());
                HttpResponseMessage r = await Send(owner, op, path, ValidBodies.TryGetValue(op.OperationId, out var body) ? body(_a) : null);
                if (r.StatusCode != HttpStatusCode.NotFound)
                {
                    problems.Add($"{op.OperationId} with store B's {param}: expected 404, got {(int)r.StatusCode} {await Code(r)}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [DbFact]
    public async Task SEC_T_011_a_branch_manager_cannot_reach_another_branch()
    {
        // BranchManager of B1 in store A, aimed at branch B2 and its terminal (same store, other branch)
        var problems = new List<string>();
        HttpClient manager = As(await BranchScopedManager(), _a.Tenant);
        Fixture otherBranch = _a with { Branch = _a.Branch2, Terminal = _a.Terminal2 };
        foreach (Operation op in Implemented().Where(o => o.Scope == "Branch" && o.PathParams.Any(p => p is "branchId" or "terminalId")))
        {
            string path = PathWith(op, p => (p is "branchId" or "terminalId" ? otherBranch : _a).Id(p).ToString());
            HttpResponseMessage r = await Send(manager, op, path, ValidBodies.TryGetValue(op.OperationId, out var body) ? body(otherBranch) : null);
            if (r.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.NotFound))
            {
                problems.Add($"{op.OperationId} on another branch: expected 403/404, got {(int)r.StatusCode} {await Code(r)}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [DbFact]
    public async Task SEC_T_013_fields_a_command_does_not_define_are_refused_by_name()
    {
        string[] forbidden = ["tenantId", "status", "id", "createdBy", "version"];
        var problems = new List<string>();
        HttpClient owner = As(_roleSubjects[$"owner-{_a.Tenant}"], _a.Tenant);
        foreach (Operation op in Implemented().Where(o => o.BodyProps is not null))
        {
            string field = forbidden.First(f => !op.BodyProps!.Contains(f, StringComparer.Ordinal));
            string path = PathWith(op, p => NotTenantResources.ContainsKey(p) ? new string('x', 40) : _a.Id(p).ToString());
            HttpResponseMessage r = await Send(owner, op, path, new Dictionary<string, object> { [field] = Guid.NewGuid() });
            string text = await r.Content.ReadAsStringAsync();
            if (r.StatusCode != HttpStatusCode.BadRequest || !text.Contains($"\"{field}\":[\"unknown-field\"]", StringComparison.Ordinal))
            {
                problems.Add($"{op.OperationId} with '{field}': expected 400 unknown-field, got {(int)r.StatusCode} {text[..Math.Min(text.Length, 160)]}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
