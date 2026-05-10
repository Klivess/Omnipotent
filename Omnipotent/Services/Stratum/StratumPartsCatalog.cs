using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Text;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Live distributor lookup for module BOM enrichment. Uses the Mouser Search API
    /// (https://api.mouser.com/api/v1/search/keyword) — single API key, no OAuth, returns
    /// structured part data (MPN, description, price, datasheet, stock, image).
    ///
    /// Pinout data is intentionally NOT sourced from this API — distributors expose pinouts
    /// only inside PDF datasheets. The agent's curated <see cref="StratumModuleLibrary"/>
    /// remains the source of truth for wiring; this catalog only enriches the BOM with live
    /// pricing/datasheet/stock once the agent has finalised its module choices.
    ///
    /// API key resolution: read from the Stratum service's OmniSettings ("MouserAPIKey").
    /// If unset, lookups are skipped and BomLine.DistributorCandidates stays empty.
    /// </summary>
    public class StratumPartsCatalog
    {
        private const string MouserSearchUrl = "https://api.mouser.com/api/v1/search/keyword";
        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private readonly string? mouserApiKey;
        private readonly Action<string>? logger;
        private readonly Dictionary<string, List<DistributorPart>> cache = new(StringComparer.OrdinalIgnoreCase);

        public bool MouserEnabled => !string.IsNullOrWhiteSpace(mouserApiKey);

        public StratumPartsCatalog(string? mouserApiKey, Action<string>? logger = null)
        {
            this.mouserApiKey = string.IsNullOrWhiteSpace(mouserApiKey) ? null : mouserApiKey;
            this.logger = logger;
        }

        /// <summary>
        /// Lookup live distributor candidates for a curated module. Returns empty list if no
        /// API key is configured or the request fails (caller decides how to surface it). Cached
        /// in-memory for the lifetime of this catalog instance.
        /// </summary>
        public async Task<List<DistributorPart>> LookupAsync(ModuleSpec module, int maxResults = 3, CancellationToken ct = default)
        {
            if (!MouserEnabled) return new List<DistributorPart>();
            if (string.IsNullOrWhiteSpace(module.MouserKeyword)) return new List<DistributorPart>();

            string cacheKey = $"{module.Id}|{module.MouserKeyword}|{maxResults}";
            if (cache.TryGetValue(cacheKey, out var cached)) return cached;

            try
            {
                string url = $"{MouserSearchUrl}?apiKey={Uri.EscapeDataString(mouserApiKey!)}";
                var body = new
                {
                    SearchByKeywordRequest = new
                    {
                        keyword = module.MouserKeyword,
                        records = maxResults,
                        startingRecord = 0,
                        searchOptions = "InStock",
                        searchWithYourSignUpLanguage = "false",
                    }
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json")
                };
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var resp = await http.SendAsync(req, ct);
                string payload = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    logger?.Invoke($"Mouser lookup '{module.MouserKeyword}' failed: HTTP {(int)resp.StatusCode}: {Truncate(payload, 200)}");
                    cache[cacheKey] = new List<DistributorPart>();
                    return cache[cacheKey];
                }

                var parsed = ParseMouserResponse(payload, maxResults);
                cache[cacheKey] = parsed;
                return parsed;
            }
            catch (Exception ex)
            {
                logger?.Invoke($"Mouser lookup '{module.MouserKeyword}' threw: {ex.Message}");
                cache[cacheKey] = new List<DistributorPart>();
                return cache[cacheKey];
            }
        }

        private static List<DistributorPart> ParseMouserResponse(string payload, int maxResults)
        {
            var result = new List<DistributorPart>();
            JObject root;
            try { root = JObject.Parse(payload); }
            catch { return result; }

            // Mouser shape: { Errors: [...], SearchResults: { NumberOfResult, Parts: [ {...}, ... ] } }
            var parts = root["SearchResults"]?["Parts"] as JArray;
            if (parts == null) return result;

            foreach (var part in parts.Take(maxResults))
            {
                string mpn = (string?)part["ManufacturerPartNumber"] ?? "";
                if (string.IsNullOrWhiteSpace(mpn)) continue;

                // PriceBreaks: array of { Quantity, Price, Currency }. Use the qty-1 row when present.
                string priceQty1 = "";
                var pb = part["PriceBreaks"] as JArray;
                if (pb != null && pb.Count > 0)
                {
                    var first = pb[0];
                    string p = (string?)first["Price"] ?? "";
                    if (!string.IsNullOrWhiteSpace(p)) priceQty1 = p;
                }

                result.Add(new DistributorPart
                {
                    Distributor = "Mouser",
                    ManufacturerPartNumber = mpn,
                    Manufacturer = (string?)part["Manufacturer"] ?? "",
                    Description = (string?)part["Description"] ?? "",
                    ProductDetailUrl = (string?)part["ProductDetailUrl"] ?? "",
                    DataSheetUrl = (string?)part["DataSheetUrl"] ?? "",
                    ImageUrl = (string?)part["ImagePath"] ?? "",
                    PriceQty1 = priceQty1,
                    Availability = (string?)part["Availability"] ?? "",
                });
            }
            return result;
        }

        private static string Truncate(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");
    }
}
