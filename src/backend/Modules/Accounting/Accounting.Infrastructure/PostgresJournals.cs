using Npgsql;
using NpgsqlTypes;
using RetailCloud.Accounting.Application;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;

namespace RetailCloud.Accounting.Infrastructure;

/// <summary>
/// ACC-001..004 on acc.* under the tenant's RLS context. The database keeps entries balanced at commit (DI-040), posted
/// entries immutable (DI-041), posting only into open periods (DI-042), one automatic entry per source and purpose (DI-044)
/// and one reversal per entry (DI-045); this class checks the same rules first so callers get D07 errors, not commit failures.
/// </summary>
public sealed class PostgresJournals : IJournals
{
    public async Task<IReadOnlyList<AccountView>> AccountsAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT id, code, name, account_type, parent_id, is_system, requires_detail, is_postable FROM acc.account ORDER BY code");
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<AccountView>();
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new AccountView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetGuid(4),
                r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7)));
        }

        return list;
    }

    public async Task<AccountingResult<AccountView>> CreateAccountAsync(IUnitOfWorkScope scope, string code, string name, string accountType, Guid? parentId,
        bool requiresDetail, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        if (await ScalarAsync<bool>(s, "SELECT EXISTS (SELECT 1 FROM acc.account WHERE code = $1)", cancellationToken, code))
        {
            return new(AccountingOutcome.DuplicateCode, null);
        }

        if (parentId is { } parent && !await ScalarAsync<bool>(s, "SELECT EXISTS (SELECT 1 FROM acc.account WHERE id = $1 AND account_type = $2)",
                cancellationToken, parent, accountType))
        {
            return new(AccountingOutcome.UnknownAccount, null, "parentId: an account of the same type");
        }

        Guid id = Guid.CreateVersion7();
        await ExecAsync(s, "INSERT INTO acc.account (tenant_id, id, code, name, account_type, parent_id, requires_detail) " +
                           "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6)", cancellationToken,
            id, code, name.Trim(), accountType, (object?)parentId ?? DBNull.Value, requiresDetail);
        return new(AccountingOutcome.Done, (await AccountsAsync(scope, cancellationToken)).Single(a => a.Id == id));
    }

    public async Task<AccountingResult<IReadOnlyDictionary<string, Guid>>> SetMappingsAsync(IUnitOfWorkScope scope, IReadOnlyDictionary<string, Guid> mappings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var s = (PostgresUnitOfWorkScope)scope;
        foreach ((string role, Guid account) in mappings)
        {
            if (!ChartOfAccountsTemplate.Roles.Contains(role))
            {
                return new(AccountingOutcome.UnknownRole, null, role);
            }

            if (!await ScalarAsync<bool>(s, "SELECT EXISTS (SELECT 1 FROM acc.account WHERE id = $1 AND is_postable)", cancellationToken, account))
            {
                return new(AccountingOutcome.UnknownAccount, null, role);
            }

            await ExecAsync(s, "INSERT INTO acc.account_role (tenant_id, role, account_id) VALUES (core.current_tenant(), $1, $2) " +
                               "ON CONFLICT (tenant_id, role) DO UPDATE SET account_id = EXCLUDED.account_id", cancellationToken, role, account);
        }

        var all = new Dictionary<string, Guid>(StringComparer.Ordinal);
        await using NpgsqlCommand cmd = s.Command("SELECT role, account_id FROM acc.account_role ORDER BY role");
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            all[r.GetString(0)] = r.GetGuid(1);
        }

        return new(AccountingOutcome.Done, all);
    }

    public async Task<IReadOnlyList<PeriodView>> PeriodsAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT id, fiscal_year, period_no, lower(span), upper(span), status FROM acc.fiscal_period ORDER BY lower(span)");
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        var list = new List<PeriodView>();
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new PeriodView(r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4), r.GetString(5)));
        }

        return list;
    }

    public async Task<AccountingResult<JournalView>> CreateDraftAsync(IUnitOfWorkScope scope, DateTimeOffset effectiveAt, string? description,
        IReadOnlyList<JournalLineSpec> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var s = (PostgresUnitOfWorkScope)scope;
        if (await LinesProblemAsync(s, lines, cancellationToken) is { } problem)
        {
            return new(AccountingOutcome.UnknownAccount, null, problem);
        }

        if (await PeriodOfAsync(s, effectiveAt, cancellationToken) is not { } period)
        {
            return new(AccountingOutcome.NoOpenPeriod, null);
        }

        Guid id = await InsertAsync(s, period.Id, effectiveAt, description, null, null, lines, cancellationToken);
        return new(AccountingOutcome.Done, await GetAsync(scope, id, cancellationToken));
    }

    public async Task<AccountingResult<JournalView>> PostAsync(IUnitOfWorkScope scope, Guid journalId, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        if (await GetAsync(scope, journalId, cancellationToken) is not { } journal)
        {
            return new(AccountingOutcome.NotFound, null);
        }

        if (journal.Status != "Draft")
        {
            return new(AccountingOutcome.NotDraft, null);
        }

        if (await PeriodOfAsync(s, journal.EffectiveAt, cancellationToken) is not { Status: "Open" })
        {
            return new(AccountingOutcome.PeriodClosed, null);
        }

        await ExecAsync(s, "UPDATE acc.journal_entry SET status = 'Posted', posted_at = now() WHERE id = $1 AND status = 'Draft'", cancellationToken, journalId);
        return new(AccountingOutcome.Done, await GetAsync(scope, journalId, cancellationToken));
    }

    public async Task<AccountingResult<JournalView>> ReverseAsync(IUnitOfWorkScope scope, Guid journalId, string reason, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await ExecAsync(s, "SELECT 1 FROM acc.journal_entry WHERE id = $1 FOR UPDATE", cancellationToken, journalId);
        if (await GetAsync(scope, journalId, cancellationToken) is not { } original)
        {
            return new(AccountingOutcome.NotFound, null);
        }

        if (original.Status != "Posted")
        {
            return new(original.Status == "Reversed" ? AccountingOutcome.AlreadyReversed : AccountingOutcome.NotDraft, null);
        }

        // ACC-009: the reversal is dated now, in the period that is open now
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (await PeriodOfAsync(s, now, cancellationToken) is not { Status: "Open" } period)
        {
            return new(AccountingOutcome.PeriodClosed, null);
        }

        JournalLineSpec[] mirror = [.. original.Lines.Select(l => new JournalLineSpec(l.AccountId, l.Credit, l.Debit, l.DetailType, l.DetailId, l.Description))];
        Guid id = await InsertAsync(s, period.Id, now, $"برگشت سند: {reason}", null, journalId, mirror, cancellationToken);
        await ExecAsync(s, "UPDATE acc.journal_entry SET status = 'Posted', posted_at = now() WHERE id = $1", cancellationToken, id);
        await ExecAsync(s, "UPDATE acc.journal_entry SET status = 'Reversed', reversed_by = $2 WHERE id = $1", cancellationToken, journalId, id);
        return new(AccountingOutcome.Done, await GetAsync(scope, id, cancellationToken));
    }

    public async Task<AccountingResult<JournalView>> PostAutomaticAsync(IUnitOfWorkScope scope, PostingDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var s = (PostgresUnitOfWorkScope)scope;
        if (await ScalarAsync<Guid?>(s, "SELECT id FROM acc.journal_entry WHERE source_type = $1 AND source_id = $2 AND purpose = $3", cancellationToken,
                draft.SourceType, draft.SourceId, draft.Purpose) is { } existing)
        {
            return new(AccountingOutcome.Done, await GetAsync(scope, existing, cancellationToken));   // already posted (redelivery)
        }

        var lines = new List<JournalLineSpec>();
        foreach (RoleLine line in draft.Lines)
        {
            if (await ScalarAsync<Guid?>(s, "SELECT account_id FROM acc.account_role WHERE role = $1", cancellationToken, line.Role) is not { } account)
            {
                return new(AccountingOutcome.UnknownRole, null, line.Role);
            }

            lines.Add(new JournalLineSpec(account, line.Debit, line.Credit, null, null, null));
        }

        if (JournalRules.Problem(lines) is { } problem)
        {
            throw new InvalidOperationException($"posting rule {draft.Purpose} v{draft.RuleVersion} built an invalid entry: {problem}");
        }

        // ACC-009: an event whose period is already closed is posted today, in the open period, referring to its source
        DateTimeOffset effective = draft.EffectiveAt;
        if (await PeriodOfAsync(s, effective, cancellationToken) is not { Status: "Open" })
        {
            effective = DateTimeOffset.UtcNow;
        }

        if (await PeriodOfAsync(s, effective, cancellationToken) is not { Status: "Open" } period)
        {
            return new(AccountingOutcome.NoOpenPeriod, null);
        }

        Guid id = await InsertAsync(s, period.Id, effective, draft.Description, (draft.SourceType, draft.SourceId, draft.Purpose, draft.RuleVersion),
            null, lines, cancellationToken);
        await ExecAsync(s, "UPDATE acc.journal_entry SET status = 'Posted', posted_at = now() WHERE id = $1", cancellationToken, id);
        return new(AccountingOutcome.Done, await GetAsync(scope, id, cancellationToken));
    }

    public async Task<JournalView?> GetAsync(IUnitOfWorkScope scope, Guid journalId, CancellationToken cancellationToken) =>
        (await LoadAsync((PostgresUnitOfWorkScope)scope, "e.id = $1", [journalId], 1, cancellationToken)).SingleOrDefault();

    public Task<IReadOnlyList<JournalView>> ListAsync(IUnitOfWorkScope scope, DateTimeOffset? fromDate, DateTimeOffset? toDate, string? sourceType,
        (DateTimeOffset EffectiveAt, Guid Id)? before, int limit, CancellationToken cancellationToken)
    {
        var where = new List<string> { "true" };
        var args = new List<object>();
        void Add(string condition, params object[] values)
        {
            int at = args.Count;
            args.AddRange(values);
            where.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, condition, [.. values.Select((_, i) => "$" + (at + i + 1))]));
        }

        if (fromDate is { } f) Add("e.effective_at >= {0}", f);
        if (toDate is { } t) Add("e.effective_at < {0}", t);
        if (sourceType is not null) Add("e.source_type = {0}", sourceType);
        if (before is { } b) Add("(e.effective_at, e.id) < ({0}, {1})", b.EffectiveAt, b.Id);
        return LoadAsync((PostgresUnitOfWorkScope)scope, string.Join(" AND ", where), [.. args], limit, cancellationToken);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    /// <summary>Accounts exist and are postable; detail-required accounts carry a detail (DI-043).</summary>
    private static async Task<string?> LinesProblemAsync(PostgresUnitOfWorkScope s, IReadOnlyList<JournalLineSpec> lines, CancellationToken ct)
    {
        var accounts = new Dictionary<Guid, (bool Postable, bool RequiresDetail)>();
        await using (NpgsqlCommand cmd = s.Command("SELECT id, is_postable, requires_detail FROM acc.account WHERE id = ANY($1)"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = lines.Select(l => l.AccountId).Distinct().ToArray() });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                accounts[r.GetGuid(0)] = (r.GetBoolean(1), r.GetBoolean(2));
            }
        }

        for (int i = 0; i < lines.Count; i++)
        {
            if (!accounts.TryGetValue(lines[i].AccountId, out var a) || !a.Postable)
            {
                return $"lines[{i}].accountId: not a postable account of this store";
            }

            if (a.RequiresDetail && lines[i].DetailId is null)
            {
                return $"lines[{i}].detailId: this account requires a detail (customer, supplier or branch)";
            }
        }

        return null;
    }

    private static async Task<(Guid Id, string Status)?> PeriodOfAsync(PostgresUnitOfWorkScope s, DateTimeOffset at, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = s.Command("SELECT id, status FROM acc.fiscal_period WHERE span @> $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = at });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetGuid(0), r.GetString(1)) : null;
    }

    private static async Task<Guid> InsertAsync(PostgresUnitOfWorkScope s, Guid periodId, DateTimeOffset effectiveAt, string? description,
        (string Type, Guid Id, string Purpose, int Version)? source, Guid? reversalOf, IReadOnlyList<JournalLineSpec> lines, CancellationToken ct)
    {
        Guid id = Guid.CreateVersion7();
        await ExecAsync(s, "INSERT INTO acc.journal_entry (tenant_id, id, status, effective_at, period_id, description, source_type, source_id, purpose, rule_version, reversal_of) " +
                           "VALUES (core.current_tenant(), $1, 'Draft', $2, $3, $4, $5, $6, $7, $8, $9)", ct,
            id, effectiveAt, periodId, (object?)description ?? DBNull.Value, (object?)source?.Type ?? DBNull.Value, (object?)source?.Id ?? DBNull.Value,
            (object?)source?.Purpose ?? DBNull.Value, (object?)source?.Version ?? DBNull.Value, (object?)reversalOf ?? DBNull.Value);
        for (int i = 0; i < lines.Count; i++)
        {
            JournalLineSpec l = lines[i];
            await ExecAsync(s, "INSERT INTO acc.journal_line (tenant_id, entry_id, line_no, account_id, detail_type, detail_id, debit, credit, description) " +
                               "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7, $8)", ct,
                id, i + 1, l.AccountId, (object?)l.DetailType ?? DBNull.Value, (object?)l.DetailId ?? DBNull.Value, l.Debit, l.Credit,
                (object?)l.Description ?? DBNull.Value);
        }

        return id;
    }

    private static async Task<IReadOnlyList<JournalView>> LoadAsync(PostgresUnitOfWorkScope s, string where, object[] args, int limit, CancellationToken ct)
    {
        var entries = new List<JournalView>();
        await using (NpgsqlCommand cmd = s.Command(
                         "SELECT e.id, e.status, e.effective_at, e.description, e.source_type, e.source_id, e.purpose, e.rule_version, e.reversal_of, e.reversed_by " +
                         $"FROM acc.journal_entry e WHERE {where} ORDER BY e.effective_at DESC, e.id DESC LIMIT {limit}"))
        {
            foreach (object a in args)
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = a });
            }

            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                Guid? G(int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                entries.Add(new JournalView(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), S(3), S(4), G(5), S(6),
                    r.IsDBNull(7) ? null : r.GetInt32(7), G(8), G(9), []));
            }
        }

        if (entries.Count == 0)
        {
            return entries;
        }

        var lines = new List<(Guid Entry, JournalLineView Line)>();
        await using (NpgsqlCommand cmd = s.Command(
                         "SELECT l.entry_id, l.line_no, l.account_id, a.code, l.detail_type, l.detail_id, l.debit, l.credit, l.description " +
                         "FROM acc.journal_line l JOIN acc.account a ON a.tenant_id = l.tenant_id AND a.id = l.account_id WHERE l.entry_id = ANY($1) ORDER BY l.line_no"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = entries.Select(e => e.Id).ToArray() });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                lines.Add((r.GetGuid(0), new JournalLineView(r.GetInt32(1), r.GetGuid(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetGuid(5), r.GetInt64(6), r.GetInt64(7), r.IsDBNull(8) ? null : r.GetString(8))));
            }
        }

        return [.. entries.Select(e => e with { Lines = [.. lines.Where(l => l.Entry == e.Id).Select(l => l.Line)] })];
    }

    private static async Task<T?> ScalarAsync<T>(PostgresUnitOfWorkScope s, string sql, CancellationToken ct, params object[] args)
    {
        await using NpgsqlCommand cmd = s.Command(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        object? v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? default : (T)v;
    }

    private static async Task ExecAsync(PostgresUnitOfWorkScope s, string sql, CancellationToken ct, params object[] args)
    {
        await using NpgsqlCommand cmd = s.Command(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>ACC-004 / UC-06: the cash variance of a closed shift is posted by <see cref="CashVarianceRule"/> (once per shift).</summary>
public sealed class ShiftCashVarianceConsumer(IJournals journals) : IIntegrationEventConsumer
{
    public string Consumer => "accounting.shift-cash-variance";

    public bool CanHandle(string eventType, int eventVersion) => eventType == "sales.shift-closed" && eventVersion == 1;

    public async Task HandleAsync(IUnitOfWorkScope scope, IntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        System.Text.Json.JsonElement p = integrationEvent.Payload;
        if (CashVarianceRule.For(p.GetProperty("shiftId").GetGuid(), p.GetProperty("variance").GetInt64(), p.GetProperty("closedAt").GetDateTimeOffset()) is not { } draft)
        {
            return;
        }

        AccountingResult<JournalView> posted = await journals.PostAutomaticAsync(scope, draft, cancellationToken);
        if (posted.Outcome != AccountingOutcome.Done)
        {
            // a missing role mapping or no open period: fail the delivery so the relay retries and finally dead-letters it (visible, D11)
            throw new InvalidOperationException($"cash variance of shift {draft.SourceId} not posted: {posted.Outcome} {posted.Detail}");
        }
    }
}
