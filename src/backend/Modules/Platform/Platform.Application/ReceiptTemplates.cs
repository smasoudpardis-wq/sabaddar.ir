using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Platform.Application;

/// <summary>BR-086 / D-32: an industry's receipt template version, defined by the platform. A version never changes.</summary>
public sealed record IndustryTemplate(string IndustryCode, string IndustryName, int Version, string LegalText, IReadOnlyList<string> LockedFields,
    string Reason, string CreatedBy, DateTimeOffset CreatedAt)
{
    /// <summary>The version as receipts cite it, e.g. <c>supermarket@3</c> (BR-086 snapshot).</summary>
    public string Ref => $"{IndustryCode}@{Version}";
}

public enum TemplateOutcome
{
    Done,
    NotFound,
    VersionConflict,
}

public interface IIndustryTemplates
{
    /// <summary>Every industry with its newest template version.</summary>
    Task<IReadOnlyList<IndustryTemplate>> LatestAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);

    Task<IndustryTemplate?> LatestAsync(IUnitOfWorkScope scope, string industryCode, CancellationToken cancellationToken);

    /// <summary>The version a receipt cites (e.g. <c>supermarket@3</c>): versions never change, so a reprint shows the same text.</summary>
    Task<IndustryTemplate?> VersionAsync(IUnitOfWorkScope scope, string templateRef, CancellationToken cancellationToken);

    /// <summary>Publishes version <paramref name="expectedVersion"/> + 1; a concurrent publish of the same number loses (409).</summary>
    Task<(TemplateOutcome Outcome, IndustryTemplate? Template)> PublishAsync(IUnitOfWorkScope scope, string industryCode, int expectedVersion,
        string legalText, IReadOnlyList<string> lockedFields, string reason, string createdBy, CancellationToken cancellationToken);
}

/// <summary>BR-086 / SAL-012: the fields a receipt can show, and the rules for an industry's template.</summary>
public static class ReceiptTemplateRules
{
    public static readonly IReadOnlyList<string> Fields =
    [
        "legalName", "economicCode", "nationalId", "businessLicenseNo", "branchAddress", "invoiceNo", "issuedAt", "cashier", "lines", "totals",
        "tax", "payments", "qrCode",
    ];

    /// <summary>A receipt is not a receipt without these: no industry template may unlock them.</summary>
    public static readonly IReadOnlyList<string> AlwaysLocked = ["legalName", "invoiceNo", "issuedAt", "lines", "totals"];

    public static string? Problem(string? legalText, IReadOnlyList<string>? lockedFields)
    {
        if (legalText is null || legalText.Trim().Length is < 1 or > 2000)
        {
            return "legalText: 1-2000 characters";
        }

        if (lockedFields is null || lockedFields.Count == 0 || lockedFields.Distinct(StringComparer.Ordinal).Count() != lockedFields.Count)
        {
            return "lockedFields: distinct field names";
        }

        if (lockedFields.FirstOrDefault(f => !Fields.Contains(f)) is { } unknown)
        {
            return $"lockedFields: unknown field {unknown}";
        }

        return AlwaysLocked.FirstOrDefault(f => !lockedFields.Contains(f)) is { } missing ? $"lockedFields: {missing} is always shown" : null;
    }
}
