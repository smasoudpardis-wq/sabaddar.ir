using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Merchandising.Application;

namespace RetailCloud.Merchandising.Infrastructure;

/// <summary>
/// CAT-007 profiles on merch.weighted_barcode_profile. The set is replaced as a whole under the settings row's version;
/// the database refuses overlapping prefixes (DI-084, RC022) whatever the caller checked.
/// </summary>
public sealed class PostgresWeightedBarcodeProfiles : IWeightedBarcodeProfiles
{
    public async Task<WeightedProfileSet> GetAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        int version;
        await using (NpgsqlCommand v = s.Command("SELECT version FROM merch.weighted_barcode_settings"))
        {
            version = await v.ExecuteScalarAsync(cancellationToken) is int n ? n : 1;
        }

        var profiles = new List<WeightedBarcodeProfile>();
        await using NpgsqlCommand cmd = s.Command(
            "SELECT id, prefix, item_code_length, value_type, value_length, decimals, has_inner_check_digit " +
            "FROM merch.weighted_barcode_profile ORDER BY prefix");
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            profiles.Add(new WeightedBarcodeProfile(r.GetGuid(0), r.GetString(1), r.GetInt32(2), Enum.Parse<WeightedValueType>(r.GetString(3)),
                r.GetInt32(4), r.GetInt32(5), r.GetBoolean(6)));
        }

        return new WeightedProfileSet(version, profiles);
    }

    public async Task<(WeightedProfilesOutcome Outcome, WeightedProfileSet? Set)> ReplaceAsync(IUnitOfWorkScope scope, int expectedVersion,
        IReadOnlyList<WeightedBarcodeProfile> profiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var s = (PostgresUnitOfWorkScope)scope;
        if (expectedVersion < 1)
        {
            return (WeightedProfilesOutcome.VersionConflict, null);
        }

        await s.Transaction.SaveAsync("weighted", cancellationToken);   // an overlap undoes the whole replace, version bump included

        // no row yet = version 1; a concurrent first write meets the primary key and then fails the version test
        await using (NpgsqlCommand bump = s.Command(
            "INSERT INTO merch.weighted_barcode_settings AS w (tenant_id, version) SELECT core.current_tenant(), 2 WHERE $1 = 1 " +
            "ON CONFLICT (tenant_id) DO UPDATE SET version = w.version + 1 WHERE w.version = $1 RETURNING version"))
        {
            bump.Parameters.Add(new NpgsqlParameter { Value = expectedVersion });
            object? bumped = await bump.ExecuteScalarAsync(cancellationToken);
            if (bumped is null)
            {
                return (WeightedProfilesOutcome.VersionConflict, null);
            }
        }

        await using (NpgsqlCommand clear = s.Command("DELETE FROM merch.weighted_barcode_profile"))
        {
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (WeightedBarcodeProfile p in profiles)
        {
            await using NpgsqlCommand ins = s.Command(
                "INSERT INTO merch.weighted_barcode_profile (tenant_id, id, prefix, item_code_length, value_type, value_length, decimals, has_inner_check_digit) " +
                "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7)");
            foreach (object value in new object[] { p.Id, p.Prefix, p.ItemCodeLength, p.ValueType.ToString(), p.ValueLength, p.Decimals, p.HasInnerCheckDigit })
            {
                ins.Parameters.Add(new NpgsqlParameter { Value = value });
            }

            try
            {
                await ins.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException e) when (e.SqlState == "RC022")
            {
                await s.Transaction.RollbackAsync("weighted", cancellationToken);
                return (WeightedProfilesOutcome.Overlap, null);
            }
        }

        return (WeightedProfilesOutcome.Done, await GetAsync(scope, cancellationToken));
    }
}
