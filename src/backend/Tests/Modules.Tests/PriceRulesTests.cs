using RetailCloud.Merchandising.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-052 / BR-041 [ASM]: when a price change needs approval, and what exactly the approval binds (D-61).</summary>
public sealed class PriceRulesTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a"), B = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    [Theory]
    [InlineData(100_000, 130_000, false)]   // exactly +30%: no approval
    [InlineData(100_000, 130_001, true)]
    [InlineData(100_000, 70_000, false)]    // exactly -30%
    [InlineData(100_000, 69_999, true)]
    [InlineData(3, 4, true)]                // +33% on a tiny price still counts
    public void A_change_of_more_than_30_percent_either_way_needs_approval(long current, long next, bool needs)
    {
        var r = PriceRules.NeedsApproval([new PriceChange(A, current, next)], 20, 30);
        Assert.Equal(needs, r is not null);
        if (needs) Assert.Equal(("change-limit", "30%"), r);
    }

    [Fact]
    public void A_first_price_has_nothing_to_compare_with()
    {
        Assert.Null(PriceRules.NeedsApproval([new PriceChange(A, null, 999_999_999)], 20, 30));
    }

    [Fact]
    public void More_than_twenty_variants_at_once_is_a_bulk_change()
    {
        PriceChange[] twenty = [.. Enumerable.Range(0, 20).Select(_ => new PriceChange(Guid.NewGuid(), null, 1000))];
        Assert.Null(PriceRules.NeedsApproval(twenty, 20, 30));
        Assert.Equal(("bulk", "20"), PriceRules.NeedsApproval([.. twenty, new PriceChange(Guid.NewGuid(), null, 1000)], 20, 30));
    }

    [Fact]
    public void The_binding_covers_every_price_and_the_start_but_not_the_order()
    {
        Guid list = Guid.NewGuid();
        DateTimeOffset t = DateTimeOffset.Parse("2026-12-01T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        string ab = PriceRules.Binding(list, [new PriceItem(A, 100), new PriceItem(B, 200)], t);
        Assert.Equal(ab, PriceRules.Binding(list, [new PriceItem(B, 200), new PriceItem(A, 100)], t));
        Assert.NotEqual(ab, PriceRules.Binding(list, [new PriceItem(A, 100), new PriceItem(B, 201)], t));
        Assert.NotEqual(ab, PriceRules.Binding(list, [new PriceItem(A, 100), new PriceItem(B, 200)], t.AddMilliseconds(1)));
        Assert.NotEqual(ab, PriceRules.Binding(Guid.NewGuid(), [new PriceItem(A, 100), new PriceItem(B, 200)], t));
        Assert.NotEqual(ab, PriceRules.Binding(list, [new PriceItem(A, 100)], t));
    }
}
