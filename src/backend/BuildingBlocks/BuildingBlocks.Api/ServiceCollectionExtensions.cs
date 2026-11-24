using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace RetailCloud.BuildingBlocks.Api;

public sealed class MerchantAuthOptions
{
    /// <summary>Keycloak realm URL, e.g. https://id.example/realms/merchant. Also the expected issuer.</summary>
    public string Authority { get; set; } = string.Empty;

    public string Audience { get; set; } = "merchant-api";

    public bool RequireHttpsMetadata { get; set; } = true;
}

/// <summary>The platform operators' realm (D06 §6.2, TM-071): its own issuer and audience, never accepted as a merchant.</summary>
public sealed class PlatformAuthOptions
{
    public string Authority { get; set; } = string.Empty;

    public string Audience { get; set; } = "platform-api";

    public bool RequireHttpsMetadata { get; set; } = true;
}

public static class ServiceCollectionExtensions
{
    public const string MerchantPolicy = "merchant";

    /// <summary>JWT bearer scheme of the platform realm.</summary>
    public const string PlatformScheme = "platform";

    /// <summary>PlatformSuperAdmin with a second factor (D06 §6.2, AUTH-02): global templates and policies.</summary>
    public const string PlatformAdminPolicy = "platform-admin";

    /// <summary>Roles of the platform realm, as Keycloak puts them in <c>realm_access.roles</c>.</summary>
    public static bool HasRealmRole(System.Security.Claims.ClaimsPrincipal user, string role)
    {
        ArgumentNullException.ThrowIfNull(user);
        string? access = user.FindFirst("realm_access")?.Value;
        if (access is null)
        {
            return false;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(access);
            return doc.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == System.Text.Json.JsonValueKind.Array
                && roles.EnumerateArray().Any(r => r.ValueKind == System.Text.Json.JsonValueKind.String && r.GetString() == role);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>JWT bearer for the merchant audience (ADR-003, AUTH-05, BR-004) plus request context.</summary>
    public static IServiceCollection AddRetailCloudApi(this IServiceCollection services, IConfiguration configuration)
    {
        var auth = configuration.GetSection("Auth:Merchant").Get<MerchantAuthOptions>() ?? new MerchantAuthOptions();
        var platform = configuration.GetSection("Auth:Platform").Get<PlatformAuthOptions>() ?? new PlatformAuthOptions();
        static TokenValidationParameters Validation(string issuer, string audience) => new()
        {
            ValidateIssuer = true,
            ValidIssuer = string.IsNullOrEmpty(issuer) ? "unconfigured" : issuer,   // an unset realm accepts nobody
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "preferred_username",
        };
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.Authority = string.IsNullOrEmpty(auth.Authority) ? null : auth.Authority;
                o.RequireHttpsMetadata = auth.RequireHttpsMetadata;
                o.MapInboundClaims = false;
                o.TokenValidationParameters = Validation(auth.Authority, auth.Audience);
            })
            .AddJwtBearer(PlatformScheme, o =>
            {
                o.Authority = string.IsNullOrEmpty(platform.Authority) ? null : platform.Authority;
                o.RequireHttpsMetadata = platform.RequireHttpsMetadata;
                o.MapInboundClaims = false;
                o.TokenValidationParameters = Validation(platform.Authority, platform.Audience);
            });
        services.AddAuthorizationBuilder()
            .AddPolicy(MerchantPolicy, p => p.RequireAuthenticatedUser())
            .AddPolicy(PlatformAdminPolicy, p => p.AddAuthenticationSchemes(PlatformScheme).RequireAuthenticatedUser()
                .RequireClaim("acr", MfaPolicy.MfaAcr).RequireAssertion(c => HasRealmRole(c.User, "PlatformSuperAdmin")));
        services.AddMemoryCache();
        services.AddScoped<RequestContext>();
        // Brute-force bounds for guessable secrets (D07 x-rate-limit): pairing codes, invitation links. Per identity.
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter =
                    (ctx.Lease.TryGetMetadata(System.Threading.RateLimiting.MetadataName.RetryAfter, out TimeSpan wait) ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)) : 600)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Problems.WriteAsync(ctx.HttpContext, Problems.Create(ctx.HttpContext, StatusCodes.Status429TooManyRequests, "rate-limited", "Too many attempts"));
            };
            foreach (string policy in new[] { RateLimits.Pairing, RateLimits.Invitation })
            {
                o.AddPolicy(policy, http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    http.User.FindFirst("iss")?.Value + "|" + http.User.FindFirst("sub")?.Value,
                    _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10) }));
            }

            // D07 README: barcode resolve 120 a minute per user (a scanner is fast, a scraper faster)
            o.AddPolicy(RateLimits.CatalogResolve, http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirst("iss")?.Value + "|" + http.User.FindFirst("sub")?.Value,
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
        });
        services.AddSingleton(TimeProvider.System);
        services.AddProblemDetails(o => o.CustomizeProblemDetails = ProblemDefaults.Customize);
        services.AddExceptionHandler<EntitlementExceptionHandler>();
        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            // TM-013 / SEC-T-013: a body field the command does not define is refused, never silently ignored
            o.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
        });
        // binding errors reach BadRequestExceptionHandler (problem+json 400) instead of an empty 400 outside Development
        services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        services.AddExceptionHandler<BadRequestExceptionHandler>();
        return services;
    }

    public static WebApplication UseRetailCloudApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseMiddleware<RequestContextMiddleware>();
        app.UseAuthorization();
        app.UseRateLimiter();
        return app;
    }
}

/// <summary>Named rate-limit policies (D07 x-rate-limit).</summary>
public static class RateLimits
{
    public const string Pairing = "pairing";
    public const string Invitation = "invitation";
    public const string CatalogResolve = "catalog-resolve";
}

