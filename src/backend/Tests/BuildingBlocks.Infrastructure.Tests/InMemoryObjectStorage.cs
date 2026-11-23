using System.Collections.Concurrent;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.TestSupport;

/// <summary>Object storage for tests: keeps objects in memory and hands out recognisable fake links.</summary>
public sealed class InMemoryObjectStorage : IObjectStorage
{
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();

    public bool Fail { get; set; }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new IOException("storage unavailable");
        }

        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, cancellationToken);
        Objects[key] = ms.ToArray();
    }

    public Uri PresignGet(string key, TimeSpan lifetime, string downloadFileName) =>
        new($"https://storage.test/{key}?expires={(int)lifetime.TotalSeconds}&file={Uri.EscapeDataString(downloadFileName)}");

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        Objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
