using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge;

[Collection("DemiurgeSerial")]
public sealed class StewardDirectiveStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omnipotent-directive-tests", Guid.NewGuid().ToString("N"));

    private StewardDirectiveStore NewStore() => new(_ => { }, root);

    [Fact]
    public void Rule_RoundTripsAcrossStoreReload_AndIsAlwaysInWakeSeed()
    {
        const string stewardID = "directive-rule";
        var store = NewStore();
        var created = store.Create(stewardID, "Do not use bot accounts.", StewardDirectiveKind.Rule,
            StewardDirectiveScope.AllAgents, key: "account-policy");

        var reloaded = NewStore();
        var rule = Assert.Single(reloaded.List(stewardID, includeResolved: false));
        Assert.Equal(created.DirectiveID, rule.DirectiveID);
        Assert.Equal(StewardDirectiveKind.Rule, rule.Kind);

        string directives = reloaded.DescribeForPrompt(stewardID, "commander");
        string seed = StewardCommanderPrompts.BuildWakeSeed(NewSteward(stewardID), new StewardDigest(), new(), new(),
            "ordinary wake", directivesBlock: directives);

        Assert.Contains("NON-NEGOTIABLE KLIVES DIRECTIVES", seed);
        Assert.Contains("Do not use bot accounts.", seed);
        Assert.True(seed.IndexOf("NON-NEGOTIABLE KLIVES DIRECTIVES", StringComparison.Ordinal) <
            seed.IndexOf("STANDING DIGEST", StringComparison.Ordinal));
    }

    [Fact]
    public void Scope_FiltersSpecificAgentWork_ButPropagatesStandingRuleToEveryone()
    {
        const string stewardID = "directive-scope";
        var store = NewStore();
        store.Create(stewardID, "Never use bot accounts.", StewardDirectiveKind.Rule, StewardDirectiveScope.AllAgents);
        store.Create(stewardID, "Prepare the evidence report.", StewardDirectiveKind.Task,
            StewardDirectiveScope.SpecificAgents, new[] { "researcher-1" });

        string commander = store.DescribeForPrompt(stewardID, "commander");
        string researcher = store.DescribeForPrompt(stewardID, "researcher-1");
        string otherWorker = store.DescribeForPrompt(stewardID, "writer-2");

        Assert.Contains("Never use bot accounts.", commander);
        Assert.DoesNotContain("Prepare the evidence report.", commander);
        Assert.Contains("Never use bot accounts.", researcher);
        Assert.Contains("Prepare the evidence report.", researcher);
        Assert.Contains("Never use bot accounts.", otherWorker);
        Assert.DoesNotContain("Prepare the evidence report.", otherWorker);
    }

    [Fact]
    public void SteeringReply_IsNoLongerReinjected_ButTaskStaysOpenUntilCompletion()
    {
        const string stewardID = "directive-lifecycle";
        var store = NewStore();
        var steer = store.Create(stewardID, "What failed?", StewardDirectiveKind.Steering);
        var task = store.Create(stewardID, "Write a PDF incident report.", StewardDirectiveKind.Task,
            expectedArtifactPaths: new[] { ".pdf" });

        store.MarkResponded(stewardID, steer.DirectiveID, "commander", "Investigating.", 10);
        store.MarkResponded(stewardID, task.DirectiveID, "commander", "I will produce it.", 11);

        var reloaded = NewStore();
        Assert.Equal(StewardDirectiveStatus.Acknowledged, reloaded.Get(stewardID, steer.DirectiveID)!.Status);
        Assert.Equal(StewardDirectiveStatus.Acknowledged, reloaded.Get(stewardID, task.DirectiveID)!.Status);
        string prompt = reloaded.DescribeForPrompt(stewardID, "commander");
        Assert.DoesNotContain("What failed?", prompt);
        Assert.Contains("Write a PDF incident report.", prompt);
        Assert.Contains("Required deliverables: .pdf", prompt);
        Assert.Contains("steward_directive op:acknowledge then op:complete", prompt);
    }

    [Fact]
    public void SameRuleKey_ReplacesPolicyWithoutCreatingConflictingMemory()
    {
        const string stewardID = "directive-key";
        var store = NewStore();
        var first = store.Create(stewardID, "Do not use bot accounts.", StewardDirectiveKind.Rule,
            StewardDirectiveScope.AllAgents, key: "accounts");
        var replacement = store.Create(stewardID, "Use only Klives-approved human-operated accounts.",
            StewardDirectiveKind.Rule, StewardDirectiveScope.AllAgents, key: "accounts");

        var rules = store.List(stewardID, includeResolved: false);
        var soleRule = Assert.Single(rules);
        Assert.Equal(first.DirectiveID, replacement.DirectiveID);
        Assert.Equal(first.DirectiveID, soleRule.DirectiveID);
        Assert.Equal("Use only Klives-approved human-operated accounts.", soleRule.Text);
        Assert.True(soleRule.Revision > first.Revision);
    }

    [Fact]
    public void BotAccountRule_HasDeterministicAccountRegistrationBackstop()
    {
        const string stewardID = "directive-policy";
        var store = NewStore();
        store.Create(stewardID, "Do not use bot accounts.", StewardDirectiveKind.Rule, StewardDirectiveScope.AllAgents);

        string? blocked = StewardDirectivePolicy.FindViolation(store.List(stewardID), "commander", "account_register");

        Assert.NotNull(blocked);
        Assert.Contains("STEWARD_DIRECTIVE_VIOLATION", blocked);
        Assert.Null(StewardDirectivePolicy.FindViolation(store.List(stewardID), "commander", "account_list"));
    }

    [Fact]
    public void RuleThatWouldFallOutOfTheAlwaysInjectedBudget_IsRejected()
    {
        const string stewardID = "directive-capacity";
        var store = NewStore();
        string longRule = new('x', StewardDirectiveStore.MaxRuleLength);

        int accepted = 0;
        InvalidOperationException? overflow = null;
        for (int i = 0; i < 20; i++)
        {
            try
            {
                store.Create(stewardID, longRule + i, StewardDirectiveKind.Rule, StewardDirectiveScope.AllAgents, key: "r" + i);
                accepted++;
            }
            catch (InvalidOperationException ex)
            {
                overflow = ex;
                break;
            }
        }

        Assert.True(accepted > 0);
        Assert.NotNull(overflow);
        Assert.Contains("prompt capacity", overflow!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectiveLifecycleTools_AreOfferedToAgents()
    {
        var names = StewardCommanderAgent.BuildCoreToolDefinitions().Select(x => x.function.name).ToHashSet();

        Assert.Contains("list_steward_directives", names);
        Assert.Contains("acknowledge_steward_directive", names);
        Assert.Contains("complete_steward_directive", names);
    }

    [Fact]
    public void AcknowledgedTask_IsNotMovedBackToDeliveredDuringRecovery()
    {
        const string stewardID = "directive-redelivery";
        var store = NewStore();
        var task = store.Create(stewardID, "Prepare the report.", StewardDirectiveKind.Task);

        store.MarkDelivered(stewardID, task.DirectiveID, "commander", "wake-1");
        store.Acknowledge(stewardID, task.DirectiveID, "commander", "Starting now.");
        var redelivered = store.MarkDelivered(stewardID, task.DirectiveID, "commander", "wake-2");

        Assert.NotNull(redelivered);
        Assert.Equal(StewardDirectiveStatus.Acknowledged, redelivered!.Status);
        Assert.Equal("commander", redelivered.AcknowledgedBy);
        Assert.Equal("wake-2", redelivered.DeliveredWakeID);
        Assert.Single(redelivered.Deliveries);
    }

    [Fact]
    public void Completion_RequiresAcknowledgementByTheAssignedAgent()
    {
        const string stewardID = "directive-owner";
        var store = NewStore();
        var task = store.Create(stewardID, "Write the report.", StewardDirectiveKind.Task,
            StewardDirectiveScope.SpecificAgents, new[] { "writer" });

        Assert.False(StewardDirectiveStore.AppliesTo(task, "other"));
        var beforeAck = store.Complete(stewardID, task.DirectiveID, "writer", "done");
        Assert.NotNull(beforeAck);
        Assert.Equal(StewardDirectiveStatus.Active, beforeAck!.Status);
        store.Acknowledge(stewardID, task.DirectiveID, "writer", "Accepted.");
        var wrong = store.Complete(stewardID, task.DirectiveID, "other", "done");
        Assert.NotNull(wrong);
        Assert.Equal(StewardDirectiveStatus.Acknowledged, wrong!.Status);
        var completed = store.Complete(stewardID, task.DirectiveID, "writer", "done", new[] { "outputs/report.pdf" });
        Assert.Equal(StewardDirectiveStatus.Completed, completed!.Status);
        Assert.Equal(new[] { "outputs/report.pdf" }, completed.CompletionArtifactPaths);
    }

    [Fact]
    public void OverflowingTaskQueue_PrefersCurrentDirectiveWithoutMidDirectiveTruncation()
    {
        const string stewardID = "directive-overflow";
        var store = NewStore();
        var tasks = Enumerable.Range(0, 5).Select(i => store.Create(stewardID,
            $"Task {i}: " + new string((char)('a' + i), StewardDirectiveStore.MaxTaskLength - 8),
            StewardDirectiveKind.Task)).ToList();
        var preferred = tasks[^1];

        string prompt = store.DescribeForPrompt(stewardID, "commander", preferred.DirectiveID);

        Assert.Contains(preferred.DirectiveID, prompt);
        Assert.Contains("additional durable task(s)", prompt);
        Assert.DoesNotContain("[...truncated]", prompt);
        Assert.True(DemiurgeContextBudget.EstimateTokens(prompt) <= DemiurgeContextBudget.DirectivesBudget);
    }

    [Fact]
    public void CorruptMemory_FailsClosedAndDoesNotOverwriteTheOriginalFile()
    {
        const string stewardID = "directive-corrupt";
        var store = NewStore();
        Directory.CreateDirectory(root);
        string path = store.GetPath(stewardID);
        File.WriteAllText(path, "{ definitely not a directive list");

        Assert.Throws<InvalidOperationException>(() => store.List(stewardID));
        Assert.Equal("{ definitely not a directive list", File.ReadAllText(path));
        Assert.Throws<InvalidOperationException>(() =>
            store.Create(stewardID, "Do not overwrite this.", StewardDirectiveKind.Rule, StewardDirectiveScope.AllAgents));
    }

    private static Steward NewSteward(string id) => new()
    {
        StewardID = id,
        Name = "Directive test",
        Goal = "Ensure a durable instruction is followed.",
        TokenBudgetUsd = 100,
        MoneyBudgetUsd = 100,
        MoneyAutonomousThresholdUsd = 10,
        SubAgentCap = 2,
    };

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
