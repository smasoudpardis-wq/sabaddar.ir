using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;

namespace RetailCloud.BuildingBlocks.Infrastructure;

/// <summary>Writes envelopes into core.outbox within the caller's transaction (01_core.sql).</summary>
public sealed class PostgresOutbox(JsonSerializerOptions? json = null) : IOutbox
{
    private readonly JsonSerializerOptions _json = json ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);

    public async Task<Guid> AddAsync(IUnitOfWorkScope scope, string producer, string eventType, int eventVersion, Guid aggregateId,
        object payload, string? causationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        if (!eventType.StartsWith(producer.ToLowerInvariant() + ".", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Event type '{eventType}' must start with '{producer.ToLowerInvariant()}.'", nameof(eventType));
        }

        if (scope.TenantId is null)
        {
            throw new InvalidOperationException("Integration events are tenant-scoped; use a unit of work with a tenant.");
        }

        Guid id = Guid.CreateVersion7();
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(
            "INSERT INTO core.outbox (tenant_id, id, producer, event_type, event_version, aggregate_id, payload, correlation_id, causation_id) " +
            "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7, $8)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { Value = producer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = eventType });
        cmd.Parameters.Add(new NpgsqlParameter { Value = eventVersion });
        cmd.Parameters.Add(new NpgsqlParameter { Value = aggregateId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(payload, _json), NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)Activity.Current?.TraceId.ToString() ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)causationId ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }
}

public sealed class OutboxRelayOptions
{
    public int BatchSize { get; init; } = 100;

    public int MaxAttempts { get; init; } = 10;

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Relay (ADR-013, D04 §5.5). For every tenant with due messages (core.outbox_due_tenants), opens a unit of work WITH
/// that tenant's context, locks a batch with FOR UPDATE SKIP LOCKED (several Workers can run), and dispatches each
/// message to every interested handler in a separate unit of work that also inserts the inbox row. A message is marked
/// published only when all its handlers succeeded; otherwise it is retried with exponential backoff and, after
/// <see cref="OutboxRelayOptions.MaxAttempts"/>, dead-lettered. Handlers that already succeeded are skipped on retry
/// through the inbox.
/// </summary>
public sealed partial class OutboxRelay(
    IUnitOfWork unitOfWork,
    IEnumerable<IIntegrationEventConsumer> handlers,
    ILogger<OutboxRelay> logger,
    OutboxRelayOptions? options = null)
{
    public static readonly Meter Meter = new("RetailCloud.Outbox");
    private static readonly Counter<long> Published = Meter.CreateCounter<long>("rc_outbox_published_total");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("rc_outbox_handler_failures_total");
    private static readonly Counter<long> Dead = Meter.CreateCounter<long>("rc_outbox_dead_total");

    private readonly IIntegrationEventConsumer[] _handlers = [.. handlers];
    private readonly OutboxRelayOptions _options = options ?? new OutboxRelayOptions();

    /// <summary>Relays one batch per due tenant. Returns the number of messages handled (published or rescheduled).</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> tenants = await unitOfWork.ExecuteWithoutTenantAsync<IReadOnlyList<Guid>>(null, async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT core.outbox_due_tenants()");
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            var list = new List<Guid>();
            while (await r.ReadAsync(ct))
            {
                list.Add(r.GetGuid(0));
            }

            return list;
        }, cancellationToken);

        int total = 0;
        foreach (Guid tenant in tenants)
        {
            total += await RelayTenantAsync(new TenantId(tenant), cancellationToken);
        }

        return total;
    }

    private Task<int> RelayTenantAsync(TenantId tenant, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(new FixedTenantContext(tenant), async (s, ct) =>
        {
            var scope = (PostgresUnitOfWorkScope)s;
            var batch = new List<(IntegrationEvent Event, int Attempts)>();
            await using (NpgsqlCommand sel = scope.Command(
                             "SELECT id, event_type, event_version, occurred_at, correlation_id, causation_id, producer, aggregate_id, payload::text, attempts " +
                             "FROM core.outbox WHERE published_at IS NULL AND dead_at IS NULL AND next_attempt_at <= now() " +
                             "ORDER BY id FOR UPDATE SKIP LOCKED LIMIT $1"))
            {
                sel.Parameters.Add(new NpgsqlParameter { Value = _options.BatchSize });
                await using NpgsqlDataReader r = await sel.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    using JsonDocument payload = JsonDocument.Parse(r.GetString(8));
                    batch.Add((new IntegrationEvent(r.GetGuid(0), r.GetString(1), r.GetInt32(2), tenant,
                        r.GetFieldValue<DateTimeOffset>(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                        r.GetString(6), r.GetGuid(7), payload.RootElement.Clone()), r.GetInt32(9)));
                }
            }

            foreach ((IntegrationEvent e, int attempts) in batch)
            {
                string? error = await DispatchAsync(e, ct);
                if (error is null)
                {
                    await Exec(scope, "UPDATE core.outbox SET published_at = now(), last_error = NULL WHERE id = $1", ct, e.EventId);
                    Published.Add(1, new KeyValuePair<string, object?>("producer", e.Producer), new KeyValuePair<string, object?>("event_type", e.EventType));
                    continue;
                }

                int next = attempts + 1;
                if (next >= _options.MaxAttempts)
                {
                    await Exec(scope, "UPDATE core.outbox SET attempts = $2, last_error = $3, dead_at = now() WHERE id = $1", ct, e.EventId, next, error);
                    Dead.Add(1, new KeyValuePair<string, object?>("producer", e.Producer), new KeyValuePair<string, object?>("event_type", e.EventType));
                    LogDeadLettered(logger, e.EventId, e.EventType, next, error);
                }
                else
                {
                    await Exec(scope, "UPDATE core.outbox SET attempts = $2, last_error = $3, next_attempt_at = now() + $4 WHERE id = $1", ct,
                        e.EventId, next, error, Backoff(next));
                }
            }

            return batch.Count;
        }, cancellationToken);

    /// <summary>Runs every interested handler once (inbox). Returns null when all succeeded, else the first error.</summary>
    private async Task<string?> DispatchAsync(IntegrationEvent e, CancellationToken cancellationToken)
    {
        string? firstError = null;
        foreach (IIntegrationEventConsumer handler in _handlers.Where(h => h.CanHandle(e.EventType, e.EventVersion)))
        {
            try
            {
                await unitOfWork.ExecuteAsync(new FixedTenantContext(e.TenantId), async (s, ct) =>
                {
                    var scope = (PostgresUnitOfWorkScope)s;
                    await using NpgsqlCommand inbox = scope.Command(
                        "INSERT INTO core.inbox (tenant_id, consumer, event_id) VALUES (core.current_tenant(), $1, $2) ON CONFLICT DO NOTHING");
                    inbox.Parameters.Add(new NpgsqlParameter { Value = handler.Consumer });
                    inbox.Parameters.Add(new NpgsqlParameter { Value = e.EventId });
                    if (await inbox.ExecuteNonQueryAsync(ct) == 1)
                    {
                        await handler.HandleAsync(s, e, ct);
                    }

                    return 0;
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Failures.Add(1, new KeyValuePair<string, object?>("consumer", handler.Consumer), new KeyValuePair<string, object?>("event_type", e.EventType));
                LogConsumerFailed(logger, ex, handler.Consumer, e.EventId, e.EventType);
                firstError ??= $"{handler.Consumer}: {ex.GetType().Name}";
            }
        }

        return firstError;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {EventId} ({EventType}) dead-lettered after {Attempts} attempts: {Error}")]
    private static partial void LogDeadLettered(ILogger logger, Guid eventId, string eventType, int attempts, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer {Consumer} failed for {EventId} ({EventType})")]
    private static partial void LogConsumerFailed(ILogger logger, Exception exception, string consumer, Guid eventId, string eventType);

    /// <summary>2^attempt seconds with ±20 % jitter, capped.</summary>
    private TimeSpan Backoff(int attempt)
    {
        double seconds = Math.Min(Math.Pow(2, attempt), _options.MaxBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(seconds * (0.8 + (System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 401) / 1000.0)));
    }

    private static async Task Exec(PostgresUnitOfWorkScope s, string sql, CancellationToken ct, params object[] args)
    {
        await using NpgsqlCommand cmd = s.Command(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Retention (ADR-013, D-14): per tenant (RLS) delete published outbox messages after 7 days, inbox rows after 30 days
/// and idempotency records after 8 days (≥ 7 days guaranteed). Dead-lettered messages are kept for manual replay.
/// </summary>
public sealed class MessagingRetention(IUnitOfWork unitOfWork)
{
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> tenants = await unitOfWork.ExecuteWithoutTenantAsync<IReadOnlyList<Guid>>(null, async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT core.list_tenant_ids()");
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            var list = new List<Guid>();
            while (await r.ReadAsync(ct))
            {
                list.Add(r.GetGuid(0));
            }

            return list;
        }, cancellationToken);

        int deleted = 0;
        foreach (Guid t in tenants)
        {
            deleted += await unitOfWork.ExecuteAsync(new FixedTenantContext(new TenantId(t)), async (s, ct) =>
            {
                await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command(
                    "WITH o AS (DELETE FROM core.outbox WHERE published_at < now() - interval '7 days' RETURNING 1), " +
                    "i AS (DELETE FROM core.inbox WHERE processed_at < now() - interval '30 days' RETURNING 1), " +
                    "k AS (DELETE FROM core.idempotency_record WHERE created_at < now() - interval '8 days' RETURNING 1) " +
                    "SELECT (SELECT count(*) FROM o) + (SELECT count(*) FROM i) + (SELECT count(*) FROM k)");
                return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
            }, cancellationToken);
        }

        return deleted;
    }
}
