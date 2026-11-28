using System.Text.Json;
using Npgsql;
using RetailCloud.Accounting.Infrastructure;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;

namespace RetailCloud.Api.Tests;

/// <summary>
/// The chart of accounts, the role mapping and the open periods of a test tenant, as tenant registration sets them up
/// (TEN-001.1, run by the Workers' role): needed wherever a business document posts its entry in the same transaction.
/// </summary>
internal static class TestAccounting
{
    public static async Task SetUpAsync(string appConnectionString, Guid tenant)
    {
        await using var worker = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(appConnectionString) { Username = "rc_worker_test", Password = "rc_worker_test" }.ToString());
        await new PostgresUnitOfWork(worker).ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
        {
            await new TenantAccountingSetupConsumer().HandleAsync(s, new IntegrationEvent(Guid.CreateVersion7(), "platform.tenant-registered", 1,
                new TenantId(tenant), DateTimeOffset.UtcNow, null, null, "platform", tenant, JsonSerializer.SerializeToElement(new
                {
                    industryCode = "supermarket", timeZone = "Asia/Tehran", registeredAt = DateTimeOffset.UtcNow,
                })), ct);
            return 0;
        });
    }
}
