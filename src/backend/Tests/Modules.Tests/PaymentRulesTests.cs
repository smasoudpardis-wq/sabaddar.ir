using RetailCloud.Payments.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-101 / PAY-002 / PAY-002.1 / SAL-006.1: card slip inputs, change, and what a manager's override is bound to.</summary>
public sealed class PaymentRulesTests
{
    [Theory]
    [InlineData("558812345678", true)]
    [InlineData("AB12", true)]
    [InlineData("123", false)]
    [InlineData("12 34 56", false)]
    [InlineData("۱۲۳۴۵۶", false)]
    [InlineData(null, false)]
    public void A_reference_is_4_to_32_letters_or_digits(string? v, bool ok) => Assert.Equal(ok, PaymentRules.ValidReference(v));

    [Fact]
    public void Card_details_are_optional_but_well_formed()
    {
        Assert.True(PaymentRules.ValidTerminalNo(null) && PaymentRules.ValidLast4(null));
        Assert.False(PaymentRules.ValidLast4("123"));
        Assert.False(PaymentRules.ValidTerminalNo("88-99"));
    }

    [Fact]
    public void SAL006_1_change_is_what_was_handed_over_less_what_is_applied() => Assert.Equal(200_000, PaymentRules.Change(300_000, 500_000));

    [Fact]
    public void An_override_fits_one_bill_terminal_reference_day_and_amount()
    {
        Guid sale = Guid.CreateVersion7();
        var day = new DateOnly(2026, 11, 24);
        string b = PaymentRules.OverrideBinding(sale, "41", "777000111222", day, 50_000);
        Assert.Equal(b, PaymentRules.OverrideBinding(sale, "41", "777000111222", day, 50_000));
        Assert.NotEqual(b, PaymentRules.OverrideBinding(sale, "41", "777000111222", day, 50_001));
        Assert.NotEqual(b, PaymentRules.OverrideBinding(Guid.CreateVersion7(), "41", "777000111222", day, 50_000));
        Assert.NotEqual(b, PaymentRules.OverrideBinding(sale, "42", "777000111222", day, 50_000));
        Assert.NotEqual(b, PaymentRules.OverrideBinding(sale, "41", "777000111222", day.AddDays(1), 50_000));
    }
}
