using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.BuildingBlocks.Domain.Tests;

public class MoneyTests
{
    [Fact]
    public void Sequential_returns_follow_BR054_final_return_takes_exact_remainder_D08_F04()
    {
        // lineTotal 1,210,000 for 3 units, returned one unit at a time (BR-054, D08 §F-04):
        // each partial return = round(lineTotal × q / qty); the last unit returns exactly what remains.
        Money lineTotal = Money.Irr(1_210_000);
        Money first = lineTotal.Multiply(1m / 3m);
        Money second = lineTotal.Multiply(1m / 3m);
        Money last = lineTotal - first - second;
        Assert.Equal([403_333L, 403_333L, 403_334L], new[] { first.Amount, second.Amount, last.Amount });
    }

    [Fact]
    public void Upfront_allocation_gives_leftover_to_largest_remainder_then_lowest_index()
    {
        // Used for invoice-level discount split across lines (BR-014); deterministic tie-break.
        var parts = Money.Irr(1_210_000).Allocate([1, 1, 1]);
        Assert.Equal([403_334L, 403_333L, 403_333L], parts.Select(p => p.Amount));
    }

    [Fact]
    public void Sale_example_J1_two_units_at_500000_with_10_percent_vat_totals_1100000()
    {
        Money net = Money.Irr(500_000).Multiply(Quantity.Of(2));
        Money tax = new TaxRate(1000).TaxOnNet(net);
        Assert.Equal(Money.Irr(1_000_000), net);
        Assert.Equal(Money.Irr(100_000), tax);
        Assert.Equal(Money.Irr(1_100_000), net + tax);
    }

    [Theory]
    [InlineData(5, 1000, 1)]     // 0.5 → 1
    [InlineData(-5, 1000, -1)]   // −0.5 → −1 (refund mirrors sale)
    [InlineData(4, 1000, 0)]     // 0.4 → 0
    [InlineData(15, 1000, 2)]    // 1.5 → 2
    public void Multiply_rounds_half_away_from_zero(long amount, int basisPoints, long expected) =>
        Assert.Equal(expected, Money.Irr(amount).Multiply(new Percentage(basisPoints).AsFactor).Amount);

    [Fact]
    public void Wire_format_is_an_integer_string_and_round_trips() =>
        Assert.Equal(Money.Irr(-12_500_000), Money.FromWireString(Money.Irr(-12_500_000).ToWireString()));

    [Fact]
    public void Wire_format_rejects_decimals() =>
        Assert.Throws<FormatException>(() => Money.FromWireString("12.5"));

    [Fact]
    public void Allocate_rejects_zero_or_negative_weights()
    {
        Assert.Throws<ArgumentException>(() => Money.Irr(10).Allocate([0, 0]));
        Assert.Throws<ArgumentException>(() => Money.Irr(10).Allocate([1, -1]));
        Assert.Throws<ArgumentException>(() => Money.Irr(10).Allocate([]));
    }

    [Fact]
    public void Addition_overflow_throws_instead_of_wrapping() =>
        Assert.Throws<OverflowException>(() => Money.Irr(long.MaxValue) + Money.Irr(1));

    [Fact]
    public void Quantity_enforces_scale_and_unit_precision()
    {
        Assert.Throws<ArgumentException>(() => Quantity.Of(1.1234567m));
        Assert.Equal(Quantity.Of(0.75m), Quantity.Of(0.75m).EnsurePrecision(3));
        Assert.Throws<ArgumentException>(() => Quantity.Of(1.5m).EnsurePrecision(0));
        Assert.Equal("0.75", Quantity.FromWireString("0.750").ToWireString());
    }
}
