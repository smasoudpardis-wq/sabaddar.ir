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
builder.Services.AddHostedService<OutboxRelayService>();

await builder.Build().RunAsync();
