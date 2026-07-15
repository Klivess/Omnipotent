using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

[Collection("ProjectsSerial")]
public sealed class ProjectDirectiveStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omnipotent-directive-tests", Guid.NewGuid().ToString("N"));

    private ProjectDirectiveStore NewStore() => new(_ => { }, root);

    [Fact]
    public void Rule_RoundTripsAcrossStoreReload_AndIsAlwaysInWakeSeed()
    {
        const string projectID = "directive-rule";
        var store = NewStore();
        var created = store.Create(projectID, "Do not use bot accounts.", ProjectDirectiveKind.Rule,
            ProjectDirectiveScope.AllAgents, key: "account-policy");

        var reloaded = NewStore();
        var rule = Assert.Single(reloaded.List(projectID, includeResolved: false));
        Assert.Equal(created.DirectiveID, rule.DirectiveID);
        Assert.Equal(ProjectDirectiveKind.Rule, rule.Kind);

        string directives = reloaded.DescribeForPrompt(projectID, "commander");
        string seed = ProjectCommanderPrompts.BuildWakeSeed(NewProject(projectID), new ProjectDigest(), new(), new(),
            "ordinary wake", directivesBlock: directives);

        Assert.Contains("NON-NEGOTIABLE KLIVES DIRECTIVES", seed);
        Assert.Contains("Do not use bot accounts.", seed);
        Assert.True(seed.IndexOf("NON-NEGOTIABLE KLIVES DIRECTIVES", StringComparison.Ordinal) <
            seed.IndexOf("STANDING DIGEST", StringComparison.Ordinal));
    }

    [Fact]
    public void Scope_FiltersSpecificAgentWork_ButPropagatesStandingRuleToEveryone()
    {
        const string projectID = "directive-scope";
        var store = NewStore();
        store.Create(projectID, "Never use bot accounts.", ProjectDirectiveKind.Rule, ProjectDirectiveScope.AllAgents);
        store.Create(projectID, "Prepare the evidence report.", ProjectDirectiveKind.Task,
            ProjectDirectiveScope.SpecificAgents, new[] { "researcher-1" });

        string commander = store.DescribeForPrompt(projectID, "commander");
        string researcher = store.DescribeForPrompt(projectID, "researcher-1");
        string otherWorker = store.DescribeForPrompt(projectID, "writer-2");

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
        const string projectID = "directive-lifecycle";
        var store = NewStore();
        var steer = store.Create(projectID, "What failed?", ProjectDirectiveKind.Steering);
        var task = store.Create(projectID, "Write a PDF incident report.", ProjectDirectiveKind.Task,
            expectedArtifactPaths: new[] { ".pdf" });

        store.MarkResponded(projectID, steer.DirectiveID, "commander", "Investigating.", 10);
        store.MarkResponded(projectID, task.DirectiveID, "commander", "I will produce it.", 11);

        var reloaded = NewStore();
        Assert.Equal(ProjectDirectiveStatus.Acknowledged, reloaded.Get(projectID, steer.DirectiveID)!.Status);
        Assert.Equal(ProjectDirectiveStatus.Acknowledged, reloaded.Get(projectID, task.DirectiveID)!.Status);
        string prompt = reloaded.DescribeForPrompt(projectID, "commander");
        Assert.DoesNotContain("What failed?", prompt);
        Assert.Contains("Write a PDF incident report.", prompt);
        Assert.Contains("Required deliverables: .pdf", prompt);
        Assert.Contains("complete_project_directive", prompt);
    }

    [Fact]
    public void SameRuleKey_ReplacesPolicyWithoutCreatingConflictingMemory()
    {
        const string projectID = "directive-key";
        var store = NewStore();
        var first = store.Create(projectID, "Do not use bot accounts.", ProjectDirectiveKind.Rule,
            ProjectDirectiveScope.AllAgents, key: "accounts");
        var replacement = store.Create(projectID, "Use only Klives-approved human-operated accounts.",
            ProjectDirectiveKind.Rule, ProjectDirectiveScope.AllAgents, key: "accounts");

        var rules = store.List(projectID, includeResolved: false);
        var soleRule = Assert.Single(rules);
        Assert.Equal(first.DirectiveID, replacement.DirectiveID);
        Assert.Equal(first.DirectiveID, soleRule.DirectiveID);
        Assert.Equal("Use only Klives-approved human-operated accounts.", soleRule.Text);
        Assert.True(soleRule.Revision > first.Revision);
    }

    [Fact]
    public void BotAccountRule_HasDeterministicAccountRegistrationBackstop()
    {
        const string projectID = "directive-policy";
        var store = NewStore();
        store.Create(projectID, "Do not use bot accounts.", ProjectDirectiveKind.Rule, ProjectDirectiveScope.AllAgents);

        string? blocked = ProjectDirectivePolicy.FindViolation(store.List(projectID), "commander", "account_register");

        Assert.NotNull(blocked);
        Assert.Contains("PROJECT_DIRECTIVE_VIOLATION", blocked);
        Assert.Null(ProjectDirectivePolicy.FindViolation(store.List(projectID), "commander", "account_list"));
    }

    [Fact]
    public void RuleThatWouldFallOutOfTheAlwaysInjectedBudget_IsRejected()
    {
        const string projectID = "directive-capacity";
        var store = NewStore();
        string longRule = new('x', ProjectDirectiveStore.MaxRuleLength);

        int accepted = 0;
        InvalidOperationException? overflow = null;
        for (int i = 0; i < 20; i++)
        {
            try
            {
                store.Create(projectID, longRule + i, ProjectDirectiveKind.Rule, ProjectDirectiveScope.AllAgents, key: "r" + i);
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
        var names = ProjectCommanderAgent.BuildCoreToolDefinitions().Select(x => x.function.name).ToHashSet();

        Assert.Contains("list_project_directives", names);
        Assert.Contains("acknowledge_project_directive", names);
        Assert.Contains("complete_project_directive", names);
    }

    [Fact]
    public void AcknowledgedTask_IsNotMovedBackToDeliveredDuringRecovery()
    {
        const string projectID = "directive-redelivery";
        var store = NewStore();
        var task = store.Create(projectID, "Prepare the report.", ProjectDirectiveKind.Task);

        store.MarkDelivered(projectID, task.DirectiveID, "commander", "wake-1");
        store.Acknowledge(projectID, task.DirectiveID, "commander", "Starting now.");
        var redelivered = store.MarkDelivered(projectID, task.DirectiveID, "commander", "wake-2");

        Assert.NotNull(redelivered);
        Assert.Equal(ProjectDirectiveStatus.Acknowledged, redelivered!.Status);
        Assert.Equal("commander", redelivered.AcknowledgedBy);
        Assert.Equal("wake-2", redelivered.DeliveredWakeID);
        Assert.Single(redelivered.Deliveries);
    }

    [Fact]
    public void Completion_RequiresAcknowledgementByTheAssignedAgent()
    {
        const string projectID = "directive-owner";
        var store = NewStore();
        var task = store.Create(projectID, "Write the report.", ProjectDirectiveKind.Task,
            ProjectDirectiveScope.SpecificAgents, new[] { "writer" });

        Assert.False(ProjectDirectiveStore.AppliesTo(task, "other"));
        var beforeAck = store.Complete(projectID, task.DirectiveID, "writer", "done");
        Assert.NotNull(beforeAck);
        Assert.Equal(ProjectDirectiveStatus.Active, beforeAck!.Status);
        store.Acknowledge(projectID, task.DirectiveID, "writer", "Accepted.");
        var wrong = store.Complete(projectID, task.DirectiveID, "other", "done");
        Assert.NotNull(wrong);
        Assert.Equal(ProjectDirectiveStatus.Acknowledged, wrong!.Status);
        var completed = store.Complete(projectID, task.DirectiveID, "writer", "done", new[] { "outputs/report.pdf" });
        Assert.Equal(ProjectDirectiveStatus.Completed, completed!.Status);
        Assert.Equal(new[] { "outputs/report.pdf" }, completed.CompletionArtifactPaths);
    }

    [Fact]
    public void OverflowingTaskQueue_PrefersCurrentDirectiveWithoutMidDirectiveTruncation()
    {
        const string projectID = "directive-overflow";
        var store = NewStore();
        var tasks = Enumerable.Range(0, 5).Select(i => store.Create(projectID,
            $"Task {i}: " + new string((char)('a' + i), ProjectDirectiveStore.MaxTaskLength - 8),
            ProjectDirectiveKind.Task)).ToList();
        var preferred = tasks[^1];

        string prompt = store.DescribeForPrompt(projectID, "commander", preferred.DirectiveID);

        Assert.Contains(preferred.DirectiveID, prompt);
        Assert.Contains("additional durable task(s)", prompt);
        Assert.DoesNotContain("[...truncated]", prompt);
    }

    [Fact]
    public void CorruptMemory_FailsClosedAndDoesNotOverwriteTheOriginalFile()
    {
        const string projectID = "directive-corrupt";
        var store = NewStore();
        Directory.CreateDirectory(root);
        string path = store.GetPath(projectID);
        File.WriteAllText(path, "{ definitely not a directive list");

        Assert.Throws<InvalidOperationException>(() => store.List(projectID));
        Assert.Equal("{ definitely not a directive list", File.ReadAllText(path));
        Assert.Throws<InvalidOperationException>(() =>
            store.Create(projectID, "Do not overwrite this.", ProjectDirectiveKind.Rule, ProjectDirectiveScope.AllAgents));
    }

    private static Project NewProject(string id) => new()
    {
        ProjectID = id,
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
