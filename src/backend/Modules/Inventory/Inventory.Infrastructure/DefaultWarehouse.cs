using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;

namespace RetailCloud.Inventory.Infrastructure;

/// <summary>
/// TEN-001.1 / TEN-002: every branch has a sellable warehouse — W1 for the first branch (platform.tenant-registered),
/// W-&lt;branch code&gt; for each later branch (platform.branch-created).
/// </summary>
public sealed class DefaultWarehouseConsumer : IIntegrationEventConsumer
{
    public string Consumer => "inventory.default-warehouse";

    public bool CanHandle(string eventType, int eventVersion) =>
        eventVersion == 1 && eventType is "platform.tenant-registered" or "platform.branch-created";

    public async Task HandleAsync(IUnitOfWorkScope scope, IntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        bool first = integrationEvent.EventType == "platform.tenant-registered";
        Guid branchId = integrationEvent.Payload.GetProperty(first ? "firstBranchId" : "branchId").GetGuid();
        string code = first ? "W1" : "W-" + integrationEvent.Payload.GetProperty("code").GetString();
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "INSERT INTO inv.warehouse (tenant_id, id, branch_id, code, kind) VALUES (core.current_tenant(), $1, $2, $3, 'Sellable') " +
            "ON CONFLICT (tenant_id, code) DO NOTHING");
        cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = branchId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = code });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class WarehouseSetupStep : ITenantSetupStep
{
    public string Name => "warehouse";

    public async Task<bool> IsCompleteAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("SELECT EXISTS (SELECT 1 FROM inv.warehouse WHERE kind = 'Sellable')");
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }
}

public sealed class WarehouseScopeLookup : IScopeLookup
{
    public ScopeType Type => ScopeType.Warehouse;

    public async Task<Guid?> BranchOfAsync(IUnitOfWorkScope scope, Guid id, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command("SELECT branch_id FROM inv.warehouse WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        return await cmd.ExecuteScalarAsync(cancellationToken) as Guid?;
    }
}
