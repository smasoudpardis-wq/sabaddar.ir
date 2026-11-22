using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace RetailCloud.Api.Tests;

/// <summary>
/// Runs the real API in-process against PostgreSQL. Only the token signing key differs from production: the JWT handler is
/// given a test RSA key and static OIDC metadata instead of fetching them from Keycloak (no test branch in product code).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://id.test/realms/merchant";
    public const string Audience = "merchant-api";
    private static readonly RSA Rsa = RSA.Create(2048);
    public static readonly RsaSecurityKey SigningKey = new(Rsa) { KeyId = "test-key" };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Main", Environment.GetEnvironmentVariable("RC_TEST_PG") ?? "Host=unused");
        builder.UseSetting("Auth:Merchant:Authority", Issuer);
        builder.UseSetting("Auth:Merchant:Audience", Audience);
        builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.Authority = null;
                var config = new OpenIdConnectConfiguration { Issuer = Issuer };
                config.SigningKeys.Add(SigningKey);
                o.Configuration = config;
                o.TokenValidationParameters.IssuerSigningKey = SigningKey;
            }));
    }

    public static string Token(string subject, string issuer = Issuer, string audience = Audience,
        DateTime? expires = null, SecurityKey? key = null, string? acr = null, DateTimeOffset? authTime = null,
        IReadOnlyDictionary<string, object>? extraClaims = null)
    {
        DateTime exp = expires ?? DateTime.UtcNow.AddMinutes(10);
        var claims = new Dictionary<string, object> { ["sub"] = subject, ["azp"] = "rc-bff-merchant" };
        if (acr is not null)
        {
            claims["acr"] = acr;
        }

        if (authTime is not null)
        {
            claims["auth_time"] = authTime.Value.ToUnixTimeSeconds();
        }

        foreach (KeyValuePair<string, object> c in extraClaims ?? new Dictionary<string, object>())
        {
            claims[c.Key] = c.Value;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            NotBefore = exp.AddMinutes(-20),
            IssuedAt = exp.AddMinutes(-20),
            Expires = exp,
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }
}
