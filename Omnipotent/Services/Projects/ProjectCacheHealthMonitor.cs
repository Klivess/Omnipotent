using System.Globalization;
using System.Text;
using Newtonsoft.Json;

namespace Omnipotent.Services.Projects;

/// <summary>
/// Tunables for the fleet cache-health kill switch. Every one of these is an OmniSetting read at
/// startup (see <see cref="Projects.LoadCacheHealthOptionsAsync"/>) rather than a constant, because
/// the right numbers depend on the router in front of the fleet and on how many agents are awake —
/// both of which change without a code change.
/// </summary>
public sealed class ProjectCacheHealthOptions
{
    /// <summary>Master switch. Off measures and reports but never halts.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The trailing window the rate is measured over. Twenty minutes rather than an hour: an hour
    /// of a collapsed prefix cache is an hour of full prefill already paid for, and the AIRouter
    /// suspension that motivated this arrived after roughly six of them.
    /// </summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>The weighted hit rate, in percent, at or above which the fleet is allowed to run.</summary>
    public double MinimumWeightedHitRatePct { get; set; } = 80;

    /// <summary>
    /// The corroborating floor, on expiry-adjusted reusable-prefix efficiency. The weighted rate
    /// alone cannot separate "prefix assembly is broken" from "the agents were away longer than the
    /// cache lives", and the second is neither a fault nor fixable by stopping the fleet.
    ///
    /// Both floors must be breached to halt. That is not belt-and-braces: over the live journal the
    /// fleet's ordinary weighted rate is 80–92% per project, so the weighted floor sits ON the
    /// normal operating point and a trip was a matter of when, not whether — 2026-09-16 spent four
    /// hours halted at 78.3% with a perfectly healthy prefix. Efficiency over the same window was
    /// 99.96% once expiries were excluded, and the 2026-08-28 collapse this mechanism exists for ran
    /// at ~30% with continuations seconds apart, so it clears this floor by a wide margin.
    /// </summary>
    public double MinimumReusablePrefixEfficiencyPct { get; set; } = 90;

    /// <summary>
    /// How long a prefix is assumed to survive at the provider. Continuations resumed after longer
    /// than this are counted as expired and excluded from the efficiency figure.
    ///
    /// Deliberately a fixed setting rather than <see cref="KliveLLM.PrefixSurvivalMeter"/>'s measured
    /// T_eff, even though that number is better. A real cache collapse drives the measured lifetime
    /// DOWN, which would exclude more and more pairs as the fault worsened — the detector would go
    /// blind exactly when it is needed. Five minutes is where the live journal puts the knee:
    /// continuations under three minutes reuse 99.96% and none miss outright; losses start past five.
    /// </summary>
    public TimeSpan AssumedPrefixLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Floor on the population of the efficiency figure. Below this the halt is WITHHELD rather than
    /// falling back to the weighted rate alone — falling back would restore the false positive in
    /// exactly the thin windows where it is most likely. A genuine collapse is not hidden by this:
    /// agents keep taking turns while it runs, so the continuations are there, they simply all miss.
    /// </summary>
    public int MinimumReusablePrefixSamples { get; set; } = 10;

    /// <summary>
    /// Floor on the MEASURED population before the rule may fire. Not a comfort blanket: the first
    /// turn of any conversation is a genuine 0% by construction, so a handful of cold starts is a
    /// perfectly healthy fleet that happens to look terrible. Halting on those would be the
    /// false positive that gets the whole mechanism switched off.
    /// </summary>
    public int MinimumMeasuredRequests { get; set; } = 20;

    /// <summary>Token floor alongside the request floor — twenty tiny requests say nothing about
    /// prefill spend, which is the thing actually being protected.</summary>
    public long MinimumMeasuredPromptTokens { get; set; } = 250_000;

    /// <summary>
    /// How much wall-clock the measured samples must span. A burst of conversations all starting at
    /// once (a restart, an unhalt, a broadcast) clears both floors above within seconds while being
    /// nothing but cold starts; requiring elapsed time is what separates that from a real collapse.
    /// </summary>
    public TimeSpan MinimumObservationSpan { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Hard ceiling on retained samples, so a fast provider cannot grow the window without
    /// bound. Far above a realistic window: three AIRouter slots produce well under 300 an hour.</summary>
    public int MaxRetainedSamples { get; set; } = 5_000;
}

/// <summary>One grouping line of the report — the same arithmetic as the fleet total, by key.</summary>
public sealed class ProjectCacheHealthGroup
{
    public string Key { get; set; } = "";
    public int Requests { get; set; }
    public int ZeroHitRequests { get; set; }
    public long PromptTokens { get; set; }
    public long CachedTokens { get; set; }
    public long UncachedTokens { get; set; }
    public double WeightedHitRatePct { get; set; }
}

/// <summary>
/// The measured state of the trailing window: what the rate is, whether it is low enough to halt
/// on, and — when it is not — exactly which precondition is holding the trigger back. That last
/// part exists so "why didn't it fire?" is answerable from the status route instead of by reading
/// this file.
/// </summary>
public sealed class ProjectCacheHealthVerdict
{
    public DateTime EvaluatedAt { get; set; }
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }
    public double ThresholdPct { get; set; }

    /// <summary>The trigger metric: total cached prompt tokens over total prompt tokens, so a
    /// 60K-token turn counts for sixty times what a 1K-token turn does. An unweighted mean would
    /// let a flock of small cheap requests hide exactly the large prefills that cost money.</summary>
    public double WeightedHitRatePct { get; set; }

    /// <summary>The per-request mean, carried for contrast only. A large gap between this and the
    /// weighted figure means the misses are concentrated in the big prompts.</summary>
    public double UnweightedHitRatePct { get; set; }

    public int MeasuredRequests { get; set; }
    /// <summary>Requests whose provider reported no cache metrics, or which predate the current
    /// prefix contract. Excluded from the rate — a missing measurement is not a miss, and counting
    /// it as one would halt the fleet every time a non-reporting route was used.</summary>
    public int UnmeasuredRequests { get; set; }
    public int ZeroHitRequests { get; set; }
    public long PromptTokens { get; set; }
    public long CachedTokens { get; set; }
    public long UncachedTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public TimeSpan ObservationSpan { get; set; }
    public int ReusablePrefixSamples { get; set; }
    public long ReusablePrefixTokens { get; set; }
    public long ReusedPrefixTokens { get; set; }
    public double ReusablePrefixEfficiencyPct { get; set; }
    public double ReusablePrefixThresholdPct { get; set; }

    /// <summary>Continuations excluded from the efficiency figure because the agent was away longer
    /// than the assumed prefix lifetime. Not a fault — but the prefill they re-paid is the fleet's
    /// real cache cost, so it is reported rather than silently dropped.</summary>
    public int ExpiredPrefixSamples { get; set; }
    public long ExpiredPrefixTokens { get; set; }

    public bool BelowThreshold { get; set; }
    /// <summary>Whether the corroborating efficiency floor was breached too.</summary>
    public bool PrefixEfficiencyBelowThreshold { get; set; }
    public bool ShouldHalt { get; set; }
    /// <summary>Plain-language statement of the decision, in both directions.</summary>
    public string Summary { get; set; } = "";
}

/// <summary>The durable record of a trip. Survives a restart so a halted fleet stays halted.</summary>
public sealed class ProjectCacheHaltState
{
    public bool Engaged { get; set; }
    public DateTime? EngagedAt { get; set; }
    public string Reason { get; set; } = "";
    public double ObservedHitRatePct { get; set; }
    public double ThresholdPct { get; set; }
    public int WindowMinutes { get; set; }
    public int MeasuredRequests { get; set; }
    public long PromptTokens { get; set; }
    public long CachedTokens { get; set; }
    public DateTime? WindowStart { get; set; }
    public DateTime? WindowEnd { get; set; }
    public List<string> HaltedProjectIDs { get; set; } = new();
    public string? ReportPath { get; set; }
    public string? RequestLogPath { get; set; }
    public bool AlertDelivered { get; set; }
    public string? AlertError { get; set; }
    public DateTime? ClearedAt { get; set; }
    public string? ClearedBy { get; set; }
}

/// <summary>
/// Fleet-wide prompt-cache kill switch.
///
/// Every Projects LLM turn is already funnelled through
/// <see cref="ProjectBudgetLedger.RecordTokenSpendAsync"/>, which reports the provider's
/// <c>cached_tokens</c> alongside the spend. This watches that stream, keeps a trailing window of
/// the raw request records, and when the token-weighted hit rate across the whole fleet falls below
/// the configured floor AND continuations resumed inside the cache's lifetime are missing too, it
/// latches — after which no agent is admitted to another model call until
/// Klives clears it.
///
/// It exists because a cache collapse is silent: every answer is still correct, every test still
/// passes, and the only symptom is the prefill bill. The 2026-08-28 incident ran six hours at 30%
/// and reached Klives as a suspension email from the router's owner
/// (see PromptPrefixStability.cs). The point of halting rather than merely logging is that a
/// warning nobody is awake to read costs the same as no warning at all.
///
/// The second condition is not redundancy, it is what keeps the first usable. A fleet doing cold
/// starts and long idle gaps has a low weighted rate by construction — 2026-09-16 stopped five
/// projects for four hours over 78.3% while every warm continuation in the window was hitting — and
/// no amount of halting makes that work cheaper. See
/// <see cref="ProjectCacheHealthOptions.MinimumReusablePrefixEfficiencyPct"/>.
///
/// The measurement is deliberately the same one the analytics page shows
/// (<see cref="ProjectPromptCacheAnalytics.IsMeasuredSample"/>): a number that stops the fleet and
/// a number on a dashboard must never be able to disagree.
/// </summary>
public sealed class ProjectCacheHealthMonitor
{
    private readonly object sync = new();
    private readonly List<ProjectTokenUsageRecord> samples = new();
    private readonly Func<DateTime> clock;
    private readonly Action<string> log;
    private readonly string stateDirectory;

    private ProjectCacheHealthOptions options;
    private ProjectCacheHaltState state = new();
    // Latch and dispatch are separate: the latch closes synchronously inside the lock so a second
    // concurrent turn can never trip a second halt, while the (slow, IO-bound) halt action runs off
    // the caller's thread. That caller is a live wake's token-accounting path.
    private int haltDispatched;

    /// <summary>
    /// Invoked exactly once per trip, off the observing thread: halts the fleet, writes the report
    /// and alerts Klives. Set by <see cref="Projects"/>; null in tests that only exercise the maths.
    /// </summary>
    public Func<ProjectCacheHealthVerdict, IReadOnlyList<ProjectTokenUsageRecord>, Task>? HaltAction { get; set; }

    public ProjectCacheHealthMonitor(
        string stateDirectory,
        Action<string>? log = null,
        Func<DateTime>? clock = null,
        ProjectCacheHealthOptions? options = null)
    {
        this.stateDirectory = stateDirectory;
        this.log = log ?? (_ => { });
        this.clock = clock ?? (() => DateTime.UtcNow);
        this.options = options ?? new ProjectCacheHealthOptions();
        Directory.CreateDirectory(stateDirectory);
        state = LoadState();
        if (state.Engaged) haltDispatched = 1; // a restart must not re-alert for a trip already delivered
    }

    public ProjectCacheHealthOptions Options
    {
        get { lock (sync) return options; }
    }

    /// <summary>Applies settings resolved after construction. Never clears an engaged latch: a
    /// configuration reload is not a decision to resume.</summary>
    public void Configure(ProjectCacheHealthOptions next)
    {
        if (next == null) return;
        lock (sync)
        {
            options = next;
            TrimLocked(clock());
        }
    }

    public ProjectCacheHaltState GetState()
    {
        lock (sync) return Clone(state);
    }

    public bool IsHalted
    {
        get { lock (sync) return state.Engaged; }
    }

    /// <summary>
    /// The reason every LLM turn is being refused, or null when the fleet may run. Read on the
    /// admission path of every model call, so it is a plain field read behind the same lock rather
    /// than anything that touches disk.
    /// </summary>
    public string? AdmissionRefusal()
    {
        lock (sync)
        {
            if (!state.Engaged) return null;
            return $"the Projects fleet is halted on prompt-cache health "
                + $"({state.ObservedHitRatePct:0.0}% weighted cache hit rate over {state.WindowMinutes} minutes, "
                + $"floor {state.ThresholdPct:0.#}%). Klives must clear the halt before any agent sends another request.";
        }
    }

    /// <summary>
    /// Ingests one completed model turn. Called from the ledger for every Projects LLM request —
    /// Commander, sub-agent, council and utility alike, which is what makes the rate a fleet rate.
    /// </summary>
    public void Observe(ProjectTokenUsageRecord? record)
    {
        if (record == null) return;
        // Reconciliation rows re-book cost for a request already counted; they carry no tokens of
        // their own and would otherwise dilute the window with phantom samples.
        if (string.Equals(record.RecordKind, "cost-adjustment", StringComparison.OrdinalIgnoreCase)) return;
        if (record.PromptTokens <= 0) return;

        ProjectCacheHealthVerdict verdict;
        List<ProjectTokenUsageRecord> snapshot;
        lock (sync)
        {
            samples.Add(record);
            DateTime now = clock();
            TrimLocked(now);
            if (state.Engaged || !options.Enabled) return;

            verdict = EvaluateLocked(now);
            if (!verdict.ShouldHalt) return;
            if (Interlocked.Exchange(ref haltDispatched, 1) != 0) return;

            snapshot = samples.ToList();
            state = new ProjectCacheHaltState
            {
                Engaged = true,
                EngagedAt = now,
                Reason = verdict.Summary,
                ObservedHitRatePct = verdict.WeightedHitRatePct,
                ThresholdPct = verdict.ThresholdPct,
                WindowMinutes = (int)Math.Round(options.Window.TotalMinutes),
                MeasuredRequests = verdict.MeasuredRequests,
                PromptTokens = verdict.PromptTokens,
                CachedTokens = verdict.CachedTokens,
                WindowStart = verdict.WindowStart,
                WindowEnd = verdict.WindowEnd,
            };
            SaveStateLocked();
        }

        log($"Projects cache health: HALTING the fleet — {verdict.Summary}");
        var action = HaltAction;
        if (action == null) return;
        // Detached deliberately: the caller is inside a live wake's spend accounting, and the halt
        // action cancels that very wake.
        _ = Task.Run(async () =>
        {
            try { await action(verdict, snapshot); }
            catch (Exception ex) { log($"Projects cache health: halt action failed: {ex.Message}"); }
        });
    }

    /// <summary>Records what the halt action achieved, so the status route and a later restart can
    /// both say whether Klives was actually told.</summary>
    public void NoteHaltOutcome(
        IReadOnlyList<string> haltedProjectIDs,
        string? reportPath,
        string? requestLogPath,
        bool alertDelivered,
        string? alertError)
    {
        lock (sync)
        {
            if (!state.Engaged) return;
            state.HaltedProjectIDs = haltedProjectIDs?.ToList() ?? new List<string>();
            state.ReportPath = reportPath;
            state.RequestLogPath = requestLogPath;
            state.AlertDelivered = alertDelivered;
            state.AlertError = alertError;
            SaveStateLocked();
        }
    }

    /// <summary>
    /// Releases the latch. The trailing window is dropped with it: resuming against the samples
    /// that caused the trip would re-halt on the next request, which would read as the clear having
    /// silently failed.
    /// </summary>
    public bool Clear(string clearedBy)
    {
        lock (sync)
        {
            if (!state.Engaged) return false;
            state.Engaged = false;
            state.ClearedAt = clock();
            state.ClearedBy = string.IsNullOrWhiteSpace(clearedBy) ? "klives" : clearedBy.Trim();
            samples.Clear();
            Interlocked.Exchange(ref haltDispatched, 0);
            SaveStateLocked();
            return true;
        }
    }

    /// <summary>The current window, for the status route. Never halts — reading is not deciding.</summary>
    public ProjectCacheHealthVerdict Describe()
    {
        lock (sync)
        {
            DateTime now = clock();
            TrimLocked(now);
            return EvaluateLocked(now);
        }
    }

    /// <summary>Every retained request in the trailing window, newest last. This is the raw
    /// material of the alert attachment.</summary>
    public IReadOnlyList<ProjectTokenUsageRecord> WindowSamples()
    {
        lock (sync)
        {
            TrimLocked(clock());
            return samples.ToList();
        }
    }

    private void TrimLocked(DateTime now)
    {
        DateTime cutoff = now - options.Window;
        samples.RemoveAll(sample => sample.OccurredAt.ToUniversalTime() < cutoff);
        int excess = samples.Count - Math.Max(1, options.MaxRetainedSamples);
        if (excess > 0) samples.RemoveRange(0, excess);
    }

    private ProjectCacheHealthVerdict EvaluateLocked(DateTime now)
    {
        var measured = samples.Where(ProjectPromptCacheAnalytics.IsMeasuredSample).ToList();
        var verdict = new ProjectCacheHealthVerdict
        {
            EvaluatedAt = now,
            WindowStart = now - options.Window,
            WindowEnd = now,
            ThresholdPct = options.MinimumWeightedHitRatePct,
            MeasuredRequests = measured.Count,
            UnmeasuredRequests = samples.Count - measured.Count,
        };

        double perRequestTotal = 0;
        foreach (var record in measured)
        {
            long prompt = Math.Max(0, record.PromptTokens);
            long cached = Math.Clamp(record.CachedPromptTokens, 0, prompt);
            verdict.PromptTokens += prompt;
            verdict.CachedTokens += cached;
            verdict.UncachedTokens += prompt - cached;
            verdict.CacheWriteTokens += Math.Max(0, record.CacheWritePromptTokens);
            if (cached == 0) verdict.ZeroHitRequests++;
            perRequestTotal += prompt > 0 ? (double)cached / prompt : 0;
        }

        verdict.WeightedHitRatePct = Percent(verdict.CachedTokens, verdict.PromptTokens);
        verdict.UnweightedHitRatePct = measured.Count > 0
            ? Math.Round(perRequestTotal * 100.0 / measured.Count, 1)
            : 0;

        var reusable = ProjectPromptCacheAnalytics.MeasureReusablePrefix(
            measured, options.AssumedPrefixLifetime);
        verdict.ReusablePrefixSamples = reusable.Samples;
        verdict.ReusablePrefixTokens = reusable.ReusableTokens;
        verdict.ReusedPrefixTokens = reusable.ReusedTokens;
        verdict.ReusablePrefixEfficiencyPct = Percent(reusable.ReusedTokens, reusable.ReusableTokens);
        verdict.ReusablePrefixThresholdPct = options.MinimumReusablePrefixEfficiencyPct;
        verdict.ExpiredPrefixSamples = reusable.ExpiredSamples;
        verdict.ExpiredPrefixTokens = reusable.ExpiredTokens;

        if (measured.Count > 0)
        {
            DateTime first = measured.Min(record => record.OccurredAt.ToUniversalTime());
            DateTime last = measured.Max(record => record.OccurredAt.ToUniversalTime());
            verdict.ObservationSpan = last - first;
        }

        verdict.BelowThreshold = verdict.PromptTokens > 0
            && verdict.WeightedHitRatePct < options.MinimumWeightedHitRatePct;
        verdict.PrefixEfficiencyBelowThreshold = reusable.ReusableTokens > 0
            && verdict.ReusablePrefixEfficiencyPct < options.MinimumReusablePrefixEfficiencyPct;

        string? withheld =
            !options.Enabled ? "the fleet cache halt is disabled"
            : measured.Count < options.MinimumMeasuredRequests
                ? $"only {measured.Count} of the required {options.MinimumMeasuredRequests} measured requests"
            : verdict.PromptTokens < options.MinimumMeasuredPromptTokens
                ? $"only {verdict.PromptTokens:N0} of the required {options.MinimumMeasuredPromptTokens:N0} measured prompt tokens"
            : verdict.ObservationSpan < options.MinimumObservationSpan
                ? $"measured requests span only {verdict.ObservationSpan.TotalMinutes:0.#} of the required {options.MinimumObservationSpan.TotalMinutes:0.#} minutes"
            : reusable.Samples < options.MinimumReusablePrefixSamples
                ? $"only {reusable.Samples} of the required {options.MinimumReusablePrefixSamples} live continuations to judge prefix health by"
                  + (reusable.ExpiredSamples > 0
                      ? $" ({reusable.ExpiredSamples:N0} more were excluded as expired)" : "")
            // The whole point of the second floor: a low weighted rate with healthy efficiency is a
            // fleet paying for cold starts and expiries, which halting does not fix and cannot.
            : !verdict.PrefixEfficiencyBelowThreshold
                ? $"reusable-prefix efficiency is {verdict.ReusablePrefixEfficiencyPct:0.0}% over "
                  + $"{reusable.Samples:N0} live continuations, at or above the "
                  + $"{options.MinimumReusablePrefixEfficiencyPct:0.#}% floor — the prefix is intact and the "
                  + $"shortfall is cold starts and {reusable.ExpiredSamples:N0} expired continuations"
            : null;

        verdict.ShouldHalt = verdict.BelowThreshold && withheld == null;

        string window = $"{options.Window.TotalMinutes:0.#}-minute window";
        verdict.Summary = verdict.ShouldHalt
            ? $"Reusable-prefix efficiency is {verdict.ReusablePrefixEfficiencyPct:0.0}% over "
              + $"{verdict.ReusablePrefixSamples:N0} live continuations, below the "
              + $"{options.MinimumReusablePrefixEfficiencyPct:0.#}% floor, and the weighted hit rate is "
              + $"{verdict.WeightedHitRatePct:0.0}% over the {window}, below the "
              + $"{options.MinimumWeightedHitRatePct:0.#}% floor "
              + $"({verdict.MeasuredRequests:N0} measured requests, {verdict.PromptTokens:N0} prompt tokens, "
              + $"{verdict.UncachedTokens:N0} of them uncached). Warm prefixes are being lost, not merely "
              + $"expiring: continuations sent inside the {options.AssumedPrefixLifetime.TotalMinutes:0.#}-minute "
              + $"assumed prefix lifetime are missing."
            : verdict.BelowThreshold
                ? $"Weighted hit rate is {verdict.WeightedHitRatePct:0.0}% over the {window}, below the "
                  + $"{options.MinimumWeightedHitRatePct:0.#}% floor, but the halt is withheld: {withheld}."
                : verdict.MeasuredRequests == 0
                    ? $"No measured requests in the {window}."
                    : $"Weighted hit rate is {verdict.WeightedHitRatePct:0.0}% over the {window}, at or above the "
                      + $"{options.MinimumWeightedHitRatePct:0.#}% floor ({verdict.MeasuredRequests:N0} measured requests).";
        return verdict;
    }

    // ── grouping + report text ──

    /// <summary>
    /// Groups the window by one attribution key. The alert carries several of these because "the
    /// rate dropped" is not actionable on its own — whether it dropped on one project, one model or
    /// everything at once points at completely different causes.
    /// </summary>
    public static List<ProjectCacheHealthGroup> Group(
        IEnumerable<ProjectTokenUsageRecord> records,
        Func<ProjectTokenUsageRecord, string> key)
    {
        return records
            .Where(ProjectPromptCacheAnalytics.IsMeasuredSample)
            .GroupBy(record => Clean(key(record)), StringComparer.Ordinal)
            .Select(group =>
            {
                var line = new ProjectCacheHealthGroup { Key = group.Key };
                foreach (var record in group)
                {
                    long prompt = Math.Max(0, record.PromptTokens);
                    long cached = Math.Clamp(record.CachedPromptTokens, 0, prompt);
                    line.Requests++;
                    if (cached == 0) line.ZeroHitRequests++;
                    line.PromptTokens += prompt;
                    line.CachedTokens += cached;
                    line.UncachedTokens += prompt - cached;
                }
                line.WeightedHitRatePct = Percent(line.CachedTokens, line.PromptTokens);
                return line;
            })
            // Worst first by uncached tokens rather than by rate: the aim is to name what is
            // actually costing prefill, not the smallest sample with the ugliest percentage.
            .OrderByDescending(line => line.UncachedTokens)
            .ThenBy(line => line.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The human-readable half of the alert: the verdict, the arithmetic behind it, every
    /// attribution breakdown, and a tail of individual requests. The machine-readable half is the
    /// JSONL written next to it, which carries every field of every request.
    /// </summary>
    public static string BuildReport(
        ProjectCacheHealthVerdict verdict,
        IReadOnlyList<ProjectTokenUsageRecord> window,
        ProjectCacheHealthOptions options,
        IReadOnlyList<string> haltedProjectIDs)
    {
        var measured = window.Where(ProjectPromptCacheAnalytics.IsMeasuredSample).ToList();
        var text = new StringBuilder();
        text.AppendLine("# Projects fleet halted — prompt-cache hit rate below floor");
        text.AppendLine();
        text.AppendLine($"Generated: {Stamp(verdict.EvaluatedAt)}");
        text.AppendLine($"Window: {Stamp(verdict.WindowStart)} → {Stamp(verdict.WindowEnd)} "
            + $"({options.Window.TotalMinutes:0.#} minutes)");
        text.AppendLine($"Trigger: reusable-prefix efficiency < {options.MinimumReusablePrefixEfficiencyPct:0.#}% "
            + $"AND weighted cache hit rate < {options.MinimumWeightedHitRatePct:0.#}%");
        text.AppendLine();
        text.AppendLine(verdict.Summary);
        text.AppendLine();

        text.AppendLine("## Fleet totals");
        text.AppendLine();
        text.AppendLine($"| Metric | Value |");
        text.AppendLine($"| --- | --- |");
        text.AppendLine($"| Weighted hit rate (trigger metric) | **{verdict.WeightedHitRatePct:0.0}%** |");
        text.AppendLine($"| Unweighted per-request mean | {verdict.UnweightedHitRatePct:0.0}% |");
        text.AppendLine($"| Floor | {verdict.ThresholdPct:0.#}% |");
        text.AppendLine($"| Measured requests | {verdict.MeasuredRequests:N0} |");
        text.AppendLine($"| Unmeasured requests (excluded) | {verdict.UnmeasuredRequests:N0} |");
        text.AppendLine($"| Zero-hit requests | {verdict.ZeroHitRequests:N0} |");
        text.AppendLine($"| Prompt tokens | {verdict.PromptTokens:N0} |");
        text.AppendLine($"| Cached prompt tokens | {verdict.CachedTokens:N0} |");
        text.AppendLine($"| Uncached prompt tokens | {verdict.UncachedTokens:N0} |");
        text.AppendLine($"| Cache-write tokens | {verdict.CacheWriteTokens:N0} |");
        text.AppendLine($"| Reusable-prefix efficiency (trigger metric) | **{verdict.ReusablePrefixEfficiencyPct:0.0}%** "
            + $"({verdict.ReusedPrefixTokens:N0} of {verdict.ReusablePrefixTokens:N0} tokens over "
            + $"{verdict.ReusablePrefixSamples:N0} live continuations) |");
        text.AppendLine($"| Reusable-prefix floor | {verdict.ReusablePrefixThresholdPct:0.#}% |");
        text.AppendLine($"| Expired continuations (excluded) | {verdict.ExpiredPrefixSamples:N0} "
            + $"({verdict.ExpiredPrefixTokens:N0} tokens re-prefilled) |");
        text.AppendLine($"| Assumed prefix lifetime | {options.AssumedPrefixLifetime.TotalMinutes:0.#} min |");
        text.AppendLine($"| Measured span | {verdict.ObservationSpan.TotalMinutes:0.#} min |");
        text.AppendLine();
        text.AppendLine("The weighted rate is total cached prompt tokens over total prompt tokens, so each");
        text.AppendLine("request counts in proportion to its size. On its own it cannot be a trigger: its");
        text.AppendLine("ceiling is set by how much COLD work the fleet is doing — first turns, and");
        text.AppendLine("continuations resumed after the provider's cache had already dropped the prefix —");
        text.AppendLine("and halting the fleet does not make any of that cheaper.");
        text.AppendLine();
        text.AppendLine("Reusable-prefix efficiency is the correctness figure, and both floors must be");
        text.AppendLine("breached to halt. Of the prefix a continuation should have reused, how much the");
        text.AppendLine("provider actually served — counting only continuations resumed INSIDE the assumed");
        text.AppendLine("prefix lifetime, because one resumed after it was never going to hit. Those are");
        text.AppendLine("counted separately above: they are real prefill spend and worth watching, but they");
        text.AppendLine("are a fact about how long the agents were away, not about prefix assembly.");
        text.AppendLine();

        text.AppendLine($"## Halted projects ({haltedProjectIDs.Count})");
        text.AppendLine();
        if (haltedProjectIDs.Count == 0) text.AppendLine("_No project was in a haltable state._");
        else foreach (string id in haltedProjectIDs) text.AppendLine($"- `{id}`");
        text.AppendLine();

        AppendGroups(text, "By project", Group(measured, record => record.ProjectID));
        AppendGroups(text, "By agent", Group(measured,
            record => $"{record.ProjectID}/{record.AgentID}"));
        AppendGroups(text, "By model", Group(measured, record => record.Model));
        AppendGroups(text, "By routed provider", Group(measured,
            record => record.RoutedProvider ?? record.Provider ?? "unknown"));
        AppendGroups(text, "By source", Group(measured, record => record.Source));
        AppendGroups(text, "By prompt-assembly status", Group(measured,
            record => record.PromptAssemblyStatus ?? "(not first turn)"));

        var zeroHit = measured
            .Where(record => Math.Clamp(record.CachedPromptTokens, 0, record.PromptTokens) == 0)
            .OrderByDescending(record => record.PromptTokens)
            .Take(40)
            .ToList();
        text.AppendLine($"## Largest zero-hit requests ({zeroHit.Count} of {verdict.ZeroHitRequests:N0} shown)");
        text.AppendLine();
        text.AppendLine("| occurred | usageID | generationID | project/agent | turn (epoch) | prompt | model |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var record in zeroHit)
            text.AppendLine($"| {Stamp(record.OccurredAt)} | `{record.UsageID}` | `{record.GenerationID ?? "—"}` "
                + $"| {record.ProjectID}/{record.AgentID} | {record.TurnIndex} ({record.CacheEpochTurnIndex}) "
                + $"| {record.PromptTokens:N0} | {record.Model} |");
        text.AppendLine();

        text.AppendLine($"## Every measured request in the window ({measured.Count:N0})");
        text.AppendLine();
        text.AppendLine("Full per-request records — every field, including IDs — are in the attached JSONL.");
        text.AppendLine();
        text.AppendLine("| occurred | usageID | genID | project | agent | src | turn | epochTurn | epochID | session | model | routed | prompt | cached | hit% | write | compacted | queueMs | providerMs | totalMs | respCache |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var record in measured.OrderBy(record => record.OccurredAt).ThenBy(record => record.Sequence))
        {
            long prompt = Math.Max(0, record.PromptTokens);
            long cached = Math.Clamp(record.CachedPromptTokens, 0, prompt);
            text.AppendLine(
                $"| {Stamp(record.OccurredAt)} | `{record.UsageID}` | `{record.GenerationID ?? "—"}` "
                + $"| {record.ProjectID} | {record.AgentID} | {record.Source} | {record.TurnIndex} "
                + $"| {record.CacheEpochTurnIndex} | `{record.CacheEpochID ?? "—"}` | `{record.CacheSessionID ?? "—"}` "
                + $"| {record.Model} | {record.RoutedProvider ?? record.Provider ?? "—"} "
                + $"| {prompt:N0} | {cached:N0} | {Percent(cached, prompt):0.0} "
                + $"| {record.CacheWritePromptTokens:N0} | {(record.ContextWasCompacted ? "yes" : "no")} "
                + $"| {record.QueueDurationMs:N0} | {record.ProviderDurationMs:N0} | {record.RequestDurationMs:N0} "
                + $"| {record.ResponseCacheStatus ?? "—"} |");
        }
        text.AppendLine();

        var unmeasured = window.Where(record => !ProjectPromptCacheAnalytics.IsMeasuredSample(record)).ToList();
        text.AppendLine($"## Unmeasured requests in the window ({unmeasured.Count:N0})");
        text.AppendLine();
        text.AppendLine("These were excluded from the rate: the provider reported no cache metrics, or the row");
        text.AppendLine("predates the current prefix contract. They are listed because a sudden rise here is");
        text.AppendLine("itself a fault — it means the fleet has stopped being measurable.");
        text.AppendLine();
        if (unmeasured.Count == 0) text.AppendLine("_None._");
        else
        {
            text.AppendLine("| occurred | usageID | project | agent | source | model | provider | prompt | telemetryVersion | metricsAvailable |");
            text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (var record in unmeasured.OrderBy(record => record.OccurredAt).Take(200))
                text.AppendLine($"| {Stamp(record.OccurredAt)} | `{record.UsageID}` | {record.ProjectID} "
                    + $"| {record.AgentID} | {record.Source} | {record.Model} | {record.Provider ?? "—"} "
                    + $"| {record.PromptTokens:N0} | {record.PromptCacheTelemetryVersion ?? "—"} "
                    + $"| {(record.CacheMetricsAvailable ? "yes" : "no")} |");
        }
        text.AppendLine();

        text.AppendLine("## Resuming");
        text.AppendLine();
        text.AppendLine("No agent will be admitted to another model call until the halt is cleared:");
        text.AppendLine("`POST /projects/cache-health/clear` (optionally `{\"unhalt\": true}` to also restore every");
        text.AppendLine("project to the status it held before the halt). Clearing drops the trailing window, so the");
        text.AppendLine("next twenty minutes are judged fresh.");
        return text.ToString();
    }

    /// <summary>The attachment: one complete JSON record per request, nothing elided.</summary>
    public static string BuildRequestLog(IReadOnlyList<ProjectTokenUsageRecord> window)
    {
        var text = new StringBuilder();
        foreach (var record in window.OrderBy(record => record.OccurredAt).ThenBy(record => record.Sequence))
            text.AppendLine(JsonConvert.SerializeObject(record, Formatting.None));
        return text.ToString();
    }

    private static void AppendGroups(StringBuilder text, string title, List<ProjectCacheHealthGroup> groups)
    {
        text.AppendLine($"## {title}");
        text.AppendLine();
        if (groups.Count == 0)
        {
            text.AppendLine("_No measured requests._");
            text.AppendLine();
            return;
        }
        text.AppendLine("| key | requests | zero-hit | prompt | cached | uncached | hit% |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var line in groups)
            text.AppendLine($"| {line.Key} | {line.Requests:N0} | {line.ZeroHitRequests:N0} "
                + $"| {line.PromptTokens:N0} | {line.CachedTokens:N0} | {line.UncachedTokens:N0} "
                + $"| {line.WeightedHitRatePct:0.0} |");
        text.AppendLine();
    }

    // ── persistence ──

    private string StatePath => Path.Combine(stateDirectory, "cache-halt.json");

    private ProjectCacheHaltState LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return new ProjectCacheHaltState();
            var loaded = JsonConvert.DeserializeObject<ProjectCacheHaltState>(File.ReadAllText(StatePath));
            if (loaded == null) return new ProjectCacheHaltState();
            if (loaded.Engaged)
                log($"Projects cache health: restored an engaged halt from {Stamp(loaded.EngagedAt ?? default)} — "
                    + "no agent will send a request until it is cleared.");
            return loaded;
        }
        catch (Exception ex)
        {
            // Fail OPEN on an unreadable latch: the alternative is a corrupt file silently holding the
            // whole fleet down with no way to see why. A real collapse re-trips within the window.
            log($"Projects cache health: halt state unreadable ({ex.Message}); starting un-halted.");
            return new ProjectCacheHaltState();
        }
    }

    private void SaveStateLocked()
    {
        try
        {
            string temporary = StatePath + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(state, Formatting.Indented));
            File.Move(temporary, StatePath, overwrite: true);
        }
        catch (Exception ex) { log($"Projects cache health: could not persist halt state: {ex.Message}"); }
    }

    private static ProjectCacheHaltState Clone(ProjectCacheHaltState source)
        => JsonConvert.DeserializeObject<ProjectCacheHaltState>(
            JsonConvert.SerializeObject(source)) ?? new ProjectCacheHaltState();

    private static double Percent(long numerator, long denominator)
        => denominator > 0 ? Math.Round(numerator * 100.0 / denominator, 1) : 0;

    private static string Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

    private static string Stamp(DateTime value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
