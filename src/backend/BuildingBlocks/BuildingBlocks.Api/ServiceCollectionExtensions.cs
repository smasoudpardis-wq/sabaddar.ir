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

public static class ServiceCollectionExtensions
{
    public const string MerchantPolicy = "merchant";

    /// <summary>JWT bearer for the merchant audience (ADR-003, AUTH-05, BR-004) plus request context.</summary>
    public static IServiceCollection AddRetailCloudApi(this IServiceCollection services, IConfiguration configuration)
    {
        var auth = configuration.GetSection("Auth:Merchant").Get<MerchantAuthOptions>() ?? new MerchantAuthOptions();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.Authority = string.IsNullOrEmpty(auth.Authority) ? null : auth.Authority;
            o.RequireHttpsMetadata = auth.RequireHttpsMetadata;
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = auth.Authority,
                ValidateAudience = true,
                ValidAudience = auth.Audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256],
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = "preferred_username",
            };
        });
        services.AddAuthorizationBuilder().AddPolicy(MerchantPolicy, p => p.RequireAuthenticatedUser());
        services.AddMemoryCache();
        services.AddScoped<RequestContext>();
        // Brute-force bounds for guessable secrets (D07 x-rate-limit): pairing codes, invitation links. Per identity.
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "600";
                await Problems.WriteAsync(ctx.HttpContext, Problems.Create(ctx.HttpContext, StatusCodes.Status429TooManyRequests, "rate-limited", "Too many attempts"));
            };
            foreach (string policy in new[] { RateLimits.Pairing, RateLimits.Invitation })
            {
                o.AddPolicy(policy, http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    http.User.FindFirst("iss")?.Value + "|" + http.User.FindFirst("sub")?.Value,
                    _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10) }));
            }
        });
        services.AddSingleton(TimeProvider.System);
        services.AddProblemDetails(o => o.CustomizeProblemDetails = ProblemDefaults.Customize);
        services.AddExceptionHandler<EntitlementExceptionHandler>();
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
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
}

