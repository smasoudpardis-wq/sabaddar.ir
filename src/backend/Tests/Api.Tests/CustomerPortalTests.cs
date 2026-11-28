using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RetailCloud.Api.Tests;

/// <summary>US-141 / CUS-001 / CUS-002 / CUS-002.1 / TM-043: sign in with a phone code, get a short customer token, rotate the public code.</summary>
public sealed class CustomerPortalTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<JsonElement> Body(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync());

    private static string NewPhone() => "09" + Random.Shared.Next(100_000_000, 999_999_999);

    private HttpClient Anonymous(string? ip = null)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forwarded-For", ip ?? $"10.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        return c;
    }

    private async Task<(Guid Id, string Code)> StartAsync(HttpClient c, string phone)
    {
        HttpResponseMessage r = await c.PostAsJsonAsync("/api/v1/c/auth/otp", new { phone });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        JsonElement b = await Body(r);
        Assert.True(b.GetProperty("resendAfter").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddSeconds(50));
        return (b.GetProperty("challengeId").GetGuid(), factory.Otps.Latest["+98" + phone[1..]]);
    }

    [DbFact]
    public async Task AT120_121_a_verified_phone_gets_a_public_code_and_a_ten_minute_token_and_signing_in_again_finds_the_same_customer()
    {
        HttpClient c = Anonymous();
        string phone = NewPhone();
        (Guid challenge, string code) = await StartAsync(c, phone);

        HttpResponseMessage ok = await c.PostAsJsonAsync("/api/v1/c/auth/otp/verify", new { challengeId = challenge, code });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        JsonElement session = await Body(ok);
        Assert.True(session.GetProperty("isNewCustomer").GetBoolean());
        string publicCode = session.GetProperty("publicCode").GetString()!;
        Assert.Matches("^CU-[0-9A-HJKMNP-TV-Z]{8}$", publicCode);
        Assert.InRange((session.GetProperty("expiresAt").GetDateTimeOffset() - DateTimeOffset.UtcNow).TotalMinutes, 9, 10.1);

        HttpClient me = factory.CreateClient();
        me.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        JsonElement mine = await Body(await me.GetAsync("/api/v1/c/me"));
        Assert.Equal(publicCode, mine.GetProperty("publicCode").GetString());
        Assert.Matches(@"^09\d{2}\*\*\*\d{4}$", mine.GetProperty("maskedPhone").GetString()!);
        Assert.DoesNotContain(phone[4..8], await (await me.GetAsync("/api/v1/c/me")).Content.ReadAsStringAsync(), StringComparison.Ordinal);   // the middle of the number is never returned

        // the challenge is single use
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostAsJsonAsync("/api/v1/c/auth/otp/verify", new { challengeId = challenge, code })).StatusCode);
    }

    [DbFact]
    public async Task AT120_a_wrong_code_counts_down_five_tries_then_the_challenge_is_dead_even_for_the_right_code()
    {
        HttpClient c = Anonymous();
        (Guid challenge, string code) = await StartAsync(c, NewPhone());
        string wrong = code == "000000" ? "111111" : "000000";
        for (int left = 4; left >= 0; left--)
        {
            HttpResponseMessage r = await c.PostAsJsonAsync("/api/v1/c/auth/otp/verify", new { challengeId = challenge, code = wrong });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            JsonElement p = await Body(r);
            Assert.Equal(("otp-invalid", left), (p.GetProperty("code").GetString(), p.GetProperty("context").GetProperty("attemptsRemaining").GetInt32()));
        }

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostAsJsonAsync("/api/v1/c/auth/otp/verify", new { challengeId = challenge, code })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostAsJsonAsync("/api/v1/c/auth/otp/verify", new { challengeId = Guid.NewGuid(), code })).StatusCode);   // unknown looks the same
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/c/auth/otp/verify", new { challengeId = challenge, code = "12ab56" })).StatusCode);
    }

    [DbFact]
    public async Task TM043_only_iranian_mobiles_and_the_second_request_inside_a_minute_is_limited_with_Retry_After()
    {
        HttpClient c = Anonymous();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/c/auth/otp", new { phone = "+14155550123" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/c/auth/otp", new { phone = "02112345678" })).StatusCode);

        string phone = NewPhone();
        await StartAsync(c, phone);
        HttpResponseMessage again = await c.PostAsJsonAsync("/api/v1/c/auth/otp", new { phone });
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
        Assert.Equal("otp-rate-limited", (await Body(again)).GetProperty("code").GetString());
        Assert.InRange(int.Parse(again.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture), 1, 60);
    }

    [DbFact]
    public async Task A_failed_send_is_reported_and_the_customer_endpoints_refuse_other_realms_tokens()
    {
        HttpClient c = Anonymous();
        factory.Otps.Fail = true;
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await c.PostAsJsonAsync("/api/v1/c/auth/otp", new { phone = NewPhone() })).StatusCode);
        }
        finally
        {
            factory.Otps.Fail = false;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/c/me")).StatusCode);
        HttpClient merchant = factory.CreateClient();
        merchant.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token("someone"));
        Assert.False((await merchant.GetAsync("/api/v1/c/me")).IsSuccessStatusCode);   // a store user's token is no customer token
        // and a customer token is no merchant token
        (Guid ch, string code) = await StartAsync(Anonymous(), NewPhone());
        JsonElement s = await Body(await Anonymous().PostAsJsonAsync("/api/v1/c/auth/otp/verify", new { challengeId = ch, code }));
        HttpClient asCustomer = factory.CreateClient();
        asCustomer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await asCustomer.GetAsync("/api/v1/me")).StatusCode);
    }

    [DbFact]
    public async Task AT122_rotating_the_public_code_replaces_it_at_once()
    {
        (Guid ch, string code) = await StartAsync(Anonymous(), NewPhone());
        JsonElement s = await Body(await Anonymous().PostAsJsonAsync("/api/v1/c/auth/otp/verify", new { challengeId = ch, code }));
        HttpClient me = factory.CreateClient();
        me.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("accessToken").GetString());
        string old = s.GetProperty("publicCode").GetString()!;
        JsonElement rotated = await Body(await me.PostAsync("/api/v1/c/me/public-code/rotate", null));
        Assert.NotEqual(old, rotated.GetProperty("publicCode").GetString());
        Assert.Equal(rotated.GetProperty("publicCode").GetString(), (await Body(await me.GetAsync("/api/v1/c/me"))).GetProperty("publicCode").GetString());
    }
}
