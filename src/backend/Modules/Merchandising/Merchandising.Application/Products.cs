using System.Globalization;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.Merchandising.Application;

public sealed record BarcodeView(Guid Id, string Code, string CodeNormalized, string PackagingUnit, string UnitFactor, bool IsInternal, bool Active);

/// <summary><paramref name="UnitPrice"/>: rials in force now in the default price list (CAT-009); null = no price yet.</summary>
public sealed record VariantView(Guid Id, string Sku, IReadOnlyDictionary<string, string> AxisValues, string Status, IReadOnlyList<BarcodeView> Barcodes,
    long? UnitPrice = null);

public sealed record ProductView(Guid Id, string Name, string? Brand, string BaseUnit, string? TaxCode, int TaxRate, string Status,
    IReadOnlyList<VariantView> Variants, int Version, IReadOnlyList<string>? Axes = null);

/// <summary>
/// A barcode as stored: <paramref name="Normalized"/> is GTIN-14 for every GS1 code (CAT-003) and the code itself for a
/// free internal code. <paramref name="IsInternal"/> also covers GS1 restricted-circulation numbers (02, 20–29; CAT-003.1):
/// valid only inside this tenant and never offered to the global catalog.
/// </summary>
public sealed record BarcodeSpec(string Code, string Normalized, string PackagingUnit, decimal UnitFactor, bool IsInternal)
{
    /// <summary>GS1 code (validated by check digit) or, when <paramref name="freeInternal"/>, a store's own code; null if invalid.</summary>
    public static BarcodeSpec? From(string code, string packagingUnit, decimal unitFactor, bool freeInternal)
    {
        string c = code.Trim();
        if (freeInternal)
        {
            return InternalCode.IsValid(c) ? new BarcodeSpec(c, c, packagingUnit, unitFactor, true) : null;
        }

        return Gtin.TryParse(c, out Gtin g) ? new BarcodeSpec(c, g.Normalized14, packagingUnit, unitFactor, g.IsRestrictedCirculation) : null;
    }
}

/// <summary><paramref name="PackagingUnit"/> / <paramref name="UnitFactor"/>: what one scan of this barcode is (a carton of 24, BR-044).</summary>
public sealed record BarcodeMatch(Guid ProductId, Guid VariantId, string Name, string Sku, string BaseUnit, bool IsInternal, long? UnitPrice,
    string PackagingUnit = "EA", decimal UnitFactor = 1);

/// <summary>
/// What the POS needs to price a variant (SAL-001): the name with the matrix values, the tax rate, the default list's price
/// per base unit now (null: no price), and whether both the product and the variant are active.
/// </summary>
public sealed record SellableVariant(Guid ProductId, Guid VariantId, string Name, string Sku, string BaseUnit, int TaxRateBp, long? UnitPrice, bool Active);

public sealed record VariantUnits(string BaseUnit, IReadOnlyDictionary<string, decimal> Factors);

public sealed record VariantSpec(string Sku, IReadOnlyDictionary<string, string> AxisValues, IReadOnlyList<BarcodeSpec> Barcodes);

/// <summary><paramref name="Axes"/>: CAT-008 matrix axes; when present every variant names exactly these axes.</summary>
public sealed record ProductSpec(string Name, string? Brand, string BaseUnit, string? TaxCode, int TaxRate, IReadOnlyList<VariantSpec> Variants,
    IReadOnlyList<string>? Axes = null);

public sealed record ProductChange(int ExpectedVersion, string? Name, string? Brand, string? TaxCode, int? TaxRate, string? Status);

/// <summary>Why a catalog write did not happen; the API maps each to D07 codes.</summary>
public enum CatalogOutcome
{
    Done,
    NotFound,
    VersionConflict,
    DuplicateSku,
    DuplicateAxes,
    BarcodeAssigned,
    /// <summary>CAT-008: the variant does not name exactly the product's axes.</summary>
    AxesMismatch,
}

public sealed record CatalogResult<T>(CatalogOutcome Outcome, T? Value, Guid? ConflictProductId = null);

public interface IProductCatalog
{
    Task<CatalogResult<ProductView>> CreateAsync(IUnitOfWorkScope scope, ProductSpec spec, CancellationToken cancellationToken);

    Task<ProductView?> GetAsync(IUnitOfWorkScope scope, Guid productId, CancellationToken cancellationToken);

    /// <summary>CAT-011: by Persian-normalised name or SKU (<paramref name="query"/>) or by exact barcode; keyset paged by name.</summary>
    Task<(IReadOnlyList<ProductView> Items, string? NextCursor)> SearchAsync(IUnitOfWorkScope scope, string? query, string? barcode,
        string? cursor, int limit, CancellationToken cancellationToken);

    Task<CatalogResult<ProductView>> UpdateAsync(IUnitOfWorkScope scope, Guid productId, ProductChange change, CancellationToken cancellationToken);

    Task<CatalogResult<VariantView>> AddVariantAsync(IUnitOfWorkScope scope, Guid productId, VariantSpec spec, CancellationToken cancellationToken);

    Task<CatalogResult<BarcodeView>> AddBarcodeAsync(IUnitOfWorkScope scope, Guid productId, Guid variantId, BarcodeSpec spec,
        CancellationToken cancellationToken);

    /// <summary>CAT-008: switch one combination of a matrix off (or back on); versioned on the product.</summary>
    Task<CatalogOutcome> SetVariantStatusAsync(IUnitOfWorkScope scope, Guid productId, Guid variantId, int expectedVersion, string status,
        CancellationToken cancellationToken);

    /// <summary>The variant an active barcode (GTIN-14, or an internal code) currently belongs to (CAT-003, CAT-007).</summary>
    Task<BarcodeMatch?> FindByCodeAsync(IUnitOfWorkScope scope, string codeNormalized, CancellationToken cancellationToken);

    Task<CatalogOutcome> RemoveBarcodeAsync(IUnitOfWorkScope scope, Guid productId, Guid variantId, Guid barcodeId, CancellationToken cancellationToken);

    /// <summary>Which of these ids are variants of this store, active or switched off (stock of either can be adjusted).</summary>
    /// <summary>
    /// BR-044: the units each variant can be counted in, with their factor to the base unit: the base unit (1) and the
    /// packaging unit of each active barcode. A unit whose barcodes disagree on the factor is left out (ambiguous).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, VariantUnits>> UnitsAsync(IUnitOfWorkScope scope, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    Task<SellableVariant?> SellableAsync(IUnitOfWorkScope scope, Guid variantId, CancellationToken cancellationToken);

    Task<IReadOnlySet<Guid>> ExistingVariantsAsync(IUnitOfWorkScope scope, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);
}

/// <summary>CAT-001/002 input rules; error codes are D07 validation codes (invalid-barcode is 422 on its own).</summary>
public static class ProductRules
{
    public static readonly IReadOnlySet<string> Axes = new HashSet<string>(StringComparer.Ordinal) { "color", "size", "model" };

    public static bool ValidUnit(string? unit) => unit is { Length: >= 1 and <= 10 } && unit.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c));

    public static bool ValidSku(string? sku) =>
        sku is { Length: >= 1 and <= 64 } && sku.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '/' or '-');

    /// <summary>D07 Quantity: a positive decimal string, at most 6 decimals (unitFactor of a pack, e.g. "12").</summary>
    public static bool TryFactor(string? text, out decimal factor)
    {
        factor = 1;
        if (text is null)
        {
            return true;
        }

        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out factor)
               && factor > 0 && factor < 1_000_000 && decimal.Round(factor, 6) == factor;
    }

    public static void Name(IDictionary<string, string[]> errors, string field, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
        {
            errors[field] = ["1-200 characters"];
        }
    }
}
