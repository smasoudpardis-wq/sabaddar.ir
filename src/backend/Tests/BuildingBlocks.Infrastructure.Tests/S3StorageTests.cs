using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.Platform.Application;
using RetailCloud.Platform.Infrastructure;

namespace RetailCloud.BuildingBlocks.Infrastructure.Tests;

/// <summary>Runs only when an S3 endpoint is given (RC_TEST_S3 = service URL; CI runs moto; RC_TEST_S3_STRICT=1 against MinIO).</summary>
public sealed class S3FactAttribute : FactAttribute
{
    public S3FactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RC_TEST_S3")))
        {
            Skip = "RC_TEST_S3 not set (S3-compatible endpoint, e.g. MinIO)";
        }
    }
}

/// <summary>US-016 / SEC-T-066 against a real S3 API: upload, presigned download, expiry and tampering (MinIO validates both).</summary>
public sealed class S3StorageTests
{
    private static readonly HttpClient Http = new();

    private static S3ObjectStorage Storage(string bucket) => new(new ObjectStorageOptions
    {
        ServiceUrl = Environment.GetEnvironmentVariable("RC_TEST_S3")!,
        Bucket = bucket,
        AccessKey = Environment.GetEnvironmentVariable("RC_TEST_S3_KEY") ?? "minio",
        SecretKey = Environment.GetEnvironmentVariable("RC_TEST_S3_SECRET") ?? "minio-dev-password",
    });

    private static bool Strict => Environment.GetEnvironmentVariable("RC_TEST_S3_STRICT") == "1";

    [S3Fact]
    public async Task Upload_creates_the_bucket_and_the_presigned_link_downloads_the_file()
    {
        using S3ObjectStorage s3 = Storage("rc-test-" + Guid.NewGuid().ToString("N")[..12]);   // a bucket that does not exist yet
        string key = ObjectKeys.NewExportKey(Guid.CreateVersion7());
        byte[] content = Encoding.UTF8.GetBytes("export ✓");
        await s3.PutAsync(key, new MemoryStream(content), "application/zip", CancellationToken.None);

        Uri link = s3.PresignGet(key, ObjectKeys.LinkLifetime, "retailcloud-export.zip");
        using HttpResponseMessage r = await Http.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(content, await r.Content.ReadAsByteArrayAsync());
        Assert.Equal("retailcloud-export.zip", r.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        // the object is private: the same URL without the signature is refused
        using HttpResponseMessage bare = await Http.GetAsync(new Uri(link.GetLeftPart(UriPartial.Path)));
        Assert.NotEqual(HttpStatusCode.OK, bare.StatusCode);

        await s3.DeleteAsync(key, CancellationToken.None);
        using HttpResponseMessage gone = await Http.GetAsync(s3.PresignGet(key, ObjectKeys.LinkLifetime, "x.zip"));
        Assert.NotEqual(HttpStatusCode.OK, gone.StatusCode);   // 404, or 403 where the key cannot list the bucket (S3 semantics)
    }

    [S3Fact]
    public async Task A_tampered_or_expired_link_is_refused()
    {
        if (!Strict)
        {
            return;   // moto (local and CI) does not check signatures or expiry; run with RC_TEST_S3_STRICT=1 against MinIO (Staging)
        }

        using S3ObjectStorage s3 = Storage("rc-test-strict");
        string key = ObjectKeys.NewExportKey(Guid.CreateVersion7());
        await s3.PutAsync(key, new MemoryStream([1, 2, 3]), "application/zip", CancellationToken.None);
        string link = s3.PresignGet(key, ObjectKeys.LinkLifetime, "x.zip").ToString();
        int sig = link.IndexOf("X-Amz-Signature=", StringComparison.Ordinal) + "X-Amz-Signature=".Length;
        string forged = link[..sig] + (link[sig] == 'a' ? 'b' : 'a') + link[(sig + 1)..];   // one character of the signature changed
        using HttpResponseMessage tampered = await Http.GetAsync(forged);
        Assert.Equal(HttpStatusCode.Forbidden, tampered.StatusCode);

        Uri shortLived = s3.PresignGet(key, TimeSpan.FromSeconds(1), "x.zip");
        await Task.Delay(TimeSpan.FromSeconds(3));
        using HttpResponseMessage expired = await Http.GetAsync(shortLived);
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
    }

    [S3Fact]
    public async Task A_real_export_lands_in_storage_and_downloads_as_a_valid_zip()
    {
        if (Environment.GetEnvironmentVariable("RC_TEST_PG") is not { } app)
        {
            return;
        }

        Guid tenant = Guid.CreateVersion7();
        await using var appDs = NpgsqlDataSource.Create(app);
        var appUow = new PostgresUnitOfWork(appDs);
        await appUow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)s).Command("INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'S3', 'general')");
            c.Parameters.Add(new NpgsqlParameter { Value = tenant });
            return await c.ExecuteNonQueryAsync(ct);
        });
        ExportJobView job = await appUow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), (s, ct) =>
            new PostgresExportJobs(new PostgresOutbox()).CreateAsync(s, Guid.CreateVersion7(), new ExportRequest(["Products", "Sales"], null, null), ct));

        using S3ObjectStorage s3 = Storage("retailcloud-it");
        string worker = new NpgsqlConnectionStringBuilder(app) { Username = "rc_worker_test", Password = "rc_worker_test" }.ToString();
        await using var workerDs = NpgsqlDataSource.Create(worker);
        var workerUow = new PostgresUnitOfWork(workerDs);
        var evt = new IntegrationEvent(Guid.CreateVersion7(), "platform.export-requested", 1, new TenantId(tenant), DateTimeOffset.UtcNow, null, null,
            "Platform", job.Id, JsonSerializer.SerializeToElement(new { exportId = job.Id }));
        await workerUow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
        {
            await new DataExportConsumer(s3, new PostgresAuditLog(), NullLogger<DataExportConsumer>.Instance).HandleAsync(s, evt, ct);
            return 0;
        });
        ExportJobView done = (await workerUow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), (s, ct) =>
            new PostgresExportJobs(new PostgresOutbox()).GetAsync(s, job.Id, ct)))!;
        Assert.Equal("Completed", done.Status);

        using HttpResponseMessage r = await Http.GetAsync(s3.PresignGet(done.ObjectKey!, ObjectKeys.LinkLifetime, "export.zip"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await r.Content.ReadAsByteArrayAsync()));
        Assert.Contains(zip.Entries, e => e.FullName == "manifest.json");
        Assert.Contains(zip.Entries, e => e.FullName == "sales/sales.csv");
    }
}
