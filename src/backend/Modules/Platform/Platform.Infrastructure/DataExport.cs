using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Platform.Application;

namespace RetailCloud.Platform.Infrastructure;

public sealed class PostgresExportJobs(IOutbox outbox) : IExportJobs
{
    private const string Columns = "id, status, categories, period_from, period_to, row_count, created_at, completed_at, expires_at, object_key, error";

    public async Task<ExportJobView> CreateAsync(IUnitOfWorkScope scope, Guid requestedBy, ExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var s = (PostgresUnitOfWorkScope)scope;
        Guid id = Guid.CreateVersion7();
        await using (NpgsqlCommand cmd = s.Command(
                         "INSERT INTO platform.export_job (tenant_id, id, requested_by, categories, period_from, period_to) " +
                         "VALUES (core.current_tenant(), $1, $2, $3, $4, $5)"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = id });
            cmd.Parameters.Add(new NpgsqlParameter { Value = requestedBy });
            cmd.Parameters.Add(new NpgsqlParameter { Value = request.Categories.ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)request.From ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)request.To ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await outbox.AddAsync(scope, "Platform", "platform.export-requested", 1, id, new { exportId = id }, causationId: null, cancellationToken);
        return (await GetAsync(scope, id, cancellationToken))!;
    }

    public async Task<ExportJobView?> GetAsync(IUnitOfWorkScope scope, Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command($"SELECT {Columns} FROM platform.export_job WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? Read(r) : null;
    }

    public async Task<bool> HasActiveAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "SELECT EXISTS (SELECT 1 FROM platform.export_job WHERE status IN ('Queued','Running'))");
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    internal static ExportJobView Read(NpgsqlDataReader r)
    {
        DateTimeOffset? T(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
        return new ExportJobView(r.GetGuid(0), r.GetString(1), r.GetFieldValue<string[]>(2), T(3), T(4), r.IsDBNull(5) ? null : r.GetInt64(5),
            r.GetFieldValue<DateTimeOffset>(6), T(7), T(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10));
    }
}

/// <summary>
/// TEN-005: writes the tenant's data of the requested categories as one CSV per table plus manifest.json into a ZIP.
/// Reads under the tenant's RLS context only. Encrypted contact data (cid.verified_contact) is never exported; text
/// cells starting with = + - @ are prefixed with ' so spreadsheets do not run them as formulas (AB-18, SEC-T-111).
/// </summary>
public static class TenantDataExporter
{
    private sealed record Table(string File, string Sql, string? DateColumn);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly string[] Notes =
    [
        "UTF-8 CSV with header; timestamps ISO 8601 UTC; amounts in IRR rial; bytea as hex.",
        "Customer phone numbers are not included (encrypted at rest; full numbers need customers.pii.view, TEN-005).",
    ];

    private static readonly Dictionary<string, Table[]> Tables = new()
    {
        ["Products"] =
        [
            new("products/products.csv", "SELECT * FROM merch.tenant_product", null),
            new("products/variants.csv", "SELECT * FROM merch.tenant_variant", null),
            new("products/barcodes.csv", "SELECT * FROM merch.tenant_product_barcode", null),
            new("products/price_lists.csv", "SELECT * FROM merch.price_list", null),
            new("products/price_list_items.csv", "SELECT * FROM merch.price_list_item", null),
        ],
        ["Customers"] =
        [
            new("customers/members.csv", "SELECT m.id, g.public_code, m.status, m.join_source, m.joined_at FROM cid.tenant_customer_membership m " +
                                         "JOIN cid.global_customer g ON g.id = m.global_customer_id", "m.joined_at"),
            new("customers/consents.csv", "SELECT * FROM cid.consent_record", "recorded_at"),
        ],
        ["Sales"] =
        [
            new("sales/shifts.csv", "SELECT * FROM sales.shift", "opened_at"),
            new("sales/sales.csv", "SELECT * FROM sales.sale", "finalized_at"),
            new("sales/sale_lines.csv", "SELECT l.* FROM sales.sale_line l JOIN sales.sale s ON s.tenant_id = l.tenant_id AND s.id = l.sale_id", "s.finalized_at"),
            new("sales/sale_tenders.csv", "SELECT t.* FROM sales.sale_tender t JOIN sales.sale s ON s.tenant_id = t.tenant_id AND s.id = t.sale_id", "s.finalized_at"),
        ],
        ["Inventory"] =
        [
            new("inventory/warehouses.csv", "SELECT * FROM inv.warehouse", null),
            new("inventory/stock_balances.csv", "SELECT * FROM inv.stock_balance", null),
            new("inventory/stock_movements.csv", "SELECT * FROM inv.stock_movement", "effective_at"),
        ],
        ["Journals"] =
        [
            new("journals/accounts.csv", "SELECT * FROM acc.account", null),
            new("journals/fiscal_periods.csv", "SELECT * FROM acc.fiscal_period", null),
            new("journals/entries.csv", "SELECT * FROM acc.journal_entry", "effective_at"),
            new("journals/lines.csv", "SELECT l.* FROM acc.journal_line l JOIN acc.journal_entry e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id", "e.effective_at"),
        ],
        ["Receivables"] =
        [
            new("receivables/credit_accounts.csv", "SELECT * FROM ar.credit_account", null),
            new("receivables/entries.csv", "SELECT * FROM ar.receivable_entry", "recorded_at"),
        ],
        ["TaxInvoices"] =
        [
            new("tax/tax_invoices.csv", "SELECT * FROM tax.tax_invoice", "issued_at"),
        ],
    };

    /// <summary>Writes the ZIP to <paramref name="output"/> and returns the number of data rows.</summary>
    public static async Task<long> WriteAsync(PostgresUnitOfWorkScope scope, Guid tenant, ExportJobView job, Stream output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        long total = 0;
        var files = new List<object>();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string category in job.Categories)
            {
                foreach (Table table in Tables[category])
                {
                    ZipArchiveEntry entry = zip.CreateEntry(table.File, CompressionLevel.Optimal);
                    long rows;
                    await using (Stream es = entry.Open())
                    {
                        rows = await WriteCsvAsync(scope, table, job, es, ct);
                    }

                    total += rows;
                    files.Add(new { file = table.File, category, rows });
                }
            }

            ZipArchiveEntry manifest = zip.CreateEntry("manifest.json");
            await using Stream ms = manifest.Open();
            await JsonSerializer.SerializeAsync(ms, new
            {
                format = "retailcloud-export/1",
                tenantId = tenant,
                exportId = job.Id,
                createdAt = DateTimeOffset.UtcNow,
                categories = job.Categories,
                period = new { from = job.From, to = job.To },
                files,
                notes = Notes,
            }, Indented, ct);
        }

        return total;
    }

    private static async Task<long> WriteCsvAsync(PostgresUnitOfWorkScope scope, Table table, ExportJobView job, Stream output, CancellationToken ct)
    {
        string sql = table.Sql;
        await using NpgsqlCommand cmd = scope.Command(sql);
        if (table.DateColumn is { } col && (job.From is not null || job.To is not null))
        {
            var conds = new List<string>();
            if (job.From is { } from)
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = from });
                conds.Add($"{col} >= ${cmd.Parameters.Count}");
            }

            if (job.To is { } to)
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = to });
                conds.Add($"{col} < ${cmd.Parameters.Count}");
            }

            cmd.CommandText = $"{sql} WHERE {string.Join(" AND ", conds)}";
        }

        await using var w = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16, leaveOpen: true);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        await w.WriteLineAsync(string.Join(",", Enumerable.Range(0, r.FieldCount).Select(i => Csv(r.GetName(i), text: true))));
        long rows = 0;
        var cells = new string[r.FieldCount];
        while (await r.ReadAsync(ct))
        {
            for (int i = 0; i < r.FieldCount; i++)
            {
                cells[i] = r.IsDBNull(i) ? "" : Cell(r, i);
            }

            await w.WriteLineAsync(string.Join(",", cells));
            rows++;
        }

        return rows;
    }

    private static string Cell(NpgsqlDataReader r, int i)
    {
        object v = r.GetValue(i);
        string type = r.GetDataTypeName(i);
        return v switch
        {
            DateTime d => Csv(new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture), false),
            DateTimeOffset d => Csv(d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), false),
            byte[] b => Csv(Convert.ToHexStringLower(b), false),
            bool b => b ? "true" : "false",
            Array a => Csv(JsonSerializer.Serialize(a), text: true),
            IFormattable f => Csv(f.ToString(null, CultureInfo.InvariantCulture), false),
            _ => Csv(v.ToString() ?? "", text: type is "text" or "character varying" or "character" or "jsonb" or "json"),
        };
    }

    /// <summary>RFC 4180 quoting; text that a spreadsheet would read as a formula gets a leading apostrophe (AB-18).</summary>
    public static string Csv(string value, bool text)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (text && value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny("\",\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}

/// <summary>
/// Runs a requested export (platform.export-requested) inside the Workers: builds the ZIP, uploads it under an
/// unguessable tenant key, completes the job (24 h) and audits it. A failure marks the job Failed (the owner may retry);
/// it is not redelivered, so a broken category cannot loop. DET-08 (more than 3 exports a day or over 100 000 rows)
/// is logged and counted for the alert RCExportLarge.
/// </summary>
public sealed partial class DataExportConsumer(IObjectStorage storage, IAuditLog audit, ILogger<DataExportConsumer> logger) : IIntegrationEventConsumer
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private static readonly Meter Meter = new("RetailCloud.Exports");
    private static readonly Counter<long> Large = Meter.CreateCounter<long>("rc_export_large_total");

    public string Consumer => "platform.data-export";

    public bool CanHandle(string eventType, int eventVersion) => eventVersion == 1 && eventType == "platform.export-requested";

    public async Task HandleAsync(IUnitOfWorkScope scope, IntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var s = (PostgresUnitOfWorkScope)scope;
        Guid id = integrationEvent.Payload.GetProperty("exportId").GetGuid();
        ExportJobView? job;
        await using (NpgsqlCommand get = s.Command(
                         "SELECT id, status, categories, period_from, period_to, row_count, created_at, completed_at, expires_at, object_key, error " +
                         "FROM platform.export_job WHERE id = $1 FOR UPDATE"))
        {
            get.Parameters.Add(new NpgsqlParameter { Value = id });
            await using NpgsqlDataReader r = await get.ExecuteReaderAsync(cancellationToken);
            job = await r.ReadAsync(cancellationToken) ? PostgresExportJobs.Read(r) : null;
        }

        if (job is null || job.Status is "Completed" or "Failed")
        {
            return;   // unknown or already done (redelivery)
        }

        Guid tenant = scope.TenantId!.Value.Value;
        string key = ObjectKeys.NewExportKey(tenant);
        string tmp = Path.Combine(Path.GetTempPath(), $"rc-export-{Guid.NewGuid():N}.zip");
        try
        {
            long rows;
            byte[] sha;
            await using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                             FileOptions.DeleteOnClose | FileOptions.Asynchronous))
            {
                rows = await TenantDataExporter.WriteAsync(s, tenant, job, file, cancellationToken);
                file.Position = 0;
                sha = await SHA256.HashDataAsync(file, cancellationToken);
                file.Position = 0;
                await storage.PutAsync(key, file, "application/zip", cancellationToken);
            }

            await Update(s, id, "UPDATE platform.export_job SET status = 'Completed', object_key = $2, row_count = $3, sha256 = $4, " +
                                "completed_at = now(), expires_at = now() + $5 WHERE id = $1", cancellationToken, key, rows, sha, Retention);
            await audit.WriteAsync(scope, new AuditEntry("platform.export.completed", "Export", id.ToString(),
                Details: new { job.Categories, rows }), new AuditActor(null, "Worker", [], null, null, null), cancellationToken);
            await CheckLargeAsync(s, tenant, id, rows, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(logger, e, tenant, id);
            await Update(s, id, "UPDATE platform.export_job SET status = 'Failed', error = $2 WHERE id = $1", cancellationToken, "export-failed");
        }
    }

    private async Task CheckLargeAsync(PostgresUnitOfWorkScope s, Guid tenant, Guid id, long rows, CancellationToken ct)
    {
        await using NpgsqlCommand c = s.Command("SELECT count(*) FROM platform.export_job WHERE created_at > now() - interval '1 day'");
        long today = (long)(await c.ExecuteScalarAsync(ct))!;
        string? reason = rows > 100_000 ? "rows" : today > 3 ? "frequency" : null;
        if (reason is not null)
        {
            Large.Add(1, new KeyValuePair<string, object?>("reason", reason));
            LogLarge(logger, tenant, id, reason, rows, today);
        }
    }

    private static async Task Update(PostgresUnitOfWorkScope s, Guid id, string sql, CancellationToken ct, params object[] args)
    {
        await using NpgsqlCommand cmd = s.Command(sql);
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        foreach (object a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    [LoggerMessage(EventId = 4101, Level = LogLevel.Error, Message = "export.failed tenant={Tenant} export={Export}")]
    private static partial void LogFailed(ILogger logger, Exception e, Guid tenant, Guid export);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Warning, Message = "export.large tenant={Tenant} export={Export} reason={Reason} rows={Rows} today={Today} (DET-08)")]
    private static partial void LogLarge(ILogger logger, Guid tenant, Guid export, string reason, long rows, long today);
}

/// <summary>
/// TEN-005: export files live 24 hours. Hourly, the Workers delete expired files from storage, tenant by tenant under
/// each tenant's own context, and mark the job purged (the API already stops issuing links at expires_at).
/// </summary>
public sealed class ExportRetention(IUnitOfWork unitOfWork, IObjectStorage storage)
{
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> tenants = await unitOfWork.ExecuteWithoutTenantAsync(null, async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT core.list_tenant_ids()");
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            var ids = new List<Guid>();
            while (await r.ReadAsync(ct))
            {
                ids.Add(r.GetGuid(0));
            }

            return (IReadOnlyList<Guid>)ids;
        }, cancellationToken);

        int purged = 0;
        foreach (Guid tenant in tenants)
        {
            purged += await unitOfWork.ExecuteAsync(new FixedTenantContext(new BuildingBlocks.Domain.TenantId(tenant)), async (s, ct) =>
            {
                var due = new List<(Guid Id, string Key)>();
                await using (NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(
                                 "SELECT id, object_key FROM platform.export_job WHERE status = 'Completed' AND purged_at IS NULL AND expires_at <= now() " +
                                 "ORDER BY expires_at LIMIT 100 FOR UPDATE SKIP LOCKED"))
                await using (NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        due.Add((r.GetGuid(0), r.GetString(1)));
                    }
                }

                foreach ((Guid id, string key) in due)
                {
                    await storage.DeleteAsync(key, ct);   // idempotent in S3: deleting a missing key succeeds
                    await using NpgsqlCommand mark = ((PostgresUnitOfWorkScope)s).Command("UPDATE platform.export_job SET purged_at = now() WHERE id = $1");
                    mark.Parameters.Add(new NpgsqlParameter { Value = id });
                    await mark.ExecuteNonQueryAsync(ct);
                }

                return due.Count;
            }, cancellationToken);
        }

        return purged;
    }
}
