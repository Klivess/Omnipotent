using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Data;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Omnipotent.Services.OmniTrader.Research.Alpha
{
    /// <summary>
    /// Free sources of new finance research: the arXiv API (quantitative finance categories plus
    /// keyword searches) and OpenAlex (journals, SSRN and preprint servers, restricted to the
    /// economics/finance field and searched on title and abstract).
    /// </summary>
    public static class PaperSources
    {
        private static readonly SemaphoreSlim arxivGate = new(1, 1);
        private static DateTime lastArxivCall = DateTime.MinValue;

        // ── arXiv ────────────────────────────────────────────────────────────────

        public static async Task<List<ResearchPaper>> ArxivAsync(IEnumerable<string> categories, IEnumerable<string> queries,
            int perQuery = 50, CancellationToken ct = default)
        {
            var papers = new List<ResearchPaper>();
            var cats = categories.ToList();
            if (cats.Count > 0)
            {
                string search = string.Join("+OR+", cats.Select(c => "cat:" + c));
                papers.AddRange(await ArxivQueryAsync(search, Math.Min(200, perQuery * 3), ct));
            }
            foreach (var q in queries)
            {
                // Keyword searches across all of arXiv, restricted to finance-ish categories so physics
                // "momentum" does not flood in.
                string terms = string.Join("+AND+", q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => "abs:" + Uri.EscapeDataString(t)));
                string scope = cats.Count > 0 ? "+AND+(" + string.Join("+OR+", cats.Concat(new[] { "econ.GN", "stat.ML", "cs.LG" }).Distinct().Select(c => "cat:" + c)) + ")" : "";
                try { papers.AddRange(await ArxivQueryAsync($"({terms}){scope}", perQuery, ct)); }
                catch { /* one query failing keeps the rest */ }
            }
            return papers;
        }

        private static async Task<List<ResearchPaper>> ArxivQueryAsync(string searchQuery, int max, CancellationToken ct)
        {
            await arxivGate.WaitAsync(ct);
            try
            {
                // arXiv asks for no more than one request every three seconds.
                var wait = lastArxivCall.AddSeconds(3.1) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                string url = $"https://export.arxiv.org/api/query?search_query={searchQuery}&sortBy=submittedDate&sortOrder=descending&start=0&max_results={max}";
                string xml = await DataHttp.GetStringAsync("arxiv", "research", url, DataHttp.Research, ct);
                lastArxivCall = DateTime.UtcNow;
                return ParseArxiv(xml);
            }
            finally { arxivGate.Release(); }
        }

        internal static List<ResearchPaper> ParseArxiv(string xml)
        {
            XNamespace atom = "http://www.w3.org/2005/Atom";
            XNamespace arxiv = "http://arxiv.org/schemas/atom";
            var doc = XDocument.Parse(xml);
            var list = new List<ResearchPaper>();
            foreach (var e in doc.Descendants(atom + "entry"))
            {
                string idUrl = e.Element(atom + "id")?.Value?.Trim() ?? "";
                var m = Regex.Match(idUrl, @"abs/(?<id>.+?)(v\d+)?$");
                string arxivId = m.Success ? m.Groups["id"].Value : idUrl;
                string title = Collapse(e.Element(atom + "title")?.Value);
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(arxivId)) continue;
                var cats = e.Elements(atom + "category").Select(c => c.Attribute("term")?.Value).Where(c => c != null).Select(c => c!).ToList();
                var primary = e.Element(arxiv + "primary_category")?.Attribute("term")?.Value;
                if (primary != null && !cats.Contains(primary)) cats.Insert(0, primary);
                list.Add(new ResearchPaper
                {
                    Id = "arxiv:" + arxivId,
                    Source = "arxiv",
                    ExternalId = arxivId,
                    Doi = e.Element(arxiv + "doi")?.Value,
                    Title = title,
                    Authors = e.Elements(atom + "author").Select(a => Collapse(a.Element(atom + "name")?.Value)).Where(a => a.Length > 0).ToList(),
                    Abstract = Collapse(e.Element(atom + "summary")?.Value),
                    PublishedUtc = ParseDate(e.Element(atom + "published")?.Value),
                    UpdatedUtc = ParseDate(e.Element(atom + "updated")?.Value),
                    Categories = cats,
                    Venue = e.Element(arxiv + "journal_ref")?.Value is { } jr ? Collapse(jr) : "arXiv " + (primary ?? ""),
                    Url = $"https://arxiv.org/abs/{arxivId}",
                    PdfUrl = $"https://arxiv.org/pdf/{arxivId}",
                    DedupeKey = DedupeKeyFor(title),
                });
            }
            return list;
        }

        // ── OpenAlex ─────────────────────────────────────────────────────────────

        public static async Task<List<ResearchPaper>> OpenAlexAsync(IEnumerable<string> queries, DateTime since, int perQuery = 40,
            CancellationToken ct = default)
        {
            var papers = new List<ResearchPaper>();
            foreach (var q in queries)
            {
                string filter = $"title_and_abstract.search:{Uri.EscapeDataString(q)},primary_topic.field.id:20,from_publication_date:{since:yyyy-MM-dd},has_abstract:true";
                string url = $"https://api.openalex.org/works?filter={filter}&sort=publication_date:desc&per_page={Math.Clamp(perQuery, 5, 100)}&mailto=admin@klive.dev";
                try
                {
                    var json = await DataHttp.GetJsonAsync("openalex", "research", url, DataHttp.Research, ct);
                    papers.AddRange(ParseOpenAlex(json));
                }
                catch { }
                await Task.Delay(150, ct); // polite pool: ~10 req/s max
            }
            return papers;
        }

        internal static List<ResearchPaper> ParseOpenAlex(JToken json)
        {
            var list = new List<ResearchPaper>();
            foreach (var w in json["results"] as JArray ?? new JArray())
            {
                string title = Collapse(w["title"]?.Value<string>() ?? w["display_name"]?.Value<string>());
                string id = (w["id"]?.Value<string>() ?? "").Replace("https://openalex.org/", "");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(id)) continue;
                string abs = InvertedIndexToText(w["abstract_inverted_index"] as JObject);
                string? doi = w["doi"]?.Value<string>()?.Replace("https://doi.org/", "");
                string? oa = w["open_access"]?["oa_url"]?.Value<string>();
                string? landing = w["primary_location"]?["landing_page_url"]?.Value<string>();
                list.Add(new ResearchPaper
                {
                    Id = "openalex:" + id,
                    Source = "openalex",
                    ExternalId = id,
                    Doi = doi,
                    Title = title,
                    Authors = (w["authorships"] as JArray ?? new JArray()).Select(a => a["author"]?["display_name"]?.Value<string>() ?? "").Where(a => a.Length > 0).Take(12).ToList(),
                    Abstract = abs,
                    PublishedUtc = DataHttp.Date(w["publication_date"]),
                    Categories = new[] { w["primary_topic"]?["display_name"]?.Value<string>(), w["primary_topic"]?["subfield"]?["display_name"]?.Value<string>() }
                        .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).ToList(),
                    Venue = w["primary_location"]?["source"]?["display_name"]?.Value<string>(),
                    Url = landing ?? (doi != null ? "https://doi.org/" + doi : "https://openalex.org/" + id),
                    PdfUrl = w["primary_location"]?["pdf_url"]?.Value<string>() ?? (oa != null && oa.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? oa : null),
                    Citations = w["cited_by_count"]?.Type == JTokenType.Integer ? w["cited_by_count"]!.Value<int>() : null,
                    DedupeKey = DedupeKeyFor(title),
                });
            }
            return list;
        }

        /// <summary>OpenAlex ships abstracts as word → positions; rebuild the text.</summary>
        internal static string InvertedIndexToText(JObject? index)
        {
            if (index == null) return "";
            var words = new SortedDictionary<int, string>();
            foreach (var p in index.Properties())
                foreach (var pos in p.Value as JArray ?? new JArray())
                    words[pos.Value<int>()] = p.Name;
            var sb = new StringBuilder();
            foreach (var w in words.Values) { if (sb.Length > 0) sb.Append(' '); sb.Append(w); }
            return sb.ToString();
        }

        public static string DedupeKeyFor(string title)
        {
            string t = Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]", "");
            return t.Length > 80 ? t[..80] : t;
        }

        private static string Collapse(string? s) => s == null ? "" : Regex.Replace(s, "\\s+", " ").Trim();

        private static DateTime? ParseDate(string? s)
            => DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }
}
