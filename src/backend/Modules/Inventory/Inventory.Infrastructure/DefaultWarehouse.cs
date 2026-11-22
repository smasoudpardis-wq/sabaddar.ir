using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;

namespace RetailCloud.Inventory.Infrastructure;

/// <summary>TEN-001.1: first sellable warehouse W1 in the first branch (consumer of platform.tenant-registered).</summary>
public sealed class DefaultWarehouseConsumer : IIntegrationEventConsumer
{
    public string Consumer => "inventory.default-warehouse";

    public bool CanHandle(string eventType, int eventVersion) => eventType == "platform.tenant-registered" && eventVersion == 1;

    public async Task HandleAsync(IUnitOfWorkScope scope, IntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "INSERT INTO inv.warehouse (tenant_id, id, branch_id, code, kind) VALUES (core.current_tenant(), $1, $2, 'W1', 'Sellable') " +
            "ON CONFLICT (tenant_id, code) DO NOTHING");
        cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = integrationEvent.Payload.GetProperty("firstBranchId").GetGuid() });
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
