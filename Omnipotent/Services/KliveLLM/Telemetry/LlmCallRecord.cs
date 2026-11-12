using System.Diagnostics;
using System.Net;

namespace Omnipotent.Services.KliveLLM.Telemetry
{
    /// <summary>How one call, or one attempt of it, ended. Stored as these strings.</summary>
    public static class LlmOutcome
    {
        public const string Ok = "ok";
        public const string Cancelled = "cancelled";
        /// <summary>Refused before anything was sent: paused, over budget, spending not allowed, no usable router.</summary>
        public const string Blocked = "blocked";
        public const string RateLimited = "rate_limited";
        public const string Timeout = "timeout";
        public const string Network = "network";
        public const string Unavailable = "unavailable";
        public const string Auth = "auth";
        public const string Invalid = "invalid_request";
        public const string Model = "model_unavailable";
        public const string Empty = "empty";
        public const string Credit = "no_credit";
        public const string Stalled = "stalled";
        public const string Error = "error";

        /// <summary>Fixed order: aggregates store one counter per entry, by index.</summary>
        public static readonly string[] All =
        {
            Ok, Cancelled, Blocked, RateLimited, Timeout, Network, Unavailable, Auth, Invalid, Model, Empty, Credit, Stalled, Error,
        };

        private static readonly Dictionary<string, int> index = All
            .Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i, StringComparer.Ordinal);

        public static int IndexOf(string? outcome) => outcome != null && index.TryGetValue(outcome, out int i) ? i : All.Length - 1;

        /// <summary>The router itself failed: it counts against its availability.</summary>
        public static bool IsRouterFault(string? outcome) => outcome is Timeout or Network or Unavailable or Empty or Stalled;

        /// <summary>The router answered but would not serve: a limit, a key, credit or a model it does not have.</summary>
        public static bool IsRouterRefusal(string? outcome) => outcome is RateLimited or Auth or Credit or Model;

        public static bool IsFailure(string? outcome) => outcome is not (Ok or Cancelled);

        public static string Of(RemoteLLMFailureKind kind) => kind switch
        {
            RemoteLLMFailureKind.Network => Network,
            RemoteLLMFailureKind.Timeout => Timeout,
            RemoteLLMFailureKind.RateLimited => RateLimited,
            RemoteLLMFailureKind.InsufficientProviderCredit => Credit,
            RemoteLLMFailureKind.Authentication => Auth,
            RemoteLLMFailureKind.InvalidRequest => Invalid,
            RemoteLLMFailureKind.ModelUnavailable => Model,
            RemoteLLMFailureKind.ProviderUnavailable => Unavailable,
            RemoteLLMFailureKind.EmptyResponse => Empty,
            RemoteLLMFailureKind.Paused or RemoteLLMFailureKind.BudgetExceeded
                or RemoteLLMFailureKind.SpendingNotAllowed or RemoteLLMFailureKind.RouterUnavailable => Blocked,
            _ => Error,
        };

        public static string Of(Exception? ex, bool cancelled = false)
        {
            if (cancelled || ex is OperationCanceledException and not TaskCanceledException) return Cancelled;
            return ex switch
            {
                null => Ok,
                RemoteLLMException remote => Of(remote.Kind),
                KliveLLMStreamStalledException => Stalled,
                TaskCanceledException or TimeoutException => Timeout,
                HttpRequestException { StatusCode: { } status } => Of(KliveLLM.ClassifyRemoteFailure(status, null)),
                HttpRequestException or IOException or System.Net.Sockets.SocketException => Network,
                _ => Error,
            };
        }

        public static string OfStatus(HttpStatusCode status, string? body) => Of(KliveLLM.ClassifyRemoteFailure(status, body));
    }

    [Flags]
    public enum LlmCallFlags
    {
        None = 0,
        Compacted = 1,
        /// <summary>The first turn of a cache epoch: nothing reusable was expected.</summary>
        ColdStart = 2,
        /// <summary>The router does not serve the model that was asked for, so another was sent.</summary>
        Redirected = 4,
        Pinned = 8,
        /// <summary>Served by a fallback router after the primary failed.</summary>
        Fallback = 16,
        /// <summary>finish_reason "length": the reply hit its output cap.</summary>
        Truncated = 32,
        Stalled = 64,
        /// <summary>Prompt and reply text was kept for this call.</summary>
        ContentKept = 128,
        /// <summary>Imported from the Projects usage journal, not seen live.</summary>
        Imported = 256,
        /// <summary>Token counts are estimates (the local model reports none).</summary>
        Estimated = 512,
    }

    /// <summary>One HTTP exchange inside a call. A call that succeeds on its third attempt still shows
    /// its router two failures, which is what router health is built from.</summary>
    public sealed record LlmAttempt(string Router, string Outcome, int? Status, long QueueMs, long ProviderMs, long? RetryAfterMs = null);

    /// <summary>One model call as the ledger keeps it. Numbers only: no prompt or reply text.</summary>
    public sealed class LlmCallRecord
    {
        public long Id { get; set; }
        /// <summary>Unix milliseconds. Calls are bucketed by when they ended.</summary>
        public long StartedMs { get; set; }
        public long EndedMs { get; set; }

        public string Service { get; set; } = "Other";
        public string Lane { get; set; } = "default";
        public string? Subject { get; set; }
        public string? Operation { get; set; }
        public string? SessionId { get; set; }
        /// <summary>chat, tool, warm, stt, tts or local.</summary>
        public string Kind { get; set; } = "chat";
        public string? WorkClass { get; set; }

        public string Router { get; set; } = "";
        public string? RouterName { get; set; }
        /// <summary>The upstream that ran the model, when the router says (OpenRouter).</summary>
        public string? Provider { get; set; }
        public string? ModelRequested { get; set; }
        public string? ModelServed { get; set; }
        public bool Stream { get; set; }

        public string Outcome { get; set; } = LlmOutcome.Ok;
        public int? HttpStatus { get; set; }
        public string? FinishReason { get; set; }
        public int Attempts { get; set; }

        public long PromptTokens { get; set; }
        public long CachedTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public long CompletionTokens { get; set; }
        public long ReasoningTokens { get; set; }
        /// <summary>The provider reported a cache split, so a zero in <see cref="CachedTokens"/> is a real zero.</summary>
        public bool CacheMetrics { get; set; }

        public double? CostUsd { get; set; }
        public double? UpstreamCostUsd { get; set; }
        /// <summary>The cost the ledger books when the router reports none: tokens × the price book at the
        /// time of the call (zero on free and flat-fee routers). Filled by the writer.</summary>
        public double? EstimatedCostUsd { get; set; }
        /// <summary>per_token, flat or free: what the router charges, not what this call cost.</summary>
        public string Billing { get; set; } = "per_token";

        public long QueueMs { get; set; }
        public long ProviderMs { get; set; }
        public long TotalMs { get; set; }
        public long? TtftMs { get; set; }
        public long RequestBytes { get; set; }
        public int ToolCalls { get; set; }

        public LlmCallFlags Flags { get; set; }
        public string? FallbackFrom { get; set; }
        public string? CacheEpoch { get; set; }
        public int CacheEpochTurn { get; set; }
        public long RoutingRevision { get; set; }
        public string? Error { get; set; }
        public List<LlmAttempt>? AttemptLog { get; set; }

        // Kept only when text capture is on for the lane; written to the content store, never to `calls`.
        [Newtonsoft.Json.JsonIgnore] public string? CapturedRequest { get; set; }
        [Newtonsoft.Json.JsonIgnore] public string? CapturedResponse { get; set; }

        public string Model => string.IsNullOrWhiteSpace(ModelServed) ? (ModelRequested ?? "") : ModelServed!;
        public bool Has(LlmCallFlags flag) => (Flags & flag) != 0;
    }

    public enum LlmCallState
    {
        Starting,
        Queued,
        AwaitingProvider,
        Reasoning,
        Streaming,
        Retrying,
    }

    /// <summary>
    /// The live half of a call record: created when a call is dispatched, updated by the transport as
    /// it queues, sends, streams and retries, and turned into an <see cref="LlmCallRecord"/> when it
    /// ends. While it lives it is what the page lists as "in flight".
    /// </summary>
    public sealed class LlmCallTrace
    {
        private readonly object sync = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<LlmAttempt> attempts = new();
        private long stateSinceMs;
        private long queueMs;
        private long providerMs;
        private long ttftMs = -1;
        private long streamedChars;

        public LlmCallTrace(long id, LlmCaller caller, string kind)
        {
            Id = id;
            Caller = caller;
            Kind = kind;
            StartedUtc = DateTime.UtcNow;
        }

        public long Id { get; }
        public LlmCaller Caller { get; }
        public string Kind { get; }
        public DateTime StartedUtc { get; }
        public string? SessionId { get; set; }
        public string? WorkClass { get; set; }
        public bool Stream { get; set; }
        public string Router { get; private set; } = "";
        public string? RouterName { get; private set; }
        public string? Model { get; private set; }
        public string? ModelRequested { get; set; }
        public string Billing { get; private set; } = "per_token";
        public string? FallbackFrom { get; private set; }
        public LlmCallFlags Flags { get; set; }
        public long RoutingRevision { get; set; }
        public string? CacheEpoch { get; set; }
        public int CacheEpochTurn { get; set; }
        public long EstimatedPromptTokens { get; set; }
        /// <summary>The last response the transport got for this call, for its usage figures.</summary>
        public HFWrapper.HFLLMInferenceResponse? LastResponse { get; set; }
        /// <summary>Set when this lane's text is being kept; the transport then hands over the bodies.</summary>
        public bool CaptureContent { get; set; }
        public string? CapturedRequest { get; set; }
        public string? CapturedResponse { get; set; }

        public LlmCallState State { get; private set; } = LlmCallState.Starting;
        public long ElapsedMs => clock.ElapsedMilliseconds;
        public long StateElapsedMs { get { lock (sync) return clock.ElapsedMilliseconds - stateSinceMs; } }
        public long QueueMs => Interlocked.Read(ref queueMs);
        public long ProviderMs => Interlocked.Read(ref providerMs);
        public long? TtftMs { get { long v = Interlocked.Read(ref ttftMs); return v < 0 ? null : v; } }
        public long StreamedChars => Interlocked.Read(ref streamedChars);
        public int AttemptCount { get { lock (sync) return attempts.Count; } }

        public void SetRoute(string routerId, string? routerName, string? model, string billing)
        {
            lock (sync)
            {
                if (!string.IsNullOrEmpty(Router) && !string.Equals(Router, routerId, StringComparison.OrdinalIgnoreCase))
                {
                    FallbackFrom ??= Router;
                    Flags |= LlmCallFlags.Fallback;
                }
                Router = routerId;
                RouterName = routerName;
                Model = model;
                Billing = billing;
            }
        }

        public void SetState(LlmCallState next)
        {
            lock (sync)
            {
                if (State == next) return;
                State = next;
                stateSinceMs = clock.ElapsedMilliseconds;
            }
        }

        public void AddQueue(long ms) { if (ms > 0) Interlocked.Add(ref queueMs, ms); }

        public void NoteAttempt(string outcome, int? status, long attemptQueueMs, long attemptProviderMs, TimeSpan? retryAfter = null)
        {
            if (attemptProviderMs > 0) Interlocked.Add(ref providerMs, attemptProviderMs);
            lock (sync)
            {
                // A wedged retry loop must not grow a list without bound.
                if (attempts.Count < 64)
                    attempts.Add(new LlmAttempt(Router, outcome, status, Math.Max(0, attemptQueueMs), Math.Max(0, attemptProviderMs),
                        retryAfter is { } ra ? (long)ra.TotalMilliseconds : null));
            }
        }

        /// <summary>The first byte of model output (content, reasoning or a tool call) on a stream.</summary>
        public void NoteFirstToken()
            => Interlocked.CompareExchange(ref ttftMs, clock.ElapsedMilliseconds, -1);

        public void NoteStreamed(int chars) { if (chars > 0) Interlocked.Add(ref streamedChars, chars); }

        public List<LlmAttempt> SnapshotAttempts() { lock (sync) return attempts.ToList(); }

        /// <summary>Builds the finished record. Usage comes from the response, or is left at zero.</summary>
        public LlmCallRecord Finish(string outcome, HFWrapper.HFLLMInferenceResponse? response, Exception? error = null)
        {
            long ended = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long total = clock.ElapsedMilliseconds;
            var usage = response?.usage;
            var message = response?.choices is { Count: > 0 } choices ? choices[0] : null;
            var list = SnapshotAttempts();
            var record = new LlmCallRecord
            {
                Id = Id,
                StartedMs = ended - total,
                EndedMs = ended,
                Service = Caller.Service,
                Lane = Caller.Lane,
                Subject = Clip(Caller.Subject, 120),
                Operation = Clip(Caller.Operation, 120),
                SessionId = Clip(SessionId, 200),
                Kind = Kind,
                WorkClass = WorkClass,
                Router = Router,
                RouterName = RouterName,
                Provider = Clip(KliveLLM.SelectedOpenRouterProvider(response?.openrouter_metadata), 80),
                ModelRequested = Clip(ModelRequested ?? Model, 200),
                ModelServed = Clip(string.IsNullOrWhiteSpace(response?.model) ? Model : response!.model, 200),
                Stream = Stream,
                Outcome = outcome,
                HttpStatus = (error as HttpRequestException)?.StatusCode is { } status ? (int)status
                    : list.LastOrDefault()?.Status,
                FinishReason = Clip(message?.finish_reason, 40),
                Attempts = Math.Max(list.Count, outcome == LlmOutcome.Blocked ? 0 : 1),
                PromptTokens = Math.Max(0, usage?.prompt_tokens ?? 0),
                CachedTokens = Math.Max(0, usage?.prompt_tokens_details?.cached_tokens ?? 0),
                CacheWriteTokens = Math.Max(0, usage?.prompt_tokens_details?.cache_write_tokens ?? 0),
                CompletionTokens = Math.Max(0, usage?.completion_tokens ?? 0),
                ReasoningTokens = Math.Max(0, usage?.completion_tokens_details?.reasoning_tokens ?? 0),
                CacheMetrics = usage?.prompt_tokens_details?.HasCacheReadMetrics == true,
                CostUsd = Finite(usage?.cost),
                UpstreamCostUsd = Finite(usage?.cost_details?.upstream_inference_cost),
                Billing = Billing,
                QueueMs = QueueMs,
                ProviderMs = ProviderMs,
                TotalMs = total,
                TtftMs = TtftMs,
                RequestBytes = Math.Max(0, response?.request_bytes ?? 0),
                ToolCalls = message?.message?.tool_calls?.Count ?? 0,
                Flags = Flags,
                FallbackFrom = FallbackFrom,
                CacheEpoch = CacheEpoch,
                CacheEpochTurn = CacheEpochTurn,
                RoutingRevision = RoutingRevision,
                Error = error == null ? null : Clip(error.Message, 600),
                AttemptLog = list.Count > 0 ? list : null,
            };
            if (record.CachedTokens > record.PromptTokens) record.CachedTokens = record.PromptTokens;
            if (CacheEpochTurn <= 1 && CacheEpoch != null) record.Flags |= LlmCallFlags.ColdStart;
            if (string.Equals(record.FinishReason, "length", StringComparison.OrdinalIgnoreCase)) record.Flags |= LlmCallFlags.Truncated;
            if (error is KliveLLMStreamStalledException) record.Flags |= LlmCallFlags.Stalled;
            if (CaptureContent && (CapturedRequest != null || CapturedResponse != null))
            {
                record.CapturedRequest = CapturedRequest;
                record.CapturedResponse = CapturedResponse;
                record.Flags |= LlmCallFlags.ContentKept;
            }
            return record;
        }

        private static double? Finite(double? value) => value is { } v && double.IsFinite(v) && v >= 0 ? v : null;

        internal static string? Clip(string? value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string clean = value.Trim();
            return clean.Length <= max ? clean : clean[..max];
        }
    }
}
