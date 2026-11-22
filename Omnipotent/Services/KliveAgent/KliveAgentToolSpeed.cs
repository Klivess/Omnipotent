using Newtonsoft.Json;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveAgent;

/// <summary>How a tool compares with the others, from what its calls have really taken on this server.</summary>
public enum ToolSpeedTier
{
    /// <summary>Usually under 300 ms.</summary>
    Instant,
    /// <summary>Usually under 2 s.</summary>
    Fast,
    /// <summary>Usually under 10 s.</summary>
    Moderate,
    /// <summary>Usually 10 s or more.</summary>
    Slow,
    /// <summary>Overruns its patience often, or one call in ten takes two minutes or more.</summary>
    HangProne,
    /// <summary>Waits for something on purpose (an email, a person, a change) and bounds itself.</summary>
    Waits,
}

/// <summary>How one finished call ended, for the speed record.</summary>
public enum ToolCallOutcome
{
    Ok,
    Error,
    /// <summary>The call ran past the time it was given: the run stopped waiting for it, or its script
    /// timed out.</summary>
    Overran,
}

/// <summary>What the record says about one tool, or one operation of a tool.</summary>
public sealed record ToolSpeedEstimate(string Key, ToolSpeedTier Tier, bool Measured, int Samples,
    long TypicalMs, long SlowMs, long WorstMs, int Overruns, int Errors)
{
    public double OverrunRate => Samples == 0 ? 0 : Overruns / (double)Samples;
}

/// <summary>
/// KliveAgent's record of how long each tool really takes here, and the ranking it uses to put the
/// fastest tool for a job in front of the model.
///
/// Why: a run is a chain of model calls and tool calls, and the tool half was never measured unless a
/// call took a minute or more. The model picked tools on their descriptions alone, so a job grep does
/// in 200 ms went to execute_csharp (a compile first), a page already fetched was fetched again, and an
/// operation that hangs one call in five held the run for ten minutes each time it did.
///
/// What it does with the record:
/// <list type="bullet">
/// <item>The <c>[Tool speed]</c> prompt block lists, for each goal, the tools that can do it, fastest
/// first, with what they typically take, and names the ones that are slow or hang-prone right now.</item>
/// <item><see cref="PatienceFor"/> gives each call a wait sized to that operation's own history, so an
/// operation that answers in a second and hangs is not waited on for the full ten-minute deadline. The
/// brain moves a call that outlives its patience to a background job, so its result is not lost.</item>
/// <item><see cref="GateAdvice"/> turns down a call to a hang-prone tool once per run when a much faster
/// tool can do the same job, and says which. Calling it again runs it.</item>
/// <item><see cref="SlowCallHint"/> adds a line under a slow result naming the faster choice.</item>
/// </list>
/// Durations only: no arguments, results or other content are kept.
/// </summary>
public sealed class KliveAgentToolSpeed
{
    /// <summary>Calls kept per tool or operation. Old calls fall out, so the record follows the server
    /// as it is now (a fixed index, a restarted container) rather than as it was last month.</summary>
    internal const int SamplesKept = 64;

    /// <summary>Calls needed before a measurement replaces the built-in expectation.</summary>
    internal const int MinSamplesToTrust = 8;

    /// <summary>At most this many tools and operations are tracked; the least recently used goes first.</summary>
    internal const int MaxSeries = 400;

    /// <summary>A slow result gets a hint only when it took at least this long.</summary>
    internal const long HintAfterMs = 5_000;

    /// <summary>The pseudo-tool every dedicated Service Surface tool is also recorded under, so "use the
    /// service's own tool" can be ranked against omniservice, omni_api and execute_csharp.</summary>
    internal const string ServiceToolsKey = "[service tools]";

    private readonly string? path;
    private readonly object gate = new();
    private readonly Dictionary<string, Series> series = new(StringComparer.Ordinal);
    private CancellationTokenSource? pendingSave;

    public KliveAgentToolSpeed(string? path = null)
    {
        this.path = path;
    }

    // ── Recording ──

    /// <summary>Records one finished call under its tool and, when the operation matters, under the
    /// operation too.</summary>
    public void Record(string tool, string? argsJson, long durationMs, ToolCallOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(tool)) return;
        durationMs = Math.Max(0, durationMs);
        var now = DateTime.UtcNow;
        lock (gate)
        {
            Add(tool, tool, null, durationMs, outcome, now);
            if (OperationKey(tool, argsJson) is { } op)
                Add(tool + " " + op, tool, op, durationMs, outcome, now);
            if (IsDedicatedServiceTool(tool))
                Add(ServiceToolsKey, ServiceToolsKey, null, durationMs, outcome, now);
            Evict();
        }
        QueueSave();
    }

    private void Add(string key, string tool, string? op, long ms, ToolCallOutcome outcome, DateTime now)
    {
        if (!series.TryGetValue(key, out var s))
            series[key] = s = new Series { Key = key, Tool = tool, Operation = op };
        s.Ms.Add(ms);
        s.Outcomes.Add((byte)outcome);
        if (s.Ms.Count > SamplesKept)
        {
            s.Ms.RemoveRange(0, s.Ms.Count - SamplesKept);
            s.Outcomes.RemoveRange(0, s.Outcomes.Count - SamplesKept);
        }
        s.TotalCalls++;
        if (outcome == ToolCallOutcome.Overran) s.TotalOverruns++;
        if (outcome == ToolCallOutcome.Error) s.TotalErrors++;
        s.LastUtc = now;
    }

    private void Evict()
    {
        if (series.Count <= MaxSeries) return;
        foreach (var key in series.Values.OrderBy(s => s.LastUtc).Take(series.Count - MaxSeries).Select(s => s.Key).ToList())
            series.Remove(key);
    }

    /// <summary>
    /// The part of a call's arguments that changes how long it takes, or null when only the tool does.
    /// A service tool's 'op' is a different operation each time; whether web_search downloads the top
    /// pages is the difference between a second and ten. A file path is not: read_file is fast whatever
    /// it reads, and one record per path would never reach a trusted sample.
    /// </summary>
    internal static string? OperationKey(string tool, string? argsJson)
    {
        switch (tool)
        {
            case "web_search":
                return IntArg(argsJson, "fetchTop") is 0 ? "snippets" : "fetch";
            case "search_knowledge":
                return BoolArg(argsJson, "includeMessages") is false ? "noMessages" : null;
            case "tool_jobs":
            case "task_ledger":
            case "computer_browser_action":
                return Normalize(StringArg(argsJson, "op") ?? StringArg(argsJson, "action"));
            case "computer_cdp":
                return Normalize(StringArg(argsJson, "action") ?? StringArg(argsJson, "op"));
        }
        return KliveAgentServiceTools.IsHandled(tool) ? Normalize(KliveAgentBrain.ToolOperationOf(argsJson)) : null;
    }

    private static readonly Regex GuidPattern = new(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LongHexPattern = new(@"\b[0-9a-f]{16,}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DigitsPattern = new(@"\d+", RegexOptions.Compiled);

    /// <summary>Ids and numbers in an operation are folded away, so /orders/123 and /orders/456 are one
    /// operation and the record does not grow a key per call.</summary>
    internal static string? Normalize(string? operation)
    {
        if (string.IsNullOrWhiteSpace(operation)) return null;
        string s = operation.Trim().ToLowerInvariant();
        int query = s.IndexOf('?');
        if (query >= 0) s = s[..query];
        s = GuidPattern.Replace(s, "{id}");
        s = LongHexPattern.Replace(s, "{id}");
        s = DigitsPattern.Replace(s, "#");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length == 0) return null;
        return s.Length <= 80 ? s : s[..80];
    }

    private static bool IsDedicatedServiceTool(string tool) =>
        tool != Omnipotent.Services.ServiceTools.OmniToolCatalog.UniversalServiceTool
        && tool != Omnipotent.Services.ServiceTools.OmniToolCatalog.UniversalApiTool
        && KliveAgentServiceTools.IsHandled(tool);

    // ── Reading the record ──

    /// <summary>The operation's own record when it has enough calls, else the tool's, else what the tool
    /// is expected to take.</summary>
    public ToolSpeedEstimate Estimate(string tool, string? argsJson = null)
    {
        lock (gate)
        {
            if (OperationKey(tool, argsJson) is { } op && Measure(tool + " " + op) is { } byOp) return byOp;
            if (Measure(tool) is { } byTool) return byTool;
        }
        return ExpectedFor(tool, argsJson);
    }

    /// <summary>Caller holds the lock.</summary>
    private ToolSpeedEstimate? Measure(string key)
    {
        if (!series.TryGetValue(key, out var s) || s.Ms.Count < MinSamplesToTrust) return null;
        var sorted = s.Ms.OrderBy(x => x).ToArray();
        int overruns = s.Outcomes.Count(o => o == (byte)ToolCallOutcome.Overran);
        int errors = s.Outcomes.Count(o => o == (byte)ToolCallOutcome.Error);
        long typical = Percentile(sorted, 0.5), slow = Percentile(sorted, 0.9), worst = sorted[^1];
        var tier = IsWaitingTool(s.Tool) ? ToolSpeedTier.Waits : TierOf(typical, slow, overruns, sorted.Length);
        return new ToolSpeedEstimate(key, tier, true, sorted.Length, typical, slow, worst, overruns, errors);
    }

    internal static ToolSpeedTier TierOf(long typicalMs, long slowMs, int overruns, int samples)
    {
        if ((overruns >= 2 && overruns >= samples * 0.10) || slowMs >= 120_000) return ToolSpeedTier.HangProne;
        if (typicalMs >= 10_000 || slowMs >= 30_000) return ToolSpeedTier.Slow;
        if (typicalMs >= 2_000 || slowMs >= 8_000) return ToolSpeedTier.Moderate;
        if (typicalMs >= 300 || slowMs >= 1_500) return ToolSpeedTier.Fast;
        return ToolSpeedTier.Instant;
    }

    private static long Percentile(long[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        int index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    /// <summary>p90 of the calls that finished in time: what the tool takes when it works. Overruns are
    /// left out on purpose, or the hangs a patience is meant to cut short would set the patience.</summary>
    private long? CleanSlowMs(string tool, string? argsJson)
    {
        lock (gate)
        {
            foreach (var key in KeysFor(tool, argsJson))
            {
                if (!series.TryGetValue(key, out var s)) continue;
                var clean = s.Ms.Where((_, i) => s.Outcomes[i] != (byte)ToolCallOutcome.Overran).OrderBy(x => x).ToArray();
                if (clean.Length >= MinSamplesToTrust) return Percentile(clean, 0.9);
            }
        }
        return null;
    }

    private static IEnumerable<string> KeysFor(string tool, string? argsJson)
    {
        if (OperationKey(tool, argsJson) is { } op) yield return tool + " " + op;
        yield return tool;
    }

    /// <summary>
    /// How long to wait for a call before it is moved to the background. Four times what nine in ten of
    /// this operation's good calls take, never under <paramref name="floorSeconds"/> and never over
    /// <paramref name="capSeconds"/> (the run's native-tool deadline). Zero means "no limit": the cap is
    /// off, or the tool waits on purpose and bounds itself.
    /// </summary>
    public TimeSpan PatienceFor(string tool, string? argsJson, int capSeconds, int floorSeconds)
    {
        if (capSeconds <= 0 || IsWaitingTool(tool)) return TimeSpan.Zero;
        long basis = CleanSlowMs(tool, argsJson) ?? ExpectedSlowMs(ExpectedFor(tool, argsJson).Tier);
        long floorMs = Math.Max(1, floorSeconds) * 1000L;
        long ms = Math.Max(floorMs, basis * 4);
        return TimeSpan.FromMilliseconds(Math.Min(ms, capSeconds * 1000L));
    }

    private static long ExpectedSlowMs(ToolSpeedTier tier) => tier switch
    {
        ToolSpeedTier.Instant => 1_000,
        ToolSpeedTier.Fast => 5_000,
        ToolSpeedTier.Moderate => 30_000,
        _ => 120_000,
    };

    // ── What a tool is expected to take before it has a record ──

    /// <summary>Tools that wait for something on purpose. They are never cut short and never ranked
    /// against tools that do work.</summary>
    internal static bool IsWaitingTool(string tool) =>
        tool.Contains("wait_for", StringComparison.Ordinal)
        || tool is "tool_jobs" or "request_human" or "computer_wait" or "computer_confirm_action" or "computer_confirm_and_click";

    private static readonly Dictionary<string, (ToolSpeedTier tier, long typicalMs)> Expectations = new(StringComparer.Ordinal)
    {
        ["read_file"] = (ToolSpeedTier.Instant, 20),
        ["list_directory"] = (ToolSpeedTier.Instant, 20),
        ["get_global_path"] = (ToolSpeedTier.Instant, 5),
        ["find_files"] = (ToolSpeedTier.Instant, 150),
        ["grep"] = (ToolSpeedTier.Fast, 600),
        ["read_data_file"] = (ToolSpeedTier.Instant, 30),
        ["list_data_directory"] = (ToolSpeedTier.Instant, 50),
        ["recall_memories"] = (ToolSpeedTier.Instant, 40),
        ["recall_memories_by_tag"] = (ToolSpeedTier.Instant, 20),
        ["save_memory"] = (ToolSpeedTier.Instant, 50),
        ["save_shortcut"] = (ToolSpeedTier.Instant, 50),
        ["get_shortcuts"] = (ToolSpeedTier.Instant, 20),
        ["delete_memory"] = (ToolSpeedTier.Instant, 30),
        ["task_ledger"] = (ToolSpeedTier.Instant, 1),
        ["tool_speed"] = (ToolSpeedTier.Instant, 1),
        ["start_background_tool"] = (ToolSpeedTier.Instant, 1),
        ["schedule_task"] = (ToolSpeedTier.Instant, 30),
        ["list_scheduled_tasks"] = (ToolSpeedTier.Instant, 5),
        ["cancel_scheduled_task"] = (ToolSpeedTier.Instant, 20),
        ["account_list"] = (ToolSpeedTier.Instant, 50),
        ["account_register"] = (ToolSpeedTier.Fast, 400),
        ["account_update"] = (ToolSpeedTier.Fast, 400),
        ["klivemail_create_mailbox"] = (ToolSpeedTier.Fast, 300),
        ["klivemail_list_messages"] = (ToolSpeedTier.Fast, 300),
        ["klivemail_get_message"] = (ToolSpeedTier.Fast, 300),
        ["notify_klives"] = (ToolSpeedTier.Fast, 800),
        ["read_knowledge_doc"] = (ToolSpeedTier.Fast, 300),
        ["search_knowledge"] = (ToolSpeedTier.Moderate, 2_500),
        ["web_fetch"] = (ToolSpeedTier.Moderate, 4_000),
        ["web_search"] = (ToolSpeedTier.Slow, 10_000),
        ["web_search fetch"] = (ToolSpeedTier.Slow, 10_000),
        ["web_search snippets"] = (ToolSpeedTier.Fast, 1_500),
        ["search_knowledge noMessages"] = (ToolSpeedTier.Fast, 800),
        ["execute_csharp"] = (ToolSpeedTier.Fast, 1_000),
        ["omniservice"] = (ToolSpeedTier.Fast, 500),
        ["omni_api"] = (ToolSpeedTier.Fast, 800),
        [ServiceToolsKey] = (ToolSpeedTier.Fast, 400),
        ["computer_browser_inspect"] = (ToolSpeedTier.Fast, 800),
        ["computer_browser_action"] = (ToolSpeedTier.Fast, 1_200),
        ["computer_click_browser_control"] = (ToolSpeedTier.Fast, 1_200),
        ["computer_cdp"] = (ToolSpeedTier.Fast, 600),
        ["computer_screenshot"] = (ToolSpeedTier.Fast, 1_500),
        ["computer_read_screen"] = (ToolSpeedTier.Moderate, 2_500),
        ["computer_find_text"] = (ToolSpeedTier.Moderate, 2_500),
        ["computer_click"] = (ToolSpeedTier.Fast, 1_500),
        ["computer_navigate"] = (ToolSpeedTier.Moderate, 4_000),
        ["computer_terminal"] = (ToolSpeedTier.Moderate, 3_000),
        ["computer_upload_file"] = (ToolSpeedTier.Moderate, 4_000),
    };

    /// <summary>The expectation for this call's operation when there is one ("web_search snippets"),
    /// else for the tool.</summary>
    internal static ToolSpeedEstimate ExpectedFor(string tool, string? argsJson) =>
        OperationKey(tool, argsJson) is { } op && Expectations.ContainsKey(tool + " " + op)
            ? Expected(tool + " " + op)
            : Expected(tool);

    /// <summary>The built-in expectation for a tool that has no record yet.</summary>
    internal static ToolSpeedEstimate Expected(string tool)
    {
        if (IsWaitingTool(tool)) return new(tool, ToolSpeedTier.Waits, false, 0, 0, 0, 0, 0, 0);
        var (tier, typical) = Expectations.TryGetValue(tool, out var known) ? known
            : IsDedicatedServiceTool(tool) ? Expectations[ServiceToolsKey]
            : (ToolSpeedTier.Fast, 1_000L);
        return new(tool, tier, false, 0, typical, ExpectedSlowMs(tier) / 2, ExpectedSlowMs(tier), 0, 0);
    }

    // ── Which tools do the same job ──

    /// <summary>One goal and the tools that can reach it, in the order to try them when nothing has been
    /// measured. The note says when the faster tool applies, because it does not always.</summary>
    /// <param name="RankOnly">The tools are routes to the same operation (a service's own tool, omniservice
    /// and omni_api all reach one method), so when that operation is slow no route is faster: the family
    /// orders the prompt but never names an alternative.</param>
    internal sealed record ToolFamily(string Goal, string[] Tools, string? Note = null, bool ComputerOnly = false, bool RankOnly = false);

    internal static readonly ToolFamily[] Families =
    {
        new("search code by content", new[] { "grep", "execute_csharp" }, "grep outputMode:\"files\" when you only need where"),
        new("find files by name", new[] { "find_files", "list_directory", "execute_csharp" }),
        new("read a source file", new[] { "read_file", "execute_csharp" }),
        new("read runtime data (SavedData)", new[] { "read_data_file", "execute_csharp" }),
        new("list runtime data", new[] { "list_data_directory", "execute_csharp" }),
        new("recall facts about Klives or the past", new[] { "recall_memories", "recall_memories_by_tag", "search_knowledge" }),
        new("search history across systems", new[] { "search_knowledge noMessages", "search_knowledge" }),
        new("read a web page", new[] { "read_knowledge_doc", "web_fetch" }, "read_knowledge_doc only for a page already fetched or found by search"),
        new("get current facts from the web", new[] { "web_search snippets", "web_search fetch" }, "then web_fetch only the page you need"),
        new("read or drive a live service", new[] { ServiceToolsKey, "omniservice", "omni_api", "execute_csharp" }, RankOnly: true),
        new("see what is on a web page", new[] { "computer_browser_inspect", "computer_cdp", "computer_screenshot", "computer_read_screen" }, ComputerOnly: true),
        new("act on a web page", new[] { "computer_browser_action", "computer_click_browser_control", "computer_click" }, ComputerOnly: true),
    };

    /// <summary>How a family member is named to the model. A member is a tool, or a tool and one of its
    /// operations ("web_search snippets") when the operation is what makes it fast or slow.</summary>
    internal static string DisplayName(string member) => member switch
    {
        ServiceToolsKey => "its own service tool",
        "web_search snippets" => "web_search fetchTop:0",
        "web_search fetch" => "web_search fetching pages",
        "search_knowledge noMessages" => "search_knowledge includeMessages:false",
        _ => member,
    };

    /// <summary>A family member's record: the operation's own when the member names one, else the
    /// tool's, else the expectation.</summary>
    internal ToolSpeedEstimate EstimateMember(string member)
    {
        int space = member.IndexOf(' ');
        string tool = space > 0 && member != ServiceToolsKey ? member[..space] : member;
        lock (gate)
        {
            if (Measure(member) is { } own) return own;
            // A member that is a whole tool reads the tool's record. One that names an operation never
            // borrows the tool's: that would rank "web_search fetchTop:0" by every search's time.
            if (member == tool && Measure(tool) is { } byTool) return byTool;
        }
        return Expectations.ContainsKey(member) ? Expected(member) : Expected(tool);
    }

    /// <summary>Which of a family's members this call is, or null when the family does not include it.</summary>
    private static string? MemberIn(ToolFamily family, string tool, string? argsJson)
    {
        string t = IsDedicatedServiceTool(tool) ? ServiceToolsKey : tool;
        if (OperationKey(tool, argsJson) is { } op && family.Tools.Contains(t + " " + op)) return t + " " + op;
        return family.Tools.Contains(t) ? t : null;
    }

    /// <summary>A family's tools, fastest first. A measured median beats an expectation; a hang-prone
    /// tool goes last whatever its median.</summary>
    internal IReadOnlyList<(string tool, ToolSpeedEstimate estimate)> Rank(ToolFamily family)
    {
        return family.Tools
            .Select((tool, order) => (tool, order, estimate: EstimateMember(tool)))
            .OrderBy(x => x.estimate.Tier == ToolSpeedTier.HangProne ? 1 : 0)
            .ThenBy(x => x.estimate.TypicalMs)
            .ThenBy(x => x.order)
            .Select(x => (x.tool, x.estimate))
            .ToList();
    }

    /// <summary>The fastest other tool for the same job, when it is clearly faster: at most a fifth of
    /// what this call takes, and not itself slow.</summary>
    internal (ToolFamily family, string tool, ToolSpeedEstimate estimate)? FasterAlternative(string tool, string? argsJson, long thisCallMs)
    {
        (ToolFamily family, string tool, ToolSpeedEstimate estimate)? best = null;
        foreach (var family in Families)
        {
            if (family.RankOnly || MemberIn(family, tool, argsJson) is not { } member) continue;
            foreach (var other in family.Tools)
            {
                // execute_csharp can do almost anything, so it is never offered as the faster way.
                if (other == member || other == "execute_csharp") continue;
                var estimate = EstimateMember(other);
                if (estimate.Tier is ToolSpeedTier.Slow or ToolSpeedTier.HangProne or ToolSpeedTier.Waits) continue;
                if (estimate.TypicalMs * 5 > thisCallMs) continue;
                if (best == null || estimate.TypicalMs < best.Value.estimate.TypicalMs) best = (family, other, estimate);
            }
        }
        return best;
    }

    // ── Steering ──

    /// <summary>
    /// Advice instead of a run, for a call to a tool the record says is hang-prone when a much faster
    /// tool does the same job. Null means "run it". The brain asks once per tool per run, so calling
    /// again runs it: the model may know the faster tool cannot do this particular job.
    /// </summary>
    public string? GateAdvice(string tool, string? argsJson)
    {
        var estimate = Estimate(tool, argsJson);
        if (!estimate.Measured || estimate.Tier != ToolSpeedTier.HangProne) return null;
        if (FasterAlternative(tool, argsJson, Math.Max(estimate.TypicalMs, estimate.SlowMs)) is not { } alt) return null;
        return $"[NOT RUN | slow tool] {tool} has been slow here lately: {DescribeRecord(estimate)}. "
            + $"To {alt.family.Goal}, {DisplayName(alt.tool)} usually takes {FormatMs(alt.estimate.TypicalMs)}"
            + (alt.family.Note is { } note ? $" ({note})" : "") + ". "
            + $"Use it if it can do this job. If {tool} really is the only way, call it again unchanged and it will run, "
            + "or start it with start_background_tool and carry on with something else while it works.";
    }

    /// <summary>A line to add under a slow result, naming the faster tool for the same job. Null when the
    /// call was quick or nothing faster does the job.</summary>
    public string? SlowCallHint(string tool, string? argsJson, long durationMs)
    {
        if (durationMs < HintAfterMs || IsWaitingTool(tool)) return null;
        if (FasterAlternative(tool, argsJson, durationMs) is not { } alt) return null;
        return $"[speed] {tool} took {FormatMs(durationMs)}. To {alt.family.Goal}, {DisplayName(alt.tool)} usually takes "
            + $"{FormatMs(alt.estimate.TypicalMs)}" + (alt.family.Note is { } note ? $" ({note})" : "") + ".";
    }

    /// <summary>
    /// The [Tool speed] prompt block: for each goal, its tools fastest first, then what is slow right
    /// now. It sits below the cache breakpoint with the other task-volatile sections, and the times are
    /// rounded to a few steps so it changes only when a tool really got faster or slower.
    /// </summary>
    public string BuildPromptBlock(bool computerUse)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Tool speed] Of the tools that can do a job, use the fastest. Per goal, fastest first (typical time on this server where measured):");
        foreach (var family in Families)
        {
            if (family.ComputerOnly && !computerUse) continue;
            var ranked = Rank(family);
            sb.Append("- ").Append(family.Goal).Append(": ");
            sb.Append(string.Join(" → ", ranked.Select(r => DisplayName(r.tool) + (r.estimate.Measured
                ? " " + FormatMs(r.estimate.TypicalMs) + (r.estimate.Tier == ToolSpeedTier.HangProne ? " (hang-prone)" : "")
                : ""))));
            if (family.Note != null) sb.Append(" · ").Append(family.Note);
            sb.AppendLine();
        }

        var slow = SlowRightNow(computerUse).Take(5).ToList();
        if (slow.Count > 0)
            sb.AppendLine("Slow right now: " + string.Join("; ", slow.Select(s => $"{s.Key} ({DescribeRecord(s)})")) + ".");
        sb.AppendLine("A call that runs far past its usual time is not waited for: it carries on as a background job and tool_jobs collects it. "
            + "Start a call you expect to be slow with start_background_tool and do other work meanwhile.");
        return sb.ToString();
    }

    private List<ToolSpeedEstimate> SlowRightNow(bool computerUse)
    {
        lock (gate)
        {
            return series.Values
                .Where(s => s.Tool != ServiceToolsKey && !IsWaitingTool(s.Tool) && (computerUse || !s.Tool.StartsWith("computer_", StringComparison.Ordinal)))
                .Select(s => Measure(s.Key))
                .Where(e => e is { Tier: ToolSpeedTier.HangProne or ToolSpeedTier.Slow })
                .Select(e => e!)
                .OrderByDescending(e => e.Tier)
                .ThenByDescending(e => e.TypicalMs)
                .ToList();
        }
    }

    private static string DescribeRecord(ToolSpeedEstimate e)
    {
        var parts = new List<string> { $"typically {FormatMs(e.TypicalMs)}", $"1 in 10 over {FormatMs(e.SlowMs)}" };
        if (e.Overruns > 0) parts.Add($"{e.Overruns} of the last {e.Samples} overran");
        return string.Join(", ", parts);
    }

    /// <summary>Times rounded to a few steps: the model needs the order of magnitude, and a block that
    /// changes on every call would change the prompt on every run.</summary>
    internal static string FormatMs(long ms)
    {
        if (ms < 10) return "<10ms";
        if (ms < 100) return $"{(ms + 5) / 10 * 10}ms";
        if (ms < 1_000) return $"{(ms + 25) / 50 * 50}ms";
        if (ms < 10_000) return (Math.Round(ms / 1000.0, 1)).ToString("0.#", CultureInfo.InvariantCulture) + "s";
        if (ms < 120_000) return $"{Math.Round(ms / 1000.0)}s";
        return (Math.Round(ms / 60000.0, 1)).ToString("0.#", CultureInfo.InvariantCulture) + "min";
    }

    /// <summary>The tool_speed tool's answer: the record for every tool (or those whose name contains
    /// <paramref name="filter"/>), slowest kind first, then the ranking per goal.</summary>
    public string Describe(string? filter = null)
    {
        var rows = BuildReport().Tools
            .Where(t => string.IsNullOrWhiteSpace(filter) || t.Key.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var sb = new StringBuilder();
        if (rows.Count == 0)
            sb.AppendLine(string.IsNullOrWhiteSpace(filter)
                ? "No tool calls recorded yet; the ranking below uses the expected speeds."
                : $"No recorded tool or operation matches '{filter}'.");
        else
        {
            sb.AppendLine($"Tool speed on this server (each over its last {SamplesKept} calls at most; typical = median, slow = the time 9 in 10 finish within):");
            foreach (var t in rows.Take(60))
                sb.AppendLine($"- {t.Key}: {t.Samples} calls · typical {FormatMs(t.TypicalMs)} · slow {FormatMs(t.SlowMs)} · worst {FormatMs(t.WorstMs)}"
                    + (t.Overruns > 0 ? $" · {t.Overruns} overran" : "") + (t.Errors > 0 ? $" · {t.Errors} failed" : "")
                    + $" · {t.Tier.ToString().ToLowerInvariant()}" + (t.Trusted ? "" : " (too few calls to rank)"));
        }
        sb.AppendLine("Fastest tool per goal:");
        foreach (var family in Families)
            sb.AppendLine($"- {family.Goal}: " + string.Join(" → ", Rank(family).Select(r => DisplayName(r.tool))));
        return sb.ToString().TrimEnd();
    }

    public sealed class ToolSpeedReport
    {
        public int SamplesKept { get; set; }
        public int MinSamplesToTrust { get; set; }
        public List<ToolSpeedRow> Tools { get; set; } = new();
        public List<ToolFamilyRow> Families { get; set; } = new();
    }

    public sealed class ToolSpeedRow
    {
        public string Key { get; set; } = "";
        public string Tool { get; set; } = "";
        public string? Operation { get; set; }
        public int Samples { get; set; }
        public long TotalCalls { get; set; }
        public long TypicalMs { get; set; }
        public long SlowMs { get; set; }
        public long WorstMs { get; set; }
        public int Overruns { get; set; }
        public int Errors { get; set; }
        public ToolSpeedTier Tier { get; set; }
        public bool Trusted { get; set; }
        public DateTime LastUtc { get; set; }
    }

    public sealed class ToolFamilyRow
    {
        public string Goal { get; set; } = "";
        public List<string> Fastest { get; set; } = new();
        public string? Note { get; set; }
    }

    /// <summary>The view behind GET /kliveagent/stats/tools.</summary>
    public ToolSpeedReport BuildReport()
    {
        var report = new ToolSpeedReport { SamplesKept = SamplesKept, MinSamplesToTrust = MinSamplesToTrust };
        lock (gate)
        {
            foreach (var s in series.Values)
            {
                if (s.Ms.Count == 0) continue;
                var sorted = s.Ms.OrderBy(x => x).ToArray();
                int overruns = s.Outcomes.Count(o => o == (byte)ToolCallOutcome.Overran);
                long typical = Percentile(sorted, 0.5), slow = Percentile(sorted, 0.9);
                report.Tools.Add(new ToolSpeedRow
                {
                    Key = s.Key,
                    Tool = s.Tool,
                    Operation = s.Operation,
                    Samples = sorted.Length,
                    TotalCalls = s.TotalCalls,
                    TypicalMs = typical,
                    SlowMs = slow,
                    WorstMs = sorted[^1],
                    Overruns = overruns,
                    Errors = s.Outcomes.Count(o => o == (byte)ToolCallOutcome.Error),
                    Tier = IsWaitingTool(s.Tool) ? ToolSpeedTier.Waits : TierOf(typical, slow, overruns, sorted.Length),
                    Trusted = sorted.Length >= MinSamplesToTrust,
                    LastUtc = s.LastUtc,
                });
            }
        }
        report.Tools = report.Tools
            .OrderByDescending(t => t.Tier == ToolSpeedTier.Waits ? -1 : (int)t.Tier)
            .ThenByDescending(t => t.TypicalMs)
            .ToList();
        foreach (var family in Families)
            report.Families.Add(new ToolFamilyRow { Goal = family.Goal, Note = family.Note, Fastest = Rank(family).Select(r => r.tool).ToList() });
        return report;
    }

    // ── Persistence ──

    private sealed class Series
    {
        public string Key { get; set; } = "";
        public string Tool { get; set; } = "";
        public string? Operation { get; set; }
        public List<long> Ms { get; set; } = new();
        public List<byte> Outcomes { get; set; } = new();
        public long TotalCalls { get; set; }
        public long TotalOverruns { get; set; }
        public long TotalErrors { get; set; }
        public DateTime LastUtc { get; set; }
    }

    private sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public List<Series> Series { get; set; } = new();
    }

    /// <summary>Reads the record kept by the last process. A missing or damaged file starts empty: the
    /// expectations stand in until calls are measured again.</summary>
    public async Task LoadAsync()
    {
        if (path == null || !File.Exists(path)) return;
        try
        {
            var snapshot = JsonConvert.DeserializeObject<Snapshot>(await File.ReadAllTextAsync(path));
            if (snapshot?.Series == null) return;
            lock (gate)
            {
                foreach (var s in snapshot.Series)
                {
                    if (string.IsNullOrWhiteSpace(s.Key) || s.Ms == null || s.Outcomes == null || s.Ms.Count != s.Outcomes.Count) continue;
                    if (s.Ms.Count > SamplesKept)
                    {
                        s.Ms.RemoveRange(0, s.Ms.Count - SamplesKept);
                        s.Outcomes.RemoveRange(0, s.Outcomes.Count - SamplesKept);
                    }
                    series[s.Key] = s;
                }
                Evict();
            }
        }
        catch
        {
            // A damaged record is not worth failing start-up over.
        }
    }

    private void QueueSave()
    {
        if (path == null) return;
        CancellationTokenSource next;
        lock (gate)
        {
            pendingSave?.Cancel();
            pendingSave?.Dispose();
            pendingSave = next = new CancellationTokenSource();
        }
        _ = SaveAfterDelayAsync(next.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(2_000, token);
            await SaveAsync();
        }
        catch (OperationCanceledException) { }
        catch { /* the next call saves again */ }
    }

    /// <summary>Writes the record now (atomically: a temporary file, then a replace).</summary>
    internal async Task SaveAsync()
    {
        if (path == null) return;
        string json;
        lock (gate)
            json = JsonConvert.SerializeObject(new Snapshot { Series = series.Values.ToList() });
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json);
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    // ── Argument readers ──

    private static Newtonsoft.Json.Linq.JObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return Newtonsoft.Json.Linq.JObject.Parse(json); }
        catch { return null; }
    }

    private static string? StringArg(string? json, string name) =>
        Parse(json)?.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var t) == true && t.Type == Newtonsoft.Json.Linq.JTokenType.String
            ? (string?)t : null;

    private static int? IntArg(string? json, string name) =>
        Parse(json)?.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var t) == true && t.Type == Newtonsoft.Json.Linq.JTokenType.Integer
            ? (int)t : null;

    private static bool? BoolArg(string? json, string name) =>
        Parse(json)?.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var t) == true && t.Type == Newtonsoft.Json.Linq.JTokenType.Boolean
            ? (bool)t : null;
}
