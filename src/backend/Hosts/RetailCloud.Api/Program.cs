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
builder.Services.AddSingleton<IScopeLookup, BranchScopeLookup>();
builder.Services.AddSingleton<IScopeLookup, RegisterScopeLookup>();
builder.Services.AddSingleton<IScopeLookup, RetailCloud.Inventory.Infrastructure.WarehouseScopeLookup>();
builder.Services.AddRetailCloudApi(builder.Configuration);

var app = builder.Build();
app.UseRetailCloudApi();

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
    mfaFreshUntil = ctx.MfaSatisfied ? ctx.AuthTime + MfaPolicy.SensitiveFreshness : null,
})).AllowAnyAuthenticated();

// selectActiveTenant (D07): the BFF calls this before storing the choice in its session; 404 unless the user is a member,
// 403 mfa-required when the user's role there needs a second factor the token does not carry (IAM-003).
v1.MapPost("/me/active-tenant", (SelectTenant body, RequestContext ctx, HttpContext http) =>
    ctx.Memberships.FirstOrDefault(m => m.TenantId.Value == body.TenantId) switch
    {
        null => Problems.NotFound(http),
        { } m when MfaPolicy.Requires(m) && !ctx.MfaSatisfied => MfaPolicy.MfaRequired(http),
        _ => Results.NoContent(),
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

        AcceptedInvitation? accepted = await uow.ExecuteAsync(new FixedTenantContext(tenant, user.Value.Id), (s, c) => users.AcceptAsync(s, token, user.Value.Id, c), ct);
        if (accepted is null)
        {
            return Problems.NotFound(http);
        }

        RequestContextMiddleware.ForgetMemberships(cache, user.Value.Id);
        return Results.Ok(new { tenantId = accepted.TenantId, tenantName = accepted.TenantName, roles = accepted.RoleAssignments.Select(a => a.Role).Distinct() });
    })
    .WithMetadata(new AllowUnprovisionedUserAttribute())
    .AllowAnyAuthenticated();

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

internal sealed record RoleAssignmentInput(Guid RoleId, ScopeType ScopeType, Guid? ScopeId);

internal sealed record InviteUser(string? DisplayName, string? Email, string? Mobile, List<RoleAssignmentInput>? RoleAssignments);

/// <summary>Entry point type for WebApplicationFactory in tests.</summary>
public partial class Program;
