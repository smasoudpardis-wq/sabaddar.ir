using System.Diagnostics.Metrics;
using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;

namespace RetailCloud.Workers;

/// <summary>
/// Runs the outbox relay (ADR-013): LISTEN rc_outbox wakes it at commit; polling (200 ms, doubling to 2 s when idle)
/// is the safety net. Several Worker pods may run it: FOR UPDATE SKIP LOCKED gives each message to one of them.
/// Also publishes the outbox gauges of D11 and runs retention hourly.
/// </summary>
public sealed partial class OutboxRelayService(
    NpgsqlDataSource dataSource,
    OutboxRelay relay,
    MessagingRetention retention,
    IUnitOfWork unitOfWork,
    ILogger<OutboxRelayService> logger) : BackgroundService
{
    private static readonly TimeSpan MinIdle = TimeSpan.FromMilliseconds(200), MaxIdle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetentionEvery = TimeSpan.FromHours(1);
    private volatile IReadOnlyList<(string Producer, long Pending, double Oldest, long Dead)> _stats = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        OutboxRelay.Meter.CreateObservableGauge("rc_outbox_pending", () => _stats.Select(s => new Measurement<long>(s.Pending, Tag(s.Producer))));
        OutboxRelay.Meter.CreateObservableGauge("rc_outbox_oldest_pending_seconds", () => _stats.Select(s => new Measurement<double>(s.Oldest, Tag(s.Producer))));
        OutboxRelay.Meter.CreateObservableGauge("rc_outbox_dead", () => _stats.Select(s => new Measurement<long>(s.Dead, Tag(s.Producer))));

        DateTimeOffset nextRetention = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using NpgsqlConnection listen = await dataSource.OpenConnectionAsync(stoppingToken);
                await using (var cmd = new NpgsqlCommand("LISTEN rc_outbox", listen))
                {
                    await cmd.ExecuteNonQueryAsync(stoppingToken);
                }

                TimeSpan idle = MinIdle;
                while (!stoppingToken.IsCancellationRequested)
                {
                    int handled = await relay.RunOnceAsync(stoppingToken);
                    _stats = await ReadStatsAsync(stoppingToken);
                    if (DateTimeOffset.UtcNow >= nextRetention)
                    {
                        await retention.PurgeAsync(stoppingToken);
                        nextRetention = DateTimeOffset.UtcNow + RetentionEvery;
                    }

                    if (handled > 0)
                    {
                        idle = MinIdle;
                        continue;
                    }

                    await listen.WaitAsync(idle, stoppingToken); // returns early on NOTIFY
                    idle = TimeSpan.FromTicks(Math.Min(idle.Ticks * 2, MaxIdle.Ticks));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogRelayError(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private Task<IReadOnlyList<(string, long, double, long)>> ReadStatsAsync(CancellationToken ct) =>
        unitOfWork.ExecuteWithoutTenantAsync<IReadOnlyList<(string, long, double, long)>>(null, async (s, c) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT producer, pending, oldest_pending_seconds, dead FROM core.outbox_stats()");
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(c);
            var list = new List<(string, long, double, long)>();
            while (await r.ReadAsync(c))
            {
                list.Add((r.GetString(0), r.GetInt64(1), r.GetDouble(2), r.GetInt64(3)));
            }

            return list;
        }, ct);

    private static KeyValuePair<string, object?> Tag(string producer) => new("producer", producer);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox relay loop failed; restarting in 2 s")]
    private static partial void LogRelayError(ILogger logger, Exception exception);
}
