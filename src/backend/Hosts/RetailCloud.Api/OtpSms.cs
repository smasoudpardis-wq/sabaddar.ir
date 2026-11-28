using RetailCloud.CustomerIdentity.Application;
using RetailCloud.Notifications.Application;

namespace RetailCloud.Api;

/// <summary>Sends the code through the platform's SMS channel (the same adapter, US-152); the number rides only in the in-memory recipient reference.</summary>
internal sealed class SmsOtpSender(INotificationChannel sms) : IOtpSender
{
    public async Task<bool> SendAsync(string e164, string code, string idempotencyKey, CancellationToken cancellationToken)
    {
        ChannelResult r = await sms.SendAsync(new OutgoingMessage(Guid.Empty, Guid.Empty, "Sms", idempotencyKey, "tel:" + e164,
            $"کد ورود شما به RetailCloud: {code}\nاعتبار: ۲ دقیقه. آن را به کسی نگویید."), cancellationToken);
        return r.Outcome == ChannelOutcome.Sent;
    }
}

/// <summary>Resolves the in-memory <c>tel:</c> reference of an OTP to the number (the SMS adapter asks for it only when sending).</summary>
internal sealed class TelRefResolver : IRecipientAddressResolver
{
    public Task<string?> ResolveAsync(Guid tenantId, string recipientRef, CancellationToken cancellationToken) =>
        Task.FromResult(recipientRef.StartsWith("tel:", StringComparison.Ordinal) ? recipientRef[4..] : null);
}
