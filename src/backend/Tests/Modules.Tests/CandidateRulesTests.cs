using RetailCloud.Merchandising.Application;

namespace RetailCloud.Modules.Tests;

/// <summary>US-043 / CAT-004 / CAT-004.1 / TM-040 / TM-041: candidate confidence, conflicts and cleaning of provider data.</summary>
public sealed class CandidateRulesTests
{
    private static readonly string[] Hosts = ["images.openfoodfacts.org"];

    [Theory]
    [InlineData("<script>alert(1)</script>شیر کم‌چرب", "alert(1) شیر کم‌چرب")]       // tags go, the half-space (ZWNJ) stays
    [InlineData("<img src=x onerror=alert(1)>Milk", "Milk")]
    [InlineData("Juice‮gnp.exe", "Juice gnp.exe")]                              // a direction override cannot disguise text
    [InlineData("  a\u0000b\t\nc  ", "a b c")]
    [InlineData("   ", null)]
    [InlineData("<b></b>", null)]
    public void TM041_provider_text_is_plain_text(string raw, string? clean) => Assert.Equal(clean, CandidateRules.Clean(raw, 200));

    [Fact]
    public void TM041_provider_text_is_capped() => Assert.Equal(10, CandidateRules.Clean(new string('ا', 50), 10)!.Length);

    [Theory]
    [InlineData("https://images.openfoodfacts.org/images/products/626/front.jpg", true)]
    [InlineData("http://images.openfoodfacts.org/a.jpg", false)]                        // https only
    [InlineData("https://images.openfoodfacts.org:8443/a.jpg", false)]
    [InlineData("https://user@images.openfoodfacts.org/a.jpg", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]                     // SEC-T-040: never an internal address
    [InlineData("https://evil.example/a.jpg", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/relative.jpg", false)]
    public void SEC_T_040_an_image_is_a_https_link_on_the_providers_image_host_only(string url, bool kept) =>
        Assert.Equal(kept, CandidateRules.Image(url, Hosts) is not null);

    [Fact]
    public void CAT0041_a_provider_never_reaches_the_threshold_and_a_moderated_product_does()
    {
        Assert.Equal(0.7m, CandidateRules.ProviderConfidence(new CandidateFields("شیر", "پگاه", null, null, null)));
        Assert.Equal(0.5m, CandidateRules.ProviderConfidence(new CandidateFields("شیر", null, null, null, null)));
        Assert.True(CandidateRules.ProviderConfidence(new CandidateFields("a", "b", "c", "d", "e")) < CandidateRules.Threshold);
        Assert.True(CandidateRules.GlobalConfidence >= CandidateRules.Threshold);
    }

    [Fact]
    public void UC01_6a_candidates_with_another_name_or_brand_conflict()
    {
        static CatalogCandidate C(string name, string? brand) => new("global", 0.95m, null, null, null, new CandidateFields(name, brand, null, null, null), null);
        IReadOnlyList<IReadOnlyList<int>> c = CandidateRules.Conflicts([C("شير پگاه", "پگاه"), C("شیر  پگاه", null), C("ماست", "پگاه")]);
        Assert.Equal([2], c[0]);          // Arabic yeh and double spaces are the same name; a missing brand does not conflict
        Assert.Equal([2], c[1]);
        Assert.Equal([0, 1], c[2]);
    }
}
