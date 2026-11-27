using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Platform.Application;

/// <summary>INT-001: a partner's read key as the store sees it; the secret is never kept, only shown once at creation.</summary>
public sealed record IntegrationKeyView(Guid Id, string Name, string Prefix, IReadOnlyList<string> Scopes, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt);

/// <summary>What a key opens, found by its public prefix without a store context.</summary>
public sealed record IntegrationKeyTarget(Guid TenantId, Guid KeyId, byte[] SecretHash, IReadOnlyList<string> Scopes);

public interface IIntegrationKeys
{
    Task<IntegrationKeyView> CreateAsync(IUnitOfWorkScope scope, string prefix, byte[] secretHash, string name, IReadOnlyList<string> scopes,
        DateTimeOffset expiresAt, Guid createdBy, CancellationToken cancellationToken);

    Task<IReadOnlyList<IntegrationKeyView>> ListAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);

    /// <summary>False when the key is unknown or already revoked.</summary>
    Task<bool> RevokeAsync(IUnitOfWorkScope scope, Guid keyId, Guid revokedBy, CancellationToken cancellationToken);

    /// <summary>Without a store context (platform.integration_key_target): the key in force with this prefix.</summary>
    Task<IntegrationKeyTarget?> FindAsync(IUnitOfWorkScope scope, string prefix, CancellationToken cancellationToken);

    Task TouchAsync(IUnitOfWorkScope scope, Guid keyId, CancellationToken cancellationToken);
}

/// <summary>INT-001: the key format <c>rck_&lt;prefix&gt;_&lt;secret&gt;</c> and its rules.</summary>
public static class IntegrationKeyRules
{
    public static readonly IReadOnlyList<string> Scopes = ["catalog.read", "inventory.read"];

    public const int MaxDays = 365;

    /// <summary>A new key: a 10-character public prefix and a 256-bit secret; returns the full key and the secret's SHA-256.</summary>
    public static (string Prefix, string Key, byte[] SecretHash) New()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        string prefix = new([.. System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(alphabet, 10)]);
        string secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (prefix, $"rck_{prefix}_{secret}", Hash(secret));
    }

    public static byte[] Hash(string secret) => System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(secret));

    /// <summary>Splits a presented key; null when it is not shaped like one.</summary>
    public static (string Prefix, string Secret)? Parse(string? key)
    {
        if (key is not { Length: > 20 and < 80 } || !key.StartsWith("rck_", StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = key.Split('_', 3);
        return parts.Length == 3 && parts[1].Length == 10 && parts[1].All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
               && parts[2].Length >= 40 && parts[2].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? (parts[1], parts[2])
            : null;
    }
}
