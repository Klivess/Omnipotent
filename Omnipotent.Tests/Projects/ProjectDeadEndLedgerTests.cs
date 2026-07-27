using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Projects;
using LlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.Projects;

/// <summary>
/// The dead-end ledger is what makes a per-wake context reset safe. Verified facts record what IS true;
/// nothing recorded what has already been PROVEN NOT to work, so a failed approach lived only in the
/// event log and fell out of the recent-event window after a couple of wakes — after which the agent
/// would cheerfully retry it. Health.LastFailure is not a substitute: it holds one entry and is cleared
/// on the next success.
/// </summary>
public sealed class ProjectDeadEndLedgerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omnipotent-deadend-tests", Guid.NewGuid().ToString("N"));

    private ProjectRuntimeStateStore NewStore() => new(_ => { }, root);

    [Fact]
    public void RecordedDeadEnd_IsSeededIntoEveryLaterWake()
    {
        var store = NewStore();
        const string pid = "p1";

        store.RecordFailedApproach(pid, new ProjectFailedApproach
        {
            Key = "ig-signup:email-domain",
            Approach = "Signed up using a @klive.dev address",
            Outcome = "Instagram rejected the domain at the verification step",
            Instead = "Use an established mailbox provider for this signup",
        });

        string seeded = store.DescribeForWake(pid);
        Assert.Contains("dead ends", seeded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ig-signup:email-domain", seeded, StringComparison.Ordinal);
        Assert.Contains("Instagram rejected the domain", seeded, StringComparison.Ordinal);
        Assert.Contains("instead: Use an established mailbox provider", seeded, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedAttempt_IncrementsRatherThanDuplicating()
    {
        var store = NewStore();
        const string pid = "p1";

        for (int i = 0; i < 3; i++)
            store.RecordFailedApproach(pid, new ProjectFailedApproach
            {
                Key = "build:libfoo",
                Approach = "pip install libfoo",
                Outcome = "no wheel for this platform",
            });

        var active = store.GetActiveFailedApproaches(pid);
        var entry = Assert.Single(active);
        Assert.Equal(3, entry.AttemptCount);
    }

    [Fact]
    public void ResolvedDeadEnd_StopsSteeringButKeepsHistory()
    {
        var store = NewStore();
        const string pid = "p1";
        store.RecordFailedApproach(pid, new ProjectFailedApproach
        {
            Key = "api:auth", Approach = "bearer token", Outcome = "401",
        });

        Assert.Single(store.GetActiveFailedApproaches(pid));

        store.ResolveFailedApproach(pid, "api:auth", "Token scope was the problem; works now.");

        Assert.Empty(store.GetActiveFailedApproaches(pid));
        Assert.DoesNotContain("api:auth", store.DescribeForWake(pid), StringComparison.Ordinal);
        // Retained for the record rather than deleted.
        Assert.Single(store.Get(pid).Checkpoint.FailedApproaches);
    }

    [Fact]
    public void TransientDeadEnd_AgesOutOfTheSeedOnItsOwn()
    {
        var store = NewStore();
        const string pid = "p1";
        store.RecordFailedApproach(pid, new ProjectFailedApproach
        {
            Key = "provider:rate-limit",
            Approach = "burst of requests",
            Outcome = "429",
            RetryNotBefore = DateTime.UtcNow.AddMinutes(-1), // already elapsed
        });

        Assert.Empty(store.GetActiveFailedApproaches(pid));
    }

    [Fact]
    public void Ledger_IsBounded()
    {
        var store = NewStore();
        const string pid = "p1";
        for (int i = 0; i < ProjectRuntimeStateStore.MaxFailedApproaches + 15; i++)
            store.RecordFailedApproach(pid, new ProjectFailedApproach
            {
                Key = $"key-{i}", Approach = "a", Outcome = "b",
            });

        Assert.True(store.Get(pid).Checkpoint.FailedApproaches.Count
            <= ProjectRuntimeStateStore.MaxFailedApproaches);
    }

    [Fact]
    public void WakeTail_RoundTripsForTheNextWake()
    {
        var store = NewStore();
        const string pid = "p1";
        store.SetLastWakeTail(pid, "Ended mid-upload; next step is to confirm the post rendered.");

        Assert.Contains("mid-upload", store.Get(pid).Checkpoint.LastWakeTail!, StringComparison.Ordinal);
        Assert.NotNull(store.Get(pid).Checkpoint.LastWakeTailAt);

        store.SetLastWakeTail(pid, null);
        Assert.Null(store.Get(pid).Checkpoint.LastWakeTail);
    }

    [Fact]
    public void ConversationCacheBreakpoint_DoesNotMutateCallerContent()
    {
        // BuildMessagesFromList copies HFMessage objects but SHARES their content reference with the
        // live session. Tagging cache_control in place would rewrite the caller's stored history,
        // silently changing earlier turns between requests.
        // This is the shape BuildMessagesFromList produces: a NEW HFMessage whose content reference is
        // still the session's own object.
        var sessionOwnedParts = new List<object> { new HFWrapper.HFTextPart { text = "earlier user turn" } };
        var originalPart = (HFWrapper.HFTextPart)sessionOwnedParts[0];

        var payload = new HFWrapper.HFLLMInferenceRequest
        {
            messages = new[]
            {
                new HFWrapper.HFMessage { role = "system", content = "doctrine" },
                new HFWrapper.HFMessage { role = "user", content = "plain earlier turn" },
                new HFWrapper.HFMessage { role = "assistant", content = "ok" },
                new HFWrapper.HFMessage { role = "user", content = sessionOwnedParts },
                new HFWrapper.HFMessage { role = "user", content = "the live turn" },
            },
        };

        LlmService.ApplyConversationCacheBreakpoint(payload);

        // The session's own list and its parts are untouched — no cache_control leaked back into
        // stored history, so the next request rebuilds from identical source material.
        Assert.Single(sessionOwnedParts);
        Assert.Null(originalPart.cache_control);

        // The breakpoint lands on the PREVIOUS user turn, not the live one: marking the live turn
        // would write a fresh cache entry every request and never read one back.
        var tagged = Assert.IsType<List<object>>(payload.messages[3].content);
        Assert.NotSame(sessionOwnedParts, tagged);
        Assert.NotNull(Assert.IsType<HFWrapper.HFTextPart>(tagged[0]).cache_control);

        // The live turn is left untouched.
        Assert.IsType<string>(payload.messages[4].content);
    }

    [Fact]
    public void ConversationCacheBreakpoint_HandlesPlainStringContent()
    {
        var payload = new HFWrapper.HFLLMInferenceRequest
        {
            messages = new[]
            {
                new HFWrapper.HFMessage { role = "system", content = "doctrine" },
                new HFWrapper.HFMessage { role = "user", content = "seed" },
                new HFWrapper.HFMessage { role = "user", content = "live" },
            },
        };

        LlmService.ApplyConversationCacheBreakpoint(payload);

        var parts = Assert.IsType<List<object>>(payload.messages[1].content);
        var text = Assert.IsType<HFWrapper.HFTextPart>(parts[0]);
        Assert.Equal("seed", text.text);
        Assert.NotNull(text.cache_control);
    }

    [Fact]
    public void ConversationCacheBreakpoint_NoOpsWithASingleUserTurn()
    {
        var payload = new HFWrapper.HFLLMInferenceRequest
        {
            messages = new[]
            {
                new HFWrapper.HFMessage { role = "system", content = "doctrine" },
                new HFWrapper.HFMessage { role = "user", content = "only turn" },
            },
        };

        LlmService.ApplyConversationCacheBreakpoint(payload);

        Assert.IsType<string>(payload.messages[1].content);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }
}
