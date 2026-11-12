using System.Globalization;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>One evaluated held-out reply.</summary>
    public sealed record EvalItem(string PairId, string ConfigKey, string Split, string ContextKind, long OccurredMs, long AsOfMs, string? Generated, string Outcome,
        bool? JudgeCorrect, double? JudgeConfidence, double? EmbeddingSimilarity, double? StyleDistance, double? LengthError, double? BurstError, string? Model, string? Detail);

    /// <summary>
    /// Fidelity evaluation with auto-tuning (plan "Evaluation", F321–F324). Each held-out reply is regenerated exactly the
    /// way chat would (same composer, candidates and judge) by a replica that has never seen it: held-out pairs and their
    /// shadows are invisible to every prompt-visible source of the version, and each reply is composed as of the start of
    /// its calendar quarter, so nothing from its own period or later is in the prompt (temporal holdout). Replies of one
    /// quarter share that prefix, so a bucket costs one cold prefill and every other call is served from cache. A
    /// discrimination judge then sees the real and the replica reply (order by stable hash) and picks the real one:
    /// 50% means indistinguishable. Auto-tune picks temperature on the tuning split; only the separate test split is
    /// reported, with a Wilson interval. Every item is stored as it finishes, so a restarted evaluation resumes.
    /// </summary>
    public sealed class ReplicaEvaluator
    {
        private sealed record DiscriminateAnswer(string? Real, double? Confidence);

        private static readonly LlmOutputSchema DiscriminateSchema = ReplicaLlm.Schema("which_is_real", "Which reply the person really wrote.", """
            {"type":"object","required":["real"],"properties":{"real":{"type":"string","enum":["A","B"]},"confidence":{"type":"number","minimum":0,"maximum":1}}}
            """);

        public const int MinPriorPairs = 60;

        private readonly ReplicaServices s;

        public ReplicaEvaluator(ReplicaServices services) => s = services;

        internal async Task<StageResult> RunAsync(VersionRecord v, JobContext job)
        {
            var ct = job.Cancellation;
            if (s.Llm == null || s.Generator == null) return StageResult.Degraded("No LLM gateway: fidelity was not measured.");
            if (s.Llm.Llm.CircuitOpen) throw ReplicaFailures.Transient("The LLM circuit is open (credentials refused); evaluation waits.");
            var snapshot = ReplicaSnapshot.Load(s.Stores, v.VersionId);
            var test = snapshot.Pairs.Where(p => p.Split == PairSplits.Test).OrderBy(p => TextFeatures.Unit("eval|" + p.PairId)).Take(s.Settings.EvalPairs).ToList();
            var tune = snapshot.Pairs.Where(p => p.Split == PairSplits.Tune).OrderBy(p => TextFeatures.Unit("eval|" + p.PairId)).Take(s.Settings.TunePairs).ToList();
            if (test.Count == 0) return StageResult.Skipped("Too few eligible replies to hold any out; fidelity was not measured.");

            var previous = PreviousConfig(v);
            var baseConfig = (previous ?? GenerationConfig.Default) with { Candidates = s.Settings.CandidatesInteractive, LlmJudge = s.Settings.LlmJudge };
            var configs = new List<GenerationConfig> { baseConfig };
            bool tuning = s.Settings.AutoTune && tune.Count >= 6 && (previous == null || v.Number % 5 == 1);
            if (tuning)
            {
                configs.Add(baseConfig with { Temperature = Math.Round(Math.Max(0.3, baseConfig.Temperature - 0.2), 2) });
                configs.Add(baseConfig with { Temperature = Math.Round(Math.Min(1.4, baseConfig.Temperature + 0.2), 2) });
            }

            int done = 0, total = (tuning ? tune.Count * configs.Count : 0) + test.Count;
            var tuneResults = new List<ReplicaTuneResultDto>();
            var chosen = baseConfig;
            if (tuning)
            {
                var scored = new List<(GenerationConfig Config, IReadOnlyList<EvalItem> Items)>();
                foreach (var config in configs)
                {
                    var items = await EvaluateSetAsync(snapshot, tune, config, PairSplits.Tune, job, () => done++, total, ct).ConfigureAwait(false);
                    scored.Add((config, items));
                }
                chosen = scored.OrderBy(x => Accuracy(x.Items) ?? 1).ThenByDescending(x => Mean(x.Items, i => i.EmbeddingSimilarity) ?? 0)
                    .ThenBy(x => Mean(x.Items, i => i.StyleDistance) ?? 1).ThenBy(x => x.Config.Key, StringComparer.Ordinal).First().Config;
                tuneResults = scored.Select(x => new ReplicaTuneResultDto(x.Config.Key, x.Config.Temperature, x.Config.Retrieved, x.Config.BurstSampling, x.Items.Count(i => i.Outcome == "ok"),
                    Accuracy(x.Items), Mean(x.Items, i => i.EmbeddingSimilarity), Mean(x.Items, i => i.StyleDistance), x.Config.Key == chosen.Key)).ToList();
            }
            var testItems = await EvaluateSetAsync(snapshot, test, chosen, PairSplits.Test, job, () => done++, total, ct).ConfigureAwait(false);
            var metrics = Aggregate(testItems, chosen, tuneResults, s.Clock.NowMs);
            await s.Versions.UpdateFieldsAsync(v.VersionId, metricsJson: RJson.Write(metrics), configJson: RJson.Write(chosen), ct: ct).ConfigureAwait(false);
            string detail = metrics.DiscriminationAccuracy is double acc
                ? string.Create(CultureInfo.InvariantCulture, $"judge picked the real reply {acc:P0} of the time on {metrics.Evaluated} held-out replies (50% = indistinguishable); config {chosen.Key}")
                : $"no held-out reply could be judged ({metrics.Failed} failed)";
            return metrics.Evaluated == 0 ? StageResult.Degraded("Fidelity not measured: " + detail) : StageResult.Done(detail);
        }

        private GenerationConfig? PreviousConfig(VersionRecord v)
        {
            var serving = s.Versions.Person(v.PersonId)?.ServingVersionId;
            return serving == null ? null : RJson.Read<GenerationConfig>(s.Versions.Version(serving)?.ConfigJson);
        }

        private async Task<IReadOnlyList<EvalItem>> EvaluateSetAsync(ReplicaSnapshot snapshot, IReadOnlyList<ReplicaPair> pairs, GenerationConfig config, string split,
            JobContext job, Action tick, int total, CancellationToken ct)
        {
            var existing = Existing(snapshot.VersionId, config.Key);
            var results = new List<EvalItem>();
            var model = await s.ModelAsync(ct).ConfigureAwait(false);
            // One bucket (quarter) at a time: its replies share the cached prefix.
            foreach (var bucket in pairs.GroupBy(p => QuarterStart(p.ReplyAtMs)).OrderBy(g => g.Key))
            {
                foreach (var pair in bucket.OrderBy(p => p.ReplyAtMs))
                {
                    ct.ThrowIfCancellationRequested();
                    if (existing.TryGetValue(pair.PairId, out var prior) && prior.Outcome is "ok" or "skipped") { results.Add(prior); tick(); continue; }
                    var item = await EvaluateAsync(snapshot, pair, config, split, bucket.Key - 1, model, job, ct).ConfigureAwait(false);
                    await SaveAsync(snapshot, pair, item, ct).ConfigureAwait(false);
                    results.Add(item);
                    tick();
                    await job.ReportProgressAsync(new { stage = Stages.Eval, split, config = config.Key, done = results.Count, of = pairs.Count, total }).ConfigureAwait(false);
                }
            }
            return results;
        }

        /// <summary>Regenerates one held-out reply as of <paramref name="asOfMs"/> and asks the discrimination judge.</summary>
        internal async Task<EvalItem> EvaluateAsync(ReplicaSnapshot snapshot, ReplicaPair pair, GenerationConfig config, string split, long asOfMs, ModelContextInfo model,
            JobContext? job, CancellationToken ct)
        {
            EvalItem Skip(string why) => new(pair.PairId, config.Key, split, pair.ContextKind, pair.ReplyAtMs, asOfMs, null, "skipped", null, null, null, null, null, null, null, why);
            int prior = snapshot.VisibleAsOf(asOfMs).Count();
            if (prior < MinPriorPairs) return Skip($"only {prior} earlier exchanges before {ReplicaComposer.Day(asOfMs)}");

            var ctx = await ContextAsync(snapshot, pair, asOfMs, ct).ConfigureAwait(false);
            if (ctx.Latest.Count == 0) return Skip("its stimulus is no longer readable");
            var request = new ComposeRequest
            {
                Snapshot = snapshot,
                CoreAsOfMs = asOfMs,
                NowMs = pair.ReplyAtMs,
                ContextKind = pair.ContextKind,
                AddresseePersonId = pair.PartnerPersonId,
                AddresseeName = ctx.AddresseeName,
                History = ctx.History,
                Latest = ctx.Latest,
                Config = config,
                Model = model,
                Facts = ctx.Facts,
                ZoneAt = s.ZoneFunc(snapshot.PersonId, snapshot.Person.IsSelf),
                Name = ctx.Name,
                ReservedOutputTokens = s.Settings.ReservedOutputTokens,
                CurrentVector = ctx.CurrentVector,
            };
            var prompt = s.Composer.Compose(request);
            var gen = await s.Generator!.GenerateAsync(new GenerateRequest
            {
                Prompt = prompt,
                Recompose = shrink => s.Composer.Compose(request with { Shrink = shrink }),
                Snapshot = snapshot,
                Config = config,
                Candidates = config.Candidates,
                LlmJudge = config.LlmJudge,
                Scope = $"eval|{snapshot.VersionId}|{pair.PairId}|{config.Key}",
                Interactive = false,
                JobId = job?.Job.JobId,
                Purpose = "replica.eval.compose",
            }, ct).ConfigureAwait(false);
            if (!gen.Ok)
                return new EvalItem(pair.PairId, config.Key, split, pair.ContextKind, pair.ReplyAtMs, asOfMs, null, "failed", null, null, null, null, null, null, gen.Model, gen.Problem);

            var real = pair.ReplyLines.Select(TextFeatures.ForPrompt).ToList();
            var generated = gen.Lines;
            bool realIsA = TextFeatures.Unit("discriminate|" + pair.PairId + "|" + config.Key) < 0.5;
            var (correct, confidence) = await DiscriminateAsync(prompt, real, generated, realIsA, snapshot, pair, config, job, ct).ConfigureAwait(false);
            double? similarity = await SimilarityAsync(string.Join("\n", real), string.Join("\n", generated), ct).ConfigureAwait(false);
            double style = StyleFingerprint.Distance(real, generated);
            double realLen = Math.Max(1, string.Join(" ", real).Length);
            double lengthError = Math.Abs(string.Join(" ", generated).Length - realLen) / realLen;
            double burstError = Math.Abs(generated.Count - real.Count);
            return new EvalItem(pair.PairId, config.Key, split, pair.ContextKind, pair.ReplyAtMs, asOfMs, string.Join("\n", generated), correct == null ? "unjudged" : "ok",
                correct, confidence, similarity, style, lengthError, burstError, gen.Model, null);
        }

        internal sealed record EvalContext(IReadOnlyList<ChatTurn> History, IReadOnlyList<string> Latest, string Name, string AddresseeName,
            IReadOnlyList<PersonaFacts.Fact> Facts, float[]? CurrentVector, IReadOnlyList<ConvMessage> Messages);

        /// <summary>The real moment before a held-out reply: what was said, by whom (names then), text as it stood then.</summary>
        internal async Task<EvalContext> ContextAsync(ReplicaSnapshot snapshot, ReplicaPair pair, long asOfMs, CancellationToken ct)
        {
            IReadOnlyList<ConvMessage> messages;
            string name, addressee = "someone";
            using (var corpus = s.Stores.Corpus.OpenRead())
            {
                var conv = new Conversations(corpus);
                messages = conv.ByIds(pair.ContextIds.Concat(pair.StimulusIds).Distinct(StringComparer.Ordinal).ToList(), pair.ReplyAtMs - 1);
                conv.Names.Load(snapshot.Person.IdentityIds);
                name = snapshot.Person.IdentityIds.Select(i => conv.Names.NameAt(i, asOfMs)).FirstOrDefault(n => n != null) ?? snapshot.Person.DisplayName;
                if (pair.PartnerPersonId != null)
                    addressee = messages.LastOrDefault(m => m.AuthorPersonId == pair.PartnerPersonId)?.Name ?? addressee;
            }
            bool group = pair.ContextKind != ContextKinds.Dm;
            var stimulusIds = pair.StimulusIds.ToHashSet(StringComparer.Ordinal);
            var history = Conversations.AsTurns(messages.Where(m => !stimulusIds.Contains(m.MessageId)), snapshot.PersonId, group);
            var latest = messages.Where(m => stimulusIds.Contains(m.MessageId) && !m.AuthorIsBot && m.AuthorPersonId != snapshot.PersonId)
                .Select(m => group ? m.Name + ": " + m.Text : m.Text).ToList();
            IReadOnlyList<PersonaFacts.Fact> facts;
            using (var intel = s.Stores.Intel.OpenRead()) facts = PersonaFacts.AsOf(intel, snapshot.Person.MemberIds, asOfMs);
            float[]? vector = null;
            if (s.Encoder() is { } enc && latest.Count > 0)
            {
                try { vector = VecMath.Normalized((await enc.EmbedAsync(new[] { string.Join("\n", latest) }, ct).ConfigureAwait(false))[0]); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { vector = null; }
            }
            return new EvalContext(history, latest, name, addressee, facts, vector, messages);
        }

        private async Task<(bool? Correct, double? Confidence)> DiscriminateAsync(ComposedPrompt prompt, IReadOnlyList<string> real, IReadOnlyList<string> generated, bool realIsA,
            ReplicaSnapshot snapshot, ReplicaPair pair, GenerationConfig config, JobContext? job, CancellationToken ct)
        {
            var a = realIsA ? real : generated;
            var b = realIsA ? generated : real;
            string task = "\n\n[Evaluation task, not a chat message: one of the two replies below is what " + prompt.Name + " really wrote at this moment, the other is an imitation. "
                + "Using " + prompt.Name + "'s real messages above as the reference, which one did " + prompt.Name + " really write?]\nReply A:\n" + string.Join("\n", a) + "\nReply B:\n" + string.Join("\n", b) + "\n";
            var request = new LlmRequest
            {
                Purpose = "replica.discriminate",
                SessionKey = ReplicaLlm.SessionKey(false, "compose", snapshot.VersionId + "|" + prompt.CoreKey),
                SystemPrompt = prompt.System,
                Turns = prompt.WithTail(task),
                MaxOutputTokens = 120,
                Sampling = new ModelSamplingParameters(Temperature: 0),
                WorkClass = AIRouterWorkClass.Background,
                OutputSchema = DiscriminateSchema,
            };
            var outcome = await s.Llm!.StructuredAsync<DiscriminateAnswer>("replica.discriminate", ReplicaPrompts.Discriminate, request,
                x => x.Real?.Trim().ToUpperInvariant() is "A" or "B" ? null : "real must be A or B", job?.Job.JobId, ct).ConfigureAwait(false);
            if (!outcome.Ok) return (null, null);
            bool pickedA = outcome.Value!.Real!.Trim().ToUpperInvariant() == "A";
            return (pickedA == realIsA, outcome.Value.Confidence is double c ? Math.Clamp(c, 0, 1) : null);
        }

        private async Task<double?> SimilarityAsync(string a, string b, CancellationToken ct)
        {
            if (s.Encoder() is not { } enc) return null;
            try
            {
                var v = await enc.EmbedAsync(new[] { StyleLine.Prose(a), StyleLine.Prose(b) }, ct).ConfigureAwait(false);
                return VecMath.Dot(VecMath.Normalized(v[0]), VecMath.Normalized(v[1]));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        }

        private Dictionary<string, EvalItem> Existing(string versionId, string configKey)
        {
            using var c = s.Stores.Intel.OpenRead();
            return c.Query(null, """
                SELECT pair_id, config_key, split, context_kind, occurred_ms, as_of_ms, generated_text, outcome, judge_correct, judge_confidence, embedding_sim, style_distance,
                       length_error, burst_error, model, detail_json
                FROM replica_eval_items WHERE version_id=$v AND config_key=$k
                """, r => new EvalItem(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetStringOrNull(6), r.GetString(7),
                    r.IsDBNull(8) ? null : r.GetInt32(8) == 1, r.GetDoubleOrNull(9), r.GetDoubleOrNull(10), r.GetDoubleOrNull(11), r.GetDoubleOrNull(12), r.GetDoubleOrNull(13),
                    r.GetStringOrNull(14), r.GetStringOrNull(15)), ("$v", versionId), ("$k", configKey))
                .ToDictionary(i => i.PairId, StringComparer.Ordinal);
        }

        private Task SaveAsync(ReplicaSnapshot snapshot, ReplicaPair pair, EvalItem i, CancellationToken ct) =>
            s.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT OR REPLACE INTO replica_eval_items(version_id, pair_id, config_key, split, person_id, context_kind, occurred_ms, as_of_ms, generated_text, outcome,
                    judge_correct, judge_confidence, embedding_sim, style_distance, length_error, burst_error, model, prompt_version, created_at_ms, detail_json)
                VALUES($v,$p,$k,$s,$person,$ctx,$o,$a,$g,$out,$jc,$jconf,$es,$sd,$le,$be,$m,$pv,$t,$d)
                """, ("$v", snapshot.VersionId), ("$p", pair.PairId), ("$k", i.ConfigKey), ("$s", i.Split), ("$person", snapshot.PersonId), ("$ctx", i.ContextKind),
                ("$o", i.OccurredMs), ("$a", i.AsOfMs), ("$g", i.Generated), ("$out", i.Outcome), ("$jc", i.JudgeCorrect is bool b ? (b ? 1 : 0) : null), ("$jconf", i.JudgeConfidence),
                ("$es", i.EmbeddingSimilarity), ("$sd", i.StyleDistance), ("$le", i.LengthError), ("$be", i.BurstError), ("$m", i.Model), ("$pv", ReplicaPrompts.Compose),
                ("$t", s.Clock.NowMs), ("$d", i.Detail)), ct);

        // ── aggregation ──────────────────────────────────────────────────────────

        public static long QuarterStart(long ms)
        {
            var d = Instant.ToUtcDateTime(ms);
            int month = (d.Month - 1) / 3 * 3 + 1;
            return Instant.FromUtc(new DateTime(d.Year, month, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        private static double? Accuracy(IReadOnlyList<EvalItem> items)
        {
            var judged = items.Where(i => i.JudgeCorrect != null).ToList();
            return judged.Count == 0 ? null : judged.Average(i => i.JudgeCorrect == true ? 1.0 : 0.0);
        }

        private static double? Mean(IReadOnlyList<EvalItem> items, Func<EvalItem, double?> pick)
        {
            var values = items.Select(pick).Where(x => x != null).Select(x => x!.Value).ToList();
            return values.Count == 0 ? null : values.Average();
        }

        /// <summary>Wilson score interval for a proportion (95%).</summary>
        public static (double Low, double High) Wilson(int successes, int n, double z = 1.96)
        {
            if (n == 0) return (0, 1);
            double p = (double)successes / n, z2 = z * z;
            double centre = (p + z2 / (2 * n)) / (1 + z2 / n);
            double half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / (1 + z2 / n);
            return (Math.Max(0, centre - half), Math.Min(1, centre + half));
        }

        public static ReplicaMetricsDto Aggregate(IReadOnlyList<EvalItem> items, GenerationConfig config, IReadOnlyList<ReplicaTuneResultDto> tuning, long nowMs)
        {
            var judged = items.Where(i => i.JudgeCorrect != null).ToList();
            int correct = judged.Count(i => i.JudgeCorrect == true);
            var (lo, hi) = Wilson(correct, judged.Count);
            var byContext = judged.GroupBy(i => i.ContextKind).Where(g => g.Count() >= 3)
                .ToDictionary(g => g.Key, g => g.Average(i => i.JudgeCorrect == true ? 1.0 : 0.0), StringComparer.Ordinal);
            string model = items.Select(i => i.Model).FirstOrDefault(m => m != null) ?? "unknown";
            int skipped = items.Count(i => i.Outcome == "skipped");
            string note = judged.Count == 0
                ? "No held-out reply could be judged."
                : string.Create(CultureInfo.InvariantCulture,
                    $"{judged.Count} held-out real replies the replica never saw, each regenerated as of the start of its quarter and judged by {model}: it picked the real reply {(double)correct / judged.Count:P0} of the time (50% = indistinguishable).")
                  + (skipped > 0 ? $" {skipped} skipped (too little earlier data)." : "");
            return new ReplicaMetricsDto(judged.Count, items.Count(i => i.Outcome is "failed"), judged.Count == 0 ? null : (double)correct / judged.Count,
                judged.Count == 0 ? null : lo, judged.Count == 0 ? null : hi, Mean(items, i => i.EmbeddingSimilarity), Mean(items, i => i.StyleDistance),
                Mean(items, i => i.LengthError), Mean(items, i => i.BurstError), config.Key, tuning, byContext, model, nowMs, note);
        }
    }
}
