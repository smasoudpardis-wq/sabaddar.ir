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
using RetailCloud.Inventory.Application;
using RetailCloud.Payments.Application;

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
builder.Services.AddSingleton<IWeightedBarcodeProfiles, PostgresWeightedBarcodeProfiles>();
builder.Services.AddSingleton<IPriceLists, PostgresPriceLists>();
builder.Services.AddSingleton<IDiscounts, PostgresDiscounts>();
builder.Services.AddSingleton<IGlobalCatalog, PostgresGlobalCatalog>();
builder.Services.AddSingleton<IIntegrationKeys, RetailCloud.Platform.Infrastructure.PostgresIntegrationKeys>();
// ADR-007: licensed providers; Open Food Facts stays off until the ODbL review (X-06). No request can choose its host (SEC-T-040).
OpenFoodFactsOptions offOptions = builder.Configuration.GetSection("Catalog:OpenFoodFacts").Get<OpenFoodFactsOptions>() ?? new OpenFoodFactsOptions();
builder.Services.AddSingleton(offOptions);
builder.Services.AddSingleton<ICatalogProvider>(_ =>
{
    var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // each lookup has its own deadline
    http.DefaultRequestHeaders.UserAgent.ParseAdd("RetailCloud/1.0 (catalog lookup)");
    return new OpenFoodFactsProvider(http, offOptions);
});
builder.Services.AddSingleton<IIndustryTemplates, RetailCloud.Platform.Infrastructure.PostgresIndustryTemplates>();
builder.Services.AddSingleton<IReceiptTemplateOverrides, PostgresReceiptTemplateOverrides>();
builder.Services.AddSingleton<IStockLedger, RetailCloud.Inventory.Infrastructure.PostgresStockLedger>();
builder.Services.AddSingleton<IStockAdjustments, RetailCloud.Inventory.Infrastructure.PostgresStockAdjustments>();
builder.Services.AddSingleton<IProcurement, RetailCloud.Inventory.Infrastructure.PostgresProcurement>();
builder.Services.AddSingleton<IStockTransfers, RetailCloud.Inventory.Infrastructure.PostgresStockTransfers>();
builder.Services.AddSingleton<ICounts, RetailCloud.Inventory.Infrastructure.PostgresCounts>();
builder.Services.AddSingleton<ISales, PostgresSales>();
builder.Services.AddSingleton<RetailCloud.Payments.Application.IPayments, RetailCloud.Payments.Infrastructure.PostgresPayments>();
builder.Services.AddSingleton<ISettlements, RetailCloud.Payments.Infrastructure.PostgresSettlements>();
builder.Services.AddSingleton<IShifts, PostgresShifts>();
builder.Services.AddSingleton<IJournals, PostgresJournals>();
builder.Services.AddSingleton<IAccountingReports, PostgresAccountingReports>();
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

// ---------------- US-172 / BR-086 / D-32: receipt templates. The industry's template (legal text, locked fields) is the
// platform's and versioned; a store changes only its footer and signature (DI-085: there is nowhere to put anything else).
object IndustryJson(IndustryTemplate t) => new
{
    code = t.IndustryCode, name = t.IndustryName, receiptTemplateVersion = t.Ref, version = t.Version, t.LockedFields, t.LegalText, t.Reason,
    publishedAt = t.CreatedAt,
};

object ReceiptTemplateJson(IndustryTemplate t, ReceiptOverride o) => new
{
    industryTemplateVersion = t.Ref, industryName = t.IndustryName, lockedFields = t.LockedFields, legalText = t.LegalText,
    overrides = new { footerText = o.FooterText, signature = o.Signature, logoObjectKey = o.LogoObjectKey }, version = o.Version,
};

v1.MapGet("/templates/receipt", async (RequestContext ctx, IUnitOfWork uow, ITenantProfileQuery profiles, IIndustryTemplates industries,
        IReceiptTemplateOverrides overrides, HttpContext http, CancellationToken ct) =>
    {
        if (await profiles.GetAsync(ctx.ActiveTenant!.Value, ct) is not { } tenant) return Problems.NotFound(http);
        var (template, own) = await uow.ExecuteAsync(ctx, async (s, c) => (await industries.LatestAsync(s, tenant.IndustryCode, c), await overrides.GetAsync(s, c)), ct);
        return template is null ? Problems.NotFound(http) : Results.Ok(ReceiptTemplateJson(template, own));
    })
    .RequireTenantMember();

v1.MapPut("/templates/receipt", async (ReceiptTemplateUpdateInput body, RequestContext ctx, IUnitOfWork uow, ITenantProfileQuery profiles,
        IIndustryTemplates industries, IReceiptTemplateOverrides overrides, HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        if (body.ExpectedVersion is not int expected || expected < 0) errors["expectedVersion"] = ["required (0 before the first change)"];
        string? footer = string.IsNullOrWhiteSpace(body.Overrides?.FooterText) ? null : body.Overrides!.FooterText!.Trim();
        string? signature = string.IsNullOrWhiteSpace(body.Overrides?.Signature) ? null : body.Overrides!.Signature!.Trim();
        if (footer is { Length: > 300 }) errors["overrides.footerText"] = ["up to 300 characters"];
        if (signature is { Length: > 100 }) errors["overrides.signature"] = ["up to 100 characters"];
        if (body.Overrides?.LogoObjectKey is not null) errors["overrides.logoObjectKey"] = ["not-yet-supported"];   // uploads come with US-092
        if (errors.Count > 0) return Problems.Validation(http, errors);
        if (await profiles.GetAsync(ctx.ActiveTenant!.Value, ct) is not { } tenant) return Problems.NotFound(http);
        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            if (await industries.LatestAsync(s, tenant.IndustryCode, c) is not { } template) return Problems.NotFound(http);
            if (await overrides.SetAsync(s, body.ExpectedVersion!.Value, footer, signature, ctx.User!.Value.Value, c) is not { } saved)
            {
                return Problems.Create(http, StatusCodes.Status409Conflict, "version-conflict", "The template changed; reload it");
            }

            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("templates.receipt.updated", "ReceiptTemplate", null, null,
                new { footer, signature, saved.Version, industryTemplate = template.Ref }), c);
            return Results.Ok(ReceiptTemplateJson(template, saved));
        }, ct);
    })
    .RequirePermission(Permissions.TemplatesReceiptManage);

// The platform API (D07 audience "platform", D06 §6.2): only a token of the platform realm with PlatformSuperAdmin and a
// second factor (TM-071: a merchant token is refused here, and a platform token is refused on the merchant API). Global
// data only: no tenant context, no merchant data.
ILogger platformLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RetailCloud.Platform");
RouteGroupBuilder platformApi = app.MapGroup("/api/v1/platform").RequireAuthorization(ServiceCollectionExtensions.PlatformAdminPolicy);

platformApi.MapGet("/industry-profiles", async (IUnitOfWork uow, IIndustryTemplates industries, CancellationToken ct) =>
    Results.Ok(new { items = (await uow.ExecuteWithoutTenantAsync(null, (s, c) => industries.LatestAsync(s, c), ct)).Select(IndustryJson) }));

platformApi.MapPut("/industry-profiles/{code}", async (string code, IndustryProfileUpdateInput body, IUnitOfWork uow, IIndustryTemplates industries,
        HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        if (body.ExpectedVersion is null) errors["expectedVersion"] = ["required"];
        if (body.Reason is null || body.Reason.Trim().Length is < 3 or > 500) errors["reason"] = ["3-500 characters"];
        if (ReceiptTemplateRules.Problem(body.LegalText, body.LockedFields) is { } problem) errors[problem.Split(':')[0]] = [problem];
        if (errors.Count > 0) return Problems.Validation(http, errors);
        string operatorId = $"{http.User.FindFirst("iss")?.Value}|{http.User.FindFirst("sub")?.Value}";
        var (outcome, template) = await uow.ExecuteWithoutTenantAsync(null, (s, c) => industries.PublishAsync(s, code, body.ExpectedVersion!.Value,
            body.LegalText!.Trim(), body.LockedFields!, body.Reason!.Trim(), operatorId, c), ct);
        if (outcome == TemplateOutcome.NotFound) return Problems.NotFound(http);
        if (outcome == TemplateOutcome.VersionConflict)
        {
            return Problems.Create(http, StatusCodes.Status409Conflict, "version-conflict", "A newer version was published; reload it");
        }

        // the template row itself is the record (who, why, when); the log line feeds the platform's own audit trail (US-171)
        PlatformLog.TemplatePublished(platformLogger, template!.IndustryCode, template.Version, operatorId);
        return Results.Ok(IndustryJson(template));
    });

// IAM-008 / US-027 (platform side): PlatformSupport asks a store for time-limited access with a reason; only its TenantOwner
// approves (SoD-05, store side). While the grant is in force the operator sees a read-only, masked view (no customer data,
// no payment references or card digits), and every look is in the store's audit with actor kind Support.
RouteGroupBuilder supportApi = app.MapGroup("/api/v1/platform").RequireAuthorization(ServiceCollectionExtensions.PlatformSupportPolicy);
string OperatorOf(HttpContext http) => $"{http.User.FindFirst("iss")?.Value}|{http.User.FindFirst("sub")?.Value}";
AuditActor SupportActor(HttpContext http) => new(null, "Support", ["PlatformSupport"], null, null, ClientIp(http));

supportApi.MapPost("/tenants/{tenantId:guid}/support-access-requests", async (Guid tenantId, SupportAccessRequestInput body, IUnitOfWork uow,
        ISupportAccess support, ITenantProfileQuery profiles, HttpContext http, CancellationToken ct) =>
    {
        if (SupportAccessRules.Problem(body.Reason, body.DurationMinutes) is { } problem)
        {
            return Problems.Validation(http, new Dictionary<string, string[]> { [problem.Split(':')[0]] = [problem] });
        }

        string op = OperatorOf(http);
        if (await profiles.GetAsync(new TenantId(tenantId), ct) is null) return Problems.NotFound(http);
        SupportAccessView created = await uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenantId)), async (s, c) =>
        {
            SupportAccessView v = await support.RequestAsync(s, op, body.Reason!.Trim(), body.DurationMinutes!.Value, c);
            await Audit(http, s, SupportActor(http), new AuditEntry("support.access.requested", "SupportAccessRequest", v.Id.ToString(), v.Reason,
                new { @operator = op, v.DurationMinutes }), c);
            return v;
        }, ct);
        return Results.Created($"/api/v1/support-access-requests/{created.Id}", created);
    });

supportApi.MapGet("/support/tenants/{tenantId:guid}/sales", async (Guid tenantId, IUnitOfWork uow, ISupportAccess support, ISales sales,
        HttpContext http, CancellationToken ct) =>
    {
        string op = OperatorOf(http);
        return await uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenantId)), async (s, c) =>
        {
            // no grant in force for this operator in this store: indistinguishable from a store that does not exist
            if (await support.ActiveGrantAsync(s, op, c) is not { } grant)
            {
                return Problems.Create(http, StatusCodes.Status403Forbidden, "support-access-required",
                    "The store's owner has not granted you access, or the grant has ended");
            }

            IReadOnlyList<SaleView> recent = await sales.RecentAsync(s, 50, c);
            await Audit(http, s, SupportActor(http), new AuditEntry("support.sales.viewed", "Sale", null, null,
                new { @operator = op, grant = grant.Id, count = recent.Count }), c);
            return Results.Ok(new
            {
                grantedUntil = grant.GrantedUntil,
                items = recent.Select(x =>
                {
                    var t = SaleMath.Totals(x.Lines);
                    return new
                    {
                        x.Id, x.Status, x.InvoiceNo, x.BranchId, x.RegisterId, x.Channel, x.LastActivityAt, x.FinalizedAt,
                        lines = x.Lines.Select(l => new { l.Name, qty = Qty(l.Qty), l.Unit, lineTotal = Money(l.LineTotal) }),
                        totals = new { gross = Money(t.Gross), discount = Money(t.Discount), tax = Money(t.Tax), total = Money(t.Total), paid = Money(x.Paid) },
                        // IAM-008 masking: how it was paid, never the references, card digits or who the customer is
                        tenders = (x.Tenders ?? []).Select(td => new { td.Method, amount = Money(td.Amount), status = td.Voided ? "Voided" : "Confirmed" }),
                        customer = (object?)null,
                    };
                }),
            });
        }, ct);
    });

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
    AccountingOutcome.NotDraft => Problems.Create(http, StatusCodes.Status409Conflict, "journal-not-draft", "Only a draft entry can be posted or deleted"),
    AccountingOutcome.AlreadyReversed => Problems.Create(http, StatusCodes.Status409Conflict, "journal-already-reversed", "This entry is already reversed"),
    AccountingOutcome.DuplicateCode => Problems.Create(http, StatusCodes.Status409Conflict, "duplicate-code", "This account code is already used"),
    AccountingOutcome.UnknownRole => Problems.Validation(http, new Dictionary<string, string[]> { ["mappings"] = [$"unknown role: {detail}"] }),
    _ => Problems.Validation(http, new Dictionary<string, string[]> { ["lines"] = [detail ?? "unknown account"] }),
};

// an automatic entry that cannot be posted refuses its business document: a role with no account, or no open period
IResult AutomaticEntryProblem(HttpContext http, AccountingResult<JournalView> entry) => entry.Outcome == AccountingOutcome.UnknownRole
    ? Problems.Create(http, StatusCodes.Status422UnprocessableEntity, "account-role-unmapped",
        $"No account is mapped to the role {entry.Detail}; set it in the account mappings")
    : AccountingProblem(http, entry.Outcome, entry.Detail);

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

object PeriodJson(PeriodView p) => new { p.Id, p.FiscalYear, p.PeriodNo, start = p.Start, end = p.End, p.Status, p.ClosedAt, p.ClosedBy, p.Version };

v1.MapGet("/accounting/periods", async (RequestContext ctx, IUnitOfWork uow, IJournals journals, CancellationToken ct) =>
        Results.Ok((await uow.ExecuteAsync(ctx, (s, c) => journals.PeriodsAsync(s, c), ct)).Select(PeriodJson)))
    .RequirePermission(Permissions.AccountingRead);

// ACC-009 / D-45 (US-113): closing a period that has ended, in order, with no drafts left in it. Nothing is posted into it
// afterwards (DI-042, in the DB too); a late event is posted today in the open period, referring to its source.
v1.MapIdempotentPost<VersionInput>("/accounting/periods/{periodId:guid}/close", "accounting.close-period", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        Guid periodId = Guid.Parse((string)http.GetRouteValue("periodId")!, System.Globalization.CultureInfo.InvariantCulture);
        if (body.ExpectedVersion is not int expected)
        {
            return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { ["expectedVersion"] = ["required"] }));
        }

        DateTimeOffset now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var r = await http.RequestServices.GetRequiredService<IJournals>().ClosePeriodAsync(scope, periodId, expected, ctx.User!.Value.Value, now, ct);
        IResult? refused = r.Outcome switch
        {
            AccountingOutcome.Done => null,
            AccountingOutcome.NotFound => Problems.NotFound(http),
            AccountingOutcome.PeriodClosed => Problems.Create(http, StatusCodes.Status409Conflict, "period-closed", "The fiscal period is already closed"),
            AccountingOutcome.VersionConflict => Problems.Create(http, StatusCodes.Status409Conflict, "version-conflict", "The period changed; reload it"),
            AccountingOutcome.PeriodNotEnded => Problems.Create(http, StatusCodes.Status409Conflict, "period-not-ended",
                "A period is closed once it has ended; today's documents need an open period"),
            AccountingOutcome.EarlierPeriodOpen => Problems.Create(http, StatusCodes.Status409Conflict, "earlier-period-open", "Close the earlier periods first"),
            AccountingOutcome.PeriodHasDrafts => Problems.WithContext(http, StatusCodes.Status409Conflict, "period-has-drafts",
                "Post or delete the draft entries dated in this period first", new { drafts = int.Parse(r.Detail!, System.Globalization.CultureInfo.InvariantCulture) }),
            _ => AccountingProblem(http, r.Outcome, r.Detail),
        };
        if (refused is not null) return CommandResult.Fail(refused);

        PeriodView period = r.Value!;
        await http.RequestServices.GetRequiredService<IOutbox>().AddAsync(scope, "Accounting", "accounting.period-closed", 1, period.Id, new
        {
            periodId = period.Id, period.FiscalYear, period.PeriodNo, start = period.Start, end = period.End, closedAt = period.ClosedAt,
        }, causationId: null, ct);
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("accounting.period.closed", "FiscalPeriod", period.Id.ToString(), null,
            new { period.FiscalYear, period.PeriodNo }), ct);
        return CommandResult.Ok(PeriodJson(period));
    })
    .RequirePermission(Permissions.AccountingPeriodClose);

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

// US-114 / ACC-007 (AT-107): the trial balance and the ledger of an account, a group of accounts or one detail
// The range is [from, to); without one, the fiscal year that holds `to` (default now) so far.
async Task<(DateTimeOffset From, DateTimeOffset To)?> ReportRangeAsync(IJournals journals, IUnitOfWorkScope s, DateTimeOffset? from, DateTimeOffset? to,
    DateTimeOffset now, CancellationToken c)
{
    DateTimeOffset until = to ?? now;
    if (from is { } f) return f < until && until - f <= TimeSpan.FromDays(800) ? (f, until) : null;
    IReadOnlyList<PeriodView> periods = await journals.PeriodsAsync(s, c);
    PeriodView? holding = periods.FirstOrDefault(p => p.Start < until && until <= p.End) ?? periods.LastOrDefault(p => p.Start < until);
    DateTimeOffset start = holding is null ? until.AddDays(-30) : periods.Where(p => p.FiscalYear == holding.FiscalYear).Min(p => p.Start);
    return (start, until);
}

object AmountSide(long net) => new { debit = Rial(Math.Max(net, 0)), credit = Rial(Math.Max(-net, 0)) };

v1.MapGet("/accounting/reports/trial-balance", async (RequestContext ctx, IUnitOfWork uow, IJournals journals, IAccountingReports reports, HttpContext http,
        DateTimeOffset? from, DateTimeOffset? to, bool? includeEmpty, CancellationToken ct) =>
    {
        DateTimeOffset now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var result = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            if (await ReportRangeAsync(journals, s, from, to, now, c) is not { } range) return null;
            IReadOnlyList<AccountView> accounts = await journals.AccountsAsync(s, c);
            IReadOnlyList<AccountMovement> moves = await reports.MovementsAsync(s, range.From, range.To, c);
            return new { range, built = TrialBalance.Build(accounts, moves, includeEmpty ?? false) };
        }, ct);
        if (result is null) return Problems.Validation(http, new Dictionary<string, string[]> { ["query"] = ["from < to, at most 800 days"] });
        TrialBalanceTotals t = result.built.Totals;
        return Results.Ok(new
        {
            from = result.range.From, to = result.range.To,
            items = result.built.Lines.Select(l => new
            {
                accountId = l.Account.Id, accountCode = l.Account.Code, accountName = l.Account.Name, accountType = l.Account.AccountType,
                parentId = l.Account.ParentId, postable = l.Account.IsPostable, l.Level,
                openingDebit = Rial(l.OpeningDebit), openingCredit = Rial(l.OpeningCredit), periodDebit = Rial(l.PeriodDebit), periodCredit = Rial(l.PeriodCredit),
                closingDebit = Rial(l.ClosingDebit), closingCredit = Rial(l.ClosingCredit),
            }),
            totals = new
            {
                openingDebit = Rial(t.OpeningDebit), openingCredit = Rial(t.OpeningCredit), periodDebit = Rial(t.PeriodDebit), periodCredit = Rial(t.PeriodCredit),
                closingDebit = Rial(t.ClosingDebit), closingCredit = Rial(t.ClosingCredit), t.Balanced,
            },
            meta = new { generatedAt = now, dataAsOf = now },
        });
    })
    .RequirePermission(Permissions.AccountingRead);

v1.MapGet("/accounting/reports/ledger/{accountId:guid}", async (Guid accountId, RequestContext ctx, IUnitOfWork uow, IJournals journals, IAccountingReports reports,
        HttpContext http, Guid? detailId, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int? limit, CancellationToken ct) =>
    {
        int size = limit ?? 100;
        int offset = 0;
        if (size is < 1 or > 500 || (cursor is not null && (!int.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out offset)
            || offset > 1_000_000)))
        {
            return Problems.Validation(http, new Dictionary<string, string[]> { ["query"] = ["limit 1-500; cursor from the previous page"] });
        }

        DateTimeOffset now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var result = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            IReadOnlyList<AccountView> accounts = await journals.AccountsAsync(s, c);
            if (accounts.FirstOrDefault(a => a.Id == accountId) is not { } account) return (Found: false, Range: ((DateTimeOffset, DateTimeOffset)?)null, Account: (AccountView?)null, Page: (LedgerPage?)null);
            if (await ReportRangeAsync(journals, s, from, to, now, c) is not { } range) return (true, null, account, null);
            LedgerPage page = await reports.LedgerAsync(s, TrialBalance.WithDescendants(accounts, accountId), detailId, range.From, range.To, offset, size, c);
            return (true, range, account, page);
        }, ct);
        if (!result.Found) return Problems.NotFound(http);
        if (result.Page is not { } p) return Problems.Validation(http, new Dictionary<string, string[]> { ["query"] = ["from < to, at most 800 days"] });
        AccountView a = result.Account!;
        return Results.Ok(new
        {
            account = new { a.Id, a.Code, a.Name, a.AccountType, postable = a.IsPostable }, detailId,
            from = result.Range!.Value.Item1, to = result.Range!.Value.Item2,
            opening = AmountSide(p.Opening), periodDebit = Rial(p.PeriodDebit), periodCredit = Rial(p.PeriodCredit), closing = AmountSide(p.Closing),
            items = p.Lines.Select(l => new
            {
                l.JournalId, l.EffectiveAt, l.Description, l.SourceType, l.AccountId, l.AccountCode, l.DetailType, l.DetailId,
                debit = Rial(l.Debit), credit = Rial(l.Credit), runningBalance = Rial(l.RunningBalance),
            }),
            page = new { nextCursor = p.HasMore ? (offset + size).ToString(System.Globalization.CultureInfo.InvariantCulture) : null, hasMore = p.HasMore },
        });
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

// D02 §6.3: a manual draft that will not be posted is deleted, with an audit record of what it held
v1.MapDelete("/accounting/journals/{journalId:guid}", async (Guid journalId, RequestContext ctx, IUnitOfWork uow, IJournals journals, HttpContext http,
        CancellationToken ct) =>
    {
        var r = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var deleted = await journals.DeleteDraftAsync(s, journalId, c);
            if (deleted.Outcome == AccountingOutcome.Done)
            {
                await Audit(http, s, ActorOf(ctx, http), new AuditEntry("accounting.journal.draft-deleted", "Journal", journalId.ToString(), deleted.Value!.Description,
                    new { lines = deleted.Value.Lines.Count, total = deleted.Value.Lines.Sum(l => l.Debit), deleted.Value.EffectiveAt }), c);
            }

            return deleted;
        }, ct);
        return r.Outcome == AccountingOutcome.Done ? Results.NoContent() : AccountingProblem(http, r.Outcome, r.Detail);
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
    p.Id, p.Name, p.Brand, baseUnit = p.BaseUnit, p.TaxCode, taxRate = p.TaxRate, p.Status, p.Version, axes = p.Axes ?? [],
    variants = p.Variants.Select(VariantJson),
};
object VariantJson(VariantView v) => new { v.Id, v.Sku, axisValues = v.AxisValues, v.Status, barcodes = v.Barcodes,
    unitPrice = v.UnitPrice?.ToString(System.Globalization.CultureInfo.InvariantCulture) };

IResult CatalogProblem(HttpContext http, CatalogOutcome outcome, Guid? conflictProduct) => outcome switch
{
    CatalogOutcome.NotFound => Problems.NotFound(http),
    CatalogOutcome.AxesMismatch => Problems.Validation(http, new Dictionary<string, string[]> { ["axisValues"] = ["a value for each axis of the product, and no other"] }),
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
    if (v?.UnitPrice is not null && !TryMoney(v.UnitPrice, positive: true, out _)) errors[$"{at}.unitPrice"] = ["positive rial amount as a whole-number string"];
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
        decimal openingQty = 0;
        long openingCost = 0;
        if (body.OpeningStock is { } os)
        {
            // UC-01: the stock a new product starts with, at its cost (OpeningBalance movement, INV-002)
            if (os.WarehouseId is null) errors["openingStock.warehouseId"] = ["required"];
            if (os.Qty is null || !ProductRules.TryFactor(os.Qty, out openingQty)) errors["openingStock.qty"] = ["positive decimal, up to 6 places"];
            if (!TryMoney(os.UnitCost, positive: false, out long unitCost)) errors["openingStock.unitCost"] = ["rial amount per base unit as a whole-number string"];
            else openingCost = (long)Math.Round(openingQty * unitCost, MidpointRounding.AwayFromZero);
            if (body.Variants is not { Length: 1 }) errors["openingStock"] = ["only for a product with one variant"];
        }

        // CAT-008: a matrix names its axes; each variant then has a value for exactly those axes (the panel builds the product of the values)
        string[] axesWanted = body.Axes ?? [];
        if (axesWanted.Any(a => !ProductRules.Axes.Contains(a)) || axesWanted.Distinct(StringComparer.Ordinal).Count() != axesWanted.Length)
        {
            errors["axes"] = ["distinct of color|size|model"];
        }
        else if (axesWanted.Length > 0)
        {
            for (int i = 0; i < (body.Variants?.Length ?? 0); i++)
            {
                if (!((IEnumerable<string>?)body.Variants![i]?.AxisValues?.Keys ?? []).Order(StringComparer.Ordinal).SequenceEqual(axesWanted.Order(StringComparer.Ordinal)))
                {
                    errors[$"variants[{i}].axisValues"] = [$"a value for each of: {string.Join(", ", axesWanted)}"];
                }
            }
        }
        if (body.Confirmation?.Confirmed is null) errors["confirmation.confirmed"] = ["required: true (CAT-004.1)"];
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
        // CAT-004.1 / D-17 / AT-201: suggested data is saved only by the user's explicit confirmation, whatever its confidence
        if (body.Confirmation!.Confirmed == false)
        {
            return CommandResult.Fail(Problems.WithContext(http, StatusCodes.Status422UnprocessableEntity, "confirmation-required",
                "Confirm the suggested product data before saving", new { catalogProductId = body.CatalogProductId, sourceCandidate = body.Confirmation.SourceCandidate }));
        }

        if (body.CatalogProductId is { } adopted
            && !await http.RequestServices.GetRequiredService<IGlobalCatalog>().PublishedAsync(scope, adopted, ct))
        {
            return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { ["catalogProductId"] = ["not a published catalog product"] }));
        }

        var result = await http.RequestServices.GetRequiredService<IProductCatalog>().CreateAsync(scope,
            new ProductSpec(body.Name!, body.Brand, body.BaseUnit!, body.TaxCode, body.TaxRate ?? 0, variants, axesWanted, body.CatalogProductId), ct);
        if (result.Outcome != CatalogOutcome.Done) return CommandResult.Fail(CatalogProblem(http, result.Outcome, result.ConflictProductId));
        // CAT-008/009: first prices of a new product go to the default list from now. Not a price change, so no BR-041 approval.
        PriceItem[] firstPrices = [.. body.Variants!.Where(v => v.UnitPrice is not null)
            .Select(v => new PriceItem(result.Value!.Variants.Single(x => x.Sku == v.Sku).Id, long.Parse(v.UnitPrice!, System.Globalization.CultureInfo.InvariantCulture)))];
        if (firstPrices.Length > 0)
        {
            IPriceLists priceLists = http.RequestServices.GetRequiredService<IPriceLists>();
            Guid defaultList = (await priceLists.ListAsync(scope, ct)).Single(l => l.IsDefault).Id;
            if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.CatalogPriceManage, ScopeRef.Tenant)) return CommandResult.Fail(Forbidden(http));
            await priceLists.PreviewAsync(scope, defaultList, firstPrices, DateTimeOffset.UtcNow, ct);   // locks the list
            await priceLists.SetAsync(scope, defaultList, firstPrices, DateTimeOffset.UtcNow, "قیمت اولیه کالا", ctx.User!.Value.Value, ct);
            result = result with { Value = await http.RequestServices.GetRequiredService<IProductCatalog>().GetAsync(scope, result.Value!.Id, ct) };
        }

        if (body.OpeningStock is { WarehouseId: Guid warehouseId })
        {
            ScopeRef? warehouse = await ResolveScope(scope, http.RequestServices.GetServices<IScopeLookup>(), ScopeType.Warehouse, warehouseId, ct);
            if (warehouse is null)
            {
                return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { ["openingStock.warehouseId"] = ["not a warehouse of this store"] }));
            }

            if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.InventoryReceiptCreate, warehouse)) return CommandResult.Fail(Forbidden(http));
            StockResult opening = await http.RequestServices.GetRequiredService<IStockLedger>().PostAsync(scope, new StockDocument("Product", result.Value!.Id,
                DateTimeOffset.UtcNow, [new StockLine(1, warehouseId, result.Value.Variants[0].Id, MovementType.OpeningBalance, openingQty, openingCost)], "opening-stock"), ct);
            if (opening.Outcome != StockOutcome.Done) throw new InvalidOperationException($"opening stock of a new product: {opening.Outcome}");

            // UC-01 step 9: the entry «Inventory / OpeningBalanceEquity» in the same transaction
            if (OpeningStockRule.For(result.Value.Id, openingCost, DateTimeOffset.UtcNow, result.Value.Name) is { } draft)
            {
                AccountingResult<JournalView> entry = await http.RequestServices.GetRequiredService<IJournals>().PostAutomaticAsync(scope, draft, ct);
                if (entry.Outcome != AccountingOutcome.Done) return CommandResult.Fail(AutomaticEntryProblem(http, entry));
            }
        }

        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("catalog.product.created", "Product", result.Value!.Id.ToString(),
            Details: new
            {
                result.Value.Name, variants = variants.Count, barcodes = variants.Sum(v => v.Barcodes.Count), axes = axesWanted,
                prices = firstPrices.Length,
                openingStock = body.OpeningStock is null ? null : new { body.OpeningStock.WarehouseId, qty = openingQty, cost = openingCost },
            }), ct);
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

v1.MapPatch("/products/{productId:guid}/variants/{variantId:guid}", async (Guid productId, Guid variantId, VariantUpdate body, RequestContext ctx,
        IUnitOfWork uow, IProductCatalog catalog, HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        if (body.ExpectedVersion is null) errors["expectedVersion"] = ["required (the product's version)"];
        if (body.Status is not ("Active" or "Inactive")) errors["status"] = ["Active|Inactive"];
        if (errors.Count > 0) return Problems.Validation(http, errors);
        CatalogOutcome outcome = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            CatalogOutcome o = await catalog.SetVariantStatusAsync(s, productId, variantId, body.ExpectedVersion!.Value, body.Status!, c);
            if (o == CatalogOutcome.Done)
            {
                await Audit(http, s, ActorOf(ctx, http), new AuditEntry("catalog.variant.updated", "Product", productId.ToString(),
                    Details: new { variantId, body.Status }), c);
            }

            return o;
        }, ct);
        return outcome == CatalogOutcome.Done
            ? Results.Ok(ProductJson((await uow.ExecuteAsync(ctx, (s, c) => catalog.GetAsync(s, productId, c), ct))!))
            : CatalogProblem(http, outcome, null);
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

// ---------------- US-045: weighted barcode profiles (CAT-007, DI-084) and barcode resolve
v1.MapGet("/weighted-barcode-profiles", async (RequestContext ctx, IUnitOfWork uow, IWeightedBarcodeProfiles weighted, CancellationToken ct) =>
        Results.Ok(await uow.ExecuteAsync(ctx, (s, c) => weighted.GetAsync(s, c), ct)))
    .RequireTenantMember();

v1.MapPut("/weighted-barcode-profiles", async (WeightedProfilesInput body, RequestContext ctx, IUnitOfWork uow, IWeightedBarcodeProfiles weighted,
        HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        if (body.ExpectedVersion is null) errors["expectedVersion"] = ["required"];
        if (body.Profiles is not { Length: <= WeightedBarcodes.MaxProfiles }) errors["profiles"] = [$"0-{WeightedBarcodes.MaxProfiles} profiles"];
        for (int i = 0; i < (body.Profiles?.Length ?? 0) && body.Profiles!.Length <= WeightedBarcodes.MaxProfiles; i++)
        {
            WeightedProfileInput p = body.Profiles[i] ?? new WeightedProfileInput(null, null, null, null, null, null, null);
            WeightedBarcodes.Validate(errors, $"profiles[{i}]", p.Prefix, p.ItemCodeLength, p.ValueType, p.ValueLength, p.Decimals, p.HasInnerCheckDigit);
        }

        if (errors.Count == 0)
        {
            if (body.Profiles!.Where(p => p.Id is not null).GroupBy(p => p.Id).Any(g => g.Count() > 1)) errors["profiles"] = ["duplicate id"];
            else if (WeightedBarcodes.Overlap([.. body.Profiles!.Select(p => p.Prefix!)]) is { } clash)
            {
                errors["profiles"] = [$"prefixes {clash.A} and {clash.B} overlap: a label would decode two ways (DI-084)"];
            }
        }

        if (errors.Count > 0) return Problems.Validation(http, errors);
        WeightedBarcodeProfile[] profiles = [.. body.Profiles!.Select(p => new WeightedBarcodeProfile(p.Id ?? Guid.CreateVersion7(), p.Prefix!,
            p.ItemCodeLength!.Value, Enum.Parse<WeightedValueType>(p.ValueType!), p.ValueLength!.Value, p.Decimals!.Value, p.HasInnerCheckDigit ?? false))];
        var (outcome, set) = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var r = await weighted.ReplaceAsync(s, body.ExpectedVersion!.Value, profiles, c);
            if (r.Outcome == WeightedProfilesOutcome.Done)
            {
                await Audit(http, s, ActorOf(ctx, http), new AuditEntry("catalog.weighted-profiles.set", "WeightedBarcodeProfile", null,
                    Details: new { version = r.Set!.Version, prefixes = profiles.Select(p => p.Prefix) }), c);
            }

            return r;
        }, ct);
        return outcome switch
        {
            WeightedProfilesOutcome.Done => Results.Ok(set),
            WeightedProfilesOutcome.VersionConflict => Problems.Create(http, StatusCodes.Status409Conflict, "version-conflict",
                "The profiles changed; reload and retry"),
            _ => Problems.Validation(http, new Dictionary<string, string[]> { ["profiles"] = ["prefixes overlap (DI-084)"] }),
        };
    })
    .RequirePermission(Permissions.TenantSettingsManage);

// CAT-003/CAT-007: what a scanned code is in this store. Weighted labels are decoded first (their item code is the
// variant's internal code); then the code is looked up as GTIN-14 or internal code. CAT-004 (US-043): a public code the
// store does not know gets candidates from the global catalog and licensed providers, each with its source, confidence
// and licence; providers are asked outside the transaction, within a 4-second budget, and a failure answers degraded.
v1.MapPost("/catalog/barcodes/resolve", async (BarcodeResolveInput body, RequestContext ctx, IUnitOfWork uow, IProductCatalog catalog,
        IWeightedBarcodeProfiles weighted, HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        if (body.Barcode is not { Length: >= 1 and <= 32 } || string.IsNullOrWhiteSpace(body.Barcode)) errors["barcode"] = ["1-32 characters"];
        if (body.BranchId is null) errors["branchId"] = ["required"];
        if (body.PreferredSources?.Any(x => x is not ("tenant" or "global" or "licensed_providers")) == true)
        {
            errors["preferredSources"] = ["tenant|global|licensed_providers"];
        }

        if (errors.Count > 0) return Problems.Validation(http, errors);
        if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.CatalogProductManage, new ScopeRef(ScopeType.Branch, body.BranchId, body.BranchId)))
        {
            return Forbidden(http);
        }

        string code = Gtin.NormalizeDigits(body.Barcode!.Trim());
        bool gs1 = Gtin.TryParse(code, out Gtin gtin);
        bool digitsOnly = code.All(char.IsAsciiDigit) && code.Length is 8 or 12 or 13 or 14;
        if (!gs1 && (digitsOnly || !InternalCode.IsValid(code)))
        {
            return InputProblem(http, new Dictionary<string, string[]> { ["barcode"] = ["not a valid EAN-8, UPC-A, EAN-13, GTIN-14 or internal code"] }, true);
        }

        var result = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            WeightedProfileSet set = await weighted.GetAsync(s, c);
            var (status, label) = WeightedBarcodes.Decode(set.Profiles, code);
            BarcodeMatch? match = status switch
            {
                WeightedDecodeStatus.Decoded => await catalog.FindByCodeAsync(s, label!.ItemCode, c),
                WeightedDecodeStatus.NotWeighted => await catalog.FindByCodeAsync(s, gs1 ? gtin.Normalized14 : code, c),
                _ => null,
            };
            // CAT-010 / D-18: the one automatic rule that applies here and now (the largest), for the POS to show
            DiscountRuleView? rule = match is null ? null : DiscountPolicy.Best(
                await http.RequestServices.GetRequiredService<IDiscounts>().RulesForAsync(s, [match.ProductId], DateTimeOffset.UtcNow, c),
                match.ProductId, body.BranchId!.Value, DateTimeOffset.UtcNow);
            return (status, label, match, rule);
        }, ct);
        if (result.status == WeightedDecodeStatus.Invalid)
        {
            return InputProblem(http, new Dictionary<string, string[]> { ["barcode"] = ["weighted label: the value check digit does not match"] }, true);
        }

        bool isWeighted = result.status == WeightedDecodeStatus.Decoded;
        var normalized = new Dictionary<string, object?>
        {
            ["isInternal"] = !gs1 || gtin.IsRestrictedCirculation,
            ["isWeighted"] = isWeighted,
        };
        if (gs1) normalized["gtin14"] = gtin.Normalized14;
        if (isWeighted && result.match is not null)
        {
            WeightedLabel l = result.label!;
            var weightedJson = new Dictionary<string, object?>
            {
                ["variantId"] = result.match.VariantId, ["value"] = l.ValueText, ["valueType"] = l.Profile.ValueType.ToString(),
            };
            if (result.match.UnitPrice is long unit and > 0)
            {
                // CAT-007.1: a price label is the reference amount; a weight label is weight x unit price
                (decimal qty, long amount) = l.Profile.ValueType == WeightedValueType.Price
                    ? WeightedBarcodes.PriceLabelLine((long)l.Value, unit)
                    : (l.Value, (long)Math.Round(l.Value * unit, MidpointRounding.AwayFromZero));
                weightedJson["quantity"] = qty.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
                weightedJson["amount"] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            normalized["weighted"] = weightedJson;
        }

        var candidates = new List<CatalogCandidate>();
        bool degraded = false;
        string[] sources = body.PreferredSources is { Length: > 0 } ? body.PreferredSources : ["tenant", "global", "licensed_providers"];
        if (result.match is null && gs1 && !gtin.IsRestrictedCirculation && !isWeighted)
        {
            IGlobalCatalog global = http.RequestServices.GetRequiredService<IGlobalCatalog>();
            List<ICatalogProvider> providers = [.. http.RequestServices.GetServices<ICatalogProvider>().Where(p => p.Enabled)];
            bool askProviders = sources.Contains("licensed_providers") && providers.Count > 0;
            var (fromGlobal, cached) = await uow.ExecuteAsync(ctx, async (s, c) => (
                sources.Contains("global") ? await global.ByGtinAsync(s, gtin.Normalized14, c) : [],
                askProviders ? await global.CachedAsync(s, gtin.Normalized14, DateTimeOffset.UtcNow.AddDays(-30), c) : []), ct);
            candidates.AddRange(fromGlobal);
            var records = cached.ToList();
            List<ICatalogProvider> toAsk = askProviders ? [.. providers.Where(p => records.All(r => r.Source != p.Source))] : [];
            if (toAsk.Count > 0)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(TimeSpan.FromSeconds(4));   // AT-203: the answer comes within 5 seconds whatever the providers do
                var answers = await Task.WhenAll(toAsk.Select(async p =>
                {
                    try { return (Provider: p, Lookup: await p.LookupAsync(gtin.Normalized14, budget.Token)); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (Provider: p, Lookup: ProviderLookup.Failure); }
                }));
                degraded = answers.Any(a => a.Lookup.Failed);
                List<ProviderRecord> fresh = [.. answers.Where(a => !a.Lookup.Failed).Select(a => new ProviderRecord(a.Provider.Source, gtin.Normalized14,
                    a.Lookup.Found, a.Lookup.Fields ?? new CandidateFields(null, null, null, null, null),
                    a.Lookup.Fields is { } f ? CandidateRules.ProviderConfidence(f) : 0m, a.Provider.License, a.Provider.Attribution, DateTimeOffset.UtcNow))];
                if (fresh.Count > 0)
                {
                    await uow.ExecuteAsync(ctx, async (s, c) => { foreach (ProviderRecord r in fresh) await global.SaveAsync(s, r, c); return 0; }, ct);
                }

                records.AddRange(fresh);
            }

            candidates.AddRange(records.Where(r => r.Found && r.Fields.Name is not null)
                .Select(r => new CatalogCandidate(r.Source, r.Confidence, r.FetchedAt, r.License, r.Attribution, r.Fields, null)));
        }

        IReadOnlyList<IReadOnlyList<int>> conflicts = CandidateRules.Conflicts(candidates);
        var response = new Dictionary<string, object?>
        {
            ["kind"] = result.match is not null ? "known" : candidates.Count > 0 ? "candidates" : "not_found",
            ["normalized"] = normalized,
            // AT-202: a candidate is global reference data or a provider's: never another store's product, price or stock
            ["candidates"] = candidates.Select((c, i) => new
            {
                c.Source, confidence = c.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), c.LastVerifiedAt, c.License,
                c.Attribution, c.CatalogProductId,
                fields = new { name = c.Fields.Name, brand = c.Fields.Brand, category = c.Fields.Category, netContent = c.Fields.NetContent, imageUrl = c.Fields.ImageUrl },
                conflictsWith = conflicts[i],
                // CAT-004.1: one unopposed candidate at or above 0.8 may be pre-selected; the save is still the user's confirmation
                preselect = c.Confidence >= CandidateRules.Threshold && conflicts[i].Count == 0 && candidates.Count(x => x.Confidence >= CandidateRules.Threshold) == 1,
            }),
            ["degraded"] = degraded,
        };
        if (result.match is { } m)
        {
            response["tenantProduct"] = new
            {
                productId = m.ProductId, variantId = m.VariantId, name = m.Name, sku = m.Sku,
                unitPrice = m.UnitPrice?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                discount = result.rule is { } r ? new { ruleId = r.Id, percent = r.PercentBp, name = r.Name } : null,
            };
        }

        return Results.Ok(response);
    })
    .RequirePermission(Permissions.CatalogProductManage)
    .AllowWhenReadOnly()
    .RequireRateLimiting(RateLimits.CatalogResolve);

// ---------------- US-044 / CAT-006: merging a duplicate product into another, and CAT-005.1: suggesting a product to the
// global catalog (opt-in, descriptive fields and public barcodes only)
v1.MapIdempotentPost<ProductMergeInput>("/products/{productId:guid}/merge", "catalog.merge-product", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        Guid productId = Guid.Parse((string)http.GetRouteValue("productId")!, System.Globalization.CultureInfo.InvariantCulture);
        var errors = new Dictionary<string, string[]>();
        if (body.TargetProductId is null) errors["targetProductId"] = ["required"];
        if (body.ExpectedVersion is null) errors["expectedVersion"] = ["required"];
        string? reason = body.Reason?.Trim();
        if (reason is not { Length: >= 3 and <= 200 }) errors["reason"] = ["3-200 characters"];
        if (errors.Count > 0) return CommandResult.Fail(Problems.Validation(http, errors));
        if (body.TargetProductId == productId)
        {
            return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { ["targetProductId"] = ["another product"] }));
        }

        IProductCatalog catalog = http.RequestServices.GetRequiredService<IProductCatalog>();
        if (await catalog.GetAsync(scope, productId, ct) is not { } source) return CommandResult.Fail(Problems.NotFound(http));
        // stock is the ledger's: a merge never moves it silently; transfer or adjust it first (INV-002)
        IStockLedger ledger = http.RequestServices.GetRequiredService<IStockLedger>();
        var holding = new List<string>();
        foreach (VariantView v in source.Variants)
        {
            if ((await ledger.BalancesAsync(scope, null, v.Id, ct)).Any(b => b.OnHand != 0 || b.Reserved != 0)) holding.Add(v.Sku);
        }

        if (holding.Count > 0)
        {
            return CommandResult.Fail(Problems.WithContext(http, StatusCodes.Status409Conflict, "merge-source-has-stock",
                "Adjust or transfer the duplicate's stock to zero first", new { skus = holding }));
        }

        MergeResult merged = await catalog.MergeAsync(scope, productId, body.TargetProductId!.Value, body.ExpectedVersion!.Value, ct);
        IResult? refused = merged.Outcome switch
        {
            MergeOutcome.Done => null,
            MergeOutcome.NotFound => Problems.NotFound(http),
            MergeOutcome.SameProduct => Problems.Validation(http, new Dictionary<string, string[]> { ["targetProductId"] = ["another product"] }),
            MergeOutcome.AlreadyMerged => Problems.Create(http, StatusCodes.Status409Conflict, "product-merged", "One of the products is already merged"),
            MergeOutcome.VersionConflict => Problems.Create(http, StatusCodes.Status409Conflict, "version-conflict", "The product changed; reload it"),
            MergeOutcome.UnitMismatch => Problems.WithContext(http, StatusCodes.Status409Conflict, "base-unit-mismatch",
                "Only products with the same base unit merge (CAT-006)", new { units = merged.Detail }),
            _ => Problems.WithContext(http, StatusCodes.Status409Conflict, "merge-variant-mismatch",
                "Every variant of the duplicate needs a variant with the same axis values in the target", new { sku = merged.Detail }),
        };
        if (refused is not null) return CommandResult.Fail(refused);
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("catalog.product.merged", "Product", productId.ToString(), reason,
            new { into = body.TargetProductId, barcodes = merged.MovedBarcodes, sourceName = source.Name }), ct);
        return CommandResult.Ok(ProductJson(merged.Target!));
    })
    .RequirePermission(Permissions.CatalogProductManage);

v1.MapPost("/products/{productId:guid}/catalog-suggestions", async (Guid productId, CatalogSuggestInput body, RequestContext ctx, IUnitOfWork uow,
        IProductCatalog catalog, IGlobalCatalog global, HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        // CAT-005.1: the store opts in explicitly, each time; nothing is sent on its behalf
        if (body.Consent != true) errors["consent"] = ["required: true (CAT-005.1 opt-in)"];
        string? category = string.IsNullOrWhiteSpace(body.Category) ? null : body.Category.Trim();
        if (category is { Length: > 100 }) errors["category"] = ["up to 100 characters"];
        if (errors.Count > 0) return Problems.Validation(http, errors);
        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            if (await catalog.GetAsync(s, productId, c) is not { Status: not "Merged" } product) return Problems.NotFound(http);
            string[] codes = [.. product.Variants.SelectMany(v => v.Barcodes)
                .Where(b => b.Active && !b.IsInternal && Gtin.TryParse(b.CodeNormalized, out Gtin g) && !g.IsRestrictedCirculation)
                .Select(b => b.CodeNormalized).Distinct()];
            if (codes.Length == 0)
            {
                return Problems.Create(http, StatusCodes.Status422UnprocessableEntity, "no-public-barcode",
                    "Only a product with a public (GS1) barcode can be suggested; internal and weighed codes stay in the store");
            }

            byte[] source = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("catalog-suggestion|" + ctx.ActiveTenant!.Value.Value));
            var sent = new List<string>();
            foreach (string code in codes)
            {
                if (await global.SuggestAsync(s, code, product.Name, product.Brand, category, source, c)) sent.Add(code);
            }

            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("catalog.product.suggested", "Product", productId.ToString(), null,
                new { codes = sent, already = codes.Except(sent) }), c);
            return Results.Accepted(value: new { submitted = sent, alreadySubmitted = codes.Except(sent) });
        }, ct);
    })
    .RequirePermission(Permissions.CatalogProductManage);

// ---------------- US-047 / CAT-005: the CatalogModerator's queue of store suggestions (global catalog only, never store data)
RouteGroupBuilder catalogModeration = app.MapGroup("/api/v1/platform/catalog").RequireAuthorization(ServiceCollectionExtensions.PlatformCatalogPolicy);

catalogModeration.MapGet("/suggestions", async (IUnitOfWork uow, IGlobalCatalog global, HttpContext http, string? cursor, int? limit,
        CancellationToken ct) =>
    {
        int size = limit ?? 50;
        if (size is < 1 or > 200) return Problems.Validation(http, new Dictionary<string, string[]> { ["limit"] = ["1-200"] });
        var (items, next) = await uow.ExecuteWithoutTenantAsync(null, (s, c) => global.PendingAsync(s, cursor, size, c), ct);
        return Results.Ok(new
        {
            items = items.Select(x => new
            {
                x.Id, x.Gtin14, fields = new { x.Name, x.Brand, x.Category }, x.SubmittedAt, storesForBarcode = x.StoresForBarcode,
                existing = x.Existing is { } e ? new { e.CatalogProductId, e.Fields.Name, e.Fields.Brand, e.Fields.Category, verifiedAt = e.LastVerifiedAt } : null,
            }),
            page = new { nextCursor = next, hasMore = next is not null },
        });
    });

catalogModeration.MapPost("/suggestions/{suggestionId:guid}/decision", async (Guid suggestionId, CatalogReviewInput body, IUnitOfWork uow,
        IGlobalCatalog global, HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        bool? publish = body.Decision switch { "Publish" => true, "Reject" => false, _ => null };
        if (publish is null) errors["decision"] = ["Publish or Reject"];
        string? reason = body.Reason?.Trim();
        if (reason is not { Length: >= 3 and <= 500 }) errors["reason"] = ["3-500 characters"];
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        CandidateFields? edits = body.Edits is { } ed ? new CandidateFields(Clean(ed.Name), Clean(ed.Brand), Clean(ed.Category), null, null) : null;
        if (edits?.Name is { Length: > 200 }) errors["edits.name"] = ["at most 200 characters"];
        if (edits?.Brand is { Length: > 100 }) errors["edits.brand"] = ["at most 100 characters"];
        if (edits?.Category is { Length: > 100 }) errors["edits.category"] = ["at most 100 characters"];
        if (publish == false && edits is not null) errors["edits"] = ["only when publishing"];
        if (errors.Count > 0) return Problems.Validation(http, errors);
        string moderator = OperatorOf(http);
        var (outcome, productId) = await uow.ExecuteWithoutTenantAsync(null,
            (s, c) => global.DecideAsync(s, suggestionId, publish!.Value, edits, reason!, moderator, c), ct);
        switch (outcome)
        {
            case ModerationOutcome.NotFound:
                return Problems.NotFound(http);
            case ModerationOutcome.AlreadyDecided:
                return Problems.Create(http, StatusCodes.Status409Conflict, "already-decided", "This suggestion was already decided");
        }

        PlatformLog.SuggestionDecided(platformLogger, suggestionId, outcome == ModerationOutcome.Published ? "Published" : "Rejected", productId, moderator);
        return Results.NoContent();
    });

// ---------------- US-162 / INT-001: partner integration keys (read scopes only in the MVP) and the partner API they open
object KeyJson(IntegrationKeyView k, string? secret = null) => new
{
    k.Id, k.Name, k.Prefix, k.Scopes, k.CreatedAt, k.ExpiresAt, k.RevokedAt, k.LastUsedAt,
    status = k.RevokedAt is not null ? "Revoked" : k.ExpiresAt <= DateTimeOffset.UtcNow ? "Expired" : "Active", secret,
};

v1.MapGet("/integrations/keys", async (RequestContext ctx, IUnitOfWork uow, IIntegrationKeys keys, CancellationToken ct) =>
        Results.Ok(new { items = (await uow.ExecuteAsync(ctx, (s, c) => keys.ListAsync(s, c), ct)).Select(k => KeyJson(k)) }))
    .RequirePermission(Permissions.IntegrationsManage);

v1.MapPost("/integrations/keys", async (IntegrationKeyInput body, RequestContext ctx, IUnitOfWork uow, IIntegrationKeys keys, HttpContext http,
        CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        string? name = body.Name?.Trim();
        if (name is not { Length: >= 1 and <= 100 }) errors["name"] = ["1-100 characters"];
        string[] scopes = [.. (body.Scopes ?? []).Distinct(StringComparer.Ordinal)];
        if (scopes.Length == 0 || scopes.Any(x => !IntegrationKeyRules.Scopes.Contains(x))) errors["scopes"] = ["catalog.read and/or inventory.read"];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (body.ExpiresAt is not { } expires || expires <= now.AddMinutes(5) || expires > now.AddDays(IntegrationKeyRules.MaxDays))
        {
            errors["expiresAt"] = [$"between now and {IntegrationKeyRules.MaxDays} days ahead"];
        }

        if (errors.Count > 0) return Problems.Validation(http, errors);
        var (prefix, key, hash) = IntegrationKeyRules.New();
        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            IntegrationKeyView created = await keys.CreateAsync(s, prefix, hash, name!, scopes, body.ExpiresAt!.Value, ctx.User!.Value.Value, c);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("integrations.key.created", "IntegrationKey", created.Id.ToString(), null,
                new { created.Name, created.Prefix, created.Scopes, created.ExpiresAt }), c);
            return Results.Created($"/api/v1/integrations/keys/{created.Id}", KeyJson(created, key));   // the only time the key is shown
        }, ct);
    })
    .RequirePermission(Permissions.IntegrationsManage)
    .RequireRecentMfa();   // ABAC-05

v1.MapDelete("/integrations/keys/{keyId:guid}", async (Guid keyId, RequestContext ctx, IUnitOfWork uow, IIntegrationKeys keys, HttpContext http,
        CancellationToken ct) =>
        await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            if (!await keys.RevokeAsync(s, keyId, ctx.User!.Value.Value, c)) return Problems.NotFound(http);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("integrations.key.revoked", "IntegrationKey", keyId.ToString()), c);
            return Results.NoContent();
        }, ct))
    .RequirePermission(Permissions.IntegrationsManage)
    .RequireRecentMfa();   // ABAC-05

// the partner API: no user, no session — the X-Api-Key alone opens one store, for the scopes it was given (TM-071: never a JWT)
RouteGroupBuilder partner = app.MapGroup("/api/v1/partner").AllowAnonymous().RequireRateLimiting(RateLimits.Partner);
partner.AddEndpointFilter(async (fc, next) =>
{
    HttpContext http = fc.HttpContext;
    string? need = http.GetEndpoint()?.Metadata.GetMetadata<PartnerScope>()?.Scope;
    if (IntegrationKeyRules.Parse(http.Request.Headers["X-Api-Key"]) is not { } presented)
    {
        return Problems.Create(http, StatusCodes.Status401Unauthorized, "invalid-api-key", "A valid X-Api-Key is required");
    }

    IUnitOfWork uow = http.RequestServices.GetRequiredService<IUnitOfWork>();
    IntegrationKeyTarget? target = await uow.ExecuteWithoutTenantAsync(null,
        (s, c) => http.RequestServices.GetRequiredService<IIntegrationKeys>().FindAsync(s, presented.Prefix, c), http.RequestAborted);
    if (target is null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(target.SecretHash, IntegrationKeyRules.Hash(presented.Secret)))
    {
        return Problems.Create(http, StatusCodes.Status401Unauthorized, "invalid-api-key", "A valid X-Api-Key is required");   // unknown, wrong, revoked or expired alike
    }

    if (need is null || !target.Scopes.Contains(need))
    {
        return Problems.Create(http, StatusCodes.Status403Forbidden, "scope-missing", $"This key does not have {need}");
    }

    http.Items[typeof(IntegrationKeyTarget)] = target;
    return await next(fc);
});
IntegrationKeyTarget KeyOf(HttpContext http) => (IntegrationKeyTarget)http.Items[typeof(IntegrationKeyTarget)]!;

partner.MapGet("/catalog/products", async (IUnitOfWork uow, IProductCatalog catalog, IIntegrationKeys keys, HttpContext http, string? cursor, int? limit,
        CancellationToken ct) =>
    {
        int size = limit ?? 50;
        if (size is < 1 or > 200) return Problems.Validation(http, new Dictionary<string, string[]> { ["limit"] = ["1-200"] });
        IntegrationKeyTarget key = KeyOf(http);
        var (items, next) = await uow.ExecuteAsync(new FixedTenantContext(new TenantId(key.TenantId)), async (s, c) =>
        {
            await keys.TouchAsync(s, key.KeyId, c);
            return await catalog.SearchAsync(s, null, null, cursor, size, c);
        }, ct);
        return Results.Ok(new { items = items.Select(ProductJson), page = new { nextCursor = next, hasMore = next is not null } });
    })
    .WithMetadata(new PartnerScope("catalog.read"));

partner.MapGet("/inventory/balances", async (IUnitOfWork uow, IStockLedger ledger, IIntegrationKeys keys, HttpContext http, Guid? warehouseId,
        Guid? variantId, CancellationToken ct) =>
    {
        IntegrationKeyTarget key = KeyOf(http);
        var rows = await uow.ExecuteAsync(new FixedTenantContext(new TenantId(key.TenantId)), async (s, c) =>
        {
            await keys.TouchAsync(s, key.KeyId, c);
            return await ledger.BalancesAsync(s, warehouseId, variantId, c);
        }, ct);
        // inventory.read is quantities only: never the cost (WAC) or the value (INT-001)
        return Results.Ok(new
        {
            items = rows.Select(b => new { b.WarehouseId, b.VariantId, onHand = Qty(b.OnHand), reserved = Qty(b.Reserved), available = Qty(b.OnHand - b.Reserved) }),
            page = new { nextCursor = (string?)null, hasMore = false },
        });
    })
    .WithMetadata(new PartnerScope("inventory.read"));

// ---------------- US-061: stock ledger (INV-001..006): balances, card, negative-stock policy
v1.MapGet("/warehouses", async (RequestContext ctx, IUnitOfWork uow, CancellationToken ct) =>
        Results.Ok(new
        {
            items = await uow.ExecuteAsync(ctx, async (s, c) =>
            {
                await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT id, branch_id, code, kind FROM inv.warehouse ORDER BY code");
                await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(c);
                var list = new List<object>();
                while (await r.ReadAsync(c)) list.Add(new { id = r.GetGuid(0), branchId = r.GetGuid(1), code = r.GetString(2), kind = r.GetString(3) });
                return list;
            }, ct),
        }))
    .RequireTenantMember();

string Qty(decimal q) => q.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

v1.MapGet("/inventory/balances", async (RequestContext ctx, IUnitOfWork uow, IStockLedger ledger, HttpContext http, Guid? warehouseId, Guid? variantId,
        bool? belowReorderPoint, CancellationToken ct) =>
    {
        var rows = await uow.ExecuteAsync(ctx, (s, c) => ledger.BalancesAsync(s, warehouseId, variantId, c, belowReorderPoint == true), ct);
        // the low-stock list names its items, so it can be acted on (INV-010)
        var names = new Dictionary<Guid, SellableVariant>();
        if (belowReorderPoint == true)
        {
            IProductCatalog catalog = http.RequestServices.GetRequiredService<IProductCatalog>();
            names = await uow.ExecuteAsync(ctx, async (s, c) =>
            {
                var found = new Dictionary<Guid, SellableVariant>();
                foreach (Guid v in rows.Select(b => b.VariantId).Distinct())
                {
                    if (await catalog.SellableAsync(s, v, c) is { } sv) found[v] = sv;
                }

                return found;
            }, ct);
        }

        // TM-014: cost and value only for whoever may see reports for that warehouse's branch
        var lookups = http.RequestServices.GetServices<IScopeLookup>();
        var canSee = new Dictionary<Guid, bool>();
        foreach (Guid w in rows.Select(b => b.WarehouseId).Distinct())
        {
            ScopeRef? scope = await uow.ExecuteAsync(ctx, (s, c) => ResolveScope(s, lookups, ScopeType.Warehouse, w, c), ct);
            canSee[w] = scope is not null && AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.ReportsView, scope);
        }

        return Results.Ok(new
        {
            items = rows.Select(b => new
            {
                b.WarehouseId, b.VariantId, onHand = Qty(b.OnHand), reserved = Qty(b.Reserved), available = Qty(b.OnHand - b.Reserved),
                wac = canSee[b.WarehouseId] ? b.Wac.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                value = canSee[b.WarehouseId] ? b.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                reorderPoint = b.ReorderPoint is decimal rp ? Qty(rp) : null, belowReorderPoint = b.BelowReorderPoint,
                name = names.TryGetValue(b.VariantId, out SellableVariant? n) ? n.Name : null, sku = n?.Sku,
            }),
            page = new { nextCursor = (string?)null, hasMore = rows.Count == 500 },
        });
    })
    .RequireTenantMember();

v1.MapGet("/inventory/movements", async (RequestContext ctx, IUnitOfWork uow, IStockLedger ledger, HttpContext http, Guid? variantId, Guid? warehouseId,
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
    {
        if (variantId is null) return Problems.Validation(http, new Dictionary<string, string[]> { ["variantId"] = ["required"] });
        if (warehouseId is { } w)
        {
            ScopeRef? scope = await uow.ExecuteAsync(ctx, (s, c) => ResolveScope(s, http.RequestServices.GetServices<IScopeLookup>(), ScopeType.Warehouse, w, c), ct);
            if (scope is null) return Problems.NotFound(http);
            if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.ReportsView, scope)) return Forbidden(http);
        }
        else if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.ReportsView, ScopeRef.Tenant))
        {
            return Forbidden(http);   // every warehouse at once needs the store-wide permission
        }

        var rows = await uow.ExecuteAsync(ctx, (s, c) => ledger.MovementsAsync(s, variantId.Value, warehouseId, 200, c, from, to), ct);
        return Results.Ok(new
        {
            items = rows.Select(m => new
            {
                m.Id, m.RecordedAt, m.EffectiveAt, type = m.Type, qtyBase = Qty(m.QtyBase),
                unitCost = m.UnitCost.ToString(System.Globalization.CultureInfo.InvariantCulture),
                totalCost = m.TotalCost.ToString(System.Globalization.CultureInfo.InvariantCulture),
                m.SourceType, m.SourceId, m.SourceLine, enteredQty = m.EnteredQty is { } q ? Qty(q) : null, enteredUnit = m.EnteredUnit,
                unitFactor = m.UnitFactor is { } f ? Qty(f) : null,
            }),
            page = new { nextCursor = (string?)null, hasMore = rows.Count == 200 },
        });
    })
    .RequirePermission(Permissions.ReportsView);

// INV-010 (US-066): the point at which an item of a warehouse is low; null removes it. Set by who receives goods there.
v1.MapPut("/inventory/reorder-points", async (ReorderPointInput body, RequestContext ctx, IUnitOfWork uow, IStockLedger ledger, IProductCatalog catalog,
        HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        if (body.WarehouseId is null) errors["warehouseId"] = ["required"];
        if (body.VariantId is null) errors["variantId"] = ["required"];
        decimal? point = null;
        if (body.ReorderPoint is not null)
        {
            if (body.ReorderPoint == "0") point = 0;
            else if (!ProductRules.TryFactor(body.ReorderPoint, out decimal p)) errors["reorderPoint"] = ["0 or a positive decimal in the base unit, up to 6 places; null removes it"];
            else point = p;
        }

        if (errors.Count > 0) return Problems.Validation(http, errors);
        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            if (await ResolveScope(s, http.RequestServices.GetServices<IScopeLookup>(), ScopeType.Warehouse, body.WarehouseId, c) is not { } warehouse)
            {
                return Problems.NotFound(http);
            }

            if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.InventoryReceiptCreate, warehouse)) return Forbidden(http);
            if (!(await catalog.ExistingVariantsAsync(s, [body.VariantId!.Value], c)).Contains(body.VariantId.Value)) return Problems.NotFound(http);
            await ledger.SetReorderPointAsync(s, body.WarehouseId!.Value, body.VariantId.Value, point, ctx.User!.Value.Value, c);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("inventory.reorder-point.set", "ReorderPoint", $"{body.WarehouseId}/{body.VariantId}",
                Details: new { body.WarehouseId, body.VariantId, reorderPoint = point is decimal v ? Qty(v) : null }), c);
            return Results.NoContent();
        }, ct);
    })
    .RequirePermission(Permissions.InventoryReceiptCreate);

v1.MapPut("/inventory/negative-stock-policies", async (NegativeStockPolicyInput body, RequestContext ctx, IUnitOfWork uow, IStockLedger ledger,
        HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        if (body.ScopeType is not ("Tenant" or "Branch" or "Variant")) errors["scopeType"] = ["Tenant|Branch|Variant"];
        if (body.ScopeType is "Branch" or "Variant" && body.ScopeId is null) errors["scopeId"] = ["required for Branch and Variant"];
        if (body.ScopeType is "Tenant" && body.ScopeId is not null) errors["scopeId"] = ["not for Tenant"];
        if (!Enum.TryParse(body.Policy, out NegativeStockPolicy policy) || !Enum.IsDefined(policy)) errors["policy"] = ["Forbid|AllowWithWarning"];
        if (errors.Count > 0) return Problems.Validation(http, errors);
        Guid scopeId = body.ScopeId ?? ctx.ActiveTenant!.Value.Value;
        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            if (body.ScopeType == "Branch" && await ResolveScope(s, http.RequestServices.GetServices<IScopeLookup>(), ScopeType.Branch, scopeId, c) is null)
            {
                return Problems.NotFound(http);
            }

            await ledger.SetPolicyAsync(s, body.ScopeType!, scopeId, policy, ctx.User!.Value.Value, c);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("inventory.negative-stock-policy.set", "NegativeStockPolicy", $"{body.ScopeType}/{scopeId}",
                Details: new { body.ScopeType, scopeId, policy = policy.ToString() }), c);
            return Results.NoContent();
        }, ct);
    })
    .RequirePermission(Permissions.TenantSettingsManage);

// ---------------- US-063: manual stock adjustments with a reason (INV-002), an approval above the threshold (BR-033, AB-05)
// and their journal entry in the same transaction (D02 §5.8.1). BR-033 [ASM]: a tenant setting later; until then
// Inventory:AdjustmentApprovalThreshold (rials, default 50,000,000).
long adjustmentThreshold = builder.Configuration.GetValue<long?>("Inventory:AdjustmentApprovalThreshold") ?? AdjustmentRules.DefaultApprovalThreshold;

static string ReasonFa(string reason) => reason switch
{
    "Damage" => "آسیب", "Expiry" => "انقضا", "Theft" => "سرقت", "CountError" => "خطای شمارش", _ => "سایر",
};

object AdjustmentJson(AdjustmentRecord a, IReadOnlyList<string>? warnings = null) => new
{
    a.Id, a.WarehouseId, a.Reason, a.Note, a.LineCount, value = Rial(a.Value), shrinkage = Rial(a.Shrinkage), overage = Rial(a.Overage),
    a.JournalEntryId, a.ApprovalId, a.CreatedBy, a.PostedAt, warnings = warnings ?? [],
};

// INV-002 / BR-033 / D02 §5.8.1 (US-063, US-065): one adjustment in the caller's transaction: the movements first (valued at the
// WAC of the moment under the balances' locks, INV-004/006), then an approval when <needsApproval> says so for that value (D-61:
// bound to this exact adjustment and value; a refusal rolls the movements back), then the entry and the audited header.
async Task<(CommandResult? Failed, AdjustmentRecord? Record, IReadOnlyList<string> Warnings)> PostAdjustment(HttpContext http, IUnitOfWorkScope scope,
    RequestContext ctx, Guid id, Guid warehouseId, ScopeRef warehouse, string reason, string? note, IReadOnlyList<AdjustmentLine> lines, MovementType? type,
    Func<long, (string Condition, string? Limit, string Binding)?> needsApproval, CancellationToken ct)
{
    DateTimeOffset now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
    StockResult posted = await http.RequestServices.GetRequiredService<IStockLedger>().PostAsync(scope, new StockDocument("StockAdjustment", id, now,
        [.. lines.Select((l, i) => new StockLine(i + 1, warehouseId, l.VariantId, type ?? AdjustmentRules.TypeOf(l.QtyDelta), l.QtyDelta))]), ct);
    if (posted.Outcome == StockOutcome.InsufficientStock)
    {
        return (CommandResult.Fail(Problems.WithContext(http, StatusCodes.Status409Conflict, "insufficient-stock", "Not enough stock", new
        {
            shortages = posted.Shortages.Select(s => new { s.WarehouseId, s.VariantId, available = Qty(s.Available), requested = Qty(s.Requested) }),
        })), null, []);
    }

    if (posted.Outcome != StockOutcome.Done) throw new InvalidOperationException($"adjustment {id}: {posted.Outcome}");
    (long value, long shrinkage, long overage) = AdjustmentRules.ValueOf(posted.Movements);

    Guid? approvalId = null;
    if (needsApproval(value) is { } need)
    {
        var ask = new ApprovalAsk(Permissions.InventoryAdjust, "StockAdjustment", need.Binding, warehouse, need.Condition, Rial(value), need.Limit,
            $"{ReasonFa(reason)}{(note is null ? "" : ": " + note)}");
        IApprovals approvals = http.RequestServices.GetRequiredService<IApprovals>();
        bool approved = Guid.TryParse(http.Request.Headers["X-Approval-Id"], out Guid given)
            && await approvals.ConsumeAsync(scope, given, ask, ctx.User!.Value, ct);
        if (!approved)
        {
            if (given != Guid.Empty && await approvals.GetAsync(scope, given, ct) is { Status: "Pending" } open
                && open.RequestedBy == ctx.User!.Value.Value && open.ResourceId == ask.ResourceId && open.Value == ask.Value)
            {
                return (CommandResult.Fail(Problems.ApprovalRequired(http, given, ask.Condition, ask.Value, ask.Limit)), null, []);
            }

            Guid requested = await http.RequestServices.GetRequiredService<IUnitOfWork>()
                .ExecuteAsync(ctx, (s, c) => approvals.RequestAsync(s, ask, ctx.User!.Value, c), ct);
            return (CommandResult.Fail(Problems.ApprovalRequired(http, requested, ask.Condition, ask.Value, ask.Limit)), null, []);
        }

        approvalId = given;
    }

    Guid? journalId = null;
    if (InventoryAdjustmentRule.For(id, shrinkage, overage, now, ReasonFa(reason)) is { } draft)
    {
        AccountingResult<JournalView> entry = await http.RequestServices.GetRequiredService<IJournals>().PostAutomaticAsync(scope, draft, ct);
        if (entry.Outcome != AccountingOutcome.Done) return (CommandResult.Fail(AutomaticEntryProblem(http, entry)), null, []);
        journalId = entry.Value!.Id;
    }

    AdjustmentRecord record = await http.RequestServices.GetRequiredService<IStockAdjustments>().RecordAsync(scope, new AdjustmentRecord(id, warehouseId,
        reason, note, lines.Count, value, shrinkage, overage, journalId, approvalId, ctx.User!.Value.Value, now), ct);
    await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("inventory.adjustment.posted", "StockAdjustment", id.ToString(), note, new
    {
        warehouseId, reason, value, shrinkage, overage, journalId, approvalId, type = type?.ToString(),
        lines = lines.Take(50).Select(l => new { l.VariantId, qtyDelta = Qty(l.QtyDelta) }),
    }), ct);
    return (null, record, posted.Warnings);
}

v1.MapIdempotentPost<AdjustmentInput>("/inventory/adjustments", "inventory.post-adjustment", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        var errors = new Dictionary<string, string[]>();
        if (body.WarehouseId is null) errors["warehouseId"] = ["required"];
        if (body.Reason is null || !AdjustmentRules.Reasons.Contains(body.Reason)) errors["reason"] = ["Damage|Expiry|Theft|CountError|Other"];
        string? note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
        if (note is { Length: > AdjustmentRules.MaxNote }) errors["note"] = [$"up to {AdjustmentRules.MaxNote} characters"];
        else if (body.Reason == "Other" && note is null) errors["note"] = ["required when the reason is Other"];
        var lines = new List<AdjustmentLine>();
        if (body.Lines is not { Length: >= 1 and <= AdjustmentRules.MaxLines }) errors["lines"] = [$"1-{AdjustmentRules.MaxLines} lines"];
        for (int i = 0; i < (body.Lines?.Length ?? 0) && body.Lines!.Length <= AdjustmentRules.MaxLines; i++)
        {
            AdjustmentLineInput? l = body.Lines[i];
            if (l?.VariantId is null) errors[$"lines[{i}].variantId"] = ["required"];
            if (!AdjustmentRules.TryDelta(l?.QtyDelta, out decimal delta)) errors[$"lines[{i}].qtyDelta"] = ["signed non-zero decimal in the base unit, up to 6 places"];
            else if (l?.VariantId is { } v) lines.Add(new AdjustmentLine(v, delta));
        }

        if (lines.Select(l => l.VariantId).Distinct().Count() != lines.Count) errors["lines"] = ["one line per variant"];
        if (errors.Count > 0) return CommandResult.Fail(Problems.Validation(http, errors));

        Guid warehouseId = body.WarehouseId!.Value;
        if (await ResolveScope(scope, http.RequestServices.GetServices<IScopeLookup>(), ScopeType.Warehouse, warehouseId, ct) is not { } warehouse)
        {
            return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { ["warehouseId"] = ["not a warehouse of this store"] }));
        }

        if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.InventoryAdjust, warehouse)) return CommandResult.Fail(Forbidden(http));
        IReadOnlySet<Guid> known = await http.RequestServices.GetRequiredService<IProductCatalog>().ExistingVariantsAsync(scope, [.. lines.Select(l => l.VariantId)], ct);
        if (lines.FindIndex(l => !known.Contains(l.VariantId)) is var unknown and >= 0)
        {
            return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { [$"lines[{unknown}].variantId"] = ["not a variant of this store"] }));
        }

        string reason = body.Reason!;
        var (failed, record, warnings) = await PostAdjustment(http, scope, ctx, Guid.CreateVersion7(), warehouseId, warehouse, reason, note, lines, null,
            v => AdjustmentRules.NeedsApproval(v, adjustmentThreshold)
                ? ("value-threshold", Rial(adjustmentThreshold), AdjustmentRules.Binding(warehouseId, reason, lines))
                : null, ct);
        return failed ?? CommandResult.Created(AdjustmentJson(record!, warnings));
    })
    .RequirePermission(Permissions.InventoryAdjust);

// AB-05 detection: the adjustments and their value, for whoever may see reports of each warehouse's branch (TM-014)
v1.MapGet("/inventory/adjustments", async (RequestContext ctx, IUnitOfWork uow, IStockAdjustments adjustments, HttpContext http, CancellationToken ct) =>
    {
        var lookups = http.RequestServices.GetServices<IScopeLookup>();
        return Results.Ok(await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var rows = await adjustments.ListAsync(s, null, c);
            var canSee = new Dictionary<Guid, bool>();
            foreach (Guid w in rows.Select(a => a.WarehouseId).Distinct())
            {
                canSee[w] = await ResolveScope(s, lookups, ScopeType.Warehouse, w, c) is { } scope
                            && AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.ReportsView, scope);
            }

            return new { items = rows.Where(a => canSee[a.WarehouseId]).Select(a => AdjustmentJson(a)) };
        }, ct));
    })
    .RequirePermission(Permissions.ReportsView);

// ---------------- US-071: suppliers (PRC-001) and goods receipts (PRC-002, UC-05): movements, WAC, balances and the entry
// «Inventory + InputVat / the supplier's payable» in one transaction.
object SupplierJson(SupplierRecord s, long balance) => new
{
    s.Id, s.Name, s.NationalId, s.EconomicCode, s.Phone, balance = Rial(balance),
};

object ReceiptJson(ReceiptRecord r, IReadOnlyList<PostedMovement>? posted = null, IReadOnlyList<string>? warnings = null) => new
{
    r.Id, r.SupplierId, r.WarehouseId, r.SupplierInvoiceNo, r.EffectiveAt, netTotal = Rial(r.NetTotal), inputVat = Rial(r.InputVat), total = Rial(r.Total),
    lines = r.Lines.Select(l => new
    {
        l.LineNo, l.VariantId, qty = Qty(l.Qty), l.Unit, unitFactor = Qty(l.UnitFactor), qtyBase = Qty(l.QtyBase), unitCost = Rial(l.UnitCost),
        discount = Rial(l.Discount), inputVat = Rial(l.InputVat), lineCost = Rial(l.LineCost),
        newWac = posted?.FirstOrDefault(m => m.Line == l.LineNo) is { } m ? Rial(m.WacAfter) : null,
    }),
    r.JournalEntryId, r.CreatedBy, r.PostedAt, warnings = warnings ?? [],
};

v1.MapGet("/suppliers", async (RequestContext ctx, IUnitOfWork uow, IProcurement procurement, IJournals journals, string? q, CancellationToken ct) =>
        Results.Ok(await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var suppliers = await procurement.SuppliersAsync(s, string.IsNullOrWhiteSpace(q) ? null : q.Trim(), c);
            var balances = await journals.DetailBalancesAsync(s, "TradePayables", "Supplier", [.. suppliers.Select(x => x.Id)], c);
            return new
            {
                items = suppliers.Select(x => SupplierJson(x, balances.GetValueOrDefault(x.Id))),
                page = new { nextCursor = (string?)null, hasMore = suppliers.Count == 200 },
            };
        }, ct)))
    .RequireTenantMember();

v1.MapPost("/suppliers", async (SupplierInput body, RequestContext ctx, IUnitOfWork uow, IProcurement procurement, HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        string name = body.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 200) errors["name"] = ["1-200 characters"];
        string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        string? nationalId = Blank(body.NationalId), economicCode = Blank(body.EconomicCode), phone = Blank(body.Phone);
        if (!ReceiptRules.ValidNationalId(nationalId)) errors["nationalId"] = ["10 digits (person) or 11 digits (legal entity)"];
        if (!ReceiptRules.ValidEconomicCode(economicCode)) errors["economicCode"] = ["12-14 digits"];
        if (!ReceiptRules.ValidPhone(phone)) errors["phone"] = ["8-15 digits, optionally starting with +"];
        if (errors.Count > 0) return Problems.Validation(http, errors);
        if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.InventoryReceiptCreate, ScopeRef.Tenant)) return Forbidden(http);
        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var (outcome, supplier) = await procurement.CreateSupplierAsync(s, name, nationalId, economicCode, phone, ctx.User!.Value.Value, c);
            if (outcome == SupplierOutcome.DuplicateNationalId)
            {
                return Problems.Create(http, StatusCodes.Status409Conflict, "duplicate-national-id", "A supplier with this national id already exists");
            }

            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("procurement.supplier.created", "Supplier", supplier!.Id.ToString(),
                Details: new { supplier.Name, supplier.NationalId, supplier.EconomicCode }), c);
            return Results.Created($"/api/v1/suppliers/{supplier.Id}", SupplierJson(supplier, 0));
        }, ct);
    })
    .RequirePermission(Permissions.InventoryReceiptCreate);

v1.MapIdempotentPost<ReceiptInput>("/inventory/receipts", "inventory.post-goods-receipt", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        var errors = new Dictionary<string, string[]>();
        if (body.SupplierId is null) errors["supplierId"] = ["required"];
        if (body.WarehouseId is null) errors["warehouseId"] = ["required"];
        string? invoiceNo = string.IsNullOrWhiteSpace(body.SupplierInvoiceNo) ? null : body.SupplierInvoiceNo.Trim();
        if (invoiceNo is { Length: > 40 }) errors["supplierInvoiceNo"] = ["up to 40 characters"];
        if (body.EffectiveDate is not null) errors["effectiveDate"] = ["not-yet-supported"];   // a back-dated receipt: with the accountant (OQ-05)
        string[] confirmed = body.ConfirmedWarnings ?? [];
        var inputs = new List<(Guid Variant, decimal Qty, string Unit, long UnitCost, long Discount, long Vat)>();
        if (body.Lines is not { Length: >= 1 and <= ReceiptRules.MaxLines }) errors["lines"] = [$"1-{ReceiptRules.MaxLines} lines"];
        for (int i = 0; i < (body.Lines?.Length ?? 0) && body.Lines!.Length <= ReceiptRules.MaxLines; i++)
        {
            ReceiptLineInput? l = body.Lines[i];
            bool ok = true;
            if (l?.VariantId is null) { errors[$"lines[{i}].variantId"] = ["required"]; ok = false; }
            if (l?.Qty is null || !ProductRules.TryFactor(l.Qty, out decimal qty)) { errors[$"lines[{i}].qty"] = ["positive decimal, up to 6 places"]; ok = false; qty = 0; }
            if (!ProductRules.ValidUnit(l?.Unit)) { errors[$"lines[{i}].unit"] = ["1-10 of A-Z 0-9"]; ok = false; }
            if (!TryMoney(l?.UnitCost, positive: false, out long unitCost)) { errors[$"lines[{i}].unitCost"] = ["rials per unit as a whole-number string"]; ok = false; }
            long discount = 0, vat = 0;
            if (l?.Discount is not null && !TryMoney(l.Discount, positive: false, out discount)) { errors[$"lines[{i}].discount"] = ["rials as a whole-number string"]; ok = false; }
            if (l?.InputVat is not null && !TryMoney(l.InputVat, positive: false, out vat)) { errors[$"lines[{i}].inputVat"] = ["rials as a whole-number string"]; ok = false; }
            if (ok && ReceiptRules.LineCost(qty, unitCost, discount) < 0) { errors[$"lines[{i}].discount"] = ["not more than the line's amount"]; ok = false; }
            if (ok) inputs.Add((l!.VariantId!.Value, qty, l.Unit!, unitCost, discount, vat));
        }

        if (errors.Count > 0) return CommandResult.Fail(Problems.Validation(http, errors));

        Guid warehouseId = body.WarehouseId!.Value, supplierId = body.SupplierId!.Value;
        if (await ResolveScope(scope, http.RequestServices.GetServices<IScopeLookup>(), ScopeType.Warehouse, warehouseId, ct) is not { } warehouse)
        {
            return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { ["warehouseId"] = ["not a warehouse of this store"] }));
        }

        if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.InventoryReceiptCreate, warehouse)) return CommandResult.Fail(Forbidden(http));
        IProcurement procurement = http.RequestServices.GetRequiredService<IProcurement>();
        if (await procurement.SupplierAsync(scope, supplierId, ct) is not { } supplier)
        {
            return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { ["supplierId"] = ["not a supplier of this store"] }));
        }

        // BR-044: the unit must be the base unit or a packaging unit of the variant; its factor is taken now and kept
        var units = await http.RequestServices.GetRequiredService<IProductCatalog>().UnitsAsync(scope, [.. inputs.Select(x => x.Variant).Distinct()], ct);
        var lines = new List<ReceiptLine>();
        for (int i = 0; i < inputs.Count; i++)
        {
            var x = inputs[i];
            if (!units.TryGetValue(x.Variant, out VariantUnits? vu))
            {
                return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { [$"lines[{i}].variantId"] = ["not a variant of this store"] }));
            }

            if (!vu.Factors.TryGetValue(x.Unit, out decimal factor))
            {
                return CommandResult.Fail(Problems.WithContext(http, StatusCodes.Status422UnprocessableEntity, "unknown-unit",
                    "This unit has no conversion to the product's base unit", new { line = i, unit = x.Unit, units = vu.Factors.Keys }));
            }

            lines.Add(new ReceiptLine(i + 1, x.Variant, x.Qty, x.Unit, factor, x.UnitCost, x.Discount, x.Vat));
        }

        // UC-05: a zero cost and a repeated supplier invoice are confirmed explicitly, not refused
        IReadOnlyList<string> warnings = ReceiptRules.Warnings(lines, invoiceNo is not null && await procurement.InvoiceSeenAsync(scope, supplierId, invoiceNo, ct));
        if (warnings.Except(confirmed).Any())
        {
            return CommandResult.Fail(Problems.WithContext(http, StatusCodes.Status422UnprocessableEntity, "confirmation-required",
                "Confirm the warnings and send the receipt again", new { warnings = warnings.Except(confirmed) }));
        }

        Guid id = Guid.CreateVersion7();
        DateTimeOffset now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        StockResult posted = await http.RequestServices.GetRequiredService<IStockLedger>().PostAsync(scope, new StockDocument("GoodsReceipt", id, now,
            [.. lines.Select(l => new StockLine(l.LineNo, warehouseId, l.VariantId, MovementType.GoodsReceipt, l.Qty, l.LineCost,
                l.UnitFactor == 1 ? null : new EnteredUnit(l.Unit, l.UnitFactor)))]), ct);
        if (posted.Outcome != StockOutcome.Done) throw new InvalidOperationException($"goods receipt {id}: {posted.Outcome}");

        long net = lines.Sum(l => l.LineCost), vatTotal = lines.Sum(l => l.InputVat);
        Guid? journalId = null;
        if (GoodsReceiptRule.For(id, supplierId, net, vatTotal, now, $"رسید خرید از {supplier.Name}{(invoiceNo is null ? "" : $" (فاکتور {invoiceNo})")}") is { } draft)
        {
            AccountingResult<JournalView> entry = await http.RequestServices.GetRequiredService<IJournals>().PostAutomaticAsync(scope, draft, ct);
            if (entry.Outcome != AccountingOutcome.Done) return CommandResult.Fail(AutomaticEntryProblem(http, entry));
            journalId = entry.Value!.Id;
        }

        ReceiptRecord receipt = await procurement.RecordReceiptAsync(scope, new ReceiptRecord(id, supplierId, warehouseId, invoiceNo, now, lines, net, vatTotal,
            net + vatTotal, journalId, ctx.User!.Value.Value, now), ct);
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("inventory.goods-receipt.posted", "GoodsReceipt", id.ToString(), null, new
        {
            supplierId, warehouseId, invoiceNo, net, inputVat = vatTotal, journalId, lines = lines.Count, confirmed = warnings,
        }), ct);
        return CommandResult.Created(ReceiptJson(receipt, posted.Movements, [.. warnings, .. posted.Warnings]));
    })
    .RequirePermission(Permissions.InventoryReceiptCreate);

v1.MapGet("/inventory/receipts", async (RequestContext ctx, IUnitOfWork uow, IProcurement procurement, HttpContext http, CancellationToken ct) =>
    {
        var lookups = http.RequestServices.GetServices<IScopeLookup>();
        return Results.Ok(await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var rows = await procurement.ReceiptsAsync(s, null, c);
            var canSee = new Dictionary<Guid, bool>();
            foreach (Guid w in rows.Select(r => r.WarehouseId).Distinct())
            {
                canSee[w] = await ResolveScope(s, lookups, ScopeType.Warehouse, w, c) is { } scope
                            && AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.ReportsView, scope);
            }

            return new { items = rows.Where(r => canSee[r.WarehouseId]).Select(r => ReceiptJson(r)) };
        }, ct));
    })
    .RequirePermission(Permissions.ReportsView);

// ---------------- US-072: paying a supplier (PRC-003) and sending goods back (PRC-004). The payable is the ledger's
// (TradePayables with the supplier as detail); settling more than the store owes is allowed once confirmed.
object PaymentJson(SupplierPaymentRecord p, long balanceAfter, IReadOnlyList<string>? warnings = null) => new
{
    p.Id, p.SupplierId, amount = Rial(p.Amount), p.Method, p.Reference, p.Note, p.JournalEntryId, balanceAfter = Rial(balanceAfter),
    p.CreatedBy, p.PaidAt, warnings = warnings ?? [],
};

object ReturnJson(PurchaseReturnRecord r, long? balanceAfter = null, IReadOnlyList<string>? warnings = null) => new
{
    r.Id, r.SupplierId, r.WarehouseId,
    lines = r.Lines.Select(l => new { l.LineNo, l.VariantId, qty = Qty(l.Qty), unitCost = Rial(l.UnitCost), lineCost = Rial(l.LineCost) }),
    cost = Rial(r.Cost), agreedAmount = Rial(r.AgreedAmount), variance = Rial(r.Variance), r.Note, r.JournalEntryId,
    balanceAfter = balanceAfter is long b ? Rial(b) : null, r.CreatedBy, r.PostedAt, warnings = warnings ?? [],
};

v1.MapIdempotentPost<SupplierPaymentInput>("/suppliers/{supplierId:guid}/payments", "procurement.pay-supplier", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        Guid supplierId = Guid.Parse((string)http.GetRouteValue("supplierId")!, System.Globalization.CultureInfo.InvariantCulture);
        var errors = new Dictionary<string, string[]>();
        if (!TryMoney(body.Amount, positive: true, out long amount)) errors["amount"] = ["positive rial amount as a string"];
        if (body.Method is not ("Cash" or "Bank")) errors["method"] = ["Cash|Bank"];
        string? reference = string.IsNullOrWhiteSpace(body.Reference) ? null : body.Reference.Trim();
        if (reference is { Length: > 60 }) errors["reference"] = ["up to 60 characters"];
        else if (body.Method == "Bank" && reference is null) errors["reference"] = ["required for a bank payment (transfer or cheque number)"];
        string? note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
        if (note is { Length: > 500 }) errors["note"] = ["up to 500 characters"];
        // cash out of a register drawer belongs to the shift's cash count (SAL-020): not yet (follow-up with US-084)
        if (body.RegisterShiftId is not null) errors["registerShiftId"] = ["not-yet-supported"];
        if (errors.Count > 0) return CommandResult.Fail(Problems.Validation(http, errors));

        IProcurement procurement = http.RequestServices.GetRequiredService<IProcurement>();
        if (await procurement.SupplierAsync(scope, supplierId, ct) is not { } supplier) return CommandResult.Fail(Problems.NotFound(http));
        IJournals journals = http.RequestServices.GetRequiredService<IJournals>();
        long owed = (await journals.DetailBalancesAsync(scope, "TradePayables", "Supplier", [supplierId], ct)).GetValueOrDefault(supplierId);
        IReadOnlyList<string> warnings = ReceiptRules.SettlementWarnings(owed, amount);
        if (warnings.Except(body.ConfirmedWarnings ?? []).Any())
        {
            return CommandResult.Fail(Problems.WithContext(http, StatusCodes.Status422UnprocessableEntity, "confirmation-required",
                "Confirm the warnings and send the payment again", new { warnings, owed = Rial(owed) }));
        }

        Guid id = Guid.CreateVersion7();
        DateTimeOffset now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        AccountingResult<JournalView> entry = await journals.PostAutomaticAsync(scope, SupplierPaymentRule.For(id, supplierId, amount, body.Method == "Bank", now,
            $"پرداخت به {supplier.Name}{(reference is null ? "" : $" ({reference})")}"), ct);
        if (entry.Outcome != AccountingOutcome.Done) return CommandResult.Fail(AutomaticEntryProblem(http, entry));

        SupplierPaymentRecord payment = await procurement.RecordPaymentAsync(scope, new SupplierPaymentRecord(id, supplierId, amount, body.Method!, reference, note,
            entry.Value!.Id, ctx.User!.Value.Value, now), ct);
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("procurement.supplier.paid", "Supplier", supplierId.ToString(), note, new
        {
            paymentId = id, amount, body.Method, reference, journalId = entry.Value.Id, owedBefore = owed, confirmed = warnings,
        }), ct);
        return CommandResult.Created(PaymentJson(payment, owed - amount, warnings));
    })
    .RequirePermission(Permissions.AccountingJournalPost);

// US-105 / PAY-002.2 / AB-14: the bank's settlement file against the standalone terminal's payments (D08 J4)
object SettlementImportJson(SettlementImportView v) => new
{
    v.Id, v.FileName, v.LineCount, v.MatchedCount, matchedAmount = Rial(v.MatchedAmount), fees = Rial(v.Fees),
    deposited = Rial(v.MatchedAmount - v.Fees), v.JournalEntryId, v.ImportedBy, v.ImportedAt,
    outcomes = v.Lines?.GroupBy(l => l.Outcome.ToString()).ToDictionary(g => g.Key, g => g.Count()),
    lines = v.Lines?.Select(l => new
    {
        l.Line.LineNo, l.Line.TerminalNo, l.Line.ReferenceNo, transactionDate = l.Line.TransactionDate, amount = Rial(l.Line.Amount), fee = Rial(l.Line.Fee),
        outcome = l.Outcome.ToString(), l.PaymentId,
    }),
};

async Task<DateOnly> StoreTodayAsync(HttpContext http, RequestContext ctx, CancellationToken ct)
{
    TenantProfile profile = (await http.RequestServices.GetRequiredService<ITenantProfileQuery>().GetAsync(ctx.ActiveTenant!.Value, ct))!;
    DateTimeOffset now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
    return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(profile.TimeZone)).DateTime);
}

v1.MapIdempotentPost<SettlementImportInput>("/settlements/imports", "payments.import-settlement", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        var errors = new Dictionary<string, string[]>();
        string? fileName = body.FileName?.Trim();
        if (fileName is not { Length: >= 1 and <= 200 }) errors["fileName"] = ["1-200 characters"];
        if (string.IsNullOrEmpty(body.Content)) errors["content"] = ["required: the CSV text"];
        else if (System.Text.Encoding.UTF8.GetByteCount(body.Content) > SettlementCsv.MaxBytes) errors["content"] = [$"at most {SettlementCsv.MaxBytes} bytes"];
        if (errors.Count > 0) return CommandResult.Fail(Problems.Validation(http, errors));

        (IReadOnlyList<SettlementLine> rows, IReadOnlyList<SettlementRowError> rowErrors) = SettlementCsv.Parse(body.Content!);
        if (rowErrors.Count > 0)
        {
            return CommandResult.Fail(Problems.WithContext(http, StatusCodes.Status400BadRequest, "invalid-settlement-file",
                "The settlement file could not be read; nothing was imported", new { rows = rowErrors.Select(e => new { e.Line, e.Field, e.Message }) }));
        }

        ISettlements settlements = http.RequestServices.GetRequiredService<ISettlements>();
        await settlements.LockAsync(scope, ct);
        byte[] sha = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body.Content!.Replace("\r\n", "\n", StringComparison.Ordinal)));
        if (await settlements.FindByFileAsync(scope, sha, ct) is { } earlier)
        {
            return CommandResult.Fail(Problems.WithContext(http, StatusCodes.Status409Conflict, "already-imported", "This file was imported before",
                new { importId = earlier }));
        }

        IReadOnlyList<SettlementCandidate> candidates = await settlements.CandidatesAsync(scope, [.. rows.Select(r => r.ReferenceNo)], ct);
        IReadOnlyDictionary<Guid, string> finalized = await http.RequestServices.GetRequiredService<ISales>()
            .FinalizedInvoicesAsync(scope, [.. candidates.Where(c => c.SaleId is not null).Select(c => c.SaleId!.Value).Distinct()], ct);
        IReadOnlyList<SettlementMatch> matches = SettlementMatcher.Match(rows, candidates, finalized.Keys.ToHashSet());
        (int matched, long gross, long fees) = SettlementMatcher.Totals(matches);

        Guid id = Guid.CreateVersion7();
        DateTimeOffset now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        Guid? journalId = null;
        if (CardSettlementRule.For(id, gross, fees, now, $"تسویه کارتخوان: {fileName} ({matched} تراکنش)") is { } draft)
        {
            AccountingResult<JournalView> entry = await http.RequestServices.GetRequiredService<IJournals>().PostAutomaticAsync(scope, draft, ct);
            if (entry.Outcome != AccountingOutcome.Done) return CommandResult.Fail(AutomaticEntryProblem(http, entry));
            journalId = entry.Value!.Id;
        }

        SettlementImportView imported = await settlements.RecordAsync(scope, id, fileName!, sha, matches, journalId, ctx.User!.Value.Value, ct);
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("payments.settlement.imported", "SettlementImport", id.ToString(), null, new
        {
            fileName, lines = rows.Count, matched, gross, fees, journalId,
            outcomes = matches.GroupBy(m => m.Outcome.ToString()).ToDictionary(g => g.Key, g => g.Count()),
        }), ct);
        return CommandResult.Created(SettlementImportJson(imported));
    })
    .RequirePermission(Permissions.AccountingJournalPost);

v1.MapGet("/settlements/imports", async (RequestContext ctx, IUnitOfWork uow, ISettlements settlements, CancellationToken ct) =>
    {
        IReadOnlyList<SettlementImportView> rows = await uow.ExecuteAsync(ctx, (s, c) => settlements.RecentAsync(s, 50, c), ct);
        return Results.Ok(new { items = rows.Select(SettlementImportJson) });
    })
    .RequirePermission(Permissions.AccountingRead);

v1.MapGet("/settlements/imports/{importId:guid}", async (Guid importId, RequestContext ctx, IUnitOfWork uow, ISettlements settlements, HttpContext http,
        CancellationToken ct) =>
    {
        SettlementImportView? v = await uow.ExecuteAsync(ctx, (s, c) => settlements.GetAsync(s, importId, c), ct);
        return v is null ? Problems.NotFound(http) : Results.Ok(SettlementImportJson(v));
    })
    .RequirePermission(Permissions.AccountingRead);

// AB-14: «پرداخت دستی بدون تطبیق» — booked card payments no settlement has matched, per cashier
v1.MapGet("/settlements/unreconciled-payments", async (RequestContext ctx, IUnitOfWork uow, HttpContext http, DateOnly? from, DateOnly? to, CancellationToken ct) =>
    {
        DateOnly until = to ?? await StoreTodayAsync(http, ctx, ct);
        DateOnly since = from ?? until.AddDays(-30);
        if (since > until || until.DayNumber - since.DayNumber > 92)
        {
            return Problems.Validation(http, new Dictionary<string, string[]> { ["query"] = ["from <= to, at most 92 days"] });
        }

        const int Cap = 2000;
        var report = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            IReadOnlyList<UnreconciledPayment> open = await http.RequestServices.GetRequiredService<ISettlements>().UnreconciledAsync(s, since, until, Cap + 1, c);
            IReadOnlyDictionary<Guid, string> invoices = await http.RequestServices.GetRequiredService<ISales>()
                .FinalizedInvoicesAsync(s, [.. open.Where(p => p.SaleId is not null).Select(p => p.SaleId!.Value).Distinct()], c);
            Dictionary<Guid, string> names = (await http.RequestServices.GetRequiredService<IUserAdministration>().ListAsync(s, c))
                .ToDictionary(u => u.Id, u => u.DisplayName);
            return (open, invoices, names);
        }, ct);
        bool truncated = report.open.Count > Cap;
        UnreconciledPayment[] booked = [.. report.open.Take(Cap).Where(p => p.SaleId is { } id && report.invoices.ContainsKey(id))];
        return Results.Ok(new
        {
            from = since, to = until, truncated,
            total = new { count = booked.Length, amount = Rial(booked.Sum(p => p.Amount)) },
            byCashier = booked.GroupBy(p => p.RecordedBy).OrderByDescending(g => g.Sum(p => p.Amount)).Select(g => new
            {
                cashierId = g.Key, displayName = report.names.GetValueOrDefault(g.Key), count = g.Count(), amount = Rial(g.Sum(p => p.Amount)),
            }),
            items = booked.Select(p => new
            {
                p.PaymentId, p.SaleId, invoiceNo = report.invoices[p.SaleId!.Value], p.ReferenceNo, p.TerminalNo, p.CardLast4, amount = Rial(p.Amount),
                p.BusinessDate, cashierId = p.RecordedBy, cashierName = report.names.GetValueOrDefault(p.RecordedBy), p.RegisterId, p.RecordedAt,
            }),
        });
    })
    .RequirePermission(Permissions.AccountingRead);

v1.MapIdempotentPost<PurchaseReturnInput>("/purchase-returns", "procurement.post-purchase-return", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        var errors = new Dictionary<string, string[]>();
        if (body.SupplierId is null) errors["supplierId"] = ["required"];
        if (body.WarehouseId is null) errors["warehouseId"] = ["required"];
        if (!TryMoney(body.AgreedAmount, positive: false, out long agreed)) errors["agreedAmount"] = ["rial amount as a whole-number string"];
        string? note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
        if (note is { Length: > 500 }) errors["note"] = ["up to 500 characters"];
        var inputs = new List<(Guid Variant, decimal Qty)>();
        if (body.Lines is not { Length: >= 1 and <= ReceiptRules.MaxLines }) errors["lines"] = [$"1-{ReceiptRules.MaxLines} lines"];
        for (int i = 0; i < (body.Lines?.Length ?? 0) && body.Lines!.Length <= ReceiptRules.MaxLines; i++)
        {
            PurchaseReturnLineInput? l = body.Lines[i];
            if (l?.VariantId is null) errors[$"lines[{i}].variantId"] = ["required"];
            if (l?.Qty is null || !ProductRules.TryFactor(l.Qty, out decimal qty)) errors[$"lines[{i}].qty"] = ["positive decimal in the base unit, up to 6 places"];
            else if (l.VariantId is { } v) inputs.Add((v, qty));
        }

        if (inputs.Select(x => x.Variant).Distinct().Count() != inputs.Count) errors["lines"] = ["one line per variant"];
        if (errors.Count > 0) return CommandResult.Fail(Problems.Validation(http, errors));

        Guid warehouseId = body.WarehouseId!.Value, supplierId = body.SupplierId!.Value;
        if (await ResolveScope(scope, http.RequestServices.GetServices<IScopeLookup>(), ScopeType.Warehouse, warehouseId, ct) is not { } warehouse)
        {
            return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { ["warehouseId"] = ["not a warehouse of this store"] }));
        }

        if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.InventoryReceiptCreate, warehouse)) return CommandResult.Fail(Forbidden(http));
        IProcurement procurement = http.RequestServices.GetRequiredService<IProcurement>();
        if (await procurement.SupplierAsync(scope, supplierId, ct) is not { } supplier)
        {
            return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { ["supplierId"] = ["not a supplier of this store"] }));
        }

        IReadOnlySet<Guid> known = await http.RequestServices.GetRequiredService<IProductCatalog>().ExistingVariantsAsync(scope, [.. inputs.Select(x => x.Variant)], ct);
        if (inputs.FindIndex(x => !known.Contains(x.Variant)) is var unknown and >= 0)
        {
            return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { [$"lines[{unknown}].variantId"] = ["not a variant of this store"] }));
        }

        IJournals journals = http.RequestServices.GetRequiredService<IJournals>();
        long owed = (await journals.DetailBalancesAsync(scope, "TradePayables", "Supplier", [supplierId], ct)).GetValueOrDefault(supplierId);
        IReadOnlyList<string> warnings = ReceiptRules.SettlementWarnings(owed, agreed);
        if (warnings.Except(body.ConfirmedWarnings ?? []).Any())
        {
            return CommandResult.Fail(Problems.WithContext(http, StatusCodes.Status422UnprocessableEntity, "confirmation-required",
                "Confirm the warnings and send the return again", new { warnings, owed = Rial(owed) }));
        }

        // the goods leave at the WAC in force, under the balances' locks (INV-004/006); a shortage refuses the whole return
        Guid id = Guid.CreateVersion7();
        DateTimeOffset now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        StockResult posted = await http.RequestServices.GetRequiredService<IStockLedger>().PostAsync(scope, new StockDocument("PurchaseReturn", id, now,
            [.. inputs.Select((x, i) => new StockLine(i + 1, warehouseId, x.Variant, MovementType.PurchaseReturn, -x.Qty))]), ct);
        if (posted.Outcome == StockOutcome.InsufficientStock)
        {
            return CommandResult.Fail(Problems.WithContext(http, StatusCodes.Status409Conflict, "insufficient-stock", "Not enough stock", new
            {
                shortages = posted.Shortages.Select(x => new { x.WarehouseId, x.VariantId, available = Qty(x.Available), requested = Qty(x.Requested) }),
            }));
        }

        if (posted.Outcome != StockOutcome.Done) throw new InvalidOperationException($"purchase return {id}: {posted.Outcome}");
        var lines = posted.Movements.OrderBy(m => m.Line)
            .Select(m => new PurchaseReturnLine(m.Line, m.VariantId, -m.QtyBase, m.UnitCost, -m.TotalCost)).ToList();
        long cost = lines.Sum(l => l.LineCost);

        Guid? journalId = null;
        if (PurchaseReturnRule.For(id, supplierId, cost, agreed, now, $"برگشت از خرید به {supplier.Name}") is { } draft)
        {
            AccountingResult<JournalView> entry = await journals.PostAutomaticAsync(scope, draft, ct);
            if (entry.Outcome != AccountingOutcome.Done) return CommandResult.Fail(AutomaticEntryProblem(http, entry));
            journalId = entry.Value!.Id;
        }

        PurchaseReturnRecord record = await procurement.RecordReturnAsync(scope, new PurchaseReturnRecord(id, supplierId, warehouseId, lines, cost, agreed, note,
            journalId, ctx.User!.Value.Value, now), ct);
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("procurement.purchase-return.posted", "PurchaseReturn", id.ToString(), note, new
        {
            supplierId, warehouseId, cost, agreed, variance = agreed - cost, journalId, lines = lines.Count, owedBefore = owed, confirmed = warnings,
        }), ct);
        return CommandResult.Created(ReturnJson(record, owed - agreed, [.. warnings, .. posted.Warnings]));
    })
    .RequirePermission(Permissions.InventoryReceiptCreate);

v1.MapGet("/purchase-returns", async (RequestContext ctx, IUnitOfWork uow, IProcurement procurement, HttpContext http, CancellationToken ct) =>
    {
        var lookups = http.RequestServices.GetServices<IScopeLookup>();
        return Results.Ok(await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var rows = await procurement.ReturnsAsync(s, null, c);
            var canSee = new Dictionary<Guid, bool>();
            foreach (Guid w in rows.Select(r => r.WarehouseId).Distinct())
            {
                canSee[w] = await ResolveScope(s, lookups, ScopeType.Warehouse, w, c) is { } scope
                            && AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.ReportsView, scope);
            }

            return new { items = rows.Where(r => canSee[r.WarehouseId]).Select(r => ReturnJson(r)) };
        }, ct));
    })
    .RequirePermission(Permissions.ReportsView);

// ---------------- US-092 / SAL-012 / BR-086 / CUS-009: the printed receipt (80 mm or A4) and its link. Every print issues a
// fresh receipt link for the QR (only its SHA-256 is kept, 90 days, revocable); a reprint is numbered as a copy and audited.
string receiptBase = (builder.Configuration["Receipts:PublicBaseUrl"] ?? "http://127.0.0.1:3000").TrimEnd('/');

async Task<object> SaleReceiptJson(IUnitOfWorkScope s, HttpContext http, Guid tenantId, SaleView sale, ReceiptSnapshot snap, CancellationToken c)
{
    TenantProfile? shop = await http.RequestServices.GetRequiredService<ITenantProfileQuery>().GetAsync(new TenantId(tenantId), c);
    BranchView? branch = await http.RequestServices.GetRequiredService<IBranchAdministration>().GetBranchAsync(s, sale.BranchId, c);
    IndustryTemplate? template = snap.TemplateRef is null ? null
        : await http.RequestServices.GetRequiredService<IIndustryTemplates>().VersionAsync(s, snap.TemplateRef, c);
    var t = SaleMath.Totals(sale.Lines);
    return new
    {
        storeName = shop?.LegalName, branchName = branch?.Name, sale.InvoiceNo, sale.FinalizedAt,
        lines = sale.Lines.Select(l => new
        {
            l.Name, qty = Qty(l.Qty), l.Unit, unitPrice = Money(l.UnitPrice), discount = Money(l.Discount), tax = Money(l.Tax), lineTotal = Money(l.LineTotal),
        }),
        totals = new { gross = Money(t.Gross), discount = Money(t.Discount), net = Money(t.Net), tax = Money(t.Tax), total = Money(t.Total) },
        tenders = (sale.Tenders ?? []).Where(x => !x.Voided).Select(x => new { x.Method, amount = Money(x.Amount) }),
        credit = (object?)null,   // a credit sale prints its credit, due date and balance with EP-12
        legalText = template?.LegalText, lockedFields = template?.LockedFields ?? [], templateVersion = snap.TemplateRef,
        footer = snap.Footer, signature = snap.Signature,
    };
}

v1.MapPost("/sales/{saleId:guid}/print", async (Guid saleId, PrintInput body, RequestContext ctx, IUnitOfWork uow, ISales sales, HttpContext http,
        CancellationToken ct) =>
    {
        if (body.Format is not ("80mm" or "A4")) return Problems.Validation(http, new Dictionary<string, string[]> { ["format"] = ["80mm|A4"] });
        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            if (await sales.GetAsync(s, saleId, c) is not { } sale
                || !AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.PosSell, new ScopeRef(ScopeType.Register, sale.RegisterId, sale.BranchId)))
            {
                return Problems.NotFound(http);
            }

            if (sale.Status != "Finalized") return Problems.Create(http, StatusCodes.Status409Conflict, "sale-not-finalized", "Only an issued invoice is printed");
            ReceiptSnapshot snap = (await sales.ReceiptSnapshotAsync(s, saleId, c))!;
            int copy = await sales.CountPrintAsync(s, saleId, c);
            // CUS-009: 128 random bits, URL-safe; only the hash is stored, so the link is shown now or never
            string token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            DateTimeOffset expires = await sales.CreateReceiptLinkAsync(s, saleId, System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token)),
                ctx.User!.Value.Value, c);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("sales.receipt.printed", "Sale", saleId.ToString(), null,
                new { copy, body.Format, sale.InvoiceNo }), c);
            object receipt = await SaleReceiptJson(s, http, ctx.ActiveTenant!.Value.Value, sale, snap, c);
            return Results.Ok(new { receipt, format = body.Format, copy, isCopy = copy > 1, receiptUrl = $"{receiptBase}/r/{token}", linkExpiresAt = expires });
        }, ct);
    })
    .RequirePermission(Permissions.PosSell);

v1.MapDelete("/sales/{saleId:guid}/receipt-links", async (Guid saleId, RequestContext ctx, IUnitOfWork uow, ISales sales, HttpContext http, CancellationToken ct) =>
        await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            if (await sales.GetAsync(s, saleId, c) is not { } sale
                || !AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.PosSell, new ScopeRef(ScopeType.Register, sale.RegisterId, sale.BranchId)))
            {
                return Problems.NotFound(http);
            }

            int revoked = await sales.RevokeReceiptLinksAsync(s, saleId, c);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("sales.receipt-links.revoked", "Sale", saleId.ToString(), null, new { revoked }), c);
            return Results.Ok(new { revoked });
        }, ct))
    .RequirePermission(Permissions.PosSell);

// CUS-009 / AB-12: the receipt behind a link, without signing in and without any customer data; unknown, expired and revoked
// links look the same (404); every view is audited in the store and the endpoint is rate-limited per client address
app.MapGet("/api/v1/r/{token}", async (string token, IUnitOfWork uow, ISales sales, HttpContext http, CancellationToken ct) =>
    {
        if (token.Length is < 20 or > 30 || !token.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) return Problems.NotFound(http);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token));
        if (await uow.ExecuteWithoutTenantAsync(null, (s, c) => sales.ReceiptLinkTargetAsync(s, hash, c), ct) is not { } target) return Problems.NotFound(http);
        return await uow.ExecuteAsync(new FixedTenantContext(new TenantId(target.TenantId)), async (s, c) =>
        {
            if (await sales.GetAsync(s, target.SaleId, c) is not { Status: "Finalized" } sale) return Problems.NotFound(http);
            ReceiptSnapshot snap = (await sales.ReceiptSnapshotAsync(s, sale.Id, c))!;
            await Audit(http, s, new AuditActor(null, "System", [], null, null, ClientIp(http)), new AuditEntry("sales.receipt.viewed", "Sale", sale.Id.ToString(),
                null, new { via = "receipt-link" }), c);
            return Results.Ok(await SaleReceiptJson(s, http, target.TenantId, sale, snap, c));
        }, ct);
    })
    .AllowAnonymous()
    .RequireRateLimiting(RateLimits.ReceiptLink);

// ---------------- US-082: the POS cart (SAL-001, SAL-002, SAL-010). A cart is the cashier's own, on their open shift,
// worked on a registered device of the branch (ABAC-04, BR-050); every amount is the server's (BR-013..BR-018).
string Money(long v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

object SaleJson(SaleView s, IReadOnlyList<string>? warnings = null, IReadOnlyList<PaymentView>? payments = null, IReadOnlyList<object>? rejectedLines = null)
{
    var t = SaleMath.Totals(s.Lines);
    long change = payments?.Where(p => p.Status != "Voided").Sum(p => p.Change ?? 0) ?? 0;
    return new
    {
        s.Id, s.Status, s.ReturnStatus, s.BranchId, s.RegisterId, s.ShiftId, cashierId = s.CashierId, terminalId = s.TerminalId, s.Channel,
        s.ClientDraftId, pricingMode = s.PricingMode.ToString(), s.InvoiceNo, s.HoldNote, s.CancelReason, customer = (object?)null,
        lines = s.Lines.Select(l => new
        {
            l.LineNo, l.VariantId, l.Name, l.Sku, qty = Qty(l.Qty), l.Unit, unitFactor = Qty(l.UnitFactor), weighted = l.WeightedCode is not null,
            unitPrice = Money(l.UnitPrice), gross = Money(l.Gross),
            discounts = l.DiscountSource is null ? [] : new[]
            {
                new { kind = "Percent", value = (l.DiscountBp ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture), source = l.DiscountSource,
                      amount = Money(l.Discount), approvalId = (Guid?)null },
            },
            net = Money(l.Net), taxRate = l.TaxRateBp, tax = Money(l.Tax), lineTotal = Money(l.LineTotal), returnedQty = Qty(l.ReturnedQty),
        }),
        saleDiscount = (object?)null,
        tenders = (s.Tenders ?? []).Select(x =>
        {
            PaymentView? p = payments?.FirstOrDefault(p => p.Id == x.PaymentId);
            return new
            {
                x.Seq, x.Method, amount = Money(x.Amount), status = x.Voided ? "Voided" : "Confirmed", x.PaymentId, reference = p?.ReferenceNo,
                voidReason = x.VoidReason,
                tendered = p?.Tendered is long td ? Money(td) : null, change = p?.Change is long ch ? Money(ch) : null,
            };
        }),
        totals = new
        {
            gross = Money(t.Gross), discount = Money(t.Discount), net = Money(t.Net), tax = Money(t.Tax), total = Money(t.Total),
            paid = Money(s.Paid), remaining = Money(s.Remaining), change = Money(change),
        },
        s.FinalizedAt, receiptUrl = (string?)null, s.Version, rejectedLines,
        warnings = warnings ?? (s.Status is "Draft" or "Held" ? ["price-preview-only"] : []),
    };
}

// US-101: once a payment is taken the bill is fixed (the database agrees, RC026)
IResult PaidInto(HttpContext http) =>
    Problems.Create(http, StatusCodes.Status409Conflict, "sale-has-payments", "Payments were taken on this bill; its lines no longer change");

IResult SaleProblem(HttpContext http, SaleOutcome outcome) => outcome switch
{
    SaleOutcome.NotFound or SaleOutcome.LineNotFound => Problems.NotFound(http),
    SaleOutcome.NotDraft => Problems.Create(http, StatusCodes.Status409Conflict, "sale-not-draft", "Only a draft cart can change"),
    SaleOutcome.VersionConflict => Problems.Create(http, StatusCodes.Status409Conflict, "version-conflict", "The cart changed; reload and retry"),
    _ => Problems.Create(http, StatusCodes.Status409Conflict, "client-draft-id-taken", "This clientDraftId belongs to another sale"),
};

// a cart is changed only by its cashier, on a registered device of its branch, while its shift is open
async Task<IResult?> MayWorkOn(HttpContext http, RequestContext ctx, IUnitOfWorkScope scope, SaleView sale, CancellationToken ct)
{
    if (sale.CashierId != ctx.User!.Value.Value) return Problems.NotFound(http);
    if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.PosSell, new ScopeRef(ScopeType.Register, sale.RegisterId, sale.BranchId)))
    {
        return Forbidden(http);
    }

    if (OnTerminalOf(http, ctx, sale.BranchId) is { } device) return device;
    return await http.RequestServices.GetRequiredService<IShifts>().GetAsync(scope, sale.ShiftId, ct) is { Status: "Open" }
        ? null
        : Problems.Create(http, StatusCodes.Status409Conflict, "shift-not-open", "The cart's shift is not open (BR-050)");
}

// BR-013 / BR-016 / CAT-010: one line priced now, in this branch: the default list's price for the unit, the largest rule
// discount, the product's tax rate in the cart's pricing mode. labelAmount: a price label's amount is the gross (CAT-007.1).
async Task<(SaleLineSpec? Line, IResult? Problem)> PriceLine(HttpContext http, IUnitOfWorkScope scope, SaleView sale, SellableVariant v, decimal qty,
    string unit, decimal factor, string? weightedCode, long? labelAmount, CancellationToken ct)
{
    if (!v.Active)
    {
        return (null, Problems.Create(http, StatusCodes.Status422UnprocessableEntity, "not-sellable", "This product or combination is switched off"));
    }

    if (v.UnitPrice is not long basePrice)
    {
        return (null, Problems.Create(http, StatusCodes.Status422UnprocessableEntity, "price-missing", "This product has no price yet"));
    }

    long unitPrice = SaleMath.UnitPrice(basePrice, factor);
    long gross = labelAmount ?? SaleMath.Gross(qty, unitPrice);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DiscountRuleView? rule = DiscountPolicy.Best(await http.RequestServices.GetRequiredService<IDiscounts>().RulesForAsync(scope, [v.ProductId], now, ct),
        v.ProductId, sale.BranchId, now);
    long discount = rule is null ? 0 : DiscountPolicy.AmountOff(gross, rule.PercentBp);
    LineAmounts amounts = SaleMath.Line(gross, discount, v.TaxRateBp, sale.PricingMode);
    return (new SaleLineSpec(v.VariantId, v.Name, v.Sku, qty, unit, factor, weightedCode, unitPrice, amounts, discount > 0 ? "Rule" : null,
        discount > 0 ? rule!.PercentBp : null, discount > 0 ? rule!.Id : null, v.TaxRateBp), null);
}

// SAL-010 / BR-050: the POS starts from its device: the branch, its registers and each one's current shift
v1.MapGet("/pos/context", async (RequestContext ctx, IUnitOfWork uow, IShifts shifts, HttpContext http, CancellationToken ct) =>
    {
        if (ctx.Terminal is not { } terminal)
        {
            return Problems.Create(http, StatusCodes.Status403Forbidden, "terminal-not-registered", "Use a registered device of this branch (ABAC-04)");
        }

        return Results.Ok(await uow.ExecuteAsync<object>(ctx, async (s, c) =>
        {
            var registers = new List<(Guid Id, string Code)>();
            await using (NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT id, code FROM platform.register WHERE branch_id = $1 ORDER BY code"))
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = terminal.BranchId });
                await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(c);
                while (await r.ReadAsync(c)) registers.Add((r.GetGuid(0), r.GetString(1)));
            }

            var items = new List<object>();
            foreach ((Guid id, string code) in registers)
            {
                ShiftView? shift = await shifts.CurrentAsync(s, id, c);
                items.Add(new
                {
                    id, code,
                    mayOpenShift = AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.ShiftManage, new ScopeRef(ScopeType.Register, id, terminal.BranchId)),
                    shift = shift is null ? null : new { shift.Id, shift.Status, mine = shift.CashierId == ctx.User!.Value.Value, shift.OpenedAt },
                });
            }

            return new { terminalId = terminal.TerminalId, branchId = terminal.BranchId, registers = items };
        }, ct));
    })
    .RequirePermission(Permissions.PosSell);

v1.MapPost("/pos/sales", async (SaleCreateInput body, RequestContext ctx, IUnitOfWork uow, IShifts shifts, ISales sales, ITenantProfileQuery profiles,
        HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        if (body.RegisterId is null) errors["registerId"] = ["required"];
        if (body.ClientDraftId is not { Length: >= 8 and <= 64 } || !body.ClientDraftId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            errors["clientDraftId"] = ["8-64 of A-Z a-z 0-9 _ -"];
        }

        if (body.Lines is { Length: > SaleMath.MaxLines }) errors["lines"] = [$"up to {SaleMath.MaxLines} lines"];
        if (errors.Count > 0) return Problems.Validation(http, errors);
        PricingMode mode = Enum.Parse<PricingMode>((await profiles.GetAsync(ctx.ActiveTenant!.Value, ct))!.PricingMode);
        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            if (await ResolveScope(s, http.RequestServices.GetServices<IScopeLookup>(), ScopeType.Register, body.RegisterId, c) is not { } register)
            {
                return Problems.NotFound(http);
            }

            if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.PosSell, register)) return Forbidden(http);
            if (OnTerminalOf(http, ctx, register.BranchId!.Value) is { } device) return device;
            // BR-050: a cart lives on the cashier's own open shift on this register
            if (await shifts.CurrentAsync(s, body.RegisterId!.Value, c) is not { Status: "Open" } shift || shift.CashierId != ctx.User!.Value.Value)
            {
                return Problems.Create(http, StatusCodes.Status409Conflict, "shift-not-open", "Open your shift on this register first (BR-050)");
            }

            var (result, created) = await sales.CreateAsync(s, body.ClientDraftId!, new SaleOrigin(register.BranchId.Value, body.RegisterId.Value, shift.Id,
                ctx.User!.Value.Value, ctx.Terminal!.TerminalId, "POS", mode), c);
            if (result.Outcome != SaleOutcome.Done) return SaleProblem(http, result.Outcome);
            if (!created)
            {
                return Results.Ok(SaleJson(result.Sale!));   // SAL-013: a cart synced again keeps what it has; its lines are not added twice
            }

            // SAL-013: a cart built offline arrives with its lines, added at today's server prices in this transaction; a line
            // the store cannot sell now (an unknown code, no price) is left out and reported, not fatal to the rest
            SaleView sale = result.Sale!;
            var rejected = new List<object>();
            for (int i = 0; i < (body.Lines?.Length ?? 0); i++)
            {
                var (after, problem) = await AddSaleLine(http, ctx, s, sale.Id, body.Lines![i], c);
                if (after is not null)
                {
                    sale = after;
                }
                else
                {
                    rejected.Add(new { index = i, code = (problem as Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)?.ProblemDetails.Extensions["code"]?.ToString() });
                }
            }

            if (body.Lines is { Length: > 0 })
            {
                await Audit(http, s, ActorOf(ctx, http), new AuditEntry("pos.sale.synced-offline", "Sale", sale.Id.ToString(), null,
                    new { clientDraftId = body.ClientDraftId, lines = body.Lines.Length, rejected = rejected.Count }), c);
            }

            return Results.Created($"/api/v1/pos/sales/{sale.Id}", SaleJson(sale, rejected.Count > 0 ? ["offline-lines-rejected", "price-preview-only"] : null,
                rejectedLines: body.Lines is null ? null : rejected));
        }, ct);
    })
    .RequirePermission(Permissions.PosSell);

v1.MapGet("/pos/sales/{saleId:guid}", async (Guid saleId, RequestContext ctx, IUnitOfWork uow, ISales sales, IPayments payments, HttpContext http,
        CancellationToken ct) =>
    {
        var (sale, paid) = await uow.ExecuteAsync(ctx, async (s, c) => (await sales.GetAsync(s, saleId, c), await payments.ForSaleAsync(s, saleId, c)), ct);
        return sale is not null
               && AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.PosSell, new ScopeRef(ScopeType.Register, sale.RegisterId, sale.BranchId))
            ? Results.Ok(SaleJson(sale, payments: paid))
            : Problems.NotFound(http);
    })
    .RequirePermission(Permissions.PosSell);

// SAL-001 / SAL-002 / CAT-003 / CAT-007: one scan or pick added to a draft cart in the caller's transaction (addSaleLine,
// and createSale's offline lines, SAL-013). A refusal changes nothing and comes back as the problem.
async Task<(SaleView? Sale, IResult? Problem)> AddSaleLine(HttpContext http, RequestContext ctx, IUnitOfWorkScope s, Guid saleId, SaleLineInput body,
    CancellationToken c)
{
    ISales sales = http.RequestServices.GetRequiredService<ISales>();
    IProductCatalog catalog = http.RequestServices.GetRequiredService<IProductCatalog>();
    IWeightedBarcodeProfiles weighted = http.RequestServices.GetRequiredService<IWeightedBarcodeProfiles>();
    var errors = new Dictionary<string, string[]>();
    if ((body.VariantId is null) == (body.Barcode is null)) errors["barcode"] = ["exactly one of barcode or variantId"];
    if (body.Barcode is { } b && (b.Length is < 1 or > 32 || string.IsNullOrWhiteSpace(b))) errors["barcode"] = ["1-32 characters"];
    if (body.Qty is null || !ProductRules.TryFactor(body.Qty, out decimal qty)) { errors["qty"] = ["positive decimal, up to 6 places"]; qty = 0; }
    if (body.Unit is not null && !ProductRules.ValidUnit(body.Unit)) errors["unit"] = ["1-10 of A-Z 0-9"];
    if (errors.Count > 0) return (null, Problems.Validation(http, errors));

    string? code = body.Barcode is null ? null : Gtin.NormalizeDigits(body.Barcode.Trim());
    bool gs1 = code is not null && Gtin.TryParse(code, out _);
    if (code is not null && !gs1 && (code.All(char.IsAsciiDigit) && code.Length is 8 or 12 or 13 or 14 || !InternalCode.IsValid(code)))
    {
        return (null, InputProblem(http, new Dictionary<string, string[]> { ["barcode"] = ["not a valid EAN-8, UPC-A, EAN-13, GTIN-14 or internal code"] }, true));
    }

    SaleResult locked = await sales.LockDraftAsync(s, saleId, null, c);
    if (locked.Outcome == SaleOutcome.NotFound) return (null, Problems.NotFound(http));
    if (await MayWorkOn(http, ctx, s, locked.Sale!, c) is { } denied) return (null, denied);
    if (locked.Outcome != SaleOutcome.Done) return (null, SaleProblem(http, locked.Outcome));
    if (locked.Sale!.HasPayments) return (null, PaidInto(http));
    SaleView sale = locked.Sale!;

    // CAT-003 / CAT-007: a scale label first, then the code itself; an unknown code is for UC-01 (define) or a name search
    Guid variantId;
    string? unit = body.Unit, weightedCode = null;
    decimal? fixedQty = null;
    long? labelValue = null;
    WeightedValueType? labelType = null;
    if (code is not null)
    {
        var (status, label) = WeightedBarcodes.Decode((await weighted.GetAsync(s, c)).Profiles, code);
        if (status == WeightedDecodeStatus.Invalid)
        {
            return (null, InputProblem(http, new Dictionary<string, string[]> { ["barcode"] = ["weighted label: the value check digit does not match"] }, true));
        }

        BarcodeMatch? match = await catalog.FindByCodeAsync(s, status == WeightedDecodeStatus.Decoded ? label!.ItemCode
            : Gtin.TryParse(code, out Gtin g) ? g.Normalized14 : code, c);
        if (match is null) return (null, Problems.Create(http, StatusCodes.Status404NotFound, "barcode-not-found", "No product of this store has this code"));
        variantId = match.VariantId;
        if (status == WeightedDecodeStatus.Decoded)
        {
            weightedCode = code;
            unit = match.BaseUnit;
            labelType = label!.Profile.ValueType;
            if (labelType == WeightedValueType.Weight) fixedQty = label.Value;
            else labelValue = (long)label.Value;
        }
        else if (unit is null && match.UnitFactor != 1)
        {
            unit = match.PackagingUnit;   // a carton's barcode sells a carton (BR-044)
        }
    }
    else
    {
        variantId = body.VariantId!.Value;
    }

    if (await catalog.SellableAsync(s, variantId, c) is not { } v) return (null, Problems.NotFound(http));
    unit ??= v.BaseUnit;
    if (!(await catalog.UnitsAsync(s, [variantId], c))[variantId].Factors.TryGetValue(unit, out decimal factor))
    {
        return (null, Problems.Create(http, StatusCodes.Status422UnprocessableEntity, "unknown-unit", "This unit has no conversion to the product's base unit"));
    }

    long? labelAmount = null;
    if (labelValue is { } amount)
    {
        if (v.UnitPrice is not long price || price <= 0)
        {
            return (null, Problems.Create(http, StatusCodes.Status422UnprocessableEntity, "price-missing", "This product has no price yet"));
        }

        (fixedQty, labelAmount) = WeightedBarcodes.PriceLabelLine(amount, price);
    }

    if (fixedQty is <= 0)
    {
        return (null, InputProblem(http, new Dictionary<string, string[]> { ["barcode"] = ["the label's quantity is zero"] }, true));
    }

    // a repeated scan of the same item and unit adds to its line; a scale label is always a line of its own
    SaleLineView? same = weightedCode is null ? sale.Lines.FirstOrDefault(l => l.VariantId == variantId && l.Unit == unit && l.WeightedCode is null) : null;
    if (same is null && sale.Lines.Count >= SaleMath.MaxLines)
    {
        return (null, Problems.Validation(http, new Dictionary<string, string[]> { ["lines"] = [$"up to {SaleMath.MaxLines} lines"] }));
    }

    decimal lineQty = fixedQty ?? (same?.Qty ?? 0) + qty;
    var (line, problem) = await PriceLine(http, s, sale, v, lineQty, unit, factor, weightedCode, labelAmount, c);
    if (problem is not null) return (null, problem);
    return (await sales.WriteLineAsync(s, saleId, same?.LineNo, line!, c), null);
}

v1.MapPost("/pos/sales/{saleId:guid}/lines", async (Guid saleId, SaleLineInput body, RequestContext ctx, IUnitOfWork uow, HttpContext http,
        CancellationToken ct) =>
    {
        var (sale, problem) = await uow.ExecuteAsync(ctx, (s, c) => AddSaleLine(http, ctx, s, saleId, body, c), ct);
        return problem ?? Results.Ok(SaleJson(sale!));
    })
    .RequirePermission(Permissions.PosSell);

v1.MapPatch("/pos/sales/{saleId:guid}/lines/{lineNo:int}", async (Guid saleId, int lineNo, SaleLinePatchInput body, RequestContext ctx, IUnitOfWork uow,
        ISales sales, IProductCatalog catalog, HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        if (body.ExpectedVersion is null) errors["expectedVersion"] = ["required"];
        decimal qty = 0;
        if (body.Qty is not null && !ProductRules.TryFactor(body.Qty, out qty)) errors["qty"] = ["positive decimal, up to 6 places"];
        if (body.Unit is not null && !ProductRules.ValidUnit(body.Unit)) errors["unit"] = ["1-10 of A-Z 0-9"];
        if (body.Qty is null && body.Unit is null) errors["qty"] = ["qty or unit"];
        if (errors.Count > 0) return Problems.Validation(http, errors);
        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            SaleResult locked = await sales.LockDraftAsync(s, saleId, body.ExpectedVersion, c);
            if (locked.Outcome == SaleOutcome.NotFound) return Problems.NotFound(http);
            if (await MayWorkOn(http, ctx, s, locked.Sale!, c) is { } denied) return denied;
            if (locked.Outcome != SaleOutcome.Done) return SaleProblem(http, locked.Outcome);
            if (locked.Sale!.HasPayments) return PaidInto(http);
            if (locked.Sale!.Lines.FirstOrDefault(l => l.LineNo == lineNo) is not { } old) return Problems.NotFound(http);
            if (old.WeightedCode is not null)
            {
                return Problems.Create(http, StatusCodes.Status409Conflict, "weighted-line", "A weighed item's quantity comes from its label; remove and scan again");
            }

            if (await catalog.SellableAsync(s, old.VariantId, c) is not { } v) return Problems.NotFound(http);
            string unit = body.Unit ?? old.Unit;
            if (!(await catalog.UnitsAsync(s, [old.VariantId], c))[old.VariantId].Factors.TryGetValue(unit, out decimal factor))
            {
                return Problems.Create(http, StatusCodes.Status422UnprocessableEntity, "unknown-unit", "This unit has no conversion to the product's base unit");
            }

            var (line, problem) = await PriceLine(http, s, locked.Sale, v, body.Qty is null ? old.Qty : qty, unit, factor, null, null, c);
            if (problem is not null) return problem;
            return Results.Ok(SaleJson(await sales.WriteLineAsync(s, saleId, lineNo, line!, c)));
        }, ct);
    })
    .RequirePermission(Permissions.PosSell);

// AB-03 / D-44: a removed line is audited (who, what, how much) to find lines scanned and then taken off
v1.MapDelete("/pos/sales/{saleId:guid}/lines/{lineNo:int}", async (Guid saleId, int lineNo, int? expectedVersion, RequestContext ctx, IUnitOfWork uow,
        ISales sales, HttpContext http, CancellationToken ct) =>
    {
        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            // another store's or another cashier's cart is not found, whatever the query says (BR-003)
            SaleResult locked = await sales.LockDraftAsync(s, saleId, expectedVersion, c);
            if (locked.Outcome == SaleOutcome.NotFound) return Problems.NotFound(http);
            if (await MayWorkOn(http, ctx, s, locked.Sale!, c) is { } denied) return denied;
            if (expectedVersion is null) return Problems.Validation(http, new Dictionary<string, string[]> { ["expectedVersion"] = ["required"] });
            if (locked.Outcome != SaleOutcome.Done) return SaleProblem(http, locked.Outcome);
            if (locked.Sale!.HasPayments) return PaidInto(http);
            if (locked.Sale!.Lines.FirstOrDefault(l => l.LineNo == lineNo) is not { } old) return Problems.NotFound(http);
            SaleView after = await sales.RemoveLineAsync(s, saleId, lineNo, c);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("sales.line.removed", "Sale", saleId.ToString(), null, new
            {
                lineNo, old.VariantId, old.Sku, qty = Qty(old.Qty), old.Unit, lineTotal = old.LineTotal, after.RegisterId, after.ShiftId,
                terminalId = ctx.Terminal?.TerminalId,
            }), c);
            return Results.Ok(SaleJson(after));
        }, ct);
    })
    .RequirePermission(Permissions.PosSell);

// ---------------- US-101: taking payment on a bill (SAL-006, PAY-001/002). Cash shows the change and only the applied amount
// counts (SAL-006.1); a standalone card terminal is confirmed by its reference, once per terminal and day unless a manager
// overrides (PAY-002.1, AB-14). Several payments may share a bill; together never more than its total (RC027).
v1.MapIdempotentPost<TenderInput>("/pos/sales/{saleId:guid}/tenders", "pos.record-tender", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        Guid saleId = Guid.Parse((string)http.GetRouteValue("saleId")!, System.Globalization.CultureInfo.InvariantCulture);
        var errors = new Dictionary<string, string[]>();
        if (body.Method is not ("Cash" or "ExternalCardTerminal"))
        {
            errors["method"] = [body.Method is "CustomerCredit" or "StoreCredit" ? "not-yet-supported" : "Cash|ExternalCardTerminal|CustomerCredit|StoreCredit"];
        }

        if (!TryMoney(body.Amount, positive: true, out long amount)) errors["amount"] = ["positive rial amount as a whole-number string"];
        long tendered = amount;
        if (body.Method == "Cash" && body.Tendered is not null && (!TryMoney(body.Tendered, positive: true, out tendered) || tendered < amount))
        {
            errors["tendered"] = ["rials handed over, at least the amount"];
        }

        if (body.Method == "ExternalCardTerminal")
        {
            if (!PaymentRules.ValidReference(body.ReferenceNo)) errors["referenceNo"] = ["4-32 letters or digits from the card terminal's slip"];
            if (!PaymentRules.ValidTerminalNo(body.TerminalNo)) errors["terminalNo"] = ["1-16 letters or digits"];
            if (!PaymentRules.ValidLast4(body.CardLast4)) errors["cardLast4"] = ["4 digits"];
        }
        else if (body.ReferenceNo is not null || body.TerminalNo is not null || body.CardLast4 is not null)
        {
            errors["referenceNo"] = ["only for ExternalCardTerminal"];
        }

        if (errors.Count > 0) return CommandResult.Fail(Problems.Validation(http, errors));

        ISales sales = http.RequestServices.GetRequiredService<ISales>();
        // SAL-005.1: the first payment fixes the bill, so it is priced again first; a change is saved (in its own unit of
        // work: this command is refused) and the cashier confirms the new total
        IResult? changed = await http.RequestServices.GetRequiredService<IUnitOfWork>().ExecuteAsync(ctx, async (s, c) =>
        {
            SaleResult cart = await sales.LockDraftAsync(s, saleId, null, c);
            if (cart.Outcome != SaleOutcome.Done || cart.Sale!.HasPayments || cart.Sale.CashierId != ctx.User!.Value.Value) return (IResult?)null;
            var (lines, problem) = await Reprice(http, s, cart.Sale, c);
            if (problem is not null) return problem;
            var moved = lines!.Where(x => !SaleMath.SameAmounts(x.Shown, x.Now)).ToList();
            foreach ((SaleLineView shown, SaleLineSpec repriced) in moved)
            {
                await sales.WriteLineAsync(s, saleId, shown.LineNo, repriced, c);
            }

            return moved.Count == 0 ? null : PriceChanged(http, lines!);
        }, ct);
        if (changed is not null) return CommandResult.Fail(changed);

        SaleResult locked = await sales.LockDraftAsync(scope, saleId, null, ct);
        if (locked.Outcome == SaleOutcome.NotFound) return CommandResult.Fail(Problems.NotFound(http));
        if (await MayWorkOn(http, ctx, scope, locked.Sale!, ct) is { } denied) return CommandResult.Fail(denied);
        if (locked.Outcome != SaleOutcome.Done) return CommandResult.Fail(SaleProblem(http, locked.Outcome));
        SaleView sale = locked.Sale!;
        if (sale.Lines.Count == 0 || amount > sale.Remaining)
        {
            return CommandResult.Fail(Problems.WithContext(http, StatusCodes.Status409Conflict, "amount-exceeds-remaining",
                "More than is left to pay on this bill", new { remaining = Money(sale.Remaining) }));
        }

        // PAY-002.1: the day of the reference is the store's local day
        TenantProfile profile = (await http.RequestServices.GetRequiredService<ITenantProfileQuery>().GetAsync(ctx.ActiveTenant!.Value, ct))!;
        DateTimeOffset now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        DateOnly businessDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(profile.TimeZone)).DateTime);
        var origin = new PaymentOrigin(sale.Id, sale.RegisterId, sale.ShiftId, ctx.User!.Value.Value, businessDate);
        IPayments payments = http.RequestServices.GetRequiredService<IPayments>();
        PaymentView payment;
        Guid? overrideId = null;
        if (body.Method == "Cash")
        {
            payment = await payments.RecordCashAsync(scope, amount, tendered, origin, ct);
        }
        else
        {
            PaymentResult card = await payments.RecordManualCardAsync(scope, amount, body.ReferenceNo!, body.TerminalNo, body.CardLast4, null, origin, ct);
            if (card.Outcome == PaymentOutcome.DuplicateReference)
            {
                // the same slip twice is refused; another manager may let it through for this bill and amount (audited)
                var ask = new ApprovalAsk(Permissions.ShiftSupervise, "CardReference",
                    PaymentRules.OverrideBinding(sale.Id, body.TerminalNo, body.ReferenceNo!, businessDate, amount),
                    new ScopeRef(ScopeType.Register, sale.RegisterId, sale.BranchId), "duplicate-reference", body.ReferenceNo, null,
                    $"payment {card.ExistingPaymentId}");
                IApprovals approvals = http.RequestServices.GetRequiredService<IApprovals>();
                if (body.ApprovalId is { } given && await approvals.ConsumeAsync(scope, given, ask, ctx.User!.Value, ct))
                {
                    card = await payments.RecordManualCardAsync(scope, amount, body.ReferenceNo!, body.TerminalNo, body.CardLast4, given, origin, ct);
                    overrideId = given;
                }
                else
                {
                    Guid requested = body.ApprovalId is { } pending && await approvals.GetAsync(scope, pending, ct) is { Status: "Pending" } open
                                     && open.RequestedBy == ctx.User!.Value.Value && open.ResourceId == ask.ResourceId
                        ? pending
                        : await http.RequestServices.GetRequiredService<IUnitOfWork>().ExecuteAsync(ctx, (s, c) => approvals.RequestAsync(s, ask, ctx.User!.Value, c), ct);
                    return CommandResult.Fail(Problems.WithContext(http, StatusCodes.Status409Conflict, "duplicate-reference",
                        "This card reference was already recorded today; a manager may override it",
                        new { existingPaymentId = card.ExistingPaymentId, approvalRequestId = requested }));
                }
            }

            payment = card.Payment!;
        }

        SaleView after = await sales.AddTenderAsync(scope, sale.Id, body.Method!, amount, payment.Id, ct);
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry(overrideId is null ? "sales.tender.recorded" : "payments.reference.override", "Sale",
            sale.Id.ToString(), null, new
            {
                method = body.Method, amount, paymentId = payment.Id, payment.ReferenceNo, payment.TerminalNo, overrideApprovalId = overrideId,
                sale.RegisterId, sale.ShiftId,
            }), ct);
        return CommandResult.Ok(SaleJson(after, payments: await payments.ForSaleAsync(scope, sale.Id, ct)));
    })
    .RequirePermission(Permissions.PosSell);

// SAL-005.1: every line of a cart priced again now, as addSaleLine would (a scale label keeps what it states). A problem
// when a product can no longer be sold (switched off, or no price).
async Task<(List<(SaleLineView Shown, SaleLineSpec Now)>? Lines, IResult? Problem)> Reprice(HttpContext http, IUnitOfWorkScope scope, SaleView sale,
    CancellationToken ct)
{
    IProductCatalog catalog = http.RequestServices.GetRequiredService<IProductCatalog>();
    IReadOnlyList<WeightedBarcodeProfile>? profiles = null;
    var result = new List<(SaleLineView, SaleLineSpec)>();
    foreach (SaleLineView l in sale.Lines)
    {
        if (await catalog.SellableAsync(scope, l.VariantId, ct) is not { } v) return (null, Problems.NotFound(http));
        long? labelAmount = null;
        if (l.WeightedCode is not null)
        {
            profiles ??= (await http.RequestServices.GetRequiredService<IWeightedBarcodeProfiles>().GetAsync(scope, ct)).Profiles;
            var (status, label) = WeightedBarcodes.Decode(profiles, l.WeightedCode);
            if (status == WeightedDecodeStatus.Decoded && label!.Profile.ValueType == WeightedValueType.Price) labelAmount = (long)label.Value;
        }

        var (now, problem) = await PriceLine(http, scope, sale, v, l.Qty, l.Unit, l.UnitFactor, l.WeightedCode, labelAmount, ct);
        if (problem is not null) return (null, problem);
        result.Add((l, now!));
    }

    return (result, null);
}

IResult PriceChanged(HttpContext http, IEnumerable<(SaleLineView Shown, SaleLineSpec Now)> lines) =>
    Problems.WithContext(http, StatusCodes.Status409Conflict, "price-changed", "Prices changed since the cart was shown; confirm the new total", new
    {
        lines = lines.Where(x => !SaleMath.SameAmounts(x.Shown, x.Now)).Select(x => new
        {
            x.Shown.LineNo, x.Shown.Sku, was = Money(x.Shown.LineTotal), now = Money(x.Now.Amounts.LineTotal), unitPrice = Money(x.Now.UnitPrice),
        }),
        total = Money(lines.Sum(x => x.Now.Amounts.LineTotal)),
    });

// ---------------- US-091: Finalize (SAL-007, UC-02 step 8): one transaction takes the bill from paid to invoiced — prices
// recomputed (SAL-005.1), payments complete (SAL-006), stock issued at WAC under the balances' locks (INV-004/006, AT-211),
// a gapless invoice number (BR-056), each line's snapshot and cost (SAL-005), the sale and cost entries (ACC-006, D-04) and
// SaleFinalized in the outbox. Idempotent: a retry with the same key gets the same invoice (AT-210); business errors are
// not stored and may be retried with the same key (D-34).
v1.MapIdempotentPost<FinalizeInput>("/sales/{saleId:guid}/finalize", "sales.finalize", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        Guid saleId = Guid.Parse((string)http.GetRouteValue("saleId")!, System.Globalization.CultureInfo.InvariantCulture);
        var errors = new Dictionary<string, string[]>();
        if (body.ExpectedVersion is null) errors["expectedVersion"] = ["required"];
        if (!TryMoney(body.ExpectedTotal, positive: false, out long expectedTotal)) errors["expectedTotal"] = ["the total the cashier saw, in rials"];
        if (errors.Count > 0) return CommandResult.Fail(Problems.Validation(http, errors));

        ISales sales = http.RequestServices.GetRequiredService<ISales>();
        SaleResult locked = await sales.LockDraftAsync(scope, saleId, body.ExpectedVersion, ct);
        if (locked.Outcome == SaleOutcome.NotFound) return CommandResult.Fail(Problems.NotFound(http));
        if (await MayWorkOn(http, ctx, scope, locked.Sale!, ct) is { } denied) return CommandResult.Fail(denied);   // BR-050
        if (locked.Outcome != SaleOutcome.Done) return CommandResult.Fail(SaleProblem(http, locked.Outcome));
        SaleView sale = locked.Sale!;
        if (sale.Lines.Count == 0) return CommandResult.Fail(Problems.Create(http, StatusCodes.Status409Conflict, "sale-empty", "The cart has no items"));

        // SAL-005.1 / BR-018: the server's price now, compared with what the cashier confirmed
        var (repriced, problem) = await Reprice(http, scope, sale, ct);
        if (problem is not null) return CommandResult.Fail(problem);
        List<(SaleLineView Shown, SaleLineSpec Now)> priced = repriced!;
        if (priced.Any(x => !SaleMath.SameAmounts(x.Shown, x.Now)) || sale.Total != expectedTotal)
        {
            return CommandResult.Fail(PriceChanged(http, priced));
        }

        // SAL-006: what the confirmed payments bring equals the bill
        if (sale.Paid != sale.Total)
        {
            return CommandResult.Fail(Problems.WithContext(http, StatusCodes.Status409Conflict, "payment-incomplete", "The payments do not cover the bill yet",
                new { remaining = Money(sale.Remaining) }));
        }

        // INV-004 / AT-211: issued from the branch's sellable warehouse, under the balances' locks; a shortage refuses the sale
        Guid? warehouse = null;
        string branchCode = "";
        await using (NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT (SELECT id FROM inv.warehouse WHERE branch_id = $1 AND kind = 'Sellable' ORDER BY code LIMIT 1), (SELECT code FROM platform.branch WHERE id = $1)"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = sale.BranchId });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            warehouse = r.IsDBNull(0) ? null : r.GetGuid(0);
            branchCode = r.GetString(1);
        }

        if (warehouse is not Guid warehouseId)
        {
            return CommandResult.Fail(Problems.Create(http, StatusCodes.Status422UnprocessableEntity, "no-sellable-warehouse", "This branch has no sellable warehouse"));
        }

        DateTimeOffset now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        // INV-004.1: what was held for this sale (online payment) is used now: given back first, then issued under the locks
        await http.RequestServices.GetRequiredService<IStockLedger>().EndReservationsAsync(scope, "Sale", sale.Id, ReservationEnd.Consumed, ct);
        StockResult issued = await http.RequestServices.GetRequiredService<IStockLedger>().PostAsync(scope, new StockDocument("Sale", sale.Id, now,
            [.. sale.Lines.Select(l => new StockLine(l.LineNo, warehouseId, l.VariantId, MovementType.SaleIssue, -l.Qty, null,
                l.UnitFactor == 1 ? null : new EnteredUnit(l.Unit, l.UnitFactor)))]), ct);
        if (issued.Outcome == StockOutcome.InsufficientStock)
        {
            return CommandResult.Fail(Problems.WithContext(http, StatusCodes.Status409Conflict, "insufficient-stock", "Not enough stock", new
            {
                shortages = issued.Shortages.Select(x => new { x.WarehouseId, x.VariantId, available = Qty(x.Available), requested = Qty(x.Requested) }),
            }));
        }

        if (issued.Outcome != StockOutcome.Done) throw new InvalidOperationException($"sale {sale.Id}: {issued.Outcome}");

        // BR-056: the number of (branch, Jalali fiscal year), in this transaction
        TenantProfile profile = (await http.RequestServices.GetRequiredService<ITenantProfileQuery>().GetAsync(ctx.ActiveTenant!.Value, ct))!;
        int fiscalYear = FiscalCalendar.FiscalYearOf(now, TimeZoneInfo.FindSystemTimeZoneById(profile.TimeZone));
        string invoiceNo = InvoiceNumbers.Format(branchCode, fiscalYear, await sales.NextInvoiceSequenceAsync(scope, sale.BranchId, fiscalYear, ct));

        // SAL-005: what was sold, as it was, kept with the line
        var finalized = new List<FinalizedLine>();
        foreach ((SaleLineView l, SaleLineSpec _) in priced)
        {
            long cost = -issued.Movements.Single(m => m.Line == l.LineNo).TotalCost;
            finalized.Add(new FinalizedLine(l.LineNo, cost, System.Text.Json.JsonSerializer.Serialize(new
            {
                productName = l.Name, sku = l.Sku, qty = Qty(l.Qty), unit = l.Unit, unitFactor = Qty(l.UnitFactor), weightedCode = l.WeightedCode,
                unitPrice = l.UnitPrice, gross = l.Gross,
                discounts = l.DiscountSource is null ? [] : new[] { new { source = l.DiscountSource, percentBp = l.DiscountBp, amount = l.Discount, ruleId = l.DiscountRuleId } },
                taxCode = (await http.RequestServices.GetRequiredService<IProductCatalog>().SellableAsync(scope, l.VariantId, ct))?.TaxCode,
                taxRate = l.TaxRateBp, taxableAmount = l.Net, taxAmount = l.Tax, lineTotal = l.LineTotal, costAtSale = cost,
            })));
        }

        // ACC-006 / D-04: the sale and its cost, posted with it
        var (_, _, net, tax, _) = SaleMath.Totals(sale.Lines);
        long cash = (sale.Tenders ?? []).Where(t => !t.Voided && t.Method == "Cash").Sum(t => t.Amount);
        long card = (sale.Tenders ?? []).Where(t => !t.Voided && t.Method == "ExternalCardTerminal").Sum(t => t.Amount);
        IJournals journals = http.RequestServices.GetRequiredService<IJournals>();
        foreach (PostingDraft? draft in new[] { SaleRule.For(sale.Id, invoiceNo, cash, card, net, tax, now), SaleCostRule.For(sale.Id, invoiceNo, finalized.Sum(f => f.CostAtSale), now) })
        {
            if (draft is null) continue;
            AccountingResult<JournalView> entry = await journals.PostAutomaticAsync(scope, draft, ct);
            if (entry.Outcome != AccountingOutcome.Done) return CommandResult.Fail(AutomaticEntryProblem(http, entry));
        }

        SaleView after = await sales.FinalizeAsync(scope, sale.Id, invoiceNo, fiscalYear, finalized, ct);
        // BR-086 (US-092): the receipt cites the template version in force now and the store's footer and signature as they are now
        if (await http.RequestServices.GetRequiredService<ITenantProfileQuery>().GetAsync(ctx.ActiveTenant!.Value, ct) is { } shop
            && await http.RequestServices.GetRequiredService<IIndustryTemplates>().LatestAsync(scope, shop.IndustryCode, ct) is { } template)
        {
            ReceiptOverride own = await http.RequestServices.GetRequiredService<IReceiptTemplateOverrides>().GetAsync(scope, ct);
            await sales.SnapshotReceiptAsync(scope, sale.Id, template.Ref, own.FooterText, own.Signature, ct);
        }
        await http.RequestServices.GetRequiredService<IOutbox>().AddAsync(scope, "Sales", "sales.sale-finalized", 1, sale.Id, new
        {
            saleId = sale.Id, invoiceNo, after.BranchId, after.RegisterId, after.ShiftId, after.CashierId, after.TerminalId, after.Channel,
            total = after.Total, net, tax, cash, card, finalizedAt = after.FinalizedAt,
        }, causationId: null, ct);
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("sales.sale.finalized", "Sale", sale.Id.ToString(), null, new
        {
            invoiceNo, total = after.Total, cash, card, lines = after.Lines.Count, after.RegisterId, after.ShiftId,
        }), ct);
        return CommandResult.Ok(SaleJson(after, issued.Warnings, await http.RequestServices.GetRequiredService<IPayments>().ForSaleAsync(scope, sale.Id, ct)));
    })
    .RequirePermission(Permissions.PosSell);

// Taking a payment back before Finalize (a wrong amount or slip, or a price that changed after payment): the tender is marked
// voided with its reason, never removed; it stops counting toward the bill, the drawer and the entry, and once none is left
// the lines can change again. The cashier gives the money back or reverses the card on the bank terminal (audited, AB-03).
v1.MapIdempotentPost<TenderVoidInput>("/pos/sales/{saleId:guid}/tenders/{seq:int}/void", "pos.void-tender", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        Guid saleId = Guid.Parse((string)http.GetRouteValue("saleId")!, System.Globalization.CultureInfo.InvariantCulture);
        int seq = int.Parse((string)http.GetRouteValue("seq")!, System.Globalization.CultureInfo.InvariantCulture);
        var errors = new Dictionary<string, string[]>();
        if (body.ExpectedVersion is null) errors["expectedVersion"] = ["required"];
        string? reason = body.Reason?.Trim();
        if (reason is not { Length: >= 3 and <= 200 }) errors["reason"] = ["3-200 characters"];
        if (errors.Count > 0) return CommandResult.Fail(Problems.Validation(http, errors));

        ISales sales = http.RequestServices.GetRequiredService<ISales>();
        SaleResult locked = await sales.LockDraftAsync(scope, saleId, body.ExpectedVersion, ct);
        if (locked.Outcome == SaleOutcome.NotFound) return CommandResult.Fail(Problems.NotFound(http));
        if (await MayWorkOn(http, ctx, scope, locked.Sale!, ct) is { } denied) return CommandResult.Fail(denied);
        if (locked.Outcome != SaleOutcome.Done) return CommandResult.Fail(SaleProblem(http, locked.Outcome));
        SaleView sale = locked.Sale!;
        if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.PosSaleCancel, new ScopeRef(ScopeType.Register, sale.RegisterId, sale.BranchId)))
        {
            return CommandResult.Fail(Forbidden(http));
        }

        if (sale.Tenders?.FirstOrDefault(t => t.Seq == seq) is not { Voided: false } tender) return CommandResult.Fail(Problems.NotFound(http));
        SaleView after = (await sales.VoidTenderAsync(scope, saleId, seq, ctx.User!.Value.Value, reason!, ct))!;
        IPayments payments = http.RequestServices.GetRequiredService<IPayments>();
        if (tender.PaymentId is { } paymentId) await payments.VoidAsync(scope, paymentId, ctx.User!.Value.Value, ct);
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("sales.tender.voided", "Sale", saleId.ToString(), reason, new
        {
            seq, tender.Method, amount = tender.Amount, tender.PaymentId, sale.RegisterId, sale.ShiftId, terminalId = ctx.Terminal?.TerminalId,
        }), ct);
        return CommandResult.Ok(SaleJson(after, payments: await payments.ForSaleAsync(scope, saleId, ct)));
    })
    .RequirePermission(Permissions.PosSaleCancel);

// ---------------- US-064: transfers between warehouses (INV-008, MVP single-step): TransferOut from the source at its WAC and
// TransferIn to the destination carrying the same value, in one transaction; both warehouses need inventory.transfer.
object TransferJson(TransferRecord t, IEnumerable<PostedMovement>? issued = null) => new
{
    t.Id, t.FromWarehouseId, t.ToWarehouseId, t.Status, t.LineCount, value = Rial(t.Value), t.Note, t.CreatedBy, t.PostedAt,
    lines = issued?.Select(m => new { m.Line, m.VariantId, qty = Qty(-m.QtyBase), unitCost = Rial(m.UnitCost), value = Rial(-m.TotalCost) }),
};

v1.MapIdempotentPost<TransferInput>("/inventory/transfers", "inventory.post-transfer", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        var errors = new Dictionary<string, string[]>();
        if (body.FromWarehouseId is null) errors["fromWarehouseId"] = ["required"];
        if (body.ToWarehouseId is null) errors["toWarehouseId"] = ["required"];
        else if (body.ToWarehouseId == body.FromWarehouseId) errors["toWarehouseId"] = ["another warehouse than the source"];
        string? note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
        if (note is { Length: > 500 }) errors["note"] = ["up to 500 characters"];
        var lines = new List<(Guid Variant, decimal Qty)>();
        if (body.Lines is not { Length: >= 1 and <= TransferRules.MaxLines }) errors["lines"] = [$"1-{TransferRules.MaxLines} lines"];
        for (int i = 0; i < (body.Lines?.Length ?? 0) && body.Lines!.Length <= TransferRules.MaxLines; i++)
        {
            TransferLineInput? l = body.Lines[i];
            if (l?.VariantId is null) errors[$"lines[{i}].variantId"] = ["required"];
            if (l?.Qty is null || !ProductRules.TryFactor(l.Qty, out decimal qty)) errors[$"lines[{i}].qty"] = ["positive decimal in the base unit, up to 6 places"];
            else if (l.VariantId is { } v) lines.Add((v, qty));
        }

        if (lines.Select(l => l.Variant).Distinct().Count() != lines.Count) errors["lines"] = ["one line per variant"];
        if (errors.Count > 0) return CommandResult.Fail(Problems.Validation(http, errors));

        var lookups = http.RequestServices.GetServices<IScopeLookup>();
        ScopeRef? from = await ResolveScope(scope, lookups, ScopeType.Warehouse, body.FromWarehouseId, ct);
        ScopeRef? to = await ResolveScope(scope, lookups, ScopeType.Warehouse, body.ToWarehouseId, ct);
        if (from is null || to is null)
        {
            return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]>
            {
                [from is null ? "fromWarehouseId" : "toWarehouseId"] = ["not a warehouse of this store"],
            }));
        }

        // stock leaves one place and arrives at another: the person moves it in both
        if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.InventoryTransfer, from)
            || !AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.InventoryTransfer, to))
        {
            return CommandResult.Fail(Forbidden(http));
        }

        IReadOnlySet<Guid> known = await http.RequestServices.GetRequiredService<IProductCatalog>().ExistingVariantsAsync(scope, [.. lines.Select(l => l.Variant)], ct);
        if (lines.FindIndex(l => !known.Contains(l.Variant)) is var unknown and >= 0)
        {
            return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { [$"lines[{unknown}].variantId"] = ["not a variant of this store"] }));
        }

        Guid id = Guid.CreateVersion7();
        DateTimeOffset now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        IStockLedger ledger = http.RequestServices.GetRequiredService<IStockLedger>();
        List<StockLine> outbound = [.. lines.Select((l, i) => new StockLine(i + 1, body.FromWarehouseId!.Value, l.Variant, MovementType.TransferOut, -l.Qty))];
        StockResult issued = await ledger.PostAsync(scope, new StockDocument("Transfer", id, now, outbound, "out"), ct);
        if (issued.Outcome == StockOutcome.InsufficientStock)
        {
            return CommandResult.Fail(Problems.WithContext(http, StatusCodes.Status409Conflict, "insufficient-stock", "Not enough stock", new
            {
                shortages = issued.Shortages.Select(x => new { x.WarehouseId, x.VariantId, available = Qty(x.Available), requested = Qty(x.Requested) }),
            }));
        }

        if (issued.Outcome != StockOutcome.Done) throw new InvalidOperationException($"transfer {id} out: {issued.Outcome}");
        StockResult received = await ledger.PostAsync(scope, new StockDocument("Transfer", id, now,
            TransferRules.Inbound(body.ToWarehouseId!.Value, outbound, issued.Movements), "in"), ct);
        if (received.Outcome != StockOutcome.Done) throw new InvalidOperationException($"transfer {id} in: {received.Outcome}");

        TransferRecord record = await http.RequestServices.GetRequiredService<IStockTransfers>().RecordAsync(scope, new TransferRecord(id, body.FromWarehouseId!.Value,
            body.ToWarehouseId.Value, "Completed", lines.Count, -issued.Movements.Sum(m => m.TotalCost), note, ctx.User!.Value.Value, now), ct);
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("inventory.transfer.posted", "StockTransfer", id.ToString(), note, new
        {
            from = record.FromWarehouseId, to = record.ToWarehouseId, value = record.Value, lines = lines.Take(50).Select(l => new { variantId = l.Variant, qty = Qty(l.Qty) }),
        }), ct);
        return CommandResult.Created(TransferJson(record, issued.Movements));
    })
    .RequirePermission(Permissions.InventoryTransfer);

v1.MapGet("/inventory/transfers", async (RequestContext ctx, IUnitOfWork uow, IStockTransfers transfers, HttpContext http, CancellationToken ct) =>
    {
        var lookups = http.RequestServices.GetServices<IScopeLookup>();
        return Results.Ok(await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var rows = await transfers.ListAsync(s, c);
            var canSee = new Dictionary<Guid, bool>();
            foreach (Guid w in rows.SelectMany(t => new[] { t.FromWarehouseId, t.ToWarehouseId }).Distinct())
            {
                canSee[w] = await ResolveScope(s, lookups, ScopeType.Warehouse, w, c) is { } scope
                            && AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.ReportsView, scope);
            }

            return new { items = rows.Where(t => canSee[t.FromWarehouseId] || canSee[t.ToWarehouseId]).Select(t => TransferJson(t)) };
        }, ct));
    })
    .RequirePermission(Permissions.ReportsView);

// ---------------- US-083: hold a cart and resume it later, on another register of the branch too (SAL-011); cancel a cart
// without payments in force (D02 §6.1). The database allows only the state table's transitions (RC031).
v1.MapPost("/pos/sales/{saleId:guid}/hold", async (Guid saleId, HoldInput body, RequestContext ctx, IUnitOfWork uow, ISales sales, HttpContext http,
        CancellationToken ct) =>
    {
        string? note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
        if (body.ExpectedVersion is null || note is { Length: > 100 })
        {
            return Problems.Validation(http, new Dictionary<string, string[]> { [body.ExpectedVersion is null ? "expectedVersion" : "note"] = ["expectedVersion required; note up to 100 characters"] });
        }

        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            SaleResult locked = await sales.LockDraftAsync(s, saleId, body.ExpectedVersion, c);
            if (locked.Outcome == SaleOutcome.NotFound) return Problems.NotFound(http);
            if (await MayWorkOn(http, ctx, s, locked.Sale!, c) is { } denied) return denied;
            if (locked.Outcome != SaleOutcome.Done) return SaleProblem(http, locked.Outcome);
            if (locked.Sale!.HasPayments) return PaidInto(http);
            if (locked.Sale.Lines.Count == 0) return Problems.Create(http, StatusCodes.Status409Conflict, "sale-empty", "An empty cart is not held");
            return Results.Ok(SaleJson(await sales.HoldAsync(s, saleId, note, c)));
        }, ct);
    })
    .RequirePermission(Permissions.PosSell);

v1.MapGet("/pos/held-sales", async (RequestContext ctx, IUnitOfWork uow, ISales sales, HttpContext http, CancellationToken ct) =>
    {
        if (ctx.Terminal is not { } terminal)
        {
            return Problems.Create(http, StatusCodes.Status403Forbidden, "terminal-not-registered", "Use a registered device of this branch (ABAC-04)");
        }

        var held = await uow.ExecuteAsync(ctx, (s, c) => sales.HeldAsync(s, terminal.BranchId, c), ct);
        return Results.Ok(new
        {
            items = held.Select(x => new
            {
                x.Id, x.HoldNote, total = Money(x.Total), lines = x.Lines.Count, x.CashierId, x.RegisterId, heldAt = x.LastActivityAt, x.Version,
            }),
        });
    })
    .RequirePermission(Permissions.PosSell);

// SAL-011: resumed by a cashier with an open shift on a register of the same branch; the cart moves to that register and shift
v1.MapPost("/pos/sales/{saleId:guid}/resume", async (Guid saleId, ResumeInput body, RequestContext ctx, IUnitOfWork uow, ISales sales, IShifts shifts,
        HttpContext http, CancellationToken ct) =>
    {
        if (body.ExpectedVersion is null) return Problems.Validation(http, new Dictionary<string, string[]> { ["expectedVersion"] = ["required"] });
        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            SaleResult locked = await sales.LockAsync(s, saleId, body.ExpectedVersion, c);
            if (locked.Outcome == SaleOutcome.NotFound) return Problems.NotFound(http);
            SaleView sale = locked.Sale!;
            if (ctx.Terminal is not { } terminal)
            {
                return Problems.Create(http, StatusCodes.Status403Forbidden, "terminal-not-registered", "Use a registered device of this branch (ABAC-04)");
            }

            if (terminal.BranchId != sale.BranchId) return Problems.NotFound(http);   // another branch's cart is not found from here

            if (locked.Outcome != SaleOutcome.Done) return SaleProblem(http, locked.Outcome);
            if (sale.Status != "Held") return Problems.Create(http, StatusCodes.Status409Conflict, "sale-not-held", "Only a held cart is resumed");
            Guid registerId = body.RegisterId ?? sale.RegisterId;
            if (await ResolveScope(s, http.RequestServices.GetServices<IScopeLookup>(), ScopeType.Register, registerId, c) is not { } register
                || register.BranchId != sale.BranchId)
            {
                return Problems.Validation(http, new Dictionary<string, string[]> { ["registerId"] = ["a register of the cart's branch"] });
            }

            if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.PosSell, register)) return Forbidden(http);
            if (await shifts.CurrentAsync(s, registerId, c) is not { Status: "Open" } shift || shift.CashierId != ctx.User!.Value.Value)
            {
                return Problems.Create(http, StatusCodes.Status409Conflict, "shift-not-open", "Open your shift on this register first (BR-050)");
            }

            SaleView resumed = await sales.ResumeAsync(s, saleId, new SaleOrigin(sale.BranchId, registerId, shift.Id, ctx.User!.Value.Value, terminal.TerminalId,
                sale.Channel, sale.PricingMode), c);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("sales.sale.resumed", "Sale", saleId.ToString(), null, new
            {
                fromRegister = sale.RegisterId, fromCashier = sale.CashierId, toRegister = registerId, shiftId = shift.Id,
            }), c);
            return Results.Ok(SaleJson(resumed));
        }, ct);
    })
    .RequirePermission(Permissions.PosSell);

// SAL-004 / AB-03: a cart without payments in force is cancelled with a reason (audited); its cashier, or a supervisor of the branch
v1.MapPost("/pos/sales/{saleId:guid}/cancel", async (Guid saleId, CancelInput body, RequestContext ctx, IUnitOfWork uow, ISales sales,
        HttpContext http, CancellationToken ct) =>
    {
        string? reason = body.Reason?.Trim();
        if (body.ExpectedVersion is null || reason is not { Length: >= 3 and <= 200 })
        {
            return Problems.Validation(http, new Dictionary<string, string[]> { [body.ExpectedVersion is null ? "expectedVersion" : "reason"] = ["expectedVersion required; reason 3-200 characters"] });
        }

        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            SaleResult locked = await sales.LockAsync(s, saleId, body.ExpectedVersion, c);
            if (locked.Outcome == SaleOutcome.NotFound) return Problems.NotFound(http);
            SaleView sale = locked.Sale!;
            var register = new ScopeRef(ScopeType.Register, sale.RegisterId, sale.BranchId);
            bool own = sale.CashierId == ctx.User!.Value.Value && AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.PosSaleCancel, register);
            bool supervisor = AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.ShiftSupervise, new ScopeRef(ScopeType.Branch, sale.BranchId, sale.BranchId));
            if (!own && !supervisor) return Problems.NotFound(http);
            if (locked.Outcome != SaleOutcome.Done) return SaleProblem(http, locked.Outcome);
            if (sale.Status is not ("Draft" or "Held")) return Problems.Create(http, StatusCodes.Status409Conflict, "sale-not-draft", "Only a draft or held cart is cancelled here");
            if (sale.HasPayments) return PaidInto(http);
            SaleView cancelled = await sales.CancelAsync(s, saleId, reason!, c);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("sales.sale.cancelled", "Sale", saleId.ToString(), reason, new
            {
                total = sale.Total, lines = sale.Lines.Count, sale.RegisterId, sale.ShiftId, sale.CashierId,
            }), c);
            return Results.Ok(SaleJson(cancelled));
        }, ct);
    })
    .RequirePermission(Permissions.PosSaleCancel);

// ---------------- US-065: stock counts (INV-009, UC-09, AT-280). Counting needs inventory.count in the warehouse; confirming
// (which corrects the stock) needs inventory.adjust, and an approval when the confirmer counted too (SoD-03) or the
// correction is worth more than the threshold (BR-033).
object CountJson(CountView c) => new
{
    c.Id, c.WarehouseId, c.Scope, c.Blind, c.Status, c.SnapshotAt, c.ClosedAt, c.ConfirmedAt, c.AdjustmentId, c.CreatedBy, c.Version, counters = c.Counters.Count,
};

IResult CountProblem(HttpContext http, CountOutcome outcome) => outcome switch
{
    CountOutcome.NotFound => Problems.NotFound(http),
    CountOutcome.AlreadyOpen => Problems.Create(http, StatusCodes.Status409Conflict, "count-open", "This warehouse already has a count in progress"),
    CountOutcome.NotOpen => Problems.Create(http, StatusCodes.Status409Conflict, "count-not-open", "The count is closed: nothing more is counted"),
    CountOutcome.NotClosed => Problems.Create(http, StatusCodes.Status409Conflict, "count-not-closed", "Close the count before confirming it"),
    _ => Problems.Create(http, StatusCodes.Status409Conflict, "version-conflict", "The count changed; reload and retry"),
};

// the count's warehouse, if the caller holds <permission> there; otherwise not found / forbidden
async Task<(ScopeRef? Warehouse, IResult? Problem)> CountScope(HttpContext http, RequestContext ctx, IUnitOfWorkScope s, CountView? count, string permission,
    CancellationToken ct)
{
    if (count is null || await ResolveScope(s, http.RequestServices.GetServices<IScopeLookup>(), ScopeType.Warehouse, count.WarehouseId, ct) is not { } warehouse)
    {
        return (null, Problems.NotFound(http));
    }

    if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.InventoryCount, warehouse)) return (null, Problems.NotFound(http));
    return AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, permission, warehouse) ? (warehouse, null) : (null, Forbidden(http));
}

v1.MapIdempotentPost<CountCreateInput>("/inventory/counts", "inventory.create-count", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        var errors = new Dictionary<string, string[]>();
        if (body.WarehouseId is null) errors["warehouseId"] = ["required"];
        if (body.Scope is not (null or "All")) errors["scope"] = [body.Scope == "Categories" ? "not-yet-supported" : "All|Categories"];   // categories: with the category tree
        if (errors.Count > 0) return CommandResult.Fail(Problems.Validation(http, errors));
        if (await ResolveScope(scope, http.RequestServices.GetServices<IScopeLookup>(), ScopeType.Warehouse, body.WarehouseId, ct) is not { } warehouse)
        {
            return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { ["warehouseId"] = ["not a warehouse of this store"] }));
        }

        if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.InventoryCount, warehouse)) return CommandResult.Fail(Forbidden(http));
        CountResult created = await http.RequestServices.GetRequiredService<ICounts>().CreateAsync(scope, body.WarehouseId!.Value, body.Blind ?? true,
            ctx.User!.Value.Value, ct);
        if (created.Outcome != CountOutcome.Done) return CommandResult.Fail(CountProblem(http, created.Outcome));
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("inventory.count.opened", "InventoryCount", created.Count!.Id.ToString(),
            Details: new { body.WarehouseId, created.Count.Blind }), ct);
        return CommandResult.Created(CountJson(created.Count));
    })
    .RequirePermission(Permissions.InventoryCount);

v1.MapGet("/inventory/counts", async (RequestContext ctx, IUnitOfWork uow, ICounts counts, HttpContext http, Guid? warehouseId, CancellationToken ct) =>
    {
        var lookups = http.RequestServices.GetServices<IScopeLookup>();
        return Results.Ok(await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var visible = new List<object>();
            foreach (CountView count in await counts.ListAsync(s, warehouseId, c))
            {
                if (await ResolveScope(s, lookups, ScopeType.Warehouse, count.WarehouseId, c) is { } w
                    && AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.InventoryCount, w))
                {
                    visible.Add(CountJson(count));
                }
            }

            return new { items = visible };
        }, ct));
    })
    .RequirePermission(Permissions.InventoryCount);

v1.MapGet("/inventory/counts/{countId:guid}", async (Guid countId, RequestContext ctx, IUnitOfWork uow, ICounts counts, HttpContext http, CancellationToken ct) =>
        await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            CountView? count = await counts.GetAsync(s, countId, c);
            var (_, problem) = await CountScope(http, ctx, s, count, Permissions.InventoryCount, c);
            return problem ?? Results.Ok(CountJson(count!));
        }, ct))
    .RequirePermission(Permissions.InventoryCount);

// several people count at once, each batch adding up; a batch synced again (offline device) adds nothing (UC-09)
v1.MapPost("/inventory/counts/{countId:guid}/entries", async (Guid countId, CountEntriesInput body, RequestContext ctx, IUnitOfWork uow, ICounts counts,
        IProductCatalog catalog, HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        if (body.ClientBatchId is not { Length: >= 8 and <= 64 } || !body.ClientBatchId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            errors["clientBatchId"] = ["8-64 of A-Z a-z 0-9 _ -"];
        }

        var entries = new List<(Guid, decimal)>();
        if (body.Entries is not { Length: >= 1 and <= CountRules.MaxEntries }) errors["entries"] = [$"1-{CountRules.MaxEntries} entries"];
        for (int i = 0; i < (body.Entries?.Length ?? 0) && body.Entries!.Length <= CountRules.MaxEntries; i++)
        {
            CountEntryInput? e = body.Entries[i];
            if (e?.VariantId is null) errors[$"entries[{i}].variantId"] = ["required"];
            if (!AdjustmentRules.TryDelta(e?.Qty, out decimal qty)) errors[$"entries[{i}].qty"] = ["non-zero decimal in the base unit (negative corrects a wrong scan)"];
            else if (e?.VariantId is { } v) entries.Add((v, qty));
        }

        if (errors.Count > 0) return Problems.Validation(http, errors);
        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var (_, problem) = await CountScope(http, ctx, s, await counts.GetAsync(s, countId, c), Permissions.InventoryCount, c);
            if (problem is not null) return problem;
            IReadOnlySet<Guid> known = await catalog.ExistingVariantsAsync(s, [.. entries.Select(x => x.Item1)], c);
            if (entries.FindIndex(x => !known.Contains(x.Item1)) is var unknown and >= 0)
            {
                return Problems.Validation(http, new Dictionary<string, string[]> { [$"entries[{unknown}].variantId"] = ["not a variant of this store"] });
            }

            CountOutcome added = await counts.AddEntriesAsync(s, countId, body.ClientBatchId!, entries, ctx.User!.Value.Value, c);
            return added == CountOutcome.Done ? Results.NoContent() : CountProblem(http, added);
        }, ct);
    })
    .RequirePermission(Permissions.InventoryCount);

v1.MapPost("/inventory/counts/{countId:guid}/close", async (Guid countId, VersionInput body, RequestContext ctx, IUnitOfWork uow, ICounts counts,
        HttpContext http, CancellationToken ct) =>
    {
        if (body.ExpectedVersion is null) return Problems.Validation(http, new Dictionary<string, string[]> { ["expectedVersion"] = ["required"] });
        return await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            var (_, problem) = await CountScope(http, ctx, s, await counts.GetAsync(s, countId, c), Permissions.InventoryCount, c);
            if (problem is not null) return problem;
            CountResult closed = await counts.CloseAsync(s, countId, body.ExpectedVersion.Value, ctx.User!.Value.Value, c);
            if (closed.Outcome != CountOutcome.Done) return CountProblem(http, closed.Outcome);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("inventory.count.closed", "InventoryCount", countId.ToString(),
                Details: new { closed.Count!.WarehouseId, counters = closed.Count.Counters.Count }), c);
            return Results.Ok(CountJson(closed.Count));
        }, ct);
    })
    .RequirePermission(Permissions.InventoryCount);

// AT-280: difference = counted − (snapshot + movements since); valued at the WAC of now. A blind count shows the system
// quantities, while it is open, only to who may correct the stock.
v1.MapGet("/inventory/counts/{countId:guid}/result", async (Guid countId, RequestContext ctx, IUnitOfWork uow, ICounts counts, IStockLedger ledger,
        HttpContext http, CancellationToken ct) =>
        await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            CountView? count = await counts.GetAsync(s, countId, c);
            var (warehouse, problem) = await CountScope(http, ctx, s, count, Permissions.InventoryCount, c);
            if (problem is not null) return problem;
            bool mayAdjust = AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.InventoryAdjust, warehouse!);
            if (count!.Blind && count.Status == "Open" && !mayAdjust)
            {
                return Problems.Create(http, StatusCodes.Status403Forbidden, "count-blind", "A blind count shows the system quantities to who confirms it, once closed");
            }
            var wac = (await ledger.BalancesAsync(s, count.WarehouseId, null, c)).ToDictionary(b => b.VariantId, b => b.Wac);
            var lines = (await counts.LinesAsync(s, countId, c)).Select(l => new
            {
                l, value = SaleMath.Round(l.Difference * wac.GetValueOrDefault(l.VariantId)),
            }).ToList();
            long total = lines.Sum(x => x.value);
            long magnitude = lines.Sum(x => Math.Abs(x.value));
            return Results.Ok(new
            {
                lines = lines.Select(x => new
                {
                    x.l.VariantId, systemAtSnapshot = Qty(x.l.SystemAtSnapshot), movementsSince = Qty(x.l.MovementsSince), counted = Qty(x.l.Counted),
                    wasCounted = x.l.WasCounted, difference = Qty(x.l.Difference), value = Rial(x.value),
                }),
                totalValueDifference = Rial(total),
                approvalRequired = CountRules.IsCounter(count, ctx.User!.Value.Value) || AdjustmentRules.NeedsApproval(magnitude, adjustmentThreshold),
            });
        }, ct))
    .RequirePermission(Permissions.InventoryCount);

v1.MapIdempotentPost<VersionInput>("/inventory/counts/{countId:guid}/confirm", "inventory.confirm-count", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        Guid countId = Guid.Parse((string)http.GetRouteValue("countId")!, System.Globalization.CultureInfo.InvariantCulture);
        if (body.ExpectedVersion is null) return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { ["expectedVersion"] = ["required"] }));
        ICounts counts = http.RequestServices.GetRequiredService<ICounts>();
        var (warehouse, problem) = await CountScope(http, ctx, scope, await counts.GetAsync(scope, countId, ct), Permissions.InventoryAdjust, ct);
        if (problem is not null) return CommandResult.Fail(problem);
        CountResult locked = await counts.LockClosedAsync(scope, countId, body.ExpectedVersion.Value, ct);
        if (locked.Outcome != CountOutcome.Done) return CommandResult.Fail(CountProblem(http, locked.Outcome));
        CountView count = locked.Count!;

        // the corrections: every line whose count differs from the system at close
        List<AdjustmentLine> corrections = [.. (await counts.LinesAsync(scope, countId, ct)).Where(l => l.Difference != 0)
            .Select(l => new AdjustmentLine(l.VariantId, l.Difference))];
        Guid? adjustmentId = null;
        if (corrections.Count > 0)
        {
            bool counted = CountRules.IsCounter(count, ctx.User!.Value.Value);
            string binding = CountRules.Binding(countId, corrections);
            var (failed, record, _) = await PostAdjustment(http, scope, ctx, Guid.CreateVersion7(), count.WarehouseId, warehouse!, "CountError",
                $"انبارگردانی {count.SnapshotAt:yyyy-MM-dd}", corrections, MovementType.CountAdjustment,
                v => counted ? ("sod-03", null, binding)
                    : AdjustmentRules.NeedsApproval(v, adjustmentThreshold) ? ("value-threshold", Rial(adjustmentThreshold), binding) : null, ct);
            if (failed is not null) return failed;
            adjustmentId = record!.Id;
        }

        CountView confirmed = await counts.ConfirmAsync(scope, countId, adjustmentId, ctx.User!.Value.Value, ct);
        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("inventory.count.confirmed", "InventoryCount", countId.ToString(),
            Details: new { confirmed.WarehouseId, adjustmentId, corrections = corrections.Count }), ct);
        return CommandResult.Ok(new { count = CountJson(confirmed), adjustmentId });
    })
    .RequirePermission(Permissions.InventoryAdjust);

// ---------------- US-053: automatic discount rules (CAT-010, D-18) and manual discount caps (BR-021)
object RuleJson(DiscountRuleView r) => new
{
    r.Id, r.Name, target = new { kind = r.TargetKind, id = r.TargetId }, percent = r.PercentBp, r.ValidFrom, r.ValidTo, branchIds = r.BranchIds,
};

// a store-wide rule needs the permission store-wide; a branch rule needs it in each of its branches
bool MayManageRule(RequestContext ctx, IReadOnlyCollection<Guid> branchIds) =>
    branchIds.Count == 0
        ? AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.CatalogPriceManage, ScopeRef.Tenant)
        : branchIds.All(b => AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.CatalogPriceManage, new ScopeRef(ScopeType.Branch, b, b)));

v1.MapGet("/discount-rules", async (RequestContext ctx, IUnitOfWork uow, IDiscounts discounts, bool? includeEnded, CancellationToken ct) =>
        Results.Ok(new { items = (await uow.ExecuteAsync(ctx, (s, c) => discounts.ListRulesAsync(s, includeEnded ?? false, c), ct)).Select(RuleJson) }))
    .RequireTenantMember();

v1.MapPost("/discount-rules", async (DiscountRuleInput body, RequestContext ctx, IUnitOfWork uow, IDiscounts discounts, IProductCatalog catalog,
        HttpContext http, TimeProvider clock, CancellationToken ct) =>
    {
        DateTimeOffset now = clock.GetUtcNow();
        var errors = new Dictionary<string, string[]>();
        ProductRules.Name(errors, "name", body.Name);
        if (body.Target?.Kind is not ("Product" or "Category") || body.Target.Id is null) errors["target"] = ["{kind: Product|Category, id}"];
        else if (body.Target.Kind == "Category") errors["target.kind"] = ["not-yet-supported"];   // categories come with the catalog tree
        if (body.Percent is not (>= 1 and <= 10_000)) errors["percent"] = ["1-10000 basis points"];
        if (body.ValidFrom is not { } from || from > now.AddDays(366)) errors["validFrom"] = ["up to a year ahead (an earlier instant starts now)"];
        if (body.ValidTo is { } to && (to <= (body.ValidFrom ?? now) || to <= now)) errors["validTo"] = ["after validFrom and in the future"];
        Guid[] branchIds = [.. (body.BranchIds ?? []).Distinct()];
        if (branchIds.Length > 50) errors["branchIds"] = ["up to 50"];
        if (errors.Count > 0) return Problems.Validation(http, errors);
        if (!MayManageRule(ctx, branchIds)) return Forbidden(http);

        var result = await uow.ExecuteAsync<(IResult? Problem, DiscountRuleView? Rule)>(ctx, async (s, c) =>
        {
            if (await catalog.GetAsync(s, body.Target!.Id!.Value, c) is not { Status: not "Merged" })
            {
                return (Problems.Validation(http, new Dictionary<string, string[]> { ["target.id"] = ["not a product of this store"] }), null);
            }

            foreach (Guid b in branchIds)
            {
                if (await ResolveScope(s, http.RequestServices.GetServices<IScopeLookup>(), ScopeType.Branch, b, c) is null)
                {
                    return (Problems.Validation(http, new Dictionary<string, string[]> { ["branchIds"] = [$"{b}: not a branch of this store"] }), null);
                }
            }

            DateTimeOffset start = body.ValidFrom!.Value > now ? body.ValidFrom.Value : now;
            DiscountRuleView rule = await discounts.CreateRuleAsync(s, new DiscountRuleSpec(body.Name!, "Product", body.Target.Id.Value, body.Percent!.Value,
                start, body.ValidTo, branchIds), ctx.User!.Value.Value, c);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("catalog.discount-rule.created", "DiscountRule", rule.Id.ToString(),
                Details: new { rule.Name, productId = rule.TargetId, rule.PercentBp, rule.ValidFrom, rule.ValidTo, branchIds }), c);
            return (null, rule);
        }, ct);
        return result.Problem ?? Results.Created($"/api/v1/discount-rules/{result.Rule!.Id}", RuleJson(result.Rule));
    })
    .RequirePermission(Permissions.CatalogPriceManage);

v1.MapDelete("/discount-rules/{ruleId:guid}", async (Guid ruleId, RequestContext ctx, IUnitOfWork uow, IDiscounts discounts, HttpContext http,
        CancellationToken ct) =>
        await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            DiscountRuleView? rule = await discounts.GetRuleAsync(s, ruleId, c);
            if (rule is null) return Problems.NotFound(http);
            if (!MayManageRule(ctx, rule.BranchIds)) return Forbidden(http);
            if (!await discounts.EndRuleAsync(s, ruleId, c)) return Problems.NotFound(http);   // already ended
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("catalog.discount-rule.ended", "DiscountRule", ruleId.ToString(),
                Details: new { rule.Name, rule.PercentBp }), c);
            return Results.NoContent();
        }, ct))
    .RequirePermission(Permissions.CatalogPriceManage);

v1.MapGet("/discount-caps", async (RequestContext ctx, IUnitOfWork uow, IDiscounts discounts, CancellationToken ct) =>
    {
        IReadOnlyDictionary<string, int> set = await uow.ExecuteAsync(ctx, (s, c) => discounts.CapsAsync(s, c), ct);
        return Results.Ok(new { caps = DiscountPolicy.DefaultCaps.Keys.ToDictionary(r => r, r => set.TryGetValue(r, out int v) ? v : DiscountPolicy.DefaultCaps[r]) });
    })
    .RequireTenantMember();

v1.MapPut("/discount-caps", async (DiscountCapsInput body, RequestContext ctx, IUnitOfWork uow, IDiscounts discounts, HttpContext http, CancellationToken ct) =>
    {
        if (body.Caps is not { Count: >= 1 } || body.Caps.Any(c => !DiscountPolicy.DefaultCaps.ContainsKey(c.Key) || c.Value is not (>= 0 and <= 10_000)))
        {
            return Problems.Validation(http, new Dictionary<string, string[]> { ["caps"] = ["{TenantOwner|TenantAdmin|BranchManager|Cashier: 0-10000 basis points}"] });
        }

        await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            await discounts.SetCapsAsync(s, body.Caps, ctx.User!.Value.Value, c);
            await Audit(http, s, ActorOf(ctx, http), new AuditEntry("catalog.discount-caps.set", "ManualDiscountCap", null, Details: body.Caps), c);
            return 0;
        }, ct);
        return Results.NoContent();
    })
    .RequirePermission(Permissions.TenantSettingsManage);

// ---------------- US-052: versioned prices (CAT-009, BR-041, AB-06)
// BR-041 [ASM]: thresholds are tenant settings later; until then Catalog:PriceBulkLimit and Catalog:PriceChangePercent.
int priceBulkLimit = builder.Configuration.GetValue<int?>("Catalog:PriceBulkLimit") ?? 20;
int priceChangePercent = builder.Configuration.GetValue<int?>("Catalog:PriceChangePercent") ?? 30;

v1.MapGet("/price-lists", async (RequestContext ctx, IUnitOfWork uow, IPriceLists prices, CancellationToken ct) =>
        Results.Ok(new { items = await uow.ExecuteAsync(ctx, (s, c) => prices.ListAsync(s, c), ct) }))
    .RequireTenantMember();

v1.MapGet("/price-lists/{priceListId:guid}/prices", async (Guid priceListId, Guid? variantId, RequestContext ctx, IUnitOfWork uow, IPriceLists prices,
        HttpContext http, CancellationToken ct) =>
    {
        var history = await uow.ExecuteAsync(ctx, (s, c) => prices.HistoryAsync(s, priceListId, variantId, c), ct);
        return history is null ? Problems.NotFound(http) : Results.Ok(new
        {
            items = history.Select(v => new
            {
                v.VariantId, unitPrice = v.UnitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture), v.ValidFrom, v.ValidTo, v.Reason,
                v.CreatedBy, v.CreatedAt,
            }),
        });
    })
    .RequirePermission(Permissions.CatalogPriceManage);

v1.MapIdempotentPost<PriceSetInput>("/price-lists/{priceListId:guid}/prices", "catalog.set-prices", async (body, scope, http, ct) =>
    {
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        Guid priceListId = Guid.Parse((string)http.GetRouteValue("priceListId")!, System.Globalization.CultureInfo.InvariantCulture);
        DateTimeOffset now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var errors = new Dictionary<string, string[]>();
        var items = new List<PriceItem>();
        if (body.Items is not { Length: >= 1 and <= PriceRules.MaxItems }) errors["items"] = [$"1-{PriceRules.MaxItems} items"];
        for (int i = 0; i < (body.Items?.Length ?? 0) && body.Items!.Length <= PriceRules.MaxItems; i++)
        {
            PriceItemInput? it = body.Items[i];
            if (it?.VariantId is null) errors[$"items[{i}].variantId"] = ["required"];
            if (!TryMoney(it?.UnitPrice, positive: true, out long price)) errors[$"items[{i}].unitPrice"] = ["positive rial amount as a whole-number string"];
            else if (it?.VariantId is { } v) items.Add(new PriceItem(v, price));
        }

        if (items.Select(i => i.VariantId).Distinct().Count() != items.Count) errors["items"] = ["one price per variant"];
        // a price takes effect now or later, never in the past: what was charged stays as it was (AB-06). An instant up to
        // 15 minutes old (an approval round trip: 10 pending + 5 valid) is moved to now; the approval binds the value sent.
        if (body.ValidFrom is not { } from || from < now.AddMinutes(-15) || from > now.AddDays(366)) errors["validFrom"] = ["from now up to a year ahead"];
        if (body.Reason is { Length: > 200 }) errors["reason"] = ["up to 200 characters"];
        if (errors.Count > 0) return CommandResult.Fail(Problems.Validation(http, errors));

        // the default list serves every branch, so changing it needs the permission store-wide
        if (!AccessPolicy.Allows(ctx.ActiveMembership!.Assignments, Permissions.CatalogPriceManage, ScopeRef.Tenant))
        {
            return CommandResult.Fail(Forbidden(http));
        }

        DateTimeOffset asked = DateTimeOffset.FromUnixTimeMilliseconds(body.ValidFrom!.Value.ToUnixTimeMilliseconds());
        DateTimeOffset validFrom = asked > now ? asked : DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        IPriceLists prices = http.RequestServices.GetRequiredService<IPriceLists>();
        var preview = await prices.PreviewAsync(scope, priceListId, items, validFrom, ct);
        if (preview.Outcome == PriceOutcome.NotFound) return CommandResult.Fail(Problems.NotFound(http));
        if (preview.Outcome == PriceOutcome.UnknownVariant)
        {
            int at = items.FindIndex(i => i.VariantId == preview.Variant);
            return CommandResult.Fail(Problems.Validation(http, new Dictionary<string, string[]> { [$"items[{at}].variantId"] = ["not an active variant of this store"] }));
        }

        Guid? approvalId = null;
        if (PriceRules.NeedsApproval(preview.Changes!, priceBulkLimit, priceChangePercent) is { } need)
        {
            // D-61: the approval binds this exact change (list, start, every new price); it is consumed with the change
            var ask = new ApprovalAsk(Permissions.CatalogPriceManage, "PriceList", priceListId.ToString(), ScopeRef.Tenant, need.Condition,
                PriceRules.Binding(priceListId, items, asked), need.Limit, body.Reason);
            IApprovals approvals = http.RequestServices.GetRequiredService<IApprovals>();
            bool approved = Guid.TryParse(http.Request.Headers["X-Approval-Id"], out Guid given)
                && await approvals.ConsumeAsync(scope, given, ask, ctx.User!.Value, ct);
            if (!approved)
            {
                // a retry before the decision points at the same open request instead of opening another one
                if (given != Guid.Empty && await approvals.GetAsync(scope, given, ct) is { Status: "Pending" } open
                    && open.RequestedBy == ctx.User!.Value.Value && open.ResourceId == ask.ResourceId && open.Value == ask.Value)
                {
                    return CommandResult.Fail(Problems.ApprovalRequired(http, given, need.Condition, ask.Value, need.Limit));
                }

                // this command rolls back, so the request is stored on its own; the client retries with X-Approval-Id
                Guid requested = await http.RequestServices.GetRequiredService<IUnitOfWork>()
                    .ExecuteAsync(ctx, (s, c) => approvals.RequestAsync(s, ask, ctx.User!.Value, c), ct);
                return CommandResult.Fail(Problems.ApprovalRequired(http, requested, need.Condition, ask.Value, need.Limit));
            }

            approvalId = given;
        }

        if (await prices.SetAsync(scope, priceListId, items, validFrom, body.Reason, ctx.User!.Value.Value, ct) == PriceOutcome.Taken)
        {
            return CommandResult.Fail(Problems.Create(http, StatusCodes.Status409Conflict, "version-conflict",
                "A price already started at this instant; choose another validFrom"));
        }

        await Audit(http, scope, ActorOf(ctx, http), new AuditEntry("catalog.prices.set", "PriceList", priceListId.ToString(), body.Reason, new
        {
            validFrom, count = items.Count, approvalId,
            changes = preview.Changes!.Take(50).Select(c => new { c.VariantId, from = c.Current, to = c.New }),
        }), ct);
        return CommandResult.NoContent();
    })
    .RequirePermission(Permissions.CatalogPriceManage);

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

// updateUser (US-024, IAM-009): enable or disable a member of the active tenant. The caller must see the user (a role of
// theirs inside the caller's reach, as listUsers) and must hold power over every role the user has (nobody disables
// someone they could not have appointed). Disabling only reduces access, so it stays possible on a read-only tenant;
// re-enabling does not. The next request of the user loses the tenant (membership cache dropped here; ≤ 30 s elsewhere).
v1.MapPatch("/users/{userId:guid}", async (Guid userId, UserUpdateInput body, RequestContext ctx, IUnitOfWork uow, IUserAdministration users,
        IEnumerable<IScopeLookup> lookups, ITenantReadOnlyQuery readOnly, IMemoryCache cache, HttpContext http, CancellationToken ct) =>
    {
        var errors = new Dictionary<string, string[]>();
        if (body.ExpectedVersion is not >= 1)
        {
            errors["expectedVersion"] = ["required"];
        }

        if (body.Status is not ("Active" or "Disabled"))
        {
            errors["status"] = ["one of: Active, Disabled"];
        }

        if (body.DisplayName is not null)
        {
            errors["displayName"] = ["not-yet-supported"];   // the name belongs to the person, across all their stores
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(http, errors);
        }

        bool enable = body.Status == "Active";
        if (!enable && userId == ctx.User!.Value.Value)
        {
            return Problems.Create(http, StatusCodes.Status409Conflict, "cannot-disable-self", "You cannot disable your own access");
        }

        if (enable && await readOnly.ReasonAsync(ctx.ActiveTenant!.Value, ct) is { } reason)
        {
            return Problems.TenantReadOnly(http, reason);
        }

        IResult result = await uow.ExecuteAsync(ctx, async (s, c) =>
        {
            UserView? target = await users.GetAsync(s, new UserId(userId), c);
            if (target is null)
            {
                return Problems.NotFound(http);
            }

            IReadOnlyList<RoleGrant> mine = ctx.ActiveMembership!.Assignments;
            var scopes = new List<(string Role, ScopeRef? Scope)>();
            foreach (AssignmentView a in target.RoleAssignments)
            {
                scopes.Add((a.RoleName, await ResolveScope(s, lookups, Enum.Parse<ScopeType>(a.ScopeType), a.ScopeId, c)));
            }

            bool visible = scopes.Count == 0
                ? AccessPolicy.Allows(mine, Permissions.UsersManage, ScopeRef.Tenant)
                : scopes.Any(x => x.Scope is not null && AccessPolicy.Allows(mine, Permissions.UsersManage, x.Scope));
            if (!visible)
            {
                return Problems.NotFound(http);   // BR-003: someone outside the caller's reach looks like no one
            }

            if (scopes.Any(x => x.Scope is null || !AccessPolicy.CanAssign(mine, x.Role, x.Scope)))
            {
                return Forbidden(http);
            }

            if ((target.Status == "Active") == enable)
            {
                return Results.Ok(target);   // already so: nothing to change or audit
            }

            switch (await users.SetStatusAsync(s, new UserId(userId), body.ExpectedVersion!.Value, enable, c))
            {
                case UserStatusOutcome.NotFound:
                    return Problems.NotFound(http);
                case UserStatusOutcome.VersionConflict:
                    return Problems.Create(http, StatusCodes.Status409Conflict, "version-conflict", "The user changed; reload and retry");
                case UserStatusOutcome.LastOwner:
                    return Problems.Create(http, StatusCodes.Status409Conflict, "last-owner", "A store must keep at least one active owner");
            }

            await Audit(http, s, ActorOf(ctx, http), new AuditEntry(enable ? "iam.user.enabled" : "iam.user.disabled", "User", userId.ToString(),
                Details: new { roles = target.RoleAssignments.Select(a => a.RoleName).Distinct().Order() }), c);
            return Results.Ok(await users.GetAsync(s, new UserId(userId), c));
        }, ct);
        RequestContextMiddleware.ForgetMemberships(cache, new UserId(userId));
        return result;
    })
    .RequirePermission(Permissions.UsersManage)
    .AllowWhenReadOnly();

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
internal sealed record SettlementImportInput(string? FileName, string? Content);
internal sealed record UserUpdateInput(int? ExpectedVersion, string? Status, string? DisplayName);
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
internal sealed record DiscountTargetInput(string? Kind, Guid? Id);
internal sealed record DiscountRuleInput(string? Name, DiscountTargetInput? Target, int? Percent, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo, Guid[]? BranchIds);
internal sealed record DiscountCapsInput(Dictionary<string, int>? Caps);
internal sealed record NegativeStockPolicyInput(string? ScopeType, Guid? ScopeId, string? Policy);
internal sealed record AdjustmentLineInput(Guid? VariantId, string? QtyDelta);
internal sealed record AdjustmentInput(Guid? WarehouseId, string? Reason, AdjustmentLineInput?[]? Lines, string? Note);
internal sealed record ReorderPointInput(Guid? WarehouseId, Guid? VariantId, string? ReorderPoint);
internal sealed record TransferLineInput(Guid? VariantId, string? Qty);
internal sealed record TransferInput(Guid? FromWarehouseId, Guid? ToWarehouseId, TransferLineInput?[]? Lines, string? Note);
internal sealed record CountCreateInput(Guid? WarehouseId, string? Scope, Guid[]? CategoryIds, bool? Blind);
internal sealed record CountEntryInput(Guid? VariantId, string? Qty);
internal sealed record CountEntriesInput(CountEntryInput?[]? Entries, string? ClientBatchId);
internal sealed record VersionInput(int? ExpectedVersion);
internal sealed record IntegrationKeyInput(string? Name, string[]? Scopes, DateTimeOffset? ExpiresAt);

/// <summary>INT-001: the scope a partner endpoint needs from the key.</summary>
internal sealed record PartnerScope(string Scope);
internal sealed record ProductMergeInput(Guid? TargetProductId, int? ExpectedVersion, string? Reason);
internal sealed record CatalogSuggestInput(bool? Consent, string? Category);
internal sealed record PrintInput(string? Format);
internal sealed record SupportAccessRequestInput(string? Reason, int? DurationMinutes);
internal sealed record CatalogReviewEdits(string? Name, string? Brand, string? Category);
internal sealed record CatalogReviewInput(string? Decision, CatalogReviewEdits? Edits, string? Reason);
internal sealed record ReceiptOverridesInput(string? LogoObjectKey, string? FooterText, string? Signature);
internal sealed record ReceiptTemplateUpdateInput(int? ExpectedVersion, ReceiptOverridesInput? Overrides);
internal sealed record IndustryProfileUpdateInput(int? ExpectedVersion, string? LegalText, string[]? LockedFields, string? Reason);
internal sealed record HoldInput(int? ExpectedVersion, string? Note);
internal sealed record ResumeInput(int? ExpectedVersion, Guid? RegisterId);
internal sealed record CancelInput(int? ExpectedVersion, string? Reason);
internal sealed record TenderVoidInput(int? ExpectedVersion, string? Reason);
internal sealed record FinalizeInput(int? ExpectedVersion, string? ExpectedTotal);
internal sealed record TenderInput(string? Method, string? Amount, string? Tendered, string? ReferenceNo, string? TerminalNo, string? CardLast4,
    Guid? ApprovalId);
internal sealed record SaleCreateInput(Guid? RegisterId, string? ClientDraftId, SaleLineInput[]? Lines);
internal sealed record SaleLineInput(Guid? VariantId, string? Barcode, string? Unit, string? Qty);
internal sealed record SaleLinePatchInput(int? ExpectedVersion, string? Qty, string? Unit);
internal sealed record SupplierInput(string? Name, string? NationalId, string? EconomicCode, string? Phone);
internal sealed record SupplierPaymentInput(string? Amount, string? Method, string? Reference, string? Note, Guid? RegisterShiftId, string[]? ConfirmedWarnings);
internal sealed record PurchaseReturnLineInput(Guid? VariantId, string? Qty);
internal sealed record PurchaseReturnInput(Guid? SupplierId, Guid? WarehouseId, PurchaseReturnLineInput?[]? Lines, string? AgreedAmount, string? Note,
    string[]? ConfirmedWarnings);
internal sealed record ReceiptLineInput(Guid? VariantId, string? Qty, string? Unit, string? UnitCost, string? Discount, string? InputVat);
internal sealed record ReceiptInput(Guid? SupplierId, Guid? WarehouseId, ReceiptLineInput?[]? Lines, string? SupplierInvoiceNo, string? EffectiveDate,
    string[]? ConfirmedWarnings);
internal sealed record PriceItemInput(Guid? VariantId, string? UnitPrice);
internal sealed record PriceSetInput(PriceItemInput?[]? Items, DateTimeOffset? ValidFrom, string? Reason);
internal sealed record WeightedProfileInput(Guid? Id, string? Prefix, int? ItemCodeLength, string? ValueType, int? ValueLength, int? Decimals,
    bool? HasInnerCheckDigit);
internal sealed record WeightedProfilesInput(int? ExpectedVersion, WeightedProfileInput[]? Profiles);
internal sealed record BarcodeResolveInput(string? Barcode, Guid? BranchId, string[]? PreferredSources);
internal sealed record BarcodeInput(string? Code, string? PackagingUnit, string? UnitFactor, bool? IsInternal);
internal sealed record VariantInput(string? Sku, Dictionary<string, string?>? AxisValues, string? UnitPrice, BarcodeInput[]? Barcodes);
internal sealed record ProductConfirmation(bool? Confirmed, int? SourceCandidate);
internal sealed record OpeningStock(Guid? WarehouseId, string? Qty, string? UnitCost);
internal sealed record ProductCreate(string? Name, string? BaseUnit, string? Brand, Guid? CategoryId, Guid? CatalogProductId, string? TaxCode,
    int? TaxRate, string[]? Axes, VariantInput[]? Variants, ProductConfirmation? Confirmation, OpeningStock? OpeningStock);
internal sealed record VariantUpdate(int? ExpectedVersion, string? Status);
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

internal static partial class PlatformLog
{
    [LoggerMessage(EventId = 6101, Level = LogLevel.Information, Message = "platform.receipt-template.published {Industry} v{Version} by {Operator}")]
    public static partial void TemplatePublished(ILogger logger, string industry, int version, string @operator);

    [LoggerMessage(EventId = 6102, Level = LogLevel.Information, Message = "platform.catalog.suggestion-decided {Suggestion} {Outcome} product {Product} by {Operator}")]
    public static partial void SuggestionDecided(ILogger logger, Guid suggestion, string outcome, Guid? product, string @operator);
}
