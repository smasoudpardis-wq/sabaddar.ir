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
}

/// <summary>Handle given to command handlers inside a unit of work. Opaque on purpose: no Commit/Rollback here.</summary>
public interface IUnitOfWorkScope
{
    TenantId TenantId { get; }
}
