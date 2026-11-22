using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Workers;

namespace RetailCloud.BuildingBlocks.Infrastructure.Tests;

/// <summary>
/// US-005 / ADR-013 / D03 §7.1 / TM-042: transactional outbox, relay with tenant context, inbox dedup, retries,
/// dead-letter, competing relays, ordering, retention, LISTEN/NOTIFY wake-up. The producer connects as rc_app, the relay
/// as rc_worker — the production roles.
/// </summary>
public sealed class OutboxTests
{
    private static string AppCs => Environment.GetEnvironmentVariable("RC_TEST_PG")!;

    private static string WorkerCs => new NpgsqlConnectionStringBuilder(AppCs) { Username = "rc_worker_test", Password = "rc_worker_test" }.ToString();

    private static readonly NpgsqlDataSource AppDs = NpgsqlDataSource.Create(AppCs ?? "Host=unused");
    private static readonly NpgsqlDataSource WorkerDs = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("RC_TEST_PG") is null ? "Host=unused" : WorkerCs);
    private static readonly PostgresUnitOfWork App = new(AppDs), Worker = new(WorkerDs);
    private static readonly PostgresOutbox Outbox = new();

    /// <summary>Records deliveries; can fail its first N calls; captures the DB tenant context seen inside.</summary>
    private sealed class Recorder(string consumer, string eventType = "sales.test-happened", int failFirst = 0) : IIntegrationEventConsumer
    {
        private int _calls;
        public ConcurrentQueue<(IntegrationEvent Event, Guid DbTenant)> Seen { get; } = new();

        public string Consumer => consumer;

        public bool CanHandle(string type, int version) => type == eventType && version == 1;

        public async Task HandleAsync(IUnitOfWorkScope scope, IntegrationEvent e, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) <= failFirst)
            {
                throw new InvalidOperationException("transient");
            }

            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)scope).Command("SELECT core.current_tenant()");
            Seen.Enqueue((e, (Guid)(await c.ExecuteScalarAsync(ct))!));
        }
    }

    private static OutboxRelay Relay(OutboxRelayOptions? o = null, params IIntegrationEventConsumer[] consumers) =>
        new(Worker, consumers, NullLogger<OutboxRelay>.Instance, o);

    private static async Task<TenantId> NewTenant()
    {
        var t = new TenantId(Guid.CreateVersion7());
        await App.ExecuteAsync(new FixedTenantContext(t), async (s, ct) =>
        {
            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)s).Command("INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'outbox', 'general')");
            c.Parameters.Add(new NpgsqlParameter { Value = t.Value });
            return await c.ExecuteNonQueryAsync(ct);
        });
        return t;
    }

    private static Task<Guid> Emit(TenantId t, Guid? aggregate = null, string amount = "1100000", string type = "sales.test-happened") =>
        App.ExecuteAsync(new FixedTenantContext(t), (s, ct) =>
            Outbox.AddAsync(s, "Sales", type, 1, aggregate ?? Guid.CreateVersion7(), new { saleId = Guid.Empty, amount }, "cmd-1", ct));

    private static Task<T> AsWorker<T>(TenantId t, string sql, Func<NpgsqlCommand, Task<T>> run) =>
        Worker.ExecuteAsync(new FixedTenantContext(t), async (s, ct) =>
        {
            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)s).Command(sql);
            return await run(c);
        });

    private static Task<long> Count(TenantId t, string where) =>
        AsWorker(t, $"SELECT count(*) FROM core.outbox WHERE {where}", async c => (long)(await c.ExecuteScalarAsync())!);

    private static async Task Drain(OutboxRelay relay)
    {
        for (int i = 0; i < 50 && await relay.RunOnceAsync(CancellationToken.None) > 0; i++)
        {
        }
    }

    [DbFact]
    public async Task An_event_exists_only_if_the_producers_transaction_commits()
    {
        TenantId t = await NewTenant();
        await Assert.ThrowsAsync<InvalidOperationException>(() => App.ExecuteAsync<int>(new FixedTenantContext(t), async (s, ct) =>
        {
            await Outbox.AddAsync(s, "Sales", "sales.test-happened", 1, Guid.CreateVersion7(), new { }, null, ct);
            throw new InvalidOperationException("business rule failed after the event was added");
        }));
        Assert.Equal(0, await Count(t, "true"));
        await Emit(t);
        Assert.Equal(1, await Count(t, "true"));
    }

    [DbFact]
    public async Task Relay_delivers_the_envelope_in_the_events_tenant_context_and_marks_it_published()
    {
        TenantId t = await NewTenant();
        Guid aggregate = Guid.CreateVersion7();
        Guid id = await Emit(t, aggregate);
        var rec = new Recorder("test.o2");
        await Drain(Relay(null, rec));

        (IntegrationEvent e, Guid dbTenant) = Assert.Single(rec.Seen, x => x.Event.EventId == id);
        Assert.Equal(t, e.TenantId);
        Assert.Equal(t.Value, dbTenant); // the consumer runs with SET LOCAL app.tenant_id of the event (TM-042)
        Assert.Equal(("Sales", "sales.test-happened", 1, aggregate, "cmd-1"), (e.Producer, e.EventType, e.EventVersion, e.AggregateId, e.CausationId));
        Assert.Equal("1100000", e.Payload.GetProperty("amount").GetString()); // amounts as integer strings (D03 §7.1)
        Assert.Equal(1, await Count(t, "published_at IS NOT NULL"));
        Assert.Equal(1L, await AsWorker(t, "SELECT count(*) FROM core.inbox WHERE consumer = 'test.o2'", async c => (long)(await c.ExecuteScalarAsync())!));
    }

    [DbFact]
    public async Task Redelivery_after_a_crash_does_not_run_a_consumer_twice()
    {
        TenantId t = await NewTenant();
        Guid id = await Emit(t);
        var rec = new Recorder("test.o3");
        await Drain(Relay(null, rec));
        // simulate a relay crash after the consumer committed but before published_at was written
        await AsWorker(t, "UPDATE core.outbox SET published_at = NULL", c => c.ExecuteNonQueryAsync());
        await Drain(Relay(null, rec));

        Assert.Single(rec.Seen, x => x.Event.EventId == id);
        Assert.Equal(1, await Count(t, "published_at IS NOT NULL"));
    }

    [DbFact]
    public async Task A_failing_consumer_is_retried_with_backoff_without_repeating_the_others()
    {
        TenantId t = await NewTenant();
        Guid id = await Emit(t);
        var ok = new Recorder("test.o4-ok");
        var flaky = new Recorder("test.o4-flaky", failFirst: 2);
        OutboxRelay relay = Relay(null, ok, flaky);

        await relay.RunOnceAsync(CancellationToken.None);
        (int attempts, string error, bool future) = await AsWorker(t, "SELECT attempts, last_error, next_attempt_at > now() FROM core.outbox", async c =>
        {
            await using NpgsqlDataReader r = await c.ExecuteReaderAsync();
            await r.ReadAsync();
            return (r.GetInt32(0), r.GetString(1), r.GetBoolean(2));
        });
        Assert.Equal(1, attempts);
        Assert.Contains("test.o4-flaky", error, StringComparison.Ordinal);
        Assert.True(future);
        Assert.Equal(0, await Count(t, "published_at IS NOT NULL"));

        for (int i = 0; i < 3 && await Count(t, "published_at IS NULL") > 0; i++)
        {
            await AsWorker(t, "UPDATE core.outbox SET next_attempt_at = now()", c => c.ExecuteNonQueryAsync());
            await relay.RunOnceAsync(CancellationToken.None);
        }

        Assert.Equal(1, await Count(t, "published_at IS NOT NULL AND last_error IS NULL"));
        Assert.Single(ok.Seen, x => x.Event.EventId == id);
        Assert.Single(flaky.Seen, x => x.Event.EventId == id);
    }

    [DbFact]
    public async Task After_max_attempts_a_message_is_dead_lettered_and_left_alone()
    {
        TenantId t = await NewTenant();
        await Emit(t);
        OutboxRelay relay = Relay(new OutboxRelayOptions { MaxAttempts = 3 }, new Recorder("test.o5", failFirst: int.MaxValue));
        for (int i = 0; i < 5; i++)
        {
            await AsWorker(t, "UPDATE core.outbox SET next_attempt_at = now() WHERE dead_at IS NULL", c => c.ExecuteNonQueryAsync());
            await relay.RunOnceAsync(CancellationToken.None);
        }

        Assert.Equal(1, await Count(t, "dead_at IS NOT NULL AND attempts = 3 AND published_at IS NULL"));
        long dead = await Worker.ExecuteWithoutTenantAsync(null, async (s, ct) =>
        {
            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)s).Command("SELECT dead FROM core.outbox_stats() WHERE producer = 'Sales'");
            return (long)(await c.ExecuteScalarAsync(ct))!;
        });
        Assert.True(dead >= 1);
    }

    [DbFact]
    public async Task Competing_relays_deliver_each_message_exactly_once()
    {
        TenantId[] tenants = [await NewTenant(), await NewTenant(), await NewTenant()];
        var ids = new HashSet<Guid>();
        foreach (TenantId t in tenants)
        {
            for (int i = 0; i < 100; i++)
            {
                ids.Add(await Emit(t));
            }
        }

        var rec = new Recorder("test.o6");
        OutboxRelay[] relays = [.. Enumerable.Range(0, 4).Select(_ => Relay(new OutboxRelayOptions { BatchSize = 25 }, rec))];
        await Task.WhenAll(relays.Select(r => Task.Run(() => Drain(r))));

        var mine = rec.Seen.Where(x => ids.Contains(x.Event.EventId)).ToList();
        Assert.Equal(300, mine.Count);
        Assert.Equal(300, mine.Select(x => x.Event.EventId).Distinct().Count());
        Assert.All(mine, x => Assert.Equal(x.Event.TenantId.Value, x.DbTenant));
        foreach (TenantId t in tenants)
        {
            Assert.Equal(100, await Count(t, "published_at IS NOT NULL"));
        }
    }

    [DbFact]
    public async Task One_relay_delivers_an_aggregates_events_in_emission_order()
    {
        TenantId t = await NewTenant();
        Guid aggregate = Guid.CreateVersion7();
        var emitted = new List<Guid>();
        for (int i = 0; i < 20; i++)
        {
            emitted.Add(await Emit(t, aggregate));
        }

        var rec = new Recorder("test.o7");
        await Drain(Relay(null, rec));
        Assert.Equal(emitted, rec.Seen.Where(x => x.Event.AggregateId == aggregate).Select(x => x.Event.EventId).ToList());
    }

    [DbFact]
    public async Task A_module_cannot_emit_another_modules_events()
    {
        TenantId t = await NewTenant();
        await Assert.ThrowsAsync<ArgumentException>(() => Emit(t, type: "tax.invoice-requested"));
    }

    [DbFact]
    public async Task Retention_purges_old_published_messages_only()
    {
        TenantId t = await NewTenant();
        await Emit(t);
        await Emit(t);
        await Drain(Relay(null, new Recorder("test.o9")));
        await Emit(t); // still pending
        await AsWorker(t, "UPDATE core.outbox SET published_at = now() - interval '8 days' WHERE id = (SELECT id FROM core.outbox ORDER BY id LIMIT 1)", c => c.ExecuteNonQueryAsync());

        await new MessagingRetention(Worker).PurgeAsync(CancellationToken.None);
        Assert.Equal(2, await Count(t, "true"));
        Assert.Equal(1, await Count(t, "published_at IS NULL"));
    }

    [DbFact]
    public async Task The_service_wakes_up_on_notify_well_before_its_idle_poll()
    {
        TenantId t = await NewTenant();
        var rec = new Recorder("test.o10");
        OutboxRelay relay = Relay(null, rec);
        var service = new OutboxRelayService(WorkerDs, relay, new MessagingRetention(Worker), Worker, NullLogger<OutboxRelayService>.Instance);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            await Task.Delay(3500); // let the idle poll back off to its 2 s maximum
            Guid id = await Emit(t);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!rec.Seen.Any(x => x.Event.EventId == id) && sw.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(20);
            }

            Assert.Contains(rec.Seen, x => x.Event.EventId == id);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"delivered after {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            await cts.CancelAsync();
            await service.StopAsync(CancellationToken.None);
        }
    }
}
