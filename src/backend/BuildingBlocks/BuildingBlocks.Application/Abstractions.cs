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

/// <summary>A tenant the current user is an active member of, with the user's role assignments there (D06 §6).</summary>
public sealed record MembershipInfo(TenantId TenantId, string TenantName, IReadOnlyList<RoleGrant> Assignments)
{
    public IReadOnlyList<string> Roles => [.. Assignments.Select(a => a.Role).Distinct()];
}

/// <summary>Port implemented by IdentityAccess: maps a validated IdP identity to an internal user (ADR-003).</summary>
public interface IUserDirectory
{
    Task<(UserId Id, string DisplayName)?> FindActiveUserAsync(string issuer, string subject, CancellationToken cancellationToken);

    Task<IReadOnlyList<MembershipInfo>> ActiveMembershipsAsync(UserId user, CancellationToken cancellationToken);

    /// <summary>
    /// Self-service sign-up (UC-10): returns the active user for (issuer, subject), creating it on first use. A
    /// Disabled/Invited user is not reactivated: returns null.
    /// </summary>
    Task<(UserId Id, string DisplayName)?> EnsureUserAsync(string issuer, string subject, string displayName, CancellationToken cancellationToken);
}

/// <summary>
/// One provisioning step of a new tenant (TEN-001.1), owned by the module that performs it. The API composes the
/// steps into the setup status the onboarding wizard polls.
/// </summary>
public interface ITenantSetupStep
{
    string Name { get; }

    Task<bool> IsCompleteAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);
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

/// <summary>Integration event envelope (D03 §7.1). <see cref="EventId"/> is the outbox row id (UUIDv7).</summary>
public sealed record IntegrationEvent(
    Guid EventId,
    string EventType,
    int EventVersion,
    TenantId TenantId,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    string? CausationId,
    string Producer,
    Guid AggregateId,
    System.Text.Json.JsonElement Payload);

/// <summary>
/// Transactional outbox (ADR-013, US-005): the event is written in the producer's unit of work, so it exists if and
/// only if the business change committed. <paramref name="eventType"/> must start with the producer's lower-case name.
/// </summary>
public interface IOutbox
{
    Task<Guid> AddAsync(IUnitOfWorkScope scope, string producer, string eventType, int eventVersion, Guid aggregateId,
        object payload, string? causationId, CancellationToken cancellationToken);
}

/// <summary>
/// A consumer of integration events, run by the Workers relay inside its own unit of work together with the inbox row
/// (D03 §7.1 rule 4): effects and dedup commit together. Must tolerate out-of-order delivery (D04 §5.5).
/// </summary>
public interface IIntegrationEventConsumer
{
    /// <summary>Stable consumer name; part of the inbox key. Never rename a deployed consumer.</summary>
    string Consumer { get; }

    bool CanHandle(string eventType, int eventVersion);

    Task HandleAsync(IUnitOfWorkScope scope, IntegrationEvent integrationEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves a branch/warehouse/register id of the active tenant (RLS) to a <see cref="ScopeRef"/> with its branch, or
/// null if it does not exist there. One implementation per owning module (Platform: branch, register; Inventory: warehouse).
/// </summary>
public interface IScopeLookup
{
    ScopeType Type { get; }

    /// <summary>The branch the object belongs to (for a branch: itself), or null if unknown in this tenant.</summary>
    Task<Guid?> BranchOfAsync(IUnitOfWorkScope scope, Guid id, CancellationToken cancellationToken);
}

/// <summary>A registered POS device recognised from its device secret (TEN-002.1, ABAC-04).</summary>
public sealed record TerminalInfo(Guid TerminalId, Guid BranchId);

/// <summary>Resolves the X-RC-Device secret to an Active terminal of the tenant, or null (unknown, revoked, other tenant).</summary>
public interface ITerminalDirectory
{
    Task<TerminalInfo?> FindActiveAsync(TenantId tenant, string deviceSecret, CancellationToken cancellationToken);
}
