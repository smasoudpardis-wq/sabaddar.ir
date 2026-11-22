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
// Consumers (IIntegrationEventConsumer) are registered by modules as they arrive (TaxCompliance, Notifications, ...).
builder.Services.AddHostedService<OutboxRelayService>();

await builder.Build().RunAsync();
