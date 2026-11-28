using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Sales.Application;

namespace RetailCloud.Sales.Infrastructure;

/// <summary>RPT-001 over finalized sales. Plain reads at READ COMMITTED take no lock a POS write waits on (RPT-002).</summary>
public sealed class PostgresSalesReports : ISalesReports
{
    public async Task<IReadOnlyList<SalesDailyLine>> DailyAsync(IUnitOfWorkScope scope, DateTimeOffset fromDate, DateTimeOffset toDate, string timeZone,
        IReadOnlyCollection<Guid> branchIds, bool byRegister, bool byCashier, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(branchIds);
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT (s.finalized_at AT TIME ZONE $1)::date, s.branch_id, CASE WHEN $2 THEN s.register_id END, CASE WHEN $3 THEN s.cashier_id END, " +
            "count(DISTINCT s.id)::int, sum(l.gross)::bigint, sum(l.discount)::bigint, sum(l.tax)::bigint, sum(l.net)::bigint " +
            "FROM sales.sale s JOIN sales.sale_line l ON l.tenant_id = s.tenant_id AND l.sale_id = s.id " +
            "WHERE s.status = 'Finalized' AND s.finalized_at >= $4 AND s.finalized_at < $5 AND s.branch_id = ANY($6) " +
            "GROUP BY 1, 2, 3, 4 ORDER BY 1, 2, 3, 4 LIMIT $7");
        cmd.Parameters.Add(new NpgsqlParameter { Value = timeZone });
        cmd.Parameters.Add(new NpgsqlParameter { Value = byRegister });
        cmd.Parameters.Add(new NpgsqlParameter { Value = byCashier });
        cmd.Parameters.Add(new NpgsqlParameter { Value = fromDate.ToUniversalTime(), NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = toDate.ToUniversalTime(), NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = branchIds.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = limit });
        var list = new List<SalesDailyLine>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new SalesDailyLine(r.GetFieldValue<DateOnly>(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3),
                r.GetInt32(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8)));
        }

        return list;
    }

    public async Task<IReadOnlyList<GrossProfitLine>> GrossProfitAsync(IUnitOfWorkScope scope, DateTimeOffset fromDate, DateTimeOffset toDate, string timeZone,
        IReadOnlyCollection<Guid> branchIds, GrossProfitGroup group, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(branchIds);
        (string key, string name) = group switch
        {
            GrossProfitGroup.Branch => ("s.branch_id::text", "NULL::text"),
            GrossProfitGroup.Product => ("l.variant_id::text", "max(l.sku || ' ' || l.name)"),
            _ => ("((s.finalized_at AT TIME ZONE $1)::date)::text", "NULL::text"),
        };
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            $"SELECT {key}, {name}, sum(l.net)::bigint, coalesce(sum(l.cost_at_sale), 0)::bigint, " +
            "(count(*) FILTER (WHERE l.cost_at_sale IS NULL OR l.cost_at_sale = 0))::int " +
            "FROM sales.sale s JOIN sales.sale_line l ON l.tenant_id = s.tenant_id AND l.sale_id = s.id " +
            "WHERE s.status = 'Finalized' AND s.finalized_at >= $2 AND s.finalized_at < $3 AND s.branch_id = ANY($4) " +
            "GROUP BY 1 ORDER BY 1 LIMIT $5");
        cmd.Parameters.Add(new NpgsqlParameter { Value = timeZone });
        cmd.Parameters.Add(new NpgsqlParameter { Value = fromDate.ToUniversalTime(), NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = toDate.ToUniversalTime(), NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = branchIds.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = limit });
        var list = new List<GrossProfitLine>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new GrossProfitLine(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt32(4)));
        }

        return list;
    }
}
