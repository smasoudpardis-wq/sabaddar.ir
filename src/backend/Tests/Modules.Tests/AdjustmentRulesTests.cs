using RetailCloud.Accounting.Application;
using RetailCloud.Inventory.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-063 / INV-002 / BR-033 / D02 §5.8.1: an adjustment's value, its approval threshold, its binding and its entry.</summary>
public sealed class AdjustmentRulesTests
{
    private static readonly Guid W = Guid.CreateVersion7(), A = Guid.CreateVersion7(), B = Guid.CreateVersion7();

    private static PostedMovement M(decimal qty, long total) =>
        new(1, W, A, qty > 0 ? MovementType.AdjustmentIn : MovementType.AdjustmentOut, qty, 0, total, 0, 0);

    [Fact]
    public void The_value_is_what_went_out_plus_what_came_in_never_netted()
    {
        Assert.Equal((2_360_000L, 360_000L, 2_000_000L), AdjustmentRules.ValueOf([M(-3, -360_000), M(2.5m, 2_000_000)]));
        Assert.Equal((0L, 0L, 0L), AdjustmentRules.ValueOf([M(4, 0)]));   // no WAC yet: no value
    }

    [Theory]
    [InlineData(50_000_000, false)]
    [InlineData(50_000_001, true)]
    [InlineData(0, false)]
    public void BR_033_only_more_than_the_threshold_needs_an_approval(long value, bool needs) =>
        Assert.Equal(needs, AdjustmentRules.NeedsApproval(value, AdjustmentRules.DefaultApprovalThreshold));

    [Theory]
    [InlineData("-3", true)]
    [InlineData("2.500001", true)]
    [InlineData("+1", true)]
    [InlineData("0", false)]
    [InlineData("1.0000001", false)]
    [InlineData("1e3", false)]
    [InlineData("۳", false)]
    [InlineData(null, false)]
    public void A_delta_is_a_signed_non_zero_decimal_of_up_to_six_places(string? text, bool ok) =>
        Assert.Equal(ok, AdjustmentRules.TryDelta(text, out _));

    [Fact]
    public void The_binding_is_the_exact_adjustment_in_any_line_order()
    {
        string one = AdjustmentRules.Binding(W, "Theft", [new(A, -9), new(B, 1)]);
        Assert.Equal(one, AdjustmentRules.Binding(W, "Theft", [new(B, 1), new(A, -9.0m)]));
        Assert.NotEqual(one, AdjustmentRules.Binding(W, "Damage", [new(A, -9), new(B, 1)]));
        Assert.NotEqual(one, AdjustmentRules.Binding(W, "Theft", [new(A, -10), new(B, 1)]));
        Assert.NotEqual(one, AdjustmentRules.Binding(Guid.CreateVersion7(), "Theft", [new(A, -9), new(B, 1)]));
    }

    [Fact]
    public void The_entry_debits_shrinkage_and_credits_overage_against_inventory_and_balances()
    {
        PostingDraft d = InventoryAdjustmentRule.For(A, 360_000, 2_000_000, DateTimeOffset.UtcNow, "خطای شمارش")!;
        Assert.Equal(("StockAdjustment", "inventory-adjustment", 1), (d.SourceType, d.Purpose, d.RuleVersion));
        Assert.Equal([new RoleLine("InventoryShrinkage", 360_000, 0), new RoleLine("Inventory", 0, 360_000),
            new RoleLine("Inventory", 2_000_000, 0), new RoleLine("InventoryOverage", 0, 2_000_000)], d.Lines);
        Assert.Equal(d.Lines.Sum(l => l.Debit), d.Lines.Sum(l => l.Credit));
        Assert.Null(InventoryAdjustmentRule.For(A, 0, 0, DateTimeOffset.UtcNow, "آسیب"));
        Assert.Equal(2, InventoryAdjustmentRule.For(A, 5, 0, DateTimeOffset.UtcNow, "آسیب")!.Lines.Count);
    }
}
