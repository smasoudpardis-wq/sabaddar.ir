using Npgsql;
using RetailCloud.BuildingBlocks.Api;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using RetailCloud.IdentityAccess.Application;
using RetailCloud.IdentityAccess.Infrastructure;
using RetailCloud.Platform.Application;
using RetailCloud.Platform.Infrastructure;
using RetailCloud.Merchandising.Application;
using RetailCloud.Merchandising.Infrastructure;
using RetailCloud.Sales.Application;
using RetailCloud.Sales.Infrastructure;
using RetailCloud.Accounting.Application;
using RetailCloud.Accounting.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("Main")
                          ?? throw new InvalidOperationException("ConnectionStrings:Main is required.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<IUnitOfWork, PostgresUnitOfWork>();
builder.Services.AddSingleton<IUserDirectory, PostgresUserDirectory>();
builder.Services.AddSingleton<ITenantProfileQuery, PostgresTenantProfileQuery>();
builder.Services.AddSingleton<ITenantReadOnlyQuery, PostgresTenantReadOnlyQuery>();
builder.Services.AddSingleton<IExportJobs, PostgresExportJobs>();
builder.Services.AddSingleton<ISupportAccess, PostgresSupportAccess>();
builder.Services.AddSingleton<IProductCatalog, PostgresProductCatalog>();
builder.Services.AddSingleton<IShifts, PostgresShifts>();
builder.Services.AddSingleton<IJournals, PostgresJournals>();
ObjectStorageOptions? storage = builder.Configuration.GetSection("Storage").Get<ObjectStorageOptions>();
builder.Services.AddSingleton<IObjectStorage>(storage is { ServiceUrl.Length: > 0 } ? new S3ObjectStorage(storage) : new UnconfiguredObjectStorage());
builder.Services.AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>();
builder.Services.AddSingleton<IOutbox>(_ => new PostgresOutbox());
builder.Services.AddSingleton<ITenantRegistration, PostgresTenantRegistration>();
builder.Services.AddSingleton<ITenantSetupStep, BranchAndRegisterSetupStep>();
builder.Services.AddSingleton<ITenantSetupStep, RetailCloud.Inventory.Infrastructure.WarehouseSetupStep>();
builder.Services.AddSingleton<ITenantSetupStep, RetailCloud.Accounting.Infrastructure.ChartOfAccountsSetupStep>();
builder.Services.AddSingleton<ITenantSetupStep, RetailCloud.Accounting.Infrastructure.FiscalPeriodsSetupStep>();
builder.Services.AddSingleton<IUserAdministration, PostgresUserAdministration>();
builder.Services.AddSingleton<IBranchAdministration, PostgresBranchAdministration>();
builder.Services.AddSingleton<IApprovals, PostgresApprovals>();
builder.Services.AddSingleton<IPinStore, PostgresPinStore>();
builder.Services.AddSingleton<PostgresAuditLog>();
builder.Services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<PostgresAuditLog>());
builder.Services.AddSingleton<IAuditSearch>(sp => sp.GetRequiredService<PostgresAuditLog>());
builder.Services.AddSingleton<PostgresTerminals>();
builder.Services.AddSingleton<ITerminalAdministration>(sp => sp.GetRequiredService<PostgresTerminals>());
builder.Services.AddSingleton<ITerminalDirectory>(sp => sp.GetRequiredService<PostgresTerminals>());
builder.Services.AddSingleton<IScopeLookup, BranchScopeLookup>();
builder.Services.AddSingleton<IScopeLookup, RegisterScopeLookup>();
builder.Services.AddSingleton<IScopeLookup, RetailCloud.Inventory.Infrastructure.WarehouseScopeLookup>();
builder.Services.AddRetailCloudApi(builder.Configuration);

var app = builder.Build();
app.UseRetailCloudApi();

// ---------------- AUD-001..003 (US-031): who/where from the validated request; written inside each operation's transaction.
// The client IP comes from X-Forwarded-For set by the BFF (the API is not reachable from the Internet, D04 §3.3).
// AUD-001 "where": X-Forwarded-For is honoured only from a configured proxy (the BFF / ingress, Network:TrustedProxies);
// from anyone else it would let a caller write an arbitrary IP into the tamper-evident trail.
string[] trustedProxies = (builder.Configuration["Network:TrustedProxies"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

string? ClientIp(HttpContext http)
{
    string? peer = http.Connection.RemoteIpAddress?.ToString();
    bool trusted = trustedProxies.Contains("*") || (peer is not null && trustedProxies.Contains(peer));
    return trusted && http.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim() is { Length: > 0 } fwd ? fwd : peer;
}

AuditActor ActorOf(RequestContext ctx, HttpContext http, Guid? userOverride = null, IReadOnlyList<string>? rolesOverride = null) =>
    new(userOverride ?? ctx.User?.Value, "User", rolesOverride ?? ctx.ActiveMembership?.Roles ?? [], ctx.Terminal?.BranchId, ctx.Terminal?.TerminalId, ClientIp(http));

static Task Audit(HttpContext http, IUnitOfWorkScope s, AuditActor actor, AuditEntry entry, CancellationToken c) =>
    http.RequestServices.GetRequiredService<IAuditLog>().WriteAsync(s, entry, actor, c);

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous(); // R7: explicit anonymous

// Default Deny (IAM-006): every endpoint below declares RequirePermission / RequireTenantMember / AllowAnyAuthenticated,
// equal to its x-permission in D07 (EndpointPolicyTests); one that does not is refused by DefaultDenyFilter.
RouteGroupBuilder v1 = app.MapGroup("/api/v1").RequireAuthorization(ServiceCollectionExtensions.MerchantPolicy)
    .AddEndpointFilter<DefaultDenyFilter>()
    .AddEndpointFilter<ReadOnlyTenantFilter>();   // TEN-004: a read-only tenant writes only x-allowed-when-read-only

// getMe (D07). Effective permissions of the active tenant (US-025); MFA state from the token (US-022).
v1.MapGet("/me", (RequestContext ctx) => Results.Ok(new
{
    userId = ctx.User!.Value.Value,
    displayName = ctx.DisplayName,
    activeTenantId = ctx.ActiveTenant?.Value,
    memberships = ctx.Memberships.Select(m => new
    {
        tenantId = m.TenantId.Value, tenantName = m.TenantName, roles = m.Roles, mfaRequired = MfaPolicy.Requires(m),
    }),
    permissions = (ctx.ActiveMembership?.Assignments ?? []).SelectMany(a => RoleCatalog.PermissionsOf(a.Role).Select(p => new
    {
        permission = p.Key,
        scopeType = (p.Value == GrantReach.Tenant ? ScopeType.Tenant : a.ScopeType).ToString(),
        scopeId = p.Value == GrantReach.Tenant ? null : a.ScopeId,
    })).Distinct(),
    mfaSatisfied = ctx.MfaSatisfied,
    terminal = ctx.Terminal is { } t ? new { id = t.TerminalId, branchId = t.BranchId } : null,
    mfaFreshUntil = ctx.MfaSatisfied ? ctx.AuthTime + MfaPolicy.SensitiveFreshness : null,
})).AllowAnyAuthenticated();

// selectActiveTenant (D07): the BFF calls this before storing the choice in its session; 404 unless the user is a member,
// 403 mfa-required when the user's role there needs a second factor the token does not carry (IAM-003).
v1.MapPost("/me/active-tenant", async (SelectTenant body, RequestContext ctx, IUnitOfWork uow, HttpContext http, CancellationToken ct) =>
    {
        MembershipInfo? m = ctx.Memberships.FirstOrDefault(m => m.TenantId.Value == body.TenantId);
        if (m is null)
        {
            return Problems.NotFound(http);
        }

        if (MfaPolicy.Requires(m) && !ctx.MfaSatisfied)
        {
            return MfaPolicy.MfaRequired(http);
        }

        await uow.ExecuteAsync(new FixedTenantContext(m.TenantId, ctx.User), async (s, c) =>
        {
            await Audit(http, s, ActorOf(ctx, http, rolesOverride: m.Roles), new AuditEntry("iam.tenant.selected", "Tenant", m.TenantId.Value.ToString(),
                Details: new { acr = ctx.Acr }), c);
            return 0;
        }, ct);
        return Results.NoContent();
    }).AllowAnyAuthenticated()
    .AllowWhenReadOnly();

// getMyTenant (D07): tenant-scoped, read through RLS.
v1.MapGet("/tenants/me", async (RequestContext ctx, ITenantProfileQuery query, HttpContext http, CancellationToken ct) =>
        await query.GetAsync(ctx.ActiveTenant!.Value, ct) is { } t ? Results.Ok(t) : Problems.NotFound(http))
    .RequireTenantMember();

// registerTenant (D07, UC-10, TEN-001): callable before the user exists; requires MFA (the owner is a TenantOwner,
// IAM-003). Idempotent by construction: the tenant id is derived from (issuer, subject, Idempotency-Key).
v1.MapIdempotentCreateTenant<RegisterTenant>("/tenants", "platform.register-tenant", async (cmd, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        if (!ctx.MfaSatisfied)
        {
            return CommandResult.Fail(MfaPolicy.MfaRequired(http));
        }

        Dictionary<string, string[]> errors = TenantRegistrationRules.Validate(cmd);
        if (errors.Count > 0)
        {
            return CommandResult.Fail(Problems.Validation(http, errors));
        }

        var owner = await http.RequestServices.GetRequiredService<IUserDirectory>()
            .EnsureUserAsync(ctx.Issuer!, ctx.Subject!, ctx.TokenName ?? cmd.LegalName, ct);
        if (owner is null)
        {
            return CommandResult.Fail(Problems.Create(http, StatusCodes.Status403Forbidden, "user-not-provisioned", "This account is disabled"));
        }

        TenantProfile profile = await http.RequestServices.GetRequiredService<ITenantRegistration>().RegisterAsync(scope, cmd, owner.Value.Id, ct);
        await Audit(http, scope, ActorOf(ctx, http, owner.Value.Id.Value, ["TenantOwner"]),
            new AuditEntry("platform.tenant.registered", "Tenant", profile.Id.ToString(), Details: new { industry = cmd.IndustryCode, pricingMode = profile.PricingMode }), ct);
        return CommandResult.Created(profile);
    })
    .WithMetadata(new AllowUnprovisionedUserAttribute())
    .AllowAnyAuthenticated();

// getTenantSetup (D07): onboarding wizard progress, composed from each module's own step (TEN-001.1).
v1.MapGet("/tenants/me/setup", async (RequestContext ctx, IUnitOfWork uow, IEnumerable<ITenantSetupStep> steps, CancellationToken ct) =>
    {
        var result = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var list = new List<object>();
            bool all = true;
            foreach (ITenantSetupStep step in steps)
            {
                bool done = await step.IsCompleteAsync(s, c);
                all &= done;
                list.Add(new { name = step.Name, complete = done });
            }

            return new { complete = all, steps = list };
        }, ct);
        return Results.Ok(result);
    })
    .RequireTenantMember();

// ---------------- US-025: users, roles and invitations (IAM-001, IAM-006, D06 §6)
async Task<ScopeRef?> ResolveScope(IUnitOfWorkScope s, IEnumerable<IScopeLookup> lookups, ScopeType type, Guid? id, CancellationToken ct)
{
    if (type == ScopeType.Tenant)
    {
        return id is null ? ScopeRef.Tenant : null;
    }

    IScopeLookup? lookup = lookups.FirstOrDefault(l => l.Type == type);
    return id is { } g && lookup is not null && await lookup.BranchOfAsync(s, g, ct) is { } branch ? new ScopeRef(type, g, branch) : null;
}

IResult Forbidden(HttpContext http) => Problems.Create(http, StatusCodes.Status403Forbidden, "forbidden", "Your role does not allow this operation");

// listBranches (D07, TEN-002): the branches the caller's assignments reach (all of them for tenant-wide roles).
v1.MapGet("/branches", async (RequestContext ctx, IUnitOfWork uow, IEnumerable<IScopeLookup> lookups, CancellationToken ct) =>
        Results.Ok(await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            await using var cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT id, code, name, status FROM platform.branch ORDER BY code");
            await using var r = await cmd.ExecuteReaderAsync(c);
            var all = new List<(Guid Id, string Code, string Name, string Status)>();
            while (await r.ReadAsync(c))
            {
                all.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            }

            await r.DisposeAsync();
            var reached = new List<object>();
            foreach (var b in all)
            {
                var scope = new ScopeRef(ScopeType.Branch, b.Id, b.Id);
                if (ctx.ActiveMembership!.Assignments.Any(a => AccessPolicy.Covers(a, scope)))
                {
                    reached.Add(new { id = b.Id, code = b.Code, name = b.Name, status = b.Status });
                }
            }

            return reached;
        }, ct)))
    .RequireTenantMember();

// createBranch / createRegister (TEN-002, US-012): codes unique per tenant / per branch; the branch gets register R1
// now and its warehouse from platform.branch-created (Inventory, via Workers).
v1.MapPost("/branches", async (BranchCreate body, RequestContext ctx, IUnitOfWork uow, IBranchAdministration branches, HttpContext http, CancellationToken ct) =>
    {
        Dictionary<string, string[]> errors = BranchRules.Validate(body.Code, body.Name ?? "");
        if (errors.Count > 0)
        {
            return Problems.Validation(http, errors);
        }

        try
        {
            BranchView b = await uow.ExecuteAsync(ctx, async (s, c) =>
            {
                BranchView created = await branches.CreateBranchAsync(s, body.Code!, body.Name!, c);
                await Audit(http, s, ActorOf(ctx, http), new AuditEntry("platform.branch.created", "Branch", created.Id.ToString(), Details: new { created.Code }), c);
                return created;
            }, ct);
            return Results.Created($"/api/v1/branches/{b.Id}", b);
        }
        catch (DuplicateCodeException)
        {
            return Problems.Create(http, StatusCodes.Status409Conflict, "duplicate-code", "This code is already used");
        }
    })
    .RequirePermission(Permissions.TenantSettingsManage);

v1.MapPost("/branches/{branchId:guid}/registers", async (Guid branchId, RegisterCreate body, RequestContext ctx, IUnitOfWork uow,
        IBranchAdministration branches, HttpContext http, CancellationToken ct) =>
    {
        Dictionary<string, string[]> errors = BranchRules.Validate(body.Code, null);
        if (errors.Count > 0)
        {
            return Problems.Validation(http, errors);
        }

        try
        {
            RegisterView? r = await uow.ExecuteAsync(ctx, async (s, c) =>
            {
                RegisterView? created = await branches.CreateRegisterAsync(s, branchId, body.Code!, c);
                if (created is not null)
                {
                    await Audit(http, s, ActorOf(ctx, http), new AuditEntry("platform.register.created", "Register", created.Id.ToString(), Details: new { branchId, created.Code }), c);
                }

                return created;
            }, ct);
            return r is null ? Problems.NotFound(http) : Results.Created($"/api/v1/branches/{branchId}/registers/{r.Id}", r);
        }
        catch (DuplicateCodeException)
        {
            return Problems.Create(http, StatusCodes.Status409Conflict, "duplicate-code", "This code is already used");
        }
    })
    .RequirePermission(Permissions.TenantSettingsManage);

// ---------------- US-013: POS terminals (TEN-002.1, ABAC-04)
v1.MapPost("/branches/{branchId:guid}/pairing-codes", async (Guid branchId, RequestContext ctx, IUnitOfWork uow, ITerminalAdministration terminals,
        HttpContext http, CancellationToken ct) =>
    {
        if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.TerminalsManage, new ScopeRef(ScopeType.Branch, branchId, branchId)))
        {
            return Forbidden(http);
        }

        PairingCodeView? code = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            PairingCodeView? issued = await terminals.IssueCodeAsync(s, branchId, ctx.User!.Value, c);
            if (issued is not null)
            {
                // the code itself is a credential: never audited (AUD-003)
                await Audit(http, s, ActorOf(ctx, http), new AuditEntry("platform.pairing-code.issued", "Branch", branchId.ToString(), Details: new { issued.ExpiresAt }), c);
            }

            return issued;
        }, ct);
        return code is null ? Problems.NotFound(http) : Results.Created($"/api/v1/branches/{branchId}/pairing-codes", code);
    })
    .RequirePermission(Permissions.TerminalsManage);

v1.MapPost("/terminals/pair", async (PairTerminal body, RequestContext ctx, IUnitOfWork uow, ITerminalAdministration terminals,
        HttpContext http, CancellationToken ct) =>
    {
        string? code = PairingCodes.Normalize(body.PairingCode);
        if (string.IsNullOrWhiteSpace(body.DeviceName) || body.DeviceName.Trim().Length > 100)
        {
            return Problems.Validation(http, new Dictionary<string, string[]> { ["deviceName"] = ["length-1-100"] });
        }

        TerminalView? terminal = code is null ? null
            : await uow.ExecuteAsync(ctx, async (s, c) =>
            {
                TerminalView? paired = await terminals.PairAsync(s, code, body.DeviceName, ctx.User!.Value, c);
                if (paired is not null)
                {
                    await Audit(http, s, ActorOf(ctx, http), new AuditEntry("platform.terminal.paired", "Terminal", paired.Id.ToString(),
                        Details: new { paired.BranchId, paired.DeviceName }), c);
                }

                return paired;
            }, ct);
        return terminal is null ? Problems.NotFound(http) : Results.Created($"/api/v1/terminals/{terminal.Id}", terminal);
    })
    .RequireTenantMember()
    .RequireRateLimiting(RateLimits.Pairing);

v1.MapGet("/terminals", async (RequestContext ctx, IUnitOfWork uow, ITerminalAdministration terminals, CancellationToken ct) =>
        Results.Ok((await uow.ExecuteAsync(ctx, (s, c) => terminals.ListAsync(s, c), ct))
            .Where(t => AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.TerminalsManage, new ScopeRef(ScopeType.Branch, t.BranchId, t.BranchId)))))
    .RequirePermission(Permissions.TerminalsManage);

v1.MapDelete("/terminals/{terminalId:guid}", async (Guid terminalId, RequestContext ctx, IUnitOfWork uow, ITerminalAdministration terminals,
        HttpContext http, CancellationToken ct) =>
        await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            TerminalView? t = await terminals.GetAsync(s, terminalId, c);
            if (t is null || !AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.TerminalsManage, new ScopeRef(ScopeType.Branch, t.BranchId, t.BranchId)))
            {
                return Problems.NotFound(http); // outside the caller's branches looks like "does not exist"
            }

            await terminals.RevokeAsync(s, terminalId, ctx.User!.Value, c);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("platform.terminal.revoked", "Terminal", terminalId.ToString(), Details: new { t.BranchId }), c);
            return Results.NoContent();
        }, ct))
    .RequirePermission(Permissions.TerminalsManage)
    .AllowWhenReadOnly();

// ---------------- US-026: on-the-spot approvals (IAM-007, D08 F-14, SoD-01, D-61)
object ApprovalJson(ApprovalView a) => new
{
    id = a.Id, action = a.Action, resourceType = a.ResourceType, resourceId = a.ResourceId, reason = a.Reason,
    requestedBy = a.RequestedBy, status = a.Status, expiresAt = a.ExpiresAt, decidedBy = a.DecidedBy, decidedAt = a.DecidedAt,
    validUntil = a.ValidUntil, context = new { condition = a.Condition, value = a.Value, limit = a.Limit },
};

bool Involved(RequestContext ctx, ApprovalView a) =>
    a.RequestedBy == ctx.User!.Value.Value || AccessPolicy.CanApprove(ctx.ActiveMembership!.Assignments, a.Action, a.Scope);

// listApprovals: what I may approve (not my own) plus my own open requests.
v1.MapGet("/approvals", async (RequestContext ctx, IUnitOfWork uow, IApprovals approvals, CancellationToken ct) =>
        Results.Ok((await uow.ExecuteAsync(ctx, (s, c) => approvals.ListOpenAsync(s, c), ct)).Where(a => Involved(ctx, a)).Select(ApprovalJson)))
    .RequireTenantMember();

v1.MapGet("/approvals/{approvalId:guid}", async (Guid approvalId, RequestContext ctx, IUnitOfWork uow, IApprovals approvals, HttpContext http, CancellationToken ct) =>
        await uow.ExecuteAsync(ctx, (s, c) => approvals.GetAsync(s, approvalId, c), ct) is { } a && Involved(ctx, a)
            ? Results.Ok(ApprovalJson(a))
            : Problems.NotFound(http))
    .RequireTenantMember();

// listAudit (US-032, AUD-001.1): the tenant's trail, newest first, keyset-paged on seq. The cursor is opaque to clients.
v1.MapGet("/audit", async (HttpContext http, RequestContext ctx, IUnitOfWork uow, IAuditSearch audit, DateTimeOffset? from, DateTimeOffset? to,
        string? action, Guid? actorId, string? resourceType, string? resourceId, string? cursor, int? limit, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        long? before = null;
        if (cursor is not null)
        {
            if (AuditCursor.TryDecode(cursor, out long seq)) before = seq;
            else errors["cursor"] = ["Invalid cursor"];
        }

        int size = limit ?? 50;
        if (size is < 1 or > 200) errors["limit"] = ["Must be 1-200"];
        if (from is not null && to is not null && from >= to) errors["to"] = ["Must be after 'from'"];
        foreach ((string name, string? value) in new[] { ("action", action), ("resourceType", resourceType), ("resourceId", resourceId) })
        {
            if (value is { Length: 0 or > 100 }) errors[name] = ["1-100 characters"];
        }

        if (errors.Count > 0) return Problems.Validation(http, errors);
        IReadOnlyList<AuditRecordView> rows = await uow.ExecuteAsync(ctx, (s, c) =>
            audit.SearchAsync(s, new AuditQuery(from, to, action, actorId, resourceType, resourceId, before, size + 1), c), ct);
        bool hasMore = rows.Count > size;
        AuditRecordView[] items = [.. rows.Take(size)];
        return Results.Ok(new
        {
            items = items.Select(r => new
            {
                r.Seq, r.OccurredAt, r.ActorKind, r.ActorId, r.ActorRoles, r.ImpersonatedBy, r.Action, r.ResourceType, r.ResourceId, r.Reason,
                r.BranchId, r.TerminalId, r.ClientIp, details = System.Text.Json.JsonElement.Parse(r.DetailsJson),
            }),
            page = new { nextCursor = hasMore ? AuditCursor.Encode(items[^1].Seq) : null, hasMore },
        });
    })
    .RequirePermission(Permissions.AuditView);

// Accounting (US-111, ACC-001..004/006/009): chart, role mappings, periods and journal entries. Reads need
// accounting.read; writes accounting.journal.post. Balance, immutability and open periods are enforced by the database too.
static string Rial(long v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
object JournalJson(JournalView j) => new
{
    j.Id, j.Status, j.EffectiveAt, j.Description,
    source = j.SourceType is null ? null : new { type = j.SourceType, id = j.SourceId, purpose = j.Purpose, ruleVersion = j.RuleVersion },
    j.ReversalOf, j.ReversedBy,
    lines = j.Lines.Select(l => new { l.LineNo, l.AccountId, l.AccountCode, l.DetailType, l.DetailId, debit = Rial(l.Debit), credit = Rial(l.Credit), l.Description }),
};

IResult AccountingProblem(HttpContext http, AccountingOutcome outcome, string? detail) => outcome switch
{
    AccountingOutcome.NotFound => Problems.NotFound(http),
    AccountingOutcome.NoOpenPeriod => Problems.Create(http, StatusCodes.Status422UnprocessableEntity, "no-open-period", "No fiscal period covers this date"),
    AccountingOutcome.PeriodClosed => Problems.Create(http, StatusCodes.Status409Conflict, "period-closed", "The fiscal period is closed"),
    AccountingOutcome.NotDraft => Problems.Create(http, StatusCodes.Status409Conflict, "journal-not-draft", "Only a draft entry can be posted"),
    AccountingOutcome.AlreadyReversed => Problems.Create(http, StatusCodes.Status409Conflict, "journal-already-reversed", "This entry is already reversed"),
    AccountingOutcome.DuplicateCode => Problems.Create(http, StatusCodes.Status409Conflict, "duplicate-code", "This account code is already used"),
    AccountingOutcome.UnknownRole => Problems.Validation(http, new Dictionary<string, string[]> { ["mappings"] = [$"unknown role: {detail}"] }),
    _ => Problems.Validation(http, new Dictionary<string, string[]> { ["lines"] = [detail ?? "unknown account"] }),
};

v1.MapGet("/accounting/accounts", async (RequestContext ctx, IUnitOfWork uow, IJournals journals, CancellationToken ct) =>
        Results.Ok(await uow.ExecuteAsync(ctx, (s, c) => journals.AccountsAsync(s, c), ct)))
    .RequirePermission(Permissions.AccountingRead);

v1.MapPost("/accounting/accounts", async (AccountCreateInput body, RequestContext ctx, IUnitOfWork uow, IJournals journals, HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        if (body.Code is not { Length: >= 1 and <= 12 } || !body.Code.All(char.IsAsciiDigit)) errors["code"] = ["1-12 digits"];
        ProductRules.Name(errors, "name", body.Name);
        if (body.AccountType is not ("Asset" or "Liability" or "Equity" or "Revenue" or "Expense")) errors["accountType"] = ["Asset|Liability|Equity|Revenue|Expense"];
        if (errors.Count > 0) return Problems.Validation(http, errors);
        var r = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var created = await journals.CreateAccountAsync(s, body.Code!, body.Name!, body.AccountType!, body.ParentId, body.RequiresDetail ?? false, c);
            if (created.Outcome == AccountingOutcome.Done)
            {
                await Audit(http, s, ActorOf(ctx, http), new AuditEntry("accounting.account.created", "Account", created.Value!.Id.ToString(),
                    Details: new { body.Code, body.AccountType }), c);
            }

            return created;
        }, ct);
        return r.Outcome == AccountingOutcome.Done
            ? Results.Created($"/api/v1/accounting/accounts", r.Value)
            : r.Outcome == AccountingOutcome.UnknownAccount
                ? Problems.Validation(http, new Dictionary<string, string[]> { ["parentId"] = [r.Detail!] })
                : AccountingProblem(http, r.Outcome, r.Detail);
    })
    .RequirePermission(Permissions.AccountingJournalPost);

v1.MapPut("/accounting/account-mappings", async (AccountMappingInput body, RequestContext ctx, IUnitOfWork uow, IJournals journals, HttpContext http,
        CancellationToken ct) =>
    {
        if (body.Mappings is not { Length: >= 1 and <= 50 } || body.Mappings.Any(m => m.Role is null || m.AccountId is null)
            || body.Mappings.Select(m => m.Role).Distinct(StringComparer.Ordinal).Count() != body.Mappings.Length)
        {
            return Problems.Validation(http, new Dictionary<string, string[]> { ["mappings"] = ["1-50 distinct {role, accountId}"] });
        }

        if (body.Mappings.Any(m => m.BranchId is not null))
        {
            return Problems.Validation(http, new Dictionary<string, string[]> { ["mappings.branchId"] = ["not-yet-supported"] });   // per-branch mapping later
        }

        var r = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var set = await journals.SetMappingsAsync(s, body.Mappings.ToDictionary(m => m.Role!, m => m.AccountId!.Value, StringComparer.Ordinal), c);
            if (set.Outcome == AccountingOutcome.Done)
            {
                await Audit(http, s, ActorOf(ctx, http), new AuditEntry("accounting.mappings.set", "AccountMapping", null,
                    Details: new { roles = body.Mappings.Select(m => m.Role) }), c);
            }

            return set;
        }, ct);
        return r.Outcome switch
        {
            AccountingOutcome.Done => Results.NoContent(),
            AccountingOutcome.UnknownAccount => Problems.Validation(http, new Dictionary<string, string[]> { ["mappings"] = [$"{r.Detail}: not a postable account of this store"] }),
            _ => AccountingProblem(http, r.Outcome, r.Detail),
        };
    })
    .RequirePermission(Permissions.AccountingJournalPost);

v1.MapGet("/accounting/periods", async (RequestContext ctx, IUnitOfWork uow, IJournals journals, CancellationToken ct) =>
        Results.Ok((await uow.ExecuteAsync(ctx, (s, c) => journals.PeriodsAsync(s, c), ct))
            .Select(p => new { p.Id, p.FiscalYear, p.PeriodNo, start = p.Start, end = p.End, p.Status })))
    .RequirePermission(Permissions.AccountingRead);

v1.MapGet("/accounting/journals", async (RequestContext ctx, IUnitOfWork uow, IJournals journals, HttpContext http, DateTimeOffset? from, DateTimeOffset? to,
        string? sourceType, string? cursor, int? limit, CancellationToken ct) =>
    {
        int size = limit ?? 50;
        (DateTimeOffset, Guid)? before = null;
        if (cursor is not null)
        {
            string[] parts = cursor.Split('_');
            if (parts.Length != 2 || !long.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long ticks)
                || !Guid.TryParse(parts[1], out Guid lastId))
            {
                return Problems.Validation(http, new Dictionary<string, string[]> { ["cursor"] = ["Invalid cursor"] });
            }

            before = (new DateTimeOffset(ticks, TimeSpan.Zero), lastId);
        }

        if (size is < 1 or > 200 || sourceType is { Length: > 50 } || (from is not null && to is not null && from >= to))
        {
            return Problems.Validation(http, new Dictionary<string, string[]> { ["query"] = ["limit 1-200, from < to"] });
        }

        IReadOnlyList<JournalView> rows = await uow.ExecuteAsync(ctx, (s, c) => journals.ListAsync(s, from, to, sourceType, before, size + 1, c), ct);
        bool more = rows.Count > size;
        JournalView[] page = [.. rows.Take(size)];
        string? next = more ? $"{page[^1].EffectiveAt.UtcTicks}_{page[^1].Id}" : null;
        return Results.Ok(new { items = page.Select(JournalJson), page = new { nextCursor = next, hasMore = more } });
    })
    .RequirePermission(Permissions.AccountingRead);

v1.MapIdempotentPost<JournalCreateInput>("/accounting/journals", "accounting.create-journal", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        var errors = new Dictionary<string, string[]>();
        if (body.EffectiveAt is null) errors["effectiveAt"] = ["required"];
        if (body.Description is not { Length: >= 1 and <= 500 }) errors["description"] = ["1-500 characters"];
        var lines = new List<JournalLineSpec>();
        for (int i = 0; i < (body.Lines?.Length ?? 0); i++)
        {
            JournalLineInput l = body.Lines![i];
            long debit = 0, credit = 0;
            if (l.AccountId is null || (l.Debit is not null && !TryMoney(l.Debit, true, out debit)) || (l.Credit is not null && !TryMoney(l.Credit, true, out credit))
                || l.DetailType is not (null or "Customer" or "Supplier" or "Branch") || l.Description is { Length: > 200 })
            {
                errors[$"lines[{i}]"] = ["accountId, and a positive debit or credit (rial string)"];
                continue;
            }

            lines.Add(new JournalLineSpec(l.AccountId.Value, debit, credit, l.DetailType, l.DetailId, l.Description));
        }

        if (errors.Count == 0 && JournalRules.Problem(lines) is { } problem) errors["lines"] = [problem];   // ACC-002 domain check
        if (errors.Count > 0) return CommandResult.Fail(Problems.Validation(http, errors));
        var r = await http.RequestServices.GetRequiredService<IJournals>().CreateDraftAsync(scope, body.EffectiveAt!.Value, body.Description, lines, ct);
        if (r.Outcome != AccountingOutcome.Done) return CommandResult.Fail(AccountingProblem(http, r.Outcome, r.Detail));
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("accounting.journal.drafted", "Journal", r.Value!.Id.ToString(),
            Details: new { lines = lines.Count, total = lines.Sum(l => l.Debit) }), ct);
        return CommandResult.Created(JournalJson(r.Value));
    })
    .RequirePermission(Permissions.AccountingJournalPost);

v1.MapIdempotentPost<JournalPostInput>("/accounting/journals/{journalId:guid}/post", "accounting.post-journal", async (_, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        Guid journalId = Guid.Parse((string)http.GetRouteValue("journalId")!, System.Globalization.CultureInfo.InvariantCulture);
        var r = await http.RequestServices.GetRequiredService<IJournals>().PostAsync(scope, journalId, ct);
        if (r.Outcome != AccountingOutcome.Done) return CommandResult.Fail(AccountingProblem(http, r.Outcome, r.Detail));
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("accounting.journal.posted", "Journal", journalId.ToString()), ct);
        return CommandResult.Ok(JournalJson(r.Value!));
    })
    .RequirePermission(Permissions.AccountingJournalPost);

v1.MapIdempotentPost<JournalReverseInput>("/accounting/journals/{journalId:guid}/reverse", "accounting.reverse-journal", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        Guid journalId = Guid.Parse((string)http.GetRouteValue("journalId")!, System.Globalization.CultureInfo.InvariantCulture);
        if (body.Reason is not { Length: >= 3 and <= 200 })
        {
            return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { ["reason"] = ["3-200 characters"] }));
        }

        var r = await http.RequestServices.GetRequiredService<IJournals>().ReverseAsync(scope, journalId, body.Reason, ct);
        if (r.Outcome != AccountingOutcome.Done) return CommandResult.Fail(AccountingProblem(http, r.Outcome, r.Detail));
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("accounting.journal.reversed", "Journal", journalId.ToString(), body.Reason,
            new { reversal = r.Value!.Id }), ct);
        return CommandResult.Created(JournalJson(r.Value));
    })
    .RequirePermission(Permissions.AccountingJournalPost);

// Shifts (US-081, UC-06, SAL-009): open, cash in/out, blind close, variance approval. POS operations run on a registered
// terminal of the register's branch (ABAC-04); a shift is visible to its cashier and to supervisors of its branch only.
// The variance needing approval is a tenant setting later; until then Sales:VarianceApprovalThreshold (rial) [ASM].
long varianceThreshold = builder.Configuration.GetValue<long?>("Sales:VarianceApprovalThreshold") ?? 500_000;

object ShiftJson(ShiftView v) => new
{
    v.Id, v.RegisterId, v.BranchId, v.CashierId, v.Status,
    openingCash = v.OpeningCash.ToString(System.Globalization.CultureInfo.InvariantCulture),
    expectedCash = v.ExpectedCash?.ToString(System.Globalization.CultureInfo.InvariantCulture),   // null until counted (blind close, D-24)
    countedCash = v.CountedCash?.ToString(System.Globalization.CultureInfo.InvariantCulture),
    variance = v.Variance?.ToString(System.Globalization.CultureInfo.InvariantCulture),
    v.VarianceStatus, v.VarianceReason, v.OpenedAt, v.ClosedAt, v.Version,
};

static bool TryMoney(string? text, bool positive, out long value) =>
    long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value)
    && value <= 1_000_000_000_000_000 && (!positive || value > 0);

bool SeesShift(RequestContext ctx, ShiftView shift) =>
    shift.CashierId == ctx.User!.Value.Value
    || AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.ShiftSupervise, new ScopeRef(ScopeType.Branch, shift.BranchId, shift.BranchId));

IResult? OnTerminalOf(HttpContext http, RequestContext ctx, Guid branchId) =>
    ctx.Terminal is { } t && t.BranchId == branchId
        ? null
        : Problems.Create(http, StatusCodes.Status403Forbidden, "terminal-not-registered", "Use a registered device of this branch (ABAC-04)");

IResult ShiftProblem(HttpContext http, ShiftOutcome outcome) => outcome switch
{
    ShiftOutcome.NotFound => Problems.NotFound(http),
    ShiftOutcome.AlreadyOpen => Problems.Create(http, StatusCodes.Status409Conflict, "shift-already-open", "This register already has an open shift"),
    ShiftOutcome.NotOpen => Problems.Create(http, StatusCodes.Status409Conflict, "shift-not-open", "The shift is not open"),
    ShiftOutcome.VersionConflict => Problems.Create(http, StatusCodes.Status409Conflict, "version-conflict", "The shift changed; reload and retry"),
    ShiftOutcome.SelfApproval => Problems.Create(http, StatusCodes.Status403Forbidden, "self-approval", "Not for your own shift (SoD-01)"),
    _ => Problems.Create(http, StatusCodes.Status409Conflict, "variance-not-pending", "This shift has no variance waiting for approval"),
};

v1.MapIdempotentPost<ShiftOpenInput>("/shifts", "sales.open-shift", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        var errors = new Dictionary<string, string[]>();
        if (body.RegisterId is null) errors["registerId"] = ["required"];
        if (!TryMoney(body.OpeningCash, positive: false, out long opening)) errors["openingCash"] = ["rial amount as a whole-number string"];
        if (errors.Count > 0) return CommandResult.Fail(Problems.Validation(http, errors));

        ScopeRef? register = await ResolveScope(scope, http.RequestServices.GetServices<IScopeLookup>(), ScopeType.Register, body.RegisterId, ct);
        if (register is null) return CommandResult.Fail(Problems.NotFound(http));
        if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.ShiftManage, register)) return CommandResult.Fail(Forbidden(http));
        if (OnTerminalOf(http, ctx, register.BranchId!.Value) is { } device) return CommandResult.Fail(device);

        ShiftResult<ShiftView> opened = await http.RequestServices.GetRequiredService<IShifts>().OpenAsync(scope, body.RegisterId!.Value, ctx.User!.Value.Value, opening, ct);
        if (opened.Outcome != ShiftOutcome.Done) return CommandResult.Fail(ShiftProblem(http, opened.Outcome));
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("sales.shift.opened", "Shift", opened.Value!.Id.ToString(),
            Details: new { body.RegisterId, openingCash = opening }), ct);
        return CommandResult.Created(ShiftJson(opened.Value));
    })
    .RequirePermission(Permissions.ShiftManage);

v1.MapGet("/shifts/{shiftId:guid}", async (Guid shiftId, RequestContext ctx, IUnitOfWork uow, IShifts shifts, HttpContext http, CancellationToken ct) =>
        await uow.ExecuteAsync(ctx, (s, c) => shifts.GetAsync(s, shiftId, c), ct) is { } shift && SeesShift(ctx, shift)
            ? Results.Ok(ShiftJson(shift))
            : Problems.NotFound(http))
    .RequirePermission(Permissions.ShiftManage);

v1.MapIdempotentPost<CashMovementInput>("/shifts/{shiftId:guid}/cash-movements", "sales.cash-movement", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        Guid shiftId = Guid.Parse((string)http.GetRouteValue("shiftId")!, System.Globalization.CultureInfo.InvariantCulture);
        var errors = new Dictionary<string, string[]>();
        if (body.Direction is not ("In" or "Out")) errors["direction"] = ["In|Out"];
        if (!TryMoney(body.Amount, positive: true, out long amount)) errors["amount"] = ["positive rial amount as a string"];
        if (body.Reason is not { Length: >= 3 and <= 200 }) errors["reason"] = ["3-200 characters"];
        if (errors.Count > 0) return CommandResult.Fail(Problems.Validation(http, errors));

        IShifts shifts = http.RequestServices.GetRequiredService<IShifts>();
        if (await shifts.GetAsync(scope, shiftId, ct) is not { } shift || !SeesShift(ctx, shift)) return CommandResult.Fail(Problems.NotFound(http));
        if (OnTerminalOf(http, ctx, shift.BranchId) is { } device) return CommandResult.Fail(device);
        ShiftResult<CashMovementView> moved = await shifts.RecordCashAsync(scope, shiftId, body.Direction == "In", amount, body.Reason!, ctx.User!.Value.Value, ct);
        if (moved.Outcome != ShiftOutcome.Done) return CommandResult.Fail(ShiftProblem(http, moved.Outcome));
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry(body.Direction == "In" ? "sales.cash.in" : "sales.cash.out", "Shift", shiftId.ToString(),
            body.Reason, new { amount }), ct);
        return CommandResult.Created(new
        {
            moved.Value!.Id, moved.Value.Direction, amount = amount.ToString(System.Globalization.CultureInfo.InvariantCulture), moved.Value.Reason, moved.Value.RecordedAt,
        });
    })
    .RequirePermission(Permissions.ShiftManage);

v1.MapIdempotentPost<ShiftCloseInput>("/shifts/{shiftId:guid}/close", "sales.close-shift", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        Guid shiftId = Guid.Parse((string)http.GetRouteValue("shiftId")!, System.Globalization.CultureInfo.InvariantCulture);
        var errors = new Dictionary<string, string[]>();
        if (!TryMoney(body.CountedCash, positive: false, out long counted)) errors["countedCash"] = ["rial amount as a whole-number string"];
        if (body.ExpectedVersion is null) errors["expectedVersion"] = ["required"];
        if (body.VarianceReason is { Length: > 500 }) errors["varianceReason"] = ["up to 500 characters"];
        if (body.Denominations is { } notes)
        {
            long sum = 0;
            foreach (DenominationInput d in notes)
            {
                if (!TryMoney(d.Value, positive: true, out long note) || d.Count is not (>= 0 and <= 100_000)) { errors["denominations"] = ["value: positive amount, count: 0-100000"]; break; }
                sum += note * d.Count!.Value;
            }

            if (!errors.ContainsKey("denominations") && errors.Count == 0 && sum != counted) errors["denominations"] = ["must add up to countedCash"];
        }

        if (errors.Count > 0) return CommandResult.Fail(Problems.Validation(http, errors));
        IShifts shifts = http.RequestServices.GetRequiredService<IShifts>();
        if (await shifts.GetAsync(scope, shiftId, ct) is not { } shift || !SeesShift(ctx, shift)) return CommandResult.Fail(Problems.NotFound(http));
        if (OnTerminalOf(http, ctx, shift.BranchId) is { } device) return CommandResult.Fail(device);
        ShiftResult<ShiftView> closed = await shifts.CloseAsync(scope, shiftId, body.ExpectedVersion!.Value, counted, body.VarianceReason, varianceThreshold,
            ctx.User!.Value.Value, ct);
        if (closed.Outcome != ShiftOutcome.Done) return CommandResult.Fail(ShiftProblem(http, closed.Outcome));
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("sales.shift.closed", "Shift", shiftId.ToString(), body.VarianceReason,
            new { counted, closed.Value!.ExpectedCash, closed.Value.Variance, closed.Value.VarianceStatus }), ct);
        return CommandResult.Ok(ShiftJson(closed.Value));
    })
    .RequirePermission(Permissions.ShiftManage);

v1.MapPost("/shifts/{shiftId:guid}/variance-approval", async (Guid shiftId, ApprovalDecisionInput body, RequestContext ctx, IUnitOfWork uow, IShifts shifts,
        HttpContext http, CancellationToken ct) =>
    {
        if (body.Decision is not ("Approve" or "Reject") || body.Pin is not null || body.ApproverId is not null || body.Comment is { Length: > 500 })
        {
            return Problems.Validation(http, new Dictionary<string, string[]> { ["decision"] = ["Approve|Reject"] });
        }

        (ShiftOutcome outcome, ShiftView? view) = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            if (await shifts.GetAsync(s, shiftId, c) is not { } shift
                || !AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.ShiftSupervise, new ScopeRef(ScopeType.Branch, shift.BranchId, shift.BranchId)))
            {
                return (ShiftOutcome.NotFound, (ShiftView?)null);
            }

            ShiftResult<ShiftView> r = await shifts.DecideVarianceAsync(s, shiftId, body.Decision == "Approve", ctx.User!.Value.Value, c);
            if (r.Outcome == ShiftOutcome.Done)
            {
                await Audit(http, s, ActorOf(ctx, http), new AuditEntry(body.Decision == "Approve" ? "sales.shift.variance-approved" : "sales.shift.variance-rejected",
                    "Shift", shiftId.ToString(), body.Comment, new { r.Value!.Variance }), c);
            }

            return (r.Outcome, r.Value);
        }, ct);
        return outcome == ShiftOutcome.Done ? Results.Ok(ShiftJson(view!)) : ShiftProblem(http, outcome);
    })
    .RequirePermission(Permissions.ShiftSupervise)
    .RequireRecentMfa();

// Catalog (US-041, CAT-001/002/002.1/003): products, variants and barcodes. Reads for every member; writes need
// catalog.product.manage. Price lists, opening stock, categories and the variant matrix arrive with their own stories
// and are refused explicitly (not-yet-supported) instead of being ignored.
object ProductJson(ProductView p) => new
{
    p.Id, p.Name, p.Brand, baseUnit = p.BaseUnit, p.TaxCode, taxRate = p.TaxRate, p.Status, p.Version,
    variants = p.Variants.Select(VariantJson),
};
object VariantJson(VariantView v) => new { v.Id, v.Sku, axisValues = v.AxisValues, v.Status, barcodes = v.Barcodes, unitPrice = (string?)null };

IResult CatalogProblem(HttpContext http, CatalogOutcome outcome, Guid? conflictProduct) => outcome switch
{
    CatalogOutcome.NotFound => Problems.NotFound(http),
    CatalogOutcome.VersionConflict => Problems.Create(http, StatusCodes.Status409Conflict, "version-conflict", "The product changed; reload and retry"),
    CatalogOutcome.DuplicateSku => Problems.Create(http, StatusCodes.Status409Conflict, "duplicate-code", "This SKU is already used"),
    CatalogOutcome.DuplicateAxes => Problems.Create(http, StatusCodes.Status409Conflict, "duplicate-code",
        "Another variant of this product has the same axis values (DI-081)"),
    _ => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This barcode already belongs to a product",
        type: "https://errors.retailcloud.example/barcode-already-assigned",
        extensions: new Dictionary<string, object?>
        {
            ["code"] = "barcode-already-assigned", ["context"] = new { productId = conflictProduct }, ["traceId"] = http.TraceIdentifier,
        }),
};

// a barcode from input, or why not: (spec, field-error) — invalid GS1 codes are 422 invalid-barcode (CAT-003)
(BarcodeSpec? Spec, string? Error, bool Invalid) ParseBarcode(BarcodeInput? b)
{
    if (b is null || string.IsNullOrWhiteSpace(b.Code)) return (null, "code required", false);
    string unit = b.PackagingUnit ?? "EA";
    if (!ProductRules.ValidUnit(unit)) return (null, "packagingUnit: 1-10 of A-Z 0-9 (EA, PK, CT)", false);
    if (!ProductRules.TryFactor(b.UnitFactor, out decimal factor)) return (null, "unitFactor: positive decimal, up to 6 places", false);
    bool freeInternal = b.IsInternal ?? false;
    return BarcodeSpec.From(b.Code, unit, factor, freeInternal) is { } spec
        ? (spec, null, false)
        : (null, freeInternal ? "internal code: 1-32 of letters, digits, -" : "not a valid EAN-8, UPC-A, EAN-13 or GTIN-14", !freeInternal);
}

(VariantSpec? Spec, Dictionary<string, string[]> Errors, bool Invalid) ParseVariant(VariantInput? v, string at)
{
    var errors = new Dictionary<string, string[]>();
    bool invalid = false;
    if (v is null || !ProductRules.ValidSku(v.Sku)) errors[$"{at}.sku"] = ["1-64 of letters, digits, . _ / -"];
    if (v?.UnitPrice is not null) errors[$"{at}.unitPrice"] = ["not-yet-supported"];   // price lists (US-043)
    var axes = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach ((string axis, string? value) in v?.AxisValues ?? [])
    {
        if (!ProductRules.Axes.Contains(axis) || value is not { Length: >= 1 and <= 50 }) errors[$"{at}.axisValues"] = ["color|size|model, 1-50 characters"];
        else axes[axis] = value.Trim();
    }

    var barcodes = new List<BarcodeSpec>();
    for (int i = 0; i < (v?.Barcodes?.Length ?? 0); i++)
    {
        (BarcodeSpec? b, string? error, bool bad) = ParseBarcode(v!.Barcodes![i]);
        if (b is null) { errors[$"{at}.barcodes[{i}]"] = [error!]; invalid |= bad; }
        else barcodes.Add(b);
    }

    if (barcodes.Select(b => b.Normalized).Distinct(StringComparer.Ordinal).Count() != barcodes.Count) errors[$"{at}.barcodes"] = ["duplicate barcode"];
    return (errors.Count == 0 ? new VariantSpec(v!.Sku!, axes, barcodes) : null, errors, invalid);
}

IResult InputProblem(HttpContext http, Dictionary<string, string[]> errors, bool invalidBarcode) =>
    invalidBarcode
        ? Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Invalid barcode", type: "https://errors.retailcloud.example/invalid-barcode",
            extensions: new Dictionary<string, object?> { ["code"] = "invalid-barcode", ["errors"] = errors, ["traceId"] = http.TraceIdentifier })
        : Problems.Validation(http, errors);

v1.MapGet("/products", async (RequestContext ctx, IUnitOfWork uow, IProductCatalog catalog, HttpContext http, string? q, string? barcode,
        string? cursor, int? limit, Guid? branchId, CancellationToken ct) =>
    {
        int size = limit ?? 50;
        if (size is < 1 or > 200 || q is { Length: > 100 } || barcode is { Length: > 32 })
        {
            return Problems.Validation(http, new Dictionary<string, string[]> { ["query"] = ["limit 1-200, q up to 100, barcode up to 32"] });
        }

        var (items, next) = await uow.ExecuteAsync(ctx, (s, c) => catalog.SearchAsync(s, q, barcode, cursor, size, c), ct);
        return Results.Ok(new { items = items.Select(ProductJson), page = new { nextCursor = next, hasMore = next is not null } });
    })
    .RequireTenantMember();

v1.MapGet("/products/{productId:guid}", async (Guid productId, RequestContext ctx, IUnitOfWork uow, IProductCatalog catalog, HttpContext http,
        CancellationToken ct) =>
        await uow.ExecuteAsync(ctx, (s, c) => catalog.GetAsync(s, productId, c), ct) is { } p ? Results.Ok(ProductJson(p)) : Problems.NotFound(http))
    .RequireTenantMember();

v1.MapIdempotentPost<ProductCreate>("/products", "catalog.create-product", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        var errors = new Dictionary<string, string[]>();
        bool invalidBarcode = false;
        ProductRules.Name(errors, "name", body.Name);
        if (!ProductRules.ValidUnit(body.BaseUnit)) errors["baseUnit"] = ["1-10 of A-Z 0-9 (EA, KG, L)"];
        if (body.Brand is { Length: > 100 }) errors["brand"] = ["up to 100 characters"];
        if (body.TaxRate is < 0 or > 10000) errors["taxRate"] = ["0-10000 basis points"];
        if (body.TaxCode is { Length: > 32 }) errors["taxCode"] = ["up to 32 characters"];
        if (body.CategoryId is not null) errors["categoryId"] = ["not-yet-supported"];
        if (body.CatalogProductId is not null) errors["catalogProductId"] = ["not-yet-supported"];   // global catalog (US-044)
        if (body.OpeningStock is not null) errors["openingStock"] = ["not-yet-supported"];           // inventory ledger (US-051)
        if (body.Axes is { Length: > 0 }) errors["axes"] = ["not-yet-supported"];                     // variant matrix (US-046)
        if (body.Confirmation?.Confirmed != true) errors["confirmation.confirmed"] = ["required: true (CAT-004.1)"];
        var variants = new List<VariantSpec>();
        if (body.Variants is not { Length: >= 1 and <= 100 }) errors["variants"] = ["1-100 variants"];
        for (int i = 0; i < (body.Variants?.Length ?? 0); i++)
        {
            var (spec, verrors, bad) = ParseVariant(body.Variants![i], $"variants[{i}]");
            foreach (var e in verrors) errors[e.Key] = e.Value;
            invalidBarcode |= bad;
            if (spec is not null) variants.Add(spec);
        }

        if (variants.Select(v => v.Sku).Distinct(StringComparer.Ordinal).Count() != variants.Count) errors["variants"] = ["duplicate sku"];
        if (variants.Select(v => string.Join("|", v.AxisValues.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key + "=" + a.Value)))
                .Distinct(StringComparer.Ordinal).Count() != variants.Count)
        {
            errors["variants"] = ["variants must differ by axisValues (DI-081)"];
        }
        if (variants.SelectMany(v => v.Barcodes).Select(b => b.Normalized).Distinct(StringComparer.Ordinal).Count() != variants.Sum(v => v.Barcodes.Count))
        {
            errors["variants"] = ["the same barcode twice"];
        }

        if (errors.Count > 0) return CommandResult.Fail(InputProblem(http, errors, invalidBarcode && errors.Values.All(v => v[0] != "required: true (CAT-004.1)")));
        var result = await http.RequestServices.GetRequiredService<IProductCatalog>().CreateAsync(scope,
            new ProductSpec(body.Name!, body.Brand, body.BaseUnit!, body.TaxCode, body.TaxRate ?? 0, variants), ct);
        if (result.Outcome != CatalogOutcome.Done) return CommandResult.Fail(CatalogProblem(http, result.Outcome, result.ConflictProductId));
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("catalog.product.created", "Product", result.Value!.Id.ToString(),
            Details: new { result.Value.Name, variants = variants.Count, barcodes = variants.Sum(v => v.Barcodes.Count) }), ct);
        return CommandResult.Created(ProductJson(result.Value));
    })
    .RequirePermission(Permissions.CatalogProductManage);

v1.MapPatch("/products/{productId:guid}", async (Guid productId, ProductUpdate body, RequestContext ctx, IUnitOfWork uow, IProductCatalog catalog,
        HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        if (body.ExpectedVersion is null) errors["expectedVersion"] = ["required"];
        if (body.Name is not null) ProductRules.Name(errors, "name", body.Name);
        if (body.Brand is { Length: > 100 }) errors["brand"] = ["up to 100 characters"];
        if (body.TaxRate is < 0 or > 10000) errors["taxRate"] = ["0-10000 basis points"];
        if (body.TaxCode is { Length: > 32 }) errors["taxCode"] = ["up to 32 characters"];
        if (body.Status is not (null or "Active" or "Inactive")) errors["status"] = ["Active|Inactive"];
        if (errors.Count > 0) return Problems.Validation(http, errors);
        var result = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var r = await catalog.UpdateAsync(s, productId, new ProductChange(body.ExpectedVersion!.Value, body.Name, body.Brand, body.TaxCode, body.TaxRate, body.Status), c);
            if (r.Outcome == CatalogOutcome.Done)
            {
                await Audit(http, s, ActorOf(ctx, http), new AuditEntry("catalog.product.updated", "Product", productId.ToString(),
                    Details: new { body.Name, body.Brand, body.TaxCode, body.TaxRate, body.Status }), c);
            }

            return r;
        }, ct);
        return result.Outcome == CatalogOutcome.Done ? Results.Ok(ProductJson(result.Value!)) : CatalogProblem(http, result.Outcome, null);
    })
    .RequirePermission(Permissions.CatalogProductManage);

v1.MapPost("/products/{productId:guid}/variants", async (Guid productId, VariantInput body, RequestContext ctx, IUnitOfWork uow, IProductCatalog catalog,
        HttpContext http, CancellationToken ct) =>
    {
        var (spec, errors, invalid) = ParseVariant(body, "variant");
        if (spec is null) return InputProblem(http, errors, invalid);
        var result = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var r = await catalog.AddVariantAsync(s, productId, spec, c);
            if (r.Outcome == CatalogOutcome.Done)
            {
                await Audit(http, s, ActorOf(ctx, http), new AuditEntry("catalog.variant.added", "Product", productId.ToString(),
                    Details: new { spec.Sku, barcodes = spec.Barcodes.Count }), c);
            }

            return r;
        }, ct);
        return result.Outcome == CatalogOutcome.Done
            ? Results.Created($"/api/v1/products/{productId}", VariantJson(result.Value!))
            : CatalogProblem(http, result.Outcome, result.ConflictProductId);
    })
    .RequirePermission(Permissions.CatalogProductManage);

v1.MapPost("/products/{productId:guid}/variants/{variantId:guid}/barcodes", async (Guid productId, Guid variantId, BarcodeInput body,
        RequestContext ctx, IUnitOfWork uow, IProductCatalog catalog, HttpContext http, CancellationToken ct) =>
    {
        var (spec, error, invalid) = ParseBarcode(body);
        if (spec is null) return InputProblem(http, new Dictionary<string, string[]> { ["code"] = [error!] }, invalid);
        var result = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var r = await catalog.AddBarcodeAsync(s, productId, variantId, spec, c);
            if (r.Outcome == CatalogOutcome.Done)
            {
                await Audit(http, s, ActorOf(ctx, http), new AuditEntry("catalog.barcode.added", "Product", productId.ToString(),
                    Details: new { variantId, code = spec.Normalized, spec.PackagingUnit }), c);
            }

            return r;
        }, ct);
        return result.Outcome == CatalogOutcome.Done
            ? Results.Created($"/api/v1/products/{productId}", result.Value)
            : CatalogProblem(http, result.Outcome, result.ConflictProductId);
    })
    .RequirePermission(Permissions.CatalogProductManage);

v1.MapDelete("/products/{productId:guid}/variants/{variantId:guid}/barcodes/{barcodeId:guid}", async (Guid productId, Guid variantId, Guid barcodeId,
        RequestContext ctx, IUnitOfWork uow, IProductCatalog catalog, HttpContext http, CancellationToken ct) =>
    {
        CatalogOutcome outcome = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            CatalogOutcome o = await catalog.RemoveBarcodeAsync(s, productId, variantId, barcodeId, c);
            if (o == CatalogOutcome.Done)
            {
                await Audit(http, s, ActorOf(ctx, http), new AuditEntry("catalog.barcode.removed", "Product", productId.ToString(),
                    Details: new { variantId, barcodeId }), c);
            }

            return o;
        }, ct);
        return outcome == CatalogOutcome.Done ? Results.NoContent() : CatalogProblem(http, outcome, null);
    })
    .RequirePermission(Permissions.CatalogProductManage);

// Support JIT access (US-027, IAM-008, SoD-05): the store owner sees, approves, rejects and ends platform support
// access. Deciding and ending need TenantOwner (not just tenant.settings.manage) and a fresh second factor.
bool IsOwner(RequestContext ctx) => ctx.ActiveMembership!.Assignments.Any(a => a.Role == "TenantOwner");

IResult SupportOutcome(HttpContext http, SupportAccessOutcome outcome, SupportAccessView? view) => outcome switch
{
    SupportAccessOutcome.Done => Results.Ok(view),
    SupportAccessOutcome.NotFound => Problems.NotFound(http),
    _ => Problems.Create(http, StatusCodes.Status409Conflict, "support-access-not-open", "This support request is no longer open"),
};

v1.MapGet("/support-access-requests", async (RequestContext ctx, IUnitOfWork uow, ISupportAccess support, CancellationToken ct) =>
        Results.Ok(await uow.ExecuteAsync(ctx, (s, c) => support.ListAsync(s, c), ct)))
    .RequirePermission(Permissions.TenantSettingsManage);

v1.MapPost("/support-access-requests/{requestId:guid}/decision", async (Guid requestId, ApprovalDecisionInput body, RequestContext ctx,
        IUnitOfWork uow, ISupportAccess support, HttpContext http, CancellationToken ct) =>
    {
        if (body.Decision is not ("Approve" or "Reject") || body.Pin is not null || body.ApproverId is not null || body.Comment is { Length: > 500 })
        {
            return Problems.Validation(http, new Dictionary<string, string[]> { ["decision"] = ["Approve|Reject (no on-device approval)"] });
        }

        if (!IsOwner(ctx))
        {
            return Problems.Create(http, StatusCodes.Status403Forbidden, "owner-only", "Only the store owner decides support access (SoD-05)");
        }

        (SupportAccessOutcome outcome, SupportAccessView? view) = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var result = await support.DecideAsync(s, requestId, body.Decision == "Approve", ctx.User!.Value.Value, c);
            if (result.Outcome == SupportAccessOutcome.Done)
            {
                await Audit(http, s, ActorOf(ctx, http), new AuditEntry(body.Decision == "Approve" ? "iam.support-access.approved" : "iam.support-access.rejected",
                    "SupportAccess", requestId.ToString(), body.Comment, new { result.View!.RequestedBy, result.View.DurationMinutes, result.View.GrantedUntil }), c);
            }

            return result;
        }, ct);
        return SupportOutcome(http, outcome, view);
    })
    .RequirePermission(Permissions.TenantSettingsManage)
    .RequireRecentMfa()
    .AllowWhenReadOnly();

v1.MapDelete("/support-access-requests/{requestId:guid}", async (Guid requestId, RequestContext ctx, IUnitOfWork uow, ISupportAccess support,
        HttpContext http, CancellationToken ct) =>
    {
        if (!IsOwner(ctx))
        {
            return Problems.Create(http, StatusCodes.Status403Forbidden, "owner-only", "Only the store owner ends support access (SoD-05)");
        }

        (SupportAccessOutcome outcome, SupportAccessView? view) = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var result = await support.RevokeAsync(s, requestId, ctx.User!.Value.Value, c);
            if (result.Outcome == SupportAccessOutcome.Done)
            {
                await Audit(http, s, ActorOf(ctx, http), new AuditEntry("iam.support-access.revoked", "SupportAccess", requestId.ToString(),
                    Details: new { result.View!.RequestedBy }), c);
            }

            return result;
        }, ct);
        return SupportOutcome(http, outcome, view);
    })
    .RequirePermission(Permissions.TenantSettingsManage)
    .RequireRecentMfa()
    .AllowWhenReadOnly();

// createExport / getExport (US-016, TEN-005): asynchronous export of the tenant's data; the file is only reachable
// through a presigned link issued here after authorization (5 minutes, D-49), and every request and link is audited.
object ExportJson(ExportJobView j, Uri? link) => new
{
    j.Id,
    status = j.Status == "Completed" && j.ExpiresAt <= DateTimeOffset.UtcNow ? "Expired" : j.Status,
    j.Categories,
    from = j.From,
    to = j.To,
    j.RowCount,
    j.CreatedAt,
    j.CompletedAt,
    j.ExpiresAt,
    downloadUrl = link?.ToString(),
    downloadUrlExpiresAt = link is null ? (DateTimeOffset?)null : DateTimeOffset.UtcNow + ObjectKeys.LinkLifetime,
};

v1.MapPost("/exports", async (ExportCreate body, RequestContext ctx, IUnitOfWork uow, IExportJobs exports, HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        string[] categories = body.Categories ?? [];
        if (categories.Length == 0 || categories.Length != categories.Distinct(StringComparer.Ordinal).Count()
            || categories.Any(c => !ExportCategories.All.Contains(c)))
        {
            errors["categories"] = ["1-7 distinct of: " + string.Join(", ", ExportCategories.All)];
        }

        if (body.From is not null && body.To is not null && body.From >= body.To)
        {
            errors["to"] = ["Must be after 'from'"];
        }

        if (body.IncludeFullPhone == true)
        {
            // full numbers need customers.pii.view (TEN-005); decrypting them arrives with the customer identity module
            if (!AccessPolicy.HasAnywhere(ctx.ActiveMembership!.Assignments, Permissions.CustomersPiiView))
            {
                return Problems.Create(http, StatusCodes.Status403Forbidden, "forbidden", "Full phone numbers need customers.pii.view");
            }

            errors["includeFullPhone"] = ["not-yet-supported"];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(http, errors);
        }

        ExportJobView? job = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            if (await exports.HasActiveAsync(s, c))
            {
                return null;
            }

            ExportJobView created = await exports.CreateAsync(s, ctx.User!.Value.Value, new ExportRequest(categories, body.From, body.To), c);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("platform.export.requested", "Export", created.Id.ToString(),
                Details: new { categories, body.From, body.To }), c);
            return created;
        }, ct);
        return job is null
            ? Problems.Create(http, StatusCodes.Status409Conflict, "export-in-progress", "An export of this store is already running")
            : Results.Accepted($"/api/v1/exports/{job.Id}", ExportJson(job, null));
    })
    .RequirePermission(Permissions.DataExport)
    .RequireRecentMfa()
    .AllowWhenReadOnly();

v1.MapGet("/exports/{exportId:guid}", async (Guid exportId, RequestContext ctx, IUnitOfWork uow, IExportJobs exports, IObjectStorage storage,
        HttpContext http, CancellationToken ct) =>
    {
        (ExportJobView? job, Uri? link) = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            ExportJobView? j = await exports.GetAsync(s, exportId, c);
            if (j is not { Status: "Completed", ObjectKey: { } key } || j.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return (j, (Uri?)null);
            }

            Uri url = storage.PresignGet(key, ObjectKeys.LinkLifetime, $"retailcloud-export-{j.CreatedAt:yyyyMMdd}.zip");
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("platform.export.link-issued", "Export", j.Id.ToString()), c);
            return (j, url);
        }, ct);
        return job is null ? Problems.NotFound(http) : Results.Ok(ExportJson(job, link));
    })
    .RequirePermission(Permissions.DataExport)
    .RequireRecentMfa();

IResult PinProblem(HttpContext http, PinCheck result, int attemptsLeft) => result switch
{
    PinCheck.Locked => Problems.Create(http, StatusCodes.Status403Forbidden, "pin-locked", "PIN locked after 5 failed attempts; sign in again"),
    PinCheck.NotSet => Problems.Create(http, StatusCodes.Status409Conflict, "pin-not-set", "No PIN has been set"),
    _ => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Wrong PIN", type: "https://errors.retailcloud.example/pin-invalid",
        extensions: new Dictionary<string, object?> { ["code"] = "pin-invalid", ["attemptsLeft"] = attemptsLeft, ["traceId"] = http.TraceIdentifier }),
};

// decideApproval (D08 F-14), two ways:
//  - remote: the signed-in approver, with a second factor no older than 15 minutes (ABAC-05);
//  - on the same device: approverId + the approver's PIN, only from a registered terminal of the request's branch.
// Always: approver ≠ requester (SoD-01, also a DB CHECK) and an uncapped permission over the request scope.
v1.MapPost("/approvals/{approvalId:guid}/decision", async (Guid approvalId, ApprovalDecisionInput body, RequestContext ctx, IUnitOfWork uow,
        IApprovals approvals, IUserDirectory directory, IPinStore pins, HttpContext http, CancellationToken ct) =>
    {
        if (body.Decision is not ("Approve" or "Reject") || body.Comment is { Length: > 500 })
        {
            return Problems.Validation(http, new Dictionary<string, string[]> { ["decision"] = ["Approve|Reject"] });
        }

        bool onDevice = body.Pin is not null || body.ApproverId is not null;
        IReadOnlyList<RoleGrant> approverGrants = ctx.ActiveMembership!.Assignments;
        UserId approver = ctx.User!.Value;
        if (onDevice)
        {
            if (body.Pin is null || body.ApproverId is not { } approverId)
            {
                return Problems.Validation(http, new Dictionary<string, string[]> { ["pin"] = ["pin-and-approverId-together"] });
            }

            if (ctx.Terminal is null)
            {
                return Problems.Create(http, StatusCodes.Status403Forbidden, "terminal-not-registered", "On-device approval needs a registered terminal");
            }

            approver = new UserId(approverId);
            approverGrants = (await directory.ActiveMembershipsAsync(approver, ct)).FirstOrDefault(m => m.TenantId == ctx.ActiveTenant)?.Assignments ?? [];
        }
        else
        {
            DateTimeOffset now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            bool fresh = ctx.MfaSatisfied && ctx.AuthTime is { } at && now - at <= MfaPolicy.SensitiveFreshness && at <= now.AddMinutes(1);
            if (!fresh)
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Re-authenticate with your second factor",
                    type: "https://errors.retailcloud.example/step-up-required",
                    extensions: new Dictionary<string, object?> { ["code"] = "step-up-required", ["maxAgeSeconds"] = (int)MfaPolicy.SensitiveFreshness.TotalSeconds, ["traceId"] = http.TraceIdentifier });
            }
        }

        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            ApprovalView? a = await approvals.GetAsync(s, approvalId, c);
            if (a is null || !Involved(ctx, a))
            {
                return Problems.NotFound(http);
            }

            if (a.RequestedBy == approver.Value)
            {
                return Problems.Create(http, StatusCodes.Status403Forbidden, "self-approval", "You cannot approve your own request");
            }

            var deviceBranch = ctx.Terminal is { } t ? new ScopeRef(ScopeType.Branch, t.BranchId, t.BranchId) : null;
            if (!AccessPolicy.CanApprove(approverGrants, a.Action, a.Scope)
                || (onDevice && (deviceBranch is null || !AccessPolicy.CanApprove(approverGrants, a.Action, deviceBranch))))
            {
                return Forbidden(http);
            }

            if (onDevice)
            {
                (PinCheck check, int left) = await pins.VerifyAsync(s, approver, body.Pin!, c);
                if (check != PinCheck.Ok)
                {
                    await Audit(http, s, ActorOf(ctx, http), new AuditEntry(check == PinCheck.Locked ? "iam.pin.locked" : "iam.pin.failed", "User",
                        approver.Value.ToString(), Details: new { purpose = "approval", approvalId }), c);
                    return PinProblem(http, check, left); // the failure count and its audit record commit with this transaction
                }
            }

            DecisionOutcome outcome = await approvals.DecideAsync(s, approvalId, approver, body.Decision == "Approve", body.Comment, c);
            if (outcome == DecisionOutcome.Decided)
            {
                // AUD-001 / IAM-007: approver, requester, reason and time
                await Audit(http, s, ActorOf(ctx, http, approver.Value, onDevice ? approverGrants.Select(g => g.Role).Distinct().ToList() : null),
                    new AuditEntry(body.Decision == "Approve" ? "iam.approval.approved" : "iam.approval.rejected", "ApprovalRequest", approvalId.ToString(), a.Reason,
                        new { a.Action, a.ResourceType, a.ResourceId, a.RequestedBy, a.Value, a.Limit, onDevice, comment = body.Comment }), c);
            }

            return outcome switch
            {
                DecisionOutcome.Decided => Results.Ok(ApprovalJson((await approvals.GetAsync(s, approvalId, c))!)),
                DecisionOutcome.Expired => Problems.Create(http, StatusCodes.Status409Conflict, "approval-expired", "The request has expired"),
                DecisionOutcome.NotPending => Problems.Create(http, StatusCodes.Status409Conflict, "approval-not-pending", "The request was already decided"),
                DecisionOutcome.SelfApproval => Problems.Create(http, StatusCodes.Status403Forbidden, "self-approval", "You cannot approve your own request"),
                _ => Problems.NotFound(http),
            };
        }, ct);
    })
    .RequireTenantMember();

// ---------------- US-023: register PIN (IAM-004, AUTH-08)
// setMyPin: only within 10 minutes of a full sign-in (the PIN replaces it for quick unlocks afterwards).
v1.MapPut("/me/pin", async (SetPin body, RequestContext ctx, IUnitOfWork uow, IPinStore pins, HttpContext http, CancellationToken ct) =>
    {
        DateTimeOffset now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        if (ctx.AuthTime is not { } at || now - at > TimeSpan.FromMinutes(10))
        {
            return Problems.Create(http, StatusCodes.Status403Forbidden, "reauth-required", "Sign in again to set a PIN");
        }

        if (!PinRules.IsValid(body.Pin))
        {
            return Problems.Validation(http, new Dictionary<string, string[]> { ["pin"] = ["4-6-digits"] });
        }

        await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            await pins.SetAsync(s, ctx.User!.Value, body.Pin!, c);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("iam.pin.set", "User", ctx.User!.Value.Value.ToString()), c);
            return 0;
        }, ct);
        return Results.NoContent();
    })
    .RequireTenantMember();

// verifyMyPin: unlock a locked register; registered terminals only; 5 failures lock the PIN.
v1.MapPost("/me/pin/verify", async (SetPin body, RequestContext ctx, IUnitOfWork uow, IPinStore pins, HttpContext http, CancellationToken ct) =>
    {
        (PinCheck check, int left) = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            (PinCheck result, int attemptsLeft) = await pins.VerifyAsync(s, ctx.User!.Value, body.Pin ?? "", c);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry(result switch
            {
                PinCheck.Ok => "iam.register.unlocked",
                PinCheck.Locked => "iam.pin.locked",
                _ => "iam.pin.failed",
            }, "User", ctx.User!.Value.Value.ToString(), Details: new { attemptsLeft }), c);
            return (result, attemptsLeft);
        }, ct);
        return check == PinCheck.Ok ? Results.NoContent() : PinProblem(http, check, left);
    })
    .RequireTenantMember()
    .AddEndpointFilter<RequireTerminalFilter>()
    .AllowWhenReadOnly();

// listRoles: the system roles with their stable ids and permissions (custom roles: later story).
v1.MapGet("/roles", () => Results.Ok(RoleCatalog.Roles.Select(r => new
    {
        id = RoleCatalog.IdOf(r), name = r, isSystem = true, permissions = RoleCatalog.PermissionsOf(r).Keys.Order(),
    })))
    .RequirePermission(Permissions.UsersManage);

// listUsers: a branch-scoped manager only sees users who have a role inside the branches they manage.
v1.MapGet("/users", async (RequestContext ctx, IUnitOfWork uow, IUserAdministration users, IEnumerable<IScopeLookup> lookups, CancellationToken ct) =>
        Results.Ok(await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var visible = new List<UserView>();
            foreach (UserView u in await users.ListAsync(s, c))
            {
                foreach (AssignmentView a in u.RoleAssignments)
                {
                    ScopeRef? scope = await ResolveScope(s, lookups, Enum.Parse<ScopeType>(a.ScopeType), a.ScopeId, c);
                    if (scope is not null && AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.UsersManage, scope))
                    {
                        visible.Add(u);
                        break;
                    }
                }
            }

            return visible;
        }, ct)))
    .RequirePermission(Permissions.UsersManage);

// inviteUser: every requested role must be assignable by the inviter at that scope (anti-escalation, D06 §6).
v1.MapPost("/users/invitations", async (InviteUser body, RequestContext ctx, IUnitOfWork uow, IUserAdministration users,
        IEnumerable<IScopeLookup> lookups, HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(body.DisplayName) || body.DisplayName.Trim().Length > 100)
        {
            errors["displayName"] = ["length-1-100"];
        }

        if (body.RoleAssignments is not { Count: >= 1 and <= 20 })
        {
            errors["roleAssignments"] = ["count-1-20"];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(http, errors);
        }

        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var grants = new List<RoleGrant>();
            foreach (RoleAssignmentInput input in body.RoleAssignments!)
            {
                string? role = RoleCatalog.FromId(input.RoleId);
                ScopeRef? scope = role is null ? null : await ResolveScope(s, lookups, input.ScopeType, input.ScopeId, c);
                if (role is null || scope is null)
                {
                    return Problems.Validation(http, new Dictionary<string, string[]> { ["roleAssignments"] = ["unknown-role-or-scope"] });
                }

                if (!AccessPolicy.CanAssign(ctx.ActiveMembership!.Assignments, role, scope))
                {
                    return Forbidden(http);
                }

                grants.Add(new RoleGrant(role, input.ScopeType, input.ScopeId));
            }

            InvitationView invitation = await users.InviteAsync(s, ctx.User!.Value, body.DisplayName!.Trim(), body.Email, body.Mobile, grants, c);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("iam.invitation.created", "Invitation", invitation.Id.ToString(),
                Details: new { roles = grants }), c); // no name, contact or token (AUD-003)
            return Results.Created($"/api/v1/users/invitations/{invitation.Id}", invitation);
        }, ct);
    })
    .RequirePermission(Permissions.UsersManage);

// acceptInvitation: any identity holding the link; provisions the user on first use (single use, 7 days).
v1.MapPost("/invitations/{token}/accept", async (string token, RequestContext ctx, IUnitOfWork uow, IUserAdministration users,
        IUserDirectory directory, IMemoryCache cache, HttpContext http, CancellationToken ct) =>
    {
        if (token.Length is < 32 or > 64 || await users.FindInvitationTenantAsync(token, ct) is not { } tenant)
        {
            return Problems.NotFound(http); // unknown, used, revoked and expired look the same
        }

        // TEN-004: the tenant comes from the invitation, not from a header, so the read-only filter cannot see it
        if (await http.RequestServices.GetRequiredService<ITenantReadOnlyQuery>().ReasonAsync(tenant, ct) is { } readOnly)
        {
            return Problems.TenantReadOnly(http, readOnly);
        }

        var user = await directory.EnsureUserAsync(ctx.Issuer!, ctx.Subject!, ctx.TokenName ?? "کاربر", ct);
        if (user is null)
        {
            return Problems.Create(http, StatusCodes.Status403Forbidden, "user-not-provisioned", "This account is disabled");
        }

        AcceptedInvitation? accepted = await uow.ExecuteAsync(new FixedTenantContext(tenant, user.Value.Id), async (s, c) =>
        {
            AcceptedInvitation? done = await users.AcceptAsync(s, token, user.Value.Id, c);
            if (done is not null)
            {
                await Audit(http, s, ActorOf(ctx, http, user.Value.Id.Value, [.. done.RoleAssignments.Select(g => g.Role).Distinct()]),
                    new AuditEntry("iam.invitation.accepted", "User", user.Value.Id.Value.ToString(), Details: new { roles = done.RoleAssignments }), c);
            }

            return done;
        }, ct);
        if (accepted is null)
        {
            return Problems.NotFound(http);
        }

        RequestContextMiddleware.ForgetMemberships(cache, user.Value.Id);
        return Results.Ok(new { tenantId = accepted.TenantId, tenantName = accepted.TenantName, roles = accepted.RoleAssignments.Select(a => a.Role).Distinct() });
    })
    .WithMetadata(new AllowUnprovisionedUserAttribute())
    .AllowAnyAuthenticated()
    .RequireRateLimiting(RateLimits.Invitation);

// addRoleAssignment / removeRoleAssignment: the caller must be able to grant (or would be able to grant) that role there.
v1.MapPost("/users/{userId:guid}/role-assignments", async (Guid userId, RoleAssignmentInput input, RequestContext ctx, IUnitOfWork uow,
        IUserAdministration users, IEnumerable<IScopeLookup> lookups, IMemoryCache cache, HttpContext http, CancellationToken ct) =>
    {
        IResult result = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            string? role = RoleCatalog.FromId(input.RoleId);
            ScopeRef? scope = role is null ? null : await ResolveScope(s, lookups, input.ScopeType, input.ScopeId, c);
            if (role is null || scope is null)
            {
                return Problems.Validation(http, new Dictionary<string, string[]> { ["roleId"] = ["unknown-role-or-scope"] });
            }

            if (!AccessPolicy.CanAssign(ctx.ActiveMembership!.Assignments, role, scope))
            {
                return Forbidden(http);
            }

            try
            {
                AssignmentView? added = await users.AddAssignmentAsync(s, new UserId(userId), new RoleGrant(role, input.ScopeType, input.ScopeId), ctx.User!.Value, c);
                if (added is not null)
                {
                    await Audit(http, s, ActorOf(ctx, http), new AuditEntry("iam.role.assigned", "User", userId.ToString(),
                        Details: new { role, input.ScopeType, input.ScopeId, assignmentId = added.Id }), c);
                }
                return added is null ? Problems.NotFound(http) : Results.Created($"/api/v1/users/{userId}/role-assignments/{added.Id}", added);
            }
            catch (DuplicateAssignmentException)
            {
                return Problems.Create(http, StatusCodes.Status409Conflict, "duplicate-assignment", "The user already has this role at this scope");
            }
        }, ct);
        RequestContextMiddleware.ForgetMemberships(cache, new UserId(userId));
        return result;
    })
    .RequirePermission(Permissions.UsersManage);

v1.MapDelete("/users/{userId:guid}/role-assignments/{assignmentId:guid}", async (Guid userId, Guid assignmentId, RequestContext ctx,
        IUnitOfWork uow, IUserAdministration users, IEnumerable<IScopeLookup> lookups, IMemoryCache cache, HttpContext http, CancellationToken ct) =>
    {
        IResult result = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            AssignmentView? existing = await users.GetAssignmentAsync(s, new UserId(userId), assignmentId, c);
            ScopeRef? scope = existing is null ? null : await ResolveScope(s, lookups, Enum.Parse<ScopeType>(existing.ScopeType), existing.ScopeId, c);
            if (existing is null || scope is null)
            {
                return Problems.NotFound(http);
            }

            if (!AccessPolicy.CanAssign(ctx.ActiveMembership!.Assignments, existing.RoleName, scope))
            {
                return Forbidden(http);
            }

            try
            {
                await users.RemoveAssignmentAsync(s, new UserId(userId), assignmentId, c);
                await Audit(http, s, ActorOf(ctx, http), new AuditEntry("iam.role.removed", "User", userId.ToString(),
                    Details: new { role = existing.RoleName, existing.ScopeType, existing.ScopeId, assignmentId }), c);
                return Results.NoContent();
            }
            catch (LastOwnerException)
            {
                return Problems.Create(http, StatusCodes.Status409Conflict, "last-owner", "A store must keep at least one owner");
            }
        }, ct);
        RequestContextMiddleware.ForgetMemberships(cache, new UserId(userId));
        return result;
    })
    .RequirePermission(Permissions.UsersManage)
    .AllowWhenReadOnly();

await app.RunAsync();

internal sealed record SelectTenant(Guid TenantId);
internal sealed record AccountCreateInput(string? Code, string? Name, string? AccountType, Guid? ParentId, bool? RequiresDetail);
internal sealed record AccountMappingItem(string? Role, Guid? AccountId, Guid? BranchId);
internal sealed record AccountMappingInput(AccountMappingItem[]? Mappings);
internal sealed record JournalLineInput(Guid? AccountId, string? DetailType, Guid? DetailId, string? Debit, string? Credit, string? Description);
internal sealed record JournalCreateInput(DateTimeOffset? EffectiveAt, string? Description, JournalLineInput[]? Lines);
internal sealed record JournalReverseInput(string? Reason);
internal sealed record JournalPostInput;
internal sealed record ShiftOpenInput(Guid? RegisterId, string? OpeningCash);
internal sealed record CashMovementInput(string? Direction, string? Amount, string? Reason);
internal sealed record DenominationInput(string? Value, int? Count);
internal sealed record ShiftCloseInput(string? CountedCash, int? ExpectedVersion, DenominationInput[]? Denominations, string? VarianceReason);
internal sealed record BarcodeInput(string? Code, string? PackagingUnit, string? UnitFactor, bool? IsInternal);
internal sealed record VariantInput(string? Sku, Dictionary<string, string?>? AxisValues, string? UnitPrice, BarcodeInput[]? Barcodes);
internal sealed record ProductConfirmation(bool? Confirmed, int? SourceCandidate);
internal sealed record OpeningStock(Guid? WarehouseId, string? Qty, string? UnitCost);
internal sealed record ProductCreate(string? Name, string? BaseUnit, string? Brand, Guid? CategoryId, Guid? CatalogProductId, string? TaxCode,
    int? TaxRate, string[]? Axes, VariantInput[]? Variants, ProductConfirmation? Confirmation, OpeningStock? OpeningStock);
internal sealed record ProductUpdate(int? ExpectedVersion, string? Name, string? Brand, string? TaxCode, int? TaxRate, string? Status);
internal sealed record ExportCreate(string[]? Categories, DateTimeOffset? From, DateTimeOffset? To, bool? IncludeFullPhone);

internal sealed record ApprovalDecisionInput(string? Decision, string? Comment, string? Pin, Guid? ApproverId);

internal sealed record SetPin(string? Pin);

internal sealed record PairTerminal(string? PairingCode, string? DeviceName, string? UserAgent);

internal sealed record BranchCreate(string? Code, string? Name);

internal sealed record RegisterCreate(string? Code);

internal sealed record RoleAssignmentInput(Guid RoleId, ScopeType ScopeType, Guid? ScopeId);

internal sealed record InviteUser(string? DisplayName, string? Email, string? Mobile, List<RoleAssignmentInput>? RoleAssignments);

/// <summary>Entry point type for WebApplicationFactory in tests.</summary>
public partial class Program;

/// <summary>Opaque keyset cursor for listAudit: base64url of "a:{seq}".</summary>
static class AuditCursor
{
    public static string Encode(long seq) =>
        Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"a:{seq}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string cursor, out long seq)
    {
        seq = 0;
        if (cursor.Length is 0 or > 40) return false;
        string b64 = cursor.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
        Span<byte> bytes = stackalloc byte[32];
        return Convert.TryFromBase64String(b64, bytes, out int n)
            && System.Text.Encoding.ASCII.GetString(bytes[..n]) is ['a', ':', .. string digits]
            && long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out seq) && seq > 0;
    }
}
