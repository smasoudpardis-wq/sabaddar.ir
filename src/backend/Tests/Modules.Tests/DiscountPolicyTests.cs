using RetailCloud.BuildingBlocks.Application;
using RetailCloud.Merchandising.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-053 / CAT-010 / D-18 / BR-021 (AT-033, SEC-T-101): best automatic rule and manual discount caps.</summary>
public sealed class DiscountPolicyTests
{
    private static readonly Guid Product = Guid.NewGuid(), B1 = Guid.NewGuid(), B2 = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-12-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static DiscountRuleView Rule(int bp, DateTimeOffset from, DateTimeOffset? to = null, Guid? product = null, params Guid[] branches) =>
        new(Guid.NewGuid(), "r" + bp, "Product", product ?? Product, bp, from, to, branches);

    [Fact]
    public void Of_several_rules_only_the_largest_applies_never_their_sum()
    {
        DiscountRuleView ten = Rule(1000, Now.AddDays(-1)), fifteen = Rule(1500, Now.AddDays(-1));
        Assert.Same(fifteen, DiscountPolicy.Best([ten, fifteen], Product, B1, Now));
        Assert.Equal(1500, DiscountPolicy.Best([ten, fifteen], Product, B1, Now)!.PercentBp);   // not 2500
    }

    [Fact]
    public void A_rule_applies_only_in_its_window_branches_and_product()
    {
        DiscountRuleView future = Rule(5000, Now.AddHours(1)), past = Rule(4000, Now.AddDays(-3), Now.AddDays(-1));
        DiscountRuleView endsNow = Rule(3000, Now.AddDays(-1), Now), onlyB2 = Rule(2500, Now.AddDays(-1), null, null, B2);
        DiscountRuleView other = Rule(9000, Now.AddDays(-1), null, Guid.NewGuid()), small = Rule(500, Now.AddDays(-1));
        Assert.Same(small, DiscountPolicy.Best([future, past, endsNow, onlyB2, other, small], Product, B1, Now));
        Assert.Same(onlyB2, DiscountPolicy.Best([future, past, endsNow, onlyB2, other, small], Product, B2, Now));
        Assert.Null(DiscountPolicy.Best([future, past], Product, B1, Now));
    }

    [Fact]
    public void A_cashier_gives_nothing_by_default_and_a_manager_anything_in_their_branch()
    {
        var register = new ScopeRef(ScopeType.Register, Guid.NewGuid(), B1);
        var none = new Dictionary<string, int>();
        Assert.Equal(0, DiscountPolicy.CapOf([new RoleGrant("Cashier", ScopeType.Branch, B1)], register, none));
        Assert.Equal(10_000, DiscountPolicy.CapOf([new RoleGrant("BranchManager", ScopeType.Branch, B1)], register, none));
        // a manager of another branch has no power here
        Assert.Equal(0, DiscountPolicy.CapOf([new RoleGrant("BranchManager", ScopeType.Branch, B2)], register, none));
        // the store's own caps win over the defaults, and the highest role counts
        var caps = new Dictionary<string, int> { ["Cashier"] = 500, ["BranchManager"] = 2000 };
        Assert.Equal(2000, DiscountPolicy.CapOf([new RoleGrant("Cashier", ScopeType.Branch, B1), new RoleGrant("BranchManager", ScopeType.Branch, B1)], register, caps));
    }

    [Theory]
    [InlineData("Percent", 500, 1_000_000, 500)]
    [InlineData("Amount", 50_000, 1_000_000, 500)]
    [InlineData("Amount", 50_001, 1_000_000, 501)]   // rounded up: 5.0001% is above a 5% cap
    [InlineData("Amount", 2_000_000, 1_000_000, 10_000)]
    public void An_amount_off_is_judged_as_its_share_of_the_line(string kind, long value, long gross, int bp) =>
        Assert.Equal(bp, DiscountPolicy.PercentOf(kind, value, gross));

    [Fact]
    public void The_amount_off_rounds_half_up_to_the_rial() =>
        Assert.Equal(16_667, DiscountPolicy.AmountOff(333_333, 500));   // 16,666.65
}
