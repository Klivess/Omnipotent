using Omnipotent.Services.OmniscienceV3.Ingest;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    /// <summary>The kinds of place a conversation can be, as a filter offers them: a server's channel, a group chat, a direct chat.</summary>
    public static class ScopePlaces
    {
        public const string Server = "server";
        public const string Group = "group";
        public const string Direct = "dm";

        public static readonly IReadOnlyList<string> All = new[] { Server, Group, Direct };

        /// <summary>The conversation kinds a place covers. A conversation whose kind is unknown is in no place.</summary>
        public static IReadOnlyList<string> KindsOf(string place) => place switch
        {
            Server => new[] { ConversationKinds.Text, ConversationKinds.Thread, ConversationKinds.Channel, ConversationKinds.LegacyServerChannel },
            Group => new[] { ConversationKinds.GroupDm },
            Direct => new[] { ConversationKinds.Dm },
            _ => Array.Empty<string>(),
        };

        /// <summary>The place a conversation of this kind is in; null when the kind does not say.</summary>
        public static string? Of(string? kind) => kind switch
        {
            ConversationKinds.Text or ConversationKinds.Thread or ConversationKinds.Channel or ConversationKinds.LegacyServerChannel => Server,
            ConversationKinds.GroupDm => Group,
            ConversationKinds.Dm => Direct,
            _ => null,
        };
    }

    /// <summary>
    /// The filters every search shares: who, on which platform, in which server and conversation, in what kind of place, and
    /// when. Each "who" and "where" filter can name what to keep or what to leave out. All of it is optional; an empty filter
    /// changes nothing.
    /// <para>On the wire: <c>persons</c>, <c>guilds</c>, <c>conversations</c>, <c>platforms</c> (comma-separated ids) keep,
    /// their <c>not…</c> twins (<c>notPersons</c>, …) leave out, <c>places</c> is any of server, group, dm, and
    /// <c>from</c>/<c>to</c> are instants (a half-open window).</para>
    /// </summary>
    public sealed record ScopeFilter(
        IReadOnlyList<string> Persons, IReadOnlyList<string> NotPersons,
        IReadOnlyList<string> Guilds, IReadOnlyList<string> NotGuilds,
        IReadOnlyList<string> Conversations, IReadOnlyList<string> NotConversations,
        IReadOnlyList<string> Platforms, IReadOnlyList<string> NotPlatforms,
        IReadOnlyList<string> Places, long? FromMs, long? ToMs)
    {
        /// <summary>How many values one filter may list. A search narrowed to more than this is not a search a person typed.</summary>
        public const int MaxValues = 60;

        private static readonly IReadOnlyList<string> NoValues = Array.Empty<string>();

        public static readonly ScopeFilter None = new(NoValues, NoValues, NoValues, NoValues, NoValues, NoValues, NoValues, NoValues, NoValues, null, null);

        /// <summary>True when nothing but (perhaps) the time window is filtered.</summary>
        public bool OnlyTime => Persons.Count + NotPersons.Count + Guilds.Count + NotGuilds.Count + Conversations.Count + NotConversations.Count + Platforms.Count + NotPlatforms.Count
                                + Places.Count == 0;

        public bool IsEmpty => OnlyTime && FromMs == null && ToMs == null;

        /// <summary>
        /// Reads the shared filters from a query string. <paramref name="singlePerson"/> and <paramref name="singleConversation"/>
        /// name the older one-value parameters of a route (<c>person</c>, <c>personId</c>, <c>conversation</c>), which keep working.
        /// </summary>
        public static ScopeFilter Parse(ApiQuery q, string[]? singlePerson = null, string[]? singleConversation = null)
        {
            IReadOnlyList<string> With(string list, string[]? singles)
            {
                var values = q.List(list, MaxValues).ToList();
                foreach (string name in singles ?? Array.Empty<string>())
                    if (q.Str(name, 256) is { } one) values.Add(one);
                return values.Distinct(StringComparer.Ordinal).ToList();
            }

            IReadOnlyList<string> Platforms(string name)
            {
                var values = q.List(name, MaxValues).Select(p => p.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
                foreach (string p in values)
                    if (p.Length > 32 || !p.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')) throw ApiException.BadRequest($"'{name}' holds something that is not a platform name.");
                return values;
            }

            var places = q.List("places", 8).Select(p => p.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
            foreach (string place in places)
                if (!ScopePlaces.All.Contains(place)) throw ApiException.BadRequest($"'places' must be any of: {string.Join(", ", ScopePlaces.All)}.");
            // Every place asked for is the same as none asked for.
            if (places.Count == ScopePlaces.All.Count) places.Clear();

            var (from, to) = q.Window();
            return new ScopeFilter(
                With("persons", singlePerson), q.List("notPersons", MaxValues).Distinct(StringComparer.Ordinal).ToList(),
                q.List("guilds", MaxValues).Distinct(StringComparer.Ordinal).ToList(), q.List("notGuilds", MaxValues).Distinct(StringComparer.Ordinal).ToList(),
                With("conversations", singleConversation), q.List("notConversations", MaxValues).Distinct(StringComparer.Ordinal).ToList(),
                Platforms("platforms"), Platforms("notPlatforms"), places, from, to);
        }
    }

    /// <summary>
    /// Where a table keeps what the shared filters ask about (SQL expressions; null: the table does not say). Without a
    /// platform column the platform is read from the conversation id, which begins with it. Without a kind column a place is
    /// told by whether there is a server, and for the rest by looking the conversations up.
    /// </summary>
    public sealed record ScopeColumns(string? Person = null, string? Guild = null, string? Conversation = null, string? Platform = null, string? Time = null, string? Kind = null,
        string? SecondPerson = null);

    /// <summary>What the shared filters need to know from the corpus: every id a person has had, and the conversations of a kind.</summary>
    public sealed class ScopeResolver
    {
        /// <summary>How many conversations a place filter names at most when the table has no kind column.</summary>
        public const int MaxPlaceConversations = 5_000;

        private readonly OmniscienceStores stores;
        private readonly IPolicyOracle policy;

        public ScopeResolver(OmniscienceStores stores, IPolicyOracle policy)
        {
            this.stores = stores;
            this.policy = policy;
        }

        public string Canonical(string personId) => policy.Canonical(personId);

        /// <summary>The people as they are now, and every id that was merged into them: rows written before a merge still carry the old id.</summary>
        public IReadOnlyList<string> PersonIds(IReadOnlyList<string> persons)
        {
            if (persons.Count == 0) return Array.Empty<string>();
            var canonical = persons.Select(policy.Canonical).Distinct(StringComparer.Ordinal).ToList();
            using var c = stores.Corpus.OpenRead();
            var merged = c.QueryIn(null, "SELECT person_id FROM person_canonical WHERE canonical_id IN ({keys})", canonical, r => r.GetString(0));
            return canonical.Concat(merged).Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>The most recently active conversations of these kinds that belong to no server.</summary>
        public IReadOnlyList<string> ConversationsOutsideServers(IReadOnlyList<string> kinds)
        {
            if (kinds.Count == 0) return Array.Empty<string>();
            using var c = stores.Corpus.OpenRead();
            return c.QueryIn(null, $"SELECT conversation_id FROM conversations WHERE kind IN ({{keys}}) ORDER BY last_message_ms DESC LIMIT {MaxPlaceConversations}", kinds.ToList(), r => r.GetString(0));
        }
    }

    /// <summary>Turns the shared filters into WHERE clauses for one table.</summary>
    public static class ScopeSql
    {
        /// <summary>
        /// Adds the clauses and their bindings for <paramref name="filter"/>. A filter the table has no column for is refused
        /// rather than ignored: a search that silently drops a filter answers a different question.
        /// </summary>
        /// <param name="prefix">Begins every parameter name, so two filtered tables can share one statement.</param>
        public static void Apply(ScopeFilter filter, ScopeColumns columns, ScopeResolver resolver, List<string> where, List<(string, object?)> args, string prefix = "$sf")
        {
            if (filter.IsEmpty) return;

            string In(IReadOnlyList<string> values, string tag)
            {
                var names = values.Select((_, i) => prefix + tag + i).ToArray();
                for (int i = 0; i < names.Length; i++) args.Add((names[i], values[i]));
                return string.Join(",", names);
            }

            string Need(string? column, string what) => column ?? throw ApiException.BadRequest($"This search cannot be filtered by {what}.");

            if (filter.Persons.Count > 0)
            {
                string column = Need(columns.Person, "person");
                var ids = resolver.PersonIds(filter.Persons);
                string list = In(ids, "p");
                where.Add(columns.SecondPerson == null ? $"{column} IN ({list})" : $"({column} IN ({list}) OR {columns.SecondPerson} IN ({list}))");
            }
            if (filter.NotPersons.Count > 0)
            {
                string column = Need(columns.Person, "person");
                string list = In(resolver.PersonIds(filter.NotPersons), "np");
                where.Add($"({column} IS NULL OR {column} NOT IN ({list}))");
                if (columns.SecondPerson != null) where.Add($"({columns.SecondPerson} IS NULL OR {columns.SecondPerson} NOT IN ({list}))");
            }

            if (filter.Guilds.Count > 0) where.Add($"{Need(columns.Guild, "server")} IN ({In(filter.Guilds, "g")})");
            if (filter.NotGuilds.Count > 0)
            {
                string column = Need(columns.Guild, "server");
                where.Add($"({column} IS NULL OR {column} NOT IN ({In(filter.NotGuilds, "ng")}))");
            }

            if (filter.Conversations.Count > 0) where.Add($"{Need(columns.Conversation, "conversation")} IN ({In(filter.Conversations, "c")})");
            if (filter.NotConversations.Count > 0)
            {
                string column = Need(columns.Conversation, "conversation");
                where.Add($"({column} IS NULL OR {column} NOT IN ({In(filter.NotConversations, "nc")}))");
            }

            // Platform names were checked to be letters, digits, '_' and '-' when they were read, so one is safe inside a LIKE pattern.
            string Platform(IReadOnlyList<string> values, string tag)
            {
                if (columns.Platform != null) return $"{columns.Platform} IN ({In(values, tag)})";
                string conversation = Need(columns.Conversation, "platform");
                var names = values.Select((_, i) => prefix + tag + i).ToArray();
                for (int i = 0; i < names.Length; i++) args.Add((names[i], values[i] + ":%"));
                return "(" + string.Join(" OR ", names.Select(n => $"{conversation} LIKE {n}")) + ")";
            }
            if (filter.Platforms.Count > 0) where.Add(Platform(filter.Platforms, "pl"));
            if (filter.NotPlatforms.Count > 0) where.Add("NOT " + Platform(filter.NotPlatforms, "npl"));

            if (filter.Places.Count > 0)
            {
                if (columns.Kind != null)
                    where.Add($"{columns.Kind} IN ({In(filter.Places.SelectMany(ScopePlaces.KindsOf).ToList(), "k")})");
                else
                {
                    // No kind column: a server's channel is one with a server; a group or direct chat is looked up.
                    var either = new List<string>();
                    if (filter.Places.Contains(ScopePlaces.Server)) either.Add($"{Need(columns.Guild, "place")} IS NOT NULL");
                    var outside = filter.Places.Where(p => p != ScopePlaces.Server).ToList();
                    if (outside.Count == 2 && columns.Guild != null) either.Add($"{columns.Guild} IS NULL");
                    else if (outside.Count > 0)
                    {
                        var ids = resolver.ConversationsOutsideServers(outside.SelectMany(ScopePlaces.KindsOf).ToList());
                        either.Add(ids.Count == 0 ? "0" : $"{Need(columns.Conversation, "place")} IN ({In(ids, "kc")})");
                    }
                    where.Add("(" + string.Join(" OR ", either) + ")");
                }
            }

            if (filter.FromMs != null || filter.ToMs != null)
            {
                string column = Need(columns.Time, "time");
                if (filter.FromMs != null) { where.Add($"{column} >= {prefix}from"); args.Add((prefix + "from", filter.FromMs)); }
                if (filter.ToMs != null) { where.Add($"{column} < {prefix}to"); args.Add((prefix + "to", filter.ToMs)); }
            }
        }
    }
}
