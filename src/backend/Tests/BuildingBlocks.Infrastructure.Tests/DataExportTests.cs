using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.Platform.Application;
using RetailCloud.Platform.Infrastructure;
using RetailCloud.TestSupport;

namespace RetailCloud.BuildingBlocks.Infrastructure.Tests;

/// <summary>US-016 / TEN-005 / AT-006 / SEC-T-066: the export job, run as the Workers run it (rc_worker under RLS).</summary>
[Collection(WorkerJobs.Name)]   // the relay service runs tenant-wide jobs (export retention) that these tests also exercise
public sealed class DataExportTests
{
    private static string App => Environment.GetEnvironmentVariable("RC_TEST_PG")!;

    private static string Worker => new NpgsqlConnectionStringBuilder(App) { Username = "rc_worker_test", Password = "rc_worker_test" }.ToString();

    private static async Task<Guid> Tenant(PostgresUnitOfWork uow, string productName)
    {
        Guid t = Guid.CreateVersion7();
        await uow.ExecuteAsync(new FixedTenantContext(new TenantId(t)), async (s, ct) =>
        {
            await using NpgsqlBatch b = ((PostgresUnitOfWorkScope)s).Connection.CreateBatch();
            b.Transaction = ((PostgresUnitOfWorkScope)s).Transaction;
            b.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'صادرات', 'general')")
                { Parameters = { new() { Value = t } } });
            b.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO merch.tenant_product (tenant_id, id, name, base_unit_code) VALUES ($1, $2, $3, 'EA')")
                { Parameters = { new() { Value = t }, new() { Value = Guid.CreateVersion7() }, new() { Value = productName } } });
            return await b.ExecuteNonQueryAsync(ct);
        });
        return t;
    }

    private static async Task<(ExportJobView Job, InMemoryObjectStorage Storage)> RunExport(Guid tenant, string[] categories, bool storageFails = false)
    {
        await using var appDs = NpgsqlDataSource.Create(App);
        ExportJobView job = await new PostgresUnitOfWork(appDs).ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), (s, ct) =>
            new PostgresExportJobs(new PostgresOutbox()).CreateAsync(s, Guid.CreateVersion7(), new ExportRequest(categories, null, null), ct));

        var storage = new InMemoryObjectStorage { Fail = storageFails };
        var consumer = new DataExportConsumer(storage, new PostgresAuditLog(), NullLogger<DataExportConsumer>.Instance);
        await using var workerDs = NpgsqlDataSource.Create(Worker);
        var workerUow = new PostgresUnitOfWork(workerDs);
        var evt = new IntegrationEvent(Guid.CreateVersion7(), "platform.export-requested", 1, new TenantId(tenant), DateTimeOffset.UtcNow, null, null,
            "platform", job.Id, JsonSerializer.SerializeToElement(new { exportId = job.Id }));
        await workerUow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) => { await consumer.HandleAsync(s, evt, ct); return 0; });
        ExportJobView done = (await workerUow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), (s, ct) =>
            new PostgresExportJobs(new PostgresOutbox()).GetAsync(s, job.Id, ct)))!;
        return (done, storage);
    }

    private static string Entry(ZipArchive zip, string name)
    {
        using var r = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    [DbFact]
    public async Task The_export_holds_only_this_tenants_data_under_an_unguessable_tenant_key()
    {
        await using var ds = NpgsqlDataSource.Create(App);
        var uow = new PostgresUnitOfWork(ds);
        Guid mine = await Tenant(uow, "=HYPERLINK(\"http://evil.example\",\"click\")");
        Guid other = await Tenant(uow, "کالای فروشگاه دیگر");

        (ExportJobView job, InMemoryObjectStorage storage) = await RunExport(mine, ["Products", "Customers"]);
        Assert.Equal("Completed", job.Status);
        Assert.Matches($"^tenants/{mine}/exports/[A-Za-z0-9_-]{{43}}\\.zip$", job.ObjectKey);
        Assert.InRange(job.ExpiresAt!.Value - job.CompletedAt!.Value, TimeSpan.FromHours(23.9), TimeSpan.FromHours(24.1));
        Assert.Equal(1, job.RowCount);

        using var zip = new ZipArchive(new MemoryStream(storage.Objects[job.ObjectKey!]));
        string products = Entry(zip, "products/products.csv");
        Assert.DoesNotContain("کالای فروشگاه دیگر", products, StringComparison.Ordinal);       // RLS: never another tenant
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil.example\"\",\"\"click\"\")\"", products, StringComparison.Ordinal);   // AB-18
        Assert.Contains("members.csv", string.Join(",", zip.Entries.Select(e => e.Name)), StringComparison.Ordinal);
        Assert.DoesNotContain("value_enc", string.Join("\n", zip.Entries.Where(e => e.Name.EndsWith(".csv", StringComparison.Ordinal)).Select(e => Entry(zip, e.FullName))), StringComparison.Ordinal);
        using JsonDocument manifest = JsonDocument.Parse(Entry(zip, "manifest.json"));
        Assert.Equal(mine, manifest.RootElement.GetProperty("tenantId").GetGuid());
        Assert.Equal(["Products", "Customers"], manifest.RootElement.GetProperty("categories").EnumerateArray().Select(e => e.GetString()));

        // the other tenant's export has its own key prefix and its own product only
        (ExportJobView otherJob, InMemoryObjectStorage otherStorage) = await RunExport(other, ["Products"]);
        Assert.StartsWith($"tenants/{other}/", otherJob.ObjectKey, StringComparison.Ordinal);
        using var otherZip = new ZipArchive(new MemoryStream(otherStorage.Objects[otherJob.ObjectKey!]));
        Assert.DoesNotContain("HYPERLINK", Entry(otherZip, "products/products.csv"), StringComparison.Ordinal);
    }

    [DbFact]
    public async Task A_storage_failure_fails_the_job_instead_of_retrying_forever()
    {
        await using var ds = NpgsqlDataSource.Create(App);
        Guid t = await Tenant(new PostgresUnitOfWork(ds), "کالا");
        (ExportJobView job, InMemoryObjectStorage storage) = await RunExport(t, ["Products"], storageFails: true);
        Assert.Equal("Failed", job.Status);
        Assert.Null(job.ObjectKey);
        Assert.Empty(storage.Objects);
    }

    [DbFact]
    public async Task Expired_files_are_deleted_from_storage_and_marked_and_fresh_ones_stay()
    {
        await using var ds = NpgsqlDataSource.Create(App);
        Guid t = await Tenant(new PostgresUnitOfWork(ds), "کالا");
        (ExportJobView old, InMemoryObjectStorage storage) = await RunExport(t, ["Products"]);
        (ExportJobView fresh, InMemoryObjectStorage freshStorage) = await RunExport(t, ["Products"]);
        foreach ((string key, byte[] bytes) in freshStorage.Objects)
        {
            storage.Objects[key] = bytes;   // one bucket holding both files
        }

        string admin = new NpgsqlConnectionStringBuilder(App) { Username = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres",
            Password = Environment.GetEnvironmentVariable("PGPASSWORD") }.ToString();
        await using (var a = NpgsqlDataSource.Create(admin))
        {
            await using NpgsqlCommand c = a.CreateCommand("UPDATE platform.export_job SET completed_at = now() - interval '2 days', expires_at = now() - interval '1 day' WHERE id = $1");
            c.Parameters.Add(new NpgsqlParameter { Value = old.Id });
            await c.ExecuteNonQueryAsync();
        }

        await using var workerDs = NpgsqlDataSource.Create(Worker);
        var retention = new ExportRetention(new PostgresUnitOfWork(workerDs), storage);
        Assert.True(await retention.PurgeAsync(CancellationToken.None) >= 1);
        Assert.False(storage.Objects.ContainsKey(old.ObjectKey!));
        Assert.True(storage.Objects.ContainsKey(fresh.ObjectKey!));
        await using (var a = NpgsqlDataSource.Create(admin))
        {
            await using NpgsqlCommand c = a.CreateCommand(
                "SELECT bool_and(CASE WHEN id = $1 THEN purged_at IS NOT NULL ELSE purged_at IS NULL END) FROM platform.export_job WHERE id IN ($1, $2)");
            c.Parameters.Add(new NpgsqlParameter { Value = old.Id });
            c.Parameters.Add(new NpgsqlParameter { Value = fresh.Id });
            Assert.True((bool)(await c.ExecuteScalarAsync())!);
        }
    }

    [Theory]
    [InlineData("=1+2", true, "'=1+2")]
    [InlineData("-5", false, "-5")]                 // numbers keep their sign
    [InlineData("@SUM(A1)", true, "'@SUM(A1)")]
    [InlineData("a,b", true, "\"a,b\"")]
    [InlineData("say \"hi\"", true, "\"say \"\"hi\"\"\"")]
    [InlineData("خط\nدوم", true, "\"خط\nدوم\"")]
    public void Csv_cells_are_quoted_and_formulas_neutralised(string value, bool text, string expected) =>
        Assert.Equal(expected, TenantDataExporter.Csv(value, text));

    [Fact]
    public void S3_links_are_path_style_signed_and_live_five_minutes()
    {
        using var s3 = new S3ObjectStorage(new ObjectStorageOptions
        {
            ServiceUrl = "http://127.0.0.1:9000", Bucket = "retailcloud", AccessKey = "key", SecretKey = "secret",
        });
        string key = ObjectKeys.NewExportKey(Guid.CreateVersion7());
        Uri url = s3.PresignGet(key, ObjectKeys.LinkLifetime, "export.zip");
        Assert.Equal("http", url.Scheme);
        Assert.StartsWith("/retailcloud/tenants/", url.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Expires=300", url.Query, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Signature=", url.Query, StringComparison.Ordinal);
        Assert.NotEqual(ObjectKeys.NewExportKey(Guid.Empty), ObjectKeys.NewExportKey(Guid.Empty));
    }
}

/// <summary>Tests that run tenant-wide Workers jobs must not run in parallel with each other.</summary>
[CollectionDefinition(Name)]
public sealed class WorkerJobs
{
    public const string Name = "worker-jobs";
}
