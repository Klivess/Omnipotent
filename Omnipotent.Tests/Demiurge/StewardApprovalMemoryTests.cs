using Newtonsoft.Json;
using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge;

/// <summary>
/// Approvals had no memory in either direction. The Commander's wake seed carried no view of its own
/// pending gates, so it could re-open a card already sitting in front of Klives — the duplicate
/// "Complete the Steward?" pair in the Discord transcript. And Discuss released the waiting agent while
/// leaving <c>Resolved = false</c> and recording nothing on the gate, so Klives' comment survived only as a
/// log line that aged out of the recent-event window.
/// </summary>
[Collection("DemiurgeSerial")]
public sealed class StewardApprovalMemoryTests
{
    private static (StewardCommanderTools tools, StewardGateManager gates, StewardRuntimeStateStore runtime, string pid)
        NewTools(string root, GateDecision decision, string comment)
    {
        var store = new StewardStore(_ => { });
        var log = new StewardEventLogStore(_ => { });
        var digests = new StewardDigestStore(_ => { });
        var subAgents = new StewardSubAgentManager(store, log);
        var gates = new StewardGateManager(log, _ => { });
        gates.GateOpened += gate => gates.ResolveGate(gate.StewardID, gate.GateID,
            new GateResolution(decision, comment, "klives"));
        var fetcher = new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { });
        var budget = new StewardBudgetLedger(store, log, fetcher, _ => { });
        var vault = new StewardVault(_ => { });
        var runtime = new StewardRuntimeStateStore(_ => { }, root);
        var p = store.CreateSteward("approval-memory", "goal", 100, 100, 10, 5);
        p.Status = StewardStatus.Active;
        store.SaveSteward(p);
        var tools = new StewardCommanderTools(p, log, digests, subAgents, gates, budget, vault, store, "commander", "wake1")
        {
            RuntimeState = runtime,
        };
        return (tools, gates, runtime, p.StewardID);
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "omnipotent-approval-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void TheSameQuestionHashesToTheSameRequest()
    {
        string a = StewardGateManager.ComputeDedupeHash("action", "Complete the Steward?", "The goal is achieved.");
        string b = StewardGateManager.ComputeDedupeHash("action", "  complete the Steward?  ", "The  goal is\nachieved.");
        Assert.Equal(a, b);
        Assert.NotEqual(a, StewardGateManager.ComputeDedupeHash("action", "Publish the first post?", "The goal is achieved."));
    }

    [Fact]
    public async Task ASecondIdenticalApprovalIsRefusedRatherThanOpened()
    {
        string root = TempRoot();
        try
        {
            // Never resolved: the first gate stays pending, exactly like a real approval awaiting Klives.
            var store = new StewardStore(_ => { });
            var log = new StewardEventLogStore(_ => { });
            var gates = new StewardGateManager(log, _ => { });
            var fetcher = new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { });
            var p = store.CreateSteward("approval-dupe", "goal", 100, 100, 10, 5);
            p.Status = StewardStatus.Active;
            store.SaveSteward(p);
            var tools = new StewardCommanderTools(p, log, new StewardDigestStore(_ => { }),
                new StewardSubAgentManager(store, log), gates,
                new StewardBudgetLedger(store, log, fetcher, _ => { }), new StewardVault(_ => { }),
                store, "commander", "wake1")
            { RuntimeState = new StewardRuntimeStateStore(_ => { }, root) };

            string args = JsonConvert.SerializeObject(new
            {
                title = "Complete the Steward?",
                description = "Investigation complete; the report is archived.",
                rationale = "Completion archives the channel.",
            });

            // The first ask blocks on Klives, so run it in the background and let it sit unanswered.
            using var cts = new CancellationTokenSource();
            var pendingAsk = tools.DispatchAsync("request_user_approval", args, cts.Token);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (gates.CountPending(p.StewardID) == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.Equal(1, gates.CountPending(p.StewardID));

            var second = await tools.DispatchAsync("request_user_approval", args, CancellationToken.None);
            Assert.False(second.Succeeded);
            Assert.Contains("APPROVAL_ALREADY_PENDING", second.ResultText, StringComparison.Ordinal);
            Assert.Contains("Do not ask again", second.ResultText, StringComparison.Ordinal);
            // Crucially, no second card was put in front of Klives.
            Assert.Equal(1, gates.CountPending(p.StewardID));

            cts.Cancel();
            try { await pendingAsk; } catch { /* the first ask was cancelled on purpose */ }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DiscussionIsPersistedOnTheGate()
    {
        var log = new StewardEventLogStore(_ => { });
        var gates = new StewardGateManager(log, _ => { });
        string pid = "discuss-" + Guid.NewGuid().ToString("N");
        var gate = new StewardGate
        {
            GateID = Guid.NewGuid().ToString("N"),
            StewardID = pid,
            Title = "Complete the Steward?",
            Description = "The goal is achieved.",
            Kind = "action",
        };
        // Open it without waiting; a fire-and-forget await would block on Klives.
        _ = gates.OpenGateAndWaitAsync(gate, CancellationToken.None);

        Assert.True(gates.BeginDiscussion(pid, gate.GateID, "keep digging deeper on Nourdin"));
        Assert.True(gates.BeginDiscussion(pid, gate.GateID, "and check the GitHub history properly"));

        var pending = gates.ListPending(pid).Single();
        Assert.Equal(2, pending.DiscussionCount);
        Assert.NotNull(pending.LastDiscussedAt);
        Assert.Contains("keep digging deeper on Nourdin", pending.DiscussionComments);
        // Still unresolved — the consequential action stays blocked, as it should.
        Assert.False(pending.Resolved);
    }

    [Fact]
    public void TheSeedShowsPendingRequestsAndRecentDecisions()
    {
        var log = new StewardEventLogStore(_ => { });
        var gates = new StewardGateManager(log, _ => { });
        string pid = "seed-" + Guid.NewGuid().ToString("N");

        var pending = new StewardGate
        {
            GateID = Guid.NewGuid().ToString("N"), StewardID = pid,
            Title = "Complete the Steward?", Description = "The goal is achieved.", Kind = "action",
        };
        _ = gates.OpenGateAndWaitAsync(pending, CancellationToken.None);
        gates.BeginDiscussion(pid, pending.GateID, "keep digging deeper");

        var resolved = new StewardGate
        {
            GateID = Guid.NewGuid().ToString("N"), StewardID = pid,
            Title = "Publish the first post?", Description = "Draft is ready.", Kind = "action",
        };
        _ = gates.OpenGateAndWaitAsync(resolved, CancellationToken.None);
        gates.ResolveGate(pid, resolved.GateID, new GateResolution(GateDecision.Deny, "not until the brand kit lands", "klives"));

        string seeded = gates.DescribeForWake(pid);
        Assert.Contains("PENDING: \"Complete the Steward?\"", seeded, StringComparison.Ordinal);
        Assert.Contains("commented 1× without deciding", seeded, StringComparison.Ordinal);
        Assert.Contains("keep digging deeper", seeded, StringComparison.Ordinal);
        Assert.Contains("Do NOT re-open this request", seeded, StringComparison.Ordinal);
        Assert.Contains("RESOLVED: \"Publish the first post?\" — Deny", seeded, StringComparison.Ordinal);
        Assert.Contains("not until the brand kit lands", seeded, StringComparison.Ordinal);
    }

    [Fact]
    public void NoGatesMeansNoApprovalsBlock()
    {
        var gates = new StewardGateManager(new StewardEventLogStore(_ => { }), _ => { });
        Assert.Equal("", gates.DescribeForWake("never-had-a-gate-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task ARefusedCompletionBecomesADurableDeadEnd()
    {
        string root = TempRoot();
        try
        {
            var (tools, _, runtime, pid) = NewTools(root, GateDecision.Deny, "keep digging deeper on Nourdin");
            var result = await tools.DispatchAsync("complete_steward",
                JsonConvert.SerializeObject(new { summary = "Investigation complete." }), CancellationToken.None);

            Assert.Contains("stays active", result.ResultText, StringComparison.Ordinal);
            Assert.Contains("This refusal is now durable", result.ResultText, StringComparison.Ordinal);

            // The refusal now steers later wakes from the dead-ends block instead of evaporating.
            var deadEnd = runtime.GetActiveFailedApproaches(pid)
                .SingleOrDefault(x => x.Key == "completion-request");
            Assert.NotNull(deadEnd);
            Assert.Contains("Deny", deadEnd!.Outcome, StringComparison.Ordinal);
            Assert.Contains("keep digging deeper", deadEnd.Instead!, StringComparison.Ordinal);
            // Time-boxed, not permanent: completion may legitimately become right once the work is done.
            Assert.NotNull(deadEnd.RetryNotBefore);
            Assert.Contains("completion-request", runtime.DescribeForWake(pid), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AnApprovedCompletionRecordsNoDeadEnd()
    {
        string root = TempRoot();
        try
        {
            var (tools, _, runtime, pid) = NewTools(root, GateDecision.Approve, "go ahead");
            await tools.DispatchAsync("complete_steward",
                JsonConvert.SerializeObject(new { summary = "Investigation complete." }), CancellationToken.None);
            Assert.DoesNotContain(runtime.GetActiveFailedApproaches(pid), x => x.Key == "completion-request");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
