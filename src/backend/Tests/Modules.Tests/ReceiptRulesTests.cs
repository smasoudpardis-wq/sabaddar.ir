using RetailCloud.Accounting.Application;
using RetailCloud.Inventory.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-071 / PRC-001 / PRC-002 / D02 §5.8.1: receipt line cost, UC-05 warnings, the receipt and opening-stock entries.</summary>
public sealed class ReceiptRulesTests
{
    private static readonly Guid V = Guid.CreateVersion7(), S = Guid.CreateVersion7();

    [Theory]
    [InlineData("2", 2_400_000, 100_000, 4_700_000)]
    [InlineData("1.5", 333, 0, 500)]       // 499.5 → 500 (half up)
    [InlineData("0.25", 2, 0, 1)]          // 0.5 → 1
    [InlineData("3", 0, 0, 0)]
    public void A_line_costs_qty_times_unit_cost_half_up_less_its_discount(string qty, long unitCost, long discount, long cost) =>
        Assert.Equal(cost, ReceiptRules.LineCost(decimal.Parse(qty, System.Globalization.CultureInfo.InvariantCulture), unitCost, discount));

    [Fact]
    public void UC05_zero_cost_and_a_seen_invoice_are_warnings()
    {
        ReceiptLine paid = new(1, V, 10, "EA", 1, 500, 0, 0), free = new(2, V, 2, "EA", 1, 0, 0, 0);
        Assert.Empty(ReceiptRules.Warnings([paid], false));
        Assert.Equal(["zero-cost", "duplicate-supplier-invoice"], ReceiptRules.Warnings([paid, free], true));
        Assert.Equal(24m, new ReceiptLine(1, V, 1, "CT", 24, 1, 0, 0).QtyBase);
    }

    [Theory]
    [InlineData("0012345678", true)]
    [InlineData("10101010101", true)]
    [InlineData("123", false)]
    [InlineData("۰۰۱۲۳۴۵۶۷۸", false)]
    public void PRC001_a_national_id_is_10_or_11_digits(string id, bool ok) => Assert.Equal(ok, ReceiptRules.ValidNationalId(id));

    [Fact]
    public void The_receipt_entry_debits_inventory_and_input_VAT_and_credits_the_suppliers_payable()
    {
        PostingDraft d = GoodsReceiptRule.For(V, S, 4_700_000, 470_000, DateTimeOffset.UtcNow, "رسید")!;
        Assert.Equal([new RoleLine("Inventory", 4_700_000, 0), new RoleLine("InputVat", 470_000, 0), new RoleLine("TradePayables", 0, 5_170_000, "Supplier", S)],
            d.Lines);
        Assert.Equal(("GoodsReceipt", "goods-receipt"), (d.SourceType, d.Purpose));
        Assert.Equal(2, GoodsReceiptRule.For(V, S, 1000, 0, DateTimeOffset.UtcNow, "رسید")!.Lines.Count);
        Assert.Null(GoodsReceiptRule.For(V, S, 0, 0, DateTimeOffset.UtcNow, "هدیه"));
    }

    [Fact]
    public void Opening_stock_debits_inventory_against_opening_balance_equity()
    {
        Assert.Equal([new RoleLine("Inventory", 14_000_000, 0), new RoleLine("OpeningBalanceEquity", 0, 14_000_000)],
            OpeningStockRule.For(V, 14_000_000, DateTimeOffset.UtcNow, "شکر")!.Lines);
        Assert.Null(OpeningStockRule.For(V, 0, DateTimeOffset.UtcNow, "شکر"));
    }
}
