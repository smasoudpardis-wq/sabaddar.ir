using RetailCloud.Merchandising.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-045: CAT-007 decoding, the GS1 value check digit, DI-084 prefixes and the CAT-007.1 price-label line.</summary>
public sealed class WeightedBarcodeTests
{
    private static readonly WeightedBarcodeProfile Weight = new(Guid.NewGuid(), "21", 5, WeightedValueType.Weight, 5, 3, false);
    private static readonly WeightedBarcodeProfile Price4 = new(Guid.NewGuid(), "22", 5, WeightedValueType.Price, 4, 0, true);
    private static readonly WeightedBarcodeProfile Price5 = new(Guid.NewGuid(), "20", 4, WeightedValueType.Price, 5, 0, true);

    [Fact]
    public void A_weight_label_gives_the_item_code_and_kilograms()
    {
        var (status, label) = WeightedBarcodes.Decode([Price4, Weight], "2100123012503");
        Assert.Equal(WeightedDecodeStatus.Decoded, status);
        Assert.Same(Weight, label!.Profile);
        Assert.Equal("00123", label.ItemCode);
        Assert.Equal(1.250m, label.Value);
        Assert.Equal("1.250", label.ValueText);
    }

    [Fact]
    public void Persian_digits_from_a_keyboard_wedge_decode_the_same()
    {
        Assert.Equal(1.250m, WeightedBarcodes.Decode([Weight], "۲۱۰۰۱۲۳۰۱۲۵۰۳").Label!.Value);
    }

    [Fact]
    public void A_price_label_with_an_inner_check_digit_gives_rials()
    {
        var (status, label) = WeightedBarcodes.Decode([Weight, Price4], "2200456928752");
        Assert.Equal(WeightedDecodeStatus.Decoded, status);
        Assert.Equal("00456", label!.ItemCode);
        Assert.Equal(2875m, label.Value);
        Assert.Equal("2875", label.ValueText);
    }

    [Theory]
    [InlineData("2875", 9)]    // GS1 GenSpecs worked example, 4-digit field
    [InlineData("14685", 6)]   // GS1 GenSpecs worked example, 5-digit field
    [InlineData("0000", 0)]
    [InlineData("00000", 0)]
    public void The_value_check_digit_follows_the_GS1_weighting_tables(string value, int expected) =>
        Assert.Equal(expected, WeightedBarcodes.ValueCheckDigit(value));

    [Fact]
    public void A_wrong_inner_check_digit_or_outer_check_digit_is_invalid_not_ordinary()
    {
        // inner digit 0 instead of 9 (outer check digit recomputed, so only the inner one is wrong)
        Assert.Equal(WeightedDecodeStatus.Invalid, WeightedBarcodes.Decode([Price4], "2200456028759").Status);
        Assert.Equal(WeightedDecodeStatus.Invalid, WeightedBarcodes.Decode([Weight], "2100123012504").Status);
        Assert.Equal(WeightedDecodeStatus.Decoded, WeightedBarcodes.Decode([Price5], "2007896146858").Status);
    }

    [Theory]
    [InlineData("6260100000018")]   // ordinary GTIN: no profile prefix
    [InlineData("2300123012500")]   // restricted circulation but no profile for 23
    [InlineData("210012301250")]    // 12 digits
    [InlineData("ABC")]
    [InlineData("")]
    public void A_code_no_profile_claims_is_left_to_ordinary_lookup(string code) =>
        Assert.Equal(WeightedDecodeStatus.NotWeighted, WeightedBarcodes.Decode([Weight, Price4], code).Status);

    [Fact]
    public void Prefixes_must_not_overlap()
    {
        Assert.Null(WeightedBarcodes.Overlap(["21", "22", "20"]));
        Assert.Equal(("2", "21"), WeightedBarcodes.Overlap(["2", "21"]));
        Assert.Equal(("21", "21"), WeightedBarcodes.Overlap(["21", "21"]));
        Assert.Equal(("210", "21"), WeightedBarcodes.Overlap(["22", "210", "21"]));
    }

    [Fact]
    public void Validation_requires_the_parts_to_fill_twelve_data_digits()
    {
        var ok = new Dictionary<string, string[]>();
        WeightedBarcodes.Validate(ok, "p", "22", 5, "Price", 4, 0, true);
        Assert.Empty(ok);

        var bad = new Dictionary<string, string[]>();
        WeightedBarcodes.Validate(bad, "p", "22", 5, "Price", 4, 0, false);
        Assert.Contains("p", bad.Keys);

        var many = new Dictionary<string, string[]>();
        WeightedBarcodes.Validate(many, "p", "12", 5, "Price", 6, 2, true);
        Assert.Contains("p.prefix", many.Keys);
        Assert.Contains("p.decimals", many.Keys);
        Assert.Contains("p.hasInnerCheckDigit", many.Keys);
    }

    [Theory]
    [InlineData(37_500, 250_000, 0.150)]
    [InlineData(100_000, 300_000, 0.333)]   // 0.3333… rounds down; the line is still 100,000
    [InlineData(200_000, 300_000, 0.667)]
    [InlineData(5, 2_000, 0.003)]           // 0.0025 rounds half away from zero
    public void A_price_label_sells_the_rounded_quantity_for_exactly_the_printed_price(long label, long unitPrice, double quantity)
    {
        var (q, amount) = WeightedBarcodes.PriceLabelLine(label, unitPrice);
        Assert.Equal((decimal)quantity, q);
        Assert.Equal(label, amount);
    }
}
