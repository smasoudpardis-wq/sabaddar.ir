using System.Globalization;
using System.Text;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Payments.Application;

/// <summary>One row of the bank's settlement file (PAY-002.2): a terminal transaction, its gross amount and the bank's fee (rials).</summary>
public sealed record SettlementLine(int LineNo, string? TerminalNo, string ReferenceNo, DateOnly TransactionDate, long Amount, long Fee);

/// <summary>A row the file could not be read at: its line number in the file (1 = the header), the column and why.</summary>
public sealed record SettlementRowError(int Line, string Field, string Message);

/// <summary>A standalone terminal payment that a settlement line may settle.</summary>
public sealed record SettlementCandidate(Guid PaymentId, string ReferenceNo, string? TerminalNo, DateOnly BusinessDate, long Amount, Guid? SaleId, bool Settled);

public enum SettlementOutcome
{
    /// <summary>One payment with this reference and amount, on a finalized sale: it is settled now (J4).</summary>
    Matched,

    /// <summary>The reference is known but no open payment has this amount (a typo at the till, or a partial settlement).</summary>
    AmountMismatch,

    /// <summary>No payment carries this reference near this date: the slip was never recorded, or was recorded wrongly (AB-14).</summary>
    NotFound,

    /// <summary>More than one open payment fits; the accountant settles it by hand.</summary>
    Ambiguous,

    /// <summary>An earlier file already settled the payment.</summary>
    AlreadySettled,

    /// <summary>An earlier line of this same file already settled the payment.</summary>
    DuplicateInFile,

    /// <summary>The payment's bill is not finalized yet, so nothing is in CardInTransit for it; import the file again later.</summary>
    Unfinalized,
}

public sealed record SettlementMatch(SettlementLine Line, SettlementOutcome Outcome, Guid? PaymentId);

/// <summary>D07 <c>SettlementImport</c>: a file that was imported, what matched, and the J4 entry it posted.</summary>
public sealed record SettlementImportView(Guid Id, string FileName, int LineCount, int MatchedCount, long MatchedAmount, long Fees, Guid? JournalEntryId,
    Guid ImportedBy, DateTimeOffset ImportedAt, IReadOnlyList<SettlementMatch>? Lines = null);

/// <summary>A ConfirmedManual payment no settlement has matched yet (the «پرداخت دستی بدون تطبیق» report, AB-14).</summary>
public sealed record UnreconciledPayment(Guid PaymentId, Guid? SaleId, string ReferenceNo, string? TerminalNo, string? CardLast4, long Amount, DateOnly BusinessDate,
    Guid RecordedBy, Guid? RegisterId, DateTimeOffset RecordedAt);

public interface ISettlements
{
    /// <summary>Imports of one store run one at a time (a payment settles once; the same file twice is refused).</summary>
    Task LockAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);

    /// <summary>The import that brought the same file before, if any (the file's SHA-256).</summary>
    Task<Guid?> FindByFileAsync(IUnitOfWorkScope scope, byte[] sha256, CancellationToken cancellationToken);

    /// <summary>Every standalone terminal payment (not voided) carrying one of these references, with whether a file settled it.</summary>
    Task<IReadOnlyList<SettlementCandidate>> CandidatesAsync(IUnitOfWorkScope scope, IReadOnlyCollection<string> referenceNos, CancellationToken cancellationToken);

    Task<SettlementImportView> RecordAsync(IUnitOfWorkScope scope, Guid id, string fileName, byte[] sha256, IReadOnlyList<SettlementMatch> lines, Guid? journalEntryId,
        Guid importedBy, CancellationToken cancellationToken);

    Task<SettlementImportView?> GetAsync(IUnitOfWorkScope scope, Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SettlementImportView>> RecentAsync(IUnitOfWorkScope scope, int limit, CancellationToken cancellationToken);

    /// <summary>ConfirmedManual payments from <paramref name="fromDate"/> to <paramref name="toDate"/> (business dates) that no file has settled, oldest first (at most <paramref name="limit"/>).</summary>
    Task<IReadOnlyList<UnreconciledPayment>> UnreconciledAsync(IUnitOfWorkScope scope, DateOnly fromDate, DateOnly toDate, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// PAY-002.2 MVP: the bank's settlement report as CSV (comma, semicolon or tab). The first row names the columns, in English
/// or Persian; reference, date and amount are required, terminal and fee optional. Persian digits and thousands separators are
/// accepted; a date is ISO (2026-11-24) or Jalali (1405/09/03). Amounts are rials (RQ-16).
/// </summary>
public static class SettlementCsv
{
    public const int MaxBytes = 1_048_576;
    public const int MaxLines = 5000;
    public const int MaxErrors = 20;

    private static readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["terminal_no"] = "terminal", ["terminal"] = "terminal", ["terminalno"] = "terminal", ["ترمینال"] = "terminal", ["پایانه"] = "terminal",
        ["شماره پایانه"] = "terminal", ["شماره ترمینال"] = "terminal",
        ["reference_no"] = "reference", ["reference"] = "reference", ["referenceno"] = "reference", ["rrn"] = "reference", ["مرجع"] = "reference",
        ["شماره مرجع"] = "reference", ["شماره پیگیری"] = "reference", ["پیگیری"] = "reference",
        ["transaction_date"] = "date", ["date"] = "date", ["تاریخ"] = "date", ["تاریخ تراکنش"] = "date",
        ["amount"] = "amount", ["مبلغ"] = "amount", ["مبلغ تراکنش"] = "amount",
        ["fee"] = "fee", ["کارمزد"] = "fee",
    };

    public static (IReadOnlyList<SettlementLine> Lines, IReadOnlyList<SettlementRowError> Errors) Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var errors = new List<SettlementRowError>();
        var lines = new List<SettlementLine>();
        string[] rows = content.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        int headerAt = Array.FindIndex(rows, r => r.Trim().Length > 0);
        if (headerAt < 0)
        {
            return ([], [new SettlementRowError(1, "file", "empty")]);
        }

        char delimiter = rows[headerAt].Contains('\t', StringComparison.Ordinal) ? '\t' : rows[headerAt].Contains(';', StringComparison.Ordinal) ? ';' : ',';
        var columns = new Dictionary<string, int>();
        List<string> header = Split(rows[headerAt], delimiter);
        for (int i = 0; i < header.Count; i++)
        {
            string name = string.Join(' ', header[i].Replace('ي', 'ی').Replace('ك', 'ک').Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (Headers.TryGetValue(name, out string? field) && !columns.ContainsKey(field))
            {
                columns[field] = i;
            }
        }

        foreach (string required in new[] { "reference", "date", "amount" })
        {
            if (!columns.ContainsKey(required))
            {
                errors.Add(new SettlementRowError(headerAt + 1, required, "missing-column"));
            }
        }

        if (errors.Count > 0)
        {
            return ([], errors);
        }

        for (int n = headerAt + 1; n < rows.Length && errors.Count < MaxErrors; n++)
        {
            if (rows[n].Trim().Length == 0)
            {
                continue;
            }

            if (lines.Count + errors.Count >= MaxLines)
            {
                errors.Add(new SettlementRowError(n + 1, "file", $"more than {MaxLines} rows"));
                break;
            }

            List<string> cells = Split(rows[n], delimiter);
            string Cell(string f) => columns.TryGetValue(f, out int i) && i < cells.Count ? Digits(cells[i]).Trim() : "";
            int before = errors.Count;
            string reference = Cell("reference");
            if (!PaymentRules.ValidReference(reference))
            {
                errors.Add(new SettlementRowError(n + 1, "reference", "4-32 letters or digits"));
            }

            string terminalCell = Cell("terminal");
            string? terminal = terminalCell.Length == 0 ? null : terminalCell;
            if (!PaymentRules.ValidTerminalNo(terminal))
            {
                errors.Add(new SettlementRowError(n + 1, "terminal", "1-16 letters or digits"));
            }

            if (!TryDate(Cell("date"), out DateOnly date))
            {
                errors.Add(new SettlementRowError(n + 1, "date", "yyyy-MM-dd or a Jalali yyyy/MM/dd"));
            }

            if (!TryRial(Cell("amount"), out long amount) || amount <= 0)
            {
                errors.Add(new SettlementRowError(n + 1, "amount", "a positive whole number of rials"));
            }

            string feeCell = Cell("fee");
            long fee = 0;
            if (feeCell.Length > 0 && (!TryRial(feeCell, out fee) || fee < 0 || fee > amount))
            {
                errors.Add(new SettlementRowError(n + 1, "fee", "a whole number of rials, at most the amount"));
            }

            if (errors.Count == before)
            {
                lines.Add(new SettlementLine(lines.Count + 1, terminal, reference.ToUpperInvariant(), date, amount, fee));
            }
        }

        if (errors.Count == 0 && lines.Count == 0)
        {
            errors.Add(new SettlementRowError(headerAt + 2, "file", "no rows"));
        }

        return (errors.Count > 0 ? [] : lines, errors);
    }

    /// <summary>Persian (۰-۹) and Arabic-Indic (٠-٩) digits as ASCII.</summary>
    public static string Digits(string v)
    {
        ArgumentNullException.ThrowIfNull(v);
        var sb = new StringBuilder(v.Length);
        foreach (char c in v)
        {
            sb.Append(c switch
            {
                >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                _ => c,
            });
        }

        return sb.ToString();
    }

    public static bool TryDate(string v, out DateOnly date)
    {
        date = default;
        string[] parts = Digits(v ?? "").Trim().Split('-', '/');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int y)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int m) || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int d))
        {
            return false;
        }

        try
        {
            date = y < 1700 ? DateOnly.FromDateTime(new PersianCalendar().ToDateTime(y, m, d, 0, 0, 0, 0)) : new DateOnly(y, m, d);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public static bool TryRial(string v, out long amount)
    {
        string s = Digits(v ?? "").Replace(",", "", StringComparison.Ordinal).Replace("٬", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out amount) && s.Length <= 15;
    }

    private static List<string> Split(string row, char delimiter)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < row.Length; i++)
        {
            char c = row[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < row.Length && row[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }
}

/// <summary>
/// PAY-002.2: a settlement line settles the one open payment with its reference (the same terminal when both name one, a
/// business date within a day of the transaction) and its amount. Anything else is left for the accountant, with why.
/// </summary>
public static class SettlementMatcher
{
    public const int DateWindowDays = 1;

    public static IReadOnlyList<SettlementMatch> Match(IReadOnlyList<SettlementLine> lines, IReadOnlyList<SettlementCandidate> candidates,
        IReadOnlySet<Guid> finalizedSales)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(finalizedSales);
        var taken = new HashSet<Guid>();
        var result = new List<SettlementMatch>(lines.Count);
        foreach (SettlementLine line in lines)
        {
            SettlementCandidate[] fits = [.. candidates.Where(c => string.Equals(c.ReferenceNo, line.ReferenceNo, StringComparison.OrdinalIgnoreCase)
                && (line.TerminalNo is null || c.TerminalNo is null || string.Equals(c.TerminalNo, line.TerminalNo, StringComparison.OrdinalIgnoreCase))
                && Math.Abs(c.BusinessDate.DayNumber - line.TransactionDate.DayNumber) <= DateWindowDays)];
            SettlementCandidate[] open = [.. fits.Where(c => !c.Settled && !taken.Contains(c.PaymentId))];
            SettlementCandidate[] same = [.. open.Where(c => c.Amount == line.Amount)];
            if (same.Length > 1)
            {
                SettlementCandidate[] sameDay = [.. same.Where(c => c.BusinessDate == line.TransactionDate)];
                same = sameDay.Length == 1 ? sameDay : same;
            }

            SettlementMatch match = (fits.Length, open.Length, same.Length) switch
            {
                (0, _, _) => new(line, SettlementOutcome.NotFound, null),
                (_, 0, _) => new(line, fits.Any(c => taken.Contains(c.PaymentId)) ? SettlementOutcome.DuplicateInFile : SettlementOutcome.AlreadySettled, null),
                (_, _, 0) => new(line, SettlementOutcome.AmountMismatch, open.Length == 1 ? open[0].PaymentId : null),
                (_, _, > 1) => new(line, SettlementOutcome.Ambiguous, null),
                _ when same[0].SaleId is not { } sale || !finalizedSales.Contains(sale) => new(line, SettlementOutcome.Unfinalized, same[0].PaymentId),
                _ => new(line, SettlementOutcome.Matched, same[0].PaymentId),
            };
            if (match.Outcome == SettlementOutcome.Matched)
            {
                taken.Add(match.PaymentId!.Value);
            }

            result.Add(match);
        }

        return result;
    }

    /// <summary>What the matched lines move out of CardInTransit (gross) and what of it the bank kept (fees).</summary>
    public static (int Count, long Gross, long Fees) Totals(IReadOnlyList<SettlementMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        SettlementMatch[] m = [.. matches.Where(x => x.Outcome == SettlementOutcome.Matched)];
        return (m.Length, m.Sum(x => x.Line.Amount), m.Sum(x => x.Line.Fee));
    }
}
