using System.Security.Cryptography;

namespace RetailCloud.BuildingBlocks.Application;

/// <summary>
/// Tenant files (exports, invoice PDFs) in object storage (MinIO in production, D11). Objects are never public: a reader
/// gets a presigned URL that lives <see cref="LinkLifetime"/> and is issued only after authorization (D-49, TM-066).
/// </summary>
public interface IObjectStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    Uri PresignGet(string key, TimeSpan lifetime, string downloadFileName);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

public static class ObjectKeys
{
    /// <summary>D-49: presigned links live five minutes.</summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(5);

    /// <summary>TM-066: tenants/&lt;tenant&gt;/exports/&lt;256-bit random&gt;.zip — unguessable, and scoped to the tenant.</summary>
    public static string NewExportKey(Guid tenant) =>
        $"tenants/{tenant}/exports/{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.zip";
}
