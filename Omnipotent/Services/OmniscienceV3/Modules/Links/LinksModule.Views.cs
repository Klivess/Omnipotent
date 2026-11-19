using System.Globalization;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Links
{
    /// <summary>
    /// A link. <c>kind</c>: video, music, game, post, code, reference, gif, image, shop, invite or page. <c>metaSource</c>:
    /// <c>embed</c> (the preview that came with a message, as of <c>metaAtMs</c>) or <c>fetched</c> (asked from the provider
    /// at <c>metaAtMs</c>). A <c>sensitive</c> link is kept without its query and is never looked up.
    /// </summary>
    [Api.ApiContract]
    public sealed record LinkDto(string UrlId, string Url, string Host, string Site, string? Provider, string Kind, bool Sensitive, string? Title, string? Author, string? Description,
        string? MetaSource, long? MetaAtMs, string? Topic, string? Summary, int Shares, int Sharers, long? FirstSharedMs, long? LastSharedMs);

    /// <summary>One time a link was shared. The person is not named when they are, or may be, under 18.</summary>
    [Api.ApiContract]
    public sealed record LinkShareDto(string MessageId, string? PersonId, string? PersonName, string ConversationId, string? ConversationTitle, string? GuildId, long AtMs, int Reactions, LinkDto Link);

    [Api.ApiContract]
    public sealed record LinkCountDto(string Key, string? Label, int Shares);

    /// <summary>What someone shares: by kind, provider, site and topic over the last <c>days</c>, and the latest links.</summary>
    [Api.ApiContract]
    public sealed record PersonLinksDto(string PersonId, string? Name, int Days, int Shares, int Links, IReadOnlyList<LinkCountDto> Kinds, IReadOnlyList<LinkCountDto> Providers,
        IReadOnlyList<LinkCountDto> Sites, IReadOnlyList<LinkCountDto> Topics, IReadOnlyList<LinkShareDto> Recent);

    [Api.ApiContract]
    public sealed record LinkSearchDto(IReadOnlyList<LinkShareDto> Items, bool Truncated);

    /// <summary>
    /// Everything about one link, or about every link to one site (<c>targetKind</c>: url or site): who shared it first, who
    /// shared it at all, where, month by month, and how the messages that carried it were received.
    /// </summary>
    [Api.ApiContract]
    public sealed record LinkTrackDto(string Target, string TargetKind, LinkDto? Link, int Shares, int Sharers, int Reactions, long? FirstAtMs, long? LastAtMs, LinkShareDto? First,
        IReadOnlyList<LinkCountDto> People, IReadOnlyList<LinkCountDto> Places, IReadOnlyList<LinkCountDto> Months, IReadOnlyList<LinkDto> TopLinks, IReadOnlyList<LinkShareDto> Items, bool Truncated);

    public sealed partial class LinksModule
    {
        private const string UrlColumns = """
            u.url_id, u.url, u.host, u.site, u.provider, u.kind, u.sensitive, u.title, u.author, u.description, u.meta_source, u.meta_at_ms, u.topic, u.summary, u.shares, u.sharers,
            u.first_shared_ms, u.last_shared_ms
            """;

        private static LinkDto ReadUrl(SqliteDataReader r, int o = 0) => new(r.GetString(o), r.GetString(o + 1), r.GetString(o + 2), r.GetString(o + 3), r.GetStringOrNull(o + 4), r.GetString(o + 5),
            r.GetInt64(o + 6) == 1, r.GetStringOrNull(o + 7), r.GetStringOrNull(o + 8), r.GetStringOrNull(o + 9), r.GetStringOrNull(o + 10), r.GetInt64OrNull(o + 11), r.GetStringOrNull(o + 12),
            r.GetStringOrNull(o + 13), r.GetInt32(o + 14), r.GetInt32(o + 15), r.GetInt64OrNull(o + 16), r.GetInt64OrNull(o + 17));

        private sealed record ShareRow(string Message, string? Person, string Conversation, string? Guild, long At, LinkDto Link);

        private List<ShareRow> Shares(string where, IReadOnlyList<(string, object?)> args, string order, int limit)
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Query(null, $"SELECT s.message_id, s.person_id, s.conversation_id, s.guild_id, s.at_ms, {UrlColumns} FROM link_shares s JOIN link_urls u ON u.url_id=s.url_id WHERE {where} ORDER BY {order} LIMIT $lim",
                r => new ShareRow(r.GetString(0), r.GetStringOrNull(1), r.GetString(2), r.GetStringOrNull(3), r.GetInt64(4), ReadUrl(r, 5)), args.Append(("$lim", (object?)limit)).ToArray());
        }

        /// <summary>Names, places and reactions for a page of shares. People who are or may be minors are not named.</summary>
        private List<LinkShareDto> Present(IReadOnlyList<ShareRow> rows)
        {
            long now = Context.Clock.NowMs;
            var hidden = rows.Select(r => r.Person).OfType<string>().Distinct(StringComparer.Ordinal).Where(p => Restricted(p, now)).ToHashSet(StringComparer.Ordinal);
            Dictionary<string, string?> names, titles;
            var reactions = new Dictionary<string, int>(StringComparer.Ordinal);
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                names = corpus.QueryIn(null, "SELECT person_id, display_name FROM persons WHERE person_id IN ({keys})", rows.Select(r => r.Person).OfType<string>().Distinct(StringComparer.Ordinal).ToList(),
                    r => (r.GetString(0), r.GetStringOrNull(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
                titles = corpus.QueryIn(null, "SELECT conversation_id, title FROM conversations WHERE conversation_id IN ({keys})", rows.Select(r => r.Conversation).Distinct(StringComparer.Ordinal).ToList(),
                    r => (r.GetString(0), r.GetStringOrNull(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
                var ids = rows.Select(r => r.Message).Distinct(StringComparer.Ordinal).ToList();
                // The counts the platform reported, or the reactions seen one by one, whichever knows of more.
                foreach (var (m, n) in corpus.QueryIn(null, "SELECT message_id, SUM(count) FROM reaction_counts WHERE message_id IN ({keys}) GROUP BY message_id", ids, r => (r.GetString(0), r.GetInt32(1))))
                    reactions[m] = n;
                foreach (var (m, n) in corpus.QueryIn(null, "SELECT message_id, COUNT(*) FROM reactions WHERE removed_at_ms IS NULL AND message_id IN ({keys}) GROUP BY message_id", ids, r => (r.GetString(0), r.GetInt32(1))))
                    reactions[m] = Math.Max(reactions.GetValueOrDefault(m), n);
            }
            return rows.Select(r =>
            {
                bool hide = r.Person == null || hidden.Contains(r.Person);
                return new LinkShareDto(r.Message, hide ? null : r.Person, hide ? null : names.GetValueOrDefault(r.Person!), r.Conversation, titles.GetValueOrDefault(r.Conversation), r.Guild, r.At,
                    reactions.GetValueOrDefault(r.Message), r.Link);
            }).ToList();
        }

        private List<LinkCountDto> Counts(string column, string where, IReadOnlyList<(string, object?)> args, int limit)
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Query(null, $"SELECT {column}, COUNT(*) FROM link_shares s JOIN link_urls u ON u.url_id=s.url_id WHERE {where} AND {column} IS NOT NULL GROUP BY 1 ORDER BY 2 DESC, 1 LIMIT $lim",
                r => new LinkCountDto(r.GetString(0), null, r.GetInt32(1)), args.Append(("$lim", (object?)limit)).ToArray());
        }

        private string RequirePerson(string personId, out string? name)
        {
            if (string.IsNullOrWhiteSpace(personId)) throw new ArgumentException("'personId' is required.");
            string person = Context.Policy.Canonical(personId.Trim());
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                if (corpus.Scalar<long>(null, "SELECT COUNT(*) FROM persons WHERE person_id=$p", ("$p", person)) == 0) throw new KeyNotFoundException($"No person '{personId}'.");
                name = corpus.Scalar<string>(null, "SELECT display_name FROM persons WHERE person_id=$p", ("$p", person));
            }
            if (Restricted(person, Context.Clock.NowMs)) throw new TacticRefusedException("They are, or may be, under 18: what a minor shares is not profiled.");
            if (AnalysisOff.IsOff(Context.Stores, person)) throw new TacticRefusedException("Analysis is switched off for this person. It can be switched back on under Data controls.");
            return person;
        }

        /// <summary>A person's media diet (N15): what kinds of things they share, from where, about what, and the latest.</summary>
        public PersonLinksDto PersonLinks(string personId, int days = 90, int limit = 30)
        {
            string person = RequirePerson(personId, out string? name);
            days = Math.Clamp(days, 1, 3650);
            var args = new (string, object?)[] { ("$p", person), ("$f", Context.Clock.NowMs - days * 86_400_000L) };
            const string where = "s.person_id=$p AND s.at_ms >= $f";
            int shares, links;
            using (var c = Context.Stores.Intel.OpenRead())
                (shares, links) = c.Query(null, $"SELECT COUNT(*), COUNT(DISTINCT s.url_id) FROM link_shares s WHERE {where}", r => (r.GetInt32(0), r.GetInt32(1)), args).First();
            return new PersonLinksDto(person, name, days, shares, links, Counts("u.kind", where, args, 20), Counts("u.provider", where, args, 20), Counts("u.site", where, args, 20),
                Counts("u.topic", where, args, 20), Present(Shares(where, args, "s.at_ms DESC, s.message_id", Math.Clamp(limit, 1, 200))));
        }

        /// <summary>How many links a search with words, a site, a kind or a topic looks at: the most recently shared that match.</summary>
        public const int SearchLinkCap = 400;

        /// <summary>How many of the newest links matching the words are considered at all.</summary>
        public const int SearchWordCap = 2_000;

        /// <summary>
        /// "What did X share about Y": shares filtered by who, what words (in the address, title, author, description or
        /// summary; a word also matches the words it begins), site, kind, topic and time. Newest first.
        /// <para>Nothing here reads more than a bounded number of rows, however common the word or the site:</para>
        /// <list type="bullet">
        /// <item>With nothing said about the link, the shares are read newest first and the read stops at the limit.</item>
        /// <item>Words are looked up in the full-text index from its newest entries (a link's rowid rises with when it
        /// was first seen), <see cref="SearchWordCap"/> at most; only those links are then read.</item>
        /// <item>A site, kind or topic is read newest first from its own index.</item>
        /// <item>Of each candidate link only its newest shares are read, and the candidates are walked from the most
        /// recently shared until none left could hold a share newer than the ones already found.</item>
        /// </list>
        /// The first version of this joined and sorted every share per request; the second still looked up every match of
        /// a common word in order to sort it, and read every share of every candidate (150 seconds on the server's busy disk).
        /// </summary>
        public LinkSearchDto Search(string? query, string? personId, string? site, string? kind, string? topic, long? fromMs, long? toMs, int limit = 50)
        {
            limit = Math.Clamp(limit, 1, 500);
            var shareWhere = new List<string>();
            var shareArgs = new List<(string, object?)>();
            if (!string.IsNullOrWhiteSpace(personId))
            {
                shareWhere.Add("s.person_id=$p");
                shareArgs.Add(("$p", RequirePerson(personId, out _)));
            }
            if (fromMs != null) { shareWhere.Add("s.at_ms >= $f"); shareArgs.Add(("$f", fromMs)); }
            if (toMs != null) { shareWhere.Add("s.at_ms < $e"); shareArgs.Add(("$e", toMs)); }

            var words = (query ?? "").ToLowerInvariant()
                .Split(new[] { ' ', ',', '"', '\'', '(', ')', '*', '^', '+', '{', '}', '[', ']' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(w => w.Any(char.IsLetterOrDigit)).Take(6).ToList();
            string? siteKey = string.IsNullOrWhiteSpace(site) ? null : SiteKey(site);
            string? kindKey = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim().ToLowerInvariant();
            string? topicKey = string.IsNullOrWhiteSpace(topic) ? null : topic.Trim().ToLowerInvariant();

            const string columns = "s.message_id, s.person_id, s.conversation_id, s.guild_id, s.at_ms, ";
            static ShareRow Row(SqliteDataReader r) => new(r.GetString(0), r.GetStringOrNull(1), r.GetString(2), r.GetStringOrNull(3), r.GetInt64(4), ReadUrl(r, 5));
            string shareFilter = shareWhere.Count == 0 ? "" : " AND " + string.Join(" AND ", shareWhere);
            using var c = Context.Stores.Intel.OpenRead();

            if (words.Count == 0 && siteKey == null && kindKey == null && topicKey == null)
            {
                // CROSS JOIN keeps the shares as the outer loop: they are read in time order and the read stops at the limit.
                var newest = c.Query(null, $"""
                    SELECT {columns}{UrlColumns} FROM link_shares s CROSS JOIN link_urls u ON u.url_id=s.url_id
                    {(shareWhere.Count == 0 ? "" : "WHERE " + string.Join(" AND ", shareWhere))}
                    ORDER BY s.at_ms DESC, s.message_id LIMIT $lim
                    """, Row, shareArgs.Append(("$lim", (object?)(limit + 1))).ToArray());
                return new LinkSearchDto(Present(newest.Take(limit).ToList()), newest.Count > limit);
            }

            // 1. The links that match, most recently shared first.
            var (links, capped) = MatchingLinks(c, words, siteKey, kindKey, topicKey);
            if (links.Count == 0) return new LinkSearchDto(Array.Empty<LinkShareDto>(), false);

            // 2. Their newest shares. A link whose last share is older than the oldest share kept so far cannot add one.
            var best = new List<ShareRow>();
            foreach (var (urlId, lastShared) in links)
            {
                if (best.Count > limit && lastShared < best[^1].At) break;
                best.AddRange(c.Query(null, $"""
                    SELECT {columns}{UrlColumns} FROM link_shares s INDEXED BY ix_link_shares_url CROSS JOIN link_urls u ON u.url_id=s.url_id
                    WHERE s.url_id=$u{shareFilter}
                    ORDER BY s.at_ms DESC, s.message_id DESC LIMIT $lim
                    """, Row, shareArgs.Append(("$u", urlId)).Append(("$lim", (object?)(limit + 1))).ToArray()));
                best = best.OrderByDescending(r => r.At).ThenBy(r => r.Message, StringComparer.Ordinal).Take(limit + 1).ToList();
            }
            return new LinkSearchDto(Present(best.Take(limit).ToList()), best.Count > limit || capped);
        }

        /// <summary>
        /// Up to <see cref="SearchLinkCap"/> links matching the words and filters, most recently shared first, and whether
        /// more matched than were looked at. Each path starts from one index and stops at a cap.
        /// </summary>
        private static (List<(string UrlId, long LastSharedMs)> Links, bool Capped) MatchingLinks(SqliteConnection c, List<string> words, string? site, string? kind, string? topic)
        {
            var rest = new List<string>();
            var args = new List<(string, object?)>();
            void Also(string clause, string name, object value) { rest.Add(clause); args.Add((name, value)); }
            static (string, long) Link(SqliteDataReader r) => (r.GetString(0), r.GetInt64(1));
            const string select = "SELECT u.url_id, COALESCE(u.last_shared_ms, 0) FROM link_urls u";

            if (words.Count > 0)
            {
                // Each word is a quoted string to the index (nothing in it is read as query syntax); all of them must be there.
                string match = string.Join(" AND ", words.Select(w => "\"" + w + "\"*"));
                var rowids = c.Query(null, "SELECT rowid FROM link_words WHERE link_words MATCH $q ORDER BY rowid DESC LIMIT $cap", r => r.GetInt64(0),
                    ("$q", match), ("$cap", SearchWordCap + 1));
                bool more = rowids.Count > SearchWordCap;
                if (site != null) Also("(u.site=$s OR u.host=$s)", "$s", site);
                if (kind != null) Also("u.kind=$k", "$k", kind);
                if (topic != null) Also("u.topic=$t", "$t", topic);
                var found = new List<(string UrlId, long LastSharedMs)>();
                foreach (var chunk in rowids.Take(SearchWordCap).Chunk(400))
                    found.AddRange(c.Query(null, $"{select} WHERE u.rowid IN ({string.Join(",", chunk)}){(rest.Count == 0 ? "" : " AND " + string.Join(" AND ", rest))}", Link, args.ToArray()));
                var ordered = found.OrderByDescending(l => l.LastSharedMs).ThenBy(l => l.UrlId, StringComparer.Ordinal).ToList();
                return (ordered.Take(SearchLinkCap).ToList(), more || ordered.Count > SearchLinkCap);
            }

            // No words: newest first from the index of one filter, the others checked on the rows it yields.
            List<(string UrlId, long LastSharedMs)> Walk(string index, string first, string name, object value)
            {
                var all = new List<(string, object?)>(args) { (name, value), ("$lim", SearchLinkCap + 1) };
                return c.Query(null, $"{select} INDEXED BY {index} WHERE {first}{(rest.Count == 0 ? "" : " AND " + string.Join(" AND ", rest))} ORDER BY u.last_shared_ms DESC LIMIT $lim",
                    Link, all.ToArray());
            }
            List<(string UrlId, long LastSharedMs)> links;
            if (site != null)
            {
                if (kind != null) Also("u.kind=$k", "$k", kind);
                if (topic != null) Also("u.topic=$t", "$t", topic);
                // A site is matched as the site or as the exact host: one walk of each index, then the newer of the two.
                links = Walk("ix_link_urls_site", "u.site=$s", "$s", site).Concat(Walk("ix_link_urls_host", "u.host=$s", "$s", site))
                    .DistinctBy(l => l.UrlId).OrderByDescending(l => l.LastSharedMs).ThenBy(l => l.UrlId, StringComparer.Ordinal).ToList();
            }
            else if (kind != null)
            {
                if (topic != null) Also("u.topic=$t", "$t", topic);
                links = Walk("ix_link_urls_kind", "u.kind=$k", "$k", kind);
            }
            else links = Walk("ix_link_urls_topic", "u.topic=$t", "$t", topic!);
            return (links.Take(SearchLinkCap).ToList(), links.Count > SearchLinkCap);
        }

        /// <summary>A site as it is stored: no scheme, no path, no "www.".</summary>
        private static string SiteKey(string site)
        {
            string s = site.Trim().ToLowerInvariant();
            if (s.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(s, UriKind.Absolute, out var uri)) s = uri.IdnHost;
            int slash = s.IndexOf('/');
            if (slash >= 0) s = s[..slash];
            return s.StartsWith("www.", StringComparison.Ordinal) ? s[4..] : s;
        }

        /// <summary>
        /// The link tracker (X23): one address, or every link to one site, optionally inside given servers, conversations or a
        /// time range. A sensitive link can be tracked like any other (by what is kept of it); it is never looked up.
        /// </summary>
        public LinkTrackDto Track(string? url, string? site, IReadOnlyCollection<string>? guilds = null, IReadOnlyCollection<string>? conversations = null, long? fromMs = null, long? toMs = null,
            int limit = 200)
        {
            limit = Math.Clamp(limit, 1, 1000);
            var where = new List<string>();
            var args = new List<(string, object?)>();
            string target, targetKind;
            LinkDto? link = null;
            if (!string.IsNullOrWhiteSpace(url))
            {
                var parsed = LinkParser.Parse(url.Trim()) ?? throw new ArgumentException("'url' must be a web address (http or https).");
                (target, targetKind) = (parsed.Url, "url");
                where.Add("s.url_id=$u");
                args.Add(("$u", parsed.UrlId));
                using var c = Context.Stores.Intel.OpenRead();
                link = c.Query(null, $"SELECT {UrlColumns} FROM link_urls u WHERE u.url_id=$u", r => ReadUrl(r), ("$u", parsed.UrlId)).FirstOrDefault();
            }
            else if (!string.IsNullOrWhiteSpace(site))
            {
                (target, targetKind) = (SiteKey(site), "site");
                where.Add("(u.site=$s OR u.host=$s)");
                args.Add(("$s", target));
            }
            else throw new ArgumentException("Give the address to track in 'url', or a site in 'site'.");

            void In(string column, IReadOnlyCollection<string>? values, string prefix)
            {
                if (values is not { Count: > 0 }) return;
                var (list, inArgs) = SqlHelpers.InList(values.Take(400).ToList(), prefix);
                where.Add($"{column} IN ({list})");
                args.AddRange(inArgs);
            }
            In("s.guild_id", guilds, "$g");
            In("s.conversation_id", conversations, "$c");
            if (fromMs != null) { where.Add("s.at_ms >= $f"); args.Add(("$f", fromMs)); }
            if (toMs != null) { where.Add("s.at_ms < $e"); args.Add(("$e", toMs)); }
            string filter = string.Join(" AND ", where);

            int shares, sharers;
            long? firstAt, lastAt;
            List<LinkCountDto> people, places, months;
            List<LinkDto> top;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                (shares, sharers, firstAt, lastAt) = c.Query(null, $"SELECT COUNT(*), COUNT(DISTINCT s.person_id), MIN(s.at_ms), MAX(s.at_ms) FROM link_shares s JOIN link_urls u ON u.url_id=s.url_id WHERE {filter}",
                    r => (r.GetInt32(0), r.GetInt32(1), r.GetInt64OrNull(2), r.GetInt64OrNull(3)), args.ToArray()).First();
                top = targetKind == "site"
                    ? c.Query(null, $"SELECT {UrlColumns}, COUNT(*) AS n FROM link_shares s JOIN link_urls u ON u.url_id=s.url_id WHERE {filter} GROUP BY u.url_id ORDER BY n DESC, u.url LIMIT 10", r => ReadUrl(r), args.ToArray())
                    : new List<LinkDto>();
            }
            people = Counts("s.person_id", filter, args, 50);
            places = Counts("s.conversation_id", filter, args, 20);
            // Months in Klives's zone, from the shares themselves (bounded: the newest 20,000).
            var zone = Context.Settings.KlivesZone;
            var all = Shares(filter, args, "s.at_ms, s.message_id", 20_000);
            months = all.GroupBy(r => Zones.LocalDate(r.At, zone).ToString("yyyy-MM", CultureInfo.InvariantCulture)).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new LinkCountDto(g.Key, null, g.Count())).ToList();
            var items = Present(all.Take(limit).ToList());

            // People and places by name; a minor is counted under "not named".
            long now = Context.Clock.NowMs;
            Dictionary<string, string?> names, titles;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                names = corpus.QueryIn(null, "SELECT person_id, display_name FROM persons WHERE person_id IN ({keys})", people.Select(p => p.Key).ToList(), r => (r.GetString(0), r.GetStringOrNull(1)))
                    .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
                titles = corpus.QueryIn(null, "SELECT conversation_id, title FROM conversations WHERE conversation_id IN ({keys})", places.Select(p => p.Key).ToList(), r => (r.GetString(0), r.GetStringOrNull(1)))
                    .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
            }
            people = people.Select(p => Restricted(p.Key, now) ? new LinkCountDto("", "not named", p.Shares) : p with { Label = names.GetValueOrDefault(p.Key) })
                .GroupBy(p => p.Key, StringComparer.Ordinal).Select(g => g.First() with { Shares = g.Sum(x => x.Shares) }).OrderByDescending(p => p.Shares).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
            places = places.Select(p => p with { Label = titles.GetValueOrDefault(p.Key) }).ToList();
            return new LinkTrackDto(target, targetKind, link, shares, sharers, items.Sum(i => i.Reactions), firstAt, lastAt, items.FirstOrDefault(), people, places, months, top, items, all.Count > limit);
        }

        /// <summary>The links in these messages, in the order they were written (for a tactic that starts from a selection).</summary>
        internal List<string> UrlsOfMessages(IReadOnlyCollection<string> messageIds)
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.QueryIn(null, "SELECT u.url FROM link_shares s JOIN link_urls u ON u.url_id=s.url_id WHERE s.message_id IN ({keys}) ORDER BY s.at_ms, s.message_id, s.idx", messageIds.Take(400).ToList(), r => r.GetString(0))
                .Distinct(StringComparer.Ordinal).ToList();
        }
    }
}
