using System.Net;
using System.Text;
using RetailCloud.Merchandising.Application;
using RetailCloud.Merchandising.Infrastructure;

namespace RetailCloud.Api.Tests;

/// <summary>ADR-007 / SEC-T-040 / SEC-T-041 / AT-203: the Open Food Facts adapter against a fake server.</summary>
public sealed class OpenFoodFactsProviderTests
{
    private sealed class Fake(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            return answer(request, cancellationToken);
        }
    }

    private static (OpenFoodFactsProvider Provider, Fake Server) With(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer,
        bool enabled = true)
    {
        var fake = new Fake(answer);
        var options = new OpenFoodFactsOptions { Enabled = enabled, BaseAddress = new Uri("https://off.test/"), Timeout = TimeSpan.FromMilliseconds(300) };
        return (new OpenFoodFactsProvider(new HttpClient(fake), options), fake);
    }

    private static Task<HttpResponseMessage> Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    [Fact]
    public async Task A_product_is_read_cleaned_and_its_image_kept_only_on_the_image_host()
    {
        var (provider, server) = With((_, _) => Json("""
            {"product":{"product_name":"<script>x</script>شیر <b>پرچرب</b>","brands":"پگاه,Pegah","categories":"لبنیات,Dairy",
             "quantity":"1 L","image_front_url":"https://images.openfoodfacts.org/images/1.jpg"}}
            """));
        ProviderLookup r = await provider.LookupAsync("06260000000013", default);
        Assert.True(r.Found);
        Assert.Equal(new CandidateFields("x شیر پرچرب", "پگاه", "لبنیات", "1 L", "https://images.openfoodfacts.org/images/1.jpg"), r.Fields);
        Uri asked = Assert.Single(server.Asked);
        Assert.Equal(("off.test", "/api/v2/product/6260000000013.json"), (asked.Host, asked.AbsolutePath));   // the configured host, the code only

        var (evil, _) = With((_, _) => Json("""{"product":{"product_name":"x","image_front_url":"https://10.0.0.1/admin"}}"""));
        Assert.Null((await evil.LookupAsync("06260000000013", default)).Fields!.ImageUrl);
    }

    [Fact]
    public async Task AT203_down_slow_or_nonsense_is_a_failure_and_not_found_is_a_miss()
    {
        Assert.True((await With((_, _) => Json("{}", HttpStatusCode.InternalServerError)).Provider.LookupAsync("06260000000013", default)).Failed);
        Assert.True((await With((_, _) => Json("<html>")).Provider.LookupAsync("06260000000013", default)).Failed);
        Assert.True((await With(async (_, ct) => { await Task.Delay(5000, ct); return new HttpResponseMessage(); }).Provider
            .LookupAsync("06260000000013", default)).Failed);
        ProviderLookup missing = await With((_, _) => Json("{}", HttpStatusCode.NotFound)).Provider.LookupAsync("06260000000013", default);
        Assert.Equal((false, false), (missing.Found, missing.Failed));
        Assert.False((await With((_, _) => Json("""{"product":{"brands":"x"}}""")).Provider.LookupAsync("06260000000013", default)).Found);   // no name
    }

    [Fact]
    public async Task A_disabled_provider_or_a_code_that_is_not_a_GTIN_asks_nothing()
    {
        var (off, server) = With((_, _) => Json("{}"), enabled: false);
        Assert.False((await off.LookupAsync("06260000000013", default)).Found);
        var (on, server2) = With((_, _) => Json("{}"));
        Assert.False((await on.LookupAsync("../../admin?x=", default)).Found);
        Assert.Empty(server.Asked);
        Assert.Empty(server2.Asked);
    }
}
