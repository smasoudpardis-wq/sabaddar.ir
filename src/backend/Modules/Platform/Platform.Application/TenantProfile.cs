using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.Platform.Application;

/// <summary>D07 schema <c>Tenant</c>.</summary>
public sealed record TenantProfile(
    Guid Id, string LegalName, string IndustryCode, string TimeZone, string BaseCurrency, string PricingMode, string Status, int Version,
    string Plan, DateTimeOffset? TrialEndsAt, Entitlements Entitlements);

/// <summary>TEN-003 caps of the tenant's plan; null = unlimited.</summary>
public sealed record Entitlements(int? MaxBranches, int? MaxRegisters, int? MaxUsers);

public interface ITenantProfileQuery
{
    /// <summary>Reads the tenant through RLS: returns null for any tenant other than the one in context.</summary>
    Task<TenantProfile?> GetAsync(TenantId tenant, CancellationToken cancellationToken);
}
