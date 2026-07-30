using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

/// <summary>
/// A project used to have four overlapping answers to "what am I doing now": Grand Plan milestones (too
/// coarse, and only a summary line reached the seed), the digest's prose next-steps (rewritten by a utility
/// model after every wake, so the plan drifted on its own), one runner-set resume action, and the directive
/// queue. None was authoritative and none carried an attempt count, so each wake re-derived a
/// plausible-looking plan and often picked something already attempted.
///
/// The step ledger is the single plan of record: one Active step, an attempt count measured from real tool
/// calls, and a next concrete action that a renewed context resumes from.
/// </summary>
public sealed class ProjectStepLedgerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omnipotent-step-tests", Guid.NewGuid().ToString("N"));

    private ProjectRuntimeStateStore NewStore() => new(_ => { }, root);

    private static ProjectStep Step(string title, string? milestone = null, string? next = null) =>
        new() { Title = title, MilestoneID = milestone, NextConcreteAction = next };

    private static List<ProjectEvidenceReference> Evidence(string reference) => new()
    {
        new ProjectEvidenceReference { Kind = ProjectEvidenceKind.Event, Reference = reference },
    };

    [Fact]
    public void QueuedStepsGetSequentialIdsAndPreserveOrder()
    {
        var store = NewStore();
        var created = store.QueueSteps("p1", new[] { Step("first"), Step("second"), Step("third") });

        Assert.Equal(new[] { "s1", "s2", "s3" }, created.Select(s => s.StepID));
        Assert.Equal(new[] { "first", "second", "third" }, store.ListSteps("p1").Select(s => s.Title));
    }

    [Fact]
    public void OnlyOneStepIsEverActive()
    {
        var store = NewStore();
        store.QueueSteps("p1", new[] { Step("a"), Step("b"), Step("c") });

        store.ActivateStep("p1", "s1");
        store.ActivateStep("p1", "s3");

        var steps = store.ListSteps("p1");
        Assert.Single(steps, s => s.Status == ProjectStepStatus.Active);
        Assert.Equal("s3", store.GetActiveStep("p1")!.StepID);
        // The demoted step returns to the queue rather than being lost or left half-open.
        Assert.Equal(ProjectStepStatus.Queued, steps.First(s => s.StepID == "s1").Status);
    }

    [Fact]
    public void ClosingAsDoneRequiresEvidence()
    {
        var store = NewStore();
        store.QueueSteps("p1", new[] { Step("verify the middle name") });
        store.ActivateStep("p1", "s1");

        var refused = store.CloseStep("p1", "s1", ProjectStepStatus.Done, "I believe this is finished.");
        Assert.False(refused.Applied);
        Assert.Contains("evidence", refused.Reason!, StringComparison.OrdinalIgnoreCase);

        var accepted = store.CloseStep("p1", "s1", ProjectStepStatus.Done, "EPCC list confirms it.", Evidence("#412"));
        Assert.True(accepted.Applied);
        Assert.Equal(ProjectStepStatus.Done, store.ListSteps("p1")[0].Status);
    }

    [Fact]
    public void AbandoningAndBlockingNeedOnlyAReason()
    {
        var store = NewStore();
        store.QueueSteps("p1", new[] { Step("scrape the private profile"), Step("wait on the API key") });

        Assert.True(store.CloseStep("p1", "s1", ProjectStepStatus.Abandoned, "Account is private; no public route exists.").Applied);
        Assert.True(store.CloseStep("p1", "s2", ProjectStepStatus.Blocked, "Waiting on Klives for the key.").Applied);
        Assert.False(store.CloseStep("p1", "s1", ProjectStepStatus.Done, "changed my mind", Evidence("#1")).Applied);
    }

    [Fact]
    public void BlockedStepsStayOpenAndKeepTheirReason()
    {
        var store = NewStore();
        store.QueueSteps("p1", new[] { Step("wait on the API key") });
        store.CloseStep("p1", "s1", ProjectStepStatus.Blocked, "Waiting on Klives for the key.");

        var step = store.ListSteps("p1")[0];
        Assert.True(step.IsOpen);
        Assert.Null(step.ClosedAt);
        Assert.Contains("Waiting on Klives", step.ClosureReason!, StringComparison.Ordinal);
        Assert.True(store.HasOpenSteps("p1"));
        // Blocked work is still work: it can be picked back up without re-queueing.
        Assert.True(store.ActivateStep("p1", "s1").Applied);
    }

    [Fact]
    public void ClosedStepsCannotBeReopened()
    {
        var store = NewStore();
        store.QueueSteps("p1", new[] { Step("a") });
        store.CloseStep("p1", "s1", ProjectStepStatus.Abandoned, "no route");

        var reactivated = store.ActivateStep("p1", "s1");
        Assert.False(reactivated.Applied);
        Assert.Contains("Abandoned", reactivated.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AttemptsAreCountedFromRealToolCalls()
    {
        var store = NewStore();
        const string pid = "p1";
        store.QueueSteps(pid, new[] { Step("verify the middle name") });
        store.ActivateStep(pid, "s1");

        // No agent cooperation: the ledger charges the active step as tool results land.
        for (int i = 0; i < 3; i++)
            store.RecordAttempt(pid, ProjectAttemptKey.For("web_search", "{\"query\":\"q" + i + "\"}"),
                "web_search", "search", succeeded: false, "no usable hits", "w1", "commander");

        var active = store.GetActiveStep(pid)!;
        Assert.Equal(3, active.Attempts);
        Assert.Contains("no usable hits", active.LastAttemptOutcome!, StringComparison.Ordinal);
        Assert.StartsWith("failed:", active.LastAttemptOutcome!, StringComparison.Ordinal);
    }

    [Fact]
    public void AttemptsAreNotChargedWhenNothingIsActive()
    {
        var store = NewStore();
        const string pid = "p1";
        store.QueueSteps(pid, new[] { Step("a") }); // queued, never activated
        store.RecordAttempt(pid, ProjectAttemptKey.For("grep", "{\"pattern\":\"x\"}"), "grep", "grep", false, "none", "w1", "commander");
        Assert.Equal(0, store.ListSteps(pid)[0].Attempts);
    }

    [Fact]
    public void TheSeedLeadsWithTheOneThingInFlight()
    {
        var store = NewStore();
        const string pid = "p1";
        store.QueueSteps(pid, new[]
        {
            Step("Verify middle name via a second source", "m5", "Companies House officer search"),
            Step("Cross-check GitHub commit author emails"),
            Step("Compile the final report"),
        });
        store.ActivateStep(pid, "s1");
        store.CloseStep(pid, "s3", ProjectStepStatus.Abandoned, "superseded by s2");

        string seeded = store.DescribeForWake(pid);

        Assert.Contains("CURRENT STEP (the ONE thing in flight)", seeded, StringComparison.Ordinal);
        Assert.Contains("Verify middle name via a second source", seeded, StringComparison.Ordinal);
        Assert.Contains("[milestone m5]", seeded, StringComparison.Ordinal);
        Assert.Contains("next concrete action: Companies House officer search", seeded, StringComparison.Ordinal);
        Assert.Contains("STEP QUEUE", seeded, StringComparison.Ordinal);
        Assert.Contains("RECENTLY CLOSED", seeded, StringComparison.Ordinal);
        // It leads: the agent should not have to read past health and facts to learn what it is doing.
        Assert.True(seeded.IndexOf("CURRENT STEP", StringComparison.Ordinal)
            < seeded.IndexOf("runtime revision", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingNextActionIsCalledOut()
    {
        var store = NewStore();
        store.QueueSteps("p1", new[] { Step("do the thing") });
        store.ActivateStep("p1", "s1");
        Assert.Contains("not set", store.DescribeForWake("p1"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithAnActiveStepTheResumeActionBecomesSubordinate()
    {
        var store = NewStore();
        const string pid = "p1";
        store.SetResumeAction(pid, new ProjectResumeAction
        {
            Kind = "work-slice", RecordedBy = "commander", Summary = "context renewed mid-search",
        });

        // No steps: the resume action is the only answer to "what now".
        Assert.Contains("EXACT RESUME ACTION", store.DescribeForWake(pid), StringComparison.Ordinal);

        store.QueueSteps(pid, new[] { Step("verify the name", next: "run the officer search") });
        store.ActivateStep(pid, "s1");

        // With a step active there must be exactly one authoritative next action, not two competing ones.
        string seeded = store.DescribeForWake(pid);
        Assert.DoesNotContain("EXACT RESUME ACTION", seeded, StringComparison.Ordinal);
        Assert.Contains("how the previous context ended", seeded, StringComparison.Ordinal);
        Assert.Contains("next concrete action: run the officer search", seeded, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateStepKeepsTheNextActionCurrent()
    {
        var store = NewStore();
        store.QueueSteps("p1", new[] { Step("verify the name") });
        store.ActivateStep("p1", "s1");

        Assert.True(store.UpdateStep("p1", "s1", "try Companies House", "university directory has no entry", null).Applied);
        var step = store.GetActiveStep("p1")!;
        Assert.Equal("try Companies House", step.NextConcreteAction);
        Assert.Equal("university directory has no entry", step.LastAttemptOutcome);

        Assert.False(store.UpdateStep("p1", "s1", null, null, null).Applied);
        Assert.False(store.UpdateStep("p1", "s9", "x", null, null).Applied);
    }

    [Fact]
    public void ReorderingMovesUnlistedStepsBehindTheListedOnes()
    {
        var store = NewStore();
        store.QueueSteps("p1", new[] { Step("a"), Step("b"), Step("c") });

        Assert.True(store.ReorderSteps("p1", new[] { "s3", "s1" }).Applied);
        Assert.Equal(new[] { "s3", "s1", "s2" }, store.ListSteps("p1").Select(s => s.StepID));
        Assert.False(store.ReorderSteps("p1", new[] { "s3", "nope" }).Applied);
    }

    [Fact]
    public void OpenStepsMakeTheLedgerTheAuthority_SoTheDigestStopsRewritingThePlan()
    {
        var store = NewStore();
        Assert.False(store.HasOpenSteps("p1"));
        store.QueueSteps("p1", new[] { Step("a") });
        Assert.True(store.HasOpenSteps("p1"));

        var existing = new ProjectDigest
        {
            ProjectID = "p1",
            CurrentFocus = "verify the middle name",
            NextSteps = new List<string> { "run the officer search", "cross-check commit emails" },
        };
        const string response = "## PLAN\nFocus: something the utility model paraphrased\nNext:\n- a vaguer step\n## SUMMARY\nnarrative";

        // With the ledger holding the plan, the rebuild must not overwrite focus/next-steps…
        var preserved = ProjectCommanderPrompts.ParseDigestResponse(response, existing, preserveNextSteps: true)!;
        Assert.Equal("verify the middle name", preserved.CurrentFocus);
        Assert.Equal(new[] { "run the officer search", "cross-check commit emails" }, preserved.NextSteps);
        Assert.Equal("narrative", preserved.RollingSummary); // …but narrative still rebuilds.

        // …and with no ledger, the historical behaviour is unchanged.
        var rewritten = ProjectCommanderPrompts.ParseDigestResponse(response, existing, preserveNextSteps: false)!;
        Assert.Equal("something the utility model paraphrased", rewritten.CurrentFocus);
        Assert.Equal(new[] { "a vaguer step" }, rewritten.NextSteps);
    }

    [Fact]
    public void ClosedStepsAgeOutButOpenOnesNeverDo()
    {
        var store = NewStore();
        const string pid = "p1";
        store.QueueSteps(pid, Enumerable.Range(0, ProjectRuntimeStateStore.MaxSteps).Select(i => Step("closed " + i)));
        for (int i = 1; i <= ProjectRuntimeStateStore.MaxSteps; i++)
            store.CloseStep(pid, "s" + i, ProjectStepStatus.Abandoned, "done with it");

        var stillOpen = store.QueueSteps(pid, new[] { Step("the one that matters"), Step("and the next") });
        Assert.Equal(2, stillOpen.Count);

        var steps = store.ListSteps(pid);
        Assert.True(steps.Count <= ProjectRuntimeStateStore.MaxSteps);
        Assert.Contains(steps, s => s.Title == "the one that matters");
        Assert.Contains(steps, s => s.Title == "and the next");
    }

    [Fact]
    public void StepsSurviveARestart()
    {
        const string pid = "p1";
        var first = NewStore();
        first.QueueSteps(pid, new[] { Step("verify the name", "m5", "officer search") });
        first.ActivateStep(pid, "s1");

        var reloaded = NewStore();
        var active = reloaded.GetActiveStep(pid);
        Assert.NotNull(active);
        Assert.Equal("verify the name", active!.Title);
        Assert.Equal("officer search", active.NextConcreteAction);
    }

    [Fact]
    public void EmptyTitlesAreRejectedRatherThanQueuedBlank()
    {
        var store = NewStore();
        Assert.Empty(store.QueueSteps("p1", new[] { Step("   ") }));
        Assert.Empty(store.ListSteps("p1"));
    }

    [Fact]
    public void NoStepsMeansNoStepBlockAtAll()
    {
        Assert.Equal("", NewStore().DescribeStepsForWake("p1"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }
}
