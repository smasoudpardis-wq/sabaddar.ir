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
            "WITH m AS (INSERT INTO iam.tenant_membership (tenant_id, user_id) VALUES (core.current_tenant(), $1) " +
            "ON CONFLICT (tenant_id, user_id) DO NOTHING RETURNING user_id) " +
            "INSERT INTO iam.role_assignment (tenant_id, id, user_id, role, scope_type) " +
            "SELECT core.current_tenant(), $2, user_id, 'TenantOwner', 'Tenant' FROM m");
        cmd.Parameters.Add(new NpgsqlParameter { Value = integrationEvent.Payload.GetProperty("ownerUserId").GetGuid() });
        cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
