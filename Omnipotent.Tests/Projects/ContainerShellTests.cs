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
            Assert.Contains("pkill -f '[c]hromium'", script);
            Assert.Contains("SingletonLock", script);
            Assert.Contains("/json/new?", script);
            Assert.Contains("single supervised launch", script);
            Assert.DoesNotContain("--new-tab", script);
            Assert.DoesNotContain('\r', script);
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
