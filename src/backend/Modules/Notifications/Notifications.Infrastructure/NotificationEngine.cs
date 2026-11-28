using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Domain;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.Notifications.Application;

namespace RetailCloud.Notifications.Infrastructure;

public sealed class PostgresNotifications : INotifications
{
    public async Task<Guid> EnqueueAsync(IUnitOfWorkScope scope, NotificationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!NotificationPurposes.All.Contains(request.Purpose))
        {
            throw new ArgumentException($"purpose '{request.Purpose}' is not allowed (NOT-007)", nameof(request));
        }

        if (NotificationTemplates.Render(request.TemplateCode, request.TemplateVersion, request.Payload) is null)
        {
            throw new ArgumentException($"template {request.TemplateCode} v{request.TemplateVersion} does not fit the payload", nameof(request));
        }

        var s = (PostgresUnitOfWorkScope)scope;
        if (request.SourceType is not null)
        {
            await using NpgsqlCommand find = s.Command(
                "SELECT id FROM notif.notification WHERE source_type = $1 AND source_id = $2 AND purpose = $3 AND channel = $4");
            find.Parameters.Add(new NpgsqlParameter { Value = request.SourceType });
            find.Parameters.Add(new NpgsqlParameter { Value = request.SourceId!.Value });
            find.Parameters.Add(new NpgsqlParameter { Value = request.Purpose });
            find.Parameters.Add(new NpgsqlParameter { Value = request.Channel });
            if (await find.ExecuteScalarAsync(cancellationToken) is Guid existing)
            {
                return existing;
            }
        }

        Guid id = Guid.CreateVersion7();
        await using NpgsqlCommand cmd = s.Command(
            "INSERT INTO notif.notification (tenant_id, id, purpose, channel, recipient_ref, template_code, template_version, payload, source_type, source_id) " +
            "VALUES (core.current_tenant(), $1, $2, $3, $4, $5, $6, $7, $8, $9)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { Value = request.Purpose });
        cmd.Parameters.Add(new NpgsqlParameter { Value = request.Channel });
        cmd.Parameters.Add(new NpgsqlParameter { Value = request.RecipientRef });
        cmd.Parameters.Add(new NpgsqlParameter { Value = request.TemplateCode });
        cmd.Parameters.Add(new NpgsqlParameter { Value = request.TemplateVersion });
        cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(request.Payload), NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)request.SourceType ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)request.SourceId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    public async Task<bool> RecordReceiptAsync(IUnitOfWorkScope scope, string providerMessageId, bool delivered, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)scope).Command(delivered
            ? "UPDATE notif.notification SET status = 'Delivered', delivered_at = $2 WHERE provider_message_id = $1 AND status = 'Sent'"
            : "UPDATE notif.notification SET status = 'Failed', last_error = 'provider reported not delivered' WHERE provider_message_id = $1 AND status = 'Sent'");
        cmd.Parameters.Add(new NpgsqlParameter { Value = providerMessageId });
        if (delivered)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = at });
        }

        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}

/// <summary>
/// NOT-001/005/006 in the Workers. Per tenant (RLS context): (1) claim due messages — Sending, one more attempt row with
/// key notificationId:channel:attemptNo — and commit; (2) call the channel outside any transaction; (3) record the
/// outcome: Sent; Retrying after the backoff; DeadLettered after the fifth attempt; Undeliverable on a permanent error.
/// A message left in Sending (worker died mid-send) is retried after 10 minutes. Channels not configured (X-03) are left queued.
/// </summary>
public sealed partial class NotificationDispatcher(IUnitOfWork unitOfWork, IEnumerable<INotificationChannel> channels, ILogger<NotificationDispatcher> logger)
{
    public static readonly Meter Meter = new("RetailCloud.Notifications");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("rc_notification_attempts_total");
    private readonly Dictionary<string, INotificationChannel> _channels = channels.ToDictionary(c => c.Channel, StringComparer.Ordinal);

    private sealed record Claim(Guid Id, string Channel, int AttemptNo, string Key, string RecipientRef, string Text);

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken, IReadOnlySet<Guid>? onlyTenants = null)
    {
        IReadOnlyList<Guid> tenants = await unitOfWork.ExecuteWithoutTenantAsync(null, async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT core.list_tenant_ids()");
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            var ids = new List<Guid>();
            while (await r.ReadAsync(ct))
            {
                ids.Add(r.GetGuid(0));
            }

            return (IReadOnlyList<Guid>)ids;
        }, cancellationToken);

        int handled = 0;
        foreach (Guid tenant in tenants.Where(t => onlyTenants is null || onlyTenants.Contains(t)))
        {
            var context = new FixedTenantContext(new TenantId(tenant));
            IReadOnlyList<Claim> claims = await unitOfWork.ExecuteAsync(context, (s, ct) => ClaimAsync((PostgresUnitOfWorkScope)s, ct), cancellationToken);
            foreach (Claim claim in claims)
            {
                ChannelResult result;
                try
                {
                    result = await _channels[claim.Channel].SendAsync(
                        new OutgoingMessage(tenant, claim.Id, claim.Channel, claim.Key, claim.RecipientRef, claim.Text), cancellationToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    result = new ChannelResult(ChannelOutcome.Transient, Error: e.GetType().Name);   // the provider's text may hold PII; keep the type only
                }

                string status = await unitOfWork.ExecuteAsync(context, (s, ct) => RecordAsync((PostgresUnitOfWorkScope)s, claim, result, ct), cancellationToken);
                Outcomes.Add(1, new KeyValuePair<string, object?>("channel", claim.Channel), new KeyValuePair<string, object?>("result", status));
                if (status == "DeadLettered")
                {
                    LogDeadLettered(logger, tenant, claim.Id, claim.Channel);
                }

                handled++;
            }
        }

        return handled;
    }

    private async Task<IReadOnlyList<Claim>> ClaimAsync(PostgresUnitOfWorkScope s, CancellationToken ct)
    {
        // a message stuck in Sending (the worker died between claim and record) gets another chance, or ends
        await using (NpgsqlCommand stale = s.Command(
                         "UPDATE notif.notification SET status = CASE WHEN attempts >= 5 THEN 'DeadLettered' ELSE 'Retrying' END, next_attempt_at = now(), " +
                         "last_error = 'interrupted while sending' WHERE status = 'Sending' AND updated_at < now() - interval '10 minutes'"))
        {
            await stale.ExecuteNonQueryAsync(ct);
        }

        var due = new List<(Guid Id, string Channel, int Attempts, string Recipient, string Code, int Version, string Payload)>();
        await using (NpgsqlCommand cmd = s.Command(
                         "SELECT id, channel, attempts, recipient_ref, template_code, template_version, payload::text FROM notif.notification " +
                         "WHERE status IN ('Queued','Retrying') AND next_attempt_at <= now() AND channel = ANY($1) " +
                         "ORDER BY next_attempt_at LIMIT 20 FOR UPDATE SKIP LOCKED"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = _channels.Keys.ToArray() });
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                due.Add((r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetString(6)));
            }
        }

        var custom = new Dictionary<string, TemplateOverride>(StringComparer.Ordinal);
        await using (NpgsqlCommand ov = s.Command(
                         "SELECT DISTINCT ON (template_code) template_code, store_name, signature FROM notif.template_override ORDER BY template_code, version DESC"))
        await using (NpgsqlDataReader r = await ov.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                custom[r.GetString(0)] = new TemplateOverride(r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
            }
        }

        var claims = new List<Claim>();
        foreach (var n in due)
        {
            int attempt = n.Attempts + 1;
            string key = $"{n.Id}:{n.Channel}:{attempt}";
            string? text = NotificationTemplates.Render(n.Code, n.Version,
                JsonSerializer.Deserialize<Dictionary<string, string>>(n.Payload) ?? [], custom.GetValueOrDefault(n.Code));
            await Exec(s, "UPDATE notif.notification SET status = 'Sending', attempts = $2 WHERE id = $1", ct, n.Id, attempt);
            await Exec(s, "INSERT INTO notif.notification_attempt (tenant_id, notification_id, attempt_no, idempotency_key) VALUES (core.current_tenant(), $1, $2, $3)",
                ct, n.Id, attempt, key);
            claims.Add(new Claim(n.Id, n.Channel, attempt, key, n.Recipient, text ?? ""));
        }

        return claims;
    }

    private static async Task<string> RecordAsync(PostgresUnitOfWorkScope s, Claim claim, ChannelResult result, CancellationToken ct)
    {
        string outcome = result.Outcome.ToString();
        string? error = result.Error is { Length: > 300 } e ? e[..300] : result.Error;
        await Exec(s, "UPDATE notif.notification_attempt SET finished_at = now(), outcome = $3, provider_message_id = $4, error = $5 " +
                      "WHERE notification_id = $1 AND attempt_no = $2", ct,
            claim.Id, claim.AttemptNo, outcome, (object?)result.ProviderMessageId ?? DBNull.Value, (object?)error ?? DBNull.Value);
        TimeSpan? wait = result.Outcome == ChannelOutcome.Transient ? NotificationBackoff.After(claim.AttemptNo) : null;
        string status = result.Outcome switch
        {
            ChannelOutcome.Sent => "Sent",
            ChannelOutcome.Permanent => "Undeliverable",
            _ => wait is null ? "DeadLettered" : "Retrying",
        };
        // every statement uses $1..$4: id, status, provider message id, error; Retrying also moves next_attempt_at
        await Exec(s, "UPDATE notif.notification SET status = $2, provider_message_id = coalesce($3, provider_message_id), last_error = $4, " +
                      "next_attempt_at = CASE WHEN $2 = 'Retrying' THEN now() + $5 ELSE next_attempt_at END WHERE id = $1", ct,
            claim.Id, status, (object?)result.ProviderMessageId ?? DBNull.Value, (object?)(result.Outcome == ChannelOutcome.Sent ? null : error) ?? DBNull.Value,
            wait ?? TimeSpan.Zero);
        return status;
    }

    private static async Task Exec(PostgresUnitOfWorkScope s, string sql, CancellationToken ct, params object[] args)
    {
        await using NpgsqlCommand cmd = s.Command(sql);
        foreach (object a in args)
        {
            cmd.Parameters.Add(a is DBNull ? new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text } : new NpgsqlParameter { Value = a });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    [LoggerMessage(EventId = 5101, Level = LogLevel.Warning, Message = "notification.dead-lettered tenant={Tenant} notification={Notification} channel={Channel}")]
    private static partial void LogDeadLettered(ILogger logger, Guid tenant, Guid notification, string channel);
}

/// <summary>
/// Development only (Notifications:DevChannel=true): "sends" an SMS by logging a masked line and returns a provider id.
/// The real SMS and Bale adapters arrive with the provider contracts (X-03, NOT-003).
/// </summary>
public sealed partial class DevSmsChannel(ILogger<DevSmsChannel> logger) : INotificationChannel
{
    public string Channel => "Sms";

    public Task<ChannelResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        LogSent(logger, message.NotificationId, message.IdempotencyKey, message.Text.Length);
        return Task.FromResult(new ChannelResult(ChannelOutcome.Sent, "dev-" + message.IdempotencyKey));
    }

    [LoggerMessage(EventId = 5102, Level = LogLevel.Information, Message = "dev-sms notification={Notification} key={Key} chars={Chars}")]
    private static partial void LogSent(ILogger logger, Guid notification, string key, int chars);
}
