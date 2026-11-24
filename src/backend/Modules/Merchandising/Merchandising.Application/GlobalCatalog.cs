using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Merchandising.Application;

/// <summary>CAT-004: the descriptive fields a candidate suggests. Never a price, cost, stock or supplier (CAT-005).</summary>
public sealed record CandidateFields(string? Name, string? Brand, string? Category, string? NetContent, string? ImageUrl);

/// <summary>
/// CAT-004 / UC-01: one suggestion for an unknown barcode, with where it came from and how sure it is (0..1). A
/// suggestion is never saved on its own: the user confirms it (CAT-004.1, D-17).
/// </summary>
public sealed record CatalogCandidate(string Source, decimal Confidence, DateTimeOffset? LastVerifiedAt, string? License, string? Attribution,
    CandidateFields Fields, Guid? CatalogProductId);

/// <summary>A provider's answer: found with fields, not found, or failed (down, slow, unusable answer).</summary>
public sealed record ProviderLookup(bool Found, CandidateFields? Fields, bool Failed)
{
    public static readonly ProviderLookup Miss = new(false, null, false);
    public static readonly ProviderLookup Failure = new(false, null, true);
}

/// <summary>A licensed product-data provider (ADR-007), e.g. Open Food Facts. Called only with a valid public GTIN-14.</summary>
public interface ICatalogProvider
{
    string Source { get; }

    /// <summary>Off until its licence is reviewed (ADR-007); a disabled provider is neither asked nor cached.</summary>
    bool Enabled { get; }

    string License { get; }

    string Attribution { get; }

    Task<ProviderLookup> LookupAsync(string gtin14, CancellationToken cancellationToken);
}

/// <summary>A provider answer kept so the provider is not asked again for the same code at once.</summary>
public sealed record ProviderRecord(string Source, string Gtin14, bool Found, CandidateFields Fields, decimal Confidence, string License,
    string Attribution, DateTimeOffset FetchedAt);

public interface IGlobalCatalog
{
    /// <summary>Published global catalog products with this public barcode.</summary>
    Task<IReadOnlyList<CatalogCandidate>> ByGtinAsync(IUnitOfWorkScope scope, string gtin14, CancellationToken cancellationToken);

    Task<bool> PublishedAsync(IUnitOfWorkScope scope, Guid catalogProductId, CancellationToken cancellationToken);

    /// <summary>The cached answers of providers for this code, fetched after <paramref name="notBefore"/>.</summary>
    Task<IReadOnlyList<ProviderRecord>> CachedAsync(IUnitOfWorkScope scope, string gtin14, DateTimeOffset notBefore, CancellationToken cancellationToken);

    Task SaveAsync(IUnitOfWorkScope scope, ProviderRecord record, CancellationToken cancellationToken);
}

/// <summary>CAT-004 / CAT-004.1 / TM-041: how candidates are scored, compared and cleaned.</summary>
public static partial class CandidateRules
{
    /// <summary>D-17: at or above this, a single candidate may be pre-selected (the user still confirms the save).</summary>
    public const decimal Threshold = 0.8m;

    /// <summary>A published global product has passed moderation.</summary>
    public const decimal GlobalConfidence = 0.95m;

    /// <summary>A provider's community data is only a suggestion: below the threshold whatever it holds.</summary>
    public static decimal ProviderConfidence(CandidateFields f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return f.Name is null ? 0m : f.Brand is not null ? 0.7m : 0.5m;
    }

    /// <summary>TM-041: provider text is data, never markup — tags, control and direction-override characters go, length is capped.</summary>
    public static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string noTags = Tags().Replace(text, " ");
        var sb = new StringBuilder(noTags.Length);
        foreach (char ch in noTags)
        {
            UnicodeCategory cat = char.GetUnicodeCategory(ch);
            bool bidi = ch is >= '‪' and <= '‮' or >= '⁦' and <= '⁩';
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format && ch != '‌' || bidi || ch is '<' or '>')
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(ch);
            }
        }

        string clean = Spaces().Replace(sb.ToString(), " ").Trim();
        return clean.Length == 0 ? null : clean.Length <= max ? clean : clean[..max].TrimEnd();
    }

    /// <summary>SEC-T-040: an image is only a https link on an allowed host; it is shown by the browser, never fetched by the server.</summary>
    public static string? Image(string? url, IReadOnlyCollection<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo)
               && allowedHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase) && u.AbsoluteUri.Length <= 500
            ? u.AbsoluteUri
            : null;
    }

    /// <summary>CAT-004.1: candidates conflict when their names (or brands, when both have one) differ after normalising.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> Conflicts(IReadOnlyList<CatalogCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        static string Key(string? s) => Spaces().Replace((s ?? "").Replace('ي', 'ی').Replace('ك', 'ک').ToLowerInvariant(), " ").Trim();
        var result = new List<IReadOnlyList<int>>();
        for (int i = 0; i < candidates.Count; i++)
        {
            CandidateFields a = candidates[i].Fields;
            var with = new List<int>();
            for (int j = 0; j < candidates.Count; j++)
            {
                CandidateFields b = candidates[j].Fields;
                if (i != j && (Key(a.Name) != Key(b.Name) || (a.Brand is not null && b.Brand is not null && Key(a.Brand) != Key(b.Brand))))
                {
                    with.Add(j);
                }
            }

            result.Add(with);
        }

        return result;
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
