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

var builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("Main")
                          ?? throw new InvalidOperationException("ConnectionStrings:Main is required.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<IUnitOfWork, PostgresUnitOfWork>();
builder.Services.AddSingleton<IUserDirectory, PostgresUserDirectory>();
builder.Services.AddSingleton<ITenantProfileQuery, PostgresTenantProfileQuery>();
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
    .AddEndpointFilter<DefaultDenyFilter>();

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
    }).AllowAnyAuthenticated();

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
    .RequirePermission(Permissions.TerminalsManage);

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
                r.BranchId, r.TerminalId, r.ClientIp, details = System.Text.Json.JsonDocument.Parse(r.DetailsJson).RootElement,
            }),
            page = new { nextCursor = hasMore ? AuditCursor.Encode(items[^1].Seq) : null, hasMore },
        });
    })
    .RequirePermission(Permissions.AuditView);

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
    .AddEndpointFilter<RequireTerminalFilter>();

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
    .RequirePermission(Permissions.UsersManage);

await app.RunAsync();

internal sealed record SelectTenant(Guid TenantId);

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
