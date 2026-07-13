using Omnipotent.Services.Projects;
using Omnipotent.Services.Projects.Stimulus;
using Omnipotent.Data_Handling;
using Omnipotent.Services.Projects.Containers;
using Omnipotent.Services.KliveLLM;
using System.Net;

namespace Omnipotent.Tests.Projects;

public class ProjectDesktopInteractionPolicyTests
{
    private static ProjectSettings Enabled() => new() { DesktopFirstWebsiteInteraction = true };

    [Theory]
    [InlineData("run_bash", "{\"script\":\"python -c \\\"from playwright.sync_api import sync_playwright\\\"\"}")]
    [InlineData("computer_terminal", "{\"command\":\"xdotool click 10 10\"}")]
    [InlineData("computer_terminal", "{\"command\":\"chromium https://social.example/signup\"}")]
    [InlineData("run_powershell", "{\"script\":\"curl --request POST https://social.example/api/post\"}")]
    [InlineData("run_bash", "{\"script\":\"fetch('https://social.example/post', {method: 'POST'})\"}")]
    public void HiddenWebsiteAutomation_IsBlocked(string tool, string arguments)
    {
        string? violation = ProjectDesktopInteractionPolicy.FindViolation(Enabled(), tool, arguments);

        Assert.StartsWith("DESKTOP_INTERACTION_REQUIRED", violation);
    }

    [Fact]
    public void MutatingPublicHttp_IsBlocked_ButReadOnlyAndLocalRequestsRemainAvailable()
    {
        Assert.NotNull(ProjectDesktopInteractionPolicy.FindViolation(Enabled(), "http_request",
            "{\"url\":\"https://social.example/account\",\"method\":\"POST\"}"));
        Assert.Null(ProjectDesktopInteractionPolicy.FindViolation(Enabled(), "http_request",
            "{\"url\":\"https://social.example/account\",\"method\":\"GET\"}"));
        Assert.Null(ProjectDesktopInteractionPolicy.FindViolation(Enabled(), "http_request",
            "{\"url\":\"http://127.0.0.1:5000/test\",\"method\":\"POST\"}"));
    }

    [Fact]
    public void DisposableMailFallbackIsBlockedAcrossBrowserAndScriptSurfaces()
    {
        Assert.Contains("DISPOSABLE_MAIL_PROHIBITED", ProjectDesktopInteractionPolicy.FindViolation(Enabled(),
            "computer_navigate", "{\"url\":\"https://mail.tm/en/\"}"));
        Assert.Contains("DISPOSABLE_MAIL_PROHIBITED", ProjectDesktopInteractionPolicy.FindViolation(Enabled(),
            "run_bash", "{\"script\":\"curl https://api.mail.tm/domains\"}"));
    }

    [Fact]
    public void InvokedProjectScript_IsInspected_NotJustTheShellCommand()
    {
        string root = Path.Combine(Path.GetTempPath(), "policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "signup.py"),
                "from selenium import webdriver\ndriver = webdriver.Chrome()");

            string? violation = ProjectDesktopInteractionPolicy.FindViolation(Enabled(), "run_bash",
                "{\"script\":\"python signup.py\"}", root);

            Assert.NotNull(violation);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ScriptStimulusCannotBecomeAHiddenBrowserWorker()
    {
        string? violation = ProjectDesktopInteractionPolicy.FindViolation(Enabled(), "create_stimulus_hook",
            "{\"sourceKind\":\"script\",\"sourceSpec\":{\"script\":\"requests.post('https://social.example/post')\"}} ");

        Assert.NotNull(violation);
    }

    [Fact]
    public void OrdinaryDiagnosticsAndSourceWork_AreNotBlocked()
    {
        Assert.Null(ProjectDesktopInteractionPolicy.FindViolation(Enabled(), "run_bash",
            "{\"script\":\"docker ps && rg TODO /project\"}"));
        Assert.Null(ProjectDesktopInteractionPolicy.FindViolation(Enabled(), "execute_csharp",
            "{\"code\":\"var fileLocator = new FileLocator();\"}"));
    }
}

public class ProjectToolCallJournalTests
{
    [Fact]
    public void RestartRecovery_PairsOnlyCallsWhoseResultWasNeverCommitted()
    {
        var log = new ProjectEventLogStore(_ => { });
        string projectID = "journal_" + Guid.NewGuid().ToString("N");
        const string wakeID = "wake-1";
        log.Append(new ProjectEvent
        {
            ProjectID = projectID, WakeID = wakeID, AgentID = "commander",
            Type = ProjectEventTypes.ToolCall, ToolCallId = "complete", ToolName = "read_file",
        });
        log.Append(new ProjectEvent
        {
            ProjectID = projectID, WakeID = wakeID, AgentID = "commander",
            Type = ProjectEventTypes.ToolResult, ToolCallId = "complete", ToolName = "read_file",
        });
        log.Append(new ProjectEvent
        {
            ProjectID = projectID, WakeID = wakeID, AgentID = "commander",
            Type = ProjectEventTypes.ToolCall, ToolCallId = "lost", ToolName = "computer_click_text",
        });

        Assert.Equal(1, ProjectToolCallJournal.ReconcileInterruptedWake(log, projectID, wakeID, "commander"));
        Assert.Equal(0, ProjectToolCallJournal.ReconcileInterruptedWake(log, projectID, wakeID, "commander"));
        var repaired = Assert.Single(log.ReadSince(projectID, 0, 20),
            e => e.Type == ProjectEventTypes.ToolResult && e.ToolCallId == "lost");
        Assert.StartsWith(ProjectToolCallJournal.InterruptedResultPrefix, repaired.Text);
        Assert.Contains("outcome is unknown", repaired.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("{\"succeeded\":false}", repaired.PayloadJson);
    }
}

public class ProjectRecurringTimerTests
{
    [Fact]
    public void RecurrenceRemainsWallClockAnchoredAcrossRestart()
    {
        DateTime anchor = new(2026, 7, 14, 9, 0, 0, DateTimeKind.Utc);
        TimeSpan daily = TimeSpan.FromDays(1);

        Assert.Equal(anchor, StimulusAdapterManager.NextTimerOccurrence(
            anchor, daily, anchor.AddHours(-1)));
        Assert.Equal(new DateTime(2026, 7, 16, 9, 0, 0, DateTimeKind.Utc),
            StimulusAdapterManager.NextTimerOccurrence(anchor, daily,
                new DateTime(2026, 7, 15, 14, 0, 0, DateTimeKind.Utc)));
    }
}

public class ProjectWakeStatusTests
{
    [Fact]
    public void MissingClosingText_IsSynthesizedOnlyFromDurableState()
    {
        var digest = new ProjectDigest { CurrentFocus = "verify profile", NextSteps = ["publish first draft"] };
        var runtime = new ProjectRuntimeState
        {
            Checkpoint = new ProjectRuntimeCheckpoint
            {
                LastSuccessfulAction = new ProjectActionCheckpoint { Summary = "signup form submitted" },
                ResumeAction = new ProjectResumeAction { Summary = "inspect the verification screen" },
            },
        };

        string status = ProjectWakeStatus.ForCommander(digest, runtime);

        Assert.Contains("synthesized from durable state", status);
        Assert.Contains("signup form submitted", status);
        Assert.Contains("inspect the verification screen", status);
        Assert.DoesNotContain("(no closing status)", status);
    }
}

public class KliveMailboxIntegrityTests
{
    [Theory]
    [InlineData("growth.bot@klive.dev", "growthbot@klive.dev", true)]
    [InlineData("growthbot@klive.dev", "growthbo@klive.dev", true)]
    [InlineData("growthbot@klive.dev", "growthbox@klive.dev", true)]
    [InlineData("growthbot@klive.dev", "growbot@klive.dev", false)]
    [InlineData("growthbot@klive.dev", "growthbot@example.com", false)]
    [InlineData("growthbot@klive.dev", "growthbot@klive.dev", false)]
    public void NearbyMailboxDetection_IsNarrowAndDomainBound(string expected, string actual, bool nearby)
    {
        Assert.Equal(nearby, ProjectCommanderTools.LikelyMailboxVariant(expected, actual));
    }
}

public class ProjectWorkProgressTests
{
    [Fact]
    public void ExplicitToolFailure_NeverEarnsAContinuationCheckpoint()
    {
        var runtime = new ProjectRuntimeStateStore(_ => { });
        string projectID = "progress_" + Guid.NewGuid().ToString("N");
        var failed = new CommanderToolResult("Output happened to look useful") { Succeeded = false };

        Assert.False(ProjectWorkProgress.RecordIfNovel(runtime, projectID, "commander",
            "run_bash", "{\"script\":\"false\"}", failed));
        Assert.Null(runtime.Get(projectID).Checkpoint.LastSuccessfulAction);
    }

    [Fact]
    public void SuccessfulOutcomeHistory_SurvivesInterveningActionsAndRestart()
    {
        var runtime = new ProjectRuntimeStateStore(_ => { });
        string projectID = "progress_" + Guid.NewGuid().ToString("N");
        var success = new CommanderToolResult("same durable observation");

        Assert.True(ProjectWorkProgress.RecordIfNovel(runtime, projectID, "commander",
            "read_file", "{\"path\":\"a.txt\"}", success));
        Assert.True(ProjectWorkProgress.RecordIfNovel(runtime, projectID, "commander",
            "read_file", "{\"path\":\"b.txt\"}", new CommanderToolResult("different observation")));

        var reloaded = new ProjectRuntimeStateStore(_ => { });
        Assert.False(ProjectWorkProgress.RecordIfNovel(reloaded, projectID, "commander",
            "read_file", "{\"path\":\"a.txt\"}", success));
    }
}

public class ProjectContextSliceTests
{
    [Fact]
    public void ProjectTurnsHaveExplicitAffordableOutputAndAggregateTokenBoundaries()
    {
        var settings = new ProjectSettings();

        Assert.InRange(settings.CommanderMaxOutputTokens, 512, 32_768);
        Assert.InRange(settings.SubAgentMaxOutputTokens, 512, 32_768);
        Assert.InRange(settings.WorkSliceTokenBudget, 16_000, 128_000);
    }
}

public class ProjectHostShellTests
{
    [Fact]
    public async Task CallerCancellation_PropagatesToToolJournalBoundary()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HostShell.RunPowerShellAsync("Write-Output 'must not run'", ct: cancellation.Token));
    }
}

public class ProjectToolAuditTests
{
    [Theory]
    [InlineData("vault_save", "{\"name\":\"password\",\"value\":\"plaintext-secret\"}")]
    [InlineData("account_register", "{\"service\":\"example\",\"username\":\"bot\",\"secrets\":{\"password\":\"plaintext-secret\"}}")]
    [InlineData("account_update", "{\"accountID\":\"id\",\"addSecretName\":\"password\",\"addSecretValue\":\"plaintext-secret\"}")]
    [InlineData("http_request", "{\"url\":\"https://example.test/hook?token=plaintext-secret\",\"method\":\"POST\",\"body\":\"plaintext-secret\"}")]
    public void DurableToolPayloads_RedactSecretValues(string toolName, string arguments)
    {
        string audit = ProjectCommanderTools.AuditPayload(toolName, arguments)!;

        Assert.DoesNotContain("plaintext-secret", audit);
        Assert.Contains(toolName == "http_request" ? "omitted" : "redacted", audit,
            StringComparison.OrdinalIgnoreCase);
    }
}

public class ProjectCurrentHealthTests
{
    [Fact]
    public void SuccessfulWake_DoesNotEraseAnUnhealthyIndependentDependency()
    {
        var store = new ProjectRuntimeStateStore(_ => { });
        string projectID = "health_" + Guid.NewGuid().ToString("N");
        store.RecordDependencyHealth(projectID, "desktop/commander", false,
            "FrameBlack", "VNC returned a black frame.");
        store.RecordExecutionFailure(projectID, new ProjectExecutionFailure
        {
            Code = "ProviderUnavailable", Summary = "temporary", Retryable = true,
        });

        var result = store.RecordExecutionSuccess(projectID, 42);

        Assert.Equal(ProjectExecutionHealthStatus.Degraded, result.State.Health.Status);
        Assert.Null(result.State.Health.LastFailure);
        Assert.Contains("desktop/commander", result.State.Health.Dependencies.Keys);
        Assert.Contains("dependency degraded", store.DescribeForWake(projectID));
    }

    [Fact]
    public void WatchdogRecoveryAccounting_IsDurableAndMonotonic()
    {
        var store = new ProjectRuntimeStateStore(_ => { });
        string projectID = "watchdog_" + Guid.NewGuid().ToString("N");
        DateTime first = new(2026, 7, 13, 10, 0, 0, DateTimeKind.Utc);
        store.RecordWatchdogRecovery(projectID, TimeSpan.FromHours(3), nowUtc: first);
        var second = store.RecordWatchdogRecovery(projectID, TimeSpan.FromHours(3), nowUtc: first.AddHours(4));

        Assert.Equal(2, second.State.Health.Watchdog.RecoveryCount);
        Assert.Single(second.State.Health.Watchdog.RecoveriesUtc);

        var reloaded = new ProjectRuntimeStateStore(_ => { }).Get(projectID);
        Assert.Equal(2, reloaded.Health.Watchdog.RecoveryCount);
    }
}

public class ProjectProviderFailureTelemetryTests
{
    [Fact]
    public void ProviderFailure_RetainsActionableFields_AndRedactsBearerTokens()
    {
        var error = new RemoteLLMException(RemoteLLMFailureKind.InvalidRequest,
            "Body: bad max_tokens; Authorization=secret-value; Bearer raw-token",
            "OpenRouter", "provider/model", HttpStatusCode.BadRequest,
            requestedMaxTokens: 8192, affordableMaxTokens: 4096);

        var failure = ProjectProviderFailure.ToExecutionFailure(error, "wake-1");
        string payload = ProjectProviderFailure.ToPayloadJson(error);

        Assert.Equal("OpenRouter", failure.Provider);
        Assert.Equal("provider/model", failure.Model);
        Assert.Equal(400, failure.HttpStatus);
        Assert.Equal(8192, failure.RequestedMaxTokens);
        Assert.Equal(4096, failure.AffordableMaxTokens);
        Assert.DoesNotContain("secret-value", failure.Summary);
        Assert.DoesNotContain("raw-token", payload);
    }

    [Theory]
    [InlineData("OpenRouter 400 InvalidRequest: malformed tools", RemoteLLMFailureKind.InvalidRequest, 400)]
    [InlineData("OpenRouter 429 rate limited", RemoteLLMFailureKind.RateLimited, 429)]
    [InlineData("provider returned no diagnostic", RemoteLLMFailureKind.ProviderUnavailable, null)]
    public void LegacyUnsuccessfulResponses_AreConvertedToStructuredFailures(
        string detail, RemoteLLMFailureKind expectedKind, int? expectedStatus)
    {
        var error = ProjectProviderFailure.FromUnsuccessfulResponse(detail, "provider/model", 8192);

        Assert.Equal(expectedKind, error.Kind);
        Assert.Equal(expectedStatus, error.StatusCode.HasValue ? (int?)error.StatusCode.Value : null);
        Assert.Equal("provider/model", error.Model);
        Assert.Equal(8192, error.RequestedMaxTokens);
    }
}

public class ProjectDesktopReadinessInvariantTests
{
    [Fact]
    public void BlackFramebuffer_IsRejected_ButPaintedDesktopIsUsable()
    {
        byte[] black = new byte[640 * 480 * 4];
        byte[] painted = new byte[640 * 480 * 4];
        for (int i = 0; i < painted.Length; i += 4)
        {
            painted[i] = 80;
            painted[i + 1] = 120;
            painted[i + 2] = 160;
            painted[i + 3] = 255;
        }

        Assert.False(ContainerDesktopManager.IsUsableFrame(black, 640, 480));
        Assert.True(ContainerDesktopManager.IsUsableFrame(painted, 640, 480));
    }

    [Fact]
    public void ApplicationArguments_AreTokenizedWithoutShellInterpretation()
    {
        string[] args = ContainerDesktopCommandBridge.SplitArguments("--title 'Growth dashboard' --count=2");

        Assert.Equal(new[] { "--title", "Growth dashboard", "--count=2" }, args);
        Assert.Throws<InvalidOperationException>(() => ContainerDesktopCommandBridge.SplitArguments("'unterminated"));
    }

    [Fact]
    public void MissingUiTarget_DoesNotInvalidateHealthyDesktopInfrastructure()
    {
        var semantic = ContainerToolAdapter.ContainerToolResult.Fail("button not found",
            ContainerToolAdapter.ContainerToolFailureKind.Semantic);
        var infrastructure = ContainerToolAdapter.ContainerToolResult.Fail("VNC disconnected",
            ContainerToolAdapter.ContainerToolFailureKind.Infrastructure);

        Assert.False(global::Omnipotent.Services.Projects.Projects.InvalidatesDesktopReadiness(semantic));
        Assert.True(global::Omnipotent.Services.Projects.Projects.InvalidatesDesktopReadiness(infrastructure));
    }
}

public class ProjectPowerShellExitCodeTests
{
    [Fact]
    public async Task FailedNativeCommand_CannotMasqueradeAsPowerShellSuccess()
    {
        var result = await HostShell.RunPowerShellAsync("cmd.exe /c exit 7", TimeSpan.FromSeconds(20));

        Assert.False(result.Success);
        Assert.Equal(7, result.ExitCode);
    }
}
