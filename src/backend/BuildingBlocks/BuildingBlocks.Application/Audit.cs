using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RetailCloud.BuildingBlocks.Application;

/// <summary>AUD-001.1 who/where of an audit record, taken from the validated request (never from client input).</summary>
public sealed record AuditActor(Guid? UserId, string Kind, IReadOnlyList<string> Roles, Guid? BranchId, Guid? TerminalId,
    string? ClientIp, Guid? ImpersonatedBy = null)
{
    public static readonly AuditActor System = new(null, "System", [], null, null, null);
}

/// <summary>AUD-001.1 what/why. <see cref="Details"/> holds before/after of non-sensitive fields only (AUD-003).</summary>
public sealed record AuditEntry(string Action, string ResourceType, string? ResourceId, string? Reason = null, object? Details = null);

/// <summary>
/// Audit trail (AUD-001..003, US-031). Written in the caller's unit of work, so the record exists if and only if the
/// change committed. Sequence and hash chain are assigned by the database (80_audit.sql).
/// </summary>
public interface IAuditLog
{
    Task WriteAsync(IUnitOfWorkScope scope, AuditEntry entry, AuditActor actor, CancellationToken cancellationToken);
}

/// <summary>
/// AUD-003: the audit trail never contains OTPs, tokens, passwords, PINs, keys, card numbers or full contact PII.
/// Properties whose name looks like a secret are replaced, and string values shaped like a JWT or a card number are masked.
/// </summary>
public static partial class AuditSanitizer
{
    public const string Redacted = "[redacted]";

    [GeneratedRegex("(pin|otp|token|password|passwd|secret|apikey|api_key|privatekey|private_key|credential|pan|cvv|cardnumber|card_number|mobile|phone|email)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    [GeneratedRegex(@"^eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]+\.|^\d{13,19}$")]
    private static partial Regex SensitiveValue();

    public static string ToJson(object? details, JsonSerializerOptions? options = null)
    {
        JsonNode? node = details is null ? new JsonObject() : JsonSerializer.SerializeToNode(details, options);
        return Clean(node)?.ToJsonString() ?? "{}";
    }

    private static JsonNode? Clean(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (string key in o.Select(p => p.Key).ToList())
                {
                    o[key] = SensitiveName().IsMatch(key) ? JsonValue.Create(Redacted) : Clean(o[key]?.DeepClone());
                }

                return o;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    a[i] = Clean(a[i]?.DeepClone());
                }

                return a;
            case JsonValue v when v.TryGetValue(out string? s) && SensitiveValue().IsMatch(s):
                return JsonValue.Create(Redacted);
            default:
                return node;
        }
    }
}

/// <summary>US-032 filters; <paramref name="Action"/> ending in '.' matches as a prefix. Newest first, keyset on seq.</summary>
public sealed record AuditQuery(DateTimeOffset? From, DateTimeOffset? To, string? Action, Guid? ActorId, string? ResourceType,
    string? ResourceId, long? BeforeSeq, int Limit);

public sealed record AuditRecordView(long Seq, DateTimeOffset OccurredAt, string ActorKind, Guid? ActorId, IReadOnlyList<string> ActorRoles,
    Guid? ImpersonatedBy, string Action, string ResourceType, string? ResourceId, string? Reason, Guid? BranchId, Guid? TerminalId,
    string? ClientIp, string DetailsJson);

/// <summary>Read side of the tenant's audit trail (RLS limits it to the active tenant).</summary>
public interface IAuditSearch
{
    Task<IReadOnlyList<AuditRecordView>> SearchAsync(IUnitOfWorkScope scope, AuditQuery query, CancellationToken cancellationToken);
}
