using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG.Web
{
    /// <summary>
    /// Thin client over SearXNG's JSON search API. Ensures the container is up, issues
    /// <c>GET /search?q=…&amp;format=json</c>, and maps the result rows. Surfaces the engine health
    /// SearXNG reports (<c>unresponsive_engines</c>) so a caller knows when recall was degraded.
    /// </summary>
    public sealed class SearxngClient
    {
        private readonly SearxngContainerManager container;
        private readonly HttpClient http;
        private readonly Action<string> log;

        public SearxngClient(SearxngContainerManager container, HttpClient http, Action<string> log)
        {
            this.container = container;
            this.http = http;
            this.log = log ?? (_ => { });
        }

        public sealed class SearchResponse
        {
            public List<WebSearchResult> Results { get; } = new();
            public List<string> UnresponsiveEngines { get; } = new();
            public string? Error { get; set; }
        }

        public async Task<SearchResponse> SearchAsync(string query, int maxResults, string? timeRange, CancellationToken ct)
        {
            var resp = new SearchResponse();
            if (string.IsNullOrWhiteSpace(query)) { resp.Error = "Empty query."; return resp; }

            var (ok, message) = await container.EnsureRunningAsync(ct);
            if (!ok) { resp.Error = message; return resp; }

            string url = $"{container.BaseUrl}/search?q={Uri.EscapeDataString(query)}&format=json&safesearch=0";
            if (!string.IsNullOrWhiteSpace(timeRange)) url += $"&time_range={Uri.EscapeDataString(timeRange)}";

            string body;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var httpResp = await http.GetAsync(url, timeout.Token);
                if (!httpResp.IsSuccessStatusCode)
                {
                    resp.Error = $"SearXNG returned {(int)httpResp.StatusCode}. If this persists, verify search.formats includes 'json' in settings.yml.";
                    return resp;
                }
                body = await httpResp.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (Exception ex) { resp.Error = $"SearXNG request failed: {ex.Message}"; return resp; }

            try
            {
                var root = JObject.Parse(body);
                if (root["unresponsive_engines"] is JArray ue)
                    foreach (var e in ue)
                        resp.UnresponsiveEngines.Add(e.Type == JTokenType.Array ? string.Join(":", e) : e.ToString());

                if (root["results"] is JArray results)
                {
                    foreach (var r in results)
                    {
                        string title = (string?)r["title"] ?? "";
                        string link = (string?)r["url"] ?? "";
                        if (string.IsNullOrWhiteSpace(link)) continue;
                        resp.Results.Add(new WebSearchResult
                        {
                            Title = title,
                            Url = link,
                            Content = (string?)r["content"] ?? "",
                            Engine = (string?)r["engine"],
                            Score = (double?)r["score"] ?? 0,
                        });
                        if (resp.Results.Count >= maxResults) break;
                    }
                }
            }
            catch (Exception ex) { resp.Error = $"Failed to parse SearXNG response: {ex.Message}"; }

            return resp;
        }
    }
}
