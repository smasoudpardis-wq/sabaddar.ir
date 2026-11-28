using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Accounting.Application;

/// <summary>What one postable account carried before a range (opening) and within it, over posted entries (ACC-007).</summary>
public sealed record AccountMovement(Guid AccountId, long OpeningDebit, long OpeningCredit, long PeriodDebit, long PeriodCredit);

/// <summary>
/// One row of the trial balance: an account at its depth in the chart. Opening and closing are shown net on one side;
/// the range's movement is gross on both (4-column: period debit/credit and closing; 2-column: closing only).
/// </summary>
public sealed record TrialBalanceLine(AccountView Account, int Level, long OpeningDebit, long OpeningCredit, long PeriodDebit, long PeriodCredit,
    long ClosingDebit, long ClosingCredit);

public sealed record TrialBalanceTotals(long OpeningDebit, long OpeningCredit, long PeriodDebit, long PeriodCredit, long ClosingDebit, long ClosingCredit)
{
    /// <summary>AT-107: a double-entry ledger is always balanced, on every column pair.</summary>
    public bool Balanced => OpeningDebit == OpeningCredit && PeriodDebit == PeriodCredit && ClosingDebit == ClosingCredit;
}

/// <summary>A posted line on the ledger of an account (or a group of accounts), with the balance after it (debit − credit).</summary>
public sealed record LedgerLine(Guid JournalId, DateTimeOffset EffectiveAt, string? Description, string? SourceType, Guid AccountId, string AccountCode,
    string? DetailType, Guid? DetailId, long Debit, long Credit, long RunningBalance);

public sealed record LedgerPage(long Opening, long PeriodDebit, long PeriodCredit, IReadOnlyList<LedgerLine> Lines, bool HasMore)
{
    public long Closing => Opening + PeriodDebit - PeriodCredit;
}

public interface IAccountingReports
{
    /// <summary>Per postable account with any posted line up to <paramref name="toDate"/>: sums before <paramref name="fromDate"/> and in [from, to).</summary>
    Task<IReadOnlyList<AccountMovement>> MovementsAsync(IUnitOfWorkScope scope, DateTimeOffset fromDate, DateTimeOffset toDate, CancellationToken cancellationToken);

    /// <summary>
    /// The posted lines of these accounts (and only of <paramref name="detailId"/> when given) in [from, to), oldest first, from
    /// <paramref name="offset"/>; the opening is debit − credit before <paramref name="fromDate"/>.
    /// </summary>
    Task<LedgerPage> LedgerAsync(IUnitOfWorkScope scope, IReadOnlyCollection<Guid> accountIds, Guid? detailId, DateTimeOffset fromDate, DateTimeOffset toDate,
        int offset, int limit, CancellationToken cancellationToken);
}

/// <summary>ACC-007: the trial balance built from the chart and each postable account's movement; groups sum their descendants.</summary>
public static class TrialBalance
{
    public static (IReadOnlyList<TrialBalanceLine> Lines, TrialBalanceTotals Totals) Build(IReadOnlyList<AccountView> accounts,
        IReadOnlyList<AccountMovement> movements, bool includeEmpty)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(movements);
        Dictionary<Guid, AccountMovement> own = movements.ToDictionary(m => m.AccountId);
        ILookup<Guid?, AccountView> children = accounts.ToLookup(a => a.ParentId);
        var known = accounts.Select(a => a.Id).ToHashSet();
        var lines = new List<TrialBalanceLine>();

        (long Od, long Oc, long Pd, long Pc) Walk(AccountView a, int level)
        {
            int at = lines.Count;
            lines.Add(null!);
            own.TryGetValue(a.Id, out AccountMovement? m);
            (long od, long oc, long pd, long pc) = (m?.OpeningDebit ?? 0, m?.OpeningCredit ?? 0, m?.PeriodDebit ?? 0, m?.PeriodCredit ?? 0);
            foreach (AccountView c in children[a.Id].OrderBy(c => c.Code, StringComparer.Ordinal))
            {
                (long cod, long coc, long cpd, long cpc) = Walk(c, level + 1);
                (od, oc, pd, pc) = (od + cod, oc + coc, pd + cpd, pc + cpc);
            }

            long opening = od - oc, closing = opening + pd - pc;
            lines[at] = new TrialBalanceLine(a, level, Math.Max(opening, 0), Math.Max(-opening, 0), pd, pc, Math.Max(closing, 0), Math.Max(-closing, 0));
            return (od, oc, pd, pc);
        }

        foreach (AccountView root in accounts.Where(a => a.ParentId is not { } p || !known.Contains(p)).OrderBy(a => a.Code, StringComparer.Ordinal))
        {
            Walk(root, 0);
        }

        // totals over the postable accounts only (a group is the sum of its children: counting both would double it)
        TrialBalanceLine[] leaves = [.. lines.Where(l => own.ContainsKey(l.Account.Id))];
        var totals = new TrialBalanceTotals(leaves.Sum(l => l.OpeningDebit), leaves.Sum(l => l.OpeningCredit), leaves.Sum(l => l.PeriodDebit),
            leaves.Sum(l => l.PeriodCredit), leaves.Sum(l => l.ClosingDebit), leaves.Sum(l => l.ClosingCredit));
        IReadOnlyList<TrialBalanceLine> shown = includeEmpty
            ? lines
            : [.. lines.Where(l => l.OpeningDebit + l.OpeningCredit + l.PeriodDebit + l.PeriodCredit > 0)];
        return (shown, totals);
    }

    /// <summary>The account and every account under it (a ledger of a group shows all its accounts' lines).</summary>
    public static IReadOnlyList<Guid> WithDescendants(IReadOnlyList<AccountView> accounts, Guid accountId)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ILookup<Guid?, AccountView> children = accounts.ToLookup(a => a.ParentId);
        var ids = new List<Guid>();
        var todo = new Stack<Guid>([accountId]);
        while (todo.Count > 0)
        {
            Guid id = todo.Pop();
            ids.Add(id);
            foreach (AccountView c in children[id])
            {
                todo.Push(c.Id);
            }
        }

        return ids;
    }
}
