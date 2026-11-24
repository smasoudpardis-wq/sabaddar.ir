using System.Net.Http.Json;
using System.Text.Json;
using RetailCloud.Merchandising.Application;

namespace RetailCloud.Merchandising.Infrastructure;

/// <summary>Configuration of the Open Food Facts provider (ADR-007). Off until the ODbL review is done (X-06).</summary>
public sealed class OpenFoodFactsOptions
{
    public bool Enabled { get; set; }

    /// <summary>Fixed by configuration, never by a request (SEC-T-040).</summary>
    public Uri BaseAddress { get; set; } = new("https://world.openfoodfacts.org/");

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    public IReadOnlyCollection<string> ImageHosts { get; set; } = ["images.openfoodfacts.org"];
}

/// <summary>
/// ADR-007 / SP-02: product data from Open Food Facts (ODbL; attribution required). Asked only with a valid public
/// GTIN-14; whatever it returns is treated as untrusted text (TM-041) and an image link is kept only on its image host.
/// </summary>
public sealed class OpenFoodFactsProvider(HttpClient http, OpenFoodFactsOptions options) : ICatalogProvider
{
    public string Source => "open_food_facts";

    public bool Enabled => options.Enabled;

    public string License => "ODbL-1.0";

    public string Attribution => "Open Food Facts contributors (openfoodfacts.org)";

    public async Task<ProviderLookup> LookupAsync(string gtin14, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gtin14);
        if (!options.Enabled || gtin14.Length != 14 || !gtin14.All(char.IsAsciiDigit))
        {
            return ProviderLookup.Miss;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            // the code is digits only and the host is configuration: the request cannot be steered elsewhere (SEC-T-040)
            using HttpResponseMessage r = await http.GetAsync(new Uri(options.BaseAddress,
                $"api/v2/product/{gtin14.TrimStart('0').PadLeft(13, '0')}.json?fields=product_name,brands,categories,quantity,image_front_url"), timeout.Token);
            if (r.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return ProviderLookup.Miss;
            }

            if (!r.IsSuccessStatusCode || r.Content.Headers.ContentLength > 256 * 1024)
            {
                return ProviderLookup.Failure;
            }

            using JsonDocument doc = await JsonDocument.ParseAsync(await r.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            if (!doc.RootElement.TryGetProperty("product", out JsonElement p) || p.ValueKind != JsonValueKind.Object)
            {
                return ProviderLookup.Miss;
            }

            string? S(string k) => p.TryGetProperty(k, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var fields = new CandidateFields(CandidateRules.Clean(S("product_name"), 200), CandidateRules.Clean(S("brands")?.Split(',')[0], 100),
                CandidateRules.Clean(S("categories")?.Split(',')[0], 100), CandidateRules.Clean(S("quantity"), 50),
                CandidateRules.Image(S("image_front_url"), options.ImageHosts));
            return fields.Name is null ? ProviderLookup.Miss : new ProviderLookup(true, fields, false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return ProviderLookup.Failure;   // down, slow or nonsense: the resolve answers degraded, never fails (AT-203)
        }
    }
}
