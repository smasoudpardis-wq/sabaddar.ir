using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RetailCloud.Notifications.Application;

namespace RetailCloud.Notifications.Infrastructure;

/// <summary>Settings of the SMS provider's service line (US-152): where to post, the key, and the sender line the provider issued.</summary>
public sealed record SmsProviderOptions(string BaseUrl, string ApiKey, string SenderLine);

/// <summary>
/// NOT-001 SMS adapter over a generic HTTPS JSON provider API (<c>POST {BaseUrl}/messages</c> with <c>to</c>, <c>from</c>, <c>text</c>;
/// the reply carries <c>messageId</c>). The idempotency key goes in the <c>Idempotency-Key</c> header so a retry never texts twice
/// (NOT-005). The phone number is resolved only here (DI-064) and never logged or stored in an error. A timeout, 408, 429 or 5xx is
/// transient; any other refusal, or no known number, is permanent. Provider-specific mapping waits for the contract (X-03).
/// </summary>
public sealed class HttpSmsChannel(HttpClient http, SmsProviderOptions options, IRecipientAddressResolver addresses) : INotificationChannel
{
    public string Channel => "Sms";

    public async Task<ChannelResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        string? to = await addresses.ResolveAsync(message.TenantId, message.RecipientRef, cancellationToken);
        if (to is null)
        {
            return new ChannelResult(ChannelOutcome.Permanent, Error: "recipient-has-no-address");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.BaseUrl.TrimEnd('/') + "/"), "messages"))
        {
            Content = JsonContent.Create(new { to, from = options.SenderLine, text = message.Text }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Headers.Add("Idempotency-Key", message.IdempotencyKey);
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("messageId", out JsonElement id) && id.ValueKind == JsonValueKind.String
                    ? new ChannelResult(ChannelOutcome.Sent, id.GetString())
                    : new ChannelResult(ChannelOutcome.Transient, Error: "reply-without-message-id");   // sent or not is unknown: retry (the key makes it safe)
            }

            return response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500
                ? new ChannelResult(ChannelOutcome.Transient, Error: $"provider-{(int)response.StatusCode}")
                : new ChannelResult(ChannelOutcome.Permanent, Error: $"provider-{(int)response.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return new ChannelResult(ChannelOutcome.Transient, Error: e.GetType().Name);
        }
    }
}

/// <summary>Until customer identity (US-141) can hold a verified number, no recipient has an address, so a real send ends Undeliverable honestly.</summary>
public sealed class NoAddressResolver : IRecipientAddressResolver
{
    public Task<string?> ResolveAsync(Guid tenantId, string recipientRef, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
