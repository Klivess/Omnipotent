using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Llm
{
    /// <summary>One audited LLM call. <see cref="Repack"/> rebuilds the request at a smaller budget
    /// (fraction of the previous one) after an overflow or a 413.</summary>
    public sealed record LlmCall
    {
        public required string ModuleId { get; init; }
        public required string Purpose { get; init; }
        public required LlmRequest Request { get; init; }
        public string? PromptVersion { get; init; }
        public long? JobId { get; init; }
        public Func<double, LlmRequest>? Repack { get; init; }
        /// <summary>The size of the request before this one on the same provider-side prefix, when this one sends that
        /// prefix again (a later question of a <see cref="CacheShapedSession"/>). Zero for a call that sends nothing the
        /// provider has seen from it before. It is what the call's cached tokens are measured against.</summary>
        public int PriorPromptTokens { get; init; }
    }

    /// <summary>
    /// The Omniscience side of the LLM gateway: audit trail (intel.db <c>llm_calls</c>), recovery
    /// (overflow → learn + repack + retry once; 413 → repack smaller + retry once; 429 → transient retry via
    /// the job queue; 401/403 → permanent, plus a circuit breaker that pauses background LLM work), token
    /// estimator calibration, cache-shaped sessions, and Omniscience's own prefix-cache figures (the hit rate, and
    /// how much of what it re-sends is served from cache).
    /// </summary>
    public sealed class OmniLlm
    {
        private readonly ILlmClient client;
        private readonly SqliteStore intel;
        private readonly IClock clock;
        private long circuitOpenUntilMs;

        public OmniLlm(ILlmClient client, SqliteStore intel, IClock clock, TokenEstimator estimator)
        {
            this.client = client;
            this.intel = intel;
            this.clock = clock;
            Estimator = estimator;
        }

        public TokenEstimator Estimator { get; }

        public bool CircuitOpen => clock.NowMs < Interlocked.Read(ref circuitOpenUntilMs);

        public Task<ModelContextInfo> ContextAsync(string? model, CancellationToken ct) => client.GetContextAsync(model, ct);

        /// <summary>An answer this short is a label or a line: the call is sent with thinking off unless it asks otherwise.</summary>
        public const int ShortAnswerTokens = 2_000;

        /// <summary>The effort a longer call gets when it names none ("off", "low", "medium", "high").</summary>
        public string DefaultReasoningEffort { get; set; } = "low";

        /// <summary>
        /// Says how hard the model should think, and leaves room for it. The model's thinking is billed against the same
        /// output ceiling as its answer, and left unsaid the router's default was "medium": a call whose ceiling was sized
        /// for the answer alone (a few hundred tokens for a label, 12,000 for a slice of claims) spent it thinking and came
        /// back cut off with no answer at all. Every call now carries an effort, and a ceiling with that effort's
        /// headroom on top of what the caller asked for the answer.
        /// </summary>
        internal LlmRequest Shape(LlmRequest request)
        {
            string effort = (request.ReasoningEffort ?? (request.MaxOutputTokens is int cap && cap <= ShortAnswerTokens ? "off" : DefaultReasoningEffort))
                .Trim().ToLowerInvariant();
            int headroom = effort switch { "off" => 256, "low" => 1_500, "medium" => 4_000, _ => 8_000 };
            return request with
            {
                ReasoningEffort = effort,
                MaxOutputTokens = request.MaxOutputTokens is int max ? max + headroom : null,
                SystemPrompt = request.SystemPrompt == null ? null : Ml.UnicodeText.WellFormed(request.SystemPrompt),
                Turns = WellFormed(request.Turns),
            };
        }

        /// <summary>The turns with any half surrogate pair replaced (chat text is not always well-formed, and a request body
        /// that is not valid Unicode is refused or mangled on the way). The same list when nothing needed replacing.</summary>
        private static IReadOnlyList<LlmTurn> WellFormed(IReadOnlyList<LlmTurn> turns)
        {
            List<LlmTurn>? changed = null;
            for (int i = 0; i < turns.Count; i++)
            {
                string clean = Ml.UnicodeText.WellFormed(turns[i].Content);
                if (ReferenceEquals(clean, turns[i].Content)) continue;
                changed ??= new List<LlmTurn>(turns);
                changed[i] = turns[i] with { Content = clean };
            }
            return changed ?? turns;
        }

        public async Task<LlmResult> CallAsync(LlmCall call, CancellationToken ct)
        {
            var request = Shape(call.Request);
            LlmResult result = await client.QueryAsync(request, ct).ConfigureAwait(false);
            await LogAsync(call, request, result, ct).ConfigureAwait(false);

            if (!result.Success && call.Repack != null && result.ErrorKind is LlmErrorKind.ContextOverflow or LlmErrorKind.RequestTooLarge)
            {
                double shrink = result.ErrorKind == LlmErrorKind.ContextOverflow ? 0.85 : 0.7;
                request = Shape(call.Repack(shrink));
                result = await client.QueryAsync(request, ct).ConfigureAwait(false);
                await LogAsync(call, request, result, ct).ConfigureAwait(false);
            }

            if (result.Success)
            {
                if (HoldsItsWholePrompt(request)) Estimator.Calibrate(PromptText(request), result.PromptTokens);
                return result;
            }

            switch (result.ErrorKind)
            {
                case LlmErrorKind.Unauthorized:
                    Interlocked.Exchange(ref circuitOpenUntilMs, clock.NowMs + (long)TimeSpan.FromMinutes(15).TotalMilliseconds);
                    throw new JobFailureException(ErrorClass.Permanent, "LLM provider refused the credentials (401/403); background LLM work paused for 15 minutes.");
                case LlmErrorKind.RateLimited:
                case LlmErrorKind.Other:
                    throw new JobFailureException(ErrorClass.Transient, $"LLM call failed ({result.ErrorKind}): {result.ErrorMessage}");
                case LlmErrorKind.Cancelled:
                    throw new OperationCanceledException(ct);
                default:
                    // Overflow/too-large after repack, refusals, missing structured output, truncation:
                    // returned to the caller, which records the outcome honestly (e.g. "refused", never "no facts").
                    return result;
            }
        }

        /// <summary>Opens a "prefill once, ask many" session over a large stable prefix.</summary>
        /// <param name="reasoningEffort">How hard every question of the session is thought about ("off", "low", "medium",
        /// "high"); null leaves it to <see cref="Shape"/>.</param>
        /// <param name="schemas">Every schema the session's questions answer with, in one fixed order: all of them are sent
        /// with each question, so the tool definitions ahead of the prefix never change between two questions
        /// (<see cref="LlmRequest.SchemaFamily"/>). Null for a session whose questions all use one schema.</param>
        public CacheShapedSession OpenSession(string moduleId, string purpose, string sessionKey, string systemPrompt, string stablePrefix,
            AIRouterWorkClass workClass, string? promptVersion = null, long? jobId = null, string? reasoningEffort = null,
            IReadOnlyList<LlmOutputSchema>? schemas = null) =>
            new(this, client, moduleId, purpose, sessionKey, systemPrompt, stablePrefix, workClass, promptVersion, jobId) { ReasoningEffort = reasoningEffort, Schemas = schemas };

        private static bool HasImages(LlmRequest request) => request.Turns.Any(t => t.Images is { Count: > 0 });

        /// <summary>
        /// Whether the provider's token count for a request is a count of the text in that request, which is what the
        /// estimator may learn from. Not with images (their tokens are not text), and not for a follow-up on a kept
        /// session: its request holds the last question while the provider counted the whole conversation. From 19 to
        /// 20 Nov 2026 every critic call was such a follow-up: a question of a thousand tokens came back counted as
        /// twelve thousand, the estimator took its text to be 2.5 times denser than it is (it rises fast and falls
        /// slowly), and extraction slices planned at 5,000 tokens held about 2,000.
        /// </summary>
        internal static bool HoldsItsWholePrompt(LlmRequest request) => !HasImages(request) && !(request.KeepSession && !request.Rewind);

        /// <summary>The text of everything a request sends: its turns, its system prompt and its tool definitions.</summary>
        internal static string PromptText(LlmRequest request) =>
            string.Concat(request.Turns.Select(t => t.Content)) + request.SystemPrompt
            + string.Concat(KliveLLM.KliveLLM.GatewaySchemas(request).Select(s => s.Name + s.Description + s.Schema.ToString(Newtonsoft.Json.Formatting.None)));

        /// <summary>
        /// Omniscience's own prefix-cache hit rate over a window (cached / prompt tokens). Calls that carried images are
        /// left out: each sends a different picture, which no prefix cache can serve. This is the figure a dashboard
        /// shows. It cannot reach 100%: whatever is read for the first time has to be prefilled once, so it says how much
        /// of the traffic is new evidence as much as how well the prompts are built. What says the latter is
        /// <see cref="ResentEfficiency"/>.
        /// </summary>
        public (double Efficiency, int Samples) CacheEfficiency(TimeSpan window, string? workClass = null)
        {
            using var c = intel.OpenRead();
            var row = c.Query(null, """
                SELECT COALESCE(SUM(cached_tokens),0), COALESCE(SUM(prompt_tokens),0), COUNT(*) FROM llm_calls
                WHERE at_ms >= $since AND outcome='ok' AND ($w IS NULL OR work_class=$w) AND images = 0
                """, r => (Cached: r.GetInt64(0), Prompt: r.GetInt64(1), N: r.GetInt32(2)),
                ("$since", clock.NowMs - (long)window.TotalMilliseconds), ("$w", workClass)).Single();
            return (row.Prompt == 0 ? 1.0 : (double)row.Cached / row.Prompt, row.N);
        }

        /// <summary>
        /// Of the tokens Omniscience sent a second time (a later question over a prefix the provider had already been
        /// sent), the share the provider served from its cache. This is the part of the hit rate that prompt assembly
        /// decides, and the one that should read 100%: a low figure means a later question no longer begins with the
        /// bytes of the one before it (a tool list that changed, text moved ahead of the evidence), or reached the
        /// provider after it had dropped the prefix. Calls that re-sent nothing are not samples.
        /// </summary>
        public (double Efficiency, int Samples) ResentEfficiency(TimeSpan window, string? workClass = null)
        {
            using var c = intel.OpenRead();
            var row = c.Query(null, """
                SELECT COALESCE(SUM(MIN(cached_tokens, resent_tokens)),0), COALESCE(SUM(resent_tokens),0), COUNT(*) FROM llm_calls
                WHERE at_ms >= $since AND outcome='ok' AND ($w IS NULL OR work_class=$w) AND resent_tokens > 0
                """, r => (Served: r.GetInt64(0), Resent: r.GetInt64(1), N: r.GetInt32(2)),
                ("$since", clock.NowMs - (long)window.TotalMilliseconds), ("$w", workClass)).Single();
            return (row.Resent == 0 ? 1.0 : (double)row.Served / row.Resent, row.N);
        }

        private Task LogAsync(LlmCall call, LlmRequest request, LlmResult r, CancellationToken ct) =>
            intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO llm_calls(at_ms, module_id, purpose, job_id, model, provider, work_class, prompt_tokens, cached_tokens,
                                      completion_tokens, request_bytes, latency_ms, outcome, finish_reason, prompt_version, error, images, resent_tokens)
                VALUES($at,$m,$p,$j,$model,$prov,$wc,$pt,$ct,$cpt,$b,$lat,$o,$fr,$pv,$err,$img,$rs)
                """,
                ("$at", clock.NowMs), ("$m", call.ModuleId), ("$p", call.Purpose), ("$j", call.JobId), ("$model", r.Model), ("$prov", r.Provider),
                ("$wc", request.WorkClass.ToString()), ("$pt", r.PromptTokens), ("$ct", r.CachedTokens), ("$cpt", r.CompletionTokens),
                ("$b", r.RequestBytes), ("$lat", r.LatencyMs), ("$o", r.Success ? "ok" : r.ErrorKind.ToString()), ("$fr", r.FinishReason),
                ("$pv", call.PromptVersion), ("$err", r.ErrorMessage is { Length: > 2000 } e ? e[..2000] : r.ErrorMessage),
                ("$img", request.Turns.Sum(t => t.Images?.Count ?? 0)),
                // What the request before it held is, at most, what this one could have had back from the cache.
                ("$rs", Math.Max(0, Math.Min(call.PriorPromptTokens, r.PromptTokens)))), ct);
    }

    /// <summary>
    /// "Prefill once, ask many" (plan "CacheShapedSession"): several questions over one large stable prefix (system +
    /// evidence), built so that each of them begins with exactly the bytes the provider was sent before.
    ///
    /// A question is one of two kinds. <see cref="AskAsync"/> is a question of its own: it travels as the prefix plus
    /// the question and nothing else, so everything up to the question comes out of the provider's prefix cache and
    /// none of the earlier answers is sent back. <see cref="FollowUpAsync"/> is a question about the answers so far:
    /// it is appended to the conversation, which the provider also holds up to its last question. Until 20 Nov 2026
    /// every question was of the second kind and went out with its own schema as the only tool. The tool definitions
    /// sit ahead of the evidence in what the provider reads, so the second question of a session shared nothing with
    /// the first past the system prompt: the evidence was prefilled again, with the first answer (often as long as
    /// the evidence) on top. The extraction critic ran at a tenth of its prompt cached, and each of those calls was
    /// counted by AIRouter's scheduler as a warm prefix lost, which shortens the cache lifetime it plans every
    /// service's turns around.
    ///
    /// All questions of a session share its key and cache identity, so AIRouter's scheduler sees one conversation and
    /// never parks a later question behind other work. Dispose ends the provider-side session.
    /// </summary>
    public sealed class CacheShapedSession : IDisposable
    {
        private readonly OmniLlm owner;
        private readonly ILlmClient client;
        private readonly string moduleId, purpose, sessionKey, systemPrompt, stablePrefix;
        private readonly AIRouterWorkClass workClass;
        private readonly string? promptVersion;
        private readonly long? jobId;
        private bool prefixSent;
        private int priorPromptTokens;
        private int priorQuestionChars;
        private bool disposed;

        internal CacheShapedSession(OmniLlm owner, ILlmClient client, string moduleId, string purpose, string sessionKey, string systemPrompt,
            string stablePrefix, AIRouterWorkClass workClass, string? promptVersion, long? jobId)
        {
            this.owner = owner;
            this.client = client;
            this.moduleId = moduleId;
            this.purpose = purpose;
            this.sessionKey = sessionKey;
            this.systemPrompt = systemPrompt;
            this.stablePrefix = stablePrefix;
            this.workClass = workClass;
            this.promptVersion = promptVersion;
            this.jobId = jobId;
        }

        public int Passes { get; private set; }

        /// <summary>The effort every question of this session is sent with; null leaves it to <see cref="OmniLlm.Shape"/>.</summary>
        public string? ReasoningEffort { get; init; }

        /// <summary>Every schema the session's questions answer with (see <see cref="OmniLlm.OpenSession"/>).</summary>
        public IReadOnlyList<LlmOutputSchema>? Schemas { get; init; }

        /// <summary>True while the provider-side session holds the stable prefix and an answer, so a follow-up travels alone.</summary>
        public bool PrefixSent => prefixSent;

        /// <summary>
        /// A question of its own over the prefix: it must say everything it is about, because the model sees the prefix
        /// and this question only, not what was asked or answered before.
        /// </summary>
        public Task<LlmResult> AskAsync(string question, LlmOutputSchema? schema, int? maxOutputTokens, ModelSamplingParameters? sampling, CancellationToken ct) =>
            SendAsync(question, schema, maxOutputTokens, sampling, followUp: false, ct);

        /// <summary>
        /// A question about the answers so far (a step of a research loop, "now argue the opposite"): the model sees the
        /// prefix, every question and answer since the last <see cref="AskAsync"/>, and this. When the session holds
        /// nothing to follow up on (nothing was asked yet, or the last question failed) it is asked over the prefix alone.
        /// </summary>
        public Task<LlmResult> FollowUpAsync(string question, LlmOutputSchema? schema, int? maxOutputTokens, ModelSamplingParameters? sampling, CancellationToken ct) =>
            SendAsync(question, schema, maxOutputTokens, sampling, followUp: true, ct);

        private async Task<LlmResult> SendAsync(string question, LlmOutputSchema? schema, int? maxOutputTokens, ModelSamplingParameters? sampling, bool followUp, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Only a follow-up to an answered question travels alone. Everything else carries the prefix and takes the
            // provider-side session back to its system prompt first: that also clears a turn a failed question left
            // without an answer, so nothing is ever appended to one or sent without its evidence.
            bool alone = followUp && prefixSent;
            // AIRouter's scheduler measures a later question against the whole request before it on the session: it takes
            // that request to be the start of this one. For questions over one opening that is as good as true while one
            // of the two questions is small beside the opening (a reading's one line, then the critic). Two long ones in
            // a row (an adjudication of many claims, then a critic of many facts) share the opening and nothing after it,
            // and nine tenths of the shorter request would not come back from cache: a warm prefix scored as lost, though
            // nothing was. Such a question gets a cache identity of its own. The provider serves the opening all the
            // same, and the scheduler sees a first call, which it does not score.
            if (!alone && Math.Min(priorQuestionChars, question.Length) > (systemPrompt.Length + stablePrefix.Length) / 12)
                client.EndSession(sessionKey);
            // A follow-up's request is the whole conversation: beside it, any question asked next is the shorter one.
            priorQuestionChars = alone ? int.MaxValue : question.Length;
            // Not until this one is answered: a call that throws (an outage, a cancellation) leaves its question unanswered too.
            prefixSent = false;
            var result = await owner.CallAsync(new LlmCall
            {
                ModuleId = moduleId,
                Purpose = purpose,
                PromptVersion = promptVersion,
                JobId = jobId,
                PriorPromptTokens = priorPromptTokens,
                Request = new LlmRequest
                {
                    Purpose = purpose,
                    SessionKey = sessionKey,
                    SystemPrompt = systemPrompt,
                    Turns = new[] { new LlmTurn("user", alone ? question : stablePrefix + "\n\n" + question) },
                    Stamp = LlmStamp.None,
                    MaxOutputTokens = maxOutputTokens,
                    Sampling = sampling,
                    WorkClass = workClass,
                    OutputSchema = schema,
                    SchemaFamily = Schemas,
                    ReasoningEffort = ReasoningEffort,
                    KeepSession = true,
                    Rewind = !alone,
                },
            }, ct).ConfigureAwait(false);
            prefixSent = result.Success;
            // A call the provider read (it counted the prompt) left the prefix in its cache, answered or not.
            if (result.PromptTokens > 0) priorPromptTokens = result.PromptTokens;
            Passes++;
            return result;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            client.EndSession(sessionKey);
        }
    }

    /// <summary>
    /// Admission control for the LLM-background lane (plan R1): background LLM work pauses while the credential
    /// circuit is open, or while the provider is not serving what Omniscience sends it a second time (so a prompt that
    /// stops matching its own prefix can never quietly drag down the fleet's rate and get the key suspended again).
    ///
    /// It judges <see cref="OmniLlm.ResentEfficiency"/>, not the plain hit rate. The plain rate counts evidence that
    /// is read for the first time as a miss, and pausing cannot change that: for two days the lane stopped whenever
    /// twenty calls of the last hour averaged under 60% cached, which extraction did by construction, and each pause
    /// let the shared head of its prompts go cold as well.
    /// </summary>
    public sealed class LlmBackgroundGate : ILaneGate
    {
        private readonly OmniLlm llm;
        private readonly double efficiencyFloor;
        private readonly int minSamples;
        private readonly TimeSpan pause;

        public LlmBackgroundGate(OmniLlm llm, double efficiencyFloor = 0.8, int minSamples = 20, TimeSpan? pause = null)
        {
            this.llm = llm;
            this.efficiencyFloor = efficiencyFloor;
            this.minSamples = minSamples;
            this.pause = pause ?? TimeSpan.FromMinutes(2);
        }

        public bool ShouldPause()
        {
            if (llm.CircuitOpen) return true;
            var (eff, n) = llm.ResentEfficiency(TimeSpan.FromHours(1), AIRouterWorkClass.Background.ToString());
            return n >= minSamples && eff < efficiencyFloor;
        }

        public async Task WaitAsync(Lane lane, CancellationToken ct)
        {
            if (lane != Lane.LlmBackground) return;
            while (ShouldPause()) await Task.Delay(pause, ct).ConfigureAwait(false);
        }
    }
}
