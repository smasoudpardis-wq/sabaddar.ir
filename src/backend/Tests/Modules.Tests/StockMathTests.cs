using FsCheck.Xunit;
using RetailCloud.Inventory.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-061 / BR-042 / BR-043 / INV-006 (AT-046): weighted average cost and movement values.</summary>
public sealed class StockMathTests
{
    [Fact]
    public void A_receipt_re_averages_the_cost_and_an_issue_is_valued_at_it()
    {
        var s = new StockState(0, 0, 0);
        (s, long unit, long total) = StockMath.Apply(s, 10, 1_000_000);   // 10 at 100,000
        Assert.Equal((100_000L, 1_000_000L), (unit, total));
        (s, _, _) = StockMath.Apply(s, 5, 650_000);                      // 5 at 130,000
        Assert.Equal(new StockState(15, 110_000, 1_650_000), s);         // (10×100,000 + 650,000) ÷ 15
        (s, unit, total) = StockMath.Apply(s, -4, null);
        Assert.Equal((110_000L, -440_000L), (unit, total));
        Assert.Equal(new StockState(11, 110_000, 1_210_000), s);         // an issue never changes the WAC
    }

    [Fact]
    public void The_average_rounds_half_up_to_whole_rials()
    {
        (StockState s, _, _) = StockMath.Apply(new StockState(2, 100, 200), 1, 101);   // 301 ÷ 3 = 100.33
        Assert.Equal(100, s.Wac);
        (s, _, _) = StockMath.Apply(new StockState(1, 100, 100), 1, 101);              // 201 ÷ 2 = 100.5
        Assert.Equal(101, s.Wac);
    }

    [Fact]
    public void After_selling_short_the_next_receipt_sets_the_cost_by_itself()
    {
        // AllowWithWarning: -3 on hand at WAC 100,000; a receipt of 10 at 120,000 makes WAC 120,000 (BR-043)
        (StockState s, _, _) = StockMath.Apply(new StockState(-3, 100_000, -300_000), 10, 1_200_000);
        Assert.Equal(120_000, s.Wac);
        Assert.Equal(7, s.OnHand);
        Assert.Equal(900_000, s.Value);   // value stays Σ movements; the 60,000 gap is the cost adjustment (D-15)
    }

    [Fact]
    public void Fractional_quantities_are_valued_to_the_rial()
    {
        (StockState s, _, _) = StockMath.Apply(new StockState(0, 0, 0), 2.5m, 1_000_000);   // 2.5 kg at 400,000
        (_, long unit, long total) = StockMath.Apply(s, -0.333m, null);
        Assert.Equal(400_000, unit);
        Assert.Equal(-133_200, total);
    }

    [Fact]
    public void An_inbound_line_without_its_own_cost_comes_in_at_the_current_cost()
    {
        (StockState s, long unit, long total) = StockMath.Apply(new StockState(5, 80_000, 400_000), 2, null);
        Assert.Equal((80_000L, 160_000L), (unit, total));
        Assert.Equal(new StockState(7, 80_000, 560_000), s);
    }

    [Property(MaxTest = 300)]
    public bool Replaying_the_ledger_gives_back_the_balance(int[] seeds)
    {
        var s = new StockState(0, 0, 0);
        var ledger = new List<(decimal, long, bool)>();
        foreach (int seed in seeds ?? [])
        {
            decimal qty = (seed % 7) - 3 is 0 ? 1 : (seed % 7) - 3;
            long? cost = qty > 0 && seed % 2 == 0 ? Math.Abs((long)seed % 1_000_000) : null;
            (s, _, long total) = StockMath.Apply(s, qty, cost);
            ledger.Add((qty, total, cost is not null));
        }

        return StockMath.Replay(ledger) == s;
    }

    [Fact]
    public void The_most_specific_policy_wins_and_the_default_is_forbid()
    {
        Assert.Equal(NegativeStockPolicy.Forbid, StockMath.Resolve(null, null, null));
        Assert.Equal(NegativeStockPolicy.AllowWithWarning, StockMath.Resolve(null, null, NegativeStockPolicy.AllowWithWarning));
        Assert.Equal(NegativeStockPolicy.Forbid, StockMath.Resolve(null, NegativeStockPolicy.Forbid, NegativeStockPolicy.AllowWithWarning));
        Assert.Equal(NegativeStockPolicy.AllowWithWarning, StockMath.Resolve(NegativeStockPolicy.AllowWithWarning, NegativeStockPolicy.Forbid, null));
    }

    [Fact]
    public void A_line_in_cartons_is_kept_in_base_units()
    {
        var line = new StockLine(1, Guid.NewGuid(), Guid.NewGuid(), MovementType.GoodsReceipt, 2, 480_000, new EnteredUnit("CT", 24));
        Assert.Equal(48, line.QtyBase);
    }
}
