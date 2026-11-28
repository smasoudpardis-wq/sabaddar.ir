using RetailCloud.Accounting.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>ACC-002 / AT-102 domain side, and the cash-variance posting rule (ACC-004, D02 §5.8.1).</summary>
public sealed class JournalRulesTests
{
    private static JournalLineSpec Dr(long v) => new(Guid.NewGuid(), v, 0, null, null, null);

    private static JournalLineSpec Cr(long v) => new(Guid.NewGuid(), 0, v, null, null, null);

    [Fact]
    public void A_balanced_entry_of_at_least_two_one_sided_lines_passes() =>
        Assert.Null(JournalRules.Problem([Dr(700), Cr(500), Cr(200)]));

    [Fact]
    public void Unbalanced_single_line_two_sided_and_negative_entries_fail()
    {
        Assert.Contains("unbalanced", JournalRules.Problem([Dr(700), Cr(600)]), StringComparison.Ordinal);
        Assert.NotNull(JournalRules.Problem([Dr(0), Cr(0)]));
        Assert.NotNull(JournalRules.Problem([Dr(100)]));
        Assert.NotNull(JournalRules.Problem([new(Guid.NewGuid(), 100, 100, null, null, null), Cr(200)]));
        Assert.NotNull(JournalRules.Problem([new(Guid.NewGuid(), -5, 0, null, null, null), Cr(5)]));
        Assert.NotNull(JournalRules.Problem([new(Guid.NewGuid(), 5, 0, "Customer", null, null), Cr(5)]));
    }

    [Fact]
    public void A_shortage_debits_CashShortage_an_overage_credits_CashOverage_and_no_variance_posts_nothing()
    {
        Guid shift = Guid.NewGuid();
        PostingDraft shortage = CashVarianceRule.For(shift, -100_000, DateTimeOffset.UtcNow)!;
        Assert.Equal([new RoleLine("CashShortage", 100_000, 0), new RoleLine("CashOnHand", 0, 100_000)], shortage.Lines);
        Assert.Equal(("Shift", shift, "cash-variance", 1), (shortage.SourceType, shortage.SourceId, shortage.Purpose, shortage.RuleVersion));
        PostingDraft overage = CashVarianceRule.For(shift, 30_000, DateTimeOffset.UtcNow)!;
        Assert.Equal([new RoleLine("CashOnHand", 30_000, 0), new RoleLine("CashOverage", 0, 30_000)], overage.Lines);
        Assert.Null(CashVarianceRule.For(shift, 0, DateTimeOffset.UtcNow));
    }
}
