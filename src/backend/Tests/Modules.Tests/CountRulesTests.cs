using RetailCloud.Inventory.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-065 / INV-009 / AT-280 / SoD-03: a count's difference, its lines and who confirms it.</summary>
public sealed class CountRulesTests
{
    private static readonly Guid V = Guid.CreateVersion7();

    [Fact]
    public void AT280_snapshot_10_two_sold_8_counted_is_no_difference() => Assert.Equal(0m, CountRules.Difference(10, -2, 8));

    [Theory]
    [InlineData(10, 8, 8, null, 0)]     // closed: 2 sold while counting; 8 found
    [InlineData(10, 8, 7, null, -1)]    // one missing
    [InlineData(0, 3, 4, null, 1)]      // received while counting; one more found
    [InlineData(5, null, 0, 5, -5)]     // open: nothing counted yet against 5 now
    public void A_lines_difference_is_counted_less_what_the_system_held_at_close(int snapshot, int? atClose, int counted, int? now, int difference)
    {
        var line = new CountLine(V, snapshot, atClose, now ?? 0, counted, counted != 0);
        Assert.Equal(difference, line.Difference);
        Assert.Equal((atClose ?? now ?? 0) - snapshot, line.MovementsSince);
    }

    [Fact]
    public void SoD03_a_counter_is_whoever_entered_a_batch()
    {
        Guid clerk = Guid.CreateVersion7(), manager = Guid.CreateVersion7();
        var count = new CountView(Guid.CreateVersion7(), Guid.CreateVersion7(), "All", true, "Closed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null,
            manager, 2, [clerk]);
        Assert.True(CountRules.IsCounter(count, clerk));
        Assert.False(CountRules.IsCounter(count, manager));
    }

    [Fact]
    public void The_approval_binds_the_count_and_its_corrections_in_any_order()
    {
        Guid count = Guid.CreateVersion7(), a = Guid.CreateVersion7(), b = Guid.CreateVersion7();
        string one = CountRules.Binding(count, [new(a, -1), new(b, -2)]);
        Assert.Equal(one, CountRules.Binding(count, [new(b, -2), new(a, -1)]));
        Assert.NotEqual(one, CountRules.Binding(count, [new(a, -1), new(b, -3)]));
        Assert.NotEqual(one, CountRules.Binding(Guid.CreateVersion7(), [new(a, -1), new(b, -2)]));
    }
}
