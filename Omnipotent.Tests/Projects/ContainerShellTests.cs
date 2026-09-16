using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Tests.Projects
{
    public class ContainerShellTests
    {
        [Theory]
        [InlineData(null, "/project")]
        [InlineData("", "/project")]
        [InlineData("/project/repo/../artifacts", "/project/artifacts")]
        [InlineData("/home/agent/work", "/home/agent/work")]
        [InlineData("/agent-runtime/venvs/uploader", "/agent-runtime/venvs/uploader")]
        public void WorkingDirectory_IsNormalizedInsideAgentOwnedRoots(string? input, string expected)
        {
            Assert.Equal(expected, ContainerShellResult.NormalizeWorkingDirectory(input));
        }

        [Theory]
        [InlineData("/")]
        [InlineData("/etc")]
        [InlineData("relative/path")]
        [InlineData("/project/../../etc")]
        public void WorkingDirectory_RejectsPathsOutsideAgentOwnedRoots(string input)
        {
            Assert.Throws<ArgumentException>(() => ContainerShellResult.NormalizeWorkingDirectory(input));
        }

        [Fact]
        public void ResultFormatting_IsBoundedAndReportsTruncation()
        {
            var result = new ContainerShellResult(0, new string('x', 100), "", false, true);

            string formatted = result.Format(80);

            Assert.True(formatted.Length < 150);
            Assert.Contains("output truncated", formatted);
            Assert.DoesNotContain(new string('x', 100), formatted);
        }

        [Theory]
        [InlineData("/project/work/tiktok/venv/bin/python signup.py")]
        [InlineData("work/tiktok/.venv/bin/python3.11 worker.py")]
        [InlineData("'/project/app/node_modules/.bin/vite' build")]
        [InlineData("/project/app/venv/Scripts/python.exe task.py")]
        public void SharedCrossOsRuntimeExecutables_AreRejected(string command)
        {
            Assert.True(ContainerToolAdapter.UsesSharedPlatformRuntime(command));
        }

        [Theory]
        [InlineData("$KLIVE_AGENT_RUNTIME/venv/bin/python /project/task.py")]
        [InlineData("/agent-runtime/node/bin/node /project/app.js")]
        [InlineData("python3 /project/task.py")]
        public void AgentRuntimeAndSystemInterpreters_RemainAvailable(string command)
        {
            Assert.False(ContainerToolAdapter.UsesSharedPlatformRuntime(command));
        }

        [Theory]
        [InlineData("chromium", true)]
        [InlineData("/usr/bin/gimp", true)]
        [InlineData("apps/gimp", false)]
        [InlineData("/usr/../bin/gimp", false)]
        [InlineData("/usr//bin/gimp", false)]
        public void DesktopApplicationExecutable_IsNameOrAbsolutePath(string executable, bool expected)
        {
            Assert.Equal(expected, ContainerDesktopCommandBridge.IsSafeExecutable(executable));
        }

        [Fact]
        public async Task CaptureStream_DiscardsBytesBeyondLimit()
        {
            using var stream = new BoundedCaptureStream(5);

            await stream.WriteAsync("abcdefgh"u8.ToArray());

            Assert.Equal("abcde", stream.GetText());
            Assert.True(stream.Truncated);
        }

        /// <summary>
        /// The quietest half of the 2026-09-16 stall. Queuing behind a wedged action wrote no event
        /// at all, so the agent looked idle for 24 minutes. It must fail, and say why.
        /// </summary>
        [Fact]
        public async Task Adapter_DoesNotQueueForeverBehindAWedgedDesktopAction()
        {
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });
            using var actionGate = new SemaphoreSlim(1, 1);
            var adapter = new ContainerToolAdapter(
                transport, "container", "agent", actionGate,
                visualGateWait: TimeSpan.FromMilliseconds(150));

            await actionGate.WaitAsync();   // stand in for the action that never finished
            try
            {
                var result = await adapter
                    .ExecuteAsync("computer_screenshot", "{}")
                    .WaitAsync(TimeSpan.FromSeconds(5));

                Assert.False(result.Success);
                Assert.Equal(ContainerToolAdapter.ContainerToolFailureKind.Contention, result.FailureKind);
                Assert.Contains("wedged rather than busy", result.Text, StringComparison.Ordinal);
            }
            finally { actionGate.Release(); }
        }

        /// <summary>
        /// The 2026-09-16 stall: a Docker call that ignores its cancellation token. Passing a token
        /// into Docker.DotNet is not a bound — exec attach is a hijacked stream with the library's
        /// own timeout disabled — so the deadline has to be enforced from outside the call.
        /// </summary>
        [Fact]
        public async Task Deadline_AbandonsADockerCallThatIgnoresItsCancellationToken()
        {
            var neverCompletes = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var start = DateTime.UtcNow;

            var thrown = await Assert.ThrowsAsync<ContainerDaemonTimeoutException>(() =>
                ContainerOrchestrator.WithDeadlineAsync(
                    _ => neverCompletes.Task, TimeSpan.FromMilliseconds(150), "an exec", CancellationToken.None));

            Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(5), "the deadline must not wait on the call");
            Assert.Contains("an exec", thrown.Message, StringComparison.Ordinal);
            // The abandoned call is left running on purpose; failing it later must not crash anything.
            neverCompletes.SetException(new InvalidOperationException("late failure from an abandoned exec"));
            await Task.Delay(50);
        }

        [Fact]
        public async Task Deadline_ReturnsTheResultWhenTheCallAnswersInTime()
        {
            string result = await ContainerOrchestrator.WithDeadlineAsync(
                async _ => { await Task.Delay(10); return "ok"; },
                TimeSpan.FromSeconds(30), "an exec", CancellationToken.None);
            Assert.Equal("ok", result);
        }

        /// <summary>A caller cancelling is not the daemon stalling, and must not be reported as one.</summary>
        [Fact]
        public async Task Deadline_SurfacesCallerCancellationRatherThanADaemonTimeout()
        {
            using var caller = new CancellationTokenSource();
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var call = ContainerOrchestrator.WithDeadlineAsync(
                _ => pending.Task, TimeSpan.FromMinutes(5), "an exec", caller.Token);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        }

        /// <summary>
        /// A wedged host must not read to the agent as a bad selector. It retried the action for
        /// twenty minutes last time because the failure said only "TaskCanceledException".
        /// </summary>
        [Fact]
        public async Task Adapter_ReportsADaemonStallAsTheHostsFaultNotTheAgents()
        {
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });
            using var actionGate = new SemaphoreSlim(1, 1);
            var adapter = new ContainerToolAdapter(
                transport, "container", "agent", actionGate,
                terminalAsync: (_, _, _, _) => throw new ContainerDaemonTimeoutException(
                    "the Docker daemon did not complete a command in container abc within 35s"));

            var result = await adapter.ExecuteAsync("computer_terminal",
                "{\"command\":\"echo hi\",\"workingDirectory\":\"/project\"}");

            Assert.False(result.Success);
            Assert.Equal(ContainerToolAdapter.ContainerToolFailureKind.Infrastructure, result.FailureKind);
            Assert.Contains("container host being unresponsive", result.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("TaskCanceledException", result.Text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Adapter_TerminalDoesNotResolveOrEchoVaultPlaceholders()
        {
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });
            using var actionGate = new SemaphoreSlim(1, 1);
            bool resolverCalled = false;
            string? executed = null;
            var adapter = new ContainerToolAdapter(
                transport, "container", "agent", actionGate,
                terminalAsync: (command, workingDirectory, timeoutSeconds, _) =>
                {
                    executed = command;
                    Assert.Equal("/project", workingDirectory);
                    Assert.Equal(12, timeoutSeconds);
                    return Task.FromResult(new ContainerShellResult(0, "installed", "", false, false));
                },
                resolveSecretsAsync: value =>
                {
                    resolverCalled = true;
                    return Task.FromResult(value.Replace("{api_key}", "plaintext-secret"));
                });

            // A wedged/long visual action must not block the independent container shell.
            await actionGate.WaitAsync();
            ContainerToolAdapter.ContainerToolResult result;
            try
            {
                result = await adapter.ExecuteAsync("computer_terminal",
                    "{\"command\":\"echo {api_key}\",\"workingDirectory\":\"/project\",\"timeoutSeconds\":12}")
                    .WaitAsync(TimeSpan.FromSeconds(1));
            }
            finally { actionGate.Release(); }

            Assert.True(result.Success);
            Assert.Equal("echo {api_key}", executed);
            Assert.False(resolverCalled);
            Assert.DoesNotContain("api_key", result.Text);
            Assert.DoesNotContain("plaintext-secret", result.Text);
            Assert.Contains("installed", result.Text);
        }

        [Fact]
        public void CoordinateMapping_PreservesNativeScreenshotPixels()
        {
            Assert.Equal((640, 399), ContainerToolAdapter.MapPointToFramebuffer(
                640, 399, shownWidth: 1280, shownHeight: 800, framebufferWidth: 1280, framebufferHeight: 800));
        }

        [Fact]
        public void CoordinateMapping_ScalesAndRejectsPointsOutsideShownFrame()
        {
            Assert.Equal((1279, 799), ContainerToolAdapter.MapPointToFramebuffer(
                639, 399, shownWidth: 640, shownHeight: 400, framebufferWidth: 1280, framebufferHeight: 800));
            Assert.Throws<ArgumentException>(() => ContainerToolAdapter.MapPointToFramebuffer(
                640, 10, shownWidth: 640, shownHeight: 400, framebufferWidth: 1280, framebufferHeight: 800));
            Assert.Throws<ArgumentException>(() => ContainerToolAdapter.MapPointToFramebuffer(
                -1, 10, shownWidth: 640, shownHeight: 400, framebufferWidth: 1280, framebufferHeight: 800));
        }

        [Fact]
        public async Task Navigate_UsesValidatedContainerLauncherInsteadOfVncTyping()
        {
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });
            ContainerDesktopControlCommand? seenCommand = null;
            string? seenArgument = null;
            var bridge = new ContainerDesktopCommandBridge(transport, (command, argument, _) =>
            {
                seenCommand = command;
                seenArgument = argument;
                return Task.CompletedTask;
            });

            await bridge.NavigateAsync("https://example.test/path?q=one", CancellationToken.None);

            Assert.Equal(ContainerDesktopControlCommand.LaunchBrowser, seenCommand);
            Assert.Equal("https://example.test/path?q=one", seenArgument);
            Assert.False(transport.Connected); // no address-bar key sequence was attempted
        }

        [Fact]
        public void BrowserReadiness_RequiresALiveProcessCdpAndInspectableTab()
        {
            var installedOnly = new Dictionary<string, string>
            {
                ["chromium"] = "yes",
                ["browser-process"] = "down",
                ["browser-cdp"] = "down",
                ["browser-tabs"] = "down",
            };
            Assert.False(ContainerDesktopManager.BrowserControlIsReady(installedOnly));

            installedOnly["browser-process"] = "up";
            installedOnly["browser-cdp"] = "up";
            installedOnly["browser-tabs"] = "up";
            Assert.True(ContainerDesktopManager.BrowserControlIsReady(installedOnly));
        }

        [Fact]
        public void DesktopReadiness_RequiresTheHumanDesktopShellAndPanel()
        {
            var capabilities = new Dictionary<string, string>
            {
                ["display"] = "up",
                ["desktop-shell"] = "up",
                ["panel"] = "up",
                ["window-manager"] = "up",
                ["vnc"] = "up",
                ["frame"] = "usable",
                ["chromium"] = "yes",
                ["browser-inspect"] = "yes",
            };

            Assert.True(ContainerDesktopManager.DesktopControlIsReady(capabilities));
            capabilities["panel"] = "down";
            Assert.False(ContainerDesktopManager.DesktopControlIsReady(capabilities));
            capabilities["panel"] = "up";
            capabilities["desktop-shell"] = "down";
            Assert.False(ContainerDesktopManager.DesktopControlIsReady(capabilities));
        }

        [Fact]
        public void BrowserLauncher_IsSingleProcessAndWaitsForCdp()
        {
            string script = ContainerOrchestrator.BrowserLaunchScriptForExec;

            Assert.Contains("wait_cdp", script);
            Assert.Contains("if cdp_up", script);
            Assert.Contains("pkill -x chromium", script);
            Assert.Contains("pkill -KILL -x chromium", script);
            Assert.Contains("pgrep -x chromium", script);
            Assert.Contains("flock -w 20", script);
            Assert.Contains("Chromium inherits fd 9", script);
            Assert.Contains("singleton markers were not removed", script);
            Assert.DoesNotContain("9>&-", script);
            Assert.DoesNotContain("pkill -f", script);
            Assert.DoesNotContain("pgrep -f", script);
            Assert.Contains("SingletonLock", script);
            Assert.Contains("/json/new?", script);
            Assert.Contains("single supervised launch", script);
            Assert.Contains("optional CDP endpoint is unavailable", script);
            Assert.Contains("browser_visible", script);
            Assert.Contains("--disable-dev-shm-usage", script);
            Assert.Contains("--remote-allow-origins=*", script);
            // Keep navigator.webdriver false / drop the automation hint (bot-detection hygiene).
            Assert.Contains("--disable-blink-features=AutomationControlled", script);
            // The fingerprint extension is loaded only when a persona is present, and degrades to a
            // no-op otherwise (the launcher guards on OMNIPOTENT_FP_JSON + the baked template).
            Assert.Contains("--load-extension", script);
            Assert.Contains("OMNIPOTENT_FP_JSON", script);
            Assert.Contains("fp_args=()", script);
            Assert.Contains("ProxyHandler({})", script);
            Assert.DoesNotContain("--new-tab", script);
            Assert.DoesNotContain('\r', script);
        }

        [Fact]
        public void BrowserInspection_ChallengeIsElevatedWithTheToolThatClearsIt()
        {
            // A captcha used to end the run on a human. The banner must now name the op that solves
            // it, or the agent falls back to request_human and the project stalls for hours.
            string result = ContainerToolAdapter.AnnotateInspection(
                "{\"title\":\"Verify\",\"humanChallenge\":{\"detected\":true,\"signals\":[\"captcha\"]}}");

            Assert.StartsWith("CHALLENGE_DETECTED", result);
            Assert.Contains("Do not retry", result);
            Assert.Contains("solve_challenge", result);
        }

        [Fact]
        public void BrowserInspection_NativeFileDialogIsNamedWithTheToolThatClearsIt()
        {
            // The GTK chooser is invisible to the DOM, so an agent sees a page that simply stopped
            // responding. Inspection has to say what is blocking it and what clears it.
            string result = ContainerToolAdapter.AnnotateInspection(
                "{\"title\":\"Upload\",\"nativeDialog\":{\"open\":true,\"fileChooser\":true,\"windows\":[{\"id\":\"0x1\",\"title\":\"Open File\",\"kind\":\"file-chooser\"}]}}");

            Assert.StartsWith("NATIVE_FILE_DIALOG_OPEN", result);
            Assert.Contains("computer_upload_file", result);
            Assert.Contains("never ask Klives", result);
        }

        [Fact]
        public void BrowserInspection_NonChooserDialogGetsItsOwnInstruction()
        {
            string result = ContainerToolAdapter.AnnotateInspection(
                "{\"nativeDialog\":{\"open\":true,\"fileChooser\":false,\"windows\":[{\"id\":\"0x1\",\"title\":\"Print\",\"kind\":\"dialog\"}]}}");

            Assert.StartsWith("NATIVE_DIALOG_OPEN", result);
            Assert.Contains("escape", result);
        }

        [Fact]
        public void BrowserInspection_QuietPageIsNotAnnotated()
        {
            string json = "{\"title\":\"Home\",\"nativeDialog\":{\"open\":false,\"fileChooser\":false,\"windows\":[]}}";

            Assert.Equal(json, ContainerToolAdapter.AnnotateInspection(json));
        }

        [Fact]
        public void Navigation_ReportsTabReuseAndAutomaticPruning()
        {
            string result = ContainerToolAdapter.DescribeNavigation(
                "{\"reusedTab\":true,\"url\":\"https://example.com/upload\",\"title\":\"Upload\",\"tabIndex\":0,\"tabCount\":2," +
                "\"closedTabs\":[{\"url\":\"about:blank\"},{\"url\":\"https://example.com/upload\"}]}",
                "https://example.com/upload");

            Assert.Contains("Navigated the active browser tab", result);
            Assert.Contains("tab 0 of 2", result);
            Assert.Contains("Closed 2", result);
        }

        [Fact]
        public void Navigation_FallsBackToPlainTextWhenTheHelperOutputIsUnreadable()
        {
            string result = ContainerToolAdapter.DescribeNavigation("not json", "https://example.com/");

            Assert.Equal("Navigated to https://example.com/.", result);
        }

        [Fact]
        public void BrowserLauncher_NormalizesWindowsLineEndingsBeforeLinuxExec()
        {
            string windowsCheckout = "set -u\r\necho ready\r\n";

            string normalized = ContainerOrchestrator.NormalizeLinuxShellScript(windowsCheckout);

            Assert.Equal("set -u\necho ready\n", normalized);
        }
    }
}
