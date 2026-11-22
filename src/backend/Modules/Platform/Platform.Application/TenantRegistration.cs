using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.Platform.Application;

/// <summary>D07 schema <c>TenantRegistration</c> (TEN-001).</summary>
public sealed record RegisterTenant(string LegalName, string IndustryCode, string? TimeZone, string? PricingMode, string FirstBranchName);

/// <summary>Integration events of the Platform module (D03 §7.2).</summary>
public static class PlatformEvents
{
    /// <summary>
    /// v1 payload: tenantId, ownerUserId, legalName, industryCode, timeZone, pricingMode, firstBranchId,
    /// firstRegisterId, registeredAt. Consumers: IdentityAccess (owner membership), Inventory (first warehouse),
    /// Accounting (chart of accounts, role mapping, fiscal periods) — TEN-001.1.
    /// </summary>
    public const string TenantRegistered = "platform.tenant-registered";
}

public static class TenantRegistrationRules
{
    public static readonly IReadOnlyList<string> Industries = ["supermarket", "apparel", "general"];
    public const string DefaultTimeZone = "Asia/Tehran";

    /// <summary>Field errors for problem+json <c>validation</c>; empty when the command is valid.</summary>
    public static Dictionary<string, string[]> Validate(RegisterTenant cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        var errors = new Dictionary<string, string[]>();
        if (cmd.LegalName is null || cmd.LegalName.Trim().Length is < 2 or > 200)
        {
            errors["legalName"] = ["length-2-200"];
        }

        if (!Industries.Contains(cmd.IndustryCode))
        {
            errors["industryCode"] = ["unknown"];
        }

        if (cmd.FirstBranchName is null || cmd.FirstBranchName.Trim().Length is < 1 or > 100)
        {
            errors["firstBranchName"] = ["length-1-100"];
        }

        if (cmd.PricingMode is not (null or "TaxExclusive" or "TaxInclusive"))
        {
            errors["pricingMode"] = ["unknown"];
        }

        if (cmd.TimeZone is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(cmd.TimeZone, out _))
        {
            errors["timeZone"] = ["unknown"];
        }

        return errors;
    }
}

/// <summary>Creates tenant, first branch and first register and emits <see cref="PlatformEvents.TenantRegistered"/>.</summary>
public interface ITenantRegistration
{
    Task<TenantProfile> RegisterAsync(IUnitOfWorkScope scope, RegisterTenant command, UserId owner, CancellationToken cancellationToken);
}
