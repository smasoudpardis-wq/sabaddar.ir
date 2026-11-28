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
