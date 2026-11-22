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
        services.AddProblemDetails();
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
        return app;
    }
}
