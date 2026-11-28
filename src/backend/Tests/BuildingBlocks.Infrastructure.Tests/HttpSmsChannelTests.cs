using System.Net;
using System.Text;
using RetailCloud.Notifications.Application;
using RetailCloud.Notifications.Infrastructure;

namespace RetailCloud.BuildingBlocks.Infrastructure.Tests;

/// <summary>US-152 / NOT-001 / NOT-005: the SMS adapter carries the idempotency key and tells a retry from a refusal.</summary>
public sealed class HttpSmsChannelTests
{
    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return reply(request);
        }
    }

    private sealed class Book(string? number) : IRecipientAddressResolver
    {
        public Task<string?> ResolveAsync(Guid tenantId, string recipientRef, CancellationToken cancellationToken) => Task.FromResult(number);
    }

    private static readonly OutgoingMessage Message = new(Guid.NewGuid(), Guid.NewGuid(), "Sms", "n1:Sms:1", "customer:42", "فروشگاه: فاکتور شما");

    private static HttpSmsChannel Channel(Fake handler, string? number = "09120000000") =>
        new(new HttpClient(handler), new SmsProviderOptions("https://sms.example/api", "secret-key", "3000123"), new Book(number));

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_send_posts_the_text_with_the_key_and_the_sender_line_and_returns_the_provider_id()
    {
        var handler = new Fake(_ => Json(HttpStatusCode.OK, "{\"messageId\":\"m-77\"}"));
        ChannelResult r = await Channel(handler).SendAsync(Message, CancellationToken.None);
        Assert.Equal((ChannelOutcome.Sent, "m-77"), (r.Outcome, r.ProviderMessageId));
        (HttpRequestMessage req, string body) = Assert.Single(handler.Seen);
        Assert.Equal("https://sms.example/api/messages", req.RequestUri!.ToString());
        Assert.Equal("n1:Sms:1", Assert.Single(req.Headers.GetValues("Idempotency-Key")));
        Assert.Equal("Bearer secret-key", req.Headers.Authorization!.ToString());
        Assert.Contains("\"from\":\"3000123\"", body, StringComparison.Ordinal);
        Assert.Contains("09120000000", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, ChannelOutcome.Transient)]
    [InlineData(HttpStatusCode.BadGateway, ChannelOutcome.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, ChannelOutcome.Transient)]
    [InlineData(HttpStatusCode.RequestTimeout, ChannelOutcome.Transient)]
    [InlineData(HttpStatusCode.BadRequest, ChannelOutcome.Permanent)]
    [InlineData(HttpStatusCode.Unauthorized, ChannelOutcome.Permanent)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ChannelOutcome.Permanent)]
    public async Task A_busy_provider_is_retried_and_a_refusal_is_final(HttpStatusCode status, ChannelOutcome expected)
    {
        ChannelResult r = await Channel(new Fake(_ => Json(status, "{}"))).SendAsync(Message, CancellationToken.None);
        Assert.Equal(expected, r.Outcome);
        Assert.DoesNotContain("0912", r.Error ?? "", StringComparison.Ordinal);   // an error never carries the number
    }

    [Fact]
    public async Task A_reply_without_an_id_is_retried_and_a_network_failure_too()
    {
        Assert.Equal(ChannelOutcome.Transient, (await Channel(new Fake(_ => Json(HttpStatusCode.OK, "{}"))).SendAsync(Message, CancellationToken.None)).Outcome);
        Assert.Equal(ChannelOutcome.Transient, (await Channel(new Fake(_ => throw new HttpRequestException("down"))).SendAsync(Message, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Without_a_known_number_nothing_is_sent_and_the_message_is_undeliverable()
    {
        var handler = new Fake(_ => Json(HttpStatusCode.OK, "{\"messageId\":\"x\"}"));
        ChannelResult r = await Channel(handler, number: null).SendAsync(Message, CancellationToken.None);
        Assert.Equal((ChannelOutcome.Permanent, "recipient-has-no-address"), (r.Outcome, r.Error));
        Assert.Empty(handler.Seen);
    }
}
