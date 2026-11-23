using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Merchandising.Application;

namespace RetailCloud.Merchandising.Infrastructure;

/// <summary>CAT-010 rules and BR-021 caps on merch.discount_rule / manual_discount_cap; a rule is only ever ended (RC025).</summary>
public sealed class PostgresDiscounts : IDiscounts
{
    private const string Columns = "id, name, target_kind, target_id, percent_bp, lower(valid), upper(valid), branch_ids";

    public async Task<DiscountRuleView> CreateRuleAsync(IUnitOfWorkScope scope, DiscountRuleSpec spec, Guid createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Guid id = Guid.CreateVersion7();
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "INSERT INTO merch.discount_rule (tenant_id, id, name, target_kind, target_id, percent_bp, valid, branch_ids, created_by) " +
            "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, tstzrange($6, $7), $8, $9)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { Value = spec.Name.Trim() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = spec.TargetKind });
        cmd.Parameters.Add(new NpgsqlParameter { Value = spec.TargetId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = spec.PercentBp });
        cmd.Parameters.Add(new NpgsqlParameter { Value = spec.ValidFrom.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)spec.ValidTo?.UtcDateTime ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = spec.BranchIds.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = createdBy });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return new DiscountRuleView(id, spec.Name.Trim(), spec.TargetKind, spec.TargetId, spec.PercentBp, spec.ValidFrom, spec.ValidTo, spec.BranchIds);
    }

    public Task<IReadOnlyList<DiscountRuleView>> ListRulesAsync(IUnitOfWorkScope scope, bool includeEnded, CancellationToken cancellationToken) =>
        ReadAsync((PostgresUnitOfWorkScope)scope,
            $"SELECT {Columns} FROM merch.discount_rule WHERE $1 OR upper(valid) IS NULL OR upper(valid) > statement_timestamp() " +
            "ORDER BY created_at DESC LIMIT 200", cancellationToken, includeEnded);

    public async Task<DiscountRuleView?> GetRuleAsync(IUnitOfWorkScope scope, Guid ruleId, CancellationToken cancellationToken) =>
        (await ReadAsync((PostgresUnitOfWorkScope)scope, $"SELECT {Columns} FROM merch.discount_rule WHERE id = $1", cancellationToken, ruleId)).SingleOrDefault();

    public async Task<bool> EndRuleAsync(IUnitOfWorkScope scope, Guid ruleId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            // a rule that has not started is closed at its start instant ([start, start]: a range cannot be empty)
            "UPDATE merch.discount_rule SET valid = CASE WHEN lower(valid) >= statement_timestamp() THEN tstzrange(lower(valid), lower(valid), '[]') " +
            "ELSE tstzrange(lower(valid), statement_timestamp()) END " +
            "WHERE id = $1 AND (upper(valid) IS NULL OR upper(valid) > statement_timestamp())");
        cmd.Parameters.Add(new NpgsqlParameter { Value = ruleId });
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public Task<IReadOnlyList<DiscountRuleView>> RulesForAsync(IUnitOfWorkScope scope, IReadOnlyCollection<Guid> productIds, DateTimeOffset at,
        CancellationToken cancellationToken) =>
        ReadAsync((PostgresUnitOfWorkScope)scope,
            $"SELECT {Columns} FROM merch.discount_rule WHERE target_kind = 'Product' AND target_id = ANY($1) AND valid @> $2::timestamptz",
            cancellationToken, productIds.ToArray(), at.UtcDateTime);

    public async Task<IReadOnlyDictionary<string, int>> CapsAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        var caps = new Dictionary<string, int>(StringComparer.Ordinal);
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("SELECT role, cap_bp FROM merch.manual_discount_cap");
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            caps[r.GetString(0)] = r.GetInt32(1);
        }

        return caps;
    }

    public async Task SetCapsAsync(IUnitOfWorkScope scope, IReadOnlyDictionary<string, int> caps, Guid changedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caps);
        foreach ((string role, int cap) in caps)
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
                "INSERT INTO merch.manual_discount_cap (tenant_id, role, cap_bp, changed_by) VALUES (core.current_tenant(), $1, $2, $3) " +
                "ON CONFLICT (tenant_id, role) DO UPDATE SET cap_bp = EXCLUDED.cap_bp, changed_by = EXCLUDED.changed_by, changed_at = now()");
            cmd.Parameters.Add(new NpgsqlParameter { Value = role });
            cmd.Parameters.Add(new NpgsqlParameter { Value = cap });
            cmd.Parameters.Add(new NpgsqlParameter { Value = changedBy });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<DiscountRuleView>> ReadAsync(PostgresUnitOfWorkScope s, string sql, CancellationToken ct, params object[] args)
    {
        await using NpgsqlCommand cmd = s.Command(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(a switch
            {
                Guid[] ids => new NpgsqlParameter { Value = ids, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid },
                DateTime t => new NpgsqlParameter { Value = t, NpgsqlDbType = NpgsqlDbType.TimestampTz },
                _ => new NpgsqlParameter { Value = a },
            });
        }

        var list = new List<DiscountRuleView>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new DiscountRuleView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetInt32(4), r.GetFieldValue<DateTimeOffset>(5),
                r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.GetFieldValue<Guid[]>(7)));
        }

        return list;
    }
}
