using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.Notifications.Application;
using RetailCloud.Notifications.Infrastructure;

namespace RetailCloud.BuildingBlocks.Infrastructure.Tests;

/// <summary>US-151 / AT-140/144/145/148: queue in the producer (rc_app), send from the Workers (rc_worker) with retries.</summary>
public sealed class NotificationTests
{
    private static string App => Environment.GetEnvironmentVariable("RC_TEST_PG")!;

    private static string Worker => new NpgsqlConnectionStringBuilder(App) { Username = "rc_worker_test", Password = "rc_worker_test" }.ToString();

    private static string Admin => new NpgsqlConnectionStringBuilder(App)
    {
        Username = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres", Password = Environment.GetEnvironmentVariable("PGPASSWORD") ?? "postgres",
    }.ToString();

    private sealed class ScriptedChannel(params ChannelOutcome[] script) : INotificationChannel
    {
        public List<OutgoingMessage> Sent { get; } = [];

        public string Channel => "Sms";

        public Task<ChannelResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            ChannelOutcome o = script[Math.Min(Sent.Count, script.Length) - 1];
            return Task.FromResult(o == ChannelOutcome.Sent ? new ChannelResult(o, "prov-" + message.IdempotencyKey) : new ChannelResult(o, Error: "busy"));
        }
    }

    private static NotificationRequest Receipt(Guid? source = null) => new("Receipt", "Sms", "customer:42", "receipt", 1,
        new Dictionary<string, string> { ["storeName"] = "فروشگاه", ["amount"] = "1,000", ["link"] = "https://r.example/x" },
        source is null ? null : "sale", source);

    private static async Task<(Guid Tenant, Guid Id)> QueueAsync(NotificationRequest request)
    {
        Guid tenant = Guid.CreateVersion7();
        await using var ds = NpgsqlDataSource.Create(App);
        Guid id = await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
        {
            await using (NpgsqlCommand c = ((PostgresUnitOfWorkScope)s).Command("INSERT INTO platform.tenant (id, legal_name, industry_code) VALUES ($1, 'اعلان', 'general')"))
            {
                c.Parameters.Add(new NpgsqlParameter { Value = tenant });
                await c.ExecuteNonQueryAsync(ct);
            }

            return await new PostgresNotifications().EnqueueAsync(s, request, ct);
        });
        return (tenant, id);
    }

    private static async Task<(string Status, int Attempts, double DueInSeconds, string? Provider)> StateAsync(Guid tenant, Guid id)
    {
        await using var ds = NpgsqlDataSource.Create(Worker);
        return await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)s).Command(
                "SELECT status, attempts, extract(epoch FROM next_attempt_at - now())::float8, provider_message_id FROM notif.notification WHERE id = $1");
            c.Parameters.Add(new NpgsqlParameter { Value = id });
            await using NpgsqlDataReader r = await c.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return (r.GetString(0), r.GetInt32(1), r.GetDouble(2), r.IsDBNull(3) ? null : r.GetString(3));
        });
    }

    private static async Task MakeDueAsync(Guid tenant, Guid id)
    {
        await using var ds = NpgsqlDataSource.Create(Worker);
        await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
        {
            await using NpgsqlCommand c = ((PostgresUnitOfWorkScope)s).Command("UPDATE notif.notification SET next_attempt_at = now() WHERE id = $1");
            c.Parameters.Add(new NpgsqlParameter { Value = id });
            return await c.ExecuteNonQueryAsync(ct);
        });
    }

    private static async Task<int> RunAsync(Guid tenant, params INotificationChannel[] channels)
    {
        await using var ds = NpgsqlDataSource.Create(Worker);
        var dispatcher = new NotificationDispatcher(new PostgresUnitOfWork(ds), channels, NullLogger<NotificationDispatcher>.Instance);
        return await dispatcher.RunOnceAsync(CancellationToken.None, new HashSet<Guid> { tenant });
    }

    [DbFact]
    public async Task A_message_is_sent_once_with_the_attempt_key_and_a_receipt_marks_it_delivered()
    {
        var (tenant, id) = await QueueAsync(Receipt());
        var channel = new ScriptedChannel(ChannelOutcome.Sent);
        Assert.Equal(1, await RunAsync(tenant, channel));
        Assert.Equal(0, await RunAsync(tenant, channel));   // nothing left to send

        OutgoingMessage m = Assert.Single(channel.Sent);
        Assert.Equal($"{id}:Sms:1", m.IdempotencyKey);
        Assert.Equal("customer:42", m.RecipientRef);
        Assert.Contains("1,000", m.Text, StringComparison.Ordinal);
        Assert.Equal(("Sent", 1), ((await StateAsync(tenant, id)).Status, (await StateAsync(tenant, id)).Attempts));

        await using var ds = NpgsqlDataSource.Create(Worker);
        var uow = new PostgresUnitOfWork(ds);
        var notifications = new PostgresNotifications();
        bool first = await uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), (s, ct) => notifications.RecordReceiptAsync(s, $"prov-{id}:Sms:1", true, DateTimeOffset.UtcNow, ct));
        bool again = await uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), (s, ct) => notifications.RecordReceiptAsync(s, $"prov-{id}:Sms:1", false, DateTimeOffset.UtcNow, ct));
        Assert.True(first);
        Assert.False(again);   // Delivered is terminal
        Assert.Equal("Delivered", (await StateAsync(tenant, id)).Status);
    }

    [DbFact]
    public async Task Transient_failures_back_off_and_dead_letter_after_the_fifth_attempt()
    {
        var (tenant, id) = await QueueAsync(Receipt());
        var channel = new ScriptedChannel(ChannelOutcome.Transient);

        await RunAsync(tenant, channel);
        var st = await StateAsync(tenant, id);
        Assert.Equal(("Retrying", 1), (st.Status, st.Attempts));
        Assert.InRange(st.DueInSeconds, 20, 31);
        Assert.Equal(0, await RunAsync(tenant, channel));   // not due yet

        for (int attempt = 2; attempt <= 5; attempt++)
        {
            await MakeDueAsync(tenant, id);
            Assert.Equal(1, await RunAsync(tenant, channel));
        }

        Assert.Equal(("DeadLettered", 5), ((await StateAsync(tenant, id)).Status, (await StateAsync(tenant, id)).Attempts));
        Assert.Equal(Enumerable.Range(1, 5).Select(n => $"{id}:Sms:{n}"), channel.Sent.Select(m => m.IdempotencyKey));
        Assert.Equal(0, await RunAsync(tenant, channel));
    }

    [DbFact]
    public async Task A_permanent_error_is_undeliverable_and_a_retry_then_success_is_sent()
    {
        var (t1, n1) = await QueueAsync(Receipt());
        await RunAsync(t1, new ScriptedChannel(ChannelOutcome.Permanent));
        Assert.Equal("Undeliverable", (await StateAsync(t1, n1)).Status);

        var (t2, n2) = await QueueAsync(Receipt());
        var flaky = new ScriptedChannel(ChannelOutcome.Transient, ChannelOutcome.Sent);
        await RunAsync(t2, flaky);
        await MakeDueAsync(t2, n2);
        await RunAsync(t2, flaky);
        var st = await StateAsync(t2, n2);
        Assert.Equal(("Sent", 2, $"prov-{n2}:Sms:2"), (st.Status, st.Attempts, st.Provider));
    }

    [DbFact]
    public async Task Without_a_configured_channel_the_message_stays_queued()
    {
        var (tenant, id) = await QueueAsync(Receipt());
        Assert.Equal(0, await RunAsync(tenant));
        Assert.Equal(("Queued", 0), ((await StateAsync(tenant, id)).Status, (await StateAsync(tenant, id)).Attempts));
    }

    [DbFact]
    public async Task Enqueue_is_idempotent_per_source_and_refuses_marketing_and_bad_payloads()
    {
        Guid sale = Guid.CreateVersion7();
        var (tenant, id) = await QueueAsync(Receipt(sale));
        await using var ds = NpgsqlDataSource.Create(App);
        var uow = new PostgresUnitOfWork(ds);
        var notifications = new PostgresNotifications();
        Guid again = await uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), (s, ct) => notifications.EnqueueAsync(s, Receipt(sale), ct));
        Assert.Equal(id, again);

        await Assert.ThrowsAsync<ArgumentException>(() => uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)),
            (s, ct) => notifications.EnqueueAsync(s, Receipt() with { Purpose = "Marketing" }, ct)));
        await Assert.ThrowsAsync<ArgumentException>(() => uow.ExecuteAsync(new FixedTenantContext(new TenantId(tenant)),
            (s, ct) => notifications.EnqueueAsync(s, Receipt() with { Payload = new Dictionary<string, string> { ["storeName"] = "x" } }, ct)));
    }

    [DbFact]
    public async Task A_message_left_in_sending_is_retried_after_ten_minutes()
    {
        var (tenant, id) = await QueueAsync(Receipt());
        // the worker "died" after claiming: Sending with one attempt, last touched 11 minutes ago (admin skips the trigger)
        await using (var admin = new NpgsqlConnection(Admin))
        {
            await admin.OpenAsync();
            await using NpgsqlTransaction tx = await admin.BeginTransactionAsync();
            await using (NpgsqlCommand off = new("SET LOCAL session_replication_role = replica", admin, tx))
            {
                await off.ExecuteNonQueryAsync();
            }

            await using NpgsqlCommand c = new(
                "UPDATE notif.notification SET status = 'Sending', attempts = 1, updated_at = now() - interval '11 minutes' WHERE id = $1", admin, tx);
            c.Parameters.Add(new NpgsqlParameter { Value = id });
            Assert.Equal(1, await c.ExecuteNonQueryAsync());
            await tx.CommitAsync();
        }

        var channel = new ScriptedChannel(ChannelOutcome.Sent);
        Assert.Equal(1, await RunAsync(tenant, channel));
        Assert.Equal($"{id}:Sms:2", Assert.Single(channel.Sent).IdempotencyKey);
        Assert.Equal(("Sent", 2), ((await StateAsync(tenant, id)).Status, (await StateAsync(tenant, id)).Attempts));
    }

    [DbFact]
    public async Task The_Workers_send_the_text_with_the_stores_newest_name_and_signature_and_a_later_edit_does_not_touch_the_platform_text()
    {
        var (tenant, id) = await QueueAsync(Receipt());
        await using (var ds = NpgsqlDataSource.Create(App))
        {
            await new PostgresUnitOfWork(ds).ExecuteAsync(new FixedTenantContext(new TenantId(tenant)), async (s, ct) =>
            {
                var overrides = new PostgresTemplateOverrides();
                var first = await overrides.PublishAsync(s, "receipt", 0, "نان‌آور", null, Guid.NewGuid(), ct);
                var second = await overrides.PublishAsync(s, "receipt", 1, "نان‌آور", "با تشکر", Guid.NewGuid(), ct);
                var stale = await overrides.PublishAsync(s, "receipt", 1, "دیگر", null, Guid.NewGuid(), ct);
                Assert.Equal((TemplateChange.Done, TemplateChange.Done, TemplateChange.VersionConflict), (first.Outcome, second.Outcome, stale.Outcome));
                Assert.Equal(2, (await overrides.LatestAsync(s, ct)).Single(v => v.Code == "receipt").Version);
                Assert.Equal(0, (await overrides.LatestAsync(s, ct)).Single(v => v.Code == "due-reminder").Version);
                return 0;
            });
        }

        var channel = new ScriptedChannel(ChannelOutcome.Sent);
        await RunAsync(tenant, channel);
        string text = Assert.Single(channel.Sent).Text;
        Assert.StartsWith("نان‌آور:", text, StringComparison.Ordinal);
        Assert.EndsWith("با تشکر", text, StringComparison.Ordinal);
        Assert.Contains("1,000", text, StringComparison.Ordinal);
    }
}
