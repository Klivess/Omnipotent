using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Ml.Classification;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.Classification
{
    public sealed partial class ClassificationModule
    {
        /// <summary>Why a message is in the teacher queue; adjudication outranks sampling.</summary>
        public static class TeacherReasons
        {
            public const string Sample = "sample";
            public const string Adjudicate = "adjudicate";
            public const string Concept = "concept";
        }

        private const int MaxQueued = 20_000;

        // ── sampling ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Fills the teacher queue towards the target sample (plan R1 "stratified teacher/gold samples, ~5–10K"), in the R2
        /// priority order: tracked people 35%, Klives's DMs 25%, watch tier 15%, everyone else 25% (shares of an empty tier
        /// pass to the next). Candidates are drawn by seeded random probes into the corpus indexes, never by a scan;
        /// they must be embedded (training needs the vector), labelable and authored, and duplicates of a queued text are skipped.
        /// <para>Every batch of probes is queued as soon as it is drawn and reported, and the job ends when its time is
        /// up: the next one, ten minutes later, carries on from what is queued. A probe is a handful of cold reads, and
        /// on the server's drive a thousand picks took 40 to 68 minutes, kept in memory until the end and reported
        /// nowhere. One such run was taken for stuck at two hours and stopped five times over, each time with everything
        /// it had drawn (22 Nov 2026), and while it ran it held one of the two workers that take long work.</para>
        /// </summary>
        private async Task<string?> TeacherSampleAsync(JobContext job)
        {
            var ct = job.Cancellation;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int target = Setting(TeacherTargetSetting, 6000);
            long have;
            HashSet<string> queuedHashes;
            using (var intel = Context.Stores.Intel.OpenRead())
            {
                have = intel.Scalar<long>(null, "SELECT COUNT(*) FROM classify_teacher_queue WHERE reason=$r", ("$r", TeacherReasons.Sample));
                queuedHashes = intel.Query(null, "SELECT text_hash FROM classify_teacher_queue WHERE text_hash IS NOT NULL", r => r.GetString(0))
                    .ToHashSet(StringComparer.Ordinal);
            }
            int need = (int)Math.Min(1_000, target - have);
            if (need <= 0 || Context.Encoder == null || Context.Vectors == null) return JsonSerializer.Serialize(new { have, target, added = 0 });

            // Seeded by the job: seeded by the hour, the several jobs of one hour each began with the probes of the one
            // before, and found what it had already queued.
            var rng = new Random(unchecked((int)(Context.Clock.NowMs / 3_600_000) * 31 + (int)job.Job.JobId * 16777619) ^ 0x5eed);
            List<string> trackedIds, watchIds, klivesDms;
            long maxRowId;
            using (var op = Context.Stores.Operator.OpenRead())
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                var tracked = op.Query(null, "SELECT person_id FROM person_settings WHERE tier='tracked' OR pinned=1", r => r.GetString(0)).Select(Context.Policy.Canonical).Distinct().ToList();
                var watch = op.Query(null, "SELECT person_id FROM person_settings WHERE tier='watch'", r => r.GetString(0)).Select(Context.Policy.Canonical).Distinct().ToList();
                trackedIds = corpus.QueryIn(null, "SELECT i.identity_id FROM identities i JOIN person_canonical pc ON pc.person_id=i.person_id WHERE pc.canonical_id IN ({keys})", tracked, r => r.GetString(0));
                watchIds = corpus.QueryIn(null, "SELECT i.identity_id FROM identities i JOIN person_canonical pc ON pc.person_id=i.person_id WHERE pc.canonical_id IN ({keys})", watch, r => r.GetString(0));
                var self = SelfIdentities(corpus).ToList();
                klivesDms = corpus.QueryIn(null, "SELECT DISTINCT cm.conversation_id FROM conversation_members cm JOIN conversations c ON c.conversation_id=cm.conversation_id WHERE c.kind='dm' AND cm.identity_id IN ({keys})",
                    self, r => r.GetString(0));
                maxRowId = corpus.Scalar<long?>(null, "SELECT MAX(rowid) FROM messages") ?? 0;
            }
            if (maxRowId == 0) return JsonSerializer.Serialize(new { have, target, added = 0 });

            using var probeConn = Context.Stores.Corpus.OpenRead();
            var tiers = new (string Name, double Share, int Priority, Func<string?> Probe)[]
            {
                ("tracked", 0.35, 300, () => ProbeBy(probeConn, "author_identity_id", trackedIds, rng)),
                ("klives_dm", 0.25, 250, () => ProbeBy(probeConn, "conversation_id", klivesDms, rng)),
                ("watch", 0.15, 200, () => ProbeBy(probeConn, "author_identity_id", watchIds, rng)),
                ("other", 0.25, 100, () => ProbeRowId(probeConn, maxRowId, rng)),
            };
            var byTier = new Dictionary<string, int>(StringComparer.Ordinal);
            var pickedIds = new HashSet<string>(StringComparer.Ordinal);
            int carry = 0, added = 0, batches = 0;
            bool outOfTime = false;
            foreach (var (name, share, priority, probe) in tiers)
            {
                int quota = (int)Math.Round(need * share) + carry;
                int got = 0, attempts = 0, shortTaken = 0;
                while (got < quota && attempts < Math.Max(4, quota / 4))
                {
                    ct.ThrowIfCancellationRequested();
                    // Looked at before every batch but the job's first: a job always draws one (a tier with nobody in it
                    // draws nothing and does not count as that one).
                    if (batches > 0 && sw.Elapsed >= TeacherSampleBudget) { outOfTime = true; break; }
                    attempts++;
                    var batch = Enumerable.Range(0, TeacherSampleProbes).Select(_ => probe()).OfType<string>().Distinct().ToList();
                    if (batch.Count == 0) break;
                    batches++;
                    var rows = LoadMessages(probeConn, batch);
                    var embedded = Vectors.Get(VectorSpaces.Message, Encoder.ModelVersion, rows.Select(r => r.MessageId).ToList());
                    var picked = new List<(string Id, string Hash)>();
                    foreach (var m in rows.OrderBy(r => r.MessageId, StringComparer.Ordinal))
                    {
                        if (got >= quota) break;
                        var gate = ContentGate.Decide(new GateInput(m.Content, m.IsBot, m.IsSystem, m.IsWebhook, m.IsForwarded));
                        if (gate.Skip || gate.NonAuthored || !embedded.TryGetValue(m.MessageId, out var v) || v.SourceVersion != m.Version) continue;
                        string hash = ContentGate.NormalizedHash(m.Content);
                        if (pickedIds.Contains(m.MessageId) || !queuedHashes.Add(hash)) continue;
                        // Keep short messages from dominating: past 40% of the quota they are taken half the time.
                        if (gate.Shape.LetterGraphemes < 20)
                        {
                            if (shortTaken > quota * 0.4 && rng.NextDouble() < 0.5) continue;
                            shortTaken++;
                        }
                        picked.Add((m.MessageId, hash));
                        pickedIds.Add(m.MessageId);
                        got++;
                    }
                    if (picked.Count > 0)
                    {
                        long now = Context.Clock.NowMs;
                        await Context.Stores.Intel.WriteAsync((c, tx) =>
                        {
                            using var put = new PreparedStatement(c, tx, """
                                INSERT OR IGNORE INTO classify_teacher_queue(message_id, reason, priority, attempts, state, enqueued_at_ms, text_hash) VALUES($m,$r,$p,0,'queued',$t,$h)
                                """, "$m", "$r", "$p", "$t", "$h");
                            foreach (var p in picked) put.Run(p.Id, TeacherReasons.Sample, priority, now, p.Hash);
                        }, ct).ConfigureAwait(false);
                        added += picked.Count;
                        byTier[name] = byTier.GetValueOrDefault(name) + picked.Count;
                    }
                    await job.ReportProgressAsync(new { have = have + added, target, added, tier = name }).ConfigureAwait(false);
                }
                if (outOfTime) break;
                carry = Math.Max(0, quota - got);
            }
            return JsonSerializer.Serialize(new { have, target, added, byTier, more = outOfTime });
        }

        /// <summary>How long one sampling job draws before it ends and leaves the rest to the next.</summary>
        internal static TimeSpan TeacherSampleBudget { get; set; } = TimeSpan.FromMinutes(4);

        /// <summary>How many random probes one batch of a sampling job makes.</summary>
        internal static int TeacherSampleProbes { get; set; } = 64;

        /// <summary>A random message of a random key (author or conversation) through its time index.</summary>
        private static string? ProbeBy(Microsoft.Data.Sqlite.SqliteConnection corpus, string column, IReadOnlyList<string> keys, Random rng)
        {
            if (keys.Count == 0) return null;
            string key = keys[rng.Next(keys.Count)];
            var span = corpus.Query(null, $"SELECT MIN(sent_at_ms), MAX(sent_at_ms) FROM messages WHERE {column}=$k",
                r => (r.IsDBNull(0) ? (long?)null : r.GetInt64(0), r.IsDBNull(1) ? (long?)null : r.GetInt64(1)), ("$k", key)).First();
            if (span.Item1 is not long lo || span.Item2 is not long hi) return null;
            long at = lo + (long)(rng.NextDouble() * Math.Max(1, hi - lo));
            return corpus.Scalar<string>(null, $"SELECT message_id FROM messages WHERE {column}=$k AND sent_at_ms >= $t ORDER BY sent_at_ms LIMIT 1", ("$k", key), ("$t", at));
        }

        private static string? ProbeRowId(Microsoft.Data.Sqlite.SqliteConnection corpus, long maxRowId, Random rng)
        {
            long r = 1 + (long)(rng.NextDouble() * maxRowId);
            return corpus.Scalar<string>(null, "SELECT message_id FROM messages WHERE rowid >= $r ORDER BY rowid LIMIT 1", ("$r", r));
        }

        /// <summary>Queues messages whose signals disagree on a high-impact task for LLM adjudication (plan A2), bounded.</summary>
        internal async Task EnqueueAdjudicationAsync(IReadOnlyCollection<string> messageIds, CancellationToken ct)
        {
            if (messageIds.Count == 0) return;
            long now = Context.Clock.NowMs;
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                long queued = c.Scalar<long>(tx, "SELECT COUNT(*) FROM classify_teacher_queue WHERE state='queued'");
                if (queued >= MaxQueued) return;
                using var put = new PreparedStatement(c, tx, """
                    INSERT INTO classify_teacher_queue(message_id, reason, priority, attempts, state, enqueued_at_ms) VALUES($m,$r,400,0,'queued',$t)
                    ON CONFLICT(message_id) DO UPDATE SET reason=excluded.reason, priority=MAX(priority, 400), state='queued', attempts=0
                    WHERE classify_teacher_queue.state <> 'queued' AND classify_teacher_queue.reason <> 'adjudicate'
                    """, "$m", "$r", "$t");
                foreach (var id in messageIds.Take((int)(MaxQueued - queued))) put.Run(id, TeacherReasons.Adjudicate, now);
            }, ct).ConfigureAwait(false);
        }

        // ── the teacher call ─────────────────────────────────────────────────────

        private const int ContextLines = 4;

        /// <summary>
        /// Labels queued messages with the LLM (background work class, byte-stable taxonomy prefix, a daily call budget in
        /// Klives's day). A batch fills the model's window up to <c>classify.teacher_batch</c> targets. Answers are validated;
        /// targets the model skipped are retried (three attempts); a refusal is recorded as a refusal, never as "no label".
        /// The labelled messages are then re-fused under the live label sets so the new signal counts at once.
        /// </summary>
        private async Task<string?> TeacherAsync(JobContext job)
        {
            var ct = job.Cancellation;
            var llm = Context.Llm;
            if (llm == null) return null;
            int perDay = Setting(TeacherCallsPerDaySetting, 80);
            int calls = 0, labelled = 0, problems = 0;
            var results = new List<object>();
            // Messages that were given up on because the teacher's answer was cut off are put back. That was never about the
            // messages: the answer for a full batch did not fit its ceiling, so every call failed, and the third failure
            // parked the whole batch (120 messages every three calls; nothing was ever labelled, 20 Nov 2026).
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                UPDATE classify_teacher_queue SET state='queued', attempts=0, done_at_ms=NULL, note=NULL
                WHERE state='failed' AND (note LIKE 'NoStructuredOutput%' OR note LIKE 'Truncated%')
                """), ct).ConfigureAwait(false);
            for (int round = 0; round < 3; round++)
            {
                string day = TeacherDay(Context.Clock.NowMs);
                int today;
                using (var c = Context.Stores.Intel.OpenRead())
                    today = (int)(c.Scalar<long?>(null, "SELECT calls FROM classify_teacher_calls WHERE call_day=$d", ("$d", day)) ?? 0);
                if (today >= perDay) { results.Add(new { stopped = "daily budget reached", today, perDay }); break; }
                var outcome = await TeacherCallAsync(job, llm, ct).ConfigureAwait(false);
                if (outcome == null) break; // nothing queued
                calls++;
                labelled += outcome.Value.Labelled;
                problems += outcome.Value.Problems;
                await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                    INSERT INTO classify_teacher_calls(call_day, calls) VALUES($d,1) ON CONFLICT(call_day) DO UPDATE SET calls=calls+1
                    """, ("$d", day)), ct).ConfigureAwait(false);
            }
            await RefreshHealthAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { calls, labelled, problems, results });
        }

        /// <summary>Room for the teacher's answer: a base, and so much per message it labels (a label for each task and a few
        /// words of why). At 40 tokens a message every full batch was cut off.</summary>
        internal const int TeacherAnswerBase = 600, TeacherAnswerPerTarget = 110;

        /// <summary>The share of a full batch the next teacher call takes. Halved whenever an answer is cut off even after
        /// being given more room, and brought back up a step with every call that works.</summary>
        private double teacherBatchShare = 1.0;

        private async Task<(int Labelled, int Problems)?> TeacherCallAsync(JobContext job, OmniLlm llm, CancellationToken ct)
        {
            int maxTargets = Math.Max(8, (int)(Setting(TeacherBatchSetting, 120) * teacherBatchShare));
            List<(string Id, string Reason)> queued;
            using (var c = Context.Stores.Intel.OpenRead())
                queued = c.Query(null, "SELECT message_id, reason FROM classify_teacher_queue WHERE state='queued' ORDER BY priority DESC, enqueued_at_ms, message_id LIMIT $l",
                    r => (r.GetString(0), r.GetString(1)), ("$l", maxTargets));
            if (queued.Count == 0) return null;

            var info = await llm.ContextAsync(null, ct).ConfigureAwait(false);
            // Output dominates slot time (R1), so the answer gets what it needs and no thinking on top.
            int outputBudget = Math.Min(info.MaxOutputTokens, TeacherAnswerBase + TeacherAnswerPerTarget * queued.Count);
            int tokenBudget = ContextPacker.Budget(info, outputBudget) - llm.Estimator.Estimate(TeacherPrompt.System);
            long byteBudget = ContextPacker.ByteBudget(info) - System.Text.Encoding.UTF8.GetByteCount(TeacherPrompt.System);

            List<MessageRow> targets;
            List<string> klivesNames;
            HashSet<string> self;
            var segments = new List<TeacherSegment>();
            var refs = new Dictionary<string, string>(StringComparer.Ordinal);
            var reasons = queued.ToDictionary(q => q.Id, q => q.Reason, StringComparer.Ordinal);
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                targets = LoadMessages(corpus, queued.Select(q => q.Id).ToList());
                self = SelfIdentities(corpus);
                klivesNames = self.Count == 0 ? new List<string>() : corpus.QueryIn(null,
                    "SELECT DISTINCT value FROM identity_names WHERE valid_to_ms IS NULL AND identity_id IN ({keys})", self.ToList(), r => r.GetString(0)).Take(10).ToList();
                var pseudonyms = new Dictionary<string, string>(StringComparer.Ordinal);
                string Name(string identity) => self.Contains(identity) ? "Klives"
                    : pseudonyms.TryGetValue(identity, out var n) ? n : pseudonyms[identity] = "P" + (pseudonyms.Count + 1).ToString(CultureInfo.InvariantCulture);
                int next = 1, usedTokens = llm.Estimator.Estimate(TeacherPrompt.User(klivesNames, Array.Empty<TeacherSegment>()));
                long usedBytes = 0;
                var drainContext = NewDrainContext(corpus);
                foreach (var conv in targets.GroupBy(t => t.ConversationId).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    var lines = new List<TeacherLine>();
                    var ordered = conv.OrderBy(t => t.SentAtMs).ThenBy(t => t.OrderKey).ToList();
                    var shown = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var t in ordered)
                    {
                        var context = corpus.Query(null, """
                            SELECT message_id, author_identity_id, sent_at_ms, time_quality, content FROM messages
                            WHERE conversation_id=$c AND (sent_at_ms, order_key) < ($t, $o) AND sent_at_ms >= $from ORDER BY sent_at_ms DESC, order_key DESC LIMIT $n
                            """, r => (Id: r.GetString(0), Author: r.GetString(1), At: r.GetInt64(2), Q: r.GetInt32(3), Text: r.GetStringOrNull(4)),
                            ("$c", t.ConversationId), ("$t", t.SentAtMs), ("$o", t.OrderKey), ("$from", t.SentAtMs - 6 * 3_600_000L), ("$n", ContextLines)).AsEnumerable().Reverse();
                        foreach (var cx in context)
                        {
                            if (!shown.Add(cx.Id)) continue;
                            lines.Add(new TeacherLine((next++).ToString(CultureInfo.InvariantCulture), cx.Id, Name(cx.Author), cx.At, cx.Q, cx.Text ?? "", Target: false));
                        }
                        if (!shown.Add(t.MessageId)) lines.RemoveAll(l => l.MessageId == t.MessageId);
                        lines.Add(new TeacherLine((next++).ToString(CultureInfo.InvariantCulture), t.MessageId, Name(t.AuthorIdentityId), t.SentAtMs, 0, t.Content ?? "", Target: true));
                    }
                    bool withKlives = DirectWithKlives(corpus, drainContext, ordered[0]);
                    var segment = new TeacherSegment(ordered[0].ConversationKind ?? "chat", withKlives || ordered.Any(o => self.Contains(o.AuthorIdentityId)), lines);
                    string text = TeacherPrompt.User(Array.Empty<string>(), new[] { segment });
                    int cost = llm.Estimator.Estimate(text);
                    long bytes = System.Text.Encoding.UTF8.GetByteCount(text);
                    if (segments.Count > 0 && (usedTokens + cost > tokenBudget || usedBytes + bytes > byteBudget)) break;
                    segments.Add(segment);
                    usedTokens += cost;
                    usedBytes += bytes;
                    foreach (var l in lines.Where(l => l.Target)) refs[l.Ref] = l.MessageId;
                }
            }
            if (refs.Count == 0)
            {
                // Every queued id vanished from the corpus (erased): clear them so the queue cannot wedge.
                await MarkQueueAsync(queued.Select(q => q.Id), "gone", "message no longer in the corpus", ct).ConfigureAwait(false);
                return (0, 0);
            }

            string user = TeacherPrompt.User(klivesNames, segments);
            var result = await llm.CallAsync(new LlmCall
            {
                ModuleId = ModuleId,
                Purpose = "classify.teacher",
                PromptVersion = TeacherPromptVersion,
                JobId = job.Job.JobId,
                LongerWhenCutOff = true,
                Request = new LlmRequest
                {
                    Purpose = "classify.teacher",
                    SessionKey = "omniscience-bg-classify-teacher",
                    SystemPrompt = TeacherPrompt.System,
                    Turns = new[] { new LlmTurn("user", user) },
                    Stamp = LlmStamp.None,
                    MaxOutputTokens = outputBudget,
                    WorkClass = AIRouterWorkClass.Background,
                    OutputSchema = TeacherPrompt.Schema,
                    Sampling = new ModelSamplingParameters(Temperature: 0),
                    ReasoningEffort = "off",
                },
            }, ct).ConfigureAwait(false);

            if (!result.Success)
            {
                if (OmniLlm.CutOff(result, null) || result.ErrorKind == LlmErrorKind.NoStructuredOutput && result.CompletionTokens >= outputBudget)
                {
                    // The answer did not fit even with more room: the batch was too large, which is not the messages' doing.
                    // They stay queued with their attempts as they were, and the next call takes half as many.
                    teacherBatchShare = Math.Max(0.05, teacherBatchShare / 2);
                    await job.ReportProgressAsync(new { teacherCutOff = true, targets = refs.Count, nextShare = teacherBatchShare }).ConfigureAwait(false);
                    return (0, 1);
                }
                string state = result.ErrorKind == LlmErrorKind.Refused ? "refused" : "queued";
                // Worded so that the put-back in TeacherAsync (which looks for the notes cut-off answers used to leave) never
                // takes these for one of them.
                await BumpAttemptsAsync(refs.Values, state, $"teacher call failed ({result.ErrorKind}): {result.ErrorMessage}", ct).ConfigureAwait(false);
                return (0, 1);
            }
            teacherBatchShare = Math.Min(1.0, teacherBatchShare * 1.5);
            var parse = TeacherPrompt.Parse(result.StructuredJson ?? result.Text, refs);
            long now = Context.Clock.NowMs;
            var byId = targets.ToDictionary(t => t.MessageId, StringComparer.Ordinal);
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                using var put = new PreparedStatement(c, tx, """
                    INSERT INTO labels(item_kind, item_id, item_version, task, label, probability, abstained, signals_json, model, model_version, prompt_version, as_of_ms, source_seq)
                    VALUES('message',$i,$v,$t,$l,1.0,0,$sj,$model,$mv,$pv,$a,$s)
                    ON CONFLICT(item_kind, item_id, task, model_version) DO UPDATE SET item_version=excluded.item_version, label=excluded.label,
                        signals_json=excluded.signals_json, model=excluded.model, prompt_version=excluded.prompt_version, as_of_ms=excluded.as_of_ms, source_seq=excluded.source_seq
                    """, "$i", "$v", "$t", "$l", "$sj", "$model", "$mv", "$pv", "$a", "$s");
                foreach (var a in parse.Answers)
                {
                    var m = byId[a.MessageId];
                    string mv = reasons.GetValueOrDefault(a.MessageId) == TeacherReasons.Adjudicate ? AdjudicationVersion : TeacherVersion;
                    string? sj = a.Why == null ? null : JsonSerializer.Serialize(new { why = a.Why });
                    foreach (var (task, label) in a.Labels) put.Run(m.MessageId, m.Version, task, label, sj, result.Model ?? "llm", mv, TeacherPromptVersion, now, m.IngestSeq);
                }
            }, ct).ConfigureAwait(false);
            var answered = parse.Answers.Select(a => a.MessageId).ToHashSet(StringComparer.Ordinal);
            await MarkQueueAsync(answered, "done", null, ct).ConfigureAwait(false);
            await BumpAttemptsAsync(refs.Values.Where(id => !answered.Contains(id)), "queued", "not answered by the teacher", ct).ConfigureAwait(false);

            // The new LLM signal enters fusion now, not at the next relabel.
            var labelsets = LiveLabelsets();
            if (labelsets.Count > 0 && answered.Count > 0)
            {
                DrainContext dc;
                using (var corpus = Context.Stores.Corpus.OpenRead()) dc = NewDrainContext(corpus);
                await LabelAsync(answered.ToList(), labelsets, dc, ct).ConfigureAwait(false);
            }
            await job.ReportProgressAsync(new { teacherLabelled = parse.Answers.Count, problems = parse.Problems.Take(10) }).ConfigureAwait(false);
            return (parse.Answers.Count, parse.Problems.Count);
        }

        private Task MarkQueueAsync(IEnumerable<string> ids, string state, string? note, CancellationToken ct)
        {
            var list = ids.ToList();
            long now = Context.Clock.NowMs;
            return Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                using var upd = new PreparedStatement(c, tx, "UPDATE classify_teacher_queue SET state=$s, done_at_ms=$t, note=COALESCE($n, note) WHERE message_id=$m", "$s", "$t", "$n", "$m");
                foreach (var id in list) upd.Run(state, now, note, id);
            }, ct);
        }

        /// <summary>Counts a failed attempt; the third failure parks the item as failed with its reason.</summary>
        private Task BumpAttemptsAsync(IEnumerable<string> ids, string state, string note, CancellationToken ct)
        {
            var list = ids.ToList();
            long now = Context.Clock.NowMs;
            return Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                using var upd = new PreparedStatement(c, tx, """
                    UPDATE classify_teacher_queue SET attempts=attempts+1,
                        state=CASE WHEN $s='refused' THEN 'refused' WHEN attempts+1 >= 3 THEN 'failed' ELSE $s END,
                        done_at_ms=CASE WHEN $s='refused' OR attempts+1 >= 3 THEN $t ELSE done_at_ms END, note=$n
                    WHERE message_id=$m
                    """, "$s", "$t", "$n", "$m");
                foreach (var id in list) upd.Run(state, now, note, id);
            }, ct);
        }
    }
}
