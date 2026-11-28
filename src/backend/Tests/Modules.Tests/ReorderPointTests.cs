using RetailCloud.Inventory.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-066 / INV-010 / DE-INV-002: low at or below the reorder point, announced only when a posting crosses it.</summary>
public sealed class ReorderPointTests
{
    [Theory]
    [InlineData(6, 5, false)]
    [InlineData(5, 5, true)]
    [InlineData(0, 0, true)]
    [InlineData(-1, 0, true)]
    public void Low_is_at_or_below_the_point(int available, int point, bool low) => Assert.Equal(low, StockMath.IsLow(available, point));

    [Theory]
    [InlineData(8, 5, 5, true)]    // down onto it
    [InlineData(8, 2, 5, true)]    // down past it
    [InlineData(5, 4, 5, false)]   // already low
    [InlineData(8, 6, 5, false)]   // still above
    [InlineData(4, 9, 5, false)]   // going up
    public void A_crossing_is_from_above_to_at_or_below(int before, int after, int point, bool crosses) =>
        Assert.Equal(crosses, StockMath.CrossesReorderPoint(before, after, point));

    [Fact]
    public void A_balance_knows_whether_it_is_low_from_what_is_available()
    {
        Guid w = Guid.CreateVersion7(), v = Guid.CreateVersion7();
        Assert.True(new StockBalanceView(w, v, 7, 2, 0, 0, 5).BelowReorderPoint);    // 7 − 2 reserved = 5
        Assert.False(new StockBalanceView(w, v, 7, 1, 0, 0, 5).BelowReorderPoint);
        Assert.False(new StockBalanceView(w, v, 0, 0, 0, 0).BelowReorderPoint);      // no point set: never low
    }
}
