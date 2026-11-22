using FsCheck.Xunit;
using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.BuildingBlocks.Domain.Tests;

public class GtinTests
{
    [Theory]
    [InlineData("4006381333931", GtinKind.Ean13, "04006381333931")]
    [InlineData("036000291452", GtinKind.UpcA, "00036000291452")]
    [InlineData("96385074", GtinKind.Ean8, "00000096385074")]
    [InlineData("۴۰۰۶۳۸۱۳۳۳۹۳۱", GtinKind.Ean13, "04006381333931")] // Persian digits from a keyboard wedge
    public void Valid_codes_are_normalized_to_gtin14(string raw, GtinKind kind, string normalized)
    {
        Assert.True(Gtin.TryParse(raw, out Gtin gtin));
        Assert.Equal(kind, gtin.Kind);
        Assert.Equal(normalized, gtin.Normalized14);
    }

    [Theory]
    [InlineData("4006381333932")] // wrong check digit
    [InlineData("12345")]         // wrong length
    [InlineData("40063813339A1")] // non-digit
    [InlineData("")]
    public void Invalid_codes_are_rejected(string raw) => Assert.False(Gtin.TryParse(raw, out _));

    [Theory]
    [InlineData("2001234567893", true)]  // prefix 20 → restricted circulation (store-internal / weighted)
    [InlineData("0212345678908", true)]  // prefix 02
    [InlineData("4006381333931", false)]
    public void Restricted_circulation_prefixes_are_detected(string raw, bool expected)
    {
        string data = raw[..^1];
        string code = data + Gtin.ComputeCheckDigit(data);
        Assert.True(Gtin.TryParse(code, out Gtin gtin));
        Assert.Equal(expected, gtin.IsRestrictedCirculation);
    }

    [Property(MaxTest = 2000)]
    public bool Any_13_digit_payload_with_computed_check_digit_is_valid_and_any_single_digit_change_is_detected(
        ulong seed, byte position, byte delta)
    {
        string data = (seed % 1_000_000_000_000UL).ToString("D12", System.Globalization.CultureInfo.InvariantCulture);
        string code = data + Gtin.ComputeCheckDigit(data);
        if (!Gtin.TryParse(code, out _))
        {
            return false;
        }

        int pos = position % code.Length;
        int d = (delta % 9) + 1; // change by 1..9
        char[] mutated = code.ToCharArray();
        mutated[pos] = (char)('0' + ((mutated[pos] - '0' + d) % 10));
        return !Gtin.TryParse(new string(mutated), out _);
    }
}

public class PublicCustomerCodeTests
{
    private static readonly System.Text.RegularExpressions.Regex DbConstraint =
        new("^CU-[0-9A-HJKMNP-TV-Z]{8}$"); // same pattern as cid.global_customer CHECK (60_cid.sql)

    [Fact]
    public void Generated_codes_always_satisfy_the_database_constraint_and_are_distinct()
    {
        var codes = Enumerable.Range(0, 10_000).Select(_ => PublicCustomerCode.Generate().Value).ToList();
        Assert.All(codes, c => Assert.Matches(DbConstraint, c));
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Theory]
    [InlineData("cu-7p4k9x2q", "CU-7P4K9X2Q")]
    [InlineData("CU 7P4K 9X2Q", "CU-7P4K9X2Q")]
    [InlineData("7P4K9X2Q", "CU-7P4K9X2Q")]
    [InlineData("CU-7P4K9X2O", "CU-7P4K9X20")] // O → 0
    [InlineData("CU-7P4KLX2I", "CU-7P4K1X21")] // L, I → 1
    public void Typed_codes_are_normalized(string input, string expected)
    {
        Assert.True(PublicCustomerCode.TryParse(input, out var code));
        Assert.Equal(expected, code.Value);
    }

    [Theory]
    [InlineData("CU-7P4K9X2")]   // too short
    [InlineData("CU-7P4K9X2U")]  // U is not in the alphabet
    [InlineData("XX-7P4K9X2Q")]
    public void Malformed_codes_are_rejected(string input) => Assert.False(PublicCustomerCode.TryParse(input, out _));
}
