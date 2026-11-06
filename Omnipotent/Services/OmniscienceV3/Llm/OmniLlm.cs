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
    }

    /// <summary>
    /// The Omniscience side of the LLM gateway: audit trail (intel.db <c>llm_calls</c>), recovery
    /// (overflow → learn + repack + retry once; 413 → repack smaller + retry once; 429 → transient retry via
    /// the job queue; 401/403 → permanent, plus a circuit breaker that pauses background LLM work), token
    /// estimator calibration, cache-shaped sessions, and Omniscience's own prefix-cache efficiency.
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

        public async Task<LlmResult> CallAsync(LlmCall call, CancellationToken ct)
        {
            var request = call.Request;
            LlmResult result = await client.QueryAsync(request, ct).ConfigureAwait(false);
            await LogAsync(call, request, result, ct).ConfigureAwait(false);

            if (!result.Success && call.Repack != null && result.ErrorKind is LlmErrorKind.ContextOverflow or LlmErrorKind.RequestTooLarge)
            {
                double shrink = result.ErrorKind == LlmErrorKind.ContextOverflow ? 0.85 : 0.7;
                request = call.Repack(shrink);
                result = await client.QueryAsync(request, ct).ConfigureAwait(false);
                await LogAsync(call, request, result, ct).ConfigureAwait(false);
            }

            if (result.Success)
            {
                Estimator.Calibrate(string.Concat(request.Turns.Select(t => t.Content)) + request.SystemPrompt, result.PromptTokens);
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
        public CacheShapedSession OpenSession(string moduleId, string purpose, string sessionKey, string systemPrompt, string stablePrefix,
            AIRouterWorkClass workClass, string? promptVersion = null, long? jobId = null) =>
            new(this, client, moduleId, purpose, sessionKey, systemPrompt, stablePrefix, workClass, promptVersion, jobId);

        /// <summary>Omniscience's own prefix-cache efficiency over a window (cached / prompt tokens).</summary>
        public (double Efficiency, int Samples) CacheEfficiency(TimeSpan window, string? workClass = null)
        {
            using var c = intel.OpenRead();
            var row = c.Query(null, """
                SELECT COALESCE(SUM(cached_tokens),0), COALESCE(SUM(prompt_tokens),0), COUNT(*) FROM llm_calls
                WHERE at_ms >= $since AND outcome='ok' AND ($w IS NULL OR work_class=$w)
                """, r => (Cached: r.GetInt64(0), Prompt: r.GetInt64(1), N: r.GetInt32(2)),
                ("$since", clock.NowMs - (long)window.TotalMilliseconds), ("$w", workClass)).Single();
            return (row.Prompt == 0 ? 1.0 : (double)row.Cached / row.Prompt, row.N);
        }

        private Task LogAsync(LlmCall call, LlmRequest request, LlmResult r, CancellationToken ct) =>
            intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO llm_calls(at_ms, module_id, purpose, job_id, model, provider, work_class, prompt_tokens, cached_tokens,
                                      completion_tokens, request_bytes, latency_ms, outcome, finish_reason, prompt_version, error)
                VALUES($at,$m,$p,$j,$model,$prov,$wc,$pt,$ct,$cpt,$b,$lat,$o,$fr,$pv,$err)
                """,
                ("$at", clock.NowMs), ("$m", call.ModuleId), ("$p", call.Purpose), ("$j", call.JobId), ("$model", r.Model), ("$prov", r.Provider),
                ("$wc", request.WorkClass.ToString()), ("$pt", r.PromptTokens), ("$ct", r.CachedTokens), ("$cpt", r.CompletionTokens),
                ("$b", r.RequestBytes), ("$lat", r.LatencyMs), ("$o", r.Success ? "ok" : r.ErrorKind.ToString()), ("$fr", r.FinishReason),
                ("$pv", call.PromptVersion), ("$err", r.ErrorMessage is { Length: > 2000 } e ? e[..2000] : r.ErrorMessage)), ct);
    }

    /// <summary>
    /// "Prefill once, ask many" (plan "CacheShapedSession"): the first question carries the large stable
    /// prefix (system + evidence); later questions append only themselves, so AIRouter serves the prefix
    /// from cache and the passes stay in one residency cohort. Dispose ends the provider-side session.
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

        public async Task<LlmResult> AskAsync(string question, LlmOutputSchema? schema, int? maxOutputTokens, ModelSamplingParameters? sampling, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var turns = new List<LlmTurn>();
            turns.Add(new LlmTurn("user", prefixSent ? question : stablePrefix + "\n\n" + question));
            var result = await owner.CallAsync(new LlmCall
            {
                ModuleId = moduleId,
                Purpose = purpose,
                PromptVersion = promptVersion,
                JobId = jobId,
                Request = new LlmRequest
                {
                    Purpose = purpose,
                    SessionKey = sessionKey,
                    SystemPrompt = systemPrompt,
                    Turns = turns,
                    Stamp = LlmStamp.None,
                    MaxOutputTokens = maxOutputTokens,
                    Sampling = sampling,
                    WorkClass = workClass,
                    OutputSchema = schema,
                    KeepSession = true,
                },
            }, ct).ConfigureAwait(false);
            if (result.Success) prefixSent = true;
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
    /// Admission control for the LLM-background lane (plan R1): background LLM work pauses while the
    /// credential circuit is open, or while Omniscience's own prefix-cache efficiency over the last hour is
    /// below the floor (so it can never quietly drag down the fleet's rate and get the key suspended again).
    /// </summary>
    public sealed class LlmBackgroundGate : ILaneGate
    {
        private readonly OmniLlm llm;
        private readonly double efficiencyFloor;
        private readonly int minSamples;
        private readonly TimeSpan pause;

        public LlmBackgroundGate(OmniLlm llm, double efficiencyFloor = 0.6, int minSamples = 20, TimeSpan? pause = null)
        {
            this.llm = llm;
            this.efficiencyFloor = efficiencyFloor;
            this.minSamples = minSamples;
            this.pause = pause ?? TimeSpan.FromMinutes(2);
        }

        public bool ShouldPause()
        {
            if (llm.CircuitOpen) return true;
            var (eff, n) = llm.CacheEfficiency(TimeSpan.FromHours(1), AIRouterWorkClass.Background.ToString());
            return n >= minSamples && eff < efficiencyFloor;
        }

        public async Task WaitAsync(Lane lane, CancellationToken ct)
        {
            if (lane != Lane.LlmBackground) return;
            while (ShouldPause()) await Task.Delay(pause, ct).ConfigureAwait(false);
        }
    }
}
