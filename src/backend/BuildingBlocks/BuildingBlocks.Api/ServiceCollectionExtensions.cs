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

/// <summary>
/// The customer portal's token (ADR-003 §6, US-141): HS256 over <see cref="Key"/>, issuer retailcloud-customer, audience customer-api, minutes long.
/// It is issued by the API after a verified phone (the BFF portal does not exist yet); with no key every customer token is refused.
/// </summary>
public sealed class CustomerAuthOptions
{
    public string Key { get; set; } = string.Empty;

    public string Issuer { get; set; } = "retailcloud-customer";

    public string Audience { get; set; } = "customer-api";
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

    /// <summary>JWT bearer scheme of the customer portal (HS256, US-141).</summary>
    public const string CustomerScheme = "customer";

    /// <summary>A customer signed in by phone: the portal API only (<c>/c/*</c>), never a store's data.</summary>
    public const string CustomerPolicy = "customer";

    /// <summary>PlatformSuperAdmin with a second factor (D06 §6.2, AUTH-02): global templates and policies.</summary>
    public const string PlatformAdminPolicy = "platform-admin";

    /// <summary>PlatformSupport with a second factor (D06 §6.2, IAM-008): asks for JIT access and uses it, nothing else.</summary>
    public const string PlatformSupportPolicy = "platform-support";

    /// <summary>CatalogModerator with a second factor (D06 §6.2): the global catalog only, never a store's data (US-047).</summary>
    public const string PlatformCatalogPolicy = "platform-catalog";

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
        var customer = configuration.GetSection("Auth:Customer").Get<CustomerAuthOptions>() ?? new CustomerAuthOptions();
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
            .AddJwtBearer(CustomerScheme, o =>
            {
                o.MapInboundClaims = false;
                byte[] key = customer.Key.Length >= 44 ? Convert.FromBase64String(customer.Key) : new byte[32];   // no key: a zero key no one signs with, and the issuer below never matches
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = customer.Key.Length >= 44 ? customer.Issuer : "unconfigured",
                    ValidateAudience = true, ValidAudience = customer.Audience,
                    ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256], ClockSkew = TimeSpan.FromSeconds(30),
                };
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
            .AddPolicy(CustomerPolicy, p => p.AddAuthenticationSchemes(CustomerScheme).RequireAuthenticatedUser())
            .AddPolicy(PlatformAdminPolicy, p => p.AddAuthenticationSchemes(PlatformScheme).RequireAuthenticatedUser()
                .RequireClaim("acr", MfaPolicy.MfaAcr).RequireAssertion(c => HasRealmRole(c.User, "PlatformSuperAdmin")))
            .AddPolicy(PlatformSupportPolicy, p => p.AddAuthenticationSchemes(PlatformScheme).RequireAuthenticatedUser()
                .RequireClaim("acr", MfaPolicy.MfaAcr).RequireAssertion(c => HasRealmRole(c.User, "PlatformSupport")))
            .AddPolicy(PlatformCatalogPolicy, p => p.AddAuthenticationSchemes(PlatformScheme).RequireAuthenticatedUser()
                .RequireClaim("acr", MfaPolicy.MfaAcr).RequireAssertion(c => HasRealmRole(c.User, "CatalogModerator")));
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

            // CUS-009 / AB-12: an anonymous receipt link, 30 a minute per client address (no one reads 30 receipts a minute)
            o.AddPolicy(RateLimits.ReceiptLink, http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));

            // INT-001: a partner key, 120 calls a minute per key (its public prefix), or per address without one
            o.AddPolicy(RateLimits.Partner, http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                http.Request.Headers["X-Api-Key"].ToString() is { Length: >= 15 } k && k.StartsWith("rck_", StringComparison.Ordinal)
                    ? k[..15] : "ip|" + http.Connection.RemoteIpAddress,
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));

            // CUS-001 / TM-043: a burst guard per client address in front of the per-phone and per-address limits kept in the database
            o.AddPolicy(RateLimits.Otp, http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                "ip|" + ClientAddress.Of(http),
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));

            // CUS-002: looking customers up, 30 a minute per user and device; a customer's QR, 6 a minute per customer
            o.AddPolicy(RateLimits.CustomerLookup, http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirst("iss")?.Value + "|" + http.User.FindFirst("sub")?.Value + "|" + http.Request.Headers[RequestContextMiddleware.DeviceHeader],
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
            o.AddPolicy(RateLimits.QrIssue, http => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                "customer|" + http.User.FindFirst("sub")?.Value,
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 6, Window = TimeSpan.FromMinutes(1) }));

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
    public const string ReceiptLink = "receipt-link";
    public const string Partner = "partner";
    public const string Otp = "otp";
    public const string CustomerLookup = "customer-lookup";
    public const string QrIssue = "qr-issue";
}


/// <summary>The caller's address: the first X-Forwarded-For entry when the peer is a trusted proxy (Network:TrustedProxies, "*" = any), else the peer itself.</summary>
public static class ClientAddress
{
    public static string Of(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        string? peer = http.Connection.RemoteIpAddress?.ToString();
        string[] trusted = (http.RequestServices.GetService<IConfiguration>()?["Network:TrustedProxies"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool fromProxy = trusted.Contains("*") || (peer is not null && trusted.Contains(peer));
        return fromProxy && http.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim() is { Length: > 0 } fwd ? fwd : peer ?? "unknown";
    }
}
