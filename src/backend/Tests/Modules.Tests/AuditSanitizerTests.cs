using System.Text.Json;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>AUD-003: secrets and contact PII never reach the audit trail.</summary>
public sealed class AuditSanitizerTests
{
    [Fact]
    public void Secret_like_property_names_are_redacted_at_any_depth()
    {
        string json = AuditSanitizer.ToJson(new
        {
            role = "Cashier",
            pin = "4821",
            nested = new { accessToken = "x", Password = "p", items = new object[] { new { otp = "123456", qty = 2 } } },
            customerMobile = "09121234567",
            email = "a@b.c",
            apiKey = "k",
        });
        using JsonDocument d = JsonDocument.Parse(json);
        JsonElement r = d.RootElement;
        Assert.Equal("Cashier", r.GetProperty("role").GetString());
        Assert.Equal(AuditSanitizer.Redacted, r.GetProperty("pin").GetString());
        Assert.Equal(AuditSanitizer.Redacted, r.GetProperty("nested").GetProperty("accessToken").GetString());
        Assert.Equal(AuditSanitizer.Redacted, r.GetProperty("nested").GetProperty("Password").GetString());
        Assert.Equal(AuditSanitizer.Redacted, r.GetProperty("nested").GetProperty("items")[0].GetProperty("otp").GetString());
        Assert.Equal(2, r.GetProperty("nested").GetProperty("items")[0].GetProperty("qty").GetInt32());
        Assert.Equal(AuditSanitizer.Redacted, r.GetProperty("customerMobile").GetString());
        Assert.Equal(AuditSanitizer.Redacted, r.GetProperty("email").GetString());
        Assert.Equal(AuditSanitizer.Redacted, r.GetProperty("apiKey").GetString());
    }

    [Theory]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln")]   // a JWT under an innocent name
    [InlineData("6037991234567890")]                           // a card number (PAN)
    public void Secret_shaped_values_are_redacted_whatever_the_name(string value)
    {
        using JsonDocument d = JsonDocument.Parse(AuditSanitizer.ToJson(new { note = value }));
        Assert.Equal(AuditSanitizer.Redacted, d.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public void Ordinary_values_stay()
    {
        using JsonDocument d = JsonDocument.Parse(AuditSanitizer.ToJson(new { code = "B2", amount = "1100000", invoiceNo = "B1-1405-000124" }));
        Assert.Equal("B2", d.RootElement.GetProperty("code").GetString());
        Assert.Equal("1100000", d.RootElement.GetProperty("amount").GetString());
        Assert.Equal("{}", AuditSanitizer.ToJson(null));
    }
}
