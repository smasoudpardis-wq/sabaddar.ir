using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;

namespace RetailCloud.IdentityAccess.Infrastructure;

/// <summary>TEN-001.1: the registering user becomes TenantOwner of the new tenant (consumer of platform.tenant-registered).</summary>
public sealed class OwnerMembershipConsumer : IIntegrationEventConsumer
{
    public string Consumer => "identity.owner-membership";

    public bool CanHandle(string eventType, int eventVersion) => eventType == "platform.tenant-registered" && eventVersion == 1;

    public async Task HandleAsync(IUnitOfWorkScope scope, IntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "INSERT INTO iam.tenant_membership (tenant_id, user_id, roles) VALUES (core.current_tenant(), $1, '{TenantOwner}') " +
            "ON CONFLICT (tenant_id, user_id) DO NOTHING");
        cmd.Parameters.Add(new NpgsqlParameter { Value = integrationEvent.Payload.GetProperty("ownerUserId").GetGuid() });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
