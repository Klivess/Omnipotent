using System.Collections.Concurrent;
using System.Text;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Ml;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>Per-signal judge scores of one candidate (each in [0,1]; null when the signal is unavailable).</summary>
    public sealed record CandidateScore(double Typicality, double? StyleEmbedding, double? Semantic, double? Assistant, double? Llm, double Total);

    public sealed record CandidateResult(string Text, IReadOnlyList<string> Lines, int? Burst, CandidateScore? Score, string? Error);

    public sealed record GenerationResult
    {
        public required bool Ok { get; init; }
        public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
        public IReadOnlyList<CandidateResult> Candidates { get; init; } = Array.Empty<CandidateResult>();
        public int Chosen { get; init; } = -1;
        public IReadOnlyList<string> Transforms { get; init; } = Array.Empty<string>();
        public string? Model { get; init; }
        public int PromptTokens { get; init; }
        public int CachedTokens { get; init; }
        public int CompletionTokens { get; init; }
        public int Calls { get; init; }
        public long LatencyMs { get; init; }
        public string? Problem { get; init; }
        public IReadOnlyList<string> Signals { get; init; } = Array.Empty<string>();
    }

    public sealed record GenerateRequest
    {
        public required ComposedPrompt Prompt { get; init; }
        /// <summary>Rebuilds the prompt at a smaller budget after a context overflow or 413 (plan F-LLM).</summary>
        public required Func<double, ComposedPrompt> Recompose { get; init; }
        public required ReplicaSnapshot Snapshot { get; init; }
        public required GenerationConfig Config { get; init; }
        public required int Candidates { get; init; }
        public bool LlmJudge { get; init; } = true;
        /// <summary>Seed scope: the same scope and inputs sample the same burst sizes and seeds (reproducible runs).</summary>
        public required string Scope { get; init; }
        public required bool Interactive { get; init; }
        public long? JobId { get; init; }
        public Func<string, int, int, Task>? Progress { get; init; }
        public string Purpose { get; init; } = "replica.compose";
    }

    /// <summary>
    /// Generates N candidates on one cached prefix (R1: "sequential on the cached prefix" — the gateway has no n-sampling),
    /// each in a burst size sampled from the person's habits, then judges them (plan "Judge"): style typicality under the
    /// as-of fingerprint, style-embedding and semantic plausibility against the person's real replies to the nearest
    /// stimuli (shared encoder), the assistant-voice house model against the person's own baseline, and an LLM judge that
    /// ranks the candidates on the same cached prefix with real examples in view (shuffled against position bias). The
    /// best wins; deterministic transforms apply only where a habit is near-universal (≥90%), never by rate (F317–F319).
    /// </summary>
    public sealed class ReplicaGenerator
    {
        private sealed record JudgeAnswer(List<string>? Ranking);

        private static readonly LlmOutputSchema JudgeSchema = ReplicaLlm.Schema("candidate_ranking", "Option letters from most to least likely to be the real person.", """
            {"type":"object","required":["ranking"],"properties":{"ranking":{"type":"array","items":{"type":"string"},"description":"option letters, best first"}}}
            """);

        private readonly ReplicaLlm llm;
        private readonly Func<IEmbeddingEncoder?> encoder;
        private readonly Func<AssistantVoiceModel?> assistantVoice;
        private readonly ConcurrentDictionary<string, (double Mean, double Std)> baselines = new(StringComparer.Ordinal);

        public ReplicaGenerator(ReplicaLlm llm, Func<IEmbeddingEncoder?> encoder, Func<AssistantVoiceModel?> assistantVoice)
        {
            this.llm = llm;
            this.encoder = encoder;
            this.assistantVoice = assistantVoice;
        }

        public async Task<GenerationResult> GenerateAsync(GenerateRequest g, CancellationToken ct)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var prompt = g.Prompt;
            var cls = g.Interactive ? AIRouterWorkClass.Interactive : AIRouterWorkClass.Background;
            string sessionKey = ReplicaLlm.SessionKey(g.Interactive, "compose", g.Snapshot.VersionId + "|" + prompt.CoreKey);
            int promptTokens = 0, cached = 0, completion = 0, calls = 0;
            string? model = null, lastProblem = null;
            var results = new List<CandidateResult>();
            var rng = new Random(TextFeatures.Seed(g.Scope));

            for (int i = 0; i < g.Candidates; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (g.Progress != null) await g.Progress("generating", i + 1, g.Candidates).ConfigureAwait(false);
                int? burst = g.Config.BurstSampling ? SampleBurst(prompt.Style, rng) : null;
                string tail = "\n" + ReplicaComposer.BurstInstruction(prompt.Name, burst);
                var current = prompt;
                LlmRequest Build(ComposedPrompt p, int maxOutput) => new()
                {
                    Purpose = g.Purpose,
                    SessionKey = sessionKey,
                    SystemPrompt = p.System,
                    Turns = p.WithTail(tail),
                    MaxOutputTokens = maxOutput,
                    Sampling = new ModelSamplingParameters(Temperature: g.Config.Temperature, TopP: g.Config.TopP, Seed: TextFeatures.Seed(g.Scope + "|" + i)),
                    WorkClass = cls,
                };
                var result = await llm.TextAsync(g.Purpose, ReplicaPrompts.Compose, Build(current, current.MaxOutputTokens), g.JobId, ct,
                    shrink => Build(current = g.Recompose(shrink), current.MaxOutputTokens)).ConfigureAwait(false);
                calls++;
                if (result.ErrorKind == LlmErrorKind.Truncated)
                {
                    // finish_reason=length: once more with twice the room; a still-truncated answer is dropped, not shown (F301).
                    result = await llm.TextAsync(g.Purpose, ReplicaPrompts.Compose, Build(current, Math.Min(4_000, current.MaxOutputTokens * 2)), g.JobId, ct).ConfigureAwait(false);
                    calls++;
                }
                promptTokens += result.PromptTokens;
                cached += result.CachedTokens;
                completion += result.CompletionTokens;
                model ??= result.Model;
                if (!result.Success)
                {
                    lastProblem = $"{result.ErrorKind}: {result.ErrorMessage}";
                    results.Add(new CandidateResult("", Array.Empty<string>(), burst, null, lastProblem));
                    continue;
                }
                var lines = Clean(result.Text ?? "", prompt.Name, prompt.Style);
                results.Add(new CandidateResult(string.Join("\n", lines), lines, burst, null, lines.Count == 0 ? "empty after cleaning" : null));
            }

            var usable = results.Select((r, i) => (r, i)).Where(x => x.r.Lines.Count > 0).ToList();
            if (usable.Count == 0)
                return new GenerationResult { Ok = false, Candidates = results, Problem = lastProblem ?? "every candidate was empty", Model = model, PromptTokens = promptTokens, CachedTokens = cached, CompletionTokens = completion, Calls = calls, LatencyMs = started.ElapsedMilliseconds };

            if (g.Progress != null) await g.Progress("judging", usable.Count, g.Candidates).ConfigureAwait(false);
            var signals = new List<string>();
            IReadOnlyList<double>? llmScores = null;
            if (g.LlmJudge && usable.Count > 1)
            {
                var (scores, judgeResult) = await LlmRankAsync(prompt, usable.Select(x => x.r).ToList(), sessionKey, cls, g, ct).ConfigureAwait(false);
                calls++;
                if (judgeResult != null)
                {
                    promptTokens += judgeResult.PromptTokens;
                    cached += judgeResult.CachedTokens;
                    completion += judgeResult.CompletionTokens;
                }
                llmScores = scores;
                if (scores != null) signals.Add("llm-judge");
            }
            var vectors = await EmbedAsync(usable.Select(x => x.r.Text).ToList(), ct).ConfigureAwait(false);
            if (vectors != null) signals.Add("embeddings");
            var av = assistantVoice();
            (double Mean, double Std)? baseline = av == null ? null : Baseline(av, g.Snapshot);
            if (av != null) signals.Add("assistant-voice");
            var scored = Judge.Score(usable.Select(x => x.r.Lines).ToList(), prompt.Style, vectors, prompt.Retrieved, g.Snapshot.ReplyVectors, av, baseline, llmScores);
            for (int k = 0; k < usable.Count; k++) results[usable[k].i] = results[usable[k].i] with { Score = scored[k] };
            int best = usable.Select((x, k) => (x.i, scored[k].Total)).OrderByDescending(x => x.Total).ThenBy(x => x.i).First().i;
            var (final, transforms) = Transforms(results[best].Lines, prompt.Style);
            return new GenerationResult
            {
                Ok = true,
                Lines = final,
                Candidates = results,
                Chosen = best,
                Transforms = transforms,
                Model = model,
                PromptTokens = promptTokens,
                CachedTokens = cached,
                CompletionTokens = completion,
                Calls = calls,
                LatencyMs = started.ElapsedMilliseconds,
                Problem = results.Any(r => r.Error != null) ? lastProblem : null,
                Signals = signals,
            };
        }

        /// <summary>Samples how many messages to write from the person's burst-size distribution.</summary>
        internal static int SampleBurst(StyleFingerprint style, Random rng)
        {
            double total = style.BurstShares.Sum();
            if (total <= 0) return 1;
            double x = rng.NextDouble() * total;
            for (int k = 0; k < style.BurstShares.Count; k++)
            {
                x -= style.BurstShares[k];
                if (x <= 0) return k + 1;
            }
            return 1;
        }

        /// <summary>
        /// Structural cleanup of a raw continuation: one message per line, a copied "Name:" prefix removed, our own control
        /// markers ([Now: …], [Reference …], section rulers) dropped, a whole-message quote unwrapped, at most a burst's worth.
        /// </summary>
        public static List<string> Clean(string raw, string name, StyleFingerprint style)
        {
            var lines = new List<string>();
            foreach (var l in raw.Replace("\r", "").Split('\n'))
            {
                string line = l.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("[Now:", StringComparison.Ordinal) || line.StartsWith("[Reference", StringComparison.Ordinal) || line.StartsWith("===", StringComparison.Ordinal)
                    || line.StartsWith("— ", StringComparison.Ordinal) || line.StartsWith("(Write ", StringComparison.Ordinal) || line.StartsWith("[Judge", StringComparison.Ordinal)) continue;
                foreach (var prefix in new[] { name + ":", name + " :", ReplicaIndexer.SelfToken + ":" })
                    if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) line = line[prefix.Length..].Trim();
                if (line.Length >= 2 && ((line[0] == '"' && line[^1] == '"') || (line[0] == '“' && line[^1] == '”'))) line = line[1..^1].Trim();
                if (line.Length > 0) lines.Add(line);
            }
            int max = Math.Max(4, style.BurstShares.Count(s => s > 0) + 2);
            return lines.Take(max).ToList();
        }

        /// <summary>Deterministic transforms, only where the habit is near-universal; "OK" becomes "ok", never "oK" (F317).</summary>
        public static (IReadOnlyList<string> Lines, IReadOnlyList<string> Applied) Transforms(IReadOnlyList<string> lines, StyleFingerprint style)
        {
            var applied = new List<string>();
            var result = lines.ToList();
            if (style.AlwaysLowercase && result.Any(l => l.Any(char.IsUpper)))
            {
                result = result.Select(LowercaseOutsideMarkup).ToList();
                applied.Add("lowercase (≥90% of their messages are all lowercase)");
            }
            if (style.NeverEndsWithPeriod && result.Any(EndsWithLonePeriod))
            {
                result = result.Select(l => EndsWithLonePeriod(l) ? l[..^1].TrimEnd() : l).ToList();
                applied.Add("no final period (≥90% of their messages end without one)");
            }
            return (result, applied);
        }

        private static bool EndsWithLonePeriod(string line) => line.EndsWith('.') && !line.EndsWith("..", StringComparison.Ordinal) && line.Length > 1;

        private static string LowercaseOutsideMarkup(string line)
        {
            var sb = new StringBuilder(line.Length);
            int depthAngle = 0, depthSquare = 0;
            foreach (char c in line)
            {
                if (c == '<') depthAngle++;
                else if (c == '>' && depthAngle > 0) depthAngle--;
                else if (c == '[') depthSquare++;
                else if (c == ']' && depthSquare > 0) depthSquare--;
                sb.Append(depthAngle > 0 || depthSquare > 0 ? c : char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private async Task<(IReadOnlyList<double>? Scores, LlmResult? Result)> LlmRankAsync(ComposedPrompt prompt, IReadOnlyList<CandidateResult> candidates, string sessionKey,
            AIRouterWorkClass cls, GenerateRequest g, CancellationToken ct)
        {
            // Deterministic shuffle against position bias.
            var order = Enumerable.Range(0, candidates.Count).OrderBy(i => TextFeatures.Unit(g.Scope + "|judge|" + i)).ToList();
            var letters = order.Select((_, k) => ((char)('A' + k)).ToString()).ToList();
            var sb = new StringBuilder("\n\n[Judge task, not a chat message: here are ").Append(candidates.Count).Append(" possible next turns. Using ")
                .Append(prompt.Name).Append("'s real messages above as the reference, rank them from most to least likely to have been written by ")
                .Append(prompt.Name).Append(" at this moment: content, length, tone, habits, language, and whether ").Append(prompt.Name).Append(" would know this.]\n");
            for (int k = 0; k < order.Count; k++)
                sb.Append("Option ").Append(letters[k]).Append(":\n").Append(string.Join("\n", candidates[order[k]].Lines)).Append('\n');
            var request = new LlmRequest
            {
                Purpose = "replica.judge",
                SessionKey = sessionKey,
                SystemPrompt = prompt.System,
                Turns = prompt.WithTail(sb.ToString()),
                MaxOutputTokens = 200,
                Sampling = new ModelSamplingParameters(Temperature: 0),
                WorkClass = cls,
                OutputSchema = JudgeSchema,
            };
            var outcome = await llm.StructuredAsync<JudgeAnswer>("replica.judge", ReplicaPrompts.Judge, request,
                a => a.Ranking is { Count: > 0 } r && r.Any(x => letters.Contains(x.Trim().ToUpperInvariant())) ? null : "the ranking names none of the options", g.JobId, ct).ConfigureAwait(false);
            if (!outcome.Ok) return (null, outcome.Result);
            var ranking = outcome.Value!.Ranking!.Select(x => x.Trim().ToUpperInvariant()).Where(letters.Contains).Distinct().ToList();
            var scores = new double[candidates.Count];
            int n = Math.Max(1, candidates.Count - 1);
            for (int k = 0; k < order.Count; k++)
            {
                int rank = ranking.IndexOf(letters[k]);
                scores[order[k]] = rank < 0 ? 0 : 1 - (double)rank / n;
            }
            return (scores, outcome.Result);
        }

        private async Task<IReadOnlyList<float[]>?> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            var enc = encoder();
            if (enc == null || texts.Count == 0) return null;
            try { return (await enc.EmbedAsync(texts, ct).ConfigureAwait(false)).Select(VecMath.Normalized).ToList(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The encoder is optional at judging time: deterministic signals still decide. Recorded as a missing signal.
                return null;
            }
        }

        /// <summary>P(assistant voice) of the person's own real replies: the judge penalises only what exceeds their baseline.</summary>
        private (double Mean, double Std) Baseline(AssistantVoiceModel model, ReplicaSnapshot s) =>
            baselines.GetOrAdd(s.VersionId + "|" + model.Version, _ =>
            {
                var sample = s.Visible.Where(p => (p.Flags & PairFlags.MediaOnly) == 0).OrderBy(p => TextFeatures.Unit("avb|" + p.PairId)).Take(300).Select(p => p.ReplyText).ToList();
                if (sample.Count < 10) return (0.2, 0.15);
                var probs = sample.Select(model.Probability).ToList();
                double mean = probs.Average();
                return (mean, Math.Sqrt(probs.Average(p => (p - mean) * (p - mean))));
            });
    }

    /// <summary>The judge's scoring rules (deterministic given its inputs).</summary>
    public static class Judge
    {
        public const double WTypicality = 0.30, WStyleEmbedding = 0.10, WSemantic = 0.15, WAssistant = 0.15, WLlm = 0.30;

        public static IReadOnlyList<CandidateScore> Score(IReadOnlyList<IReadOnlyList<string>> candidates, StyleFingerprint style, IReadOnlyList<float[]>? vectors,
            IReadOnlyList<ReplicaPair> retrieved, IReadOnlyDictionary<string, float[]> replyVectors, AssistantVoiceModel? assistant, (double Mean, double Std)? baseline,
            IReadOnlyList<double>? llm)
        {
            var neighbourReplies = retrieved.Take(8).Select(p => replyVectors.GetValueOrDefault(p.PairId)).OfType<float[]>().ToList();
            var neighbourTokens = retrieved.Take(8).Select(p => TextFeatures.Tokens(p.ReplyText).ToHashSet(StringComparer.Ordinal)).ToList();
            var semanticRaw = new double?[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
            {
                if (vectors != null && neighbourReplies.Count > 0) semanticRaw[i] = neighbourReplies.Max(v => (double)VecMath.Dot(vectors[i], v));
                else if (neighbourTokens.Count > 0)
                {
                    var tokens = TextFeatures.Tokens(string.Join(" ", candidates[i])).ToHashSet(StringComparer.Ordinal);
                    semanticRaw[i] = neighbourTokens.Max(t => t.Count == 0 || tokens.Count == 0 ? 0 : (double)t.Count(tokens.Contains) / (t.Count + tokens.Count - t.Count(tokens.Contains)));
                }
            }
            double semMax = semanticRaw.Where(x => x != null).Select(x => x!.Value).DefaultIfEmpty(0).Max();
            double semMin = semanticRaw.Where(x => x != null).Select(x => x!.Value).DefaultIfEmpty(0).Min();

            var result = new List<CandidateScore>();
            for (int i = 0; i < candidates.Count; i++)
            {
                double typ = style.Typicality(candidates[i]);
                double? emb = null;
                if (vectors != null && style.Centroid != null && style.CentroidCosMean is double mu && style.CentroidCosStd is double sd)
                {
                    double z = (VecMath.Dot(vectors[i], style.Centroid) - mu) / Math.Max(0.02, sd);
                    emb = 1 / (1 + Math.Exp(-1.7 * z));
                }
                double? sem = semanticRaw[i] is double raw ? (semMax - semMin < 1e-9 ? 0.5 : (raw - semMin) / (semMax - semMin)) : null;
                double? asst = null;
                if (assistant != null && baseline is { } b)
                {
                    double p = assistant.Probability(string.Join("\n", candidates[i]));
                    asst = 1 - Math.Clamp((p - b.Mean) / (3 * b.Std + 0.05), 0, 1);
                }
                double? l = llm?[i];
                double total = 0, weights = 0;
                void Add(double? v, double w) { if (v is double x) { total += w * x; weights += w; } }
                Add(typ, WTypicality);
                Add(emb, WStyleEmbedding);
                Add(sem, WSemantic);
                Add(asst, WAssistant);
                Add(l, WLlm);
                result.Add(new CandidateScore(typ, emb, sem, asst, l, weights == 0 ? 0 : total / weights));
            }
            return result;
        }
    }
}
