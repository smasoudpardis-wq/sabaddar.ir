namespace RetailCloud.BuildingBlocks.Domain;

public enum GtinKind
{
    Ean8,
    UpcA,
    Ean13,
    Gtin14,
}

/// <summary>A validated GS1 trade item number, normalized to 14 digits (CAT-003).</summary>
public readonly record struct Gtin
{
    private Gtin(string normalized14, string original, GtinKind kind)
    {
        Normalized14 = normalized14;
        Original = original;
        Kind = kind;
    }

    /// <summary>14-digit form used for storage and lookup (D05 <c>code_normalized</c>).</summary>
    public string Normalized14 { get; }

    /// <summary>Digits as scanned (Persian digits already converted).</summary>
    public string Original { get; }

    public GtinKind Kind { get; }

    /// <summary>
    /// Restricted-circulation numbers (GS1 prefixes 02 and 20–29 in the 13-digit form, and EAN-8 starting with 2)
    /// are store-internal: valid only inside the issuing tenant, never published to the global catalog (CAT-003.1).
    /// </summary>
    public bool IsRestrictedCirculation =>
        Kind == GtinKind.Ean8
            ? Original[0] == '2'
            : Normalized14[1] == '2' || (Normalized14[1] == '0' && Normalized14[2] == '2');

    public static bool TryParse(string? raw, out Gtin gtin)
    {
        gtin = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string digits = NormalizeDigits(raw.Trim());
        GtinKind? kind = digits.Length switch
        {
            8 => GtinKind.Ean8,
            12 => GtinKind.UpcA,
            13 => GtinKind.Ean13,
            14 => GtinKind.Gtin14,
            _ => null,
        };
        if (kind is null || !digits.All(char.IsAsciiDigit) || !HasValidCheckDigit(digits))
        {
            return false;
        }

        gtin = new Gtin(digits.PadLeft(14, '0'), digits, kind.Value);
        return true;
    }

    /// <summary>GS1 mod-10: weights 3,1,3,… from the rightmost data digit (the digit left of the check digit).</summary>
    public static int ComputeCheckDigit(ReadOnlySpan<char> dataDigits)
    {
        int sum = 0;
        for (int i = 0; i < dataDigits.Length; i++)
        {
            int digit = dataDigits[dataDigits.Length - 1 - i] - '0';
            sum += digit * (i % 2 == 0 ? 3 : 1);
        }

        return (10 - (sum % 10)) % 10;
    }

    private static bool HasValidCheckDigit(string digits) =>
        ComputeCheckDigit(digits.AsSpan(0, digits.Length - 1)) == digits[^1] - '0';

    /// <summary>Accepts Persian (U+06F0–06F9) and Arabic-Indic (U+0660–0669) digits from keyboard wedges (NFR-UX-001).</summary>
    public static string NormalizeDigits(string value) =>
        string.Create(value.Length, value, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                span[i] = c switch
                {
                    >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                    >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                    _ => c,
                };
            }
        });
}
