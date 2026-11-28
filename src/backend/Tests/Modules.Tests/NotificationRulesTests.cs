using RetailCloud.Notifications.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-151: NOT-005 backoff, NOT-007 purposes, template field discipline (NOT-002).</summary>
public sealed class NotificationRulesTests
{
    [Fact]
    public void Backoff_is_30s_2m_10m_1h_then_dead_letter_after_the_fifth_attempt()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), NotificationBackoff.After(1));
        Assert.Equal(TimeSpan.FromMinutes(2), NotificationBackoff.After(2));
        Assert.Equal(TimeSpan.FromMinutes(10), NotificationBackoff.After(3));
        Assert.Equal(TimeSpan.FromHours(1), NotificationBackoff.After(4));
        Assert.Null(NotificationBackoff.After(5));
        Assert.Null(NotificationBackoff.After(0));
    }

    [Fact]
    public void Marketing_is_not_a_purpose()
    {
        Assert.DoesNotContain("Marketing", NotificationPurposes.All);
        Assert.Equal(["DueReminder", "Internal", "Receipt"], NotificationPurposes.All.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_template_renders_only_with_exactly_its_declared_fields()
    {
        var ok = new Dictionary<string, string> { ["storeName"] = "فروشگاه", ["amount"] = NotificationTemplates.Amount(1_250_000), ["link"] = "https://r.example/x" };
        Assert.Equal("فروشگاه: فاکتور شما به مبلغ 1,250,000 ریال. مشاهده: https://r.example/x", NotificationTemplates.Render("receipt", 1, ok));
        Assert.Null(NotificationTemplates.Render("receipt", 1, new Dictionary<string, string>(ok) { ["items"] = "شیر ×۲" }));   // no item list in a receipt
        Assert.Null(NotificationTemplates.Render("receipt", 1, new Dictionary<string, string> { ["storeName"] = "x", ["amount"] = "1" }));
        Assert.Null(NotificationTemplates.Render("receipt", 2, ok));
        Assert.Null(NotificationTemplates.Render("promo", 1, ok));
    }
}
