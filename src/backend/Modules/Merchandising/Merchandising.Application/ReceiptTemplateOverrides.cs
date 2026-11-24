using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Merchandising.Application;

/// <summary>
/// BR-086 / DI-085 / D-32: the parts of the receipt a store may change. There is no field for the legal text or the
/// locked fields: they always come from the industry's template.
/// </summary>
public sealed record ReceiptOverride(string? FooterText, string? Signature, string? LogoObjectKey, int Version);

public interface IReceiptTemplateOverrides
{
    /// <summary>The store's override; version 0 when it has never changed anything.</summary>
    Task<ReceiptOverride> GetAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);

    /// <summary>Replaces the override under its version; null when <paramref name="expectedVersion"/> is stale.</summary>
    Task<ReceiptOverride?> SetAsync(IUnitOfWorkScope scope, int expectedVersion, string? footerText, string? signature, Guid updatedBy,
        CancellationToken cancellationToken);
}
