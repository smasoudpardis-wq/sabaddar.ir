using System.Globalization;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Notifications.Application;

/// <summary>NOT-007: the purposes a notification may have. Marketing is not one of them in the MVP (and the database agrees).</summary>
public static class NotificationPurposes
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { "Receipt", "DueReminder", "Internal" };
}

/// <summary>What to send. <paramref name="RecipientRef"/> is a reference (customer or user), never a phone number (DI-064).</summary>
public sealed record NotificationRequest(string Purpose, string Channel, string RecipientRef, string TemplateCode, int TemplateVersion,
    IReadOnlyDictionary<string, string> Payload, string? SourceType = null, Guid? SourceId = null);

/// <summary>One try handed to a channel adapter; <paramref name="IdempotencyKey"/> = notificationId:channel:attemptNo (NOT-005).</summary>
public sealed record OutgoingMessage(Guid TenantId, Guid NotificationId, string Channel, string IdempotencyKey, string RecipientRef, string Text);

public enum ChannelOutcome
{
    Sent,
    Transient,
    Permanent,
}

public sealed record ChannelResult(ChannelOutcome Outcome, string? ProviderMessageId = null, string? Error = null);

/// <summary>
/// NOT-001: a provider adapter (SMS, Bale). It resolves the recipient's address itself at send time and passes the
/// idempotency key to the provider. Delivery receipts arrive later and are recorded with <see cref="INotifications.RecordReceiptAsync"/>.
/// </summary>
public interface INotificationChannel
{
    string Channel { get; }

    Task<ChannelResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken);
}

public interface INotifications
{
    /// <summary>Queues in the caller's unit of work; a second request for the same (source, purpose, channel) returns the first.</summary>
    Task<Guid> EnqueueAsync(IUnitOfWorkScope scope, NotificationRequest request, CancellationToken cancellationToken);

    /// <summary>NOT-006 / DI-130: only a provider receipt moves Sent to Delivered (or Failed).</summary>
    Task<bool> RecordReceiptAsync(IUnitOfWorkScope scope, string providerMessageId, bool delivered, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>NOT-005: retry after 30 s, 2 min, 10 min, 1 h, 6 h; after the fifth failed attempt the message is dead-lettered.</summary>
public static class NotificationBackoff
{
    public const int MaxAttempts = 5;

    private static readonly TimeSpan[] Delays =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(6)];

    /// <summary>The wait after failed attempt <paramref name="attemptNo"/> (1-based), or null when no attempt is left.</summary>
    public static TimeSpan? After(int attemptNo) => attemptNo is >= 1 and < MaxAttempts ? Delays[attemptNo - 1] : null;
}

/// <summary>
/// Versioned Persian texts (NOT-009 grows these per tenant). A template uses only its declared fields; a receipt never
/// lists the items (NOT-002).
/// </summary>
public static class NotificationTemplates
{
    private static readonly Dictionary<(string Code, int Version), (string[] Fields, string Text)> All = new()
    {
        [("receipt", 1)] = (["storeName", "amount", "link"], "{storeName}: فاکتور شما به مبلغ {amount} ریال. مشاهده: {link}"),
        [("due-reminder", 1)] = (["storeName", "amount", "dueDate"], "{storeName}: یادآوری پرداخت {amount} ریال تا {dueDate}."),
        [("internal", 1)] = (["text"], "{text}"),
    };

    public static bool Exists(string code, int version) => All.ContainsKey((code, version));

    /// <summary>The text, or null when a declared field is missing or an undeclared one is present.</summary>
    public static string? Render(string code, int version, IReadOnlyDictionary<string, string> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!All.TryGetValue((code, version), out var t) || payload.Count != t.Fields.Length || t.Fields.Any(f => !payload.ContainsKey(f)))
        {
            return null;
        }

        return t.Fields.Aggregate(t.Text, (text, f) => text.Replace("{" + f + "}", payload[f], StringComparison.Ordinal));
    }

    /// <summary>The text with the store's own name and closing signature (NOT-009); null as <see cref="Render(string,int,IReadOnlyDictionary{string,string})"/>.</summary>
    public static string? Render(string code, int version, IReadOnlyDictionary<string, string> payload, TemplateOverride? custom)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (custom is null || (custom.StoreName is null && custom.Signature is null))
        {
            return Render(code, version, payload);
        }

        var fields = new Dictionary<string, string>(payload, StringComparer.Ordinal);
        if (custom.StoreName is not null && fields.ContainsKey("storeName"))
        {
            fields["storeName"] = custom.StoreName;
        }

        string? text = Render(code, version, fields);
        return text is null || custom.Signature is null ? text : text + " " + custom.Signature;
    }

    /// <summary>The platform's text of a template (its fields in braces), for the store to see what it personalises.</summary>
    public static string? Text(string code, int version) => All.TryGetValue((code, version), out var t) ? t.Text : null;

    public static string Amount(long rial) => rial.ToString("#,0", CultureInfo.InvariantCulture);
}

/// <summary>NOT-009: the only parts of an SMS text a store may change: the name it signs with and a closing signature.</summary>
public sealed record TemplateOverride(string? StoreName, string? Signature);

public sealed record TemplateOverrideView(string Code, int Version, string? StoreName, string? Signature, DateTimeOffset? CreatedAt);

public enum TemplateChange
{
    Done,
    VersionConflict,
    NotFound,
}

public interface ITemplateOverrides
{
    /// <summary>The newest version of each customisable template (version 0 = never changed).</summary>
    Task<IReadOnlyList<TemplateOverrideView>> LatestAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken);

    /// <summary>Publishes version <paramref name="expectedVersion"/> + 1; a concurrent publish of the same number loses.</summary>
    Task<(TemplateChange Outcome, TemplateOverrideView? Current)> PublishAsync(IUnitOfWorkScope scope, string code, int expectedVersion,
        string? storeName, string? signature, Guid userId, CancellationToken cancellationToken);
}

public static class SmsTextRules
{
    public static readonly IReadOnlyList<string> Codes = ["receipt", "due-reminder"];

    /// <summary>The first problem, as <c>field: message</c>, or null. An SMS line refuses links in a store's own words (a link is the platform's).</summary>
    public static string? Problem(string? storeName, string? signature)
    {
        foreach ((string field, string? v, int max) in new[] { ("storeName", storeName, 30), ("signature", signature, 40) })
        {
            if (v is null)
            {
                continue;
            }

            if (v.Trim().Length < 1 || v.Trim().Length > max || v.Any(char.IsControl))
            {
                return $"{field}: 1-{max} characters on one line";
            }

            if (v.Contains("http", StringComparison.OrdinalIgnoreCase) || v.Contains("www.", StringComparison.OrdinalIgnoreCase))
            {
                return $"{field}: links are not allowed";
            }
        }

        return storeName is null && signature is null ? "storeName: a name or a signature is required" : null;
    }
}

/// <summary>NOT-001: the SMS adapter asks for the phone number only at send time (DI-064); null = no number is known.</summary>
public interface IRecipientAddressResolver
{
    Task<string?> ResolveAsync(Guid tenantId, string recipientRef, CancellationToken cancellationToken);
}
