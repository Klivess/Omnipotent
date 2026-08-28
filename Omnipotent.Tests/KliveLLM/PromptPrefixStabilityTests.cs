using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Projects;
using LlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.KliveLLM;

/// <summary>
/// Guards the prefix-cache contract described in <see cref="PromptPrefixStability"/>.
///
/// A provider's prefix cache can only reuse tokens that are byte-identical from the START of the
/// prompt; the match ends at the first differing byte and everything after it is re-prefilled.
/// That makes cache behaviour a structural property of prompt assembly, and it fails SILENTLY —
/// nothing in the app breaks, the requests just cost several times more to serve and take longer
/// to first token. These tests exist because that failure is otherwise invisible from inside the
/// codebase: it only shows up on the provider's side of the wire.
///
/// The two regressions guarded here are the two that actually happened:
///   • a context-hygiene pass rewriting one already-sent message per turn, which pins the hit rate
///     to a low constant for the whole session no matter how long the conversation runs; and
///   • per-request or per-agent text (a wall-clock, a relative age, an agent id) sitting in the
///     opening lines of a prompt, which discards the cache for everything behind it.
/// </summary>
[Collection("ProjectsSerial")]
public class PromptPrefixStabilityTests
{
    // ── helpers ──

    private static LlmService.KliveLLMSession NewSession(string systemPrompt = "SYSTEM DOCTRINE", string seed = "WAKE SEED")
    {
        // The real constructor wires up a live KliveLLM service; these paths only ever touch
        // structuredMessages, so build the object without running it (as ProjectContextRetentionTests does).
        var session = (LlmService.KliveLLMSession)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(LlmService.KliveLLMSession));
        session.sessionId = "prefix-test";
        session.structuredMessages = new List<HFWrapper.HFMessage>
        {
            new() { role = "system", content = systemPrompt },
            new() { role = "user", content = seed },
        };
        return session;
    }

    private static void AppendExchange(LlmService.KliveLLMSession s, int i, int resultChars = 4000)
    {
        s.structuredMessages.Add(new HFWrapper.HFMessage
        {
            role = "assistant",
            content = $"step {i}",
            tool_calls = new List<HFWrapper.HFToolCall>
            {
                new() { id = $"call{i}", function = new HFWrapper.HFFunctionCall { name = "web_fetch", arguments = "{}" } },
            },
        });
        s.structuredMessages.Add(new HFWrapper.HFMessage
        {
            role = "tool",
            tool_call_id = $"call{i}",
            name = "web_fetch",
            content = $"result {i} " + new string('x', resultChars),
        });
    }

    /// The bytes a provider would see for the messages that ALREADY existed before this turn. If any
    /// of them changes, the prefix cache match ends there and the whole tail is re-prefilled.
    private static List<string> SentBytes(LlmService.KliveLLMSession s, int count) =>
        s.structuredMessages.Take(count).Select(m => HFWrapper.ContentToText(m.content)).ToList();

    private static int RewrittenCount(List<string> before, List<string> after) =>
        before.Where((text, i) => !string.Equals(text, after[i], StringComparison.Ordinal)).Count();

    /// The longest run of characters two prompts share from the start — exactly what a prefix cache
    /// can reuse between them.
    private static int SharedPrefixLength(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length), i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    // ── rule 2: never rewrite what you have already sent (unbatched) ──

    [Fact]
    public void ToolResultPruning_DoesNotRewriteSentHistoryOnEveryTurn()
    {
        // The regression: stubbing exactly one newly-old tool result per turn. Every turn then
        // changed a message the previous request had already sent, so the provider's match ended in
        // the middle of the transcript EVERY time and the tail behind it was re-prefilled on every
        // single turn — a constant low hit rate that no amount of conversation length improves.
        const int keepRecent = 16;
        var session = NewSession();
        int turns = 0, turnsThatRewroteHistory = 0;

        for (int i = 0; i < 60; i++)
        {
            int sentCount = session.structuredMessages.Count;
            var before = SentBytes(session, sentCount);

            AppendExchange(session, i);
            LlmService.PruneOldToolResults(session, keepRecent);

            turns++;
            if (RewrittenCount(before, SentBytes(session, sentCount)) > 0) turnsThatRewroteHistory++;
        }

        // With hysteresis the rewrite happens once per batch, not once per turn. At the default
        // retention of 16 that is a batch of 16, so ~1 turn in 16 disturbs the prefix.
        int batch = PromptPrefixStability.ToolResultPruneBatch(keepRecent);
        Assert.True(turnsThatRewroteHistory <= (turns / batch) + 1,
            $"history was rewritten on {turnsThatRewroteHistory}/{turns} turns; " +
            $"a batch of {batch} should keep it at or under {(turns / batch) + 1}");

        // And the point of the whole exercise: most turns extend a byte-identical prefix.
        Assert.True(turnsThatRewroteHistory * 4 < turns,
            $"only {turns - turnsThatRewroteHistory}/{turns} turns left the sent prefix intact");
    }

    [Fact]
    public void ToolResultPruning_StillBoundsWhatTheSessionCarries()
    {
        // Hysteresis trades a little retained context for a lot of cache. The trade has to stay
        // bounded: a batch that never fires would reintroduce unbounded transcript growth.
        const int keepRecent = 16;
        var session = NewSession();
        for (int i = 0; i < 200; i++)
        {
            AppendExchange(session, i);
            LlmService.PruneOldToolResults(session, keepRecent);
        }

        int fullSized = session.structuredMessages.Count(m => m.role == "tool"
            && !HFWrapper.ContentToText(m.content).Contains("trimmed to save context", StringComparison.Ordinal));
        Assert.True(fullSized <= keepRecent + PromptPrefixStability.ToolResultPruneBatch(keepRecent),
            $"{fullSized} full-size tool results retained; the batch must not defeat the retention cap");
    }

    [Fact]
    public void ToolResultPruning_StubbingIsIdempotent()
    {
        // The batch counts results still holding their full text. If re-stubbing an already-stubbed
        // result counted as outstanding work, the batch would fire forever and the hysteresis would
        // be worthless — so prove a second pass changes nothing.
        var session = NewSession();
        for (int i = 0; i < 40; i++) AppendExchange(session, i);
        LlmService.PruneOldToolResults(session, keepRecent: 4);

        var after = SentBytes(session, session.structuredMessages.Count);
        LlmService.PruneOldToolResults(session, keepRecent: 4);

        Assert.Equal(0, RewrittenCount(after, SentBytes(session, session.structuredMessages.Count)));
    }

    [Fact]
    public void ScreenshotFlattening_IsBatchedToo()
    {
        // Same failure mode on the vision path, and worse per turn: a flattened frame is a message an
        // earlier request already sent, and the frames behind it are the most expensive tokens in the
        // whole prompt.
        const int keepRecent = 3;
        var session = NewSession();
        int turnsThatRewroteHistory = 0;

        for (int i = 0; i < 30; i++)
        {
            int sentCount = session.structuredMessages.Count;
            var before = SentBytes(session, sentCount);

            session.structuredMessages.Add(new HFWrapper.HFMessage
            {
                role = "user",
                content = new List<object>
                {
                    new HFWrapper.HFTextPart { text = $"frame {i}" },
                    new HFWrapper.HFImagePart { image_url = new HFWrapper.HFImageUrl { url = "data:image/png;base64,AAAA" } },
                },
            });
            LlmService.PruneOldToolImages(session, keepRecent);

            if (RewrittenCount(before, SentBytes(session, sentCount)) > 0) turnsThatRewroteHistory++;
        }

        Assert.True(turnsThatRewroteHistory < 30 / 2,
            $"screenshots were flattened on {turnsThatRewroteHistory}/30 turns — that is the unbatched behaviour");

        // Still bounded: the retention cap plus at most one batch in flight.
        int framesHeld = session.structuredMessages.Count(m =>
            HFWrapper.ContentToText(m.content).Contains("frame ", StringComparison.Ordinal));
        Assert.True(framesHeld <= keepRecent + PromptPrefixStability.MediaPruneBatch(keepRecent),
            $"{framesHeld} frames retained; the batch must not defeat the retention cap");
    }

    // ── rule 2: compaction must leave headroom ──

    [Fact]
    public void Compaction_LeavesHeadroomSoItDoesNotRefireOnTheNextTurn()
    {
        // Compaction rewrites the MIDDLE of a transcript, so it is the most destructive thing we do
        // to a cached prefix. Compacting to just under the trigger meant the next tool result put the
        // session straight back over the line, so a long wake re-wrote — and re-prefilled — its own
        // history on essentially every turn.
        const int budget = 12_000;
        var session = NewSession(seed: "WAKE SEED " + new string('s', 4_000));
        for (int i = 0; i < 30; i++) AppendExchange(session, i);

        Assert.True(LlmService.CompactToolSessionIfNeeded(session, budget, keepRecent: 6, protectPrefixMessages: 1));

        int after = LlmService.EstimateToolSessionTokens(session.structuredMessages);
        Assert.True(after <= PromptPrefixStability.CompactionTarget(budget),
            $"compacted to {after} tokens against a {budget} trigger — that leaves no room to grow");

        // Concretely: several more exchanges must fit before the next rewrite is due.
        AppendExchange(session, 100);
        AppendExchange(session, 101);
        Assert.False(LlmService.CompactToolSessionIfNeeded(session, budget, keepRecent: 6, protectPrefixMessages: 1),
            "compaction re-fired almost immediately; the headroom target is not being honoured");
    }

    // ── rule 1: stable first, volatile last ──

    [Fact]
    public void SubAgentSystemPrompt_SharesItsDoctrineAcrossEveryWorker()
    {
        // The regression: the prompt opened "You are a {Tier}-tier SUB-AGENT (role: {Role}, ID: {AgentID})",
        // so two workers diverged about ten tokens in and shared no cached prefix at all — and a
        // respawned worker shared nothing with the agent it replaced, because its id was new.
        var project = new Project { ProjectID = "p1", Name = "P", Goal = "Ship the thing" };
        string a = ProjectSubAgentRunner.BuildSystemPrompt(project, new ProjectAgentRecord
        {
            AgentID = "agent-aaaaaaaa",
            Tier = ProjectAgentTier.Text,
            Role = "market-researcher",
            MissionKind = ProjectAgentMissionKind.Task,
        }, visionEnabled: true);
        string b = ProjectSubAgentRunner.BuildSystemPrompt(project, new ProjectAgentRecord
        {
            AgentID = "agent-bbbbbbbb",
            Tier = ProjectAgentTier.TextImageVideo,
            Role = "content-writer",
            MissionKind = ProjectAgentMissionKind.Standing,
        }, visionEnabled: true);

        int shared = SharedPrefixLength(a, b);
        Assert.True(shared > Math.Min(a.Length, b.Length) * 0.9,
            $"two workers share only {shared} of ~{Math.Min(a.Length, b.Length)} leading chars; " +
            "per-worker identity has leaked back above the doctrine");

        // Nothing that identifies a worker may appear before the cache breakpoint.
        int breakpoint = a.IndexOf(LlmService.CacheBreakpointMarker, StringComparison.Ordinal);
        Assert.True(breakpoint > 0, "the sub-agent prompt must carry a cache breakpoint");
        // Exactly one: the marker is split out on the first occurrence, so a second would survive
        // into the prompt the model actually reads.
        Assert.Equal(breakpoint, a.LastIndexOf(LlmService.CacheBreakpointMarker, StringComparison.Ordinal));
        string stable = a[..breakpoint];
        Assert.DoesNotContain("agent-aaaaaaaa", stable, StringComparison.Ordinal);
        Assert.DoesNotContain("market-researcher", stable, StringComparison.Ordinal);
        Assert.DoesNotContain("Ship the thing", stable, StringComparison.Ordinal);

        // ...and it is still all present, below the line, where the model reads it last.
        Assert.Contains("agent-aaaaaaaa", a, StringComparison.Ordinal);
        Assert.Contains("market-researcher", a, StringComparison.Ordinal);
        Assert.Contains("Ship the thing", a, StringComparison.Ordinal);
    }

    [Fact]
    public void CommanderWakeSeed_KeepsTheWallClockOutOfItsHead()
    {
        // The regression: the seed's SECOND line was a second-precision clock, so the project header,
        // capability truth, directives and grand plan behind it could never be served from cache on a
        // later wake — even when none of them had changed.
        var project = new Project
        {
            ProjectID = "p1",
            Name = "Test",
            Goal = "Do the thing",
            CreatedAt = new DateTime(2026, 7, 12, 18, 4, 33, DateTimeKind.Utc),
        };
        string seed = ProjectCommanderPrompts.BuildWakeSeed(
            project,
            new ProjectDigest { ProjectID = "p1" },
            new List<ProjectEvent>(),
            new List<ProjectRetrievalIndex.RetrievalHit>(),
            "keepalive",
            directivesBlock: "RULE: never use bot accounts.");

        int clock = seed.IndexOf("Now: ", StringComparison.Ordinal);
        int trigger = seed.IndexOf("── THIS WAKE'S TRIGGER ──", StringComparison.Ordinal);
        Assert.True(clock > 0, "the seed must still state the current time");
        Assert.True(clock > trigger,
            "the wall-clock must sit with the trigger at the end of the seed, not in its header");
        Assert.True(clock > seed.IndexOf("NON-NEGOTIABLE KLIVES DIRECTIVES", StringComparison.Ordinal));
        Assert.True(clock > seed.IndexOf("STANDING DIGEST", StringComparison.Ordinal));

        // The header's stamps are absolute — a recomputed relative age would churn the same bytes.
        string header = seed[..seed.IndexOf("STANDING DIGEST", StringComparison.Ordinal)];
        Assert.Contains("project created 2026-07-12 18:04 UTC", header, StringComparison.Ordinal);
        Assert.DoesNotContain(" ago)", header, StringComparison.Ordinal);
    }

    /// Serialises a session the way a provider sees it: one flat byte sequence, roles included, in
    /// order. The prefix cache matches a leading run of THIS.
    private static string Wire(LlmService.KliveLLMSession s) =>
        string.Join("\n", s.structuredMessages.Select(m =>
            $"<{m.role}>{HFWrapper.ContentToText(m.content)}"));

    /// The one-stub-per-turn behaviour that was in place when the router measured us.
    private static void LegacyPrune(LlmService.KliveLLMSession s, int keepRecent)
    {
        var toolIdx = new List<int>();
        for (int i = 0; i < s.structuredMessages.Count; i++)
            if (s.structuredMessages[i].role == "tool") toolIdx.Add(i);
        for (int k = 0; k < toolIdx.Count - keepRecent; k++)
        {
            var m = s.structuredMessages[toolIdx[k]];
            if (m.content is not string str || str.Length <= 240) continue;
            s.structuredMessages[toolIdx[k]] = new HFWrapper.HFMessage
            {
                role = "tool", tool_call_id = m.tool_call_id, name = m.name,
                content = str.Substring(0, 160).TrimEnd() + $"\n[…{str.Length - 160} chars trimmed]",
            };
        }
    }

    /// Mean fraction of each request that the PREVIOUS request's cache could serve — the same
    /// quantity the router reports as a hit rate.
    private static double SimulateHitRate(Action<LlmService.KliveLLMSession, int> prune, int keepRecent, int turns)
    {
        var session = NewSession(seed: "WAKE SEED " + new string('s', 6_000));
        string previous = Wire(session);
        double total = 0;
        for (int i = 0; i < turns; i++)
        {
            AppendExchange(session, i);
            prune(session, keepRecent);
            string current = Wire(session);
            total += (double)SharedPrefixLength(previous, current) / current.Length;
            previous = current;
        }
        return total / turns;
    }

    [Fact]
    public void SimulatedWake_NowServesMostOfEachRequestFromCache()
    {
        // This is the router's own measurement, reproduced locally: for each request in a wake, how
        // much of it was a byte-identical continuation of the request before it. Their reading on the
        // suspended key was ~30%, held flat for six hours.
        double legacy = SimulateHitRate(LegacyPrune, keepRecent: 16, turns: 80);
        double current = SimulateHitRate((s, k) => LlmService.PruneOldToolResults(s, k), keepRecent: 16, turns: 80);

        // Measured at the time of the fix: legacy 30.3%, current 90.2%. The legacy figure landing on
        // the router's independently-measured ~30% is the evidence that one-stub-per-turn really was
        // the mechanism. The floor below is set with headroom so this guards the property, not the
        // exact number.
        Assert.True(legacy < 0.55, $"the legacy simulation should reproduce a poor hit rate, got {legacy:P1}");
        Assert.True(current > 0.85, $"prefix reuse is only {current:P1}; the batching is not working");
        Assert.True(current > legacy * 1.5, $"legacy {legacy:P1} → current {current:P1} is not a real improvement");
    }

    // ── observability: the regression has to be visible from inside ──

    [Fact]
    public void PrefixCacheMeter_SaysSoWhenTheHitRateCollapses_ButOnlyOnRealEvidence()
    {
        var meter = new PrefixCacheMeter();
        var t = new DateTime(2026, 8, 28, 17, 0, 0, DateTimeKind.Utc);

        // A handful of cold requests is normal — fresh sessions genuinely match little. Judging on
        // that would make the warning noise, and noise is how a real one gets ignored.
        for (int i = 0; i < 30; i++)
            Assert.Null(meter.Record(55_000, 0, t.AddSeconds(i)));

        // Sustained, though, is the shape of the incident: ~30% served from cache, hour after hour.
        string? warning = null;
        for (int i = 30; i < 120 && warning == null; i++)
            warning = meter.Record(55_000, 16_500, t.AddSeconds(i));

        Assert.NotNull(warning);
        Assert.Contains("Prefix cache is being missed", warning!, StringComparison.Ordinal);

        // ...and it says it once per incident, not once per request.
        Assert.Null(meter.Record(55_000, 0, t.AddSeconds(200)));
    }

    [Fact]
    public void PrefixCacheMeter_StaysQuietWhenPromptsAreActuallyCaching()
    {
        var meter = new PrefixCacheMeter();
        var t = new DateTime(2026, 8, 28, 17, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < 300; i++)
            Assert.Null(meter.Record(55_000, 52_000, t.AddSeconds(i)));

        var snapshot = meter.Describe(t.AddSeconds(300));
        Assert.Equal(300, snapshot.Requests);
        Assert.True(snapshot.HitRate > 0.9);
    }

    [Fact]
    public void PrefixCacheMeter_ForgetsSamplesOlderThanItsWindow()
    {
        // A bounded window is what makes the meter both honest (it reports NOW, not since startup)
        // and safe to leave running in a 24/7 process.
        var meter = new PrefixCacheMeter();
        var t = new DateTime(2026, 8, 28, 17, 0, 0, DateTimeKind.Utc);

        meter.Record(10_000, 0, t);
        Assert.Equal(1, meter.Describe(t).Requests);
        Assert.Equal(0, meter.Describe(t.AddHours(2)).Requests);
        Assert.Equal(0, meter.Describe(t.AddHours(2)).PromptTokens);
    }

    [Fact]
    public void SeededApprovalLines_UseAbsoluteStampsNotRecomputedAges()
    {
        // The approvals block sits near the TOP of every wake seed, directly under the directives. A
        // relative age there recomputes on every wake, so an approval nobody had touched still handed
        // the provider different bytes each time and discarded the cache for the whole seed behind it.
        // The agent measures staleness against the seed's 'Now:' line instead — which both system
        // prompts tell it to do.
        string pid = "prefix-approvals-" + Guid.NewGuid().ToString("N");
        var gates = new ProjectGateManager(new ProjectEventLogStore(_ => { }), _ => { });

        var pending = new ProjectGate
        {
            GateID = Guid.NewGuid().ToString("N"), ProjectID = pid,
            Title = "Buy the domain?", Description = "costs $12", Kind = "money",
        };
        _ = gates.OpenGateAndWaitAsync(pending, CancellationToken.None);

        var resolved = new ProjectGate
        {
            GateID = Guid.NewGuid().ToString("N"), ProjectID = pid,
            Title = "Publish the first post?", Description = "Draft is ready.", Kind = "action",
        };
        _ = gates.OpenGateAndWaitAsync(resolved, CancellationToken.None);
        gates.ResolveGate(pid, resolved.GateID, new GateResolution(GateDecision.Approve, "go", "klives"));

        string approvals = gates.DescribeForWake(pid);

        Assert.Contains("PENDING: \"Buy the domain?\"", approvals, StringComparison.Ordinal);
        Assert.Contains("RESOLVED: \"Publish the first post?\"", approvals, StringComparison.Ordinal);
        Assert.DoesNotContain(" ago)", approvals, StringComparison.Ordinal);
        Assert.Contains(" UTC", approvals, StringComparison.Ordinal);
    }
}
