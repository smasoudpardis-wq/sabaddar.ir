using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Accounting.Application;

public sealed record JournalLineSpec(Guid AccountId, long Debit, long Credit, string? DetailType, Guid? DetailId, string? Description);

public sealed record JournalLineView(int LineNo, Guid AccountId, string AccountCode, string? DetailType, Guid? DetailId, long Debit, long Credit,
    string? Description);

public sealed record JournalView(Guid Id, string Status, DateTimeOffset EffectiveAt, string? Description, string? SourceType, Guid? SourceId,
    string? Purpose, int? RuleVersion, Guid? ReversalOf, Guid? ReversedBy, IReadOnlyList<JournalLineView> Lines);

public sealed record AccountView(Guid Id, string Code, string Name, string AccountType, Guid? ParentId, bool IsSystem, bool RequiresDetail, bool IsPostable);

public sealed record PeriodView(Guid Id, int FiscalYear, int PeriodNo, DateTimeOffset Start, DateTimeOffset End, string Status);

public enum AccountingOutcome
{
    Done,
    NotFound,
    NoOpenPeriod,
    PeriodClosed,
    NotDraft,
    AlreadyReversed,
    UnknownAccount,
    DuplicateCode,
    UnknownRole,
}

public sealed record AccountingResult<T>(AccountingOutcome Outcome, T? Value, string? Detail = null);

/// <summary>
/// ACC-002 / AT-102: the domain check before the database's deferred one — every line is one-sided and positive, there are
/// at least two lines, and debits equal credits. Amounts are whole rials (BR-010).
/// </summary>
public static class JournalRules
{
    public const int MaxLines = 200;

    public static string? Problem(IReadOnlyList<JournalLineSpec> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count is < 2 or > MaxLines)
        {
            return $"2-{MaxLines} lines";
        }

        if (lines.Any(l => (l.Debit > 0) == (l.Credit > 0) || l.Debit < 0 || l.Credit < 0))
        {
            return "each line is either a positive debit or a positive credit";
        }

        if (lines.Any(l => (l.DetailType is null) != (l.DetailId is null)))
        {
            return "detailType and detailId go together";
        }

        long debit = 0, credit = 0;
        foreach (JournalLineSpec l in lines)
        {
            debit = checked(debit + l.Debit);
            credit = checked(credit + l.Credit);
        }

        return debit == credit ? null : $"unbalanced: debit {debit} ≠ credit {credit}";
    }
}

public interface IJournals
{
    Task<IReadOnlyList<AccountView>> AccountsAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);

    Task<AccountingResult<AccountView>> CreateAccountAsync(IUnitOfWorkScope scope, string code, string name, string accountType, Guid? parentId,
        bool requiresDetail, CancellationToken cancellationToken);

    /// <summary>ACC-004: the tenant points posting-rule roles at its own accounts.</summary>
    Task<AccountingResult<IReadOnlyDictionary<string, Guid>>> SetMappingsAsync(IUnitOfWorkScope scope, IReadOnlyDictionary<string, Guid> mappings,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PeriodView>> PeriodsAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);

    Task<AccountingResult<JournalView>> CreateDraftAsync(IUnitOfWorkScope scope, DateTimeOffset effectiveAt, string? description,
        IReadOnlyList<JournalLineSpec> lines, CancellationToken cancellationToken);

    Task<AccountingResult<JournalView>> PostAsync(IUnitOfWorkScope scope, Guid journalId, CancellationToken cancellationToken);

    /// <summary>ACC-003: a posted entry is corrected only by its mirror image, dated now (in the open period).</summary>
    Task<AccountingResult<JournalView>> ReverseAsync(IUnitOfWorkScope scope, Guid journalId, string reason, CancellationToken cancellationToken);

    Task<JournalView?> GetAsync(IUnitOfWorkScope scope, Guid journalId, CancellationToken cancellationToken);

    /// <summary>Newest first; <paramref name="before"/> is the (effectiveAt, id) keyset of the previous page's last entry.</summary>
    Task<IReadOnlyList<JournalView>> ListAsync(IUnitOfWorkScope scope, DateTimeOffset? fromDate, DateTimeOffset? toDate, string? sourceType,
        (DateTimeOffset EffectiveAt, Guid Id)? before, int limit, CancellationToken cancellationToken);

    /// <summary>ACC-004: an automatic entry built from a posting rule, posted once per (source, purpose) (DI-044).</summary>
    Task<AccountingResult<JournalView>> PostAutomaticAsync(IUnitOfWorkScope scope, PostingDraft draft, CancellationToken cancellationToken);
}
