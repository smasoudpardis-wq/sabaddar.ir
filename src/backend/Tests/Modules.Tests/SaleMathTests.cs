using FsCheck.Xunit;
using RetailCloud.Sales.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-082 / BR-013 / BR-015 / BR-016 / D-13: line amounts rounded half up to the rial at each step.</summary>
public sealed class SaleMathTests
{
    [Fact]
    public void BR013_exclusive_tax_is_on_the_net_after_discount()
    {
        Assert.Equal(new LineAmounts(2_400_000, 240_000, 2_160_000, 216_000, 2_376_000), SaleMath.Line(2_400_000, 240_000, 1000, PricingMode.TaxExclusive));
        Assert.Equal(new LineAmounts(15, 0, 15, 2, 17), SaleMath.Line(15, 0, 1000, PricingMode.TaxExclusive));   // 1.5 → 2 (half up)
    }

    [Fact]
    public void BR016_inclusive_price_contains_the_tax()
    {
        Assert.Equal(new LineAmounts(1_000_000, 0, 917_431, 82_569, 1_000_000), SaleMath.Line(1_000_000, 0, 900, PricingMode.TaxInclusive));
        Assert.Equal(new LineAmounts(500, 500, 0, 0, 0), SaleMath.Line(500, 500, 1000, PricingMode.TaxInclusive));
    }

    [Theory]
    [InlineData(100_000, "24", 2_400_000)]
    [InlineData(33_333, "0.5", 16_667)]   // 16,666.5 → 16,667
    public void A_units_price_is_the_base_price_times_its_factor(long basePrice, string factor, long expected) =>
        Assert.Equal(expected, SaleMath.UnitPrice(basePrice, decimal.Parse(factor, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void A_discount_beyond_the_line_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SaleMath.Line(100, 101, 0, PricingMode.TaxExclusive));

    [Property]
    public bool DI001_every_line_total_is_its_net_plus_its_tax_and_never_more_than_one_rial_off_the_unrounded(uint grossSeed, byte discountPct, ushort rate, bool inclusive)
    {
        long gross = grossSeed % 100_000_000;
        long discount = gross * (discountPct % 101) / 100;
        int bp = rate % 10_001;
        LineAmounts a = SaleMath.Line(gross, discount, bp, inclusive ? PricingMode.TaxInclusive : PricingMode.TaxExclusive);
        decimal exact = inclusive ? gross - discount : (gross - discount) * (1 + bp / 10_000m);
        return a.LineTotal == a.Net + a.Tax && a.Tax >= 0 && a.Net >= 0 && Math.Abs(a.LineTotal - exact) <= 0.5m;
    }
}
