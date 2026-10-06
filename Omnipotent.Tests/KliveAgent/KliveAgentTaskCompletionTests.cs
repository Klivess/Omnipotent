using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.KliveAgent.Models;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Tests.KliveAgent;

/// <summary>
/// The pieces that decide whether a long, multi-step request ("make a Tumblr account with a
/// KliveMail address, create API keys, send me the details") runs to completion: what a stopped
/// run says, how hard a failed model call is retried, what the model is told after each step, how
/// the run budget is measured, how a restart is survived, and which computer the run drives.
/// </summary>
public sealed class KliveAgentTaskCompletionTests
{
    // ── Stop reasons ──

    [Fact]
    public void RunControl_RecordsTheFirstStopReasonOnly()
    {
        var control = new AgentChatRunControl();
        control.RecordStop(AgentChatRunControl.StopReasonUser);
        control.RecordStop(AgentChatRunControl.StopReasonStall, "no progress for 5 minutes");

        Assert.Equal(AgentChatRunControl.StopReasonUser, control.StopReason);
        Assert.Null(control.StopDetail);
    }

    [Fact]
    public void RunControl_AServiceShutdownIsTheReason_UnlessSomethingStoppedTheRunFirst()
    {
        using var service = new CancellationTokenSource();
        var control = new AgentChatRunControl { ServiceToken = service.Token };
        Assert.Null(control.StopReason);
        service.Cancel();
        control.RecordStop(AgentChatRunControl.StopReasonUser);
        Assert.Equal(AgentChatRunControl.StopReasonShutdown, control.StopReason);

        using var laterShutdown = new CancellationTokenSource();
        var stoppedFirst = new AgentChatRunControl { ServiceToken = laterShutdown.Token };
        stoppedFirst.RecordStop(AgentChatRunControl.StopReasonUser);
        laterShutdown.Cancel();
        Assert.Equal(AgentChatRunControl.StopReasonUser, stoppedFirst.StopReason);
    }

    [Fact]
    public void MarkStopped_AShutdownInterruptsAndQueuesAContinuation_AManualStopDoesNot()
    {
        using var service = new CancellationTokenSource();
        service.Cancel();
        AgentPendingChatResponse Run(AgentChatRunControl control) => new() { Control = control };

        var resumable = Run(new AgentChatRunControl { ServiceToken = service.Token, ResumesAfterShutdown = true });
        Assert.Equal(AgentTaskStatus.Interrupted, Omnipotent.Services.KliveAgent.KliveAgent.MarkStopped(resumable));
        Assert.Equal(AgentChatRunControl.StopReasonShutdown, resumable.StopReason);
        Assert.True(resumable.AutoResumePending);

        var exhausted = Run(new AgentChatRunControl { ServiceToken = service.Token, ResumesAfterShutdown = false });
        Assert.Equal(AgentTaskStatus.Interrupted, Omnipotent.Services.KliveAgent.KliveAgent.MarkStopped(exhausted));
        Assert.False(exhausted.AutoResumePending);

        var manual = new AgentChatRunControl { ResumesAfterShutdown = true };
        manual.RecordStop(AgentChatRunControl.StopReasonUser);
        var stopped = Run(manual);
        Assert.Equal(AgentTaskStatus.Cancelled, Omnipotent.Services.KliveAgent.KliveAgent.MarkStopped(stopped));
        Assert.Equal(AgentChatRunControl.StopReasonUser, stopped.StopReason);
        Assert.False(stopped.AutoResumePending);
    }

    [Fact]
    public void DescribeStop_NamesWhatActuallyHappened_InsteadOfAGenericNoOutputLine()
    {
        string stall = KliveAgentBrain.DescribeStop(AgentChatRunControl.StopReasonStall,
            "no progress for 5 minutes (last activity: waiting for an AIRouter slot (3/3 busy))",
            "waiting for an AIRouter slot", TimeSpan.FromMinutes(8), producedOutput: false);
        Assert.Contains("Stopped automatically", stall);
        Assert.Contains("AIRouter slot", stall);
        Assert.DoesNotContain("no output was produced yet", stall);
        Assert.DoesNotContain("I was waiting", stall); // the watchdog's detail already names the last activity

        string user = KliveAgentBrain.DescribeStop(AgentChatRunControl.StopReasonUser, null, "", TimeSpan.FromSeconds(95), true);
        Assert.Contains("Stopped by you after 1m 35s", user);

        string unknown = KliveAgentBrain.DescribeStop(null, null, "preparing context", TimeSpan.Zero, producedOutput: false);
        Assert.Contains("before I produced any output", unknown);
        Assert.Contains("preparing context", unknown);

        string markdownNote = KliveAgentBrain.DescribeStop(null, null, "_…thinking (step 3)_", TimeSpan.Zero, producedOutput: false);
        Assert.Contains("I was thinking (step 3) at the time.", markdownNote);

        // A shutdown only promises a continuation when one will actually happen.
        string resuming = KliveAgentBrain.DescribeStop(AgentChatRunControl.StopReasonShutdown, null, "", TimeSpan.Zero, true, resumes: true);
        Assert.Contains("continue automatically", resuming);
        string notResuming = KliveAgentBrain.DescribeStop(AgentChatRunControl.StopReasonShutdown, null, "", TimeSpan.Zero, true, resumes: false);
        Assert.Contains("Send the message again", notResuming);
        Assert.DoesNotContain("automatically", notResuming);
    }

    // ── Model retries ──

    [Fact]
    public void ModelRetry_FailsFastOnPermanentErrors_AndRetriesTransientOnes()
    {
        RemoteLLMException Remote(RemoteLLMFailureKind kind) => new(kind, kind.ToString(), "AIRouter", "Qwen3.8");

        Assert.False(KliveAgentBrain.IsRetryableModelFailure(Remote(RemoteLLMFailureKind.Authentication)));
        Assert.False(KliveAgentBrain.IsRetryableModelFailure(Remote(RemoteLLMFailureKind.InvalidRequest)));
        Assert.False(KliveAgentBrain.IsRetryableModelFailure(Remote(RemoteLLMFailureKind.ModelUnavailable)));
        Assert.True(KliveAgentBrain.IsRetryableModelFailure(Remote(RemoteLLMFailureKind.RateLimited)));
        Assert.True(KliveAgentBrain.IsRetryableModelFailure(Remote(RemoteLLMFailureKind.ProviderUnavailable)));
        Assert.True(KliveAgentBrain.IsRetryableModelFailure(new KliveLLMStreamStalledException(TimeSpan.FromMinutes(4), false)));
        Assert.True(KliveAgentBrain.IsRetryableModelFailure(new HttpRequestException("reset"), failedAttempt: 4));
        Assert.True(KliveAgentBrain.IsRetryableModelFailure(new TaskCanceledException("HttpClient timeout"), failedAttempt: 4));
        // An unexpected exception is more likely a bug than a blip: one quick retry, not seven minutes of them.
        Assert.True(KliveAgentBrain.IsRetryableModelFailure(new InvalidOperationException("bug"), failedAttempt: 0));
        Assert.False(KliveAgentBrain.IsRetryableModelFailure(new InvalidOperationException("bug"), failedAttempt: 1));
    }

    [Fact]
    public void ImageRejection_IsRecognised_SoADesktopTaskFallsBackToTextPerception()
    {
        Assert.True(KliveAgentBrain.LooksLikeImageRejection(new RemoteLLMException(RemoteLLMFailureKind.InvalidRequest,
            "AIRouter request failed with status 400. Body: {\"error\":\"model does not support image input\"}", "AIRouter", "Qwen3.8")));
        Assert.True(KliveAgentBrain.LooksLikeImageRejection(new RemoteLLMException(RemoteLLMFailureKind.InvalidRequest,
            "No endpoints found that support input modalities: image", "OpenRouter", "x/y")));
        Assert.False(KliveAgentBrain.LooksLikeImageRejection(new RemoteLLMException(RemoteLLMFailureKind.InvalidRequest,
            "context length exceeded", "AIRouter", "Qwen3.8")));
        Assert.False(KliveAgentBrain.LooksLikeImageRejection(new RemoteLLMException(RemoteLLMFailureKind.RateLimited,
            "image rate limit", "AIRouter", "Qwen3.8")));
    }

    [Fact]
    public void ModelRetryDelay_GrowsToMinutes_AndHonoursTheProvidersRetryAfter()
    {
        var plain = new HttpRequestException("reset");
        Assert.Equal(TimeSpan.FromSeconds(3), KliveAgentBrain.ModelRetryDelay(0, plain));
        Assert.Equal(TimeSpan.FromSeconds(30), KliveAgentBrain.ModelRetryDelay(2, plain));
        Assert.Equal(TimeSpan.FromSeconds(180), KliveAgentBrain.ModelRetryDelay(99, plain));

        var limited = new RemoteLLMException(RemoteLLMFailureKind.RateLimited, "429", "AIRouter", "Qwen3.8",
            retryAfter: TimeSpan.FromSeconds(42));
        Assert.Equal(TimeSpan.FromSeconds(42), KliveAgentBrain.ModelRetryDelay(0, limited));

        var long429 = new RemoteLLMException(RemoteLLMFailureKind.RateLimited, "429", "AIRouter", "Qwen3.8",
            retryAfter: TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromMinutes(5), KliveAgentBrain.ModelRetryDelay(0, long429));
    }

    [Fact]
    public void ModelFailure_KeepsProgressAndSaysNothingMoreWasAttempted()
    {
        string text = KliveAgentBrain.DescribeModelFailure(
            new KliveLLMStreamStalledException(TimeSpan.FromMinutes(4), false), attempts: 7, "Created the mailbox.");
        Assert.StartsWith("Created the mailbox.", text);
        Assert.Contains("7 attempts", text);
        Assert.Contains("went silent", text);
    }

    // ── Step guidance ──

    [Fact]
    public void StepGuidance_InATask_NeverTellsTheModelToAnswerAfterOneSuccessfulStep()
    {
        string task = KliveAgentBrain.BuildStepGuidance(outputBroken: false, cleanProgress: true, taskMode: true);
        Assert.DoesNotContain("give the final text-only answer NOW", task);
        Assert.Contains("NEXT step", task);
        Assert.Contains("EVERY part", task);

        string question = KliveAgentBrain.BuildStepGuidance(outputBroken: false, cleanProgress: true, taskMode: false);
        Assert.Contains("final text-only answer NOW", question);
    }

    [Theory]
    [InlineData("Account created and verified. Next I'll register the app to get your API keys.", true)]
    [InlineData("Now I'll fill in the registration form.", true)]
    [InlineData("The email is verified. I'll now open the OAuth apps page.", true)]
    [InlineData("Signup submitted — proceeding to the email verification.", true)]
    [InlineData("All done. Email: tumblr.klives@klive.dev, password: {account:tumblr.com/password}.", false)]
    [InlineData("Done — here are the keys. Let me know if you want me to set up a second app.", false)]
    [InlineData("Everything is set up. If you want, I'll also enable 2FA next.", false)]
    [InlineData("", false)]
    public void UnfinishedTaskReply_IsTheOneThatAnnouncesItsNextStep(string reply, bool unfinished) =>
        Assert.Equal(unfinished, KliveAgentBrain.LooksLikeUnfinishedTaskReply(reply));

    [Theory]
    [InlineData("computer_navigate", true)]
    [InlineData("computer_browser_action", true)]
    [InlineData("klivemail_wait_for_email", true)]
    [InlineData("account_register", true)]
    [InlineData("notify_klives", true)]
    [InlineData("recall_memories", false)]
    [InlineData("grep", false)]
    [InlineData("execute_csharp", false)]
    public void TaskTools_AreTheOnesThatActOnTheWorld(string tool, bool isTask) =>
        Assert.Equal(isTask, KliveAgentBrain.IsTaskTool(tool));

    // ── Run budget ──

    [Fact]
    public void BillableTokens_CountCachedReadsAtATenth_SoATaskIsNotBilledOncePerStep()
    {
        Assert.Equal(1_000 + 200 + 4_000, KliveAgentBrain.BillableTokens(promptTokens: 41_000, cachedPromptTokens: 40_000, completionTokens: 200));
        Assert.Equal(5_200, KliveAgentBrain.BillableTokens(5_000, 0, 200));
        // A provider that over-reports cached tokens can never produce a negative charge.
        Assert.Equal(100 + 500, KliveAgentBrain.BillableTokens(5_000, 9_000, 100));
    }

    [Fact]
    public void FormatElapsed_IsReadable()
    {
        Assert.Equal("42s", KliveAgentBrain.FormatElapsed(TimeSpan.FromSeconds(42)));
        Assert.Equal("3m 05s", KliveAgentBrain.FormatElapsed(TimeSpan.FromSeconds(185)));
        Assert.Equal("2h 01m", KliveAgentBrain.FormatElapsed(TimeSpan.FromMinutes(121)));
    }

    // ── Surviving a restart ──

    [Fact]
    public void AutoResume_ContinuesRecentInterruptedWork_ButNeverLoops()
    {
        var now = DateTime.UtcNow;
        AgentPendingChatResponse Run(AgentTaskStatus status, int resumes = 0, double ageHours = 0.5) => new()
        {
            Status = status, ResumeCount = resumes, UserMessage = "make a tumblr account", CreatedAt = now.AddHours(-ageHours),
        };

        Assert.True(Omnipotent.Services.KliveAgent.KliveAgent.ShouldAutoResume(Run(AgentTaskStatus.Interrupted), now, 6));
        Assert.False(Omnipotent.Services.KliveAgent.KliveAgent.ShouldAutoResume(Run(AgentTaskStatus.Cancelled), now, 6));
        Assert.False(Omnipotent.Services.KliveAgent.KliveAgent.ShouldAutoResume(Run(AgentTaskStatus.Completed), now, 6));
        Assert.False(Omnipotent.Services.KliveAgent.KliveAgent.ShouldAutoResume(
            Run(AgentTaskStatus.Interrupted, resumes: Omnipotent.Services.KliveAgent.KliveAgent.MaxAutoResumes), now, 6));
        Assert.False(Omnipotent.Services.KliveAgent.KliveAgent.ShouldAutoResume(Run(AgentTaskStatus.Interrupted, ageHours: 9), now, 6));
    }

    [Fact]
    public void ContinuationMessage_CarriesTheRequestAndTheWorkThatOnlyLivedInTheActivityLog()
    {
        var run = new AgentPendingChatResponse
        {
            UserMessage = "use your computer to make a tumblr account using a KliveMail email address",
            Response = "Signing up now.",
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            Activity = new List<AgentActivityEvent>
            {
                new() { Iteration = 2, Kind = "tool", Text = "klivemail_create_mailbox ok" },
                new() { Iteration = 5, Kind = "action", Text = "computer_browser_action ok" },
            },
            SteeringMessages = new List<AgentSteeringMessage> { new() { Message = "use the blog name klivesbot", Status = "applied" } },
        };

        string message = Omnipotent.Services.KliveAgent.KliveAgent.BuildContinuationMessage(run);

        Assert.Contains("tumblr account", message);
        Assert.Contains("klivemail_create_mailbox ok", message);
        Assert.Contains("use the blog name klivesbot", message);
        Assert.Contains("Signing up now.", message);
        Assert.Contains("never create a second account", message);
    }

    [Fact]
    public void FinishedDm_LinksToTheConversation_AndNeverCarriesTheReply()
    {
        var run = new AgentPendingChatResponse
        {
            ConversationId = "conv_abc",
            Status = AgentTaskStatus.Completed,
            UserMessage = "make a tumblr account and send me the login details",
            Response = "Password: hunter2-very-secret",
            FinalResponse = new AgentChatResponse { Response = "Password: hunter2-very-secret" },
        };

        string dm = Omnipotent.Services.KliveAgent.KliveAgent.BuildFinishedDm(run, TimeSpan.FromMinutes(23));

        Assert.Contains("finished", dm);
        Assert.Contains("make a tumblr account", dm);
        Assert.Contains("/kliveagent?conversation=conv_abc", dm);
        Assert.DoesNotContain("hunter2", dm);
    }

    // ── The computer ──

    [Theory]
    [InlineData("auto", true, true, KliveAgentComputerTarget.Container)]
    [InlineData("auto", false, true, KliveAgentComputerTarget.Host)]
    [InlineData("auto", false, false, KliveAgentComputerTarget.None)]
    [InlineData("container", false, true, KliveAgentComputerTarget.None)]
    [InlineData("host", true, true, KliveAgentComputerTarget.Host)]
    [InlineData("host", true, false, KliveAgentComputerTarget.None)]
    public void ComputerTarget_PrefersItsOwnDesktop_AndNeverSilentlySwitchesAnExplicitChoice(
        string setting, bool container, bool host, KliveAgentComputerTarget expected) =>
        Assert.Equal(expected, KliveAgentComputer.ChooseTarget(setting, container, host));

    [Fact]
    public void ContainerTools_IncludeStructuredBrowserControl_AndKliveAgentsOwnGates()
    {
        var tools = KliveAgentComputer.BuildContainerToolDefinitions(visionEnabled: true);
        var names = tools.Select(t => t.function.name).ToList();

        foreach (string required in new[] { "computer_navigate", "computer_browser_inspect", "computer_browser_action",
                     "computer_upload_file", "computer_terminal", "computer_screenshot", "computer_confirm_action",
                     "computer_confirm_and_click", "request_human", "save_encrypted_memory" })
            Assert.Contains(required, names);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.DoesNotContain(tools, t => t.function.description.Contains("Project approval gate"));
        Assert.All(tools, t => Assert.Equal("function", t.type));
    }

    [Fact]
    public void ContainerTools_WithoutVision_DropRawScreenshots_ButKeepTextPerception()
    {
        var names = KliveAgentComputer.BuildContainerToolDefinitions(visionEnabled: false).Select(t => t.function.name).ToList();
        Assert.DoesNotContain("computer_screenshot", names);
        Assert.Contains("computer_browser_inspect", names);
        Assert.Contains("computer_read_screen", names);
    }

    [Fact]
    public void BrainToolCatalogue_OffersTheNewTaskTools_WithoutDuplicates()
    {
        var names = KliveAgentBrain.BuildToolDefinitions(includeComputerUse: false).Select(t => t.function.name).ToList();
        foreach (string tool in new[] { "klivemail_create_mailbox", "klivemail_wait_for_email", "klivemail_list_messages",
                     "klivemail_get_message", "account_update", "notify_klives" })
            Assert.Contains(tool, names);
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    // ── Prompt policy ──

    [Fact]
    public void CompactPolicy_DrivesTasksToCompletion_AndExplainsHowToDeliverCredentials()
    {
        string policy = KliveAgentPromptPolicy.Build(true, true, KliveAgentComputerTarget.Container, visionEnabled: true);

        Assert.Contains("[Multi-step tasks]", policy);
        Assert.Contains("klivemail_wait_for_email", policy);
        Assert.Contains("{generate}", policy);
        Assert.Contains("his dashboard reveals them", policy);
        Assert.Contains("computer_browser_inspect", policy);
        Assert.Contains("Steps Klives explicitly asked for", policy);
        Assert.DoesNotContain("You can SEE and physically CONTROL this Windows machine", policy);
    }

    [Fact]
    public void CompactPolicy_WithoutVision_TellsTheModelHowToPerceive()
    {
        string policy = KliveAgentPromptPolicy.Build(true, true, KliveAgentComputerTarget.Container, visionEnabled: false);
        Assert.Contains("computer_read_screen", policy);
        Assert.Contains("not a blocker", policy);
    }

    [Fact]
    public void RunSettings_NormalizeTheComputerTarget()
    {
        Assert.Equal("container", KliveAgentRunSettings.NormalizeTarget(" Container "));
        Assert.Equal("host", KliveAgentRunSettings.NormalizeTarget("HOST"));
        Assert.Equal("auto", KliveAgentRunSettings.NormalizeTarget("something-else"));
        Assert.Equal("auto", KliveAgentRunSettings.NormalizeTarget(null));
    }
}
