using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Ml.Classification;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Insights
{
    /// <summary>Something that sets a person apart from most people. <c>z</c>: how far from the average, in standard deviations.</summary>
    [Api.ApiContract]
    public sealed record PersonTraitDto(string Trait, string Text, double Z);

    /// <summary>A trait two people share: both are far from the average, in the same direction.</summary>
    [Api.ApiContract]
    public sealed record SharedTraitDto(string Trait, string Text, double Mine, double Theirs);

    /// <summary>
    /// Someone similar, and why: <c>similarity</c> is the weighted sum of four cosines (what they write about, how their
    /// messages read, how they write, and when), each given separately, with the traits and the places they share.
    /// </summary>
    [Api.ApiContract]
    public sealed record SimilarPersonDto(string PersonId, string? Name, double Similarity, double Content, double Tone, double Style, double Rhythm, IReadOnlyList<SharedTraitDto> Shared,
        IReadOnlyList<string> SharedPlaces, long Messages, string? CommunityId);

    /// <summary><c>note</c> says why there is nothing to compare (too few messages), otherwise it is null.</summary>
    [Api.ApiContract]
    public sealed record SimilarPeopleDto(string PersonId, string? Name, long Messages, long? AsOfMs, IReadOnlyList<PersonTraitDto> Traits, IReadOnlyList<SimilarPersonDto> People, string? Note);

    [Api.ApiContract]
    public sealed record MapPointDto(string PersonId, string? Name, double X, double Y, string? CommunityId, long Messages);

    /// <summary>The social world on a plane: people who are alike are near each other. Positions keep their place from night to night.</summary>
    [Api.ApiContract]
    public sealed record PeopleMapDto(IReadOnlyList<MapPointDto> Points, long? AsOfMs, int People);

    public sealed partial class InsightsModule
    {
        public const string PeopleKind = "people";
        /// <summary>The model name of the person vectors in the vector store.</summary>
        public const string PeopleModel = "people-v1";

        private const double ContentWeight = 0.35, ToneWeight = 0.25, StyleWeight = 0.2, RhythmWeight = 0.2;

        private static readonly Migration PeopleMigration = new(ModuleId, 4, "similar people and their map", """
CREATE TABLE ins_people(
  person_id TEXT PRIMARY KEY,
  messages INTEGER NOT NULL,
  traits_json TEXT NOT NULL,
  places_json TEXT NOT NULL,
  x REAL,
  y REAL,
  mapped INTEGER NOT NULL DEFAULT 0,
  computed_at_ms INTEGER NOT NULL
);
INSERT INTO ins_state(name, value) VALUES('people_content_dims', 0);
INSERT INTO ins_state(name, value) VALUES('people_tone_dims', 0);
INSERT INTO ins_state(name, value) VALUES('people_style_dims', 0);
""");

        /// <summary>The readings that describe how someone's messages come across (not what language, not about whom).</summary>
        private static readonly IReadOnlyList<(string Task, string Label, string Title)> ToneDims = ClassificationTasks.All
            .Where(t => t.Id != ClassificationTasks.Language.Id && t.Id != ClassificationTasks.AboutKlives.Id && t.Id != ClassificationTasks.Distress.Id)
            .SelectMany(t => t.Labels.Select(l => (t.Id, l.Id, t.Title))).ToList();

        /// <summary>How someone writes, as plain ratios. Each has a name for being far above and far below most people.</summary>
        private static readonly (string Id, string High, string Low)[] StyleTraits =
        {
            ("words_per_message", "long messages", "short messages"),
            ("chars_per_word", "long words", "short words"),
            ("reply_share", "many replies", "few replies"),
            ("attachment_share", "many pictures and files", "few pictures or files"),
            ("link_share", "many links", "few links"),
            ("edit_share", "many edits", "few edits"),
            ("delete_share", "many deletions", "few deletions"),
            ("dm_share", "mostly direct chats", "hardly any direct chats"),
            ("group_share", "mostly group chats", "hardly any group chats"),
            ("reactions_per_message", "many reactions", "few reactions"),
            ("mentions_per_message", "many mentions", "few mentions"),
            ("messages_per_active_day", "long bursts", "a few messages at a time"),
            ("active_day_share", "there most days", "there now and then"),
        };

        private static readonly (string Id, string High, string Low, int FromHour, int ToHour)[] RhythmTraits =
        {
            ("night_share", "up at night", "never up at night", 0, 5),
            ("morning_share", "mornings", "rarely mornings", 5, 11),
            ("evening_share", "evenings", "rarely evenings", 18, 24),
        };

        private static string TraitText(string trait, double z)
        {
            foreach (var t in StyleTraits)
                if (t.Id == trait) return z >= 0 ? t.High : t.Low;
            foreach (var t in RhythmTraits)
                if (t.Id == trait) return z >= 0 ? t.High : t.Low;
            if (trait.StartsWith("tone:", StringComparison.Ordinal))
            {
                string[] parts = trait.Split(':');
                var dim = ToneDims.FirstOrDefault(d => d.Task == parts[1] && d.Label == parts[2]);
                string label = parts[2].Replace('_', ' '), title = (dim.Title ?? parts[1]).ToLowerInvariant();
                return (z >= 0 ? "often " : "rarely ") + label + " (" + title + ")";
            }
            return trait;
        }

        private sealed class PersonFeatures
        {
            public required string Person { get; init; }
            public long Messages;
            public float[]? Content;
            public double[] Tone = Array.Empty<double>();
            public double[] Style = Array.Empty<double>();
            public double[] Hours = new double[24];
            public double[] Rhythm = Array.Empty<double>();
            public Dictionary<string, long> Guilds = new(StringComparer.Ordinal);
            public Dictionary<string, double> Traits = new(StringComparer.Ordinal);
            public float[] Vector = Array.Empty<float>();
        }

        /// <summary>Standardises each column across the rows (z-scores; a column nobody varies in becomes zero).</summary>
        private static void Standardise(IReadOnlyList<double[]> rows)
        {
            if (rows.Count == 0) return;
            int d = rows[0].Length;
            for (int j = 0; j < d; j++)
            {
                double mean = rows.Average(r => r[j]);
                double sd = Math.Sqrt(rows.Sum(r => (r[j] - mean) * (r[j] - mean)) / rows.Count);
                foreach (var r in rows) r[j] = sd < 1e-9 || rows.Count < 3 ? 0 : (r[j] - mean) / sd;
            }
        }

        private static void AppendBlock(List<float> into, IReadOnlyList<double> block, double weight)
        {
            double norm = Math.Sqrt(block.Sum(v => v * v));
            double scale = norm < 1e-12 ? 0 : Math.Sqrt(weight) / norm;
            foreach (double v in block) into.Add((float)(v * scale));
        }

        private async Task<string?> BuildPeopleAsync(JobContext job)
        {
            var ct = job.Cancellation;
            if (!HasTable("rollup_person_day")) return Json(new { skipped = "this build has no rollups" });
            long now = Context.Clock.NowMs;
            int min = Context.Settings.Get("insights.people.min_messages", 50), maxMap = Context.Settings.Get("insights.map.max_people", 2000);
            var today = Zones.LocalDate(now, Context.Settings.KlivesZone);
            string to = Day(today), from = Day(today.AddDays(-364));

            // ── who: enough messages in a year, still there, an adult, and not switched off ──
            List<(string Person, long N)> candidates;
            int? labelset;
            using (var intel = Context.Stores.Intel.OpenRead())
            {
                candidates = intel.Query(null, "SELECT person_id, SUM(authored) FROM rollup_person_day WHERE day >= $a AND day <= $b GROUP BY person_id HAVING SUM(authored) >= $m",
                    r => (r.GetString(0), r.GetInt64(1)), ("$a", from), ("$b", to), ("$m", min));
                labelset = ClassificationReader.ActiveLabelset(intel);
            }
            var off = Storage.AnalysisOff.Load(Context.Stores);
            HashSet<string> alive;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                alive = corpus.Query(null, "SELECT person_id FROM persons", r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
            candidates = candidates.Where(c => alive.Contains(c.Person) && !off.Person(c.Person) && !Restricted(c.Person, now))
                .OrderByDescending(c => c.N).ThenBy(c => c.Person, StringComparer.Ordinal).Take(20_000).ToList();

            // ── what is known of each ──
            string? encoder = Context.Encoder?.ModelVersion;
            var people = new List<PersonFeatures>(candidates.Count);
            foreach (var (person, _) in candidates)
            {
                ct.ThrowIfCancellationRequested();
                List<PersonDayRow> days;
                using (var intel = Context.Stores.Intel.OpenRead()) days = RollupReader.PersonDays(intel, person, from, to);
                long msgs = days.Sum(d => (long)d.Msgs), authored = Math.Max(1, days.Sum(d => (long)d.Authored)), words = days.Sum(d => d.Words), chars = days.Sum(d => d.Chars);
                var f = new PersonFeatures { Person = person, Messages = msgs };
                double m = Math.Max(1, msgs);
                int activeDays = days.Count(d => d.Msgs > 0);
                f.Style = new[]
                {
                    words / (double)authored, chars / (double)Math.Max(1, words), days.Sum(d => d.Replies) / m, days.Sum(d => d.WithAttachments) / m, days.Sum(d => d.WithLinks) / m,
                    days.Sum(d => d.Edited) / m, days.Sum(d => d.Deleted) / m, days.Sum(d => d.DmMsgs) / m, days.Sum(d => d.GroupMsgs) / m, days.Sum(d => d.ReactionsGiven) / m,
                    days.Sum(d => d.MentionsMade) / m, Math.Log(1 + msgs / (double)Math.Max(1, activeDays)), activeDays / 365.0,
                };
                foreach (var d in days)
                {
                    for (int h = 0; h < 24; h++) f.Hours[h] += d.Hours[h];
                    foreach (var (g, n) in d.Guilds) f.Guilds[g] = f.Guilds.GetValueOrDefault(g) + n;
                }
                var labels = RollupReader.SumLabels(days.Select(d => (d.Labels, d.Labelset)), labelset);
                f.Tone = ToneDims.Select(dim => labels.TryGetValue(dim.Task, out var t) && t.Values.Sum() > 0 ? t.GetValueOrDefault(dim.Label) / (double)t.Values.Sum() : 0).ToArray();
                if (encoder != null && Context.Vectors != null)
                {
                    List<string> ids;
                    using (var corpus = Context.Stores.Corpus.OpenRead())
                        ids = RollupModule.IdentitiesOf(corpus, person).SelectMany(i => corpus.Query(null,
                            "SELECT message_id FROM messages WHERE author_identity_id=$i AND deleted_at_ms IS NULL ORDER BY sent_at_ms DESC LIMIT 200", r => r.GetString(0), ("$i", i))).ToList();
                    var vectors = Context.Vectors.Get(VectorSpaces.Message, encoder, ids).Values.Select(v => v.Vector).Where(v => v.Length > 0).ToList();
                    if (vectors.Count > 0) f.Content = VectorMath.Normalized(VectorMath.Centroid(vectors, vectors[0].Length));
                }
                people.Add(f);
            }

            // ── features, normalised across everyone, then one vector per person ──
            int contentDims = people.Select(p => p.Content?.Length ?? 0).DefaultIfEmpty(0).Max();
            Standardise(people.Select(p => p.Tone).ToList());
            Standardise(people.Select(p => p.Style).ToList());
            foreach (var p in people)
            {
                double total = p.Hours.Sum();
                p.Rhythm = RhythmTraits.Select(t => total <= 0 ? 0 : p.Hours.Skip(t.FromHour).Take(t.ToHour - t.FromHour).Sum() / total).ToArray();
            }
            Standardise(people.Select(p => p.Rhythm).ToList());
            foreach (var p in people)
            {
                for (int i = 0; i < StyleTraits.Length; i++) p.Traits[StyleTraits[i].Id] = p.Style[i];
                for (int i = 0; i < RhythmTraits.Length; i++) p.Traits[RhythmTraits[i].Id] = p.Rhythm[i];
                for (int i = 0; i < ToneDims.Count; i++) p.Traits["tone:" + ToneDims[i].Task + ":" + ToneDims[i].Label] = p.Tone[i];
                var v = new List<float>(contentDims + ToneDims.Count + StyleTraits.Length + 24);
                AppendBlock(v, p.Content is { } c && c.Length == contentDims ? c.Select(x => (double)x).ToArray() : new double[contentDims], ContentWeight);
                AppendBlock(v, p.Tone, ToneWeight);
                AppendBlock(v, p.Style, StyleWeight);
                // When they write: the square root of the hour distribution, so that two people's dot product is how much their days overlap.
                double hours = p.Hours.Sum();
                AppendBlock(v, p.Hours.Select(h => hours <= 0 ? 0 : Math.Sqrt(h / hours)).ToArray(), RhythmWeight);
                p.Vector = VectorMath.Normalized(v.ToArray());
            }

            if (Context.Vectors != null && people.Count > 0)
                await Context.Vectors.UpsertAsync(VectorSpaces.Person, PeopleModel, people.Select(p => new VectorItem(p.Person, p.Vector, 1)).ToList(), ct).ConfigureAwait(false);

            // ── the map: the most active people are laid out, turned to face the way last night's map did; the rest are put near who they resemble ──
            var mapped = people.Take(maxMap).ToList();
            var position = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
            if (mapped.Count > 0)
            {
                Dictionary<string, (double X, double Y)> before;
                using (var intel = Context.Stores.Intel.OpenRead())
                    before = intel.Query(null, "SELECT person_id, x, y FROM ins_people WHERE x IS NOT NULL", r => (r.GetString(0), (r.GetDouble(1), r.GetDouble(2))))
                        .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
                var common = mapped.Select((p, i) => (p.Person, Index: i)).Where(x => before.ContainsKey(x.Person)).ToList();
                // When most of tonight's people were on last night's map, tonight's starts from it (a newcomer starts beside
                // whoever they resemble most), so the map changes only as much as the people did.
                List<(double X, double Y)>? start = null;
                if (common.Count >= 3 && common.Count * 2 >= mapped.Count)
                {
                    start = new List<(double X, double Y)>(mapped.Count);
                    for (int i = 0; i < mapped.Count; i++)
                    {
                        if (before.TryGetValue(mapped[i].Person, out var known)) { start.Add(known); continue; }
                        var like = common.OrderByDescending(x => VectorMath.Dot(mapped[i].Vector, mapped[x.Index].Vector)).ThenBy(x => x.Person, StringComparer.Ordinal).First();
                        double angle = i * 2.399963;
                        start.Add((before[like.Person].X + 0.01 * Math.Cos(angle), before[like.Person].Y + 0.01 * Math.Sin(angle)));
                    }
                }
                var layout = Layout2D.Tsne(mapped.Select(p => p.Vector).ToList(), start: start);
                Func<double, double, (double X, double Y)> place;
                if (common.Count >= 3)
                    place = Layout2D.Procrustes(common.Select(x => (layout[x.Index][0], layout[x.Index][1])).ToList(), common.Select(x => before[x.Person]).ToList());
                else place = (x, y) => (x, y); // the first map: as the layout made it (it is fitted to a unit circle when served)
                for (int i = 0; i < mapped.Count; i++) position[mapped[i].Person] = place(layout[i][0], layout[i][1]);
                foreach (var p in people.Skip(maxMap))
                {
                    var nearest = mapped.Select(q => (q.Person, Score: VectorMath.Dot(p.Vector, q.Vector))).OrderByDescending(x => x.Score).ThenBy(x => x.Person, StringComparer.Ordinal).Take(5).ToList();
                    double weight = nearest.Sum(x => Math.Max(0.01, x.Score));
                    position[p.Person] = (nearest.Sum(x => position[x.Person].X * Math.Max(0.01, x.Score)) / weight, nearest.Sum(x => position[x.Person].Y * Math.Max(0.01, x.Score)) / weight);
                }
            }

            var mappedIds = mapped.Select(p => p.Person).ToHashSet(StringComparer.Ordinal);
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "DELETE FROM ins_people");
                using var put = new PreparedStatement(c, tx, "INSERT INTO ins_people(person_id, messages, traits_json, places_json, x, y, mapped, computed_at_ms) VALUES($p,$m,$t,$g,$x,$y,$mp,$n)",
                    "$p", "$m", "$t", "$g", "$x", "$y", "$mp", "$n");
                foreach (var p in people)
                {
                    var traits = p.Traits.Where(kv => Math.Abs(kv.Value) >= 0.5).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2), StringComparer.Ordinal);
                    var places = p.Guilds.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(5).Select(kv => kv.Key).ToList();
                    bool has = position.TryGetValue(p.Person, out var xy);
                    put.Run(p.Person, p.Messages, JsonSerializer.Serialize(traits, KernelJson.Options), JsonSerializer.Serialize(places, KernelJson.Options), has ? Math.Round(xy.X, 5) : null,
                        has ? Math.Round(xy.Y, 5) : null, mappedIds.Contains(p.Person) ? 1 : 0, now);
                }
                foreach (var (name, value) in new[] { ("people_content_dims", contentDims), ("people_tone_dims", ToneDims.Count), ("people_style_dims", StyleTraits.Length) })
                    c.Exec(tx, "UPDATE ins_state SET value=$v WHERE name=$n", ("$v", value), ("$n", name));
            }, ct).ConfigureAwait(false);
            return Json(new { people = people.Count, mapped = mapped.Count, contentDims });
        }

        // ── reads ────────────────────────────────────────────────────────────────

        private static double BlockCosine(float[] a, float[] b, int start, int length)
        {
            if (length <= 0 || a.Length < start + length || b.Length < start + length) return 0;
            double dot = 0, na = 0, nb = 0;
            for (int i = start; i < start + length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            return na < 1e-12 || nb < 1e-12 ? 0 : Math.Round(dot / Math.Sqrt(na * nb), 4);
        }

        /// <summary>The people most like this one, each with the reasons (N16). Minors are never compared; someone whose
        /// analysis is switched off is neither compared nor offered as a match.</summary>
        public SimilarPeopleDto Similar(string personId, int limit = 12)
        {
            if (string.IsNullOrWhiteSpace(personId)) throw new ArgumentException("'personId' is required.");
            string person = Context.Policy.Canonical(personId.Trim());
            long now = Context.Clock.NowMs;
            limit = Math.Clamp(limit, 1, 50);
            string? name;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                if (corpus.Scalar<long>(null, "SELECT COUNT(*) FROM persons WHERE person_id=$p", ("$p", person)) == 0) throw new KeyNotFoundException($"No person '{personId}'.");
                name = corpus.Scalar<string>(null, "SELECT display_name FROM persons WHERE person_id=$p", ("$p", person));
            }
            if (Restricted(person, now)) throw new TacticRefusedException("They are, or may be, under 18: minors are not profiled or compared.");
            var off = Storage.AnalysisOff.Load(Context.Stores);
            if (off.Person(person)) throw new TacticRefusedException("Analysis is switched off for this person. It can be switched back on under Data controls.");

            Dictionary<string, (long Messages, Dictionary<string, double> Traits, List<string> Places, long At)> rows;
            int contentDims, toneDims, styleDims;
            Dictionary<string, string> community;
            using (var intel = Context.Stores.Intel.OpenRead())
            {
                rows = intel.Query(null, "SELECT person_id, messages, traits_json, places_json, computed_at_ms FROM ins_people", r => (r.GetString(0), (r.GetInt64(1),
                        JsonSerializer.Deserialize<Dictionary<string, double>>(r.GetString(2), KernelJson.Options) ?? new(), JsonSerializer.Deserialize<List<string>>(r.GetString(3), KernelJson.Options) ?? new(),
                        r.GetInt64(4)))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
                var dims = intel.Query(null, "SELECT name, value FROM ins_state WHERE name LIKE 'people_%'", r => (r.GetString(0), (int)r.GetInt64(1))).ToDictionary(x => x.Item1, x => x.Item2);
                (contentDims, toneDims, styleDims) = (dims.GetValueOrDefault("people_content_dims"), dims.GetValueOrDefault("people_tone_dims"), dims.GetValueOrDefault("people_style_dims"));
                community = intel.Query(null, "SELECT person_id, community_id FROM ins_members", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
            }
            int minMessages = Context.Settings.Get("insights.people.min_messages", 50);
            if (!rows.TryGetValue(person, out var mine) || Context.Vectors == null)
                return new SimilarPeopleDto(person, name, 0, null, Array.Empty<PersonTraitDto>(), Array.Empty<SimilarPersonDto>(),
                    $"There is not enough to compare yet: it takes at least {minMessages.ToString(CultureInfo.InvariantCulture)} messages in the last year, and the comparison is rebuilt at night.");
            var own = mine.Traits.Where(kv => Math.Abs(kv.Value) >= 1).OrderByDescending(kv => Math.Abs(kv.Value)).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(8)
                .Select(kv => new PersonTraitDto(kv.Key, TraitText(kv.Key, kv.Value), kv.Value)).ToList();
            var me = Context.Vectors.Get(VectorSpaces.Person, PeopleModel, new[] { person }).GetValueOrDefault(person);
            if (me == null) return new SimilarPeopleDto(person, name, mine.Messages, mine.At, own, Array.Empty<SimilarPersonDto>(), "The comparison is being rebuilt.");

            var hits = Context.Vectors.Search(VectorSpaces.Person, PeopleModel, me.Vector, limit + 25, new VectorFilter(Accept: id => id != person && rows.ContainsKey(id) && !off.Person(id)));
            var kept = hits.Where(h => !Restricted(h.ItemId, now)).Take(limit).ToList();
            var theirs = Context.Vectors.Get(VectorSpaces.Person, PeopleModel, kept.Select(h => h.ItemId).ToList());
            var names = Names(kept.Select(h => h.ItemId));
            Dictionary<string, string?> places;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                places = corpus.QueryIn(null, "SELECT guild_id, name FROM guilds WHERE guild_id IN ({keys})", mine.Places, r => (r.GetString(0), r.GetStringOrNull(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
            var similar = kept.Select(h =>
            {
                var other = rows[h.ItemId];
                var v = theirs.GetValueOrDefault(h.ItemId)?.Vector ?? Array.Empty<float>();
                var shared = mine.Traits.Where(kv => Math.Abs(kv.Value) >= 1 && other.Traits.TryGetValue(kv.Key, out double z) && Math.Abs(z) >= 1 && Math.Sign(z) == Math.Sign(kv.Value))
                    .OrderByDescending(kv => Math.Min(Math.Abs(kv.Value), Math.Abs(other.Traits[kv.Key]))).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(5)
                    .Select(kv => new SharedTraitDto(kv.Key, TraitText(kv.Key, kv.Value), kv.Value, other.Traits[kv.Key])).ToList();
                return new SimilarPersonDto(h.ItemId, names.GetValueOrDefault(h.ItemId), Math.Round(h.Score, 4), BlockCosine(me.Vector, v, 0, contentDims), BlockCosine(me.Vector, v, contentDims, toneDims),
                    BlockCosine(me.Vector, v, contentDims + toneDims, styleDims), BlockCosine(me.Vector, v, contentDims + toneDims + styleDims, 24), shared,
                    mine.Places.Where(other.Places.Contains).Select(g => places.GetValueOrDefault(g) ?? g).Take(3).ToList(), other.Messages, community.GetValueOrDefault(h.ItemId));
            }).ToList();
            return new SimilarPeopleDto(person, name, mine.Messages, mine.At, own, similar, null);
        }

        /// <summary>The map as of last night, without anyone who has since been marked a minor or switched off.</summary>
        public PeopleMapDto PeopleMap(int limit = 5000)
        {
            List<(string Person, long Messages, double X, double Y, long At)> rows;
            Dictionary<string, string> community;
            using (var intel = Context.Stores.Intel.OpenRead())
            {
                // Stored in the layout's own scale (tonight's map starts from it); served fitted to a unit circle.
                double radius = Math.Sqrt(intel.Scalar<double?>(null, "SELECT MAX(x * x + y * y) FROM ins_people WHERE x IS NOT NULL") ?? 0);
                if (radius < 1e-9) radius = 1;
                rows = intel.Query(null, "SELECT person_id, messages, x, y, computed_at_ms FROM ins_people WHERE x IS NOT NULL ORDER BY messages DESC, person_id LIMIT $l",
                    r => (r.GetString(0), r.GetInt64(1), Math.Round(r.GetDouble(2) / radius, 5), Math.Round(r.GetDouble(3) / radius, 5), r.GetInt64(4)), ("$l", Math.Clamp(limit, 1, 20_000)));
                community = intel.Query(null, "SELECT person_id, community_id FROM ins_members", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
            }
            var off = Storage.AnalysisOff.Load(Context.Stores);
            // The full check ran when the map was built; what Klives marked since is applied here in one read.
            HashSet<string> marked;
            using (var op = Context.Stores.Operator.OpenRead())
                marked = op.Query(null, "SELECT person_id FROM person_settings WHERE minor_marked=1", r => Context.Policy.Canonical(r.GetString(0))).ToHashSet(StringComparer.Ordinal);
            rows = rows.Where(r => !off.Person(r.Person) && !marked.Contains(r.Person)).ToList();
            var names = Names(rows.Select(r => r.Person));
            return new PeopleMapDto(rows.Select(r => new MapPointDto(r.Person, names.GetValueOrDefault(r.Person), r.X, r.Y, community.GetValueOrDefault(r.Person), r.Messages)).ToList(),
                rows.Count == 0 ? null : rows.Max(r => r.At), rows.Count);
        }
    }
}
