using RetailCloud.CustomerIdentity.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-141 / CUS-001 / CUS-002 / TM-043: Iranian mobile numbers in any usual form, one-time codes, public codes without look-alike letters (T51).</summary>
public sealed class CustomerIdentityRulesTests
{
    [Theory]
    [InlineData("09123456789", "+989123456789")]
    [InlineData("+98 912 345 6789", "+989123456789")]
    [InlineData("989123456789", "+989123456789")]
    [InlineData("00989123456789", "+989123456789")]
    [InlineData("۰۹۱۲۳۴۵۶۷۸۹", "+989123456789")]
    [InlineData("٠٩١٢٣٤٥٦٧٨٩", "+989123456789")]
    [InlineData(" 0912-345-6789 ", "+989123456789")]
    public void An_iranian_mobile_is_one_number_whichever_way_it_is_written(string input, string expected)
    {
        Assert.True(PhoneNumber.TryNormalize(input, out string e164));
        Assert.Equal(expected, e164);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("02112345678")]        // a landline
    [InlineData("+14155550123")]       // a foreign number (TM-043: no toll fraud abroad)
    [InlineData("0912345678")]         // too short
    [InlineData("091234567890")]       // too long
    [InlineData("0912x456789")]
    public void Anything_else_is_refused(string? input) => Assert.False(PhoneNumber.TryNormalize(input, out _));

    [Fact]
    public void A_number_is_shown_back_masked() => Assert.Equal("0912***6789", PhoneNumber.Mask("+989123456789"));

    [Fact]
    public void A_one_time_code_is_six_digits_and_varies()
    {
        string[] codes = [.. Enumerable.Range(0, 200).Select(_ => OtpCode.Generate())];
        Assert.All(codes, c => Assert.Matches("^[0-9]{6}$", c));
        Assert.True(codes.Distinct().Count() > 150);
    }

    [Fact]
    public void T51_a_public_code_is_CU_and_eight_crockford_characters_without_look_alikes()
    {
        for (int i = 0; i < 500; i++)
        {
            string code = PublicCode.Generate();
            Assert.True(PublicCode.IsValid(code), code);
            Assert.DoesNotMatch("[ILOU]", code[3..]);
        }

        Assert.False(PublicCode.IsValid("CU-0000000I"));
        Assert.False(PublicCode.IsValid("cu-00000000"));
        Assert.False(PublicCode.IsValid("CU-0000000"));
    }

    [Fact]
    public void The_limits_are_the_srs_ones()
    {
        Assert.Equal((6, 2.0, 5, 60.0, 5, 10), (OtpPolicy.Digits, OtpPolicy.Validity.TotalMinutes, OtpPolicy.MaxAttempts, OtpPolicy.ResendAfter.TotalSeconds,
            OtpPolicy.PerPhonePerHour, OtpPolicy.PerPhonePerDay));
    }
}

/// <summary>US-142 / CUS-005 / CUS-007 / AT-222: the counter QR cannot be altered, and consent choices are well formed.</summary>
public sealed class StoreInviteAndConsentRulesTests
{
    private sealed class Mac(string key) : IContactProtector
    {
        public byte[] Hash(string purpose, string value) => System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(purpose + "\0" + value));

        public byte[] Protect(string value) => throw new NotSupportedException();

        public string Unprotect(byte[] protectedValue) => throw new NotSupportedException();
    }

    [Fact]
    public void AT222_a_token_reads_back_and_any_change_to_the_store_branch_or_expiry_breaks_it()
    {
        var protector = new Mac("k1");
        Guid tenant = Guid.NewGuid(), branch = Guid.NewGuid(), invite = Guid.NewGuid();
        DateTimeOffset exp = DateTimeOffset.UtcNow.AddDays(90);
        string token = StoreInviteToken.Create(protector, tenant, branch, invite, exp);
        Assert.True(StoreInviteToken.TryRead(protector, token, out StoreInviteToken.Parsed? ok));
        Assert.Equal((tenant, branch, invite, exp.ToUnixTimeSeconds()), (ok.TenantId, ok.BranchId, ok.InviteId, ok.ExpiresAt.ToUnixTimeSeconds()));

        // another store's id swapped into the payload: the mac no longer fits (the same payload re-encoded)
        string[] parts = token.Split('.');
        string forged = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/') + new string('=', (4 - parts[1].Length % 4) % 4)))
            .Replace(tenant.ToString("N"), Guid.NewGuid().ToString("N")))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.False(StoreInviteToken.TryRead(protector, $"v1.{forged}.{parts[2]}", out _));
        Assert.False(StoreInviteToken.TryRead(new Mac("k2"), token, out _));          // another key
        Assert.False(StoreInviteToken.TryRead(protector, "v1." + parts[1], out _));   // no mac
        Assert.False(StoreInviteToken.TryRead(protector, "", out _));
        Assert.False(StoreInviteToken.TryRead(protector, null, out _));
        Assert.False(StoreInviteToken.TryRead(protector, token + "x", out _));
    }

    [Fact]
    public void Consent_choices_name_a_known_purpose_and_channel_once_and_carry_a_policy_version()
    {
        ConsentChoice op = new("Operational", "Sms", true);
        Assert.Null(ConsentRules.Problem([op, new("Marketing", "Sms", false), new("Operational", "Bale", true)], "1"));
        Assert.Null(ConsentRules.Problem([], "1"));   // joining with no consent at all is allowed
        Assert.StartsWith("consents", ConsentRules.Problem([op, op], "1"), StringComparison.Ordinal);
        Assert.StartsWith("consents", ConsentRules.Problem([new("Spam", "Sms", true)], "1"), StringComparison.Ordinal);
        Assert.StartsWith("consents", ConsentRules.Problem([new("Operational", "Fax", true)], "1"), StringComparison.Ordinal);
        Assert.StartsWith("policyVersion", ConsentRules.Problem([op], null), StringComparison.Ordinal);
        Assert.StartsWith("policyVersion", ConsentRules.Problem([op], ""), StringComparison.Ordinal);
    }
}
