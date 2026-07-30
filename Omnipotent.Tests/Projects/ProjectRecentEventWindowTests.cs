using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

/// <summary>
/// The recent-events window is a TIMELINE, and it used to be assembled like a search result: sorted by
/// relevance score and greedily admitted, skipping over anything that did not fit. Past roughly the ninth
/// newest event the recency term saturates, so selection became keyword overlap against the wake trigger —
/// producing a window with holes in the middle, with nothing saying so. An agent reading a
/// complete-looking history that is missing the record of what it already did will do it again.
///
/// These tests pin the replacement: an unbroken chronological tail, older entries compacted rather than
/// dropped, and an explicit notice pointing at query_events when anything did not fit.
/// </summary>
public sealed class ProjectRecentEventWindowTests
{
    private static ProjectEvent Evt(long seq, string type, string author, string text,
        string? tool = null, string? callId = null, bool? succeeded = null) => new()
        {
            ProjectID = "p1",
            Sequence = seq,
            Type = type,
            Author = author,
            Text = text,
            ToolName = tool,
            ToolCallId = callId,
            Timestamp = new DateTime(2026, 7, 30, 1, 0, 0, DateTimeKind.Utc).AddMinutes(seq),
            PayloadJson = succeeded.HasValue ? "{\"succeeded\":" + (succeeded.Value ? "true" : "false") + "}" : null,
        };

    private static List<ProjectEvent> ToolHistory(int pairs, int textLength = 900)
    {
        var events = new List<ProjectEvent>();
        long seq = 1;
        for (int i = 0; i < pairs; i++)
        {
            string callId = "call-" + i;
            events.Add(Evt(seq++, ProjectEventTypes.ToolCall, "commander",
                $"web_search(query=[probe-{i}] {new string('x', textLength)})", "web_search", callId));
            events.Add(Evt(seq++, ProjectEventTypes.ToolResult, "commander",
                $"result-{i} {new string('y', textLength)}", "web_search", callId, succeeded: true));
        }
        return events;
    }

    [Fact]
    public void RetainedTail_IsContiguous_NoHolesInTheMiddle()
    {
        // Far more history than the budget can hold, so selection has to drop something.
        var events = ToolHistory(pairs: 200);
        string block = ProjectCommanderPrompts.RenderHistoryBlock("── RECENT EVENTS ──", events, budget: 2_000);

        // Every retained group is identified by its probe index; the kept set must be a suffix.
        var kept = Enumerable.Range(0, 200).Where(i => block.Contains($"[probe-{i}]", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(kept);
        Assert.Equal(199, kept[^1]);
        Assert.Equal(Enumerable.Range(kept[0], kept.Count), kept);
    }

    [Fact]
    public void OmittedHistory_SaysSo_AndPointsAtQueryEvents()
    {
        var events = ToolHistory(pairs: 200);
        string block = ProjectCommanderPrompts.RenderHistoryBlock("── RECENT EVENTS ──", events, budget: 2_000);

        Assert.Contains("older event group(s) omitted", block, StringComparison.Ordinal);
        Assert.Contains("query_events", block, StringComparison.Ordinal);
        // The notice names the boundary so the agent knows where to start reading backwards.
        Assert.Contains("complete and unbroken back to", block, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingOmitted_MeansNoNotice()
    {
        var events = ToolHistory(pairs: 3, textLength: 40);
        string block = ProjectCommanderPrompts.RenderHistoryBlock("── RECENT EVENTS ──", events, budget: 48_000);
        Assert.DoesNotContain("omitted", block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OlderToolPair_CollapsesToOneLine_WithSuccessMark()
    {
        // 30 pairs: the newest 20 stay verbatim (two lines each), the older ones compact to one.
        var events = ToolHistory(pairs: 30, textLength: 40);
        string block = ProjectCommanderPrompts.RenderHistoryBlock("── RECENT EVENTS ──", events, budget: 48_000);

        // probe-0 is the oldest, so it is in the compacted region: one line carrying call → result.
        var compacted = block.Split('\n').Where(l => l.Contains("[probe-0]", StringComparison.Ordinal)).ToList();
        Assert.Single(compacted);
        Assert.Contains("→", compacted[0]);
        Assert.Contains("result-0", compacted[0]);
        Assert.Contains("✓", compacted[0]);
        Assert.Contains("#1→#2", compacted[0].Replace("#1→2", "#1→#2")); // span notation, either rendering
    }

    [Fact]
    public void FailedToolResult_IsMarkedFailed_InTheCompactedRegion()
    {
        var events = new List<ProjectEvent>();
        long seq = 1;
        events.Add(Evt(seq++, ProjectEventTypes.ToolCall, "commander", "computer_navigate(url=example.com)", "computer_navigate", "c1"));
        events.Add(Evt(seq++, ProjectEventTypes.ToolResult, "commander", "403 Forbidden", "computer_navigate", "c1", succeeded: false));
        // Push the pair out of the full-detail band.
        for (int i = 0; i < 25; i++) events.Add(Evt(seq++, ProjectEventTypes.Status, "system", "filler " + i));

        string block = ProjectCommanderPrompts.RenderHistoryBlock("── RECENT EVENTS ──", events, budget: 48_000);
        var line = block.Split('\n').First(l => l.Contains("computer_navigate", StringComparison.Ordinal));
        Assert.Contains("✗", line);
        Assert.Contains("403 Forbidden", line);
    }

    [Fact]
    public void CompactionReachesMuchDeeper_AtTheProductionBudget()
    {
        var events = ToolHistory(pairs: 600);
        const int budget = ProjectsContextBudget.RecentEventsBudget;

        string compacted = ProjectCommanderPrompts.RenderHistoryBlock("── RECENT EVENTS ──", events, budget);
        int compactedGroups = Enumerable.Range(0, 600).Count(i => compacted.Contains($"[probe-{i}]", StringComparison.Ordinal));

        // The old window rendered every retained entry in full; approximate it by asking for full detail
        // throughout, which is what the same budget used to buy.
        var full = ProjectsContextBudget.FitEventsChronologically(
            ProjectCommanderPrompts.GroupHistory(events), budget,
            ProjectCommanderPrompts.DescribeHistoryItemFull,
            ProjectCommanderPrompts.DescribeHistoryItemFull);

        Assert.True(compactedGroups >= full.Lines.Count * 3,
            $"expected compaction to reach materially deeper: {compactedGroups} vs {full.Lines.Count} groups");
    }

    [Fact]
    public void AReducedBudgetStillBuysDepth_TheVerbatimBandCannotEatItAll()
    {
        // 20 full tool exchanges can cost more than a small budget on its own. If the verbatim band were a
        // fixed count it would spend the whole allowance on the newest few — exactly when reaching back
        // matters most — so it is capped as a share of the budget instead.
        var events = ToolHistory(pairs: 300);
        const int budget = 4_000;

        string block = ProjectCommanderPrompts.RenderHistoryBlock("── RECENT EVENTS ──", events, budget);
        int groups = Enumerable.Range(0, 300).Count(i => block.Contains($"[probe-{i}]", StringComparison.Ordinal));

        var full = ProjectsContextBudget.FitEventsChronologically(
            ProjectCommanderPrompts.GroupHistory(events), budget,
            ProjectCommanderPrompts.DescribeHistoryItemFull,
            ProjectCommanderPrompts.DescribeHistoryItemFull);

        Assert.True(groups > full.Lines.Count,
            $"a reduced budget should still reach further than all-verbatim: {groups} vs {full.Lines.Count} groups");
    }

    [Fact]
    public void KlivesWordsAndApprovals_StayVerbatim_HoweverOldTheyAre()
    {
        string longSteer = "Do not complete this project yet — keep digging deeper on the GitHub commit history, "
            + "the EPCC participant list, and any conference appearances. " + new string('z', 600);
        var events = new List<ProjectEvent>
        {
            Evt(1, ProjectEventTypes.KlivesMessage, "klives", longSteer),
            Evt(2, ProjectEventTypes.ApprovalResolved, "klives", "Deny: not until you've exhausted public sources"),
        };
        // Bury both far outside the full-detail band.
        for (long seq = 3; seq <= 60; seq++)
            events.Add(Evt(seq, ProjectEventTypes.Status, "system", "routine progress note " + seq));

        string block = ProjectCommanderPrompts.RenderHistoryBlock("── RECENT EVENTS ──", events, budget: 48_000);

        // Policy-bearing entries are never compacted: the whole steer survives, not a 240-char fragment.
        Assert.Contains(longSteer, block, StringComparison.Ordinal);
        Assert.Contains("Deny: not until you've exhausted public sources", block, StringComparison.Ordinal);
    }

    [Fact]
    public void UnpairedToolResult_StillRenders()
    {
        // A call whose result was filtered out (or vice versa) must not vanish or throw.
        var events = new List<ProjectEvent> { Evt(9, ProjectEventTypes.ToolResult, "commander", "orphan result", "grep", "gone", succeeded: false) };
        for (long seq = 10; seq <= 40; seq++) events.Add(Evt(seq, ProjectEventTypes.Status, "system", "filler " + seq));

        string block = ProjectCommanderPrompts.RenderHistoryBlock("── RECENT EVENTS ──", events, budget: 48_000);
        Assert.Contains("orphan result", block, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyHistory_RendersNothing()
    {
        Assert.Equal("", ProjectCommanderPrompts.RenderHistoryBlock("── RECENT EVENTS ──", new List<ProjectEvent>(), 48_000));
    }

    [Fact]
    public void TinyBudget_StillKeepsTheNewestEntry()
    {
        // Better one over-budget line than a window that says nothing happened.
        var events = ToolHistory(pairs: 5, textLength: 2_000);
        string block = ProjectCommanderPrompts.RenderHistoryBlock("── RECENT EVENTS ──", events, budget: 10);
        Assert.Contains("[probe-4]", block, StringComparison.Ordinal);
    }

    [Fact]
    public void RetrievalQuery_IsDistinctTermsRatherThanABlob()
    {
        // BM25 handed a 1,500-character blob matches most of the log; distinct terms rank properly.
        string query = ProjectsContextBudget.BuildRetrievalQuery(40,
            "Compile an OSINT profile on the subject",
            "the the the and of a to",
            "verify middle name via a second source",
            null);

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(terms.Length, terms.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain("the", terms, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("OSINT", terms, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("middle", terms, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetrievalQuery_RespectsTheTermCap()
    {
        string query = ProjectsContextBudget.BuildRetrievalQuery(5,
            string.Join(" ", Enumerable.Range(0, 100).Select(i => "term" + i)));
        Assert.Equal(5, query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}
