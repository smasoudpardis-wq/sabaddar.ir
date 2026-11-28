using System.Globalization;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Sales.Application;

/// <summary>One row of the daily sales report (RPT-001): finalized sales of a store-local day, by branch and optionally register and cashier.</summary>
public sealed record SalesDailyLine(DateOnly BusinessDate, Guid BranchId, Guid? RegisterId, Guid? CashierId, int Invoices, long Gross, long Discount, long Tax, long Net);

/// <summary>One row of the gross profit report (RPT-001.3): net sales less Σ costAtSale of the group's lines (the moving average of the moment of sale).</summary>
public sealed record GrossProfitLine(string Key, string? Name, long NetSales, long Cost, int ItemsWithZeroCost);

public enum GrossProfitGroup
{
    Day,
    Branch,
    Product,
}

public interface ISalesReports
{
    /// <summary>Finalized sales finalized in [fromDate, toDate), counted on the store's local day (<paramref name="timeZone"/>), at most <paramref name="limit"/> rows.</summary>
    Task<IReadOnlyList<SalesDailyLine>> DailyAsync(IUnitOfWorkScope scope, DateTimeOffset fromDate, DateTimeOffset toDate, string timeZone, IReadOnlyCollection<Guid> branchIds,
        bool byRegister, bool byCashier, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<GrossProfitLine>> GrossProfitAsync(IUnitOfWorkScope scope, DateTimeOffset fromDate, DateTimeOffset toDate, string timeZone, IReadOnlyCollection<Guid> branchIds,
        GrossProfitGroup group, int limit, CancellationToken cancellationToken);
}

/// <summary>RPT-001 / RPT-001.3 arithmetic and the dates a report shows.</summary>
public static class SalesReportRules
{
    /// <summary>Margin in basis points of net sales, rounded to the nearest; 0 when nothing was sold.</summary>
    public static int MarginBp(long netSales, long grossProfit) =>
        netSales <= 0 ? 0 : (int)Math.Round(grossProfit * 10_000m / netSales, MidpointRounding.AwayFromZero);

    /// <summary>The Jalali date (BR-023) as yyyy-MM-dd with ASCII digits, for display and filtering only.</summary>
    public static string Jalali(DateOnly date)
    {
        var calendar = new PersianCalendar();
        DateTime d = date.ToDateTime(TimeOnly.MinValue);
        return string.Create(CultureInfo.InvariantCulture, $"{calendar.GetYear(d):D4}-{calendar.GetMonth(d):D2}-{calendar.GetDayOfMonth(d):D2}");
    }

    /// <summary>CSV text (UTF-8, comma, quotes doubled) with a header, for Excel (RPT-003).</summary>
    public static string Csv(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(rows);
        static string Cell(string v) => v.IndexOfAny([',', '"', '\n', '\r']) >= 0 || v.StartsWith(' ') ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
        var sb = new System.Text.StringBuilder();
        sb.Append(string.Join(',', header.Select(Cell))).Append("\r\n");
        foreach (IReadOnlyList<string> r in rows)
        {
            sb.Append(string.Join(',', r.Select(Cell))).Append("\r\n");
        }

        return sb.ToString();
    }
}
