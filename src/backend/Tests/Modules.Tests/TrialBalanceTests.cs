using RetailCloud.Accounting.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-114 / ACC-007 (AT-107): the trial balance in chart order, groups summing their accounts, always balanced.</summary>
public sealed class TrialBalanceTests
{
    private static readonly AccountView Assets = new(Guid.CreateVersion7(), "1", "دارایی‌ها", "Asset", null, true, false, false);
    private static readonly AccountView Current = new(Guid.CreateVersion7(), "11", "دارایی جاری", "Asset", Assets.Id, true, false, false);
    private static readonly AccountView Cash = new(Guid.CreateVersion7(), "1101", "صندوق", "Asset", Current.Id, true, false, true);
    private static readonly AccountView Bank = new(Guid.CreateVersion7(), "1102", "بانک", "Asset", Current.Id, true, false, true);
    private static readonly AccountView Revenue = new(Guid.CreateVersion7(), "4", "درآمد", "Revenue", null, true, false, false);
    private static readonly AccountView Sales = new(Guid.CreateVersion7(), "4101", "فروش", "Revenue", Revenue.Id, true, false, true);
    private static readonly AccountView Fees = new(Guid.CreateVersion7(), "5106", "کارمزد", "Expense", null, true, false, true);
    private static readonly AccountView[] Chart = [Sales, Bank, Fees, Revenue, Cash, Assets, Current];

    // before the range: a sale of 1,000 in cash; in it: 300 more in cash, 600 moved to the bank
    private static readonly AccountMovement[] Moves =
    [
        new(Cash.Id, 1000, 0, 300, 600),
        new(Bank.Id, 0, 0, 600, 0),
        new(Sales.Id, 0, 1000, 0, 300),
    ];

    [Fact]
    public void AT107_rows_follow_the_chart_groups_sum_their_accounts_and_the_totals_balance()
    {
        (IReadOnlyList<TrialBalanceLine> lines, TrialBalanceTotals totals) = TrialBalance.Build(Chart, Moves, includeEmpty: false);
        Assert.Equal(new[] { "1", "11", "1101", "1102", "4", "4101" }, lines.Select(l => l.Account.Code));
        Assert.Equal(new[] { 0, 1, 2, 2, 0, 1 }, lines.Select(l => l.Level));

        TrialBalanceLine cash = lines.Single(l => l.Account == Cash), current = lines.Single(l => l.Account == Current);
        Assert.Equal((1000L, 0L, 300L, 600L, 700L, 0L), (cash.OpeningDebit, cash.OpeningCredit, cash.PeriodDebit, cash.PeriodCredit, cash.ClosingDebit, cash.ClosingCredit));
        Assert.Equal((1000L, 900L, 600L, 1300L), (current.OpeningDebit, current.PeriodDebit, current.PeriodCredit, current.ClosingDebit));
        TrialBalanceLine sales = lines.Single(l => l.Account == Sales);
        Assert.Equal((0L, 1000L, 0L, 1300L), (sales.OpeningDebit, sales.OpeningCredit, sales.ClosingDebit, sales.ClosingCredit));

        Assert.Equal(new TrialBalanceTotals(1000, 1000, 900, 900, 1300, 1300), totals);   // the groups are not counted twice
        Assert.True(totals.Balanced);
    }

    [Fact]
    public void Accounts_with_nothing_are_hidden_unless_asked_and_a_balance_that_crosses_zero_changes_side()
    {
        Assert.DoesNotContain(TrialBalance.Build(Chart, Moves, includeEmpty: false).Lines, l => l.Account == Fees);
        Assert.Equal(Chart.Length, TrialBalance.Build(Chart, Moves, includeEmpty: true).Lines.Count);

        TrialBalanceLine overdrawn = TrialBalance.Build([Bank], [new AccountMovement(Bank.Id, 100, 0, 0, 250)], false).Lines.Single();
        Assert.Equal((100L, 0L, 0L, 150L), (overdrawn.OpeningDebit, overdrawn.OpeningCredit, overdrawn.ClosingDebit, overdrawn.ClosingCredit));
    }

    [Fact]
    public void A_group_ledger_takes_every_account_under_it()
    {
        Assert.Equal(new[] { Assets.Id, Current.Id, Cash.Id, Bank.Id }.Order(), TrialBalance.WithDescendants(Chart, Assets.Id).Order());
        Assert.Equal(new[] { Cash.Id }, TrialBalance.WithDescendants(Chart, Cash.Id));
    }
}
