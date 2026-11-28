using System.Globalization;

namespace RetailCloud.Accounting.Application;

/// <summary>A fiscal month: [StartUtc, EndUtc) — the shape of acc.fiscal_period.span.</summary>
public sealed record FiscalPeriodSpan(int FiscalYear, int PeriodNo, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

/// <summary>
/// Default fiscal year = Jalali calendar year (Farvardin 1 → Esfand end), twelve monthly periods starting at local
/// midnight in the tenant's time zone (TEN-001.1). Month lengths come from <see cref="PersianCalendar"/> (31×6, 30×5,
/// 29/30), so leap years are handled.
/// </summary>
public static class FiscalCalendar
{
    private static readonly PersianCalendar Persian = new();

    public static int FiscalYearOf(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        return Persian.GetYear(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);
    }

    public static IReadOnlyList<FiscalPeriodSpan> Periods(int jalaliYear, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var periods = new List<FiscalPeriodSpan>(12);
        for (int month = 1; month <= 12; month++)
        {
            DateTime start = Persian.ToDateTime(jalaliYear, month, 1, 0, 0, 0, 0);
            DateTime end = month == 12 ? Persian.ToDateTime(jalaliYear + 1, 1, 1, 0, 0, 0, 0) : Persian.ToDateTime(jalaliYear, month + 1, 1, 0, 0, 0, 0);
            periods.Add(new FiscalPeriodSpan(jalaliYear, month, LocalMidnightUtc(start, timeZone), LocalMidnightUtc(end, timeZone)));
        }

        return periods;
    }

    private static DateTimeOffset LocalMidnightUtc(DateTime local, TimeZoneInfo tz)
    {
        DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // A midnight skipped by a DST jump starts the day at the first valid local time.
        while (tz.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddMinutes(30);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, tz), TimeSpan.Zero);
    }
}
