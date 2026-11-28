using RetailCloud.Accounting.Application;
using RetailCloud.Sales.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-091 / D02 §5.8.1 / BR-056 / SAL-005.1: the sale and cost entries, the invoice number and the price check.</summary>
public sealed class FinalizeRulesTests
{
    private static readonly Guid Sale = Guid.CreateVersion7();

    [Fact]
    public void The_sale_entry_debits_each_payment_method_and_credits_revenue_at_net_and_VAT()
    {
        PostingDraft d = SaleRule.For(Sale, "B1-1405-000001", 230_000, 100_000, 300_000, 30_000, DateTimeOffset.UtcNow);
        Assert.Equal([new RoleLine("CashOnHand", 230_000, 0), new RoleLine("CardInTransit", 100_000, 0), new RoleLine("SalesRevenue", 0, 300_000),
            new RoleLine("OutputVat", 0, 30_000)], d.Lines);
        Assert.Equal(("Sale", "sale"), (d.SourceType, d.Purpose));
        Assert.Equal(2, SaleRule.For(Sale, "x", 110, 0, 110, 0, DateTimeOffset.UtcNow).Lines.Count);   // no VAT, cash only
    }

    [Fact]
    public void Payments_that_do_not_cover_the_bill_cannot_be_posted() =>
        Assert.Throws<ArgumentException>(() => SaleRule.For(Sale, "x", 100, 0, 100, 10, DateTimeOffset.UtcNow));

    [Fact]
    public void The_cost_entry_moves_the_WAC_value_from_inventory_to_COGS()
    {
        Assert.Equal([new RoleLine("COGS", 180_000, 0), new RoleLine("Inventory", 0, 180_000)], SaleCostRule.For(Sale, "x", 180_000, DateTimeOffset.UtcNow)!.Lines);
        Assert.Null(SaleCostRule.For(Sale, "x", 0, DateTimeOffset.UtcNow));   // nothing had a cost yet
    }

    [Theory]
    [InlineData("B1", 1405, 124, "B1-1405-000124")]
    [InlineData("TEH2", 1406, 1_234_567, "TEH2-1406-1234567")]
    public void BR056_an_invoice_number_is_branch_year_and_sequence(string branch, int year, long seq, string expected) =>
        Assert.Equal(expected, InvoiceNumbers.Format(branch, year, seq));

    [Fact]
    public void SAL005_1_any_difference_in_a_line_is_a_price_change()
    {
        var shown = new SaleLineView(1, Guid.Empty, "n", "s", 1, "EA", 1, null, 100_000, 100_000, 0, null, null, null, 100_000, 1000, 10_000, 110_000, 0);
        SaleLineSpec Same(long unitPrice, int rate) => new(Guid.Empty, "n", "s", 1, "EA", 1, null, unitPrice,
            SaleMath.Line(unitPrice, 0, rate, PricingMode.TaxExclusive), null, null, null, rate);
        Assert.True(SaleMath.SameAmounts(shown, Same(100_000, 1000)));
        Assert.False(SaleMath.SameAmounts(shown, Same(120_000, 1000)));
        Assert.False(SaleMath.SameAmounts(shown, Same(100_000, 900)));   // a new tax rate is a change too (BR-017)
    }
}
