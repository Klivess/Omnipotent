namespace Omnipotent.Services.KliveLLM
{
    /// <summary>
    /// The rules that keep a provider's PREFIX CACHE able to serve our requests.
    ///
    /// Every serving stack we talk to (AIRouter/vLLM/SGLang-style radix caches, and Anthropic's
    /// explicit cache_control blocks alike) can only reuse a run of tokens that is byte-identical
    /// from the very START of the prompt. The match ends at the first differing byte, and everything
    /// after it is re-prefilled from scratch. Prefill is the expensive half of serving a request, so
    /// a single byte changed near the head of a 55K-token prompt costs almost exactly as much as
    /// having no cache at all.
    ///
    /// That makes cache behaviour a STRUCTURAL property of how we assemble a prompt, not a tuning
    /// knob. Two rules follow, and both are load-bearing:
    ///
    ///   1. STABLE FIRST, VOLATILE LAST. Within every prompt we build — system prompt, wake seed,
    ///      each block inside a seed — text that is identical across requests goes at the top and
    ///      text that changes every request goes at the bottom. A wall-clock line, a relative age
    ///      ("3h ago"), or an agent id placed in the opening sentence invalidates the whole prompt
    ///      behind it, which is why <see cref="KliveLLM.CacheBreakpointMarker"/> exists and why the
    ///      agent prompts put identity/parameters in a tail section.
    ///
    ///   2. NEVER REWRITE WHAT YOU HAVE ALREADY SENT — unless you rewrite enough of it to be worth
    ///      the invalidation. Our context-hygiene passes (stubbing old tool results, flattening old
    ///      screenshots, compacting the middle of a transcript) all EDIT messages that earlier
    ///      requests in the same session already sent. Done once per turn, each edit moves the first
    ///      differing byte back into the middle of the transcript on EVERY turn, so the tail after it
    ///      is re-prefilled every time and the hit rate pins itself at a low constant no matter how
    ///      long the conversation gets. Done in batches with hysteresis, the same total work happens
    ///      but the intervening turns extend a byte-identical prefix and are served from cache.
    ///
    /// This class owns rule 2's arithmetic so the pruning and compaction paths share one policy.
    /// </summary>
    internal static class PromptPrefixStability
    {
        /// <summary>
        /// How many old tool results must be waiting to be stubbed before we rewrite the transcript.
        ///
        /// One full retention window: the session carries up to twice what it retains, then prunes
        /// back to the window in a single pass. Measured over a simulated 120-turn wake (mean share
        /// of each request that the previous request's cache could serve, against the peak prompt
        /// size the choice costs):
        ///
        ///     batch  1 → 53.9%   (the old one-per-turn behaviour)
        ///     batch  4 → 85.8%   +8%  peak
        ///     batch  8 → 91.3%   +19% peak
        ///     batch 16 → 94.4%   +40% peak      ← one full window at the default retention of 16
        ///     batch 32 → 95.9%   +84% peak
        ///
        /// Past a full window the curve flattens while the retained transcript keeps growing, so this
        /// is the knee. The extra retained output is nearly free on the wire — those tokens are the
        /// ones being served FROM cache — but it is not free in the model's attention, which is what
        /// caps this rather than bandwidth.
        /// </summary>
        internal static int ToolResultPruneBatch(int keepRecent) =>
            Math.Clamp(keepRecent, 2, 32);

        /// <summary>
        /// The same idea for screenshots/audio clips, held to a tighter cap: media parts are far
        /// bigger per message than text, so carrying spare ones between prunes costs real window.
        /// </summary>
        internal static int MediaPruneBatch(int keepRecent) =>
            Math.Clamp(keepRecent / 2, 2, 4);

        /// <summary>
        /// The size compaction should aim for, given the size that TRIGGERS it.
        ///
        /// Compaction rewrites the middle of the transcript, so it is the single most destructive
        /// thing we do to a cached prefix. Compacting to just under the trigger (which is what
        /// "keep the largest tail that fits" naturally does) leaves no headroom at all: the next
        /// turn's tool result pushes the session straight back over the line and it compacts again,
        /// so a long wake re-writes its own history on essentially every turn.
        ///
        /// Aiming below the trigger buys back that headroom. The gap is the number of turns the
        /// session can grow before the next rewrite, and it is deliberately generous — a wake that
        /// compacts once every dozen turns is both cheaper and steadier than one that compacts
        /// constantly, and the model sees a stable transcript for longer either way.
        /// </summary>
        internal const double CompactionTargetFraction = 0.72;

        /// <summary>The post-compaction size to aim for. Always at least one token below the trigger.</summary>
        internal static int CompactionTarget(int aboveTokens) =>
            aboveTokens <= 1 ? aboveTokens : Math.Max(1, (int)(aboveTokens * CompactionTargetFraction));
    }

    /// <summary>
    /// Measures how much of what we send is actually being served from the provider's prefix cache.
    ///
    /// This exists because the failure it watches for is INVISIBLE from inside the process. A prompt
    /// assembled so that its cache never matches behaves identically to one that always matches:
    /// same responses, same tool calls, same tests passing. The only signals are on the provider's
    /// side — their prefill load, and our own time-to-first-token. So the first we knew of a
    /// six-hour regression was the router suspending the key over it.
    ///
    /// Every OpenAI-compatible response already carries the answer in
    /// <c>usage.prompt_tokens_details.cached_tokens</c>; nothing was reading it. This keeps a rolling
    /// hour of those figures and says so in the service log when the rate falls through the floor,
    /// so a prompt-assembly change that quietly destroys the cache is noticed here rather than
    /// downstream.
    /// </summary>
    internal sealed class PrefixCacheMeter
    {
        /// <summary>One meter per process: every service sharing the router key shares the window.</summary>
        internal static readonly PrefixCacheMeter Shared = new();

        private static readonly TimeSpan Window = TimeSpan.FromHours(1);

        /// <summary>Enough requests that a low rate means the prompt SHAPE is wrong, not that a few
        /// sessions happened to start cold. Fresh sessions legitimately match little.</summary>
        private const int MinSamplesToJudge = 60;

        /// <summary>A healthy agentic tool loop extends a byte-identical prefix on almost every turn
        /// and runs well above this. Sustained figures below it mean something is being rewritten
        /// between requests, or per-request text has moved to the head of a prompt.</summary>
        private const double FloorHitRate = 0.60;

        /// <summary>Long enough that the log says this once per incident, not once per request.</summary>
        private static readonly TimeSpan WarnCooldown = TimeSpan.FromMinutes(30);

        private readonly object sync = new();
        private readonly Queue<(DateTime At, long Prompt, long Cached)> samples = new();
        private long windowPrompt;
        private long windowCached;
        private DateTime lastWarnedUtc = DateTime.MinValue;

        internal readonly record struct Snapshot(int Requests, long PromptTokens, long CachedTokens)
        {
            internal double HitRate => PromptTokens > 0 ? (double)CachedTokens / PromptTokens : 0d;
            internal long UncachedTokens => Math.Max(0, PromptTokens - CachedTokens);
        }

        internal Snapshot Describe(DateTime? nowUtc = null)
        {
            lock (sync)
            {
                Prune(nowUtc ?? DateTime.UtcNow);
                return new Snapshot(samples.Count, windowPrompt, windowCached);
            }
        }

        /// <summary>
        /// Records one request's prompt/cached split. Returns a line to log when the rolling hit rate
        /// has fallen through the floor over a meaningful sample (and the cooldown has elapsed),
        /// otherwise null — so the caller stays a two-liner and the policy lives here.
        /// </summary>
        internal string? Record(long promptTokens, long cachedTokens, DateTime? nowUtc = null)
        {
            if (promptTokens <= 0) return null;
            if (cachedTokens < 0) cachedTokens = 0;
            if (cachedTokens > promptTokens) cachedTokens = promptTokens;

            DateTime now = nowUtc ?? DateTime.UtcNow;
            Snapshot snapshot;
            lock (sync)
            {
                samples.Enqueue((now, promptTokens, cachedTokens));
                windowPrompt += promptTokens;
                windowCached += cachedTokens;
                Prune(now);

                snapshot = new Snapshot(samples.Count, windowPrompt, windowCached);
                if (snapshot.Requests < MinSamplesToJudge) return null;
                if (snapshot.HitRate >= FloorHitRate) return null;
                if (now - lastWarnedUtc < WarnCooldown) return null;
                lastWarnedUtc = now;
            }

            return $"Prefix cache is being missed: only {snapshot.HitRate:P0} of prompt tokens were served from " +
                   $"cache across the last {snapshot.Requests} requests ({snapshot.UncachedTokens:N0} of " +
                   $"{snapshot.PromptTokens:N0} tokens re-prefilled). Check request prefix stability, cold starts, provider routing " +
                   "and cache expiry. See PromptPrefixStability.";
        }

        private void Prune(DateTime now)
        {
            while (samples.Count > 0 && now - samples.Peek().At > Window)
            {
                var old = samples.Dequeue();
                windowPrompt -= old.Prompt;
                windowCached -= old.Cached;
            }
            if (samples.Count != 0) return;
            windowPrompt = 0;
            windowCached = 0;
        }
    }
}
