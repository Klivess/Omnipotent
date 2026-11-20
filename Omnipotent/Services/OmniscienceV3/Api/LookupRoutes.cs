using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniscienceV3.Ingest;
using Omnipotent.Services.OmniscienceV3.Modules.Links;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    /// <summary>
    /// One thing a filter can be set to: a server, a conversation, a platform, a site. <c>Label</c> is what a person reads;
    /// <c>Detail</c> says where it is (a channel's server, "Direct message"); <c>Kind</c> is the place for a conversation
    /// (server, group, dm); <c>GroupId</c>/<c>GroupLabel</c> name the server a channel belongs to.
    /// </summary>
    [ApiContract]
    public sealed record LookupOptionDto(string Id, string Label, string? Detail = null, string? Platform = null, string? Kind = null, string? GroupId = null, string? GroupLabel = null,
        long? Count = null, long? LastMs = null);

    /// <summary>The options a filter offers for what was typed, the most recently active first. <c>Truncated</c>: more match than are listed.</summary>
    [ApiContract]
    public sealed record LookupPageDto(IReadOnlyList<LookupOptionDto> Items, bool Truncated);

    /// <summary>Names for ids a page holds without them (a filter read back from a link). An id nobody knows is simply absent.</summary>
    [ApiContract]
    public sealed record LookupLabelsDto(IReadOnlyDictionary<string, string> Persons, IReadOnlyDictionary<string, string> Guilds, IReadOnlyDictionary<string, string> Conversations);

    /// <summary>
    /// What the filter pickers list: servers, conversations and platforms by name, so nobody has to know or paste an id.
    /// Everything here reads the small naming tables of the corpus (servers, conversations, their members), never messages.
    /// </summary>
    public sealed class Lookups
    {
        private readonly OmniscienceStores stores;

        public Lookups(OmniscienceStores stores) => this.stores = stores;

        private static string Like(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        /// <summary>A whole id ("discord:123"), or the platform's own number: either can be typed or pasted where a name is asked for.</summary>
        private static bool LooksLikeId(string text) => text.Contains(':') || (text.Length >= 5 && text.All(char.IsAsciiDigit));

        private static string PlatformTitle(string platform) => platform.Length == 0 ? platform : char.ToUpperInvariant(platform[0]) + platform[1..];

        /// <summary>Servers by name (current or past) or id, the most recently active first.</summary>
        public LookupPageDto Guilds(string? query, IReadOnlyList<string> platforms, IReadOnlyList<string> ids, int limit)
        {
            limit = Math.Clamp(limit, 1, 200);
            var where = new List<string>();
            var args = new List<(string, object?)>();
            if (ids.Count > 0)
            {
                var (list, inArgs) = Modules.SqlHelpers.InList(ids, "$i");
                where.Add($"g.guild_id IN ({list})");
                args.AddRange(inArgs);
            }
            if (platforms.Count > 0)
            {
                var (list, inArgs) = Modules.SqlHelpers.InList(platforms, "$pl");
                where.Add($"g.platform IN ({list})");
                args.AddRange(inArgs);
            }
            string order = "g.last_seen_ms DESC, g.guild_id";
            if (!string.IsNullOrWhiteSpace(query))
            {
                string q = query.Trim();
                var either = new List<string>
                {
                    "g.name LIKE $like ESCAPE '\\'",
                    "EXISTS (SELECT 1 FROM guild_names n WHERE n.guild_id = g.guild_id AND n.name LIKE $like ESCAPE '\\')",
                };
                if (LooksLikeId(q)) either.Add(q.Contains(':') ? "g.guild_id = $q" : "g.guild_id LIKE $tail ESCAPE '\\'");
                where.Add("(" + string.Join(" OR ", either) + ")");
                args.Add(("$like", "%" + Like(q) + "%"));
                args.Add(("$q", q));
                args.Add(("$tail", "%:" + Like(q)));
                args.Add(("$starts", Like(q) + "%"));
                order = "CASE WHEN g.name = $q COLLATE NOCASE THEN 0 WHEN g.name LIKE $starts ESCAPE '\\' THEN 1 ELSE 2 END, " + order;
            }
            args.Add(("$lim", limit + 1));
            using var c = stores.Corpus.OpenRead();
            var rows = c.Query(null, $"""
                SELECT g.guild_id, g.name, g.platform, g.last_seen_ms, (SELECT COUNT(*) FROM conversations cv WHERE cv.guild_id = g.guild_id)
                FROM guilds g {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))} ORDER BY {order} LIMIT $lim
                """, r => new LookupOptionDto(r.GetString(0), string.IsNullOrWhiteSpace(r.GetStringOrNull(1)) ? "Unnamed server" : r.GetString(1), PlatformTitle(r.GetString(2)), r.GetString(2),
                    ScopePlaces.Server, Count: r.GetInt64(4), LastMs: r.GetInt64OrNull(3)), args.ToArray());
            return new LookupPageDto(rows.Take(limit).ToList(), rows.Count > limit);
        }

        private const string CurrentName =
            "COALESCE((SELECT n.name FROM conversation_names n WHERE n.conversation_id = cv.conversation_id AND n.valid_to_ms IS NULL ORDER BY n.valid_from_ms DESC LIMIT 1), cv.title)";

        private sealed record Place(string Id, string Platform, string Kind, string? Guild, string? GuildName, string? Name, long? Last);

        /// <summary>
        /// Conversations by name or id, the most recently active first. A direct or group chat has no name of its own, so it
        /// is found by who is in it, and called by their names when <paramref name="namePeople"/> allows.
        /// </summary>
        public LookupPageDto Conversations(string? query, IReadOnlyList<string> guilds, IReadOnlyList<string> platforms, IReadOnlyList<string> places, IReadOnlyList<string> ids, int limit,
            bool namePeople)
        {
            limit = Math.Clamp(limit, 1, 200);
            var where = new List<string>();
            var args = new List<(string, object?)>();
            void In(string column, IReadOnlyList<string> values, string prefix)
            {
                if (values.Count == 0) return;
                var (list, inArgs) = Modules.SqlHelpers.InList(values, prefix);
                where.Add($"{column} IN ({list})");
                args.AddRange(inArgs);
            }
            In("cv.conversation_id", ids, "$i");
            In("cv.guild_id", guilds, "$g");
            In("cv.platform", platforms, "$pl");
            In("cv.kind", places.SelectMany(ScopePlaces.KindsOf).ToList(), "$k");

            using var c = stores.Corpus.OpenRead();
            string order = "cv.last_message_ms DESC, cv.conversation_id";
            if (!string.IsNullOrWhiteSpace(query))
            {
                string q = query.Trim();
                var either = new List<string> { $"{CurrentName} LIKE $like ESCAPE '\\'" };
                if (LooksLikeId(q)) either.Add(q.Contains(':') ? "cv.conversation_id = $q" : "cv.conversation_id LIKE $tail ESCAPE '\\'");
                if (namePeople)
                {
                    // Chats with someone whose name (now or before) begins with what was typed.
                    var withThem = c.Query(null, """
                        SELECT DISTINCT cm.conversation_id FROM identity_names n JOIN conversation_members cm ON cm.identity_id = n.identity_id
                        JOIN conversations cv ON cv.conversation_id = cm.conversation_id
                        WHERE n.value LIKE $starts ESCAPE '\' AND cv.kind IN ('dm','group_dm') LIMIT 400
                        """, r => r.GetString(0), ("$starts", Like(q) + "%"));
                    if (withThem.Count > 0)
                    {
                        var (list, inArgs) = Modules.SqlHelpers.InList(withThem, "$m");
                        either.Add($"cv.conversation_id IN ({list})");
                        args.AddRange(inArgs);
                    }
                }
                where.Add("(" + string.Join(" OR ", either) + ")");
                args.Add(("$like", "%" + Like(q) + "%"));
                args.Add(("$q", q));
                args.Add(("$tail", "%:" + Like(q)));
                args.Add(("$starts", Like(q) + "%"));
                order = $"CASE WHEN {CurrentName} = $q COLLATE NOCASE THEN 0 WHEN {CurrentName} LIKE $starts ESCAPE '\\' THEN 1 ELSE 2 END, " + order;
            }
            args.Add(("$lim", limit + 1));
            var rows = c.Query(null, $"""
                SELECT cv.conversation_id, cv.platform, cv.kind, cv.guild_id, g.name, {CurrentName}, cv.last_message_ms
                FROM conversations cv LEFT JOIN guilds g ON g.guild_id = cv.guild_id
                {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))} ORDER BY {order} LIMIT $lim
                """, r => new Place(r.GetString(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetStringOrNull(4), r.GetStringOrNull(5), r.GetInt64OrNull(6)), args.ToArray());
            var page = rows.Take(limit).ToList();
            var members = namePeople ? Members(c, page.Where(p => string.IsNullOrWhiteSpace(p.Name)).Select(p => p.Id).ToList()) : new();
            return new LookupPageDto(page.Select(p => Option(p, members.GetValueOrDefault(p.Id))).ToList(), rows.Count > limit);
        }

        /// <summary>Who is in each of these chats, Klives himself left out, by the name each person goes by now.</summary>
        private static Dictionary<string, List<string>> Members(Microsoft.Data.Sqlite.SqliteConnection c, IReadOnlyList<string> conversationIds)
        {
            var members = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (conversationIds.Count == 0) return members;
            foreach (var (conversation, name) in c.QueryIn(null, """
                         SELECT DISTINCT cm.conversation_id, p.display_name
                         FROM conversation_members cm JOIN identities i ON i.identity_id = cm.identity_id
                         LEFT JOIN person_canonical pc ON pc.person_id = i.person_id
                         JOIN persons p ON p.person_id = COALESCE(pc.canonical_id, i.person_id)
                         WHERE cm.conversation_id IN ({keys}) AND p.is_self = 0 AND p.display_name IS NOT NULL
                         ORDER BY cm.conversation_id, p.display_name
                         """, conversationIds, r => (r.GetString(0), r.GetString(1))))
                (members.TryGetValue(conversation, out var list) ? list : members[conversation] = new List<string>()).Add(name);
            return members;
        }

        private static LookupOptionDto Option(Place p, List<string>? members)
        {
            string? place = ScopePlaces.Of(p.Kind);
            string label;
            if (!string.IsNullOrWhiteSpace(p.Name)) label = p.Name!;
            else if (members is { Count: > 0 }) label = members.Count <= 3 ? string.Join(", ", members) : string.Join(", ", members.Take(3)) + $" and {members.Count - 3} more";
            else label = place == ScopePlaces.Direct ? "Direct chat" : place == ScopePlaces.Group ? "Group chat" : "Unnamed conversation";
            string detail = place switch
            {
                ScopePlaces.Server => p.GuildName ?? "A server",
                ScopePlaces.Group => "Group chat",
                ScopePlaces.Direct => "Direct chat",
                _ => p.GuildName ?? "Conversation",
            };
            if (p.Kind == ConversationKinds.Thread) detail += " · thread";
            return new LookupOptionDto(p.Id, label, detail, p.Platform, place, p.Guild, p.GuildName, LastMs: p.Last);
        }

        /// <summary>Every platform there are conversations on, the one with the most first.</summary>
        public LookupPageDto Platforms()
        {
            using var c = stores.Corpus.OpenRead();
            var rows = c.Query(null, "SELECT platform, COUNT(*) FROM conversations GROUP BY platform ORDER BY 2 DESC, 1",
                r => new LookupOptionDto(r.GetString(0), PlatformTitle(r.GetString(0)), Platform: r.GetString(0), Count: r.GetInt64(1)));
            return new LookupPageDto(rows, false);
        }

        /// <summary>The names of these ids. People are named only when <paramref name="namePeople"/> allows.</summary>
        public LookupLabelsDto Labels(IReadOnlyList<string> persons, IReadOnlyList<string> guilds, IReadOnlyList<string> conversations, bool namePeople)
        {
            var people = namePeople && persons.Count > 0 ? WsCRoutes.NamesOf(stores, persons.ToList()) : new Dictionary<string, string>(StringComparer.Ordinal);
            var servers = guilds.Count == 0 ? new Dictionary<string, string>(StringComparer.Ordinal)
                : Guilds(null, Array.Empty<string>(), guilds, 200).Items.ToDictionary(g => g.Id, g => g.Label, StringComparer.Ordinal);
            var places = new Dictionary<string, string>(StringComparer.Ordinal);
            if (conversations.Count > 0)
                foreach (var option in Conversations(null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), conversations, 200, namePeople).Items)
                    places[option.Id] = option.Kind == ScopePlaces.Server && option.GroupLabel != null ? $"{option.Label} · {option.GroupLabel}" : option.Label;
            return new LookupLabelsDto(people, servers, places);
        }
    }

    /// <summary>
    /// Routes under <c>/omniscience/v3/lookup/</c>: what the filter pickers offer. They need only overview access; whoever
    /// may not read people gets chats without the names of who is in them.
    /// </summary>
    public sealed class LookupRoutes
    {
        private readonly V3Host host;
        private readonly RouteRegistrar registrar;

        public LookupRoutes(V3Host host, RouteRegistrar registrar)
        {
            this.host = host;
            this.registrar = registrar;
        }

        public async Task RegisterAsync(CancellationToken ct)
        {
            await Get("lookup/guilds", req =>
            {
                var q = ApiQuery.Of(req);
                return ApiResponses.OkAsync(req, new Lookups(host.Require().Stores).Guilds(q.Str("q", 100), q.List("platforms", 20), q.List("ids", 200), q.Int("limit", 30, 1, 200)));
            }, ct).ConfigureAwait(false);

            await Get("lookup/conversations", req =>
            {
                var q = ApiQuery.Of(req);
                var places = q.List("places", 8).Select(p => p.ToLowerInvariant()).ToList();
                if (places.Any(p => !ScopePlaces.All.Contains(p))) throw ApiException.BadRequest($"'places' must be any of: {string.Join(", ", ScopePlaces.All)}.");
                return ApiResponses.OkAsync(req, new Lookups(host.Require().Stores).Conversations(q.Str("q", 100), q.List("guilds", ScopeFilter.MaxValues), q.List("platforms", 20), places,
                    q.List("ids", 200), q.Int("limit", 30, 1, 200), req.Can(OmnisciencePerms.PersonsRead)));
            }, ct).ConfigureAwait(false);

            await Get("lookup/platforms", req => ApiResponses.OkAsync(req, new Lookups(host.Require().Stores).Platforms()), ct).ConfigureAwait(false);

            await Get("lookup/sites", req =>
            {
                var q = ApiQuery.Of(req);
                var links = host.Require().Host.Modules.OfType<LinksModule>().FirstOrDefault();
                return ApiResponses.OkAsync(req, links == null ? new LookupPageDto(Array.Empty<LookupOptionDto>(), false) : links.Sites(q.Str("q", 100), q.Int("limit", 30, 1, 200)));
            }, ct).ConfigureAwait(false);

            await Get("lookup/labels", req =>
            {
                var q = ApiQuery.Of(req);
                return ApiResponses.OkAsync(req, new Lookups(host.Require().Stores).Labels(q.List("persons", 200), q.List("guilds", 200), q.List("conversations", 200),
                    req.Can(OmnisciencePerms.PersonsRead)));
            }, ct).ConfigureAwait(false);
        }

        private Task Get(string path, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(V3Routes.Prefix + "/" + path, HttpMethod.Get, OmnisciencePerms.OverviewView, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);
    }
}
