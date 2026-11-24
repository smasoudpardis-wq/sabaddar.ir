using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Merchandising.Application;

namespace RetailCloud.Merchandising.Infrastructure;

/// <summary>
/// CAT-001/002/002.1 on merch.* under the tenant's RLS context. The database keeps one active owner per barcode
/// (ux_barcode_active) and the barcode history (merch.barcode_history); this class reports conflicts in D07 terms.
/// </summary>
public sealed class PostgresProductCatalog(IOutbox outbox) : IProductCatalog
{
    private const string Producer = "Merchandising";

    public async Task<CatalogResult<ProductView>> CreateAsync(IUnitOfWorkScope scope, ProductSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var s = (PostgresUnitOfWorkScope)scope;
        if (await FirstTakenBarcodeAsync(s, spec.Variants.SelectMany(v => v.Barcodes), cancellationToken) is { } taken)
        {
            return new(CatalogOutcome.BarcodeAssigned, null, taken);
        }

        if (await AnySkuTakenAsync(s, spec.Variants.Select(v => v.Sku), cancellationToken))
        {
            return new(CatalogOutcome.DuplicateSku, null);
        }

        Guid id = Guid.CreateVersion7();
        await Exec(s, "INSERT INTO merch.tenant_product (tenant_id, id, name, brand, base_unit_code, tax_code, tax_rate_bp, axes) " +
                      "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7)", cancellationToken,
            id, spec.Name.Trim(), (object?)spec.Brand?.Trim() ?? DBNull.Value, spec.BaseUnit, (object?)spec.TaxCode ?? DBNull.Value, spec.TaxRate,
            (spec.Axes ?? []).ToArray());
        foreach (VariantSpec v in spec.Variants)
        {
            await InsertVariantAsync(s, id, v, cancellationToken);
        }

        await outbox.AddAsync(scope, Producer, "merchandising.product-created", 1, id,
            new { productId = id, name = spec.Name.Trim(), variants = spec.Variants.Count }, causationId: null, cancellationToken);
        return new(CatalogOutcome.Done, await GetAsync(scope, id, cancellationToken));
    }

    public async Task<ProductView?> GetAsync(IUnitOfWorkScope scope, Guid productId, CancellationToken cancellationToken) =>
        (await LoadAsync((PostgresUnitOfWorkScope)scope, [productId], cancellationToken)).SingleOrDefault();

    public async Task<(IReadOnlyList<ProductView> Items, string? NextCursor)> SearchAsync(IUnitOfWorkScope scope, string? query, string? barcode,
        string? cursor, int limit, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        var where = new List<string>();
        await using NpgsqlCommand cmd = s.Command("");
        void Arg(object value) => cmd.Parameters.Add(new NpgsqlParameter { Value = value });
        if (!string.IsNullOrWhiteSpace(query))
        {
            // CAT-011: SKU or barcode typed into the box match exactly; otherwise every term must start a word of the
            // normalised name, in any order ("کم شیر" finds «شیر کم‌چرب»). '%term%' lets the trigram index narrow first.
            string q = query.Trim();
            string[] terms = (await NormalizeAsync(s, q, cancellationToken)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(5).ToArray();
            var nameMatch = new List<string>();
            foreach (string term in terms)
            {
                string like = term.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);
                Arg("%" + like + "%");
                Arg("% " + like + "%");
                nameMatch.Add($"(p.name_normalized LIKE ${cmd.Parameters.Count - 1} AND (' ' || p.name_normalized) LIKE ${cmd.Parameters.Count})");
            }

            Arg(q);
            int sku = cmd.Parameters.Count;
            Arg(RetailCloud.BuildingBlocks.Domain.Gtin.TryParse(q, out RetailCloud.BuildingBlocks.Domain.Gtin qg) ? qg.Normalized14 : q);
            int code = cmd.Parameters.Count;
            where.Add("(" + (nameMatch.Count > 0 ? "(" + string.Join(" AND ", nameMatch) + ") OR " : "") +
                      $"EXISTS (SELECT 1 FROM merch.tenant_variant v WHERE v.tenant_id = p.tenant_id AND v.product_id = p.id AND v.sku = ${sku}) OR " +
                      "EXISTS (SELECT 1 FROM merch.tenant_product_barcode b JOIN merch.tenant_variant v ON v.tenant_id = b.tenant_id AND v.id = b.variant_id " +
                      $"WHERE v.product_id = p.id AND b.active AND b.code_normalized = ${code}))");
        }

        if (!string.IsNullOrWhiteSpace(barcode))
        {
            Arg(RetailCloud.BuildingBlocks.Domain.Gtin.TryParse(barcode, out RetailCloud.BuildingBlocks.Domain.Gtin g) ? g.Normalized14 : barcode.Trim());
            where.Add($"EXISTS (SELECT 1 FROM merch.tenant_product_barcode b JOIN merch.tenant_variant v ON v.tenant_id = b.tenant_id AND v.id = b.variant_id " +
                      $"WHERE v.product_id = p.id AND b.active AND b.code_normalized = ${cmd.Parameters.Count})");
        }

        if (cursor is not null && DecodeCursor(cursor) is { } after)
        {
            Arg(after.Name);
            Arg(after.Id);
            where.Add($"(p.name_normalized, p.id) > (${cmd.Parameters.Count - 1}, ${cmd.Parameters.Count})");
        }

        Arg(limit + 1);
        where.Add("p.status <> 'Merged'");   // a merged product lives on as the product it was merged into
        cmd.CommandText = "SELECT p.id, p.name_normalized FROM merch.tenant_product p WHERE " + string.Join(" AND ", where) +
                          $" ORDER BY p.name_normalized, p.id LIMIT ${cmd.Parameters.Count}";
        var page = new List<(Guid Id, string Name)>();
        await using (NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await r.ReadAsync(cancellationToken))
            {
                page.Add((r.GetGuid(0), r.GetString(1)));
            }
        }

        bool more = page.Count > limit;
        page = [.. page.Take(limit)];
        IReadOnlyList<ProductView> items = await LoadAsync(s, [.. page.Select(p => p.Id)], cancellationToken);
        return (items, more ? EncodeCursor(page[^1].Name, page[^1].Id) : null);
    }

    private static async Task<string> NormalizeAsync(PostgresUnitOfWorkScope s, string text, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = s.Command("SELECT merch.normalize_fa($1)");   // one normaliser: the one the stored key uses
        cmd.Parameters.Add(new NpgsqlParameter { Value = text });
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<BarcodeMatch?> FindByCodeAsync(IUnitOfWorkScope scope, string codeNormalized, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        await using NpgsqlCommand cmd = s.Command(
            "SELECT p.id, v.id, p.name, v.sku, p.base_unit_code, b.is_internal, " +
            "(SELECT i.unit_price FROM merch.price_list_item i JOIN merch.price_list l ON l.tenant_id = i.tenant_id AND l.id = i.price_list_id " +
            "AND l.is_default WHERE i.variant_id = v.id AND i.valid @> statement_timestamp()) FROM merch.tenant_product_barcode b " +
            "JOIN merch.tenant_variant v ON v.tenant_id = b.tenant_id AND v.id = b.variant_id " +
            "JOIN merch.tenant_product p ON p.tenant_id = v.tenant_id AND p.id = v.product_id " +
            "WHERE b.active AND b.code_normalized = $1 AND v.status = 'Active' AND p.status = 'Active'");   // never sell what is switched off
        cmd.Parameters.Add(new NpgsqlParameter { Value = codeNormalized });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken)
            ? new BarcodeMatch(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5), r.IsDBNull(6) ? null : r.GetInt64(6))
            : null;
    }

    public async Task<IReadOnlySet<Guid>> ExistingVariantsAsync(IUnitOfWorkScope scope, IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("SELECT id FROM merch.tenant_variant WHERE id = ANY($1)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = variantIds.ToArray(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid });
        var found = new HashSet<Guid>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            found.Add(r.GetGuid(0));
        }

        return found;
    }

    public async Task<CatalogOutcome> SetVariantStatusAsync(IUnitOfWorkScope scope, Guid productId, Guid variantId, int expectedVersion, string status,
        CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        if (!await VariantOfAsync(s, productId, variantId, cancellationToken))
        {
            return CatalogOutcome.NotFound;
        }

        await using NpgsqlCommand cmd = s.Command(
            "WITH p AS (UPDATE merch.tenant_product SET version = version + 1 WHERE id = $1 AND version = $3 AND status <> 'Merged' RETURNING id) " +
            "UPDATE merch.tenant_variant SET status = $4 WHERE id = $2 AND product_id IN (SELECT id FROM p)");
        foreach (object v in new object[] { productId, variantId, expectedVersion, status })
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = v });
        }

        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1 ? CatalogOutcome.Done : CatalogOutcome.VersionConflict;
    }

    public async Task<CatalogResult<ProductView>> UpdateAsync(IUnitOfWorkScope scope, Guid productId, ProductChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var s = (PostgresUnitOfWorkScope)scope;
        await using NpgsqlCommand cmd = s.Command(
            "UPDATE merch.tenant_product SET name = coalesce($3, name), brand = CASE WHEN $4::text IS NULL THEN brand ELSE nullif($4, '') END, " +
            "tax_code = CASE WHEN $5::text IS NULL THEN tax_code ELSE nullif($5, '') END, tax_rate_bp = coalesce($6, tax_rate_bp), " +
            "status = coalesce($7, status), version = version + 1 WHERE id = $1 AND version = $2 AND status <> 'Merged'");
        cmd.Parameters.Add(new NpgsqlParameter { Value = productId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = change.ExpectedVersion });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)change.Name?.Trim() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)change.Brand?.Trim() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)change.TaxCode?.Trim() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)change.TaxRate ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)change.Status ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return new(await GetAsync(scope, productId, cancellationToken) is null ? CatalogOutcome.NotFound : CatalogOutcome.VersionConflict, null);
        }

        return new(CatalogOutcome.Done, await GetAsync(scope, productId, cancellationToken));
    }

    public async Task<CatalogResult<VariantView>> AddVariantAsync(IUnitOfWorkScope scope, Guid productId, VariantSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var s = (PostgresUnitOfWorkScope)scope;
        if (!await ProductExistsAsync(s, productId, cancellationToken))
        {
            return new(CatalogOutcome.NotFound, null);
        }

        await using (NpgsqlCommand axes = s.Command("SELECT axes FROM merch.tenant_product WHERE id = $1"))
        {
            axes.Parameters.Add(new NpgsqlParameter { Value = productId });
            string[] want = (string[])(await axes.ExecuteScalarAsync(cancellationToken))!;
            if (want.Length > 0 && !want.Order(StringComparer.Ordinal).SequenceEqual(spec.AxisValues.Keys.Order(StringComparer.Ordinal)))
            {
                return new(CatalogOutcome.AxesMismatch, null);
            }
        }

        if (await FirstTakenBarcodeAsync(s, spec.Barcodes, cancellationToken) is { } taken)
        {
            return new(CatalogOutcome.BarcodeAssigned, null, taken);
        }

        if (await AnySkuTakenAsync(s, [spec.Sku], cancellationToken))
        {
            return new(CatalogOutcome.DuplicateSku, null);
        }

        // DI-081: the variants of a product differ by their axis values
        await using (NpgsqlCommand same = s.Command("SELECT EXISTS (SELECT 1 FROM merch.tenant_variant WHERE product_id = $1 AND axis_values = $2)"))
        {
            same.Parameters.Add(new NpgsqlParameter { Value = productId });
            same.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(spec.AxisValues), NpgsqlDbType = NpgsqlDbType.Jsonb });
            if ((bool)(await same.ExecuteScalarAsync(cancellationToken))!)
            {
                return new(CatalogOutcome.DuplicateAxes, null);
            }
        }

        Guid id = await InsertVariantAsync(s, productId, spec, cancellationToken);
        await Exec(s, "UPDATE merch.tenant_product SET version = version + 1 WHERE id = $1", cancellationToken, productId);
        ProductView product = (await GetAsync(scope, productId, cancellationToken))!;
        return new(CatalogOutcome.Done, product.Variants.Single(v => v.Id == id));
    }

    public async Task<CatalogResult<BarcodeView>> AddBarcodeAsync(IUnitOfWorkScope scope, Guid productId, Guid variantId, BarcodeSpec spec,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var s = (PostgresUnitOfWorkScope)scope;
        if (!await VariantOfAsync(s, productId, variantId, cancellationToken))
        {
            return new(CatalogOutcome.NotFound, null);
        }

        if (await FirstTakenBarcodeAsync(s, [spec], cancellationToken) is { } taken)
        {
            return new(CatalogOutcome.BarcodeAssigned, null, taken);
        }

        Guid id = await InsertBarcodeAsync(s, variantId, spec, cancellationToken);
        await Exec(s, "UPDATE merch.tenant_product SET version = version + 1 WHERE id = $1", cancellationToken, productId);
        return new(CatalogOutcome.Done, new BarcodeView(id, spec.Code, spec.Normalized, spec.PackagingUnit, Factor(spec.UnitFactor), spec.IsInternal, true));
    }

    public async Task<CatalogOutcome> RemoveBarcodeAsync(IUnitOfWorkScope scope, Guid productId, Guid variantId, Guid barcodeId, CancellationToken cancellationToken)
    {
        var s = (PostgresUnitOfWorkScope)scope;
        if (!await VariantOfAsync(s, productId, variantId, cancellationToken))
        {
            return CatalogOutcome.NotFound;
        }

        await using NpgsqlCommand cmd = s.Command(
            "UPDATE merch.tenant_product_barcode SET active = false, removed_at = now() WHERE id = $1 AND variant_id = $2 AND active");
        cmd.Parameters.Add(new NpgsqlParameter { Value = barcodeId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = variantId });
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return CatalogOutcome.NotFound;   // unknown, another variant's, or already removed
        }

        await Exec(s, "UPDATE merch.tenant_product SET version = version + 1 WHERE id = $1", cancellationToken, productId);
        return CatalogOutcome.Done;
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static async Task<Guid> InsertVariantAsync(PostgresUnitOfWorkScope s, Guid productId, VariantSpec v, CancellationToken ct)
    {
        Guid id = Guid.CreateVersion7();
        await using (NpgsqlCommand cmd = s.Command(
                         "INSERT INTO merch.tenant_variant (tenant_id, id, product_id, sku, axis_values) VALUES (core.current_tenant(), $1, $2, $3, $4)"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = id });
            cmd.Parameters.Add(new NpgsqlParameter { Value = productId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = v.Sku });
            cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(v.AxisValues), NpgsqlDbType = NpgsqlDbType.Jsonb });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (BarcodeSpec b in v.Barcodes)
        {
            await InsertBarcodeAsync(s, id, b, ct);
        }

        return id;
    }

    private static async Task<Guid> InsertBarcodeAsync(PostgresUnitOfWorkScope s, Guid variantId, BarcodeSpec b, CancellationToken ct)
    {
        Guid id = Guid.CreateVersion7();
        await Exec(s, "INSERT INTO merch.tenant_product_barcode (tenant_id, id, variant_id, code_normalized, code_original, is_internal, packaging_unit, unit_factor) " +
                      "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7)", ct,
            id, variantId, b.Normalized, b.Code, b.IsInternal, b.PackagingUnit, b.UnitFactor);
        return id;
    }

    /// <summary>CAT-002.1 / AT-028: the product that already holds one of these codes (actively), if any.</summary>
    private static async Task<Guid?> FirstTakenBarcodeAsync(PostgresUnitOfWorkScope s, IEnumerable<BarcodeSpec> barcodes, CancellationToken ct)
    {
        string[] codes = [.. barcodes.Select(b => b.Normalized).Distinct(StringComparer.Ordinal)];
        if (codes.Length == 0)
        {
            return null;
        }

        await using NpgsqlCommand cmd = s.Command(
            "SELECT v.product_id FROM merch.tenant_product_barcode b JOIN merch.tenant_variant v ON v.tenant_id = b.tenant_id AND v.id = b.variant_id " +
            "WHERE b.active AND b.code_normalized = ANY($1) LIMIT 1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = codes });
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    private static async Task<bool> AnySkuTakenAsync(PostgresUnitOfWorkScope s, IEnumerable<string> skus, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = s.Command("SELECT EXISTS (SELECT 1 FROM merch.tenant_variant WHERE sku = ANY($1))");
        cmd.Parameters.Add(new NpgsqlParameter { Value = skus.ToArray() });
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<bool> ProductExistsAsync(PostgresUnitOfWorkScope s, Guid productId, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = s.Command("SELECT EXISTS (SELECT 1 FROM merch.tenant_product WHERE id = $1 AND status <> 'Merged')");
        cmd.Parameters.Add(new NpgsqlParameter { Value = productId });
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<bool> VariantOfAsync(PostgresUnitOfWorkScope s, Guid productId, Guid variantId, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = s.Command("SELECT EXISTS (SELECT 1 FROM merch.tenant_variant WHERE id = $1 AND product_id = $2)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = variantId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = productId });
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<IReadOnlyList<ProductView>> LoadAsync(PostgresUnitOfWorkScope s, Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0)
        {
            return [];
        }

        var products = new List<(Guid Id, string Name, string? Brand, string Unit, string? TaxCode, int TaxRate, string Status, int Version, string[] Axes)>();
        await using (NpgsqlCommand cmd = s.Command(
                         "SELECT id, name, brand, base_unit_code, tax_code, tax_rate_bp, status, version, axes FROM merch.tenant_product WHERE id = ANY($1)"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = ids });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                products.Add((r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                    r.GetInt32(5), r.GetString(6), r.GetInt32(7), r.GetFieldValue<string[]>(8)));
            }
        }

        var variants = new List<(Guid ProductId, Guid Id, string Sku, Dictionary<string, string> Axes, string Status, long? Price)>();
        // CAT-009: the price in force now in the default list (every branch sells from it until per-branch lists exist)
        await using (NpgsqlCommand cmd = s.Command(
                         "SELECT v.product_id, v.id, v.sku, v.axis_values::text, v.status, " +
                         "(SELECT i.unit_price FROM merch.price_list_item i JOIN merch.price_list l ON l.tenant_id = i.tenant_id AND l.id = i.price_list_id " +
                         "AND l.is_default WHERE i.variant_id = v.id AND i.valid @> statement_timestamp()) " +
                         "FROM merch.tenant_variant v WHERE v.product_id = ANY($1) ORDER BY v.sku"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = ids });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                variants.Add((r.GetGuid(0), r.GetGuid(1), r.GetString(2), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(3)) ?? [], r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetInt64(5)));
            }
        }

        var barcodes = new List<(Guid VariantId, BarcodeView View)>();
        await using (NpgsqlCommand cmd = s.Command(
                         "SELECT b.variant_id, b.id, b.code_original, b.code_normalized, b.packaging_unit, b.unit_factor, b.is_internal, b.active " +
                         "FROM merch.tenant_product_barcode b JOIN merch.tenant_variant v ON v.tenant_id = b.tenant_id AND v.id = b.variant_id " +
                         "WHERE v.product_id = ANY($1) ORDER BY b.added_at"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = ids });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                barcodes.Add((r.GetGuid(0), new BarcodeView(r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), Factor(r.GetDecimal(5)),
                    r.GetBoolean(6), r.GetBoolean(7))));
            }
        }

        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return [.. products.OrderBy(p => order[p.Id]).Select(p => new ProductView(p.Id, p.Name, p.Brand, p.Unit, p.TaxCode, p.TaxRate, p.Status,
            [.. variants.Where(v => v.ProductId == p.Id).Select(v => new VariantView(v.Id, v.Sku, v.Axes, v.Status,
                [.. barcodes.Where(b => b.VariantId == v.Id).Select(b => b.View)], v.Price))], p.Version, p.Axes))];
    }

    /// <summary>D07 Quantity: invariant decimal string without trailing zeros ("12", "0.5").</summary>
    private static string Factor(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string EncodeCursor(string name, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{id}|{name}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (string Name, Guid Id)? DecodeCursor(string cursor)
    {
        try
        {
            string b64 = cursor.Replace('-', '+').Replace('_', '/');
            string text = Encoding.UTF8.GetString(Convert.FromBase64String(b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=')));
            int bar = text.IndexOf('|', StringComparison.Ordinal);
            return bar == 36 && Guid.TryParse(text[..36], out Guid id) ? (text[37..], id) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static async Task Exec(PostgresUnitOfWorkScope s, string sql, CancellationToken ct, params object[] args)
    {
        await using NpgsqlCommand cmd = s.Command(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
