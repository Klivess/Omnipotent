using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Explore;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Transforms
{
    /// <summary>X20 "Find similar": messages (or a description) → messages closest in meaning, inside whatever else is selected.</summary>
    public sealed class FindSimilarTactic : TacticBase
    {
        public const string Key = "x20.find_similar";
        private const int MaxScopeCandidates = 50_000;
        private readonly TransformsModule module;

        public FindSimilarTactic(TransformsModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X20",
            Title = "Find similar",
            Category = TacticCategory.Transform,
            Description = "Messages closest in meaning to the selected ones, or to a description. Also select people, conversations, servers or a time range to search only there.",
            Accepts = new[] { SelectionKind.Message, SelectionKind.Person, SelectionKind.Conversation, SelectionKind.Guild, SelectionKind.TimeRange },
            MinSelections = 0,
            MaxSelections = 5,
            ParameterSchemaJson = """
                {"type":"object","properties":{
                  "text":{"type":"string","maxLength":2000,"description":"What to look for, when no message is selected (or in addition to them)."},
                  "k":{"type":"integer","minimum":1,"maximum":500,"default":50},
                  "minScore":{"type":"number","minimum":0,"maximum":1,"default":0}}}
                """,
            Outputs = new[] { TacticOutputKind.MessageSet, TacticOutputKind.Report },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.messages.read",
            TypicalDurationMs = 1_500,
        };

        public override string? Validate(TacticInput input)
        {
            bool seeds = input.Selections.Any(s => s.Kind == SelectionKind.Message && s.Count > 0);
            if (!seeds && string.IsNullOrWhiteSpace(input.Param<string>("text"))) return "Select at least one message, or describe what to look for in 'text'.";
            if (input.Param<int?>("k") is { } k && (k < 1 || k > 500)) return "'k' must be between 1 and 500.";
            if (input.Param<double?>("minScore") is { } m && (m < 0 || m > 1)) return "'minScore' must be between 0 and 1.";
            if (input.Selections.GroupBy(s => s.Kind).Any(g => g.Count() > 1)) return "Give at most one selection of each kind.";
            return null;
        }

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var ctx = module.Ctx;
            if (ctx.Encoder == null || ctx.Vectors == null) throw new TacticRefusedException("Finding similar messages needs the shared encoder, which this runtime does not have.");
            int k = context.Input.Param<int?>("k") ?? 50;
            double minScore = context.Input.Param<double?>("minScore") ?? 0;
            string? text = context.Input.Param<string>("text")?.Trim();
            var seeds = context.Input.Selections.Where(s => s.Kind == SelectionKind.Message).SelectMany(s => s.Items.Select(i => i.Id)).Distinct(StringComparer.Ordinal).ToList();
            var scope = TransformsModule.ScopeOf(context);
            string model = ctx.Encoder.ModelVersion;

            var seedVectors = ctx.Vectors.Get(VectorSpaces.Message, model, seeds);
            var parts = seedVectors.Values.Select(v => VectorMath.Normalized(v.Vector)).ToList();
            if (!string.IsNullOrEmpty(text)) parts.Add(VectorMath.Normalized((await ctx.Encoder.EmbedAsync(new[] { text }, context.Cancellation).ConfigureAwait(false))[0]));
            if (parts.Count == 0) throw new TacticRefusedException("None of the selected messages has been embedded yet (messages without text never are). Try again later, or describe what to look for.");
            var query = VectorMath.Normalized(VectorMath.Centroid(parts, parts[0].Length));
            var seedSet = seeds.ToHashSet(StringComparer.Ordinal);

            IReadOnlyList<VectorHit> hits;
            bool exact = false;
            if (scope.Any)
            {
                // Inside a scope the search is exact over the scope's own messages (the newest when there are very many).
                var where = new List<string> { "m.deleted_at_ms IS NULL", "m.sent_at_ms <= $asof" };
                var args = new List<(string, object?)> { ("$asof", context.AsOfMs), ("$lim", MaxScopeCandidates) };
                void In(string column, HashSet<string> values, string prefix)
                {
                    if (values.Count == 0) return;
                    var (list, inArgs) = SqlHelpers.InList(values.ToList(), prefix);
                    where.Add($"{column} IN ({list})");
                    args.AddRange(inArgs);
                }
                In("m.conversation_id", scope.Conversations, "$c");
                In("m.guild_id", scope.Guilds, "$g");
                In("COALESCE(pc.canonical_id, i.person_id)", scope.Persons, "$p");
                if (scope.FromMs != null) { where.Add("m.sent_at_ms >= $f AND m.sent_at_ms < $t"); args.Add(("$f", scope.FromMs)); args.Add(("$t", scope.ToMs)); }
                List<string> candidates;
                using (var corpus = ctx.Stores.Corpus.OpenRead())
                    candidates = corpus.Query(null, $"""
                        SELECT m.message_id FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
                        WHERE {string.Join(" AND ", where)} ORDER BY m.sent_at_ms DESC LIMIT $lim
                        """, r => r.GetString(0), args.ToArray());
                hits = candidates.Count == 0 ? Array.Empty<VectorHit>()
                    : ctx.Vectors.Search(VectorSpaces.Message, model, query, Math.Min(candidates.Count, k + seeds.Count), new VectorFilter(Candidates: candidates, Accept: id => !seedSet.Contains(id)));
                exact = true;
            }
            else
            {
                hits = ctx.Vectors.Search(VectorSpaces.Message, model, query, Math.Min(2_000, k * 4 + seeds.Count), new VectorFilter(Probes: 32, Accept: id => !seedSet.Contains(id)));
            }

            Dictionary<string, TransformsModule.MessageInfo> info;
            using (var corpus = ctx.Stores.Corpus.OpenRead())
                info = corpus.QueryIn(null, "SELECT " + TransformsModule.InfoColumns + " WHERE m.message_id IN ({keys})", hits.Select(h => h.ItemId).ToList(), TransformsModule.ReadInfo)
                    .ToDictionary(m => m.Id, StringComparer.Ordinal);
            var kept = hits.Where(h => h.Score >= minScore && info.TryGetValue(h.ItemId, out var m) && m.DeletedAt == null && m.AtMs <= context.AsOfMs
                                       && scope.Matches(m.Person, m.Conversation, m.Guild, m.AtMs)).Take(k).ToList();
            var selection = new Selection(SelectionKind.Message, kept.Select(h => new SelectionItem(h.ItemId, Math.Round(h.Score, 4))).ToList(), "similar");
            return new TacticOutput[]
            {
                new(TacticOutputKind.MessageSet, selection, Summary: $"{kept.Count} similar messages"),
                new(TacticOutputKind.Report, Data: Json(new
                {
                    seeds = seedVectors.Count, seedsWithoutVector = seeds.Count - seedVectors.Count, text = string.IsNullOrEmpty(text) ? null : text, exactInScope = exact, encoder = model,
                    results = kept.Select(h => new { messageId = h.ItemId, score = Math.Round(h.Score, 4), conversationId = info[h.ItemId].Conversation, personId = info[h.ItemId].Person, atMs = info[h.ItemId].AtMs }),
                }), Summary: kept.Count == 0 ? "Nothing similar was found." : $"The closest scores {kept[0].Score:0.00}, the last one kept {kept[^1].Score:0.00}."),
            };
        }
    }

    /// <summary>X21 "Context expander": one message → what surrounded it, what it answered, what answered it, reactions, what came next.</summary>
    public sealed class ContextExpanderTactic : TacticBase
    {
        public const string Key = "x21.context";
        private readonly TransformsModule module;

        public ContextExpanderTactic(TransformsModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X21",
            Title = "Context",
            Category = TacticCategory.Transform,
            Description = "The conversation around one message: what came before and after, the message it replied to, the replies to it and the reactions it got.",
            Accepts = new[] { SelectionKind.Message },
            MinSelections = 1,
            MaxSelections = 1,
            ParameterSchemaJson = """
                {"type":"object","properties":{
                  "before":{"type":"integer","minimum":0,"maximum":200,"default":15},
                  "after":{"type":"integer","minimum":0,"maximum":200,"default":15}}}
                """,
            Outputs = new[] { TacticOutputKind.MessageSet, TacticOutputKind.Report },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.messages.read",
            TypicalDurationMs = 300,
        };

        public override string? Validate(TacticInput input)
        {
            if (input.Selections[0].Count != 1) return "Select exactly one message.";
            foreach (string p in new[] { "before", "after" })
                if (input.Param<int?>(p) is { } n && (n < 0 || n > 200)) return $"'{p}' must be between 0 and 200.";
            return null;
        }

        public override Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var ctx = module.Ctx;
            string id = context.Input.Selections[0].Items[0].Id;
            int before = context.Input.Param<int?>("before") ?? 15, after = context.Input.Param<int?>("after") ?? 15;
            using var corpus = ctx.Stores.Corpus.OpenRead();
            var target = corpus.Query(null, "SELECT " + TransformsModule.InfoColumns + " WHERE m.message_id=$m", TransformsModule.ReadInfo, ("$m", id)).FirstOrDefault();
            if (target == null || target.AtMs > context.AsOfMs) throw new TacticRefusedException($"There is no message '{id}'" + (target == null ? "." : " at that point in time."));
            long order = corpus.Scalar<long>(null, "SELECT order_key FROM messages WHERE message_id=$m", ("$m", id));
            var earlier = corpus.Query(null, "SELECT " + TransformsModule.InfoColumns + """
                 WHERE m.conversation_id=$c AND (m.sent_at_ms < $t OR (m.sent_at_ms = $t AND m.order_key < $o)) ORDER BY m.sent_at_ms DESC, m.order_key DESC LIMIT $l
                """, TransformsModule.ReadInfo, ("$c", target.Conversation), ("$t", target.AtMs), ("$o", order), ("$l", before));
            earlier.Reverse();
            var later = corpus.Query(null, "SELECT " + TransformsModule.InfoColumns + """
                 WHERE m.conversation_id=$c AND (m.sent_at_ms > $t OR (m.sent_at_ms = $t AND m.order_key > $o)) AND m.sent_at_ms <= $asof ORDER BY m.sent_at_ms, m.order_key LIMIT $l
                """, TransformsModule.ReadInfo, ("$c", target.Conversation), ("$t", target.AtMs), ("$o", order), ("$asof", context.AsOfMs), ("$l", after));
            var replies = corpus.Query(null, "SELECT " + TransformsModule.InfoColumns + " WHERE m.reply_to_message_id=$m AND m.sent_at_ms <= $asof ORDER BY m.sent_at_ms LIMIT 100", TransformsModule.ReadInfo,
                ("$m", id), ("$asof", context.AsOfMs));
            var parent = target.ReplyTo == null ? null
                : corpus.Query(null, "SELECT " + TransformsModule.InfoColumns + " WHERE m.message_id=$m", TransformsModule.ReadInfo, ("$m", target.ReplyTo)).FirstOrDefault();
            // Who reacted with what; where only a count was ever observed (an export, a snapshot), the count.
            var reactors = corpus.Query(null, """
                SELECT r.emoji_key, COALESCE(pc.canonical_id, i.person_id) FROM reactions r LEFT JOIN identities i ON i.identity_id=r.identity_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
                WHERE r.message_id=$m AND r.removed_at_ms IS NULL AND (r.added_at_ms IS NULL OR r.added_at_ms <= $asof)
                """, r => (Emoji: r.GetString(0), Person: r.GetStringOrNull(1)), ("$m", id), ("$asof", context.AsOfMs));
            var counted = corpus.Query(null, "SELECT emoji_key, count FROM reaction_counts WHERE message_id=$m", r => (Emoji: r.GetString(0), Count: r.GetInt32(1)), ("$m", id))
                .ToDictionary(x => x.Emoji, x => x.Count, StringComparer.Ordinal);
            var reactions = reactors.GroupBy(r => r.Emoji).Select(g => new { emoji = g.Key, count = Math.Max(g.Count(), counted.GetValueOrDefault(g.Key)), people = g.Select(x => x.Person).OfType<string>().Distinct().ToList() })
                .Concat(counted.Where(c => reactors.All(r => r.Emoji != c.Key)).Select(c => new { emoji = c.Key, count = c.Value, people = new List<string>() }))
                .OrderByDescending(r => r.count).ThenBy(r => r.emoji, StringComparer.Ordinal).ToList();
            int versions = (int)corpus.Scalar<long>(null, "SELECT current_version FROM messages WHERE message_id=$m", ("$m", id));

            var window = earlier.Append(target).Concat(later).ToList();
            var everyone = window.Concat(replies).Concat(parent == null ? Array.Empty<TransformsModule.MessageInfo>() : new[] { parent }).ToList();
            var names = TransformsModule.PersonNames(corpus, everyone.Select(m => m.Person));
            var self = ClassificationModule.SelfIdentities(corpus);
            object Line(TransformsModule.MessageInfo m) => new
            {
                messageId = m.Id, personId = m.Person, author = self.Contains(m.AuthorIdentity) ? "Klives" : m.Person != null ? names.GetValueOrDefault(m.Person) : null, isKlives = self.Contains(m.AuthorIdentity),
                atMs = m.AtMs, text = m.Text is { Length: > 600 } t ? t[..600] + "…" : m.Text, deleted = m.DeletedAt != null, bot = m.Bot, replyTo = m.ReplyTo,
            };
            IReadOnlyList<TacticOutput> outputs = new TacticOutput[]
            {
                new(TacticOutputKind.MessageSet, Selection.Of(SelectionKind.Message, window.Select(m => m.Id), "context:" + id), Summary: $"{earlier.Count} before, {later.Count} after"),
                new(TacticOutputKind.Report, Data: Json(new
                {
                    messageId = id, conversationId = target.Conversation, guildId = target.Guild, edits = Math.Max(0, versions - 1), parent = parent == null ? null : Line(parent),
                    parentMissing = target.ReplyTo != null && parent == null, replies = replies.Select(Line), reactions, window = window.Select(Line),
                    next = later.Take(3).Select(Line),
                }), Summary: $"{replies.Count} replies, {reactions.Sum(r => r.count)} reactions" + (parent != null ? ", itself a reply" : "") + "."),
            };
            return Task.FromResult(outputs);
        }
    }

    /// <summary>X22 "Phrase tracker": a phrase → who used it first and how it spread across people and servers over time.</summary>
    public sealed class PhraseTrackerTactic : TacticBase
    {
        public const string Key = "x22.phrase_tracker";
        private const long PickupWindowMs = 30 * 86_400_000L;
        private readonly TransformsModule module;

        public PhraseTrackerTactic(TransformsModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X22",
            Title = "Phrase tracker",
            Category = TacticCategory.Transform,
            Description = "Who used a phrase first, who picked it up from whom, and how its use grew month by month. Select servers, conversations or a time range to look only there.",
            Accepts = new[] { SelectionKind.Guild, SelectionKind.Conversation, SelectionKind.TimeRange },
            MinSelections = 0,
            MaxSelections = 3,
            ParameterSchemaJson = """
                {"type":"object","required":["phrase"],"properties":{
                  "phrase":{"type":"string","minLength":2,"maxLength":100,"description":"The words to track, as written (letter case and accents do not matter)."},
                  "maxMessages":{"type":"integer","minimum":100,"maximum":100000,"default":20000}}}
                """,
            Outputs = new[] { TacticOutputKind.Timeline, TacticOutputKind.MessageSet },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.messages.read",
            TypicalDurationMs = 2_000,
        };

        public override string? Validate(TacticInput input)
        {
            string? phrase = input.Param<string>("phrase")?.Trim();
            if (phrase is not { Length: >= 2 and <= 100 } || !phrase.Any(char.IsLetterOrDigit)) return "Give the phrase to track (2 to 100 characters, with at least one letter or digit).";
            if (input.Selections.GroupBy(s => s.Kind).Any(g => g.Count() > 1)) return "Give at most one selection of each kind.";
            return null;
        }

        public override Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var ctx = module.Ctx;
            string phrase = context.Input.Param<string>("phrase")!.Trim();
            int cap = context.Input.Param<int?>("maxMessages") ?? 20_000;
            var scope = TransformsModule.ScopeOf(context);
            // An FTS5 phrase: the words in order. Quotes inside it are doubled, so the phrase is data, never query syntax.
            string match = "\"" + phrase.Replace("\"", "\"\"") + "\"";
            List<TransformsModule.MessageInfo> uses;
            Dictionary<string, string?> names;
            // Who uses a phrase is a statement about people: someone whose analysis is switched off is not counted.
            var off = Storage.AnalysisOff.Load(ctx.Stores);
            using (var corpus = ctx.Stores.Corpus.OpenRead())
            {
                uses = corpus.Query(null, "SELECT " + TransformsModule.InfoColumns + """
                     JOIN messages_fts f ON f.rowid = m.rowid WHERE messages_fts MATCH $q AND m.deleted_at_ms IS NULL AND m.sent_at_ms <= $asof ORDER BY m.sent_at_ms, m.order_key LIMIT $l
                    """, TransformsModule.ReadInfo, ("$q", match), ("$asof", context.AsOfMs), ("$l", cap + 1))
                    .Where(m => !m.Bot && !off.Author(m.AuthorIdentity) && scope.Matches(m.Person, m.Conversation, m.Guild, m.AtMs)).ToList();
                names = TransformsModule.PersonNames(corpus, uses.Select(m => m.Person));
            }
            bool truncated = uses.Count > cap;
            if (truncated) uses = uses.Take(cap).ToList();
            if (uses.Count == 0)
            {
                IReadOnlyList<TacticOutput> none = new[] { new TacticOutput(TacticOutputKind.Timeline, Data: Json(new { phrase, uses = 0 }), Summary: $"Nobody has written \"{phrase}\" there.") };
                return Task.FromResult(none);
            }
            var zone = ctx.Settings.KlivesZone;
            var firstByPerson = uses.Where(m => m.Person != null).GroupBy(m => m.Person!).Select(g => g.First()).OrderBy(m => m.AtMs).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
            // Who a person most likely picked it up from: whoever last used it before them in the conversation where they first did.
            var edges = new List<object>();
            foreach (var adopt in firstByPerson.Skip(1))
            {
                var source = uses.LastOrDefault(u => u.Conversation == adopt.Conversation && u.Person != null && u.Person != adopt.Person && u.AtMs < adopt.AtMs && adopt.AtMs - u.AtMs <= PickupWindowMs);
                if (source != null) edges.Add(new { from = source.Person, to = adopt.Person, viaMessageId = source.Id, atMs = adopt.AtMs, gapMs = adopt.AtMs - source.AtMs });
            }
            var months = uses.GroupBy(m => Zones.LocalDate(m.AtMs, zone).ToString("yyyy-MM", CultureInfo.InvariantCulture)).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { month = g.Key, uses = g.Count(), people = g.Select(m => m.Person ?? m.AuthorIdentity).Distinct().Count() }).ToList();
            var guilds = uses.GroupBy(m => m.Guild ?? "").OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new { guildId = g.Key.Length == 0 ? null : g.Key, uses = g.Count(), firstAtMs = g.Min(m => m.AtMs), people = g.Select(m => m.Person ?? m.AuthorIdentity).Distinct().Count() }).ToList();
            var first = uses[0];
            IReadOnlyList<TacticOutput> outputs = new TacticOutput[]
            {
                new(TacticOutputKind.Timeline, Data: Json(new
                {
                    phrase, uses = uses.Count, truncated, people = firstByPerson.Count,
                    first = new { messageId = first.Id, personId = first.Person, name = first.Person == null ? null : names.GetValueOrDefault(first.Person), atMs = first.AtMs, conversationId = first.Conversation },
                    adopters = firstByPerson.Select((m, i) => new { rank = i + 1, personId = m.Person, name = names.GetValueOrDefault(m.Person!), firstAtMs = m.AtMs, messageId = m.Id,
                        uses = uses.Count(u => u.Person == m.Person) }),
                    months, guilds, pickups = edges,
                }), Summary: $"\"{phrase}\": {uses.Count} uses by {firstByPerson.Count} people; first by {names.GetValueOrDefault(first.Person ?? "") ?? "someone"}."),
                new(TacticOutputKind.MessageSet, Selection.Of(SelectionKind.Message, firstByPerson.Select(m => m.Id), "phrase:first-uses"), Summary: $"Each person's first use ({firstByPerson.Count})"),
            };
            return Task.FromResult(outputs);
        }
    }

    /// <summary>X37 "Closest contacts": a person → the people they interact with most, with what the ranking rests on and its trend.</summary>
    public sealed class ClosestContactsTactic : TacticBase
    {
        public const string Key = "x37.closest_contacts";
        private readonly TransformsModule module;

        public ClosestContactsTactic(TransformsModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X37",
            Title = "Closest contacts",
            Category = TacticCategory.Transform,
            Description = "Who a person interacts with most over a period (replies, mentions, direct messages and reactions, both ways), how mutual it is, and whether it is growing or fading.",
            Accepts = new[] { SelectionKind.Person },
            MinSelections = 1,
            MaxSelections = 1,
            ParameterSchemaJson = """
                {"type":"object","properties":{
                  "days":{"type":"integer","minimum":14,"maximum":3650,"default":180},
                  "top":{"type":"integer","minimum":1,"maximum":100,"default":15}}}
                """,
            Outputs = new[] { TacticOutputKind.Cohort, TacticOutputKind.Report },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.persons.read",
            MinorRestriction = RestrictedCapability.RoutineInference,
            TypicalDurationMs = 500,
        };

        public override string? Validate(TacticInput input)
        {
            if (input.Selections[0].Count != 1) return "Select exactly one person.";
            if (input.Param<int?>("days") is { } d && (d < 14 || d > 3650)) return "'days' must be between 14 and 3650.";
            if (input.Param<int?>("top") is { } t && (t < 1 || t > 100)) return "'top' must be between 1 and 100.";
            return null;
        }

        public override Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var ctx = module.Ctx;
            string person = context.CanonicalPerson(context.Input.Selections[0].Items[0].Id);
            int days = context.Input.Param<int?>("days") ?? 180, top = context.Input.Param<int?>("top") ?? 15;
            if (!module.HasTable("rollup_pair_day")) throw new TacticRefusedException("Closest contacts needs the rollups, which this build does not have.");
            long now = context.AsOfMs;
            int recentDays = Math.Min(30, days / 2);
            string from = module.Day(now - days * 86_400_000L), to = module.Day(now), recentFrom = module.Day(now - recentDays * 86_400_000L);
            List<PairTotals> all, recent;
            using (var intel = ctx.Stores.Intel.OpenRead())
            {
                all = RollupReader.Pairs(intel, person, from, to);
                recent = RollupReader.Pairs(intel, person, recentFrom, to);
            }
            // Minors are left out of rankings of who is close to whom, and the output says how many were.
            var allowed = all.Where(p => !module.Restricted(p.Partner, now)).ToList();
            int total = allowed.Sum(p => p.Interactions);
            var recentBy = recent.ToDictionary(p => p.Partner, p => p.Interactions, StringComparer.Ordinal);
            Dictionary<string, string?> names;
            using (var corpus = ctx.Stores.Corpus.OpenRead()) names = TransformsModule.PersonNames(corpus, allowed.Select(p => p.Partner));
            var ranked = allowed.Take(top).Select((p, i) =>
            {
                int outgoing = p.RepliesOut + p.MentionsOut + p.DmOut + p.ReactionsOut, incoming = p.RepliesIn + p.MentionsIn + p.DmIn + p.ReactionsIn;
                double earlierRate = (p.Interactions - recentBy.GetValueOrDefault(p.Partner)) / (double)Math.Max(1, days - recentDays), recentRate = recentBy.GetValueOrDefault(p.Partner) / (double)recentDays;
                // "Growing" and "fading" only when there is enough to compare: at least 10 interactions and a rate that at least doubled or halved.
                string trend = p.Interactions < 10 ? "too little to say" : recentRate >= earlierRate * 2 && recentRate > 0 ? "growing" : recentRate <= earlierRate / 2 ? "fading" : "steady";
                return new
                {
                    rank = i + 1, personId = p.Partner, name = names.GetValueOrDefault(p.Partner), interactions = p.Interactions, share = total == 0 ? 0 : Math.Round((double)p.Interactions / total, 3),
                    outgoing, incoming, reciprocity = Math.Max(outgoing, incoming) == 0 ? 0 : Math.Round((double)Math.Min(outgoing, incoming) / Math.Max(outgoing, incoming), 3),
                    replies = p.RepliesOut + p.RepliesIn, mentions = p.MentionsOut + p.MentionsIn, directMessages = p.DmOut + p.DmIn, reactions = p.ReactionsOut + p.ReactionsIn,
                    activeDays = p.ActiveDays, recentPerDay = Math.Round(recentRate, 3), earlierPerDay = Math.Round(earlierRate, 3), trend,
                };
            }).ToList();
            var cohort = new Selection(SelectionKind.Person, ranked.Select(r => new SelectionItem(r.personId, r.share, r.name)).ToList(), "closest:" + person);
            IReadOnlyList<TacticOutput> outputs = new TacticOutput[]
            {
                new(TacticOutputKind.Cohort, cohort, Summary: $"{ranked.Count} closest contacts"),
                new(TacticOutputKind.Report, Data: Json(new { personId = person, days, recentDays, contacts = ranked, partners = allowed.Count, leftOutAsMinors = all.Count - allowed.Count, interactions = total }),
                    Summary: ranked.Count == 0 ? "No interactions with anyone in this period." : $"{ranked[0].name ?? ranked[0].personId} first, with {ranked[0].share:P0} of {total} interactions."),
            };
            return Task.FromResult(outputs);
        }
    }

    /// <summary>X34 "Common ground": several people → the servers and conversations they share, the people they both know, what is between them.</summary>
    public sealed class CommonGroundTactic : TacticBase
    {
        public const string Key = "x34.common_ground";
        private readonly TransformsModule module;

        public CommonGroundTactic(TransformsModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X34",
            Title = "Common ground",
            Category = TacticCategory.Transform,
            Description = "What two to five people have in common: servers and conversations they both write in, people they both interact with, facts they share, and how much they interact with each other.",
            Accepts = new[] { SelectionKind.Person },
            MinSelections = 1,
            MaxSelections = 1,
            ParameterSchemaJson = """{"type":"object","properties":{"days":{"type":"integer","minimum":30,"maximum":3650,"default":365}}}""",
            Outputs = new[] { TacticOutputKind.Report, TacticOutputKind.Cohort },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.persons.read",
            MinorRestriction = RestrictedCapability.IdentityFacts,
            TypicalDurationMs = 800,
        };

        public override string? Validate(TacticInput input)
        {
            int n = input.Selections[0].Items.Select(i => i.Id).Distinct().Count();
            if (n < 2 || n > 5) return "Select two to five people.";
            if (input.Param<int?>("days") is { } d && (d < 30 || d > 3650)) return "'days' must be between 30 and 3650.";
            return null;
        }

        public override Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var ctx = module.Ctx;
            var people = context.Input.Selections[0].Items.Select(i => context.CanonicalPerson(i.Id)).Distinct(StringComparer.Ordinal).ToList();
            if (people.Count < 2) throw new TacticRefusedException("After merges these are the same person; select two different people.");
            if (!module.HasTable("rollup_pair_day")) throw new TacticRefusedException("Common ground needs the rollups, which this build does not have.");
            int days = context.Input.Param<int?>("days") ?? 365;
            long now = context.AsOfMs;
            string from = module.Day(now - days * 86_400_000L), to = module.Day(now);
            var guilds = new List<Dictionary<string, int>>();
            var conversations = new List<Dictionary<string, int>>();
            var partners = new List<Dictionary<string, PairTotals>>();
            using (var intel = ctx.Stores.Intel.OpenRead())
                foreach (string p in people)
                {
                    var rows = RollupReader.PersonDays(intel, p, from, to);
                    Dictionary<string, int> Sum(Func<PersonDayRow, Dictionary<string, int>> pick)
                    {
                        var sum = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (var r in rows)
                            foreach (var (k, n) in pick(r)) sum[k] = sum.GetValueOrDefault(k) + n;
                        return sum;
                    }
                    guilds.Add(Sum(r => r.Guilds));
                    conversations.Add(Sum(r => r.Conversations));
                    partners.Add(RollupReader.Pairs(intel, p, from, to).ToDictionary(x => x.Partner, StringComparer.Ordinal));
                }
            static List<(string Id, int Least)> Shared(List<Dictionary<string, int>> sets) =>
                sets[0].Keys.Where(k => sets.All(s => s.ContainsKey(k))).Select(k => (k, sets.Min(s => s[k]))).OrderByDescending(x => x.Item2).ThenBy(x => x.k, StringComparer.Ordinal).ToList();
            var sharedGuilds = Shared(guilds);
            var sharedConversations = Shared(conversations);
            // People all of them interact with (at least three times each), never the selected people themselves, never minors.
            var mutual = partners[0].Keys.Where(k => !people.Contains(k) && partners.All(s => s.TryGetValue(k, out var t) && t.Interactions >= 3) && !module.Restricted(k, now))
                .Select(k => (Id: k, Least: partners.Min(s => s[k].Interactions))).OrderByDescending(x => x.Least).ThenBy(x => x.Id, StringComparer.Ordinal).Take(30).ToList();
            var between = new List<object>();
            for (int i = 0; i < people.Count; i++)
                for (int j = i + 1; j < people.Count; j++)
                {
                    partners[i].TryGetValue(people[j], out var t);
                    between.Add(new { a = people[i], b = people[j], interactions = t?.Interactions ?? 0, replies = (t?.RepliesOut ?? 0) + (t?.RepliesIn ?? 0), directMessages = (t?.DmOut ?? 0) + (t?.DmIn ?? 0),
                        activeDays = t?.ActiveDays ?? 0 });
                }
            // Facts with the same attribute and value for every selected person (the dossier's filters apply to each).
            List<(string Person, string Attribute, string Value, string? Source, string? Modality)> facts;
            using (var intel = ctx.Stores.Intel.OpenRead())
                facts = intel.QueryIn(null, """
                    SELECT subject_id, attribute, value_text, source_kind, modality FROM assertions
                    WHERE subject_kind='person' AND subject_id IN ({keys}) AND superseded_at_ms IS NULL AND retracted_at_ms IS NULL AND status <> 'rejected' AND value_text IS NOT NULL
                    """, people, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetStringOrNull(4)));
            var sharedFacts = facts.Where(f => MinorGuard.AllowsAttribute(f.Attribute, ctx.Policy.BandOf(f.Person, now)) && SensitiveTraits.AllowsAssertion(f.Attribute, f.Source, f.Modality))
                .GroupBy(f => (f.Attribute, Value: f.Value.Trim().ToLowerInvariant())).Where(g => g.Select(f => f.Person).Distinct().Count() == people.Count)
                .Select(g => new { attribute = g.Key.Attribute, value = g.First().Value }).OrderBy(f => f.attribute, StringComparer.Ordinal).ThenBy(f => f.value, StringComparer.Ordinal).ToList();
            Dictionary<string, string?> names, guildNames, conversationTitles;
            using (var corpus = ctx.Stores.Corpus.OpenRead())
            {
                names = TransformsModule.PersonNames(corpus, people.Concat(mutual.Select(m => m.Id)));
                guildNames = corpus.QueryIn(null, "SELECT guild_id, name FROM guilds WHERE guild_id IN ({keys})", sharedGuilds.Select(g => g.Id).ToList(), r => (r.GetString(0), r.GetStringOrNull(1)))
                    .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
                conversationTitles = corpus.QueryIn(null, "SELECT conversation_id, title FROM conversations WHERE conversation_id IN ({keys})", sharedConversations.Take(30).Select(c => c.Id).ToList(),
                    r => (r.GetString(0), r.GetStringOrNull(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
            }
            IReadOnlyList<TacticOutput> outputs = new TacticOutput[]
            {
                new(TacticOutputKind.Report, Data: Json(new
                {
                    people = people.Select(p => new { personId = p, name = names.GetValueOrDefault(p) }), days,
                    guilds = sharedGuilds.Select(g => new { guildId = g.Id, name = guildNames.GetValueOrDefault(g.Id), leastMessages = g.Least }),
                    conversations = sharedConversations.Take(30).Select(c => new { conversationId = c.Id, title = conversationTitles.GetValueOrDefault(c.Id), leastMessages = c.Least }),
                    mutualContacts = mutual.Select(m => new { personId = m.Id, name = names.GetValueOrDefault(m.Id), leastInteractions = m.Least }),
                    between, sharedFacts,
                }), Summary: $"{sharedGuilds.Count} shared servers, {mutual.Count} mutual contacts, {sharedFacts.Count} shared facts."),
                new(TacticOutputKind.Cohort, new Selection(SelectionKind.Person, mutual.Select(m => new SelectionItem(m.Id, m.Least, names.GetValueOrDefault(m.Id))).ToList(), "common-ground:mutual"),
                    Summary: $"{mutual.Count} mutual contacts"),
            };
            return Task.FromResult(outputs);
        }
    }

    /// <summary>X28 "Cohort comparison": a set of people against everyone else, measure by measure, with significance.</summary>
    public sealed class CohortComparisonTactic : TacticBase
    {
        public const string Key = "x28.cohort_comparison";
        private readonly TransformsModule module;

        public CohortComparisonTactic(TransformsModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X28",
            Title = "Cohort comparison",
            Category = TacticCategory.Transform,
            Description = "How a group of people differs from everyone else who was active in the period: how much they write, how long, where, when, and in what mood. " +
                          "Each difference comes with its sample sizes and whether it holds up after correcting for the number of measures compared.",
            Accepts = new[] { SelectionKind.Person },
            MinSelections = 1,
            MaxSelections = 1,
            ParameterSchemaJson = """
                {"type":"object","properties":{
                  "days":{"type":"integer","minimum":14,"maximum":3650,"default":90},
                  "minMessages":{"type":"integer","minimum":1,"maximum":1000,"default":20,"description":"People with fewer messages in the period are left out of both sides."}}}
                """,
            Outputs = new[] { TacticOutputKind.Report },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.persons.read",
            MinorRestriction = RestrictedCapability.RoutineInference,
            TypicalDurationMs = 1_500,
        };

        public override string? Validate(TacticInput input)
        {
            if (input.Selections[0].Count < 3) return "A cohort needs at least three people to be compared with the rest.";
            if (input.Param<int?>("days") is { } d && (d < 14 || d > 3650)) return "'days' must be between 14 and 3650.";
            if (input.Param<int?>("minMessages") is { } m && (m < 1 || m > 1000)) return "'minMessages' must be between 1 and 1000.";
            return null;
        }

        private sealed record PersonMeasures(string Person, double PerActiveDay, double Words, double ReplyShare, double DirectShare, double NightShare, double Negative, double Positive, bool HasSentiment);

        public override Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var ctx = module.Ctx;
            if (!module.HasTable("rollup_person_day")) throw new TacticRefusedException("Comparing cohorts needs the rollups, which this build does not have.");
            int days = context.Input.Param<int?>("days") ?? 90, minMessages = context.Input.Param<int?>("minMessages") ?? 20;
            long now = context.AsOfMs;
            var cohort = context.Input.Selections[0].Items.Select(i => context.CanonicalPerson(i.Id)).ToHashSet(StringComparer.Ordinal);
            List<(string Person, int Msgs, int Authored, long Words, int Replies, int Dm, int Days, int Night, int Negative, int Positive, int Neutral)> rows;
            using (var intel = ctx.Stores.Intel.OpenRead())
                rows = intel.Query(null, """
                    SELECT person_id, SUM(msgs), SUM(authored), SUM(words), SUM(replies), SUM(dm_msgs), SUM(CASE WHEN msgs > 0 THEN 1 ELSE 0 END),
                           SUM(COALESCE(json_extract(hours,'$[0]'),0)+COALESCE(json_extract(hours,'$[1]'),0)+COALESCE(json_extract(hours,'$[2]'),0)+COALESCE(json_extract(hours,'$[3]'),0)+COALESCE(json_extract(hours,'$[4]'),0)),
                           SUM(COALESCE(json_extract(labels,'$.sentiment.negative'),0)), SUM(COALESCE(json_extract(labels,'$.sentiment.positive'),0)), SUM(COALESCE(json_extract(labels,'$.sentiment.neutral'),0))
                    FROM rollup_person_day WHERE day >= $a AND day <= $b GROUP BY person_id HAVING SUM(msgs) >= $min
                    """, r => (r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10)),
                    ("$a", module.Day(now - days * 86_400_000L)), ("$b", module.Day(now)), ("$min", minMessages));
            HashSet<string> self;
            using (var corpus = ctx.Stores.Corpus.OpenRead())
                self = corpus.Query(null, "SELECT COALESCE(pc.canonical_id, p.person_id) FROM persons p LEFT JOIN person_canonical pc ON pc.person_id=p.person_id WHERE p.is_self=1", r => r.GetString(0))
                    .ToHashSet(StringComparer.Ordinal);
            int minors = 0;
            var measured = new List<PersonMeasures>();
            foreach (var r in rows)
            {
                // Klives is not "everyone else", and minors are in neither side of a comparison of habits.
                if (self.Contains(r.Person) && !cohort.Contains(r.Person)) continue;
                if (module.Restricted(r.Person, now)) { minors++; continue; }
                int sentiment = r.Negative + r.Positive + r.Neutral;
                measured.Add(new PersonMeasures(r.Person, (double)r.Msgs / Math.Max(1, r.Days), r.Authored == 0 ? 0 : (double)r.Words / r.Authored, (double)r.Replies / r.Msgs, (double)r.Dm / r.Msgs,
                    (double)r.Night / r.Msgs, sentiment == 0 ? 0 : (double)r.Negative / sentiment, sentiment == 0 ? 0 : (double)r.Positive / sentiment, sentiment >= 10));
            }
            var inside = measured.Where(m => cohort.Contains(m.Person)).ToList();
            var outside = measured.Where(m => !cohort.Contains(m.Person)).ToList();
            if (inside.Count < 3 || outside.Count < 3)
                throw new TacticRefusedException($"Too few people to compare: {inside.Count} of the cohort and {outside.Count} others wrote at least {minMessages} messages in the last {days} days (three are needed on each side).");
            var measures = new (string Id, string Title, Func<PersonMeasures, double> Value, bool SentimentOnly)[]
            {
                ("per_active_day", "Messages per active day", m => m.PerActiveDay, false), ("words", "Words per message", m => m.Words, false), ("reply_share", "Share of messages that are replies", m => m.ReplyShare, false),
                ("direct_share", "Share written in direct chats", m => m.DirectShare, false), ("night_share", "Share written between 00:00 and 05:00 (their local time)", m => m.NightShare, false),
                ("negative_share", "Share of messages read as negative", m => m.Negative, true), ("positive_share", "Share of messages read as positive", m => m.Positive, true),
            };
            var results = measures.Select(m =>
            {
                var a = inside.Where(p => !m.SentimentOnly || p.HasSentiment).Select(m.Value).ToList();
                var b = outside.Where(p => !m.SentimentOnly || p.HasSentiment).Select(m.Value).ToList();
                var (_, z, p, effect) = Stats.MannWhitney(a, b);
                return (m.Id, m.Title, A: a, B: b, Z: z, P: p, Effect: effect);
            }).Where(r => r.A.Count >= 3 && r.B.Count >= 3).ToList();
            var significant = Stats.BenjaminiHochberg(results.Select(r => r.P).ToList());
            var rowsOut = results.Select((r, i) => new
            {
                measure = r.Id, title = r.Title, cohortMedian = Math.Round(Stats.Median(r.A), 4), restMedian = Math.Round(Stats.Median(r.B), 4), cohortPeople = r.A.Count, restPeople = r.B.Count,
                p = Math.Round(r.P, 5), effect = Math.Round(r.Effect, 3), significant = significant[i], direction = r.Effect > 0 ? "higher" : r.Effect < 0 ? "lower" : "same",
            }).ToList();
            var standing = rowsOut.Where(r => r.significant).ToList();
            IReadOnlyList<TacticOutput> outputs = new[]
            {
                new TacticOutput(TacticOutputKind.Report, Data: Json(new
                {
                    days, minMessages, cohortPeople = inside.Count, restPeople = outside.Count, cohortSelected = cohort.Count, leftOutAsMinors = minors, measures = rowsOut,
                    method = "Mann-Whitney U per measure (two-sided), Benjamini-Hochberg at 5% across the measures; effect is the rank-biserial correlation.",
                }), Summary: standing.Count == 0 ? $"No difference between the {inside.Count} people and the other {outside.Count} holds up."
                    : string.Join("; ", standing.Select(s => $"{s.title}: {s.direction} ({s.cohortMedian:0.###} vs {s.restMedian:0.###})")) + "."),
            };
            return Task.FromResult(outputs);
        }
    }

    /// <summary>X29 "Period comparison": two time ranges of a server, a conversation, a person or everything → what changed.</summary>
    public sealed class PeriodComparisonTactic : TacticBase
    {
        public const string Key = "x29.period_comparison";
        private readonly TransformsModule module;

        public PeriodComparisonTactic(TransformsModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X29",
            Title = "Period comparison",
            Category = TacticCategory.Transform,
            Description = "What changed between two periods: how much was written, by how many people, who appeared and who went quiet, and how the mix of each label shifted (with significance). " +
                          "Select a server, a conversation or a person to compare only that; with nothing else selected it compares everything.",
            Accepts = new[] { SelectionKind.TimeRange, SelectionKind.Guild, SelectionKind.Conversation, SelectionKind.Person },
            MinSelections = 1,
            MaxSelections = 2,
            Outputs = new[] { TacticOutputKind.Report },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.persons.read",
            MinorRestriction = RestrictedCapability.RoutineInference,
            TypicalDurationMs = 800,
        };

        public override string? Validate(TacticInput input)
        {
            var ranges = input.Selections.Where(s => s.Kind == SelectionKind.TimeRange).ToList();
            if (ranges.Count != 1 || ranges[0].Count != 2 || !ranges[0].Items.All(i => Selection.TryParseTimeRange(i.Id, out _, out _)))
                return "Give one time-range selection holding exactly two ranges (the earlier period and the later one), each as \"fromMs..toMs\".";
            var subject = input.Selections.Where(s => s.Kind != SelectionKind.TimeRange).ToList();
            if (subject.Count > 1 || (subject.Count == 1 && subject[0].Count != 1)) return "Select at most one server, conversation or person to compare.";
            return null;
        }

        public override Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var ctx = module.Ctx;
            if (!module.HasTable("rollup_scope_day")) throw new TacticRefusedException("Comparing periods needs the rollups, which this build does not have.");
            var ranges = context.Input.Selections.First(s => s.Kind == SelectionKind.TimeRange).Items.Select(i => { Selection.TryParseTimeRange(i.Id, out long f, out long t); return (From: f, To: t); })
                .OrderBy(r => r.From).ToList();
            var subject = context.Input.Selections.FirstOrDefault(s => s.Kind != SelectionKind.TimeRange);
            string kind = subject?.Kind switch { SelectionKind.Guild => "guild", SelectionKind.Conversation => "conversation", SelectionKind.Person => "person", _ => "global" };
            string key = subject == null ? "all" : kind == "person" ? context.CanonicalPerson(subject.Items[0].Id) : subject.Items[0].Id;

            (int Days, int Msgs, Dictionary<string, int> Persons, Dictionary<string, Dictionary<string, int>> Labels, int ActiveDays) Read((long From, long To) range)
            {
                // Days are whole days in Klives's zone: a period that ends at midnight does not include the day that starts then.
                string from = module.Day(range.From), to = module.Day(range.To - 1);
                int days = (int)Math.Max(1, Math.Round((range.To - range.From) / 86_400_000.0));
                using var intel = ctx.Stores.Intel.OpenRead();
                if (kind == "person")
                {
                    var rows = RollupReader.PersonDays(intel, key, from, to);
                    return (days, rows.Sum(r => r.Msgs), new Dictionary<string, int>(), RollupReader.SumLabels(rows.Select(r => (r.Labels, r.Labelset)), rows.LastOrDefault()?.Labelset), rows.Count(r => r.Msgs > 0));
                }
                var scope = RollupReader.ScopeDays(intel, kind, key, from, to);
                var persons = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var r in scope)
                    foreach (var (p, n) in r.Persons) persons[p] = persons.GetValueOrDefault(p) + n;
                return (days, scope.Sum(r => r.Msgs), persons, RollupReader.SumLabels(scope.Select(r => (r.Labels, r.Labelset)), scope.LastOrDefault()?.Labelset), scope.Count(r => r.Msgs > 0));
            }
            var a = Read(ranges[0]);
            var b = Read(ranges[1]);
            long now = context.AsOfMs;
            // Labels: one chi-square per task over its labels, corrected across tasks; the shift of each label is shown in shares.
            var tasks = a.Labels.Keys.Union(b.Labels.Keys).OrderBy(t => t, StringComparer.Ordinal).Where(t => !t.StartsWith(ClassificationModule.ConceptTaskPrefix, StringComparison.Ordinal)).ToList();
            var tested = tasks.Select(t =>
            {
                var la = a.Labels.GetValueOrDefault(t) ?? new Dictionary<string, int>();
                var lb = b.Labels.GetValueOrDefault(t) ?? new Dictionary<string, int>();
                var labels = la.Keys.Union(lb.Keys).OrderBy(l => l, StringComparer.Ordinal).ToList();
                var (chi, df, p, v) = Stats.ChiSquare(labels.Select(l => (long)la.GetValueOrDefault(l)).ToList(), labels.Select(l => (long)lb.GetValueOrDefault(l)).ToList());
                int ta = la.Values.Sum(), tb = lb.Values.Sum();
                return (Task: t, P: p, V: v, Ta: ta, Tb: tb, Shares: labels.Select(l => new { label = l, earlier = ta == 0 ? 0 : Math.Round((double)la.GetValueOrDefault(l) / ta, 4),
                    later = tb == 0 ? 0 : Math.Round((double)lb.GetValueOrDefault(l) / tb, 4) }).ToList());
            }).Where(t => t.Ta >= 20 && t.Tb >= 20).ToList();
            var significant = Stats.BenjaminiHochberg(tested.Select(t => t.P).ToList());
            var shifts = tested.Select((t, i) => new { task = t.Task, p = Math.Round(t.P, 5), cramersV = Math.Round(t.V, 3), significant = significant[i], answeredEarlier = t.Ta, answeredLater = t.Tb, labels = t.Shares }).ToList();
            // Members (not for a single person): who wrote in one period and not in the other; minors are counted, not named.
            var onlyEarlier = a.Persons.Keys.Where(p => !b.Persons.ContainsKey(p)).ToList();
            var onlyLater = b.Persons.Keys.Where(p => !a.Persons.ContainsKey(p)).ToList();
            Dictionary<string, string?> names;
            using (var corpus = ctx.Stores.Corpus.OpenRead()) names = TransformsModule.PersonNames(corpus, onlyEarlier.Concat(onlyLater));
            object People(List<string> ids, Dictionary<string, int> counts) => new
            {
                count = ids.Count,
                people = ids.Where(p => !module.Restricted(p, now)).OrderByDescending(p => counts[p]).ThenBy(p => p, StringComparer.Ordinal).Take(25).Select(p => new { personId = p, name = names.GetValueOrDefault(p), messages = counts[p] }),
            };
            double rateA = (double)a.Msgs / a.Days, rateB = (double)b.Msgs / b.Days;
            IReadOnlyList<TacticOutput> outputs = new[]
            {
                new TacticOutput(TacticOutputKind.Report, Data: Json(new
                {
                    subject = new { kind, id = key },
                    earlier = new { fromMs = ranges[0].From, toMs = ranges[0].To, days = a.Days, messages = a.Msgs, perDay = Math.Round(rateA, 2), activeDays = a.ActiveDays, people = a.Persons.Count },
                    later = new { fromMs = ranges[1].From, toMs = ranges[1].To, days = b.Days, messages = b.Msgs, perDay = Math.Round(rateB, 2), activeDays = b.ActiveDays, people = b.Persons.Count },
                    activityChange = rateA == 0 ? (double?)null : Math.Round(rateB / rateA - 1, 3),
                    wentQuiet = kind == "person" ? null : People(onlyEarlier, a.Persons), appeared = kind == "person" ? null : People(onlyLater, b.Persons), labelShifts = shifts,
                    method = "Chi-square per label task over the answered messages of each period, Benjamini-Hochberg at 5% across tasks; tasks with fewer than 20 answered messages in a period are left out.",
                }), Summary: a.Msgs + b.Msgs == 0 ? "Nothing was written in either period."
                    : $"{rateA:0.#} → {rateB:0.#} messages a day" + (kind == "person" ? "" : $", {onlyLater.Count} people appeared, {onlyEarlier.Count} went quiet")
                      + (shifts.Any(s => s.significant) ? "; shifted: " + string.Join(", ", shifts.Where(s => s.significant).Select(s => s.task)) : "; no label mix shifted significantly") + "."),
            };
            return Task.FromResult(outputs);
        }
    }

    /// <summary>X38 "Evidence bundle": any selection → a case file of pinned items with a note, exportable as Markdown or JSON.</summary>
    public sealed class EvidenceBundleTactic : TacticBase
    {
        public const string Key = "x38.evidence_bundle";
        private readonly TransformsModule module;

        public EvidenceBundleTactic(TransformsModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X38",
            Title = "Evidence bundle",
            Category = TacticCategory.Transform,
            Description = "Pin the selected messages, people, conversations, facts or images into a case file with a title and a note. Running it again with the bundle's id adds to it. " +
                          "A bundle exports as Markdown or JSON.",
            Accepts = new[] { SelectionKind.Message, SelectionKind.Person, SelectionKind.Conversation, SelectionKind.Assertion, SelectionKind.Attachment },
            MinSelections = 1,
            MaxSelections = 5,
            ParameterSchemaJson = """
                {"type":"object","properties":{
                  "title":{"type":"string","maxLength":160,"description":"Required for a new bundle."},
                  "note":{"type":"string","maxLength":8000},
                  "bundleId":{"type":"string","description":"Add to this bundle instead of creating one."}}}
                """,
            Outputs = new[] { TacticOutputKind.Report },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.persons.read",
            TypicalDurationMs = 300,
        };

        public override string? Validate(TacticInput input)
        {
            if (string.IsNullOrWhiteSpace(input.Param<string>("bundleId")) && string.IsNullOrWhiteSpace(input.Param<string>("title"))) return "A new bundle needs a title.";
            if (input.Selections.Sum(s => s.Count) == 0) return "Select something to put in the bundle.";
            return null;
        }

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var input = context.Input;
            string? bundleId = input.Param<string>("bundleId") is { Length: > 0 } b ? b : null;
            var items = input.Selections.SelectMany(s => s.Items.Select(i => new BundleItemDto(s.Kind.ToString().ToLowerInvariant(), s.Kind == SelectionKind.Person ? context.CanonicalPerson(i.Id) : i.Id, i.Label))).ToList();
            try
            {
                string title = input.Param<string>("title")?.Trim() is { Length: > 0 } t ? t : module.Bundle(bundleId!)?.Title ?? throw new KeyNotFoundException($"No bundle '{bundleId}'.");
                var saved = await module.SaveBundleAsync(bundleId, title, input.Param<string>("note"), items, context.Actor, context.Cancellation).ConfigureAwait(false);
                return new[]
                {
                    new TacticOutput(TacticOutputKind.Report, Data: Json(new { saved.BundleId, saved.Title, items = saved.Items.Count, added = items.Count, export = new { route = "bundles/export", formats = new[] { "markdown", "json" } } }),
                        Summary: $"\"{saved.Title}\" now holds {saved.Items.Count} items."),
                };
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
            {
                throw new TacticRefusedException(ex.Message);
            }
        }
    }

    /// <summary>X39 "Dataset export": an Explore result → a file. The file itself is streamed by the export route; the tactic
    /// checks the selection and returns exactly what to ask that route for, so it can be chained and offered by Ask.</summary>
    public sealed class DatasetExportTactic : TacticBase
    {
        public const string Key = "x39.dataset_export";
        private readonly TransformsModule module;

        public DatasetExportTactic(TransformsModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X39",
            Title = "Dataset export",
            Category = TacticCategory.Transform,
            Description = "Export the records behind an Explore result (a drill selection) as CSV, JSON lines or Parquet.",
            Accepts = new[] { SelectionKind.ExploreDrill },
            MinSelections = 1,
            MaxSelections = 1,
            ParameterSchemaJson = """{"type":"object","properties":{"format":{"type":"string","enum":["csv","json","parquet"],"default":"csv"}}}""",
            Outputs = new[] { TacticOutputKind.Dataset },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.data.export",
            TypicalDurationMs = 100,
        };

        public override string? Validate(TacticInput input)
        {
            if (input.Selections[0].Count != 1) return "Select exactly one Explore result to export.";
            if (input.Param<string>("format") is { } f && f.ToLowerInvariant() is not ("csv" or "json" or "parquet")) return "'format' must be csv, json or parquet.";
            return null;
        }

        public override Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            string token = context.Input.Selections[0].Items[0].Id;
            string format = (context.Input.Param<string>("format") ?? "csv").ToLowerInvariant();
            IReadOnlyList<TacticOutput> outputs = new[]
            {
                new TacticOutput(TacticOutputKind.Dataset, Data: Json(new { route = "explore/export", method = "POST", body = new { format = char.ToUpperInvariant(format[0]) + format[1..], drillToken = token } }),
                    Summary: $"Ready to download as {format}: post the body to the export route."),
            };
            return Task.FromResult(outputs);
        }
    }

    /// <summary>X40 "Custom chart": a dataset, dimensions and measures → a chart saved on the personal dashboard.</summary>
    public sealed class CustomChartTactic : TacticBase
    {
        public const string Key = "x40.custom_chart";
        private readonly TransformsModule module;

        public CustomChartTactic(TransformsModule module) => this.module = module;

        public override TacticDescriptor Descriptor { get; } = new()
        {
            Key = Key,
            Code = "X40",
            Title = "Custom chart",
            Category = TacticCategory.Transform,
            Description = "Save a chart of any dataset (grouped by its dimensions, showing its measures) on the personal dashboard. The chart is checked against what the dataset really offers before it is saved.",
            Accepts = new[] { SelectionKind.Dataset },
            MinSelections = 1,
            MaxSelections = 1,
            ParameterSchemaJson = """
                {"type":"object","required":["name","measures"],"properties":{
                  "name":{"type":"string","minLength":1,"maxLength":120},
                  "groupBy":{"type":"array","items":{"type":"string"},"maxItems":3,"description":"Dimension ids of the dataset."},
                  "measures":{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":4,"description":"Measure ids of the dataset."},
                  "filters":{"type":"array","description":"Explore filters, as the Explore API takes them."},
                  "bucket":{"type":"string","description":"Time bucket for a time dimension (hour, day, week, month, year, hour_of_day, weekday)."},
                  "chart":{"type":"string","enum":["bar","line","area","table","heatmap"],"default":"bar"},
                  "viewId":{"type":"string","description":"Replace this saved chart."}}}
                """,
            Outputs = new[] { TacticOutputKind.Dataset },
            Lane = Lane.Cpu,
            RequiredPermission = "omniscience.overview.view",
            TypicalDurationMs = 200,
        };

        public override string? Validate(TacticInput input)
        {
            if (input.Selections[0].Count != 1) return "Select exactly one dataset.";
            if (string.IsNullOrWhiteSpace(input.Param<string>("name"))) return "A chart needs a name.";
            if (input.Param<List<string>>("measures") is not { Count: >= 1 and <= 4 }) return "Give one to four measures.";
            if (input.Param<List<string>>("groupBy") is { Count: > 3 }) return "A chart can be grouped by at most three dimensions.";
            if (input.Param<string>("chart") is { } c && c is not ("bar" or "line" or "area" or "table" or "heatmap")) return "'chart' must be bar, line, area, table or heatmap.";
            return null;
        }

        public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
        {
            var input = context.Input;
            string datasetId = input.Selections[0].Items[0].Id;
            var dataset = module.Datasets.FirstOrDefault(d => d.Id == datasetId) ?? throw new TacticRefusedException($"There is no dataset '{datasetId}' in this build.");
            var groupBy = input.Param<List<string>>("groupBy") ?? new List<string>();
            var measures = input.Param<List<string>>("measures")!;
            var unknownDims = groupBy.Where(g => g != ExploreColumnKinds.BucketDimension && dataset.Dimensions.All(d => d.DimensionId != g)).ToList();
            if (unknownDims.Count > 0) throw new TacticRefusedException($"'{dataset.Title}' has no dimension {string.Join(", ", unknownDims)}. It offers: {string.Join(", ", dataset.Dimensions.Select(d => d.DimensionId))}.");
            var unknownMeasures = measures.Where(m => dataset.Measures.All(x => x.Id != m)).ToList();
            if (unknownMeasures.Count > 0) throw new TacticRefusedException($"'{dataset.Title}' has no measure {string.Join(", ", unknownMeasures)}. It offers: {string.Join(", ", dataset.Measures.Select(m => m.Id))}.");
            string name = input.Param<string>("name")!.Trim();
            string viewId = input.Param<string>("viewId") is { Length: > 0 } v ? v : "chart_" + Guid.NewGuid().ToString("N")[..12];
            long now = context.Clock.NowMs;
            string state = JsonSerializer.Serialize(new
            {
                kind = "chart", datasetId, groupBy, measures, filters = input.Param<JsonElement?>("filters"), bucket = input.Param<string>("bucket"), chart = input.Param<string>("chart") ?? "bar",
                requiredPermission = dataset.RequiredPermission,
            }, KernelJson.Options);
            await module.Ctx.Stores.Operator.WriteAsync((c, tx) =>
            {
                c.Exec(tx, """
                    INSERT INTO saved_views(view_id, page, name, state_json, created_at_ms, updated_at_ms) VALUES($v,$p,$n,$s,$t,$t)
                    ON CONFLICT(view_id) DO UPDATE SET name=excluded.name, state_json=excluded.state_json, updated_at_ms=excluded.updated_at_ms
                    """, ("$v", viewId), ("$p", TransformsModule.DashboardPage), ("$n", name.Length > 120 ? name[..120] : name), ("$s", state), ("$t", now));
                c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,$a,'chart.save',$g,$d)", ("$t", now), ("$a", context.Actor), ("$g", viewId), ("$d", state));
            }, context.Cancellation).ConfigureAwait(false);
            return new[] { new TacticOutput(TacticOutputKind.Dataset, Data: Json(new { viewId, name, page = TransformsModule.DashboardPage, datasetId }), Summary: $"Saved \"{name}\" on the dashboard.") };
        }
    }
}
