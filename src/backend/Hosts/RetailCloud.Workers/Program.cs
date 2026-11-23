using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Workers;

// RetailCloud.Workers (D04 §4): outbox relay, process managers and outbound adapters. Connects as a member of
// rc_worker (NOBYPASSRLS): every unit of work carries a tenant context (TM-042).
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
string connectionString = builder.Configuration.GetConnectionString("Main")
                          ?? throw new InvalidOperationException("ConnectionStrings:Main is required.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<IUnitOfWork, PostgresUnitOfWork>();
builder.Services.AddSingleton<IOutbox, PostgresOutbox>(_ => new PostgresOutbox());
builder.Services.AddSingleton(new OutboxRelayOptions());
builder.Services.AddSingleton<OutboxRelay>();
builder.Services.AddSingleton<MessagingRetention>();
builder.Services.AddSingleton<AuditChainVerifier>();
builder.Services.AddSingleton<IAuditLog, PostgresAuditLog>();
ObjectStorageOptions? storage = builder.Configuration.GetSection("Storage").Get<ObjectStorageOptions>();
builder.Services.AddSingleton<IObjectStorage>(storage is { ServiceUrl.Length: > 0 } ? new S3ObjectStorage(storage) : new UnconfiguredObjectStorage());
builder.Services.AddSingleton<IIntegrationEventConsumer, RetailCloud.Platform.Infrastructure.DataExportConsumer>();   // TEN-005
builder.Services.AddSingleton<RetailCloud.Platform.Infrastructure.ExportRetention>();
// Consumers (IIntegrationEventConsumer), one per module and purpose. Never rename a deployed consumer (inbox key).
builder.Services.AddSingleton<IIntegrationEventConsumer, RetailCloud.IdentityAccess.Infrastructure.OwnerMembershipConsumer>();
builder.Services.AddSingleton<IIntegrationEventConsumer, RetailCloud.Inventory.Infrastructure.DefaultWarehouseConsumer>();
builder.Services.AddSingleton<IIntegrationEventConsumer, RetailCloud.Accounting.Infrastructure.TenantAccountingSetupConsumer>();
builder.Services.AddSingleton<RetailCloud.Accounting.Application.IJournals, RetailCloud.Accounting.Infrastructure.PostgresJournals>();
builder.Services.AddSingleton<IIntegrationEventConsumer, RetailCloud.Accounting.Infrastructure.ShiftCashVarianceConsumer>();   // ACC-004, UC-06
builder.Services.AddSingleton<RetailCloud.Notifications.Infrastructure.NotificationDispatcher>();
if (builder.Configuration.GetValue<bool>("Notifications:DevChannel"))
{
    // Development only: a logging SMS channel; the real SMS/Bale adapters come with the provider contracts (X-03)
    builder.Services.AddSingleton<RetailCloud.Notifications.Application.INotificationChannel, RetailCloud.Notifications.Infrastructure.DevSmsChannel>();
}

builder.Services.AddSingleton<OracleRunner>();
builder.Services.AddSingleton<RetailCloud.Inventory.Infrastructure.StockMaintenance>();
builder.Services.AddHostedService<NightlyService>();
builder.Services.AddHostedService<NotificationService>();
builder.Services.AddHostedService<OutboxRelayService>();

IHost host = builder.Build();
// Operator command (INV-001): `RetailCloud.Workers rebuild-stock <tenantId>` replays the ledger into the balances, then exits.
if (args is ["rebuild-stock", var tenantArg] && Guid.TryParse(tenantArg, out Guid tenantToRebuild))
{
    int corrected = await host.Services.GetRequiredService<RetailCloud.Inventory.Infrastructure.StockMaintenance>().RebuildAsync(tenantToRebuild, CancellationToken.None);
    Console.WriteLine($"balances corrected: {corrected}");
    return;
}

await host.RunAsync();
