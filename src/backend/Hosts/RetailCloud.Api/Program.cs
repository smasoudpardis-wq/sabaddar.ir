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

await app.RunAsync();

internal sealed record SelectTenant(Guid TenantId);

/// <summary>Entry point type for WebApplicationFactory in tests.</summary>
public partial class Program;
