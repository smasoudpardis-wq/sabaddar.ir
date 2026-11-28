using RetailCloud.Sales.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-161 / RPT-001.3, RPT-003: margin, the Jalali date and the CSV a report is exported as.</summary>
public sealed class SalesReportRulesTests
{
    [Theory]
    [InlineData(400_000, 160_000, 4000)]
    [InlineData(300_000, 120_000, 4000)]
    [InlineData(30_000, 10_000, 3333)]
    [InlineData(30_000, 20_000, 6667)]
    [InlineData(100, -50, -5000)]
    [InlineData(0, 0, 0)]
    public void Margin_is_basis_points_of_net_sales_rounded_and_zero_when_nothing_was_sold(long net, long profit, int bp) =>
        Assert.Equal(bp, SalesReportRules.MarginBp(net, profit));

    [Theory]
    [InlineData(2026, 11, 24, "1405-09-03")]
    [InlineData(2027, 3, 21, "1406-01-01")]
    [InlineData(2027, 3, 20, "1405-12-29")]
    public void The_Jalali_date_is_ascii_year_month_day(int y, int m, int d, string jalali) => Assert.Equal(jalali, SalesReportRules.Jalali(new DateOnly(y, m, d)));

    [Fact]
    public void Csv_quotes_what_needs_it_and_ends_every_row_with_CRLF()
    {
        string csv = SalesReportRules.Csv(["Key", "Name"], [["a,b", "say \"hi\""], ["x", "کالا"]]);
        Assert.Equal("Key,Name\r\n\"a,b\",\"say \"\"hi\"\"\"\r\nx,کالا\r\n", csv);
    }
}
