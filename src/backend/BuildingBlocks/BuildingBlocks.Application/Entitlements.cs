namespace RetailCloud.BuildingBlocks.Application;

/// <summary>
/// TEN-003: the tenant's plan does not allow another <see cref="Resource"/> (branches, registers or users).
/// Raised by the database cap (global code RC030) or by an early check; the API answers 403 entitlement-exceeded.
/// </summary>
public sealed class EntitlementExceededException(string resource, int? limit)
    : Exception($"The plan's limit for {resource} is reached ({limit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}).")
{
    public string Resource { get; } = resource;

    public int? Limit { get; } = limit;
}
