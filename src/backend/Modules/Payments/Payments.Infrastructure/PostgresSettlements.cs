using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Payments.Application;

namespace RetailCloud.Payments.Infrastructure;

/// <summary>PAY-002.2 on pay.settlement_import / pay.settlement_line (append-only; a payment settles once: ux_settlement_payment).</summary>
public sealed class PostgresSettlements : ISettlements
{
    private const string ImportColumns = "id, file_name, line_count, matched_count, matched_amount, fees, journal_entry_id, imported_by, imported_at";

    public async Task LockAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("SELECT pg_advisory_xact_lock(hashtext('pay.settlement:' || core.current_tenant()::text))");
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Guid?> FindByFileAsync(IUnitOfWorkScope scope, byte[] sha256, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("SELECT id FROM pay.settlement_import WHERE file_sha256 = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = sha256, NpgsqlDbType = NpgsqlDbType.Bytea });
        return (Guid?)await cmd.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SettlementCandidate>> CandidatesAsync(IUnitOfWorkScope scope, IReadOnlyCollection<string> referenceNos,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(referenceNos);
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT p.id, p.reference_no, p.terminal_no, p.business_date, p.amount, p.sale_id, " +
            "EXISTS (SELECT 1 FROM pay.settlement_line l WHERE l.payment_id = p.id AND l.outcome = 'Matched') " +
            "FROM pay.payment p WHERE p.method = 'ExternalCardTerminal' AND p.status = 'ConfirmedManual' AND upper(p.reference_no) = ANY($1) ORDER BY p.recorded_at, p.id");
        cmd.Parameters.Add(new NpgsqlParameter { Value = referenceNos.Select(r => r.ToUpperInvariant()).Distinct().ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        var list = new List<SettlementCandidate>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new SettlementCandidate(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetFieldValue<DateOnly>(3), r.GetInt64(4),
                r.IsDBNull(5) ? null : r.GetGuid(5), r.GetBoolean(6)));
        }

        return list;
    }

    public async Task<SettlementImportView> RecordAsync(IUnitOfWorkScope scope, Guid id, string fileName, byte[] sha256, IReadOnlyList<SettlementMatch> lines,
        Guid? journalEntryId, Guid importedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var s = (PostgresUnitOfWorkScope)scope;
        (int count, long gross, long fees) = SettlementMatcher.Totals(lines);
        await using (NpgsqlCommand cmd = s.Command(
            "INSERT INTO pay.settlement_import (tenant_id, id, file_name, file_sha256, line_count, matched_count, matched_amount, fees, journal_entry_id, imported_by) " +
            "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7, $8, $9)"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = id });
            cmd.Parameters.Add(new NpgsqlParameter { Value = fileName });
            cmd.Parameters.Add(new NpgsqlParameter { Value = sha256, NpgsqlDbType = NpgsqlDbType.Bytea });
            cmd.Parameters.Add(new NpgsqlParameter { Value = lines.Count });
            cmd.Parameters.Add(new NpgsqlParameter { Value = count });
            cmd.Parameters.Add(new NpgsqlParameter { Value = gross });
            cmd.Parameters.Add(new NpgsqlParameter { Value = fees });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)journalEntryId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            cmd.Parameters.Add(new NpgsqlParameter { Value = importedBy });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (NpgsqlCommand cmd = s.Command(
            "INSERT INTO pay.settlement_line (tenant_id, import_id, line_no, terminal_no, reference_no, transaction_date, amount, fee, outcome, payment_id) " +
            "SELECT core.current_tenant(), $1, * FROM unnest($2::int[], $3::text[], $4::text[], $5::date[], $6::bigint[], $7::bigint[], $8::text[], $9::uuid[])"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = id });
            cmd.Parameters.Add(new NpgsqlParameter { Value = lines.Select(l => l.Line.LineNo).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = lines.Select(l => l.Line.TerminalNo).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = lines.Select(l => l.Line.ReferenceNo).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = lines.Select(l => l.Line.TransactionDate).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = lines.Select(l => l.Line.Amount).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = lines.Select(l => l.Line.Fee).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = lines.Select(l => l.Outcome.ToString()).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = lines.Select(l => l.PaymentId).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return (await GetAsync(scope, id, cancellationToken))!;
    }

    public async Task<SettlementImportView?> GetAsync(IUnitOfWorkScope scope, Guid id, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        SettlementImportView? head = (await ReadImportsAsync(s, "WHERE id = $1", id, cancellationToken)).SingleOrDefault();
        if (head is null)
        {
            return null;
        }

        await using NpgsqlCommand cmd = s.Command(
            "SELECT line_no, terminal_no, reference_no, transaction_date, amount, fee, outcome, payment_id FROM pay.settlement_line WHERE import_id = $1 ORDER BY line_no");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        var lines = new List<SettlementMatch>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            lines.Add(new SettlementMatch(
                new SettlementLine(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetFieldValue<DateOnly>(3), r.GetInt64(4), r.GetInt64(5)),
                Enum.Parse<SettlementOutcome>(r.GetString(6)), r.IsDBNull(7) ? null : r.GetGuid(7)));
        }

        return head with { Lines = lines };
    }

    public async Task<IReadOnlyList<SettlementImportView>> RecentAsync(IUnitOfWorkScope scope, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return await ReadImportsAsync((PostgresUnitOfWorkScope)scope, "ORDER BY imported_at DESC, id DESC LIMIT $1", limit, cancellationToken);
    }

    public async Task<IReadOnlyList<UnreconciledPayment>> UnreconciledAsync(IUnitOfWorkScope scope, DateOnly fromDate, DateOnly toDate, int limit,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT p.id, p.sale_id, p.reference_no, p.terminal_no, p.card_last4, p.amount, p.business_date, p.recorded_by, p.register_id, p.recorded_at " +
            "FROM pay.payment p WHERE p.status = 'ConfirmedManual' AND p.business_date BETWEEN $1 AND $2 " +
            "AND NOT EXISTS (SELECT 1 FROM pay.settlement_line l WHERE l.payment_id = p.id AND l.outcome = 'Matched') " +
            "ORDER BY p.business_date, p.recorded_at, p.id LIMIT $3");
        cmd.Parameters.Add(new NpgsqlParameter { Value = fromDate });
        cmd.Parameters.Add(new NpgsqlParameter { Value = toDate });
        cmd.Parameters.Add(new NpgsqlParameter { Value = limit });
        var list = new List<UnreconciledPayment>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new UnreconciledPayment(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5), r.GetFieldValue<DateOnly>(6), r.GetGuid(7), r.IsDBNull(8) ? null : r.GetGuid(8),
                r.GetFieldValue<DateTimeOffset>(9)));
        }

        return list;
    }

    private static async Task<IReadOnlyList<SettlementImportView>> ReadImportsAsync(PostgresUnitOfWorkScope s, string tail, object arg, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = s.Command($"SELECT {ImportColumns} FROM pay.settlement_import {tail}");
        cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
        var list = new List<SettlementImportView>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new SettlementImportView(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt64(4), r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetGuid(6), r.GetGuid(7), r.GetFieldValue<DateTimeOffset>(8)));
        }

        return list;
    }
}
