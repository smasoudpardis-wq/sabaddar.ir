using RetailCloud.Sales.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-094 / BR-073: what a tax invoice needs of its buyer.</summary>
public sealed class BuyerTaxRulesTests
{
    [Theory]
    [InlineData("0013542419", true)]
    [InlineData("0499370899", true)]
    [InlineData("1234567890", false)]   // wrong check digit
    [InlineData("1111111111", false)]   // all equal passes the sum but is never issued
    [InlineData("001354241", false)]
    [InlineData("00135424190", false)]
    [InlineData("00135424x9", false)]
    [InlineData(null, false)]
    public void A_national_id_is_ten_digits_with_its_check_digit(string? id, bool valid) => Assert.Equal(valid, BuyerTaxRules.IsNationalId(id));

    [Fact]
    public void An_individual_gives_a_national_id_and_a_legal_entity_an_economic_code()
    {
        Assert.Null(BuyerTaxRules.Problem("Individual", "0013542419", null));
        Assert.StartsWith("nationalId", BuyerTaxRules.Problem("Individual", "1234567890", null), StringComparison.Ordinal);
        Assert.StartsWith("economicCode", BuyerTaxRules.Problem("Individual", "0013542419", "123456789012"), StringComparison.Ordinal);
        Assert.Null(BuyerTaxRules.Problem("Legal", null, "123456789012"));
        Assert.Null(BuyerTaxRules.Problem("Legal", "10100000001", "12345678901234"));
        Assert.StartsWith("economicCode", BuyerTaxRules.Problem("Legal", null, "12345"), StringComparison.Ordinal);
        Assert.StartsWith("nationalId", BuyerTaxRules.Problem("Legal", "123", "123456789012"), StringComparison.Ordinal);
        Assert.StartsWith("type", BuyerTaxRules.Problem("Alien", "0013542419", null), StringComparison.Ordinal);
        Assert.Equal("Legal||123456789012", BuyerTaxRules.Canonical("Legal", null, "123456789012"));
    }
}
