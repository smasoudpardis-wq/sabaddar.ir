using RetailCloud.Notifications.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-152 / NOT-009 / AT-147: a store changes only its signed name and a signature; the platform text and fields stay.</summary>
public sealed class SmsTemplateRulesTests
{
    private static readonly Dictionary<string, string> Payload = new() { ["storeName"] = "فروشگاه", ["amount"] = "1,000", ["link"] = "https://r.example/x" };

    [Fact]
    public void AT147_the_store_name_and_signature_replace_only_their_parts_of_the_platform_text()
    {
        string plain = NotificationTemplates.Render("receipt", 1, Payload)!;
        string custom = NotificationTemplates.Render("receipt", 1, Payload, new TemplateOverride("نان‌آور", "با تشکر از خرید شما"))!;
        Assert.Equal(plain.Replace("فروشگاه:", "نان‌آور:", StringComparison.Ordinal) + " با تشکر از خرید شما", custom);
        Assert.Equal(plain, NotificationTemplates.Render("receipt", 1, Payload, null));
        Assert.Equal(plain, NotificationTemplates.Render("receipt", 1, Payload, new TemplateOverride(null, null)));
        Assert.Contains("1,000", custom, StringComparison.Ordinal);   // the amount and link are never the store's to change
        Assert.Null(NotificationTemplates.Render("receipt", 1, new Dictionary<string, string> { ["storeName"] = "x" }, new TemplateOverride("y", null)));
    }

    [Theory]
    [InlineData("نان‌آور", "با تشکر", null)]
    [InlineData(null, "با تشکر", null)]
    [InlineData("نان‌آور", null, null)]
    [InlineData(null, null, "storeName")]
    [InlineData("", "ok", "storeName")]
    [InlineData("ا", "تشکر\nدوباره", "signature")]
    [InlineData("سایت http://x.ir", null, "storeName")]
    [InlineData("ok", "www.x.ir", "signature")]
    public void The_texts_are_short_single_line_and_without_links(string? name, string? signature, string? badField)
    {
        string? problem = SmsTextRules.Problem(name, signature);
        Assert.Equal(badField, problem?.Split(':')[0]);
    }

    [Fact]
    public void Lengths_are_30_and_40()
    {
        Assert.Null(SmsTextRules.Problem(new string('ا', 30), new string('ب', 40)));
        Assert.StartsWith("storeName", SmsTextRules.Problem(new string('ا', 31), null), StringComparison.Ordinal);
        Assert.StartsWith("signature", SmsTextRules.Problem(null, new string('ب', 41)), StringComparison.Ordinal);
    }
}
