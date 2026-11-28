using System.Text.RegularExpressions;
using FsCheck.Xunit;
using RetailCloud.Accounting.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>TEN-001.1 / D-30 / D-59: default chart and Jalali fiscal calendar created at tenant registration.</summary>
public sealed class AccountingSetupTests
{
    private static readonly TimeZoneInfo Tehran = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tehran");

    [Theory]
    [InlineData("supermarket")]
    [InlineData("apparel")]
    [InlineData("general")]
    public void Every_posting_role_maps_to_exactly_one_postable_leaf(string industry)
    {
        IReadOnlyList<AccountTemplate> chart = ChartOfAccountsTemplate.For(industry);
        var roles = chart.Where(a => a.Role is not null).ToList();
        Assert.Equal(ChartOfAccountsTemplate.Roles.Order(), roles.Select(a => a.Role!).Order());
        Assert.All(roles, a => Assert.True(a.Postable, a.Code));
    }

    [Fact]
    public void Roles_match_the_database_check_constraint()
    {
        string ddl = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "05-data", "sql", "50_acc.sql"));
        string check = Regex.Match(ddl, @"role\s+text NOT NULL CHECK \(role IN \(([^)]*)\)\)", RegexOptions.Singleline).Groups[1].Value;
        var dbRoles = Regex.Matches(check, "'([A-Za-z]+)'").Select(m => m.Groups[1].Value).Order();
        Assert.Equal(ChartOfAccountsTemplate.Roles.Order(), dbRoles);
    }

    [Fact]
    public void Chart_is_a_well_formed_tree()
    {
        IReadOnlyList<AccountTemplate> chart = ChartOfAccountsTemplate.For("supermarket");
        var byCode = chart.ToDictionary(a => a.Code);
        Assert.Equal(chart.Count, byCode.Count); // unique codes
        foreach (AccountTemplate a in chart)
        {
            if (a.ParentCode is null)
            {
                Assert.Single(a.Code);
                continue;
            }

            AccountTemplate parent = byCode[a.ParentCode]; // parent exists
            Assert.False(parent.Postable, $"{a.Code}: parent {parent.Code} must be a heading");
            Assert.Equal(parent.Type, a.Type);
            Assert.StartsWith(parent.Code, a.Code, StringComparison.Ordinal);
            Assert.True(chart.ToList().IndexOf(parent) < chart.ToList().IndexOf(a), "parents come first (insert order)");
        }

        Assert.All(chart.Where(a => !a.Postable), h => Assert.Contains(chart, c => c.ParentCode == h.Code));
    }

    [Fact]
    public void Unknown_industry_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ChartOfAccountsTemplate.For("casino"));

    [Fact]
    public void Year_1405_starts_at_Nowruz_midnight_Tehran_and_has_twelve_known_months()
    {
        IReadOnlyList<FiscalPeriodSpan> p = FiscalCalendar.Periods(1405, Tehran);
        Assert.Equal(12, p.Count);
        Assert.Equal(new DateTimeOffset(2026, 3, 20, 20, 30, 0, TimeSpan.Zero), p[0].StartUtc); // 1405-01-01 00:00 +03:30
        Assert.Equal(new DateTimeOffset(2027, 3, 20, 20, 30, 0, TimeSpan.Zero), p[11].EndUtc);  // 1406-01-01
        int[] days = [.. p.Select(x => (int)(x.EndUtc - x.StartUtc).TotalDays)];
        Assert.Equal([31, 31, 31, 31, 31, 31, 30, 30, 30, 30, 30, 29], days);
    }

    [Fact]
    public void Registration_on_22_november_2026_falls_in_fiscal_year_1405() =>
        Assert.Equal(1405, FiscalCalendar.FiscalYearOf(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero), Tehran));

    [Fact]
    public void Just_before_and_after_Nowruz_belong_to_different_years()
    {
        var nowruz1406 = new DateTimeOffset(2027, 3, 20, 20, 30, 0, TimeSpan.Zero);
        Assert.Equal(1405, FiscalCalendar.FiscalYearOf(nowruz1406.AddSeconds(-1), Tehran));
        Assert.Equal(1406, FiscalCalendar.FiscalYearOf(nowruz1406, Tehran));
    }

    [Property(MaxTest = 300)]
    public bool Periods_are_contiguous_cover_the_year_and_are_29_to_31_days(int seed)
    {
        int year = 1350 + (int)((uint)seed % 150);
        IReadOnlyList<FiscalPeriodSpan> p = FiscalCalendar.Periods(year, Tehran);
        bool contiguous = p.Zip(p.Skip(1)).All(x => x.First.EndUtc == x.Second.StartUtc);
        bool lengths = p.All(x => (x.EndUtc - x.StartUtc).TotalHours is >= (29 * 24) - 1 and <= (31 * 24) + 1);
        double total = (p[11].EndUtc - p[0].StartUtc).TotalDays;
        bool yearLength = Math.Abs(total - 365) < 0.1 || Math.Abs(total - 366) < 0.1;
        bool numbering = p.Select(x => x.PeriodNo).SequenceEqual(Enumerable.Range(1, 12)) && p.All(x => x.FiscalYear == year);
        bool chained = FiscalCalendar.Periods(year + 1, Tehran)[0].StartUtc == p[11].EndUtc;
        return contiguous && lengths && yearLength && numbering && chained;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "05-data")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
