using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.BuildingBlocks.Application;

/// <summary>
/// The tenant and user of the current request or job. Populated only from the authenticated session (BR-001) or,
/// for background work, from the job's explicit tenant id (D03 §8). Never from request input.
/// </summary>
public interface ITenantContext
{
    TenantId TenantId { get; }

    UserId? UserId { get; }
}

public sealed record FixedTenantContext(TenantId TenantId, UserId? UserId = null) : ITenantContext;

/// <summary>
/// Runs work in exactly one database transaction shared by every module touched (D04 §5.3, ADR-004).
/// Tenant context is applied transaction-locally before any statement runs.
/// </summary>
public interface IUnitOfWork
{
    Task<T> ExecuteAsync<T>(ITenantContext tenant, Func<IUnitOfWorkScope, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Work that runs before a tenant is chosen (identity lookup, own memberships). No tenant context is set, so every
    /// tenant-owned table is invisible (RLS); only global tables and narrow SECURITY DEFINER functions are usable.
    /// </summary>
    Task<T> ExecuteWithoutTenantAsync<T>(UserId? user, Func<IUnitOfWorkScope, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default);
}

/// <summary>Handle given to command handlers inside a unit of work. Opaque on purpose: no Commit/Rollback here.</summary>
public interface IUnitOfWorkScope
{
    /// <summary>The tenant of this unit of work, or null for <see cref="IUnitOfWork.ExecuteWithoutTenantAsync{T}"/>.</summary>
    TenantId? TenantId { get; }
}

/// <summary>A tenant the current user is an active member of, with the user's roles there (D02 §2).</summary>
public sealed record MembershipInfo(TenantId TenantId, string TenantName, IReadOnlyList<string> Roles);

/// <summary>Port implemented by IdentityAccess: maps a validated IdP identity to an internal user (ADR-003).</summary>
public interface IUserDirectory
{
    Task<(UserId Id, string DisplayName)?> FindActiveUserAsync(string issuer, string subject, CancellationToken cancellationToken);

    Task<IReadOnlyList<MembershipInfo>> ActiveMembershipsAsync(UserId user, CancellationToken cancellationToken);
}

/// <summary>
/// Idempotency registry (BR-030/031, D03 §6.3). Used inside the unit of work of the command itself, so the record and
/// the business change commit or roll back together; only successes are completed (D-34).
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Inserts an InProgress record for (tenant, key). If a concurrent transaction holds the same key, waits for it
    /// (bounded): then replays its committed response, or claims the key if it rolled back.
    /// </summary>
    Task<IdempotencyClaim> ClaimAsync(IUnitOfWorkScope scope, string key, string operation, byte[] payloadHash,
        CancellationToken cancellationToken);

    Task CompleteAsync(IUnitOfWorkScope scope, string key, int responseStatus, string responseJson, CancellationToken cancellationToken);
}

/// <summary>Outcome of <see cref="IIdempotencyStore.ClaimAsync"/>.</summary>
public abstract record IdempotencyClaim
{
    private IdempotencyClaim()
    {
    }

    /// <summary>First use of the key: run the command, then complete.</summary>
    public sealed record Claimed : IdempotencyClaim;

    /// <summary>Same key and same payload already succeeded: return this response unchanged.</summary>
    public sealed record Replay(int Status, string ResponseJson) : IdempotencyClaim;

    /// <summary>Same key with another payload or operation: 409 idempotency-key-reused.</summary>
    public sealed record Reused : IdempotencyClaim;

    /// <summary>A concurrent request with the key did not finish within the wait: 409 in-progress.</summary>
    public sealed record Busy : IdempotencyClaim;
}
