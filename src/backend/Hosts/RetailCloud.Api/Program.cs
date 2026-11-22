using Npgsql;
using RetailCloud.BuildingBlocks.Api;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
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
builder.Services.AddRetailCloudApi(builder.Configuration);

var app = builder.Build();
app.UseRetailCloudApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous(); // R7: explicit anonymous

RouteGroupBuilder v1 = app.MapGroup("/api/v1").RequireAuthorization(ServiceCollectionExtensions.MerchantPolicy);

// getMe (D07; x-permission tenant.member). Permissions are filled by US-025. MFA state from the token (US-022).
v1.MapGet("/me", (RequestContext ctx) => Results.Ok(new
{
    userId = ctx.User!.Value.Value,
    displayName = ctx.DisplayName,
    activeTenantId = ctx.ActiveTenant?.Value,
    memberships = ctx.Memberships.Select(m => new
    {
        tenantId = m.TenantId.Value, tenantName = m.TenantName, roles = m.Roles, mfaRequired = MfaPolicy.Requires(m),
    }),
    permissions = Array.Empty<object>(),
    mfaSatisfied = ctx.MfaSatisfied,
    mfaFreshUntil = ctx.MfaSatisfied ? ctx.AuthTime + MfaPolicy.SensitiveFreshness : null,
}));

// selectActiveTenant (D07): the BFF calls this before storing the choice in its session; 404 unless the user is a member,
// 403 mfa-required when the user's role there needs a second factor the token does not carry (IAM-003).
v1.MapPost("/me/active-tenant", (SelectTenant body, RequestContext ctx, HttpContext http) =>
    ctx.Memberships.FirstOrDefault(m => m.TenantId.Value == body.TenantId) switch
    {
        null => Problems.NotFound(http),
        { } m when MfaPolicy.Requires(m) && !ctx.MfaSatisfied => MfaPolicy.MfaRequired(http),
        _ => Results.NoContent(),
    });

// getMyTenant (D07): tenant-scoped, read through RLS.
v1.MapGet("/tenants/me", async (RequestContext ctx, ITenantProfileQuery query, HttpContext http, CancellationToken ct) =>
        await query.GetAsync(ctx.ActiveTenant!.Value, ct) is { } t ? Results.Ok(t) : Problems.NotFound(http))
    .AddEndpointFilter<RequireActiveTenantFilter>();

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
    .WithMetadata(new AllowUnprovisionedUserAttribute());

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
    .AddEndpointFilter<RequireActiveTenantFilter>();

await app.RunAsync();

internal sealed record SelectTenant(Guid TenantId);

/// <summary>Entry point type for WebApplicationFactory in tests.</summary>
public partial class Program;
