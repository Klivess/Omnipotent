using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Projects;
using LlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.Projects;

/// <summary>
/// Guards the context-retention contract that keeps a long wake both cheap AND competent.
///
/// The expensive failure is obvious in the token bill; the dangerous one is silent. A wake seed is a
/// single user message carrying the agent's entire rehydrated brief — directives, grand plan, typed
/// execution state, verified facts, dead ends, retrieval hits and the recent-event window. The generic
/// compactor summarises a user turn down to 240 characters, so without an explicit protected prefix a
/// compacting wake loses all of it mid-flight and starts repeating work it has already done.
/// </summary>
public class ProjectContextRetentionTests
{
    private const string SeedMarker = "DIRECTIVE-ALPHA-MUST-SURVIVE";

    /// A realistic seed: the marker sits deep inside it, the way a real directive or verified fact sits
    /// thousands of characters into the block. Anything within the first 240 characters would survive
    /// summarisation by accident and prove nothing.
    private static string Seed(int padChars = 12_000) =>
        "WAKE SEED — " + new string('s', padChars) + " " + SeedMarker + " " + new string('e', 400);

    /// Comfortably above the post-compaction size (protected seed + kept tail + digest) but far below
    /// the pre-compaction size, so compaction fires without the last-resort trimmer engaging.
    private const int CompactBudget = 12_000;

    private static LlmService.KliveLLMSession BuildSession(string seedText, int exchanges)
    {
        // The real constructor wires up a live KliveLLM service (and, for local models, llama context).
        // Compaction only ever touches structuredMessages, so build the object without running it.
        var session = (LlmService.KliveLLMSession)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(LlmService.KliveLLMSession));
        session.sessionId = "test";
        session.structuredMessages = new List<HFWrapper.HFMessage>();
        session.structuredMessages.Add(new HFWrapper.HFMessage { role = "system", content = "SYSTEM DOCTRINE" });
        session.structuredMessages.Add(new HFWrapper.HFMessage { role = "user", content = seedText });

        // Plenty of bulky tool churn, which is what compaction is actually meant to collapse.
        for (int i = 0; i < exchanges; i++)
        {
            session.structuredMessages.Add(new HFWrapper.HFMessage
            {
                role = "assistant",
                content = $"step {i} reasoning " + new string('r', 400),
                tool_calls = new List<HFWrapper.HFToolCall>
                {
                    new() { id = $"call{i}", function = new HFWrapper.HFFunctionCall { name = "web_fetch", arguments = "{}" } },
                },
            });
            session.structuredMessages.Add(new HFWrapper.HFMessage
            {
                role = "tool",
                tool_call_id = $"call{i}",
                name = "web_fetch",
                content = $"result {i} " + new string('x', 4000),
            });
        }
        return session;
    }

    [Fact]
    public void Compaction_WithoutProtection_DestroysTheWakeSeed()
    {
        // Documents the behaviour that made protection necessary. If this ever stops holding, the
        // protected-prefix parameter is no longer load-bearing and the guard below can be revisited.
        var session = BuildSession(Seed(), exchanges: 20);

        bool compacted = LlmService.CompactToolSessionIfNeeded(session, CompactBudget, keepRecent: 6);

        Assert.True(compacted);
        Assert.DoesNotContain(session.structuredMessages,
            m => HFWrapper.ContentToText(m.content).Contains(SeedMarker, StringComparison.Ordinal));
    }

    [Fact]
    public void Compaction_ProtectsTheWakeSeedVerbatim()
    {
        string seed = Seed();
        var session = BuildSession(seed, exchanges: 20);

        bool compacted = LlmService.CompactToolSessionIfNeeded(
            session, CompactBudget, keepRecent: 6, protectPrefixMessages: 1);

        Assert.True(compacted);

        // The seed survives byte-for-byte, still immediately after the system block.
        var preserved = session.structuredMessages[1];
        Assert.Equal("user", preserved.role);
        Assert.Equal(seed, HFWrapper.ContentToText(preserved.content));

        // ...and compaction still did its job on the churn behind it.
        Assert.True(session.structuredMessages.Count < 42);
    }

    [Fact]
    public void Compaction_SeedOverflowDegradesGracefullyRatherThanBeingGutted()
    {
        // If the seed ALONE still blows the window, the last-resort trimmer middle-truncates it. That
        // keeps the front (project/directives) and the back (trigger/latest evidence) — very different
        // from the 240-char summarisation clip, which keeps only the opening sentence.
        string seed = Seed(padChars: 60_000);
        var session = BuildSession(seed, exchanges: 4);

        LlmService.CompactToolSessionIfNeeded(
            session, aboveTokens: 2_000, keepRecent: 4, protectPrefixMessages: 1);

        string preserved = HFWrapper.ContentToText(session.structuredMessages[1].content);
        Assert.True(preserved.Length < seed.Length, "an over-budget seed should be trimmed");
        // Both ENDS survive — the opening context and the trailing trigger/latest evidence.
        Assert.StartsWith("WAKE SEED —", preserved, StringComparison.Ordinal);
        Assert.EndsWith(new string('e', 50), preserved, StringComparison.Ordinal);
    }

    [Fact]
    public void Compaction_ProtectsSeedAndCarriedWakeTail()
    {
        const string tailMarker = "PREVIOUS-WAKE-TAIL-MUST-SURVIVE";
        string seed = Seed(padChars: 8_000);
        var session = BuildSession(seed, exchanges: 20);
        session.structuredMessages.Insert(2, new HFWrapper.HFMessage
        {
            role = "user",
            content = "── HOW YOUR PREVIOUS WAKE ENDED ──\n" + tailMarker,
        });

        LlmService.CompactToolSessionIfNeeded(
            session, aboveTokens: 4_000, keepRecent: 6, protectPrefixMessages: 2);

        string all = string.Join("\n", session.structuredMessages.Select(m => HFWrapper.ContentToText(m.content)));
        Assert.Contains(SeedMarker, all, StringComparison.Ordinal);
        Assert.Contains(tailMarker, all, StringComparison.Ordinal);
    }

    [Fact]
    public void Compaction_NeverProtectsAwayTheEntireConversation()
    {
        // A caller passing an absurd protect count must not be able to disable compaction outright,
        // which would silently reintroduce unbounded context growth.
        string seed = Seed(padChars: 8_000);
        var session = BuildSession(seed, exchanges: 20);
        int before = session.structuredMessages.Count;

        LlmService.CompactToolSessionIfNeeded(
            session, aboveTokens: 4_000, keepRecent: 4, protectPrefixMessages: 9_999);

        Assert.True(session.structuredMessages.Count <= before);
    }

    [Fact]
    public void ImageTokenEstimate_ReflectsRealFrameCost()
    {
        // The wake loop used to charge a flat 1200 per frame, which under-counted a 1080p screenshot
        // badly enough that context routinely overshot its ceiling.
        long fullHd = ProjectsContextBudget.EstimateImageTokens(1920, 1080);
        long smaller = ProjectsContextBudget.EstimateImageTokens(1280, 800);

        Assert.True(fullHd > 1200, $"1920x1080 should cost more than the old flat estimate, got {fullHd}");
        Assert.True(smaller < fullHd, "a smaller framebuffer must cost fewer tokens");
        Assert.Equal(1600, ProjectsContextBudget.EstimateImageTokens(0, 0)); // defensive fallback
    }
}
