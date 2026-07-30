using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

/// <summary>
/// The failure this pins is the one from the Discord transcript: Klives said "keep digging, don't complete
/// yet", the Commander acknowledged it in prose — and the directive store then dropped acknowledged Steering
/// from every subsequent wake seed, so the next wake re-opened an identical completion approval.
///
/// An answer is not compliance. A steer stays in force until Klives replaces it, and a message that is
/// plainly a standing constraint is saved as a Rule, because a constraint has no deliverable and so can
/// never be "completed" out of the Task queue either.
/// </summary>
public sealed class ProjectDirectiveStickinessTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omnipotent-directive-stick-tests", Guid.NewGuid().ToString("N"));

    private ProjectDirectiveStore NewStore() => new(_ => { }, root);

    [Fact]
    public void AnAcknowledgedSteerStaysInForce()
    {
        var store = NewStore();
        const string pid = "p1";
        var steer = store.Create(pid, "Keep digging deeper — don't wrap this up yet.", ProjectDirectiveKind.Steering);
        store.Acknowledge(pid, steer.DirectiveID, "commander", "Understood, continuing.");

        string seeded = store.DescribeForPrompt(pid, "commander");
        Assert.Contains("Keep digging deeper", seeded, StringComparison.Ordinal);
        Assert.Contains("STILL IN FORCE", seeded, StringComparison.Ordinal);
        // And it must be clear that replying did not discharge it.
        Assert.Contains("Replying did not discharge it", seeded, StringComparison.Ordinal);
    }

    [Fact]
    public void AnsweredSteersAreBoundedSoChatDoesNotBecomePolicy()
    {
        var store = NewStore();
        const string pid = "p1";
        const int total = ProjectDirectiveStore.MaxAnsweredSteersInPrompt + 6;
        for (int i = 0; i < total; i++)
        {
            var steer = store.Create(pid, $"don't forget constraint [{i}]", ProjectDirectiveKind.Steering);
            store.Acknowledge(pid, steer.DirectiveID, "commander", "noted");
        }

        string seeded = store.DescribeForPrompt(pid, "commander");
        int shown = Enumerable.Range(0, total).Count(i => seeded.Contains($"constraint [{i}]", StringComparison.Ordinal));
        Assert.True(shown <= ProjectDirectiveStore.MaxAnsweredSteersInPrompt, $"{shown} answered steers reached the seed");
        // The newest survive; the oldest age out into the log.
        Assert.Contains($"constraint [{total - 1}]", seeded, StringComparison.Ordinal);
        Assert.DoesNotContain("constraint [0]", seeded, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAnsweredQuestionIsDischarged_OnlyConstraintsPersist()
    {
        var store = NewStore();
        const string pid = "p1";
        // Answering "what failed?" genuinely finishes it; answering "don't complete yet" does not. Keeping
        // every answered chat message would fill the seed with discharged questions presented as policy.
        var question = store.Create(pid, "What failed overnight?", ProjectDirectiveKind.Steering);
        var constraint = store.Create(pid, "Don't complete this yet — keep digging.", ProjectDirectiveKind.Steering);
        store.Acknowledge(pid, question.DirectiveID, "commander", "Two provider timeouts.");
        store.Acknowledge(pid, constraint.DirectiveID, "commander", "Understood.");

        string seeded = store.DescribeForPrompt(pid, "commander");
        Assert.DoesNotContain("What failed overnight?", seeded, StringComparison.Ordinal);
        Assert.Contains("Don't complete this yet", seeded, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitRulesAreNeverCrowdedOutByChatter()
    {
        var store = NewStore();
        const string pid = "p1";
        store.Create(pid, "Never create bot accounts on any platform.", ProjectDirectiveKind.Rule);
        for (int i = 0; i < ProjectDirectiveStore.MaxAnsweredSteersInPrompt; i++)
        {
            var steer = store.Create(pid, $"don't forget point {i} {new string('x', 700)}", ProjectDirectiveKind.Steering);
            store.Acknowledge(pid, steer.DirectiveID, "commander", "noted");
        }

        string seeded = store.DescribeForPrompt(pid, "commander");
        Assert.Contains("Never create bot accounts", seeded, StringComparison.Ordinal);
        Assert.Contains("RULE — NON-NEGOTIABLE", seeded, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedAndRevokedDirectivesStillLeaveTheSeed()
    {
        var store = NewStore();
        const string pid = "p1";
        var task = store.Create(pid, "Write the interim report.", ProjectDirectiveKind.Task);
        store.Acknowledge(pid, task.DirectiveID, "commander", "on it");
        store.Complete(pid, task.DirectiveID, "commander", "delivered outputs/interim.md", new List<string>());

        var rule = store.Create(pid, "Never use disposable mailboxes.", ProjectDirectiveKind.Rule);
        store.Revoke(pid, rule.DirectiveID);

        string seeded = store.DescribeForPrompt(pid, "commander");
        Assert.DoesNotContain("Write the interim report", seeded, StringComparison.Ordinal);
        Assert.DoesNotContain("disposable mailboxes", seeded, StringComparison.Ordinal);
    }

    [Fact]
    public void SupersededDirectivesStopBeingSeededButAreKept()
    {
        var store = NewStore();
        const string pid = "p1";
        var older = store.Create(pid, "Focus on the LinkedIn angle.", ProjectDirectiveKind.Steering);
        var newer = store.Create(pid, "Forget LinkedIn — focus on the GitHub history.", ProjectDirectiveKind.Steering);

        Assert.NotNull(store.Supersede(pid, older.DirectiveID, newer.DirectiveID));

        string seeded = store.DescribeForPrompt(pid, "commander");
        Assert.DoesNotContain("Focus on the LinkedIn angle", seeded, StringComparison.Ordinal);
        Assert.Contains("focus on the GitHub history", seeded, StringComparison.Ordinal);
        // Kept for the record rather than deleted.
        Assert.Equal(newer.DirectiveID, store.Get(pid, older.DirectiveID)!.SupersededBy);
    }

    [Fact]
    public void SupersedingIsIdempotentAndCannotTargetItself()
    {
        var store = NewStore();
        const string pid = "p1";
        var a = store.Create(pid, "first", ProjectDirectiveKind.Steering);
        var b = store.Create(pid, "second", ProjectDirectiveKind.Steering);

        store.Supersede(pid, a.DirectiveID, b.DirectiveID);
        store.Supersede(pid, a.DirectiveID, b.DirectiveID);
        Assert.Equal(b.DirectiveID, store.Get(pid, a.DirectiveID)!.SupersededBy);

        store.Supersede(pid, b.DirectiveID, b.DirectiveID);
        Assert.Null(store.Get(pid, b.DirectiveID)!.SupersededBy);
    }

    // ── the classifier ──

    [Theory]
    [InlineData("don't complete this yet, keep digging")]
    [InlineData("Don't use LLM-sounding prose.")]
    [InlineData("never create bot accounts")]
    [InlineData("stop asking me to run commands")]
    [InlineData("no more disposable mailboxes")]
    [InlineData("always cite a source for every claim")]
    [InlineData("From now on, check the budget before spawning workers.")]
    [InlineData("keep digging until I say stop")]
    [InlineData("carry on gathering until I tell you otherwise")]
    [InlineData("avoid contacting anyone directly")]
    public void StandingConstraintsBecomeRules(string text)
    {
        Assert.Equal(ProjectDirectiveKind.Rule, ProjectDirectiveClassifier.ClassifyStandingConstraint(text));
    }

    [Theory]
    [InlineData("send me a PDF report of everything you found")]
    [InlineData("write a summary of the GitHub portfolio")]
    [InlineData("build the brand kit in shared/")]
    [InlineData("what did you find overnight?")]
    [InlineData("looks good, nice work")]
    [InlineData("I don't think the middle name is confirmed")]
    [InlineData("")]
    [InlineData(null)]
    public void OneOffRequestsAndOrdinaryChatAreLeftAlone(string? text)
    {
        Assert.Null(ProjectDirectiveClassifier.ClassifyStandingConstraint(text));
    }

    [Fact]
    public void ADeliverableWinsOverConstraintWording()
    {
        // "write me the report, don't pad it" is work with a definition of done, not standing policy.
        Assert.Null(ProjectDirectiveClassifier.ClassifyStandingConstraint("write me the report and don't pad it out"));
    }

    [Fact]
    public void ProseTooLongToGuaranteeInEveryPromptIsNotARule()
    {
        string briefing = "Don't rush this. " + new string('x', ProjectDirectiveStore.MaxRuleLength);
        Assert.Null(ProjectDirectiveClassifier.ClassifyStandingConstraint(briefing));
    }

    [Fact]
    public void RuleCapacityIsEnforcedSoEveryRuleIsAlwaysVisible()
    {
        var store = NewStore();
        const string pid = "p1";
        // Rules are guaranteed in every seed, so the store refuses one it could not guarantee — which is
        // why auto-promotion has to fall back rather than let a Klives message fail.
        Assert.Throws<InvalidOperationException>(() =>
        {
            for (int i = 0; i < 200; i++)
                store.Create(pid, $"Never do the thing numbered {i}. " + new string('y', 400), ProjectDirectiveKind.Rule);
        });
        Assert.Contains("Never do the thing numbered 0", store.DescribeForPrompt(pid, "commander"), StringComparison.Ordinal);
    }

    [Fact]
    public void QueuedWorkStillReportsWhatItCouldNotExpand()
    {
        var store = NewStore();
        const string pid = "p1";
        for (int i = 0; i < 40; i++)
            store.Create(pid, $"Task {i}: " + new string('z', 900), ProjectDirectiveKind.Task);

        string seeded = store.DescribeForPrompt(pid, "commander");
        Assert.Contains("additional durable task(s) are queued", seeded, StringComparison.Ordinal);
        Assert.Contains("project_directive op:list", seeded, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }
}
