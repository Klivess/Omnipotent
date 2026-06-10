using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// What a person shares reveals what they consume: categorises shared URLs
    /// (news/video/music/social/gaming/shopping/dev/memes) with per-category counts and
    /// top domains. Facet splits show what they share where.
    /// </summary>
    public class LinkTaxonomyModule : IPersonAnalyticModule
    {
        public string Name => "link_taxonomy";
        public int Version => 1;

        private static readonly Regex Url = new(@"https?://([\w.-]+)", RegexOptions.Compiled);

        private static readonly (string Category, string[] Domains)[] Categories =
        {
            ("video",    new[]{ "youtube.com","youtu.be","twitch.tv","tiktok.com","vimeo.com","streamable.com","medal.tv","kick.com" }),
            ("music",    new[]{ "spotify.com","soundcloud.com","music.apple.com","bandcamp.com","genius.com" }),
            ("social",   new[]{ "twitter.com","x.com","instagram.com","facebook.com","reddit.com","threads.net","bsky.app","snapchat.com","linkedin.com" }),
            ("memes",    new[]{ "tenor.com","giphy.com","imgur.com","ifunny.co","9gag.com","knowyourmeme.com" }),
            ("gaming",   new[]{ "steampowered.com","steamcommunity.com","epicgames.com","roblox.com","minecraft.net","leagueoflegends.com","op.gg","tracker.gg" }),
            ("dev",      new[]{ "github.com","stackoverflow.com","gitlab.com","npmjs.com","pypi.org","developer.mozilla.org","learn.microsoft.com","huggingface.co" }),
            ("news",     new[]{ "bbc.co.uk","bbc.com","cnn.com","theguardian.com","reuters.com","apnews.com","nytimes.com","dailymail.co.uk","news.sky.com","telegraph.co.uk","independent.co.uk","foxnews.com" }),
            ("shopping", new[]{ "amazon.com","amazon.co.uk","ebay.com","ebay.co.uk","aliexpress.com","etsy.com","temu.com","vinted.co.uk" }),
            ("wiki",     new[]{ "wikipedia.org","fandom.com","wikihow.com" }),
            ("discord",  new[]{ "discord.gg","discord.com","cdn.discordapp.com","media.discordapp.net" }),
        };

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            return Task.FromResult(AnalyticSplits.Apply(msgs, ComputeFromMessages,
                AnalyticSplits.CompactWithArrays(5, "category_counts")));
        }

        internal static JObject ComputeFromMessages(List<AnalyticMessage> msgs)
        {
            var domainCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int totalLinks = 0, messagesWithLinks = 0;

            foreach (var m in msgs)
            {
                if (string.IsNullOrEmpty(m.Content) || !m.Content.Contains("http")) continue;
                bool any = false;
                foreach (Match u in Url.Matches(m.Content))
                {
                    string domain = u.Groups[1].Value.ToLowerInvariant();
                    if (domain.StartsWith("www.")) domain = domain[4..];
                    totalLinks++;
                    any = true;
                    domainCounts.TryGetValue(domain, out int dc); domainCounts[domain] = dc + 1;
                    string category = Categorise(domain);
                    categoryCounts.TryGetValue(category, out int cc); categoryCounts[category] = cc + 1;
                }
                if (any) messagesWithLinks++;
            }

            return new JObject(
                new JProperty("total_links", totalLinks),
                new JProperty("messages_with_links", messagesWithLinks),
                new JProperty("link_rate", msgs.Count == 0 ? 0 : (double)messagesWithLinks / msgs.Count),
                new JProperty("category_counts", new JArray(categoryCounts.OrderByDescending(c => c.Value)
                    .Select(c => new JObject(new JProperty("category", c.Key), new JProperty("count", c.Value))))),
                new JProperty("top_domains", new JArray(domainCounts.OrderByDescending(d => d.Value).Take(20)
                    .Select(d => new JObject(new JProperty("domain", d.Key), new JProperty("count", d.Value)))))
            );
        }

        private static string Categorise(string domain)
        {
            foreach (var (category, domains) in Categories)
                foreach (var d in domains)
                    if (domain == d || domain.EndsWith("." + d)) return category;
            return "other";
        }
    }
}
