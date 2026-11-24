using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Merchandising.Application;

namespace RetailCloud.Merchandising.Infrastructure;

/// <summary>CAT-004 / CAT-005 on catalog.* (global, no RLS: reference data with no store fields).</summary>
public sealed class PostgresGlobalCatalog : IGlobalCatalog
{
    public async Task<IReadOnlyList<CatalogCandidate>> ByGtinAsync(IUnitOfWorkScope scope, string gtin14, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT p.id, p.name, p.brand, p.category, p.net_content, p.image_url, p.verified_at FROM catalog.global_barcode b " +
            "JOIN catalog.catalog_product p ON p.id = b.catalog_product_id WHERE b.gtin14 = $1 AND p.status = 'Published'");
        cmd.Parameters.Add(new NpgsqlParameter { Value = gtin14 });
        var list = new List<CatalogCandidate>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new CatalogCandidate("global", CandidateRules.GlobalConfidence, r.GetFieldValue<DateTimeOffset>(6), null, null,
                new CandidateFields(r.GetString(1), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5)), r.GetGuid(0)));
        }

        return list;
    }

    public async Task<bool> PublishedAsync(IUnitOfWorkScope scope, Guid catalogProductId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT EXISTS (SELECT 1 FROM catalog.catalog_product WHERE id = $1 AND status = 'Published')");
        cmd.Parameters.Add(new NpgsqlParameter { Value = catalogProductId });
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyList<ProviderRecord>> CachedAsync(IUnitOfWorkScope scope, string gtin14, DateTimeOffset notBefore,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT source, found, fields, confidence, license, attribution, fetched_at FROM catalog.catalog_source_record WHERE gtin14 = $1 AND fetched_at >= $2");
        cmd.Parameters.Add(new NpgsqlParameter { Value = gtin14 });
        cmd.Parameters.Add(new NpgsqlParameter { Value = notBefore.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        var list = new List<ProviderRecord>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            using JsonDocument f = JsonDocument.Parse(r.GetString(2));
            string? F(string k) => f.RootElement.TryGetProperty(k, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            list.Add(new ProviderRecord(r.GetString(0), gtin14, r.GetBoolean(1), new CandidateFields(F("name"), F("brand"), F("category"), F("netContent"), F("imageUrl")),
                r.GetDecimal(3), r.GetString(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6)));
        }

        return list;
    }

    public async Task SaveAsync(IUnitOfWorkScope scope, ProviderRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var fields = new Dictionary<string, string>();
        void Put(string k, string? v)
        {
            if (v is not null)
            {
                fields[k] = v;
            }
        }

        Put("name", record.Fields.Name);
        Put("brand", record.Fields.Brand);
        Put("category", record.Fields.Category);
        Put("netContent", record.Fields.NetContent);
        Put("imageUrl", record.Fields.ImageUrl);
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "INSERT INTO catalog.catalog_source_record (source, gtin14, found, fields, confidence, license, attribution, fetched_at) " +
            "VALUES ($1, $2, $3, $4::jsonb, $5, $6, $7, now()) ON CONFLICT (source, gtin14) DO UPDATE SET found = EXCLUDED.found, fields = EXCLUDED.fields, " +
            "confidence = EXCLUDED.confidence, license = EXCLUDED.license, attribution = EXCLUDED.attribution, fetched_at = now()");
        cmd.Parameters.Add(new NpgsqlParameter { Value = record.Source });
        cmd.Parameters.Add(new NpgsqlParameter { Value = record.Gtin14 });
        cmd.Parameters.Add(new NpgsqlParameter { Value = record.Found });
        cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(fields) });
        cmd.Parameters.Add(new NpgsqlParameter { Value = record.Confidence });
        cmd.Parameters.Add(new NpgsqlParameter { Value = record.License });
        cmd.Parameters.Add(new NpgsqlParameter { Value = record.Attribution });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
