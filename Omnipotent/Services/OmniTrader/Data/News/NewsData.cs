using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Omnipotent.Services.OmniTrader.Data.News
{
    public sealed class NewsItem
    {
        public required string Title { get; init; }
        public required string Url { get; init; }
        public required string Source { get; init; }
        public DateTime PublishedUtc { get; init; }
        public string? Summary { get; init; }
        public string Feed { get; init; } = "";
        public List<string> Symbols { get; init; } = new();
    }

    public sealed record NewsFeed(string Id, string Name, string Category, string Url);

    /// <summary>
    /// Headlines from free RSS feeds: market and crypto desks, per-symbol Yahoo feeds and Google News
    /// search. Items are de-duplicated on URL and normalised title, newest first.
    /// </summary>
    public sealed class NewsData
    {
        public static readonly NewsFeed[] Feeds =
        {
            new("cnbc-top", "CNBC", "markets", "https://search.cnbc.com/rs/search/combinedcms/view.xml?partnerId=wrss01&id=100003114"),
            new("cnbc-markets", "CNBC Markets", "markets", "https://search.cnbc.com/rs/search/combinedcms/view.xml?partnerId=wrss01&id=20910258"),
            new("marketwatch", "MarketWatch", "markets", "https://feeds.content.dowjones.io/public/rss/mw_topstories"),
            new("marketwatch-pulse", "MarketWatch Pulse", "markets", "https://feeds.content.dowjones.io/public/rss/mw_marketpulse"),
            new("coindesk", "CoinDesk", "crypto", "https://www.coindesk.com/arc/outboundfeeds/rss/"),
            new("cointelegraph", "Cointelegraph", "crypto", "https://cointelegraph.com/rss"),
            new("decrypt", "Decrypt", "crypto", "https://decrypt.co/feed"),
            new("fed", "Federal Reserve", "macro", "https://www.federalreserve.gov/feeds/press_all.xml"),
            new("ecb", "ECB", "macro", "https://www.ecb.europa.eu/rss/press.html"),
            new("boe", "Bank of England", "macro", "https://www.bankofengland.co.uk/rss/news"),
            new("sec", "SEC Press", "regulatory", "https://www.sec.gov/news/pressreleases.rss"),
        };

        public static NewsFeed SymbolFeed(string symbol) => new($"yahoo:{symbol}", "Yahoo Finance", "symbol",
            $"https://feeds.finance.yahoo.com/rss/2.0/headline?s={Uri.EscapeDataString(symbol)}&region=US&lang=en-US");

        public static NewsFeed SearchFeed(string query) => new($"google:{query}", "Google News", "search",
            $"https://news.google.com/rss/search?q={Uri.EscapeDataString(query)}&hl=en-GB&gl=GB&ceid=GB:en");

        public async Task<List<NewsItem>> GetFeedAsync(NewsFeed feed, CancellationToken ct = default)
        {
            string xml = await DataHttp.GetStringAsync($"rss:{feed.Name}", "news", feed.Url, ct: ct);
            return Parse(xml, feed);
        }

        public async Task<List<NewsItem>> GetManyAsync(IEnumerable<NewsFeed> feeds, int limit = 100, CancellationToken ct = default)
        {
            var all = new List<NewsItem>();
            await Task.WhenAll(feeds.Select(async f =>
            {
                try
                {
                    var items = await GetFeedAsync(f, ct);
                    lock (all) all.AddRange(items);
                }
                catch { /* one dead feed never empties the wire */ }
            }));
            return Dedupe(all).OrderByDescending(i => i.PublishedUtc).Take(limit).ToList();
        }

        internal static List<NewsItem> Parse(string xml, NewsFeed feed)
        {
            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch { return new List<NewsItem>(); }
            XNamespace atom = "http://www.w3.org/2005/Atom";
            var items = new List<NewsItem>();

            foreach (var item in doc.Descendants("item"))
            {
                string? title = Clean(item.Element("title")?.Value);
                string? link = item.Element("link")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;
                string source = item.Element("source")?.Value?.Trim() is { Length: > 0 } s ? s : feed.Name;
                // Google News titles end with " - Publisher".
                if (feed.Category == "search" && title.EndsWith(" - " + source, StringComparison.Ordinal))
                    title = title[..^(source.Length + 3)];
                items.Add(new NewsItem
                {
                    Title = title,
                    Url = link,
                    Source = source,
                    PublishedUtc = ParseDate(item.Element("pubDate")?.Value) ?? DateTime.UtcNow,
                    Summary = Summarise(item.Element("description")?.Value),
                    Feed = feed.Id,
                });
            }
            foreach (var entry in doc.Descendants(atom + "entry"))
            {
                string? title = Clean(entry.Element(atom + "title")?.Value);
                string? link = entry.Elements(atom + "link").FirstOrDefault()?.Attribute("href")?.Value;
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;
                items.Add(new NewsItem
                {
                    Title = title,
                    Url = link,
                    Source = feed.Name,
                    PublishedUtc = ParseDate(entry.Element(atom + "updated")?.Value ?? entry.Element(atom + "published")?.Value) ?? DateTime.UtcNow,
                    Summary = Summarise(entry.Element(atom + "summary")?.Value),
                    Feed = feed.Id,
                });
            }
            return items;
        }

        private static IEnumerable<NewsItem> Dedupe(IEnumerable<NewsItem> items)
        {
            var seenUrl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenTitle = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in items.OrderByDescending(x => x.PublishedUtc))
            {
                string key = Regex.Replace(i.Title.ToLowerInvariant(), "[^a-z0-9]", "");
                if (!seenUrl.Add(i.Url) || (key.Length > 12 && !seenTitle.Add(key))) continue;
                yield return i;
            }
        }

        private static string? Clean(string? s) => s == null ? null : WebUtility.HtmlDecode(Regex.Replace(s, "<.*?>", "")).Trim();

        private static string? Summarise(string? html)
        {
            var text = Clean(html);
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = Regex.Replace(text, "\\s+", " ");
            return text.Length <= 280 ? text : text[..277] + "…";
        }

        private static DateTime? ParseDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var d)) return d.UtcDateTime;
            // RFC-822 with a named zone ("GMT", "EST") that DateTimeOffset rejects.
            var trimmed = Regex.Replace(s, "\\s+[A-Z]{2,4}$", "");
            return DateTimeOffset.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var d2) ? d2.UtcDateTime : null;
        }
    }
}
