using Npgsql;
using RetailCloud.BuildingBlocks.Application;
using RetailCloud.BuildingBlocks.Infrastructure;
using RetailCloud.CustomerIdentity.Application;
using RetailCloud.CustomerIdentity.Infrastructure;

namespace RetailCloud.BuildingBlocks.Infrastructure.Tests;

/// <summary>US-141 / CUS-001 / CUS-002 / DI-060/061 / DI-064: the limits, the blind index and the customer behind a verified number.</summary>
public sealed class CustomerIdentityTests
{
    private static string App => Environment.GetEnvironmentVariable("RC_TEST_PG")!;

    private static readonly AesContactProtector Protector = AesContactProtector.FromBase64(
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

    private static async Task<T> InAsync<T>(Func<IUnitOfWorkScope, CancellationToken, Task<T>> work)
    {
        await using var ds = NpgsqlDataSource.Create(App);
        return await new PostgresUnitOfWork(ds).ExecuteWithoutTenantAsync(null, work);
    }

    private static Task<OtpStart> Start(string phone, string ip, DateTimeOffset at) => InAsync((s, ct) =>
        new PostgresOtpChallenges().StartAsync(s, Protector.Hash("phone", phone), Protector.Hash("ip", ip), Protector.Hash("otp", "123456"), Protector.Protect(phone), at, ct));

    private static string NewPhone() => "+9891" + Random.Shared.Next(10_000_000, 99_999_999);

    [Fact]
    public void The_phone_is_encrypted_and_found_only_by_its_keyed_hash()
    {
        byte[] enc = Protector.Protect("+989123456789");
        Assert.DoesNotContain("9123456789", System.Text.Encoding.UTF8.GetString(enc), StringComparison.Ordinal);
        Assert.Equal("+989123456789", Protector.Unprotect(enc));
        Assert.NotEqual(enc, Protector.Protect("+989123456789"));   // a fresh nonce every time
        Assert.Equal(Protector.Hash("phone", "+989123456789"), Protector.Hash("phone", "+989123456789"));
        Assert.NotEqual(Protector.Hash("phone", "x"), Protector.Hash("ip", "x"));   // a purpose never reads as another
        enc[^1] ^= 1;
        Assert.ThrowsAny<Exception>(() => Protector.Unprotect(enc));   // tampering is detected
    }

    [DbFact]
    public async Task AT120_a_second_code_within_a_minute_is_refused_then_allowed_and_five_an_hour_ten_a_day_per_number()
    {
        string phone = NewPhone(), ip = "ip-" + Guid.NewGuid();
        DateTimeOffset t0 = DateTimeOffset.UtcNow.AddHours(-30);
        Assert.Equal(OtpStartOutcome.Started, (await Start(phone, ip, t0)).Outcome);
        OtpStart soon = await Start(phone, ip, t0.AddSeconds(30));
        Assert.Equal(OtpStartOutcome.TooSoon, soon.Outcome);
        Assert.InRange(soon.RetryAfter.TotalSeconds, 29, 31);

        // five in the hour, one every minute; the sixth waits for the first to leave the hour
        for (int i = 1; i < 5; i++) Assert.Equal(OtpStartOutcome.Started, (await Start(phone, ip, t0.AddSeconds(61 * i))).Outcome);
        OtpStart sixth = await Start(phone, ip, t0.AddMinutes(6));
        Assert.Equal(OtpStartOutcome.PhoneLimit, sixth.Outcome);
        Assert.InRange(sixth.RetryAfter.TotalMinutes, 53, 55);

        // after the hour five more are possible, the tenth of the day is the last
        for (int i = 0; i < 5; i++) Assert.Equal(OtpStartOutcome.Started, (await Start(phone, ip, t0.AddMinutes(70 + 2 * i))).Outcome);
        Assert.Equal(OtpStartOutcome.PhoneLimit, (await Start(phone, ip, t0.AddMinutes(130))).Outcome);
    }

    [DbFact]
    public async Task A_single_address_asking_for_many_numbers_is_stopped_at_thirty_an_hour()
    {
        string ip = "ip-" + Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow.AddHours(-3);
        for (int i = 0; i < OtpPolicy.PerAddressPerHour; i++) Assert.Equal(OtpStartOutcome.Started, (await Start(NewPhone(), ip, now)).Outcome);
        Assert.Equal(OtpStartOutcome.AddressLimit, (await Start(NewPhone(), ip, now.AddSeconds(5))).Outcome);
        Assert.Equal(OtpStartOutcome.Started, (await Start(NewPhone(), "other-" + ip, now.AddSeconds(5))).Outcome);
    }

    [DbFact]
    public async Task Five_wrong_tries_end_the_challenge_and_a_verified_one_is_single_use()
    {
        OtpStart start = await Start(NewPhone(), "ip-" + Guid.NewGuid(), DateTimeOffset.UtcNow);
        var repo = new PostgresOtpChallenges();
        int left = 0;
        for (int i = 0; i < 5; i++) left = await InAsync((s, ct) => repo.FailAttemptAsync(s, start.ChallengeId, ct));
        Assert.Equal(0, left);
        Assert.Equal("Failed", (await InAsync((s, ct) => repo.LockAsync(s, start.ChallengeId, ct)))!.Status);

        OtpStart another = await Start(NewPhone(), "ip-" + Guid.NewGuid(), DateTimeOffset.UtcNow);
        await InAsync(async (s, ct) => { await repo.VerifiedAsync(s, another.ChallengeId, ct); return 0; });
        await Assert.ThrowsAnyAsync<PostgresException>(() => InAsync((s, ct) => repo.FailAttemptAsync(s, another.ChallengeId, ct)));
    }

    [DbFact]
    public async Task DI060_one_number_is_one_customer_with_one_public_code_and_a_rotated_code_is_never_reissued()
    {
        string phone = NewPhone();
        var customers = new PostgresCustomers(Protector);
        CustomerView first = await InAsync((s, ct) => customers.FindOrCreateAsync(s, phone, DateTimeOffset.UtcNow, ct));
        CustomerView again = await InAsync((s, ct) => customers.FindOrCreateAsync(s, phone, DateTimeOffset.UtcNow, ct));
        Assert.True(first.IsNew);
        Assert.False(again.IsNew);
        Assert.Equal((first.Id, first.PublicCode), (again.Id, again.PublicCode));
        Assert.True(PublicCode.IsValid(first.PublicCode));
        Assert.Equal(PhoneNumber.Mask(phone), first.MaskedPhone);

        CustomerView rotated = (await InAsync((s, ct) => customers.RotateCodeAsync(s, first.Id, ct)))!;
        Assert.NotEqual(first.PublicCode, rotated.PublicCode);
        Assert.Equal(rotated.PublicCode, (await InAsync((s, ct) => customers.GetAsync(s, first.Id, ct)))!.PublicCode);
        // the registry keeps the retired code: it can be issued to nobody again (DI-061)
        long retired = await InAsync(async (s, ct) =>
        {
            await using NpgsqlCommand cmd = ((PostgresUnitOfWorkScope)s).Command("SELECT count(*) FROM cid.public_code_registry WHERE public_code = $1 AND retired_at IS NOT NULL");
            cmd.Parameters.Add(new NpgsqlParameter { Value = first.PublicCode });
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        });
        Assert.Equal(1, retired);
    }
}
