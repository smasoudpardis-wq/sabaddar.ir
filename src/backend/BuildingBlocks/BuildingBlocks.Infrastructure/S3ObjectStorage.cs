using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.BuildingBlocks.Infrastructure;

/// <summary>Storage settings (section "Storage"): an S3-compatible endpoint, MinIO in every environment (D11).</summary>
public sealed class ObjectStorageOptions
{
    public string ServiceUrl { get; set; } = "";

    public string Bucket { get; set; } = "retailcloud";

    public string AccessKey { get; set; } = "";

    public string SecretKey { get; set; } = "";

    public string Region { get; set; } = "us-east-1";
}

/// <summary>S3 API client for MinIO: path-style addressing, private bucket, presigned GETs only.</summary>
public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly ObjectStorageOptions _options;

    public S3ObjectStorage(ObjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _client = new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), new AmazonS3Config
        {
            ServiceURL = options.ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = options.Region,
        });
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        long start = content.Position;
        PutObjectRequest Request() => new()
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        };
        try
        {
            await _client.PutObjectAsync(Request(), cancellationToken);
        }
        catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchBucket")
        {
            // a fresh environment: create the private bucket once, then retry
            await _client.PutBucketAsync(new PutBucketRequest { BucketName = _options.Bucket }, cancellationToken);
            content.Position = start;
            await _client.PutObjectAsync(Request(), cancellationToken);
        }
    }

    public Uri PresignGet(string key, TimeSpan lifetime, string downloadFileName)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = _options.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTP : Protocol.HTTPS,
        };
        request.ResponseHeaderOverrides.ContentDisposition = $"attachment; filename=\"{downloadFileName}\"";
        return new Uri(_client.GetPreSignedURL(request));
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await _client.DeleteObjectAsync(_options.Bucket, key, cancellationToken);

    public void Dispose() => _client.Dispose();
}

/// <summary>Used when no "Storage" section is configured: the host starts, and only file operations fail.</summary>
public sealed class UnconfiguredObjectStorage : IObjectStorage
{
    private static InvalidOperationException Missing() => new("Object storage is not configured (section 'Storage').");

    public Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) => throw Missing();

    public Uri PresignGet(string key, TimeSpan lifetime, string downloadFileName) => throw Missing();

    public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw Missing();
}
