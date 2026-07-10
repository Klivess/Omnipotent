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
    }
}
