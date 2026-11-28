using Npgsql;
using NpgsqlTypes;
using RetailCloud.Accounting.Application;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;

namespace RetailCloud.Accounting.Infrastructure;

/// <summary>ACC-007 over acc.journal_entry / acc.journal_line: posted entries only (a reversed entry and its reversal both count and cancel out).</summary>
public sealed class PostgresAccountingReports : IAccountingReports
{
    public async Task<IReadOnlyList<AccountMovement>> MovementsAsync(IUnitOfWorkScope scope, DateTimeOffset fromDate, DateTimeOffset toDate,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT l.account_id, " +
            "coalesce(sum(l.debit) FILTER (WHERE e.effective_at < $1), 0)::bigint, coalesce(sum(l.credit) FILTER (WHERE e.effective_at < $1), 0)::bigint, " +
            "coalesce(sum(l.debit) FILTER (WHERE e.effective_at >= $1), 0)::bigint, coalesce(sum(l.credit) FILTER (WHERE e.effective_at >= $1), 0)::bigint " +
            "FROM acc.journal_line l JOIN acc.journal_entry e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id " +
            "WHERE e.status <> 'Draft' AND e.effective_at < $2 GROUP BY l.account_id");
        cmd.Parameters.Add(new NpgsqlParameter { Value = fromDate.ToUniversalTime(), NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = toDate.ToUniversalTime(), NpgsqlDbType = NpgsqlDbType.TimestampTz });
        var list = new List<AccountMovement>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new AccountMovement(r.GetGuid(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)));
        }

        return list;
    }

    public async Task<LedgerPage> LedgerAsync(IUnitOfWorkScope scope, IReadOnlyCollection<Guid> accountIds, Guid? detailId, DateTimeOffset fromDate,
        DateTimeOffset toDate, int offset, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        var s = (PostgresUnitOfWorkScope)scope;
        const string Where = "e.status <> 'Draft' AND l.account_id = ANY($1) AND ($2::uuid IS NULL OR l.detail_id = $2)";
        long opening, debit, credit;
        await using (NpgsqlCommand cmd = s.Command(
            "SELECT coalesce(sum(l.debit - l.credit) FILTER (WHERE e.effective_at < $3), 0)::bigint, " +
            "coalesce(sum(l.debit) FILTER (WHERE e.effective_at >= $3), 0)::bigint, coalesce(sum(l.credit) FILTER (WHERE e.effective_at >= $3), 0)::bigint " +
            $"FROM acc.journal_line l JOIN acc.journal_entry e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id WHERE {Where} AND e.effective_at < $4"))
        {
            Bind(cmd, accountIds, detailId, fromDate, toDate);
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
            await r.ReadAsync(cancellationToken);
            (opening, debit, credit) = (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
        }

        var lines = new List<LedgerLine>();
        await using (NpgsqlCommand cmd = s.Command(
            "SELECT e.id, e.effective_at, coalesce(l.description, e.description), e.source_type, l.account_id, a.code, l.detail_type, l.detail_id, l.debit, l.credit, " +
            "(sum(l.debit - l.credit) OVER (ORDER BY e.effective_at, e.id, l.line_no))::bigint " +
            "FROM acc.journal_line l JOIN acc.journal_entry e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id " +
            "JOIN acc.account a ON a.tenant_id = l.tenant_id AND a.id = l.account_id " +
            $"WHERE {Where} AND e.effective_at >= $3 AND e.effective_at < $4 ORDER BY e.effective_at, e.id, l.line_no OFFSET $5 LIMIT $6"))
        {
            Bind(cmd, accountIds, detailId, fromDate, toDate);
            cmd.Parameters.Add(new NpgsqlParameter { Value = offset });
            cmd.Parameters.Add(new NpgsqlParameter { Value = limit + 1 });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
            {
                lines.Add(new LedgerLine(r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.GetGuid(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetGuid(7), r.GetInt64(8), r.GetInt64(9),
                    opening + r.GetInt64(10)));
            }
        }

        bool more = lines.Count > limit;
        return new LedgerPage(opening, debit, credit, more ? lines.GetRange(0, limit) : lines, more);
    }

    private static void Bind(NpgsqlCommand cmd, IReadOnlyCollection<Guid> accountIds, Guid? detailId, DateTimeOffset fromDate, DateTimeOffset toDate)
    {
        cmd.Parameters.Add(new NpgsqlParameter { Value = accountIds.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)detailId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = fromDate.ToUniversalTime(), NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = toDate.ToUniversalTime(), NpgsqlDbType = NpgsqlDbType.TimestampTz });
    }
}
