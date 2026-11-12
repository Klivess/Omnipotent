using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Ml.Classification;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.Classification
{
    public sealed partial class ClassificationModule
    {
        internal const double DefaultLlmReliability = 0.85;
        private const int MinGoldForReliability = 20;

        /// <summary>Tasks whose disagreements are worth an LLM adjudication (high impact: they drive alerts and facts).</summary>
        internal static readonly IReadOnlySet<string> HighImpactTasks = new HashSet<string>(StringComparer.Ordinal)
        {
            "toxicity", "conflict", "about_klives", "distress", "modality",
        };

        /// <summary>Features of one labelled message, computed once per training run.</summary>
        private sealed record TrainingRow(string MessageId, string TextHash, float[] Single, float[] Context, bool HasPrevious);

        /// <summary>
        /// Trains every task's heads from teacher + gold labels, gates them against the active heads, learns the LLM's
        /// reliability and the fusion weights on gold, and publishes a new label set when anything changed. The new label
        /// set is filled by the relabel job while the current one keeps serving.
        /// </summary>
        private async Task<string?> TrainAsync(JobContext job)
        {
            var ct = job.Cancellation;
            var sw = Stopwatch.StartNew();
            var encoder = Encoder;
            var gold = ClassificationReader.LoadGold(Context.Stores);

            // 1. Every labelled message (teacher, adjudication, gold) at its current version.
            List<(string Item, string Task, string Label, string Version, int ItemVersion)> llmLabels;
            using (var intel = Context.Stores.Intel.OpenRead())
                llmLabels = intel.Query(null, "SELECT item_id, task, label, model_version, item_version FROM labels WHERE item_kind='message' AND model_version IN ($a,$b) AND label IS NOT NULL",
                    r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4)), ("$a", TeacherVersion), ("$b", AdjudicationVersion));
            var ids = llmLabels.Select(l => l.Item).Concat(gold.Keys.Select(k => k.Message)).Distinct(StringComparer.Ordinal).ToList();
            if (ids.Count == 0) return JsonSerializer.Serialize(new { trained = 0, reason = "no labelled messages yet" });

            List<MessageRow> rows;
            DrainContext dc;
            var previous = new Dictionary<string, (string Id, string Author)>(StringComparer.Ordinal);
            var flagsById = new Dictionary<string, ContextFlags>(StringComparer.Ordinal);
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                rows = LoadMessages(corpus, ids);
                dc = NewDrainContext(corpus);
                var mentions = dc.Self.Count == 0 ? new HashSet<string>() : corpus.QueryIn(null,
                        "SELECT message_id, target_id FROM message_mentions WHERE target_kind='user' AND message_id IN ({keys})", ids, r => (r.GetString(0), r.GetString(1)))
                    .Where(x => dc.Self.Contains(x.Item2)).Select(x => x.Item1).ToHashSet(StringComparer.Ordinal);
                var replyAuthors = corpus.QueryIn(null, "SELECT message_id, author_identity_id FROM messages WHERE message_id IN ({keys})",
                    rows.Where(r => r.ReplyTo != null).Select(r => r.ReplyTo!).Distinct().ToList(), r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
                foreach (var m in rows)
                {
                    var prev = corpus.Query(null, """
                        SELECT message_id, author_identity_id, sent_at_ms FROM messages
                        WHERE conversation_id=$c AND (sent_at_ms, order_key) < ($t, $o) ORDER BY sent_at_ms DESC, order_key DESC LIMIT 1
                        """, r => (r.GetString(0), r.GetString(1), r.GetInt64(2)), ("$c", m.ConversationId), ("$t", m.SentAtMs), ("$o", m.OrderKey)).FirstOrDefault();
                    if (prev != default && m.SentAtMs - prev.Item3 <= dc.GapMs) previous[m.MessageId] = (prev.Item1, prev.Item2);
                    flagsById[m.MessageId] = new ContextFlags(
                        PreviousByKlives: previous.TryGetValue(m.MessageId, out var p) && dc.Self.Contains(p.Author),
                        DirectWithKlives: DirectWithKlives(corpus, dc, m),
                        RepliesToKlives: m.ReplyTo != null && replyAuthors.TryGetValue(m.ReplyTo, out var ra) && dc.Self.Contains(ra),
                        MentionsKlives: mentions.Contains(m.MessageId),
                        SameAuthorAsPrevious: previous.TryGetValue(m.MessageId, out var p2) && p2.Author == m.AuthorIdentityId);
                }
            }
            var vectors = Vectors.Get(VectorSpaces.Message, encoder.ModelVersion, ids.Concat(previous.Values.Select(p => p.Id)).Distinct(StringComparer.Ordinal).ToList());
            var versions = rows.ToDictionary(r => r.MessageId, r => r.Version, StringComparer.Ordinal);
            var features = new Dictionary<string, TrainingRow>(StringComparer.Ordinal);
            foreach (var m in rows)
            {
                if (!vectors.TryGetValue(m.MessageId, out var v) || v.SourceVersion != m.Version) continue;
                float[]? prevVec = previous.TryGetValue(m.MessageId, out var p) && vectors.TryGetValue(p.Id, out var pv) ? pv.Vector : null;
                features[m.MessageId] = new TrainingRow(m.MessageId, ContentGate.NormalizedHash(m.Content), v.Vector,
                    ContextFeatures.Build(v.Vector, prevVec ?? ReadOnlySpan<float>.Empty, flagsById[m.MessageId]), prevVec != null);
            }

            // Adjudication outranks the teacher; both only at the message's current version.
            var llmByItem = new Dictionary<(string, string), string>();
            foreach (var l in llmLabels.OrderBy(l => l.Version == AdjudicationVersion ? 1 : 0))
                if (versions.TryGetValue(l.Item, out int v) && v == l.ItemVersion) llmByItem[(l.Item, l.Task)] = l.Label;

            // 2. Heads per task.
            int nextVersion;
            Dictionary<(string Task, string Kind), (int Version, ClassifierMetrics Metrics, ClassifierMetrics? Gold)> active;
            using (var intel = Context.Stores.Intel.OpenRead())
            {
                nextVersion = (int)(intel.Scalar<long?>(null, "SELECT MAX(version) FROM classify_heads") ?? 0) + 1;
                active = intel.Query(null, "SELECT task, kind, version, metrics_json, gold_metrics_json FROM classify_heads WHERE state='active'",
                        r => (r.GetString(0), r.GetString(1), r.GetInt32(2), JsonSerializer.Deserialize<ClassifierMetrics>(r.GetString(3), Kernel.Jobs.KernelJson.Options)!,
                            r.GetStringOrNull(4) is { } g ? JsonSerializer.Deserialize<ClassifierMetrics>(g, Kernel.Jobs.KernelJson.Options) : null))
                    .ToDictionary(x => (x.Item1, x.Item2), x => (x.Item3, x.Item4, x.Item5));
            }
            var trained = new List<(LinearHead Head, HeadTrainingReport Report, bool Promote)>();
            var summary = new List<object>();
            foreach (var task in ClassificationTasks.All)
            {
                ct.ThrowIfCancellationRequested();
                var items = features.Values.Select(f =>
                {
                    bool isGold = gold.TryGetValue((f.MessageId, task.Id), out var g);
                    string? label = isGold ? g : llmByItem.GetValueOrDefault((f.MessageId, task.Id));
                    return (Row: f, Label: label, Gold: isGold);
                }).Where(x => x.Label != null && task.IndexOf(x.Label) >= 0).ToList();
                foreach (var kind in task.ContextMatters ? new[] { HeadKind.Single, HeadKind.Context } : new[] { HeadKind.Single })
                {
                    var data = items.Select(x => new LabelledItem(x.Row.MessageId, kind == HeadKind.Single ? x.Row.Single : x.Row.Context, x.Label!, x.Row.TextHash, x.Gold)).ToList();
                    var report = await Task.Run(() => HeadTrainer.Train(task, kind, nextVersion, encoder.ModelVersion, data, ct: ct), ct).ConfigureAwait(false);
                    if (report == null) { summary.Add(new { task = task.Id, kind = kind.ToString(), skipped = $"too few examples ({data.Count})" }); continue; }
                    active.TryGetValue((task.Id, kind.ToString().ToLowerInvariant()), out var current);
                    bool promote = HeadTrainer.Promotes(report.Validation, report.GoldValidation, current == default ? null : current.Metrics, current == default ? null : current.Gold);
                    trained.Add((report.Head, report, promote));
                    summary.Add(new { task = task.Id, kind = kind.ToString(), macroF1 = Math.Round(report.Validation.MacroF1, 3), promote, train = report.TrainItems, validation = report.ValidationItems });
                }
            }

            // 3. LLM reliability (teacher vs gold) and fusion weights, both on gold only (teacher labels are the LLM itself).
            var reliability = new Dictionary<string, double>(StringComparer.Ordinal);
            var fusion = new Dictionary<string, FusionConfigDto>(StringComparer.Ordinal);
            var headsFor = trained.Where(t => t.Promote).ToDictionary(t => (t.Head.Task, t.Head.Kind), t => t.Head);
            LinearHead? HeadOf(string task, HeadKind kind)
            {
                if (headsFor.TryGetValue((task, kind), out var h)) return h;
                if (!active.TryGetValue((task, kind.ToString().ToLowerInvariant()), out var a)) return null;
                using var intel = Context.Stores.Intel.OpenRead();
                return LoadHead(intel, task, kind, a.Version);
            }
            foreach (var task in ClassificationTasks.All)
            {
                var goldItems = gold.Where(g => g.Key.Task == task.Id && features.ContainsKey(g.Key.Message) && task.IndexOf(g.Value) >= 0).ToList();
                var withLlm = goldItems.Where(g => llmByItem.ContainsKey((g.Key.Message, task.Id))).ToList();
                double r = withLlm.Count >= MinGoldForReliability
                    ? withLlm.Count(g => string.Equals(llmByItem[(g.Key.Message, task.Id)], g.Value, StringComparison.OrdinalIgnoreCase)) / (double)withLlm.Count
                    : DefaultLlmReliability;
                reliability[task.Id] = Math.Clamp(r, 0.34, 0.99);
                var single = HeadOf(task.Id, HeadKind.Single);
                var context = HeadOf(task.Id, HeadKind.Context);
                var stackRows = goldItems.Select(g =>
                {
                    var f = features[g.Key.Message];
                    var signals = new List<SignalOpinion>();
                    if (single != null) signals.Add(new SignalOpinion(Signals.Head, single.Predict(f.Single), single.ModelVersion));
                    if (context != null) signals.Add(new SignalOpinion(Signals.Context, context.Predict(f.Context), context.ModelVersion));
                    if (llmByItem.TryGetValue((g.Key.Message, task.Id), out var l))
                        signals.Add(new SignalOpinion(Signals.Llm, Ensemble.LlmDistribution(task, l, reliability[task.Id]), TeacherVersion));
                    return (Gold: task.IndexOf(g.Value), Signals: (IReadOnlyList<SignalOpinion>)signals);
                }).ToList();
                fusion[task.Id] = FusionConfigDto.From(Ensemble.FitStacker(task, stackRows, goldItems.Count));
            }

            // 4. Persist heads and, when anything changes, a new label set to build.
            long now = Context.Clock.NowMs;
            int? activeSet = ActiveLabelset();
            var activeLabelset = activeSet == null ? null : LoadLabelset(activeSet.Value);
            var newHeads = new LabelsetHeads(
                ClassificationTasks.All.Select(t => (t.Id, HeadOf(t.Id, HeadKind.Single))).Where(x => x.Item2 != null).ToDictionary(x => x.Id, x => x.Item2!.Version),
                ClassificationTasks.All.Select(t => (t.Id, HeadOf(t.Id, HeadKind.Context))).Where(x => x.Item2 != null).ToDictionary(x => x.Id, x => x.Item2!.Version));
            var newFusion = new LabelsetFusion(fusion, reliability);
            bool headsChanged = activeLabelset == null || trained.Any(t => t.Promote);
            bool fusionChanged = activeLabelset == null || fusion.Any(kv =>
                !activeLabelset.Fusion.TryGetValue(kv.Key, out var old) || old.Source != kv.Value.Source
                || kv.Value.Weights.Any(w => Math.Abs(old.Weights.GetValueOrDefault(w.Key) - w.Value) > 0.05))
                || reliability.Any(kv => Math.Abs(activeLabelset.LlmReliability.GetValueOrDefault(kv.Key, DefaultLlmReliability) - kv.Value) > 0.03);
            int? published = null;
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                using var put = new PreparedStatement(c, tx, """
                    INSERT INTO classify_heads(task, kind, version, task_version, encoder, labels_json, dims, weights, bias, temperature, metrics_json, gold_metrics_json, train_items, state, trained_at_ms)
                    VALUES($t,$k,$v,$tv,$e,$l,$d,$w,$b,$temp,$m,$g,$n,$s,$at)
                    """, "$t", "$k", "$v", "$tv", "$e", "$l", "$d", "$w", "$b", "$temp", "$m", "$g", "$n", "$s", "$at");
                foreach (var (head, report, promote) in trained)
                    put.Run(head.Task, head.Kind.ToString().ToLowerInvariant(), head.Version, ClassificationTasks.Get(head.Task).Version, head.EncoderVersion,
                        head.LabelsJson(), head.Dims, VectorMath.ToBytes(head.Weights), VectorMath.ToBytes(head.Bias), head.Temperature,
                        JsonSerializer.Serialize(report.Validation, Kernel.Jobs.KernelJson.Options),
                        report.GoldValidation == null ? null : JsonSerializer.Serialize(report.GoldValidation, Kernel.Jobs.KernelJson.Options),
                        report.TrainItems, promote ? "candidate" : "rejected", now);
                if ((headsChanged || fusionChanged) && newHeads.Single.Count + newHeads.Context.Count > 0)
                {
                    // A building label set that never finished is superseded: only one builds at a time.
                    c.Exec(tx, "UPDATE classify_labelsets SET state='superseded' WHERE state='building'");
                    long id = (c.Scalar<long?>(tx, "SELECT MAX(labelset) FROM classify_labelsets") ?? 0) + 1;
                    c.Exec(tx, "INSERT INTO classify_labelsets(labelset, state, encoder, heads_json, fusion_json, created_at_ms) VALUES($i,'building',$e,$h,$f,$t)",
                        ("$i", id), ("$e", encoder.ModelVersion), ("$h", JsonSerializer.Serialize(newHeads, Kernel.Jobs.KernelJson.Options)),
                        ("$f", JsonSerializer.Serialize(newFusion, Kernel.Jobs.KernelJson.Options)), ("$t", now));
                    published = (int)id;
                }
            }, ct).ConfigureAwait(false);
            await SetStateAsync("train.last", new TrainMarker(llmLabels.Select(l => l.Item).Distinct().LongCount(), gold.Count, now), ct).ConfigureAwait(false);
            if (published is int ls)
                await Context.Jobs.EnqueueAsync(Job("relabel", "labelset", ls.ToString(CultureInfo.InvariantCulture), priority: 110), ct).ConfigureAwait(false);
            await RefreshHealthAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { labelled = features.Count, published, seconds = (int)sw.Elapsed.TotalSeconds, heads = summary });
        }

        /// <summary>
        /// Fills a building label set over every embedded message (in id order, resumable from its cursor), then activates it
        /// atomically: the old set retires (still queryable), heads change state, the author language priors are rebuilt
        /// from the new labels, and a <c>classify.labelset</c> event announces it.
        /// </summary>
        private async Task<string?> RelabelAsync(JobContext job)
        {
            var ct = job.Cancellation;
            if (!int.TryParse(job.Job.SubjectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) throw new JobFailureException(ErrorClass.Permanent, "Bad label set id.");
            var set = LoadLabelset(id);
            if (set == null || set.State != "building") return JsonSerializer.Serialize(new { id, skipped = set?.State ?? "missing" });
            if (set.EncoderVersion != Encoder.ModelVersion) throw new JobFailureException(ErrorClass.Permanent, $"Label set {id} was trained for encoder {set.EncoderVersion}, not {Encoder.ModelVersion}.");
            var sw = Stopwatch.StartNew();
            string? cursor = set.CursorItem;
            long done = 0;
            DrainContext dc;
            using (var corpus = Context.Stores.Corpus.OpenRead()) dc = NewDrainContext(corpus);
            while (sw.Elapsed < DrainBudget)
            {
                ct.ThrowIfCancellationRequested();
                var page = Vectors.Page(VectorSpaces.Message, Encoder.ModelVersion, cursor, 2_000);
                if (page.Count == 0) { await ActivateAsync(id, ct).ConfigureAwait(false); return JsonSerializer.Serialize(new { id, activated = true, done }); }
                var current = LoadLabelset(id);
                if (current == null || current.State != "building") return JsonSerializer.Serialize(new { id, superseded = true, done });
                int written = await LabelAsync(page.Select(p => p.ItemId).ToList(), new[] { current }, dc, ct).ConfigureAwait(false);
                cursor = page[^1].ItemId;
                done += page.Count;
                await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE classify_labelsets SET cursor_item=$c, items_done=items_done+$n WHERE labelset=$l",
                    ("$c", cursor), ("$n", page.Count), ("$l", id)), ct).ConfigureAwait(false);
                await job.ReportProgressAsync(new { labelset = id, done, written, cursor }).ConfigureAwait(false);
            }
            return JsonSerializer.Serialize(new { id, done, more = true });
        }

        private async Task ActivateAsync(int id, CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            var set = LoadLabelset(id)!;
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "UPDATE classify_labelsets SET state='retired' WHERE state='active'");
                c.Exec(tx, "UPDATE classify_labelsets SET state='active', completed_at_ms=$t WHERE labelset=$l", ("$t", now), ("$l", id));
                c.Exec(tx, "UPDATE classify_heads SET state='retired' WHERE state='active'");
                foreach (var h in set.Single.Values.Concat(set.ContextHeads.Values))
                    c.Exec(tx, "UPDATE classify_heads SET state='active' WHERE task=$t AND kind=$k AND version=$v",
                        ("$t", h.Task), ("$k", h.Kind.ToString().ToLowerInvariant()), ("$v", h.Version));
            }, ct).ConfigureAwait(false);
            await RebuildAuthorLanguagesAsync(id, ct).ConfigureAwait(false);
            await Context.Events.PublishAsync("classify.labelset", new { labelset = id, activatedAtMs = now }, ct).ConfigureAwait(false);
            await Context.Jobs.EnqueueAsync(Job("prune", "stream", "labelsets", priority: 50), ct).ConfigureAwait(false);
            await RefreshHealthAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Recounts every author's answered languages under the newly active label set (the N19 prior).</summary>
        private async Task RebuildAuthorLanguagesAsync(int labelset, CancellationToken ct)
        {
            var counts = new Dictionary<(string Author, string Lang), long>();
            string after = "";
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                List<(string Id, string Codes)> page;
                using (var intel = Context.Stores.Intel.OpenRead())
                    page = intel.Query(null, "SELECT message_id, codes FROM classify_message WHERE labelset=$l AND message_id > $a ORDER BY message_id LIMIT 5000",
                        r => (r.GetString(0), r.GetString(1)), ("$l", labelset), ("$a", after));
                if (page.Count == 0) break;
                after = page[^1].Id;
                Dictionary<string, string> authors;
                using (var corpus = Context.Stores.Corpus.OpenRead())
                    authors = corpus.QueryIn(null, "SELECT message_id, author_identity_id FROM messages WHERE message_id IN ({keys})", page.Select(p => p.Id).ToList(),
                        r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
                foreach (var (mid, codes) in page)
                {
                    if (LabelCodec.Answer(codes, ClassificationTasks.Language.Id) is not { } lang || !authors.TryGetValue(mid, out var author)) continue;
                    counts[(author, lang)] = counts.GetValueOrDefault((author, lang)) + 1;
                }
            }
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "DELETE FROM classify_author_lang");
                using var put = new PreparedStatement(c, tx, "INSERT INTO classify_author_lang(author_identity_id, lang, n) VALUES($a,$l,$n)", "$a", "$l", "$n");
                foreach (var ((author, lang), n) in counts) put.Run(author, lang, n);
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Drops the rows of label sets older than the one most recently retired (A3: old labels stay queryable — the previous
        /// generation is kept for comparison; anything older is re-derivable from the stored heads and embeddings).
        /// </summary>
        private async Task<string?> PruneAsync(JobContext job)
        {
            var ct = job.Cancellation;
            List<int> doomed;
            using (var intel = Context.Stores.Intel.OpenRead())
            {
                var retired = intel.Query(null, "SELECT labelset FROM classify_labelsets WHERE state IN ('retired','superseded') ORDER BY labelset DESC", r => (int)r.GetInt64(0));
                int? keep = intel.Scalar<long?>(null, "SELECT MAX(labelset) FROM classify_labelsets WHERE state='retired'") is long k ? (int)k : null;
                doomed = retired.Where(r => r != keep).ToList();
            }
            long deleted = 0;
            foreach (int ls in doomed)
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx,
                        "DELETE FROM classify_message WHERE labelset=$l AND message_id IN (SELECT message_id FROM classify_message WHERE labelset=$l LIMIT 5000)",
                        ("$l", ls)), ct).ConfigureAwait(false);
                    deleted += n;
                    if (n == 0) break;
                }
                await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE classify_labelsets SET state='pruned' WHERE labelset=$l", ("$l", ls)), ct).ConfigureAwait(false);
            }
            return JsonSerializer.Serialize(new { pruned = doomed, deleted });
        }
    }
}
