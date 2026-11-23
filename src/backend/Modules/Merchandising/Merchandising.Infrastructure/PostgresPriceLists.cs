using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Merchandising.Application;

namespace RetailCloud.Merchandising.Infrastructure;

/// <summary>
/// CAT-009 on merch.price_list / price_list_item. Versions never overlap (DI-083, exclusion constraint) and what applied
/// is never rewritten (merch.price_history, RC023); this class only ends the version in force and adds the next one.
/// </summary>
public sealed class PostgresPriceLists : IPriceLists
{
    public const string DefaultName = "قیمت پایه";

    public async Task<IReadOnlyList<PriceListView>> ListAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using (NpgsqlCommand ensure = s.Command(
            "INSERT INTO merch.price_list (tenant_id, id, name, is_default) VALUES (core.current_tenant(), $1, $2, true) " +
            "ON CONFLICT (tenant_id) WHERE is_default DO NOTHING"))
        {
            ensure.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
            ensure.Parameters.Add(new NpgsqlParameter { Value = DefaultName });
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }

        var lists = new List<PriceListView>();
        await using NpgsqlCommand cmd = s.Command("SELECT id, name, is_default FROM merch.price_list ORDER BY is_default DESC, name");
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            lists.Add(new PriceListView(r.GetGuid(0), r.GetString(1), r.GetBoolean(2), []));
        }

        return lists;
    }

    public async Task<(PriceOutcome Outcome, IReadOnlyList<PriceChange>? Changes, Guid? Variant)> PreviewAsync(IUnitOfWorkScope scope, Guid priceListId,
        IReadOnlyList<PriceItem> items, DateTimeOffset validFrom, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var s = (PostgresUnitOfWorkScope)scope;
        await using (NpgsqlCommand lockList = s.Command("SELECT 1 FROM merch.price_list WHERE id = $1 FOR UPDATE"))
        {
            lockList.Parameters.Add(new NpgsqlParameter { Value = priceListId });
            if (await lockList.ExecuteScalarAsync(cancellationToken) is null)
            {
                return (PriceOutcome.NotFound, null, null);
            }
        }

        Guid[] ids = [.. items.Select(i => i.VariantId)];
        var live = new HashSet<Guid>();
        await using (NpgsqlCommand known = s.Command(
            "SELECT v.id FROM merch.tenant_variant v JOIN merch.tenant_product p ON p.tenant_id = v.tenant_id AND p.id = v.product_id " +
            "WHERE v.id = ANY($1) AND v.status = 'Active' AND p.status <> 'Merged'"))
        {
            known.Parameters.Add(new NpgsqlParameter { Value = ids, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
            await using NpgsqlDataReader r = await known.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
            {
                live.Add(r.GetGuid(0));
            }
        }

        if (items.FirstOrDefault(i => !live.Contains(i.VariantId)) is { } unknown)
        {
            return (PriceOutcome.UnknownVariant, null, unknown.VariantId);
        }

        var current = new Dictionary<Guid, long>();
        await using (NpgsqlCommand at = s.Command(
            "SELECT variant_id, unit_price FROM merch.price_list_item WHERE price_list_id = $1 AND variant_id = ANY($2) AND valid @> $3::timestamptz"))
        {
            at.Parameters.Add(new NpgsqlParameter { Value = priceListId });
            at.Parameters.Add(new NpgsqlParameter { Value = ids, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
            at.Parameters.Add(new NpgsqlParameter { Value = validFrom.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            await using NpgsqlDataReader r = await at.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
            {
                current[r.GetGuid(0)] = r.GetInt64(1);
            }
        }

        return (PriceOutcome.Done, [.. items.Select(i => new PriceChange(i.VariantId, current.TryGetValue(i.VariantId, out long c) ? c : null, i.UnitPrice))], null);
    }

    public async Task<PriceOutcome> SetAsync(IUnitOfWorkScope scope, Guid priceListId, IReadOnlyList<PriceItem> items, DateTimeOffset validFrom,
        string? reason, Guid createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var s = (PostgresUnitOfWorkScope)scope;
        DateTime from = validFrom.UtcDateTime;
        foreach (PriceItem item in items)
        {
            Guid? covering = null;
            DateTime coveringFrom = default;
            await using (NpgsqlCommand find = s.Command(
                "SELECT id, lower(valid), lower(valid) > statement_timestamp() FROM merch.price_list_item " +
                "WHERE price_list_id = $1 AND variant_id = $2 AND valid @> $3::timestamptz"))
            {
                find.Parameters.Add(new NpgsqlParameter { Value = priceListId });
                find.Parameters.Add(new NpgsqlParameter { Value = item.VariantId });
                find.Parameters.Add(new NpgsqlParameter { Value = from, NpgsqlDbType = NpgsqlDbType.TimestampTz });
                await using NpgsqlDataReader r = await find.ExecuteReaderAsync(cancellationToken);
                if (await r.ReadAsync(cancellationToken))
                {
                    covering = r.GetGuid(0);
                    coveringFrom = r.GetDateTime(1);
                    if (coveringFrom == from && !r.GetBoolean(2))
                    {
                        return PriceOutcome.Taken;   // a version starting at this instant has already applied
                    }
                }
            }

            if (covering is { } same && coveringFrom == from)
            {
                // the same start, not reached yet: correct the scheduled price instead of adding a version
                await Exec(s, "UPDATE merch.price_list_item SET unit_price = $2, reason = $3 WHERE id = $1", cancellationToken,
                    same, item.UnitPrice, (object?)reason ?? DBNull.Value);
                continue;
            }

            if (covering is { } open)
            {
                await Exec(s, "UPDATE merch.price_list_item SET valid = tstzrange(lower(valid), $2) WHERE id = $1", cancellationToken, open, from);
            }

            // until the next scheduled version, if there is one
            await Exec(s,
                "INSERT INTO merch.price_list_item (tenant_id, id, price_list_id, variant_id, unit_price, valid, created_by, reason) " +
                "SELECT core.current_tenant(), $1, $2, $3, $4, tstzrange($5, (SELECT min(lower(valid)) FROM merch.price_list_item " +
                "WHERE price_list_id = $2 AND variant_id = $3 AND lower(valid) > $5)), $6, $7",
                cancellationToken, Guid.CreateVersion7(), priceListId, item.VariantId, item.UnitPrice, from, createdBy, (object?)reason ?? DBNull.Value);
        }

        return PriceOutcome.Done;
    }

    public async Task<IReadOnlyList<PriceVersion>?> HistoryAsync(IUnitOfWorkScope scope, Guid priceListId, Guid? variantId, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using (NpgsqlCommand exists = s.Command("SELECT 1 FROM merch.price_list WHERE id = $1"))
        {
            exists.Parameters.Add(new NpgsqlParameter { Value = priceListId });
            if (await exists.ExecuteScalarAsync(cancellationToken) is null)
            {
                return null;
            }
        }

        var versions = new List<PriceVersion>();
        await using NpgsqlCommand cmd = s.Command(
            "SELECT variant_id, unit_price, lower(valid), upper(valid), reason, created_by, created_at FROM merch.price_list_item " +
            "WHERE price_list_id = $1 AND ($2::uuid IS NULL OR variant_id = $2) ORDER BY lower(valid) DESC, created_at DESC LIMIT 100");
        cmd.Parameters.Add(new NpgsqlParameter { Value = priceListId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)variantId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            versions.Add(new PriceVersion(r.GetGuid(0), r.GetInt64(1), r.GetFieldValue<DateTimeOffset>(2),
                r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetGuid(5), r.GetFieldValue<DateTimeOffset>(6)));
        }

        return versions;
    }

    private static async Task Exec(PostgresUnitOfWorkScope s, string sql, CancellationToken ct, params object[] args)
    {
        await using NpgsqlCommand cmd = s.Command(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(a is DateTime d
                ? new NpgsqlParameter { Value = d, NpgsqlDbType = NpgsqlDbType.TimestampTz }
                : new NpgsqlParameter { Value = a });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
