using Omnipotent.Services.KliveAPI.Telemetry;

namespace Omnipotent.Services.KliveLLM.Telemetry
{
    /// <summary>
    /// Everything the ledger keeps about one series in one time bucket: counters, plus log-scale
    /// histograms so percentiles survive any amount of re-bucketing. Two aggregates merge by adding,
    /// which is what makes minute rows roll up into hours and days, and lets a query regroup them by
    /// any dimension without going back to the calls.
    ///
    /// Persisted as a compact versioned blob (see <see cref="Write"/>); the dimensions it belongs to
    /// are real columns beside it, so SQL does the filtering and this does the arithmetic.
    /// </summary>
    public sealed class LlmAggregate
    {
        public long Calls;
        public long Attempts;
        /// <summary>Attempts after a call's first: what retries cost in requests.</summary>
        public long RetryAttempts;
        public long Streamed;
        public long PromptTokens;
        public long CachedTokens;
        public long CacheWriteTokens;
        public long CompletionTokens;
        public long ReasoningTokens;
        /// <summary>Prompt tokens of calls whose provider reported a cache split.</summary>
        public long MeasuredPromptTokens;
        /// <summary>Prompt and cached tokens of continuation turns (a prefix existed to be reused).</summary>
        public long ContinuationPromptTokens;
        public long ContinuationCachedTokens;
        public long QueueMs;
        public long ProviderMs;
        public long TotalMs;
        public long TtftMs;
        public long TtftCalls;
        /// <summary>Generation time and tokens of streamed calls: output speed is one over the other.</summary>
        public long GenerationMs;
        public long GenerationTokens;
        public long RequestBytes;
        public long ToolCalls;
        public long Truncated;
        public long Compacted;
        public long ColdStarts;
        public long Redirected;
        public long Fallbacks;
        public long Pinned;
        /// <summary>Calls whose router reported a cost, and their prompt and completion tokens.</summary>
        public long CostCalls;
        public long CostPromptTokens;
        public long CostCompletionTokens;
        public long Estimated;

        public double CostUsd;
        public double UpstreamCostUsd;

        /// <summary>Calls by how they ended, indexed as <see cref="LlmOutcome.All"/>.</summary>
        public long[] Outcomes = new long[LlmOutcome.All.Length];
        /// <summary>HTTP attempts by how they ended.</summary>
        public long[] AttemptOutcomes = new long[LlmOutcome.All.Length];

        public LogHistogram ProviderHist = new();
        public LogHistogram TtftHist = new();
        public LogHistogram TotalHist = new();
        public LogHistogram QueueHist = new();
        public LogHistogram PromptHist = new();
        public LogHistogram CompletionHist = new();

        // Filled by the query engine from the price book while it merges rows; never persisted,
        // because a corrected price must be able to re-cost history.
        public double EstimatedCostUsd;
        public double ListCostUsd;
        public long UnpricedTokens;

        public long Ok => Outcomes[0];
        public long Cancelled => Outcomes[1];
        public long Failed => Math.Max(0, Calls - Ok - Cancelled);
        public long FreshTokens => Math.Max(0, PromptTokens - CachedTokens);
        public long TotalTokens => PromptTokens + CompletionTokens;
        public long Retries => RetryAttempts;
        public bool IsEmpty => Calls == 0 && Attempts == 0;

        public void Add(LlmCallRecord r)
        {
            Calls++;
            Attempts += Math.Max(0, r.Attempts);
            RetryAttempts += Math.Max(0, r.Attempts - 1);
            if (r.Stream) Streamed++;
            PromptTokens += r.PromptTokens;
            CachedTokens += r.CachedTokens;
            CacheWriteTokens += r.CacheWriteTokens;
            CompletionTokens += r.CompletionTokens;
            ReasoningTokens += r.ReasoningTokens;
            if (r.CacheMetrics)
            {
                MeasuredPromptTokens += r.PromptTokens;
                if (r.CacheEpochTurn > 1)
                {
                    ContinuationPromptTokens += r.PromptTokens;
                    ContinuationCachedTokens += r.CachedTokens;
                }
            }
            QueueMs += r.QueueMs;
            ProviderMs += r.ProviderMs;
            TotalMs += r.TotalMs;
            RequestBytes += r.RequestBytes;
            ToolCalls += r.ToolCalls;
            if (r.Has(LlmCallFlags.Truncated)) Truncated++;
            if (r.Has(LlmCallFlags.Compacted)) Compacted++;
            if (r.Has(LlmCallFlags.ColdStart)) ColdStarts++;
            if (r.Has(LlmCallFlags.Redirected)) Redirected++;
            if (r.Has(LlmCallFlags.Fallback)) Fallbacks++;
            if (r.Has(LlmCallFlags.Pinned)) Pinned++;
            if (r.Has(LlmCallFlags.Estimated)) Estimated++;
            if (r.CostUsd is { } cost)
            {
                CostUsd += cost;
                CostCalls++;
                CostPromptTokens += r.PromptTokens;
                CostCompletionTokens += r.CompletionTokens;
            }
            if (r.UpstreamCostUsd is { } upstream) UpstreamCostUsd += upstream;

            Outcomes[LlmOutcome.IndexOf(r.Outcome)]++;
            if (r.AttemptLog != null)
                foreach (var attempt in r.AttemptLog) AttemptOutcomes[LlmOutcome.IndexOf(attempt.Outcome)]++;

            bool sent = r.Outcome != LlmOutcome.Blocked;
            if (sent)
            {
                TotalHist.Add(r.TotalMs);
                QueueHist.Add(r.QueueMs);
                if (r.ProviderMs > 0) ProviderHist.Add(r.ProviderMs);
            }
            if (r.TtftMs is { } ttft)
            {
                TtftMs += ttft;
                TtftCalls++;
                TtftHist.Add(ttft);
                long generating = Math.Max(0, r.ProviderMs - Math.Max(0, ttft - r.QueueMs));
                if (r.Outcome == LlmOutcome.Ok && r.CompletionTokens > 0 && generating > 0)
                {
                    GenerationMs += generating;
                    GenerationTokens += r.CompletionTokens;
                }
            }
            if (r.Outcome == LlmOutcome.Ok)
            {
                if (r.PromptTokens > 0) PromptHist.Add(r.PromptTokens);
                if (r.CompletionTokens > 0) CompletionHist.Add(r.CompletionTokens);
            }
        }

        public void Merge(LlmAggregate o)
        {
            Calls += o.Calls; Attempts += o.Attempts; RetryAttempts += o.RetryAttempts; Streamed += o.Streamed;
            PromptTokens += o.PromptTokens; CachedTokens += o.CachedTokens; CacheWriteTokens += o.CacheWriteTokens;
            CompletionTokens += o.CompletionTokens; ReasoningTokens += o.ReasoningTokens;
            MeasuredPromptTokens += o.MeasuredPromptTokens;
            ContinuationPromptTokens += o.ContinuationPromptTokens; ContinuationCachedTokens += o.ContinuationCachedTokens;
            QueueMs += o.QueueMs; ProviderMs += o.ProviderMs; TotalMs += o.TotalMs;
            TtftMs += o.TtftMs; TtftCalls += o.TtftCalls;
            GenerationMs += o.GenerationMs; GenerationTokens += o.GenerationTokens;
            RequestBytes += o.RequestBytes; ToolCalls += o.ToolCalls;
            Truncated += o.Truncated; Compacted += o.Compacted; ColdStarts += o.ColdStarts;
            Redirected += o.Redirected; Fallbacks += o.Fallbacks; Pinned += o.Pinned;
            CostCalls += o.CostCalls; CostPromptTokens += o.CostPromptTokens; CostCompletionTokens += o.CostCompletionTokens;
            Estimated += o.Estimated;
            CostUsd += o.CostUsd; UpstreamCostUsd += o.UpstreamCostUsd;
            EstimatedCostUsd += o.EstimatedCostUsd; ListCostUsd += o.ListCostUsd; UnpricedTokens += o.UnpricedTokens;
            for (int i = 0; i < Outcomes.Length; i++)
            {
                Outcomes[i] += o.Outcomes[i];
                AttemptOutcomes[i] += o.AttemptOutcomes[i];
            }
            ProviderHist.Merge(o.ProviderHist);
            TtftHist.Merge(o.TtftHist);
            TotalHist.Merge(o.TotalHist);
            QueueHist.Merge(o.QueueHist);
            PromptHist.Merge(o.PromptHist);
            CompletionHist.Merge(o.CompletionHist);
        }

        // ── persistence ──

        private const byte Version = 1;

        private long[] Counters() => new[]
        {
            Calls, Attempts, Streamed, PromptTokens, CachedTokens, CacheWriteTokens, CompletionTokens, ReasoningTokens,
            MeasuredPromptTokens, ContinuationPromptTokens, ContinuationCachedTokens, QueueMs, ProviderMs, TotalMs,
            TtftMs, TtftCalls, GenerationMs, GenerationTokens, RequestBytes, ToolCalls, Truncated, Compacted, ColdStarts,
            Redirected, Fallbacks, Pinned, CostCalls, CostPromptTokens, CostCompletionTokens, Estimated, RetryAttempts,
        };

        private void SetCounters(long[] v)
        {
            long At(int i) => i < v.Length ? v[i] : 0;
            Calls = At(0); Attempts = At(1); Streamed = At(2); PromptTokens = At(3); CachedTokens = At(4);
            CacheWriteTokens = At(5); CompletionTokens = At(6); ReasoningTokens = At(7); MeasuredPromptTokens = At(8);
            ContinuationPromptTokens = At(9); ContinuationCachedTokens = At(10); QueueMs = At(11); ProviderMs = At(12);
            TotalMs = At(13); TtftMs = At(14); TtftCalls = At(15); GenerationMs = At(16); GenerationTokens = At(17);
            RequestBytes = At(18); ToolCalls = At(19); Truncated = At(20); Compacted = At(21); ColdStarts = At(22);
            Redirected = At(23); Fallbacks = At(24); Pinned = At(25); CostCalls = At(26); CostPromptTokens = At(27);
            CostCompletionTokens = At(28); Estimated = At(29); RetryAttempts = At(30);
        }

        /// <summary>
        /// Counter and outcome counts are length-prefixed, so a later version can append fields and
        /// still read (and be read by) rows written before it.
        /// </summary>
        public byte[] ToBytes()
        {
            using var ms = new MemoryStream(160);
            using var w = new BinaryWriter(ms);
            w.Write(Version);
            long[] counters = Counters();
            w.Write((byte)counters.Length);
            foreach (long c in counters) w.Write7BitEncodedInt64(Math.Max(0, c));
            w.Write(CostUsd);
            w.Write(UpstreamCostUsd);
            w.Write((byte)Outcomes.Length);
            foreach (long c in Outcomes) w.Write7BitEncodedInt64(Math.Max(0, c));
            foreach (long c in AttemptOutcomes) w.Write7BitEncodedInt64(Math.Max(0, c));
            ProviderHist.Write(w);
            TtftHist.Write(w);
            TotalHist.Write(w);
            QueueHist.Write(w);
            PromptHist.Write(w);
            CompletionHist.Write(w);
            w.Flush();
            return ms.ToArray();
        }

        public static LlmAggregate FromBytes(byte[]? blob)
        {
            var a = new LlmAggregate();
            if (blob == null || blob.Length == 0) return a;
            using var ms = new MemoryStream(blob, writable: false);
            using var r = new BinaryReader(ms);
            byte version = r.ReadByte();
            if (version != Version) return a;
            int n = r.ReadByte();
            var counters = new long[n];
            for (int i = 0; i < n; i++) counters[i] = r.Read7BitEncodedInt64();
            a.SetCounters(counters);
            a.CostUsd = r.ReadDouble();
            a.UpstreamCostUsd = r.ReadDouble();
            int outcomes = r.ReadByte();
            for (int i = 0; i < outcomes; i++)
            {
                long v = r.Read7BitEncodedInt64();
                if (i < a.Outcomes.Length) a.Outcomes[i] = v;
            }
            for (int i = 0; i < outcomes; i++)
            {
                long v = r.Read7BitEncodedInt64();
                if (i < a.AttemptOutcomes.Length) a.AttemptOutcomes[i] = v;
            }
            a.ProviderHist = LogHistogram.Read(r);
            a.TtftHist = LogHistogram.Read(r);
            a.TotalHist = LogHistogram.Read(r);
            a.QueueHist = LogHistogram.Read(r);
            a.PromptHist = LogHistogram.Read(r);
            a.CompletionHist = LogHistogram.Read(r);
            return a;
        }

        // ── metrics ──

        /// <summary>Every metric a query can ask for, by the name the API uses.</summary>
        public static readonly string[] MetricNames =
        {
            "calls", "ok", "failed", "cancelled", "blocked", "errorPct", "attempts", "retries",
            "promptTokens", "cachedTokens", "freshTokens", "cacheWriteTokens", "completionTokens", "reasoningTokens", "totalTokens",
            "cacheHitPct", "prefixReusePct", "wastedPrefillTokens",
            "costUsd", "upstreamCostUsd", "estCostUsd", "listCostUsd", "costPerMTok", "unpricedTokens",
            "meanTotalMs", "p50TotalMs", "p90TotalMs", "p95TotalMs", "p99TotalMs", "maxTotalMs",
            "meanProviderMs", "p50ProviderMs", "p90ProviderMs", "p95ProviderMs", "p99ProviderMs", "maxProviderMs",
            "meanTtftMs", "p50TtftMs", "p95TtftMs", "p99TtftMs",
            "meanQueueMs", "p50QueueMs", "p95QueueMs", "maxQueueMs", "queueMs",
            "tokensPerSec", "meanPromptTokens", "p50PromptTokens", "p95PromptTokens", "maxPromptTokens",
            "meanCompletionTokens", "p95CompletionTokens",
            "streamedPct", "truncated", "compacted", "coldStarts", "redirected", "fallbacks", "toolCalls", "requestBytes",
            "rateLimited", "timeouts", "networkErrors", "serverErrors", "authErrors", "invalidRequests", "modelErrors",
            "emptyResponses", "creditErrors", "stalled", "otherErrors",
            "attemptFailures", "attemptFailurePct", "attemptRateLimited",
        };

        private static readonly HashSet<string> knownMetrics = new(MetricNames, StringComparer.OrdinalIgnoreCase);
        public static bool IsMetric(string? name) => name != null && knownMetrics.Contains(name);

        private long Out(string outcome) => Outcomes[LlmOutcome.IndexOf(outcome)];
        private long AttemptOut(string outcome) => AttemptOutcomes[LlmOutcome.IndexOf(outcome)];
        private static double? Ratio(double a, double b) => b > 0 ? a / b : null;
        private static double? Pct(double a, double b) => b > 0 ? 100.0 * a / b : null;

        public long AttemptFailures
        {
            get
            {
                long n = 0;
                for (int i = 0; i < AttemptOutcomes.Length; i++)
                    if (LlmOutcome.IsFailure(LlmOutcome.All[i])) n += AttemptOutcomes[i];
                return n;
            }
        }

        public long RouterFaults
        {
            get
            {
                long n = 0;
                for (int i = 0; i < AttemptOutcomes.Length; i++)
                    if (LlmOutcome.IsRouterFault(LlmOutcome.All[i])) n += AttemptOutcomes[i];
                return n;
            }
        }

        /// <summary>
        /// A metric's value for this aggregate, or null when it is undefined here (a percentage over
        /// nothing, a first-token time when nothing streamed). Null is plotted as a gap, never as zero.
        /// </summary>
        public double? Metric(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "calls": return Calls;
                case "ok": return Ok;
                case "failed": return Failed;
                case "cancelled": return Cancelled;
                case "blocked": return Out(LlmOutcome.Blocked);
                case "errorpct": return Pct(Failed, Calls - Cancelled);
                case "attempts": return Attempts;
                case "retries": return Retries;
                case "prompttokens": return PromptTokens;
                case "cachedtokens": return CachedTokens;
                case "freshtokens": return FreshTokens;
                case "cachewritetokens": return CacheWriteTokens;
                case "completiontokens": return CompletionTokens;
                case "reasoningtokens": return ReasoningTokens;
                case "totaltokens": return TotalTokens;
                // Over measured prompts only: a router that reports no cache split says nothing either way.
                case "cachehitpct": return Pct(CachedTokens, MeasuredPromptTokens);
                case "prefixreusepct": return Pct(ContinuationCachedTokens, ContinuationPromptTokens);
                case "wastedprefilltokens": return Math.Max(0, ContinuationPromptTokens - ContinuationCachedTokens);
                case "costusd": return CostUsd;
                case "upstreamcostusd": return UpstreamCostUsd;
                case "estcostusd": return EstimatedCostUsd;
                case "listcostusd": return ListCostUsd;
                case "costpermtok": return TotalTokens > 0 ? EstimatedCostUsd / (TotalTokens / 1_000_000.0) : null;
                case "unpricedtokens": return UnpricedTokens;
                case "meantotalms": return TotalHist.Count > 0 ? TotalHist.Mean : null;
                case "p50totalms": return P(TotalHist, 0.50);
                case "p90totalms": return P(TotalHist, 0.90);
                case "p95totalms": return P(TotalHist, 0.95);
                case "p99totalms": return P(TotalHist, 0.99);
                case "maxtotalms": return TotalHist.Count > 0 ? TotalHist.Max : null;
                case "meanproviderms": return ProviderHist.Count > 0 ? ProviderHist.Mean : null;
                case "p50providerms": return P(ProviderHist, 0.50);
                case "p90providerms": return P(ProviderHist, 0.90);
                case "p95providerms": return P(ProviderHist, 0.95);
                case "p99providerms": return P(ProviderHist, 0.99);
                case "maxproviderms": return ProviderHist.Count > 0 ? ProviderHist.Max : null;
                case "meanttftms": return TtftHist.Count > 0 ? TtftHist.Mean : null;
                case "p50ttftms": return P(TtftHist, 0.50);
                case "p95ttftms": return P(TtftHist, 0.95);
                case "p99ttftms": return P(TtftHist, 0.99);
                case "meanqueuems": return QueueHist.Count > 0 ? QueueHist.Mean : null;
                case "p50queuems": return P(QueueHist, 0.50);
                case "p95queuems": return P(QueueHist, 0.95);
                case "maxqueuems": return QueueHist.Count > 0 ? QueueHist.Max : null;
                case "queuems": return QueueMs;
                case "tokenspersec": return GenerationMs > 0 ? GenerationTokens / (GenerationMs / 1000.0) : null;
                case "meanprompttokens": return PromptHist.Count > 0 ? PromptHist.Mean : null;
                case "p50prompttokens": return P(PromptHist, 0.50);
                case "p95prompttokens": return P(PromptHist, 0.95);
                case "maxprompttokens": return PromptHist.Count > 0 ? PromptHist.Max : null;
                case "meancompletiontokens": return CompletionHist.Count > 0 ? CompletionHist.Mean : null;
                case "p95completiontokens": return P(CompletionHist, 0.95);
                case "streamedpct": return Pct(Streamed, Calls);
                case "truncated": return Truncated;
                case "compacted": return Compacted;
                case "coldstarts": return ColdStarts;
                case "redirected": return Redirected;
                case "fallbacks": return Fallbacks;
                case "toolcalls": return ToolCalls;
                case "requestbytes": return RequestBytes;
                case "ratelimited": return Out(LlmOutcome.RateLimited);
                case "timeouts": return Out(LlmOutcome.Timeout);
                case "networkerrors": return Out(LlmOutcome.Network);
                case "servererrors": return Out(LlmOutcome.Unavailable);
                case "autherrors": return Out(LlmOutcome.Auth);
                case "invalidrequests": return Out(LlmOutcome.Invalid);
                case "modelerrors": return Out(LlmOutcome.Model);
                case "emptyresponses": return Out(LlmOutcome.Empty);
                case "crediterrors": return Out(LlmOutcome.Credit);
                case "stalled": return Out(LlmOutcome.Stalled);
                case "othererrors": return Out(LlmOutcome.Error);
                case "attemptfailures": return AttemptFailures;
                case "attemptfailurepct": return Pct(AttemptFailures, Attempts);
                case "attemptratelimited": return AttemptOut(LlmOutcome.RateLimited);
                default: return null;
            }
        }

        private static double? P(LogHistogram h, double p) => h.Count > 0 ? h.Percentile(p) : null;

        /// <summary>The headline numbers of an aggregate, for tables and tiles.</summary>
        public Dictionary<string, double?> Summary(IEnumerable<string>? metrics = null)
        {
            var result = new Dictionary<string, double?>(StringComparer.Ordinal);
            foreach (string name in metrics ?? DefaultSummary) result[name] = Round(Metric(name));
            return result;
        }

        public static readonly string[] DefaultSummary =
        {
            "calls", "failed", "errorPct", "attempts", "retries", "promptTokens", "cachedTokens", "freshTokens",
            "completionTokens", "reasoningTokens", "totalTokens", "cacheHitPct", "prefixReusePct", "costUsd",
            "estCostUsd", "listCostUsd", "p50TotalMs", "p95TotalMs", "p50ProviderMs", "p95ProviderMs", "p50TtftMs",
            "p95TtftMs", "meanQueueMs", "p95QueueMs", "tokensPerSec", "rateLimited", "fallbacks", "redirected", "truncated",
        };

        internal static double? Round(double? value)
        {
            if (value is not { } v || !double.IsFinite(v)) return null;
            double a = Math.Abs(v);
            return a >= 1000 ? Math.Round(v, 1) : a >= 1 ? Math.Round(v, 3) : Math.Round(v, 6);
        }
    }
}
