using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Ml.Classification;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.Classification
{
    public sealed partial class ClassificationModule
    {
        private const int DrainBatch = 512;
        private static readonly TimeSpan DrainBudget = TimeSpan.FromMinutes(20);

        /// <summary>A message as the classifier sees it.</summary>
        internal sealed record MessageRow(
            string MessageId, string ConversationId, string AuthorIdentityId, long SentAtMs, long OrderKey, int Version, string? Content,
            bool IsBot, bool IsSystem, bool IsWebhook, bool IsForwarded, string? ReplyTo, long? DeletedAtMs, long IngestSeq, string? ConversationKind);

        private const string MessageColumns = """
            m.message_id, m.conversation_id, m.author_identity_id, m.sent_at_ms, m.order_key, m.current_version, m.content,
            (m.is_bot = 1 OR COALESCE(i.kind,'') = 'bot'), m.is_system, m.is_webhook, m.forwarded_from_json IS NOT NULL, m.reply_to_message_id,
            m.deleted_at_ms, m.ingest_seq, cv.kind
            """;

        private const string MessageFrom = """
            FROM messages m
            LEFT JOIN identities i ON i.identity_id = m.author_identity_id
            LEFT JOIN conversations cv ON cv.conversation_id = m.conversation_id
            """;

        private static MessageRow MapMessage(SqliteDataReader r) => new(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt32(5), r.GetStringOrNull(6),
            r.GetInt64(7) == 1, r.GetInt64(8) == 1, r.GetInt64(9) == 1, r.GetInt64(10) == 1, r.GetStringOrNull(11), r.GetInt64OrNull(12), r.GetInt64(13),
            r.GetStringOrNull(14));

        internal List<MessageRow> LoadMessages(SqliteConnection corpus, IReadOnlyCollection<string> ids)
        {
            var result = new List<MessageRow>(ids.Count);
            foreach (var chunk in ids.Distinct(StringComparer.Ordinal).Chunk(400))
            {
                var names = chunk.Select((_, i) => "$k" + i).ToArray();
                var args = chunk.Select((id, i) => (names[i], (object?)id)).ToArray();
                result.AddRange(corpus.Query(null, $"SELECT {MessageColumns} {MessageFrom} WHERE m.message_id IN ({string.Join(",", names)})", MapMessage, args));
            }
            return result;
        }

        // ── self set and conversation facts ──────────────────────────────────────

        /// <summary>Every identity of Klives (plan R5 "self" set): persons marked is_self and everyone merged into them.</summary>
        internal static HashSet<string> SelfIdentities(SqliteConnection corpus) =>
            corpus.Query(null, """
                SELECT i.identity_id FROM identities i
                WHERE i.person_id IN (SELECT person_id FROM persons WHERE is_self = 1)
                   OR i.person_id IN (SELECT pc.person_id FROM person_canonical pc WHERE pc.canonical_id IN (
                        SELECT COALESCE(pc2.canonical_id, p.person_id) FROM persons p LEFT JOIN person_canonical pc2 ON pc2.person_id = p.person_id WHERE p.is_self = 1))
                """, r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);

        /// <summary>Per-run caches for the context flags.</summary>
        internal sealed class DrainContext
        {
            public required HashSet<string> Self { get; init; }
            public Dictionary<string, bool> DirectWithKlives { get; } = new(StringComparer.Ordinal);
            public required Dictionary<(string Message, string Task), string> Gold { get; init; }
            public required long GapMs { get; init; }
        }

        internal DrainContext NewDrainContext(SqliteConnection corpus) => new()
        {
            Self = SelfIdentities(corpus),
            Gold = ClassificationReader.LoadGold(Context.Stores),
            GapMs = Setting(ContextGapMinutesSetting, 360) * 60_000L,
        };

        private static bool IsDirect(string? kind) => kind is "dm";

        private bool DirectWithKlives(SqliteConnection corpus, DrainContext dc, MessageRow m)
        {
            if (!IsDirect(m.ConversationKind) || dc.Self.Count == 0) return false;
            if (dc.DirectWithKlives.TryGetValue(m.ConversationId, out bool known)) return known;
            var self = dc.Self.ToList();
            bool member = corpus.QueryInWith("SELECT 1 FROM conversation_members WHERE conversation_id=$c AND identity_id IN ({keys}) LIMIT 1", self,
                              r => 1, ("$c", m.ConversationId)).Count > 0
                          || corpus.QueryInWith("SELECT 1 FROM messages WHERE conversation_id=$c AND author_identity_id IN ({keys}) LIMIT 1", self,
                              r => 1, ("$c", m.ConversationId)).Count > 0;
            return dc.DirectWithKlives[m.ConversationId] = member;
        }

        // ── the drain job ────────────────────────────────────────────────────────

        private async Task<string?> DrainAsync(JobContext job)
        {
            var ct = job.Cancellation;
            var sw = Stopwatch.StartNew();
            long cursor = Cursor("drain");
            long maxSeq;
            using (var cr = Context.Stores.Corpus.OpenRead()) maxSeq = ChangeLog.MaxSeq(cr);
            long backlog = maxSeq - cursor;
            int processed = 0, embedded = 0, labelled = 0;

            if (backlog > Setting(PriorityBacklogSetting, 20_000) && State<string>("priority.done") != Encoder.ModelVersion)
            {
                var (pe, pl) = await PriorityPassAsync(job).ConfigureAwait(false);
                embedded += pe;
                labelled += pl;
                await SetStateAsync("priority.done", Encoder.ModelVersion, ct).ConfigureAwait(false);
            }

            while (cursor < maxSeq && sw.Elapsed < DrainBudget)
            {
                ct.ThrowIfCancellationRequested();
                List<(long Seq, string Kind, string Entity)> changes;
                using (var cr = Context.Stores.Corpus.OpenRead())
                {
                    changes = cr.Query(null, """
                        SELECT seq, kind, entity_id FROM change_log
                        WHERE seq > $s AND seq <= $max AND kind IN ('message.insert','message.version','message.delete')
                        ORDER BY seq LIMIT $l
                        """, r => (r.GetInt64(0), r.GetString(1), r.GetString(2)), ("$s", cursor), ("$max", maxSeq), ("$l", DrainBatch));
                }
                long upTo = changes.Count < DrainBatch ? maxSeq : changes[^1].Seq;
                var ids = changes.Where(c => c.Kind != ChangeKinds.MessageDelete).Select(c => c.Entity).Distinct(StringComparer.Ordinal).ToList();
                if (ids.Count > 0)
                {
                    var (e, l) = await ProcessAsync(ids, includeNext: true, ct).ConfigureAwait(false);
                    embedded += e;
                    labelled += l;
                }
                processed += changes.Count;
                await SetCursorAsync("drain", upTo, ct).ConfigureAwait(false);
                cursor = upTo;
                await job.ReportProgressAsync(new { cursor, maxSeq, processed, embedded, labelled }).ConfigureAwait(false);
            }

            await MaybeScheduleIvfAsync(ct).ConfigureAwait(false);
            await RefreshHealthAsync(ct).ConfigureAwait(false);
            return System.Text.Json.JsonSerializer.Serialize(new { cursor, maxSeq, processed, embedded, labelled, caughtUp = cursor >= maxSeq });
        }

        /// <summary>
        /// Plan R2 priority order for a large backlog: tracked people's messages, then Klives's DMs, then the watch tier.
        /// Everything else follows in change-log order in the normal drain (which skips what is already embedded).
        /// </summary>
        private async Task<(int Embedded, int Labelled)> PriorityPassAsync(JobContext job)
        {
            var ct = job.Cancellation;
            List<string> tracked, watch;
            using (var op = Context.Stores.Operator.OpenRead())
            {
                tracked = op.Query(null, "SELECT person_id FROM person_settings WHERE tier='tracked' OR pinned=1", r => r.GetString(0));
                watch = op.Query(null, "SELECT person_id FROM person_settings WHERE tier='watch'", r => r.GetString(0));
            }
            int embedded = 0, labelled = 0;
            using var corpus = Context.Stores.Corpus.OpenRead();
            var self = SelfIdentities(corpus).ToList();
            IEnumerable<string> IdentitiesOf(IEnumerable<string> persons)
            {
                var canon = persons.Select(Context.Policy.Canonical).Distinct(StringComparer.Ordinal).ToList();
                return corpus.QueryIn(null, "SELECT i.identity_id FROM identities i JOIN person_canonical pc ON pc.person_id=i.person_id WHERE pc.canonical_id IN ({keys})",
                    canon, r => r.GetString(0));
            }
            var klivesDms = self.Count == 0 ? new List<string>() : corpus.QueryIn(null, """
                SELECT DISTINCT cm.conversation_id FROM conversation_members cm JOIN conversations c ON c.conversation_id = cm.conversation_id
                WHERE c.kind = 'dm' AND cm.identity_id IN ({keys})
                """, self, r => r.GetString(0));
            var passes = new List<(string Name, string Sql, IReadOnlyList<string> Keys)>
            {
                ("tracked", "SELECT message_id FROM messages WHERE author_identity_id=$k AND message_id > $after ORDER BY message_id LIMIT 2000", IdentitiesOf(tracked).ToList()),
                ("klives-dms", "SELECT message_id FROM messages WHERE conversation_id=$k AND message_id > $after ORDER BY message_id LIMIT 2000", klivesDms),
                ("watch", "SELECT message_id FROM messages WHERE author_identity_id=$k AND message_id > $after ORDER BY message_id LIMIT 2000", IdentitiesOf(watch).ToList()),
            };
            foreach (var (name, sql, keys) in passes)
            {
                foreach (var key in keys)
                {
                    string after = "";
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var ids = corpus.Query(null, sql, r => r.GetString(0), ("$k", key), ("$after", after));
                        if (ids.Count == 0) break;
                        after = ids[^1];
                        var (e, l) = await ProcessAsync(ids, includeNext: false, ct).ConfigureAwait(false);
                        embedded += e;
                        labelled += l;
                        await job.ReportProgressAsync(new { priorityPass = name, embedded, labelled }).ConfigureAwait(false);
                    }
                }
            }
            return (embedded, labelled);
        }

        /// <summary>
        /// Embeds what is missing or stale (by message version) and labels under every live label set. With
        /// <paramref name="includeNext"/>, each message's successor in its conversation is relabelled too: a message that
        /// arrives late (backward backfill) becomes the context of the one after it (plan T5).
        /// </summary>
        internal async Task<(int Embedded, int Labelled)> ProcessAsync(IReadOnlyList<string> ids, bool includeNext, CancellationToken ct)
        {
            var encoder = Encoder;
            List<MessageRow> rows;
            DrainContext dc;
            var nextIds = new List<string>();
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                rows = LoadMessages(corpus, ids);
                dc = NewDrainContext(corpus);
                if (includeNext)
                {
                    foreach (var m in rows)
                    {
                        string? next = corpus.Scalar<string>(null, """
                            SELECT message_id FROM messages WHERE conversation_id=$c AND (sent_at_ms, order_key) > ($t, $o)
                            ORDER BY sent_at_ms, order_key LIMIT 1
                            """, ("$c", m.ConversationId), ("$t", m.SentAtMs), ("$o", m.OrderKey));
                        if (next != null) nextIds.Add(next);
                    }
                }
            }

            // 1. Embed: everything with text whose stored vector is missing or older than the current version.
            var gated = rows.Select(m => (Row: m, Gate: ContentGate.Decide(new GateInput(m.Content, m.IsBot, m.IsSystem, m.IsWebhook, m.IsForwarded)))).ToList();
            var embeddable = gated.Where(g => !g.Gate.Skip).Select(g => g.Row).ToList();
            var existing = Vectors.Get(VectorSpaces.Message, encoder.ModelVersion, embeddable.Select(m => m.MessageId).ToList());
            var stale = embeddable.Where(m => !existing.TryGetValue(m.MessageId, out var v) || v.SourceVersion != m.Version).ToList();
            if (stale.Count > 0)
            {
                var vectors = await encoder.EmbedAsync(stale.Select(m => m.Content ?? "").ToList(), ct).ConfigureAwait(false);
                await Vectors.UpsertAsync(VectorSpaces.Message, encoder.ModelVersion,
                    stale.Select((m, i) => new VectorItem(m.MessageId, vectors[i], m.Version)).ToList(), ct).ConfigureAwait(false);
                // Taught concepts (X19) apply to new and edited messages as they arrive.
                await WriteConceptHitsAsync(stale.Select((m, i) => (m.MessageId, vectors[i], m.Version, m.IngestSeq)).ToList(), ct).ConfigureAwait(false);
            }
            // Copypasta detection is a count of distinct authors per normalised text (exact, structural).
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                using var put = new PreparedStatement(c, tx, "INSERT OR IGNORE INTO classify_text_authors(text_hash, author_identity_id) VALUES($h,$a)", "$h", "$a");
                foreach (var m in embeddable) put.Run(ContentGate.NormalizedHash(m.Content), m.AuthorIdentityId);
            }, ct).ConfigureAwait(false);

            // 2. Label under every live label set (the active one and one being built).
            var labelsets = LiveLabelsets();
            if (labelsets.Count == 0) return (stale.Count, 0);
            var toLabel = embeddable.Select(m => m.MessageId).Concat(nextIds).Distinct(StringComparer.Ordinal).ToList();
            int labelled = await LabelAsync(toLabel, labelsets, dc, ct).ConfigureAwait(false);
            return (stale.Count, labelled);
        }

        /// <summary>Computes and writes the packed labels of <paramref name="ids"/> under each label set. Returns rows written.</summary>
        internal async Task<int> LabelAsync(IReadOnlyList<string> ids, IReadOnlyList<Labelset> labelsets, DrainContext dc, CancellationToken ct)
        {
            if (ids.Count == 0 || labelsets.Count == 0) return 0;
            var encoder = Encoder;
            List<MessageRow> rows;
            var previous = new Dictionary<string, (string Id, string Author, long SentAt)>(StringComparer.Ordinal);
            var repliesToKlives = new HashSet<string>(StringComparer.Ordinal);
            var mentionsKlives = new HashSet<string>(StringComparer.Ordinal);
            var direct = new Dictionary<string, bool>(StringComparer.Ordinal);
            var copyAuthors = new Dictionary<string, int>(StringComparer.Ordinal);
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                rows = LoadMessages(corpus, ids);
                foreach (var m in rows)
                {
                    var prev = corpus.Query(null, """
                        SELECT message_id, author_identity_id, sent_at_ms FROM messages
                        WHERE conversation_id=$c AND (sent_at_ms, order_key) < ($t, $o) ORDER BY sent_at_ms DESC, order_key DESC LIMIT 1
                        """, r => (r.GetString(0), r.GetString(1), r.GetInt64(2)), ("$c", m.ConversationId), ("$t", m.SentAtMs), ("$o", m.OrderKey)).FirstOrDefault();
                    if (prev != default && m.SentAtMs - prev.Item3 <= dc.GapMs) previous[m.MessageId] = prev;
                    direct[m.MessageId] = DirectWithKlives(corpus, dc, m);
                }
                if (dc.Self.Count > 0)
                {
                    var replyTargets = rows.Where(m => m.ReplyTo != null).Select(m => m.ReplyTo!).Distinct().ToList();
                    var klivesTargets = corpus.QueryIn(null, "SELECT message_id, author_identity_id FROM messages WHERE message_id IN ({keys})", replyTargets,
                        r => (r.GetString(0), r.GetString(1))).Where(x => dc.Self.Contains(x.Item2)).Select(x => x.Item1).ToHashSet(StringComparer.Ordinal);
                    foreach (var m in rows) if (m.ReplyTo != null && klivesTargets.Contains(m.ReplyTo)) repliesToKlives.Add(m.MessageId);
                    foreach (var (msg, target) in corpus.QueryIn(null, "SELECT message_id, target_id FROM message_mentions WHERE target_kind='user' AND message_id IN ({keys})",
                                 rows.Select(m => m.MessageId).ToList(), r => (r.GetString(0), r.GetString(1))))
                        if (dc.Self.Contains(target)) mentionsKlives.Add(msg);
                }
            }
            var hashes = rows.ToDictionary(m => m.MessageId, m => ContentGate.NormalizedHash(m.Content), StringComparer.Ordinal);
            var llm = new Dictionary<(string, string), (string Label, string Version)>();
            Dictionary<string, Dictionary<string, double>> langPrior;
            using (var intel = Context.Stores.Intel.OpenRead())
            {
                foreach (var (hash, n) in intel.QueryIn(null, "SELECT text_hash, COUNT(*) FROM classify_text_authors WHERE text_hash IN ({keys}) GROUP BY text_hash",
                             hashes.Values.Distinct().ToList(), r => (r.GetString(0), (int)r.GetInt64(1))))
                    copyAuthors[hash] = n;
                var versions = rows.ToDictionary(m => m.MessageId, m => m.Version, StringComparer.Ordinal);
                foreach (var (item, task, label, mv, iv) in intel.QueryIn(null,
                             $"SELECT item_id, task, label, model_version, item_version FROM labels WHERE item_kind='message' AND item_id IN ({{keys}}) AND model_version IN ('{TeacherVersion}','{AdjudicationVersion}')",
                             rows.Select(m => m.MessageId).ToList(), r => (r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetString(3), r.GetInt32(4))))
                {
                    if (label == null || !versions.TryGetValue(item, out int current) || iv != current) continue; // a label of an older version is stale
                    if (llm.TryGetValue((item, task), out var have) && have.Version == AdjudicationVersion) continue; // adjudication outranks the teacher
                    llm[(item, task)] = (label, mv);
                }
                langPrior = intel.QueryIn(null, "SELECT author_identity_id, lang, n FROM classify_author_lang WHERE author_identity_id IN ({keys})",
                        rows.Select(m => m.AuthorIdentityId).Distinct().ToList(), r => (r.GetString(0), r.GetString(1), (double)r.GetInt64(2)))
                    .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Item2, x => x.Item3));
            }
            var needed = rows.Select(m => m.MessageId).Concat(previous.Values.Select(p => p.Id)).Distinct(StringComparer.Ordinal).ToList();
            var vectors = Vectors.Get(VectorSpaces.Message, encoder.ModelVersion, needed);

            long now = Context.Clock.NowMs;
            var adjudicate = new HashSet<string>(StringComparer.Ordinal);
            int adjudicationSet = labelsets.FirstOrDefault(l => l.State == "active")?.Id ?? labelsets[0].Id;
            var writes = new List<(string Id, int Labelset, int Version, string Hash, string Codes, string? Gate, long Seq, IReadOnlyList<FusedLabel> Details, string? Lang)>();
            foreach (var m in rows)
            {
                var gate = ContentGate.Decide(new GateInput(m.Content, m.IsBot, m.IsSystem, m.IsWebhook, m.IsForwarded, copyAuthors.GetValueOrDefault(hashes[m.MessageId], 1)));
                if (gate.Skip || !vectors.TryGetValue(m.MessageId, out var emb) || emb.SourceVersion != m.Version) continue;
                float[]? prevEmb = previous.TryGetValue(m.MessageId, out var p) && vectors.TryGetValue(p.Id, out var pv) ? pv.Vector : null;
                var flags = new ContextFlags(
                    PreviousByKlives: p != default && dc.Self.Contains(p.Author),
                    DirectWithKlives: direct.GetValueOrDefault(m.MessageId),
                    RepliesToKlives: repliesToKlives.Contains(m.MessageId),
                    MentionsKlives: mentionsKlives.Contains(m.MessageId),
                    SameAuthorAsPrevious: p != default && p.Author == m.AuthorIdentityId);
                float[]? contextFeatures = prevEmb == null ? null : ContextFeatures.Build(emb.Vector, prevEmb, flags);
                foreach (var set in labelsets)
                {
                    var fused = new List<FusedLabel>();
                    foreach (var task in ClassificationTasks.All)
                    {
                        var signals = new List<SignalOpinion>();
                        if (set.Single.TryGetValue(task.Id, out var head)) signals.Add(new SignalOpinion(Signals.Head, head.Predict(emb.Vector), head.ModelVersion));
                        if (set.ContextHeads.TryGetValue(task.Id, out var ch))
                        {
                            // Without a previous message the context head still sees the exchange flags (a DM with Klives, a reply to him).
                            var x = contextFeatures ?? ContextFeatures.Build(emb.Vector, ReadOnlySpan<float>.Empty, flags);
                            signals.Add(new SignalOpinion(Signals.Context, ch.Predict(x), ch.ModelVersion));
                        }
                        if (llm.TryGetValue((m.MessageId, task.Id), out var l))
                            signals.Add(new SignalOpinion(Signals.Llm, Ensemble.LlmDistribution(task, l.Label, set.LlmReliability.GetValueOrDefault(task.Id, 0.85)), l.Version));
                        if (task.Id == ClassificationTasks.Language.Id && langPrior.TryGetValue(m.AuthorIdentityId, out var prior) && gate.Shape.LetterGraphemes <= 20)
                            signals.Add(new SignalOpinion(Signals.Prior, PriorDistribution(task, prior), "author-language-prior"));
                        dc.Gold.TryGetValue((m.MessageId, task.Id), out var gold);
                        if (signals.Count == 0 && gold == null) continue;
                        var config = set.Fusion.GetValueOrDefault(task.Id) ?? FusionConfig.EqualWeights();
                        if (task.Id == ClassificationTasks.Language.Id && gate.Shape.LetterGraphemes <= 3)
                            config = config with { Weights = new Dictionary<string, double>(config.Weights) { [Signals.Prior] = Math.Max(1.0, config.Weights.GetValueOrDefault(Signals.Prior)) } };
                        fused.Add(Ensemble.Fuse(task, signals, config, gate.AbstainBoost, gold));
                    }
                    if (fused.Count == 0) continue;
                    var details = fused.Where(f => f.Votes.Any(v => v.Signal is Signals.Llm or Signals.Gold) || f.NeedsAdjudication).ToList();
                    // Active learning (plan A2/R1): signals that disagree on a high-impact task, with no LLM opinion yet, go to adjudication.
                    if (set.Id == adjudicationSet && !gate.NonAuthored
                        && fused.Any(f => HighImpactTasks.Contains(f.Task) && f.NeedsAdjudication && f.Votes.All(v => v.Signal is not (Signals.Llm or Signals.Gold))))
                        adjudicate.Add(m.MessageId);
                    string? lang = fused.FirstOrDefault(f => f.Task == ClassificationTasks.Language.Id) is { Abstained: false } lf ? lf.Label : null;
                    writes.Add((m.MessageId, set.Id, m.Version, hashes[m.MessageId], LabelCodec.Encode(fused), gate.Reason, m.IngestSeq, details, lang));
                }
            }
            if (writes.Count == 0) return 0;
            int priorSet = labelsets.FirstOrDefault(l => l.State == "active")?.Id ?? labelsets[0].Id;
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                using var read = new PreparedStatement(c, tx, "SELECT codes FROM classify_message WHERE message_id=$m AND labelset=$l", "$m", "$l");
                using var put = new PreparedStatement(c, tx, """
                    INSERT INTO classify_message(message_id, labelset, item_version, text_hash, codes, gate, as_of_ms, source_seq) VALUES($m,$l,$v,$h,$c,$g,$a,$s)
                    ON CONFLICT(message_id, labelset) DO UPDATE SET item_version=excluded.item_version, text_hash=excluded.text_hash, codes=excluded.codes,
                        gate=excluded.gate, as_of_ms=excluded.as_of_ms, source_seq=excluded.source_seq
                    """, "$m", "$l", "$v", "$h", "$c", "$g", "$a", "$s");
                using var detail = new PreparedStatement(c, tx, """
                    INSERT INTO labels(item_kind, item_id, item_version, task, label, probability, abstained, signals_json, model, model_version, prompt_version, as_of_ms, source_seq)
                    VALUES('message',$i,$v,$t,$l,$p,$ab,$sj,'a2',$mv,NULL,$a,$s)
                    ON CONFLICT(item_kind, item_id, task, model_version) DO UPDATE SET item_version=excluded.item_version, label=excluded.label, probability=excluded.probability,
                        abstained=excluded.abstained, signals_json=excluded.signals_json, as_of_ms=excluded.as_of_ms, source_seq=excluded.source_seq
                    """, "$i", "$v", "$t", "$l", "$p", "$ab", "$sj", "$mv", "$a", "$s");
                using var lang = new PreparedStatement(c, tx, """
                    INSERT INTO classify_author_lang(author_identity_id, lang, n) VALUES($a,$g,$d)
                    ON CONFLICT(author_identity_id, lang) DO UPDATE SET n=MAX(0, n + excluded.n)
                    """, "$a", "$g", "$d");
                var authors = rows.ToDictionary(r => r.MessageId, r => r.AuthorIdentityId, StringComparer.Ordinal);
                foreach (var w in writes)
                {
                    if (w.Labelset == priorSet)
                    {
                        // Keep the author's language counts exact under re-labelling: undo the old answer, count the new one.
                        string? old = read.ScalarRaw(w.Id, w.Labelset) as string;
                        string? oldLang = LabelCodec.Answer(old, ClassificationTasks.Language.Id);
                        if (oldLang != w.Lang)
                        {
                            if (oldLang != null) lang.Run(authors[w.Id], oldLang, -1);
                            if (w.Lang != null) lang.Run(authors[w.Id], w.Lang, 1);
                        }
                    }
                    put.Run(w.Id, w.Labelset, w.Version, w.Hash, w.Codes, w.Gate, now, w.Seq);
                    foreach (var f in w.Details)
                        detail.Run(w.Id, w.Version, f.Task, f.Label ?? f.TopLabel, f.Probability, f.Abstained ? 1 : 0, LabelCodec.SignalsJson(f.Votes), $"a2.ls{w.Labelset}", now, w.Seq);
                }
            }, ct).ConfigureAwait(false);
            if (adjudicate.Count > 0) await EnqueueAdjudicationAsync(adjudicate, ct).ConfigureAwait(false);
            return writes.Count;
        }

        /// <summary>The author's language history as a distribution (Laplace-smoothed), the N19 prior for short messages.</summary>
        private static float[] PriorDistribution(TaskDefinition task, IReadOnlyDictionary<string, double> counts)
        {
            var d = new float[task.Labels.Count];
            double total = counts.Values.Sum() + task.Labels.Count * 0.5;
            for (int i = 0; i < d.Length; i++) d[i] = (float)((counts.GetValueOrDefault(task.Labels[i].Id) + 0.5) / total);
            return d;
        }

        /// <summary>Builds or rebuilds the message IVF index when the space has grown past the threshold.</summary>
        private async Task MaybeScheduleIvfAsync(CancellationToken ct)
        {
            long count = Vectors.Count(VectorSpaces.Message, Encoder.ModelVersion);
            long threshold = Setting(IvfThresholdSetting, 50_000);
            var status = Vectors.Status(VectorSpaces.Message, Encoder.ModelVersion);
            bool wanted = (status == null && count >= threshold) || (status != null && (count >= status.TrainedOn * 4 || status.PendingVersion != null || status.UnassignedOrStale > count / 20));
            if (!wanted) return;
            await Context.Jobs.EnqueueAsync(Job("ivf", "space", VectorSpaces.Message, priority: 70), ct).ConfigureAwait(false);
        }

        private async Task<string?> IvfAsync(JobContext job)
        {
            var ct = job.Cancellation;
            var model = Encoder.ModelVersion;
            var status = Vectors.Status(VectorSpaces.Message, model);
            long count = Vectors.Count(VectorSpaces.Message, model);
            if (status == null || (count >= status.TrainedOn * 4 && status.PendingVersion == null))
                await Vectors.TrainAsync(VectorSpaces.Message, model, ct: ct).ConfigureAwait(false);
            int moved = 0, step;
            while ((step = await Vectors.ReassignStepAsync(VectorSpaces.Message, model, 5_000, ct).ConfigureAwait(false)) > 0)
            {
                moved += step;
                await job.ReportProgressAsync(new { moved }).ConfigureAwait(false);
            }
            return System.Text.Json.JsonSerializer.Serialize(new { moved, status = Vectors.Status(VectorSpaces.Message, model) });
        }
    }
}
