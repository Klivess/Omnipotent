using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Projects;
using Xunit.Abstractions;
using Llm = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.KliveLLM;

public class BriefContinuityTests(ITestOutputHelper output)
{
    private const string Id = "projects-commander-continuity-test";
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
    private static Llm.KliveLLMSession Session(Llm llm) =>
        ((Dictionary<string, Llm.KliveLLMSession>)typeof(Llm).GetField("sessions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(llm)!)[Id];
    private static ToolSessionBriefSection[] Brief(string directive = "Do the work", string clock = "12:00", bool approval = true) =>
        new[]
        {
            new ToolSessionBriefSection("directives", "── DIRECTIVES ──\n" + directive + "\n"),
            new ToolSessionBriefSection("plan", "── PLAN ──\n" + new string('p', 4000) + "\n"),
            new ToolSessionBriefSection("trigger", "── TRIGGER ──\nNow: " + clock + "\ncontinue\n", AlwaysSend: true),
        }.Concat(approval ? new[] { new ToolSessionBriefSection("approvals", "── APPROVALS ──\nBuy once\n") } : Array.Empty<ToolSessionBriefSection>()).ToArray();
    private static Llm.BriefSessionStart Begin(Llm llm, ToolSessionBriefSection[]? brief = null,
        string key = "route", int budget = 80_000, DateTime? now = null, string system = "SYSTEM") =>
        llm.StartOrContinueBriefedToolSession(Id, system, brief ?? Brief(), key, budget, now ?? Now);

    [Fact]
    public void WakeUpdate_PreservesEverySentByteAndRefreshesChangedAndClearedState()
    {
        var llm = new Llm();
        Assert.False(Begin(llm).Continued);
        CompleteBatch(Session(llm), 1);
        string sent = JsonConvert.SerializeObject(Session(llm).structuredMessages);
        int count = Session(llm).structuredMessages.Count;

        var result = Begin(llm, Brief("Do not buy anything", "12:01", approval: false));
        Assert.True(result.Continued);
        Assert.Equal(sent, JsonConvert.SerializeObject(Session(llm).structuredMessages.Take(count)));
        string delta = HFWrapper.ContentToText(Session(llm).structuredMessages[^1].content);
        Assert.Contains("Do not buy anything", delta);
        Assert.Contains("SECTION CLEARED: approvals", delta);
        Assert.Contains("12:01", delta);
        Assert.DoesNotContain(new string('p', 100), delta);
        Assert.True(result.AppendedTokens < result.FullBriefTokens / 4);
    }

    [Fact]
    public void Journal_SendsOnlyUnseenEntriesEvenWhenOldRenderingBecomesCompact()
    {
        var llm = new Llm();
        ToolSessionBriefSection Journal(string one, bool more) => new("events", "── EVENTS ──\n" + one,
            new[] { new ToolSessionBriefEntry("1:", one) }.Concat(more ? new[] { new ToolSessionBriefEntry("2:", "NEW EVENT") } : []).ToArray());
        Begin(llm, Brief().Append(Journal("OLD VERBATIM", false)).ToArray());
        Begin(llm, Brief().Append(Journal("OLD COMPACT", true)).ToArray());
        string delta = HFWrapper.ContentToText(Session(llm).structuredMessages[^1].content);
        Assert.Contains("NEW EVENT", delta);
        Assert.DoesNotContain("OLD", delta);
    }

    [Theory]
    [InlineData("configuration-changed")]
    [InlineData("idle-expired")]
    [InlineData("context-limit")]
    [InlineData("incomplete-tool-batch")]
    public void IncompatibleOrUnsafeSession_RehydratesCurrentState(string reason)
    {
        var llm = new Llm();
        Begin(llm);
        if (reason == "incomplete-tool-batch") CompleteBatch(Session(llm), 1, complete: false);
        if (reason == "context-limit") Session(llm).lastPromptTokens = 90_000;
        var start = Begin(llm, Brief("LATEST DIRECTIVE"), key: reason == "configuration-changed" ? "new-route" : "route",
            now: reason == "idle-expired" ? Now.AddMinutes(11) : Now);
        Assert.False(start.Continued);
        Assert.Equal(reason, start.Reason);
        Assert.Equal(2, Session(llm).structuredMessages.Count);
        Assert.Contains("LATEST DIRECTIVE", HFWrapper.ContentToText(Session(llm).structuredMessages[1].content));
    }

    [Fact]
    public void ChangedSystemOrTools_RotateWithoutMixingAssignments()
    {
        var llm = new Llm();
        Begin(llm);
        Assert.False(Begin(llm, system: "NEW ASSIGNMENT").Continued);
        var tools = new List<HFWrapper.HFTool>();
        string first = ProjectPromptContinuity.CompatibilityKey("provider", new[] { "route" }, tools, null);
        tools.Add(new() { function = new() { name = "new-tool", description = "changed" } });
        Assert.NotEqual(first, ProjectPromptContinuity.CompatibilityKey("provider", new[] { "route" }, tools, null));
    }

    [Fact]
    public void Compaction_ProtectsLatestDirectiveAndClearedApprovalInsteadOfObsoleteSeed()
    {
        var llm = new Llm();
        Begin(llm);
        for (int i = 0; i < 12; i++) CompleteBatch(Session(llm), i);
        Begin(llm, Brief("CURRENT POLICY", approval: false));
        for (int i = 12; i < 22; i++) CompleteBatch(Session(llm), i);
        Assert.True(Llm.CompactToolSessionIfNeeded(Session(llm), 7000, 4, 1));
        string seed = HFWrapper.ContentToText(Session(llm).structuredMessages[1].content);
        Assert.Contains("CURRENT POLICY", seed);
        Assert.DoesNotContain("Buy once", seed);
        Assert.True(Llm.HasCompleteToolExchanges(Session(llm).structuredMessages));
        // The next refresh must not assume a forgotten update is still present.
        Assert.True(Begin(llm, Brief("CURRENT POLICY", "12:02", approval: false)).Continued);
    }

    [Fact]
    public async Task ImpossibleWindow_FailsBeforeNetworkRatherThanClippingCurrentDirectives()
    {
        var llm = new Llm();
        Begin(llm, Brief(new string('D', 20_000)));
        string original = HFWrapper.ContentToText(Session(llm).structuredMessages[1].content);
        var response = await llm.QueryToolSessionAsync(Id, new(), maxTokensOverride: 500,
            contextWindowTokensOverride: 4000, enableOpenRouterContextCompression: true);
        Assert.False(response.Success);
        Assert.Contains("authoritative brief", response.ErrorMessage);
        Assert.Equal(original, HFWrapper.ContentToText(Session(llm).structuredMessages[1].content));
    }

    [Fact]
    public void StructuredCommanderBrief_IsIdenticalToExistingSeedAndKeepsJournalIdentities()
    {
        var sections = new List<ToolSessionBriefSection>();
        var events = Enumerable.Range(1, 60).Select(i => new ProjectEvent
        {
            Sequence = i, Timestamp = Now, Type = ProjectEventTypes.CommanderMessage, Author = "commander", Text = $"Entry {i}",
        }).ToList();
        string seed = ProjectCommanderPrompts.BuildWakeSeed(new Project { ProjectID = "p", Goal = "Build" },
            new ProjectDigest(), events, new(), "go", directivesBlock: "── SOME BODY HEADING ──\nObey this", briefSections: sections);
        Assert.Equal(seed, ToolSessionBriefState.FullText(sections));
        Assert.Single(sections.Where(section => section.Key == "directives"));
        var journal = Assert.Single(sections.Where(section => section.Key == "recent-events"));
        Assert.Equal(60, journal.Entries!.Count);
        Assert.Equal("1:", journal.Entries[0].Key);
        Assert.Equal("60:", journal.Entries[^1].Key);
        Assert.True(sections.Single(section => section.Key == "trigger").AlwaysSend);
    }

    [Fact]
    public void RollingBreakpoints_CacheToolsAndPinPreviousWriteAcrossLargeBatchesWithoutEditingHistory()
    {
        var llm = new Llm();
        Begin(llm);
        CompleteBatch(Session(llm), 0);
        int priorCount = Session(llm).structuredMessages.Count;
        for (int i = 1; i < 25; i++) CompleteBatch(Session(llm), i);
        string original = JsonConvert.SerializeObject(Session(llm).structuredMessages);
        var payload = new HFWrapper.HFLLMInferenceRequest();
        payload.BuildMessagesFromList(Session(llm).structuredMessages);
        Llm.ApplyPromptCaching(ref payload, Router(), priorCount);
        var json = JObject.FromObject(payload);
        Assert.NotNull(json["messages"]![priorCount - 1]!["content"]![0]!["cache_control"]);
        Assert.NotNull(json["messages"]!.Last!["content"]![0]!["cache_control"]);
        Assert.Equal(3, json.SelectTokens("$..messages[*].content[*].cache_control").Count());
        Assert.Equal(original, JsonConvert.SerializeObject(Session(llm).structuredMessages));
        Assert.Equal("tool", payload.messages[^1].role);
    }

    [Fact]
    public void MediaBreakpoint_DoesNotAddWhitespaceOrMutateParts()
    {
        var text = new HFWrapper.HFTextPart { text = "screenshot" };
        var content = new List<object> { text, new HFWrapper.HFImagePart() };
        var payload = new HFWrapper.HFLLMInferenceRequest { messages = [new() { role = "user", content = content }] };
        Llm.ApplyConversationCacheBreakpoint(payload);
        Assert.Equal(2, Assert.IsType<List<object>>(payload.messages[0].content).Count);
        Assert.Null(text.cache_control);
        Assert.Equal("screenshot", HFWrapper.ContentToText(payload.messages[0].content));
    }

    [Fact]
    public void ReferenceBudget_ReducesReplayButPreservesDirectivesApprovalsAndPolicyEvents()
    {
        var journal = Enumerable.Range(1, 30).Select(i => new ToolSessionBriefEntry(i.ToString(),
            new string((char)('a' + i % 20), 3000), MustKeep: i == 1)).ToArray();
        var raw = Brief().Append(new ToolSessionBriefSection("files", new string('f', 16000)))
            .Append(new ToolSessionBriefSection("recent-events", "── EVENTS ──\n" + string.Join("\n", journal.Select(entry => entry.Text)), journal)).ToArray();
        var fitted = ProjectPromptContinuity.FitReferences(raw);
        Assert.Equal(raw.Single(s => s.Key == "directives"), fitted.Single(s => s.Key == "directives"));
        Assert.Equal(raw.Single(s => s.Key == "approvals"), fitted.Single(s => s.Key == "approvals"));
        Assert.True(ToolSessionBriefState.FullText(fitted).Length < ToolSessionBriefState.FullText(raw).Length / 3);
        var entries = fitted.Single(s => s.Key == "recent-events").Entries!;
        Assert.Contains(entries, entry => entry.Key == "1");
        Assert.Contains(entries, entry => entry.Key == "30");
    }

    [Fact]
    public void EmptyCacheDetails_AreUnknownWhereasExplicitZeroIsMeasured()
    {
        var missing = JsonConvert.DeserializeObject<HFWrapper.HFLLMInferenceResponse.PromptTokensDetails>("{\"audio_tokens\":40}")!;
        var zero = JsonConvert.DeserializeObject<HFWrapper.HFLLMInferenceResponse.PromptTokensDetails>("{\"cached_tokens\":0}")!;
        Assert.False(missing.HasCacheReadMetrics);
        Assert.True(zero.HasCacheReadMetrics);
    }

    [Fact]
    public void MultiWakeReplay_MeasuresWeightedReuseAndAbsoluteUncachedVolumeIncludingColdStarts()
    {
        var legacy = Replay(continuity: false);
        var current = Replay(continuity: true);
        output.WriteLine($"Replay: real commander doctrine/tool schemas with synthetic reference data; 24 wakes x 8 turns, 600-token tool results; cold starts and rotations included. " +
            $"Before: {legacy.Hit:P2}, {legacy.Uncached:N0} uncached character-equivalent tokens, {legacy.Total:N0} total. " +
            $"After: {current.Hit:P2}, {current.Uncached:N0} uncached, {current.Total:N0} total, {current.Rotations} rotations. " +
            $"Uncached reduction: {1 - current.Uncached / legacy.Uncached:P2}. " +
            $"Input cost at 10% cached-read price: {1 - (current.Uncached + .1 * (current.Total - current.Uncached)) / (legacy.Uncached + .1 * (legacy.Total - legacy.Uncached)):P2} lower.");
        Assert.True(current.Uncached < legacy.Uncached * .55);
        Assert.True(current.Hit > .95);
        double BeforeCost(double readRate) => legacy.Uncached + readRate * (legacy.Total - legacy.Uncached);
        double AfterCost(double readRate) => current.Uncached + readRate * (current.Total - current.Uncached);
        Assert.True(AfterCost(.1) < BeforeCost(.1) * .80);
        Assert.True(AfterCost(.25) < BeforeCost(.25) * .85);
        Assert.True(current.Rotations > 0); // the benchmark must exercise bounded rebasing
    }

    private static (double Hit, double Uncached, double Total, int Rotations) Replay(bool continuity)
    {
        var llm = new Llm();
        string previous = "";
        double total = 0, reused = 0;
        int rotations = 0;
        var project = new Project { ProjectID = "replay", Name = "Replay", Goal = "Execute the plan" };
        string doctrine = ProjectCommanderAgent.BuildSystemPrompt(project, visionEnabled: false);
        var tools = ProjectToolFacade.Fold(ProjectAgentToolCatalog.BuildCommanderCanonical(visionEnabled: false));
        string toolPrefix = JsonConvert.SerializeObject(tools) + "\n";
        for (int wake = 0; wake < 24; wake++)
        {
            var brief = new[]
            {
                new ToolSessionBriefSection("directives", "Project rules\n" + new string('d', 5000)),
                new ToolSessionBriefSection("state", $"State revision: {wake}\n" + new string('s', 800)),
                new ToolSessionBriefSection("files", "── FILES ──\n" + new string('f', 16000)),
                new ToolSessionBriefSection("knowledge", "── KNOWLEDGE ──\n" + new string('k', 16000)),
                new ToolSessionBriefSection("kliveagent", "── CAPABILITIES ──\n" + new string('r', 16000)),
                new ToolSessionBriefSection("trigger", $"Now: wake {wake}\nContinue the next task.\n", AlwaysSend: true),
            };
            if (continuity)
            {
                var start = Begin(llm, ProjectPromptContinuity.FitReferences(brief).ToArray(), budget: 96_000, system: doctrine);
                if (!start.Continued && wake > 0) rotations++;
            }
            else
            {
                llm.StartToolSession(Id, doctrine);
                llm.AppendCacheStableWakeSeedToToolSession(Id, ToolSessionBriefState.FullText(brief));
            }
            for (int turn = 0; turn < 8; turn++)
            {
                var payload = new HFWrapper.HFLLMInferenceRequest();
                payload.BuildMessagesFromList(Session(llm).structuredMessages);
                Llm.ApplyPromptCaching(ref payload, new(Llm.LLMProvider.AIRouter, "test", "https://example.invalid", "unused", "test"));
                string current = toolPrefix + string.Join("\n", payload.messages.Select(message => JsonConvert.SerializeObject(message)));
                int prefix = 0;
                while (prefix < Math.Min(previous.Length, current.Length) && previous[prefix] == current[prefix]) prefix++;
                total += current.Length / 4.0;
                reused += prefix / 4.0;
                previous = current;
                CompleteBatch(Session(llm), wake * 8 + turn);
            }
        }
        return (reused / total, total - reused, total, rotations);
    }

    private static void CompleteBatch(Llm.KliveLLMSession session, int index, bool complete = true)
    {
        session.structuredMessages.Add(new HFWrapper.HFMessage { role = "assistant", content = "Next action", tool_calls =
            [new() { id = $"call-{index}", function = new() { name = "read", arguments = "{}" } }] });
        if (complete) session.structuredMessages.Add(new HFWrapper.HFMessage
            { role = "tool", tool_call_id = $"call-{index}", name = "read", content = new string('x', 2400) });
    }
    private static Llm.RemoteLLMProviderConfiguration Router() =>
        new(Llm.LLMProvider.OpenRouter, "OpenRouter", "https://example.invalid", "unused", "test");
}
