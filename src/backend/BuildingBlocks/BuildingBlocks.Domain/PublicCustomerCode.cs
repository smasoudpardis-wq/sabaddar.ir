using System.Security.Cryptography;

namespace RetailCloud.BuildingBlocks.Domain;

/// <summary>
/// Random public customer code "CU-XXXXXXXX" in Crockford Base32 (~40 bits, CUS-002). It identifies, it never authorizes
/// (BR-061). Format matches the database constraint in docs/05-data/sql/60_cid.sql.
/// </summary>
public readonly record struct PublicCustomerCode
{
    public const string Prefix = "CU-";
    public const int Length = 8;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public string Value { get; }

    private PublicCustomerCode(string value) => Value = value;

    public static PublicCustomerCode Generate()
    {
        Span<char> chars = stackalloc char[Length];
        for (int i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new PublicCustomerCode(Prefix + new string(chars));
    }

    /// <summary>Lenient parsing of what a cashier types: case-insensitive, hyphen optional, Crockford aliases (O→0, I/L→1).</summary>
    public static bool TryParse(string? input, out PublicCustomerCode code)
    {
        code = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string s = input.Trim().ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (s.StartsWith(Prefix, StringComparison.Ordinal))
        {
            s = s[Prefix.Length..];
        }
        else if (s.StartsWith("CU", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        if (s.Length != Length)
        {
            return false;
        }

        Span<char> normalized = stackalloc char[Length];
        for (int i = 0; i < Length; i++)
        {
            char c = s[i] switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            if (!Alphabet.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }

            normalized[i] = c;
        }

        code = new PublicCustomerCode(Prefix + new string(normalized));
        return true;
    }

    public override string ToString() => Value;
}
