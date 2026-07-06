using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Fetches the authoritative per-request cost from OpenRouter's generation endpoint
    /// (GET /api/v1/generation?id=...), the "actual OpenRouter API, not predicted" spend the
    /// design doc (§3) requires. The endpoint has a short propagation delay after a stream
    /// completes, so callers retry over a few seconds; on repeated failure the ledger keeps its
    /// provisional per-model estimate (never a silent zero).
    ///
    /// This is a sibling of KliveLLM rather than a change to its shared response struct, so
    /// non-Projects callers are untouched.
    /// </summary>
    public class OpenRouterCostFetcher
    {
        private readonly Func<Task<string?>> tokenProvider;
        private readonly Action<string> log;
        private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };

        public OpenRouterCostFetcher(Func<Task<string?>> tokenProvider, Action<string> log)
        {
            this.tokenProvider = tokenProvider;
            this.log = log ?? (_ => { });
        }

        /// <summary>
        /// Returns the real USD cost for a generation ID, or null if it couldn't be fetched
        /// after <paramref name="attempts"/> tries. Retries because OpenRouter populates the
        /// record a beat after the completion streams.
        /// </summary>
        public async Task<double?> TryGetCostAsync(string generationId, int attempts = 4, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(generationId)) return null;
            string? token = await tokenProvider();
            if (string.IsNullOrWhiteSpace(token)) return null;

            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"https://openrouter.ai/api/v1/generation?id={Uri.EscapeDataString(generationId)}");
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    using var resp = await http.SendAsync(req, ct);
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Not propagated yet — back off and retry.
                        await Task.Delay(TimeSpan.FromMilliseconds(800 * (i + 1)), ct);
                        continue;
                    }
                    if (!resp.IsSuccessStatusCode) return null;
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    var json = JObject.Parse(body);
                    // OpenRouter shape: { "data": { "total_cost": <usd>, ... } }
                    var cost = json["data"]?["total_cost"]?.Value<double?>();
                    if (cost.HasValue) return cost.Value;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log($"OpenRouterCostFetcher: attempt {i + 1} failed ({ex.Message}).");
                    await Task.Delay(TimeSpan.FromMilliseconds(800 * (i + 1)), ct);
                }
            }
            return null;
        }
    }
}
