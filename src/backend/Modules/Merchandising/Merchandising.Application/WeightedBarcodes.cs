using System.Diagnostics;
using System.Globalization;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.Merchandising.Application;

public enum WeightedValueType
{
    Weight,
    Price,
}

/// <summary>
/// CAT-007: how a store's scale prints an EAN-13 label: <c>prefix · item code · [GS1 value check digit] · value · check digit</c>.
/// The item code is the variant's internal code (its PLU on the scale). A Weight value is kilograms with
/// <paramref name="Decimals"/> places; a Price value is whole rials.
/// </summary>
public sealed record WeightedBarcodeProfile(Guid Id, string Prefix, int ItemCodeLength, WeightedValueType ValueType, int ValueLength,
    int Decimals, bool HasInnerCheckDigit);

/// <summary>A decoded label: the item code to look up and the printed weight (kg) or price (rials).</summary>
public sealed record WeightedLabel(WeightedBarcodeProfile Profile, string ItemCode, decimal Value)
{
    public string ValueText => Value.ToString(Profile.ValueType == WeightedValueType.Weight ? "0.000" : "0", CultureInfo.InvariantCulture);
}

public enum WeightedDecodeStatus
{
    /// <summary>No profile's prefix matches: treat the code as an ordinary barcode.</summary>
    NotWeighted,
    Decoded,
    /// <summary>A profile matches but the outer or the inner (price/weight) check digit is wrong: a misprint or misread.</summary>
    Invalid,
}

public static class WeightedBarcodes
{
    public const int MaxProfiles = 10;

    /// <summary>Field errors for one profile (D07 path <paramref name="at"/>); mirrors the merch.weighted_barcode_profile checks.</summary>
    public static void Validate(Dictionary<string, string[]> errors, string at, string? prefix, int? itemCodeLength, string? valueType,
        int? valueLength, int? decimals, bool? hasInnerCheckDigit)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (prefix is not { Length: >= 1 and <= 3 } || prefix[0] != '2' || !prefix.All(char.IsAsciiDigit))
        {
            errors[$"{at}.prefix"] = ["1-3 digits starting with 2 (GS1 restricted circulation)"];
        }

        if (itemCodeLength is not (>= 1 and <= 6)) errors[$"{at}.itemCodeLength"] = ["1-6"];
        if (valueType is not ("Weight" or "Price")) errors[$"{at}.valueType"] = ["Weight|Price"];
        if (valueLength is not (>= 3 and <= 6)) errors[$"{at}.valueLength"] = ["3-6"];
        if (decimals is not (>= 0 and <= 3)) errors[$"{at}.decimals"] = ["0-3"];
        else if (valueType == "Price" && decimals != 0) errors[$"{at}.decimals"] = ["a price is whole rials: 0"];
        if (hasInnerCheckDigit == true && valueLength is not (4 or 5)) errors[$"{at}.hasInnerCheckDigit"] = ["only for a 4- or 5-digit value"];
        if (prefix is { Length: >= 1 and <= 3 } && itemCodeLength is not null && valueLength is not null
            && prefix.Length + itemCodeLength + valueLength + (hasInnerCheckDigit == true ? 1 : 0) != 12)
        {
            errors[at] = ["prefix + itemCodeLength + valueLength (+1 with an inner check digit) must be 12: the data digits of an EAN-13"];
        }
    }

    /// <summary>DI-084: no prefix equals or starts another; returns the first clash.</summary>
    public static (string A, string B)? Overlap(IReadOnlyList<string> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        for (int i = 0; i < prefixes.Count; i++)
        {
            for (int j = i + 1; j < prefixes.Count; j++)
            {
                if (prefixes[i].StartsWith(prefixes[j], StringComparison.Ordinal) || prefixes[j].StartsWith(prefixes[i], StringComparison.Ordinal))
                {
                    return (prefixes[i], prefixes[j]);
                }
            }
        }

        return null;
    }

    public static (WeightedDecodeStatus Status, WeightedLabel? Label) Decode(IEnumerable<WeightedBarcodeProfile> profiles, string? scanned)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        string code = Gtin.NormalizeDigits((scanned ?? "").Trim());
        if (code.Length != 13 || !code.All(char.IsAsciiDigit))
        {
            return (WeightedDecodeStatus.NotWeighted, null);
        }

        WeightedBarcodeProfile? p = profiles.FirstOrDefault(x => code.StartsWith(x.Prefix, StringComparison.Ordinal));
        if (p is null)
        {
            return (WeightedDecodeStatus.NotWeighted, null);
        }

        if (Gtin.ComputeCheckDigit(code.AsSpan(0, 12)) != code[12] - '0')
        {
            return (WeightedDecodeStatus.Invalid, null);
        }

        int at = p.Prefix.Length;
        string item = code.Substring(at, p.ItemCodeLength);
        at += p.ItemCodeLength;
        if (p.HasInnerCheckDigit)
        {
            int inner = code[at] - '0';
            at++;
            if (ValueCheckDigit(code.AsSpan(at, p.ValueLength)) != inner)
            {
                return (WeightedDecodeStatus.Invalid, null);
            }
        }

        decimal raw = long.Parse(code.AsSpan(at, p.ValueLength), NumberStyles.None, CultureInfo.InvariantCulture);
        decimal value = p.ValueType == WeightedValueType.Weight ? raw / Pow10(p.Decimals) : raw;
        return (WeightedDecodeStatus.Decoded, new WeightedLabel(p, item, value));
    }

    /// <summary>
    /// GS1 General Specifications 7.9: check digit of a 4-digit (weights 2-, 2-, 3, 5-) or 5-digit (5+, 2-, 5-, 5+, 2-)
    /// price or weight field.
    /// </summary>
    public static int ValueCheckDigit(ReadOnlySpan<char> value)
    {
        if (value.Length is not (4 or 5))
        {
            throw new ArgumentException("A value check digit covers 4 or 5 digits.", nameof(value));
        }

        Span<int> d = stackalloc int[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            d[i] = value[i] - '0';
        }

        switch (d.Length)
        {
            case 4:
                int sum4 = Two[d[0]] + Two[d[1]] + Three[d[2]] + FiveMinus[d[3]];
                return sum4 * 3 % 10;
            case 5:
                int sum5 = FivePlus[d[0]] + Two[d[1]] + FiveMinus[d[2]] + FivePlus[d[3]] + Two[d[4]];
                int target = (10 - (sum5 % 10)) % 10;
                return Array.IndexOf(FiveMinus, target);
            default:
                throw new UnreachableException();
        }
    }

    /// <summary>
    /// CAT-007.1: a price label sells <c>round(price ÷ unitPrice, 3)</c> of the item, and the line amount is the printed
    /// price (the label is the reference), whatever the rounding of the quantity.
    /// </summary>
    public static (decimal Quantity, long Amount) PriceLabelLine(long labelPrice, long unitPrice)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unitPrice);
        ArgumentOutOfRangeException.ThrowIfNegative(labelPrice);
        return (Math.Round((decimal)labelPrice / unitPrice, 3, MidpointRounding.AwayFromZero), labelPrice);
    }

    private static decimal Pow10(int n) => n switch { 0 => 1m, 1 => 10m, 2 => 100m, _ => 1000m };

    // GS1 weighting-factor tables: the product digit for each input digit 0-9
    private static readonly int[] Two = [0, 2, 4, 6, 8, 9, 1, 3, 5, 7];
    private static readonly int[] Three = [0, 3, 6, 9, 2, 5, 8, 1, 4, 7];
    private static readonly int[] FivePlus = [0, 5, 1, 6, 2, 7, 3, 8, 4, 9];
    private static readonly int[] FiveMinus = [0, 5, 9, 4, 8, 3, 7, 2, 6, 1];
}

/// <summary>The tenant's profile set; <see cref="Version"/> guards a replace (409 version-conflict).</summary>
public sealed record WeightedProfileSet(int Version, IReadOnlyList<WeightedBarcodeProfile> Profiles);

public enum WeightedProfilesOutcome
{
    Done,
    VersionConflict,
    Overlap,
}

public interface IWeightedBarcodeProfiles
{
    Task<WeightedProfileSet> GetAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);

    Task<(WeightedProfilesOutcome Outcome, WeightedProfileSet? Set)> ReplaceAsync(IUnitOfWorkScope scope, int expectedVersion,
        IReadOnlyList<WeightedBarcodeProfile> profiles, CancellationToken cancellationToken);
}
