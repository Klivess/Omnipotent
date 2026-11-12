using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>Generation settings tuned per person (plan "auto-tuning of k, temperature, N and burst sampling").</summary>
    public sealed record GenerationConfig(double Temperature = 0.9, double TopP = 0.95, int Candidates = 3, int Retrieved = 24, bool BurstSampling = true, bool LlmJudge = true)
    {
        public static GenerationConfig Default { get; } = new();

        public string Key => FormattableString.Invariant($"t{Temperature:0.00}-p{TopP:0.00}-n{Candidates}-k{Retrieved}-b{(BurstSampling ? 1 : 0)}-j{(LlmJudge ? 1 : 0)}");
    }

    /// <summary>
    /// One replica version loaded for serving: its pairs (every split, chronological), persona items, eras, tuned config,
    /// vectors for the encoder it was built with, and a BM25 index over the visible stimuli. Immutable once loaded, so
    /// the same version + as-of always composes the same prompt (plan T9). Cached per version by <see cref="SnapshotCache"/>.
    /// </summary>
    public sealed class ReplicaSnapshot
    {
        private ReplicaSnapshot() { }

        public required string VersionId { get; init; }
        public required string PersonId { get; init; }
        public required int Number { get; init; }
        public required PersonScope Person { get; init; }
        public required IReadOnlyList<ReplicaPair> Pairs { get; init; }
        /// <summary>Train split only: the only pairs any prompt may show (holdout and shadows are invisible everywhere).</summary>
        public required IReadOnlyList<ReplicaPair> Visible { get; init; }
        public required IReadOnlyList<Era> Eras { get; init; }
        public required IReadOnlyList<PersonaItem> Persona { get; init; }
        public required GenerationConfig Config { get; init; }
        public required IReadOnlyDictionary<string, float[]> StimulusVectors { get; init; }
        public required IReadOnlyDictionary<string, float[]> ReplyVectors { get; init; }
        public string? EncoderModel { get; init; }
        public long DataToMs { get; init; }
        public long? AdultFromMs { get; init; }
        public long SourceSeq { get; init; }
        public IReadOnlyList<string> Degraded { get; init; } = Array.Empty<string>();
        public required Bm25Index StimulusIndex { get; init; }
        public required IReadOnlyList<ReplicaPair> StimulusDocs { get; init; }
        public IReadOnlyDictionary<string, ReplicaPair> ById { get; private init; } = new Dictionary<string, ReplicaPair>();
        /// <summary>Token counts of what others wrote to the person (background for distinctive vocabulary).</summary>
        public required IReadOnlyDictionary<string, int> BackgroundTokens { get; init; }

        public bool HasVectors => StimulusVectors.Count > 0;

        /// <summary>Visible pairs the person had finished writing by <paramref name="asOfMs"/> (a burst straddling the cutoff is excluded).</summary>
        public IEnumerable<ReplicaPair> VisibleAsOf(long asOfMs) => Visible.Where(p => p.ReplyEndMs <= asOfMs);

        public static ReplicaSnapshot Load(OmniscienceStores stores, string versionId)
        {
            using var intel = stores.Intel.OpenRead();
            var v = intel.Query(null, """
                SELECT person_id, number, source_seq, data_to_ms, adult_from_ms, encoder_model, config_json, degraded_json FROM replica_versions WHERE version_id=$v
                """, r => (Person: r.GetString(0), Number: r.GetInt32(1), Seq: r.GetInt64(2), To: r.GetInt64OrNull(3), Adult: r.GetInt64OrNull(4),
                    Encoder: r.GetStringOrNull(5), Config: r.GetStringOrNull(6), Degraded: r.GetStringOrNull(7)), ("$v", versionId)).FirstOrDefault();
            if (v.Person == null) throw new KeyNotFoundException($"Replica version {versionId} does not exist.");
            var pairs = intel.Query(null, $"SELECT {PairStore.Columns} FROM replica_pairs WHERE version_id=$v ORDER BY reply_at_ms, pair_id", PairStore.Map, ("$v", versionId));
            var eras = intel.Query(null, "SELECT era_index, from_ms, to_ms, pairs, summary_json FROM replica_eras WHERE version_id=$v ORDER BY era_index",
                r => new Era(r.GetInt32(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3), RJson.Read<Dictionary<string, double>>(r.GetString(4)) ?? new Dictionary<string, double>()), ("$v", versionId));
            var persona = intel.Query(null, """
                SELECT item_id, slice_index, kind, subject, text, strength, first_ms, last_ms, visible_from_ms, evidence_json, sensitive
                FROM replica_persona_items WHERE version_id=$v ORDER BY visible_from_ms, item_id
                """, r => new PersonaItem(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetStringOrNull(3), r.GetString(4), r.GetStringOrNull(5),
                    r.GetInt64OrNull(6), r.GetInt64OrNull(7), r.GetInt64(8), RJson.Ids(r.GetString(9)), r.GetInt32(10) == 1), ("$v", versionId));

            PersonScope person;
            using (var corpus = stores.Corpus.OpenRead())
                person = People.Resolve(corpus, v.Person) ?? new PersonScope(v.Person, new[] { v.Person }, Array.Empty<string>(), v.Person, false);

            var (stim, reply) = v.Encoder == null ? (new Dictionary<string, float[]>(), new Dictionary<string, float[]>()) : LoadVectors(stores, v.Person, v.Encoder, pairs);
            var visible = pairs.Where(p => PairSplits.IsVisible(p.Split)).ToList();
            var docs = visible.Where(p => !p.IsInitiation).ToList();
            var background = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in visible)
                foreach (var t in TextFeatures.Tokens(p.StimulusPlain))
                    if (!t.StartsWith("<link:", StringComparison.Ordinal)) background[t] = background.GetValueOrDefault(t) + 1;

            return new ReplicaSnapshot
            {
                VersionId = versionId,
                PersonId = v.Person,
                Number = v.Number,
                Person = person,
                Pairs = pairs,
                Visible = visible,
                Eras = eras,
                Persona = persona,
                Config = RJson.Read<GenerationConfig>(v.Config) ?? GenerationConfig.Default,
                StimulusVectors = stim,
                ReplyVectors = reply,
                EncoderModel = v.Encoder,
                DataToMs = v.To ?? (pairs.Count == 0 ? 0 : pairs.Max(p => p.ReplyEndMs)),
                AdultFromMs = v.Adult,
                SourceSeq = v.Seq,
                Degraded = RJson.Read<List<string>>(v.Degraded) ?? new List<string>(),
                StimulusIndex = new Bm25Index(docs.Select(p => (IReadOnlyList<string>)TextFeatures.Tokens(p.StimulusPlain)).ToList()),
                StimulusDocs = docs,
                ById = pairs.ToDictionary(p => p.PairId, StringComparer.Ordinal),
                BackgroundTokens = background,
            };
        }

        private static (Dictionary<string, float[]> Stimulus, Dictionary<string, float[]> Reply) LoadVectors(OmniscienceStores stores, string personId, string model, IReadOnlyList<ReplicaPair> pairs)
        {
            var byItem = new Dictionary<string, (string PairId, bool Stimulus)>(StringComparer.Ordinal);
            foreach (var p in pairs)
            {
                byItem[VectorIds.Stimulus(p)] = (p.PairId, true);
                byItem[VectorIds.Reply(p)] = (p.PairId, false);
            }
            var stim = new Dictionary<string, float[]>(StringComparer.Ordinal);
            var reply = new Dictionary<string, float[]>(StringComparer.Ordinal);
            using var c = stores.Vectors.OpenRead();
            foreach (var space in new[] { VectorIds.StimulusSpace, VectorIds.ReplySpace })
            {
                using var cmd = c.Cmd(null, "SELECT item_id, quant, scale, vec FROM vectors WHERE space=$s AND model=$m AND partition_key=$p",
                    ("$s", space), ("$m", model), ("$p", personId));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!byItem.TryGetValue(r.GetString(0), out var target)) continue; // stale: the pair's text changed since
                    var bytes = (byte[])r.GetValue(3);
                    var vec = r.GetString(1) == "i8" ? VecMath.DequantizeI8(bytes, (float)r.GetDouble(2)) : VecMath.FromBytes(bytes);
                    (target.Stimulus ? stim : reply)[target.PairId] = vec;
                }
            }
            return (stim, reply);
        }

        /// <summary>
        /// Hybrid retrieval of the person's real exchanges for the current moment (plan "Composition"): BM25 over what
        /// others said and, when vectors exist, the current message and the recent history embedded separately and fused
        /// (F294), combined by reciprocal-rank fusion (no fixed cosine floors: F296), then weighted by same partner, same
        /// context and nearness to the as-of time, and diversified by MMR on the replies. Only visible pairs finished by
        /// the as-of instant are candidates.
        /// </summary>
        public IReadOnlyList<(ReplicaPair Pair, double Score)> Similar(string current, string? history, float[]? currentVec, float[]? historyVec, long asOfMs,
            string? partner, string contextKind, int k, IReadOnlySet<string>? exclude = null)
        {
            if (k <= 0 || StimulusDocs.Count == 0) return Array.Empty<(ReplicaPair, double)>();
            bool Eligible(int i) => StimulusDocs[i].ReplyEndMs <= asOfMs && (exclude == null || !exclude.Contains(StimulusDocs[i].PairId));
            var rankings = new List<IReadOnlyList<int>>();
            var lexScores = StimulusIndex.Score(TextFeatures.Tokens(current + "\n" + (history ?? "")));
            rankings.Add(RankFusion.TopIndices(lexScores, 400, Eligible));
            if (currentVec != null && HasVectors)
            {
                var vecScores = new double[StimulusDocs.Count];
                for (int i = 0; i < StimulusDocs.Count; i++)
                {
                    if (!StimulusVectors.TryGetValue(StimulusDocs[i].PairId, out var sv)) continue;
                    double s = VecMath.Dot(currentVec, sv);
                    if (historyVec != null) s = 0.7 * s + 0.3 * VecMath.Dot(historyVec, sv);
                    vecScores[i] = s + 1; // shift so every candidate is positive; ranks are what matter
                }
                rankings.Add(RankFusion.TopIndices(vecScores, 400, Eligible));
            }
            var fused = RankFusion.Rrf(rankings);
            if (fused.Count == 0) return Array.Empty<(ReplicaPair, double)>();
            var scored = fused.Select(kv =>
            {
                var p = StimulusDocs[kv.Key];
                double w = kv.Value;
                if (partner != null && p.PartnerPersonId == partner) w *= 1.3;
                if (p.ContextKind == contextKind) w *= 1.15;
                double ageDays = Math.Max(0, (asOfMs - p.ReplyAtMs) / 86_400_000.0);
                w *= 0.4 + 0.6 * Math.Pow(0.5, ageDays / 730);
                return (Pair: p, Score: w);
            }).OrderByDescending(x => x.Score).ThenBy(x => x.Pair.PairId, StringComparer.Ordinal).Take(Math.Max(k * 6, 60)).ToList();
            return Mmr(scored, k, 0.75);
        }

        /// <summary>Maximal marginal relevance over reply similarity (vectors when present, otherwise token Jaccard).</summary>
        private List<(ReplicaPair Pair, double Score)> Mmr(List<(ReplicaPair Pair, double Score)> candidates, int k, double lambda)
        {
            var chosen = new List<(ReplicaPair Pair, double Score)>();
            if (candidates.Count == 0) return chosen;
            double max = candidates.Max(c => c.Score);
            var tokens = HasVectors ? null : candidates.ToDictionary(c => c.Pair.PairId, c => TextFeatures.Tokens(c.Pair.ReplyText).ToHashSet(StringComparer.Ordinal));
            var pool = candidates.ToList();
            while (chosen.Count < k && pool.Count > 0)
            {
                int best = 0;
                double bestValue = double.NegativeInfinity;
                for (int i = 0; i < pool.Count; i++)
                {
                    double redundancy = 0;
                    foreach (var c in chosen) redundancy = Math.Max(redundancy, ReplySimilarity(pool[i].Pair, c.Pair, tokens));
                    double value = lambda * (pool[i].Score / max) - (1 - lambda) * redundancy;
                    if (value > bestValue) { bestValue = value; best = i; }
                }
                chosen.Add(pool[best]);
                pool.RemoveAt(best);
            }
            return chosen;
        }

        private double ReplySimilarity(ReplicaPair a, ReplicaPair b, Dictionary<string, HashSet<string>>? tokens)
        {
            if (tokens == null)
                return ReplyVectors.TryGetValue(a.PairId, out var va) && ReplyVectors.TryGetValue(b.PairId, out var vb) ? Math.Max(0, VecMath.Dot(va, vb)) : 0;
            var ta = tokens[a.PairId];
            var tb = tokens[b.PairId];
            if (ta.Count == 0 || tb.Count == 0) return 0;
            int inter = ta.Count(tb.Contains);
            return (double)inter / (ta.Count + tb.Count - inter);
        }
    }

    /// <summary>Vector ids in vectors.db: per pair and text version, partitioned by person (F279/F280: one shared encoder,
    /// stimuli embedded too; a pair whose text changes gets a new id so a stale vector is never used).</summary>
    public static class VectorIds
    {
        public const string StimulusSpace = "stimulus";
        public const string ReplySpace = "replica.reply";

        public static string Stimulus(ReplicaPair p) => p.PairId + "#s" + p.TextHash[..12];
        public static string Reply(ReplicaPair p) => p.PairId + "#r" + p.TextHash[..12];
    }

    /// <summary>Row mapping for <c>replica_pairs</c>.</summary>
    internal static class PairStore
    {
        public const string Columns =
            "pair_id, person_id, conversation_id, guild_id, platform, context_kind, partner_person_id, partner_is_self, addressee_source, is_initiation, explicit_reply, " +
            "reply_at_ms, reply_end_ms, stimulus_at_ms, latency_ms, reply_ids, stimulus_ids, context_ids, reply_text, stimulus_plain, rendered, text_hash, stimulus_hash, " +
            "time_quality, flags, split, era_index, slice_index, reflex_class, source_seq, reply_count, reply_chars";

        public static ReplicaPair Map(SqliteDataReader r) => new()
        {
            PairId = r.GetString(0),
            PersonId = r.GetString(1),
            ConversationId = r.GetString(2),
            GuildId = r.GetStringOrNull(3),
            Platform = r.GetString(4),
            ContextKind = r.GetString(5),
            PartnerPersonId = r.GetStringOrNull(6),
            PartnerIsSelf = r.GetInt32(7) == 1,
            AddresseeSource = r.GetString(8),
            IsInitiation = r.GetInt32(9) == 1,
            ExplicitReply = r.GetInt32(10) == 1,
            ReplyAtMs = r.GetInt64(11),
            ReplyEndMs = r.GetInt64(12),
            StimulusAtMs = r.GetInt64OrNull(13),
            LatencyMs = r.GetInt64OrNull(14),
            ReplyIds = RJson.Ids(r.GetString(15)),
            StimulusIds = RJson.Ids(r.GetString(16)),
            ContextIds = RJson.Ids(r.GetString(17)),
            ReplyText = r.GetString(18),
            StimulusPlain = r.GetString(19),
            Rendered = r.GetString(20),
            TextHash = r.GetString(21),
            StimulusHash = r.GetString(22),
            TimeQuality = r.GetInt32(23),
            Flags = (PairFlags)r.GetInt32(24),
            Split = r.GetString(25),
            EraIndex = r.GetInt32(26),
            SliceIndex = r.GetInt32(27),
            ReflexClass = r.GetStringOrNull(28),
            SourceSeq = r.GetInt64(29),
        };

        public static void Insert(SqliteConnection c, SqliteTransaction tx, string versionId, IEnumerable<ReplicaPair> pairs)
        {
            using var stmt = new PreparedStatement(c, tx, $"""
                INSERT OR REPLACE INTO replica_pairs(version_id, {Columns})
                VALUES($v,$a0,$a1,$a2,$a3,$a4,$a5,$a6,$a7,$a8,$a9,$a10,$a11,$a12,$a13,$a14,$a15,$a16,$a17,$a18,$a19,$a20,$a21,$a22,$a23,$a24,$a25,$a26,$a27,$a28,$a29,$a30,$a31)
                """, new[] { "$v" }.Concat(Enumerable.Range(0, 32).Select(i => "$a" + i)).ToArray());
            foreach (var p in pairs)
                stmt.Run(versionId, p.PairId, p.PersonId, p.ConversationId, p.GuildId, p.Platform, p.ContextKind, p.PartnerPersonId, p.PartnerIsSelf ? 1 : 0,
                    p.AddresseeSource, p.IsInitiation ? 1 : 0, p.ExplicitReply ? 1 : 0, p.ReplyAtMs, p.ReplyEndMs, p.StimulusAtMs, p.LatencyMs,
                    RJson.Write(p.ReplyIds), RJson.Write(p.StimulusIds), RJson.Write(p.ContextIds), p.ReplyText, p.StimulusPlain, p.Rendered, p.TextHash,
                    p.StimulusHash, p.TimeQuality, (int)p.Flags, p.Split, p.EraIndex, p.SliceIndex, p.ReflexClass, p.SourceSeq, p.ReplyCount, p.ReplyText.Length);
        }
    }

    /// <summary>Loaded snapshots by version (small LRU: a snapshot of a very active person holds tens of MB of vectors).</summary>
    public sealed class SnapshotCache
    {
        private readonly OmniscienceStores stores;
        private readonly int capacity;
        private readonly object sync = new();
        private readonly LinkedList<(string VersionId, ReplicaSnapshot Snapshot)> order = new();
        private readonly Dictionary<string, Lazy<ReplicaSnapshot>> loading = new(StringComparer.Ordinal);

        public SnapshotCache(OmniscienceStores stores, int capacity = 4)
        {
            this.stores = stores;
            this.capacity = Math.Max(1, capacity);
        }

        public ReplicaSnapshot Get(string versionId)
        {
            Lazy<ReplicaSnapshot> lazy;
            lock (sync)
            {
                for (var node = order.First; node != null; node = node.Next)
                    if (node.Value.VersionId == versionId)
                    {
                        order.Remove(node);
                        order.AddFirst(node);
                        return node.Value.Snapshot;
                    }
                if (!loading.TryGetValue(versionId, out lazy!))
                    loading[versionId] = lazy = new Lazy<ReplicaSnapshot>(() => ReplicaSnapshot.Load(stores, versionId), LazyThreadSafetyMode.ExecutionAndPublication);
            }
            try
            {
                var snapshot = lazy.Value;
                lock (sync)
                {
                    loading.Remove(versionId);
                    if (!order.Any(x => x.VersionId == versionId)) order.AddFirst((versionId, snapshot));
                    while (order.Count > capacity) order.RemoveLast();
                }
                return snapshot;
            }
            catch
            {
                lock (sync) loading.Remove(versionId);
                throw;
            }
        }

        public void Evict(string versionId)
        {
            lock (sync)
            {
                for (var node = order.First; node != null; node = node.Next)
                    if (node.Value.VersionId == versionId) { order.Remove(node); break; }
            }
        }

        public void Clear()
        {
            lock (sync) order.Clear();
        }
    }
}
