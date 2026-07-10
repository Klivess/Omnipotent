using Omnipotent.Services.ComputerControl;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    public class ComputerControlContractTests
    {
        [Fact]
        public void Audit_RedactsTypedSecretsAndUrlTokens()
        {
            string typed = ComputerAudit.Describe("computer_type", "{\"text\":\"super-secret\"}");
            string url = ComputerAudit.Describe("computer_navigate", "{\"url\":\"https://example.test/a?token=super-secret&x=1\"}");
            string terminal = ComputerAudit.Describe("computer_terminal", "{\"command\":\"curl -H 'token: super-secret' example.test\"}");

            Assert.DoesNotContain("super-secret", typed);
            Assert.Contains("text=<redacted>", typed);
            Assert.DoesNotContain("super-secret", url);
            Assert.Contains("token=<redacted>", url);
            Assert.DoesNotContain("super-secret", terminal);
            Assert.Contains("command=<redacted>", terminal);
        }

        [Fact]
        public void ProjectVisualCatalog_ContainsReliableBrowserAndOcrTools()
        {
            var caps = new ComputerCapabilities
            {
                SupportsTerminalExecution = true,
                SupportedTools = new HashSet<string>(StringComparer.Ordinal)
                {
                    "computer_screenshot", "computer_find_text", "computer_click_text", "computer_wait",
                    "computer_open_browser", "computer_navigate", "computer_launch_app", "computer_terminal"
                }
            };
            var names = VisualComputerToolCatalog.Build(caps).Select(t => t.function.name).ToHashSet();

            Assert.Contains("computer_find_text", names);
            Assert.Contains("computer_click_text", names);
            Assert.Contains("computer_wait", names);
            Assert.Contains("computer_navigate", names);
            Assert.Contains("computer_terminal", names);
            Assert.DoesNotContain("computer_clipboard_set", names);
        }

        [Fact]
        public void VisualCatalog_OffersTerminalOnlyForIsolatedContainerTargets()
        {
            var hostNames = VisualComputerToolCatalog.Build(new ComputerCapabilities())
                .Select(t => t.function.name).ToHashSet();
            var containerTools = VisualComputerToolCatalog.Build(new ComputerCapabilities
            {
                SupportsTerminalExecution = true,
            });

            Assert.DoesNotContain("computer_terminal", hostNames);
            var terminal = Assert.Single(containerTools, t => t.function.name == "computer_terminal");
            string schema = System.Text.Json.JsonSerializer.Serialize(terminal.function.parameters);
            Assert.Contains("command", schema);
            Assert.Contains("workingDirectory", schema);
            Assert.Contains("timeoutSeconds", schema);
        }

        [Fact]
        public void ToolCatalog_UsesMaxMsWhileAllowingLegacyMs()
        {
            var wait = VisualComputerToolCatalog.Build(new ComputerCapabilities())
                .Single(t => t.function.name == "computer_wait");
            string schema = System.Text.Json.JsonSerializer.Serialize(wait.function.parameters);

            Assert.Contains("maxMs", schema);
            Assert.Contains("\"ms\"", schema);
        }

        [Fact]
        public void ProjectSettings_ClampVisualControlPacing()
        {
            var settings = new ProjectSettings();

            Assert.True(settings.TrySet("computerActionSettleMs", "9000"));
            Assert.True(settings.TrySet("computerTypingDelayMs", "-2"));

            Assert.Equal(5000, settings.ComputerActionSettleMs);
            Assert.Equal(0, settings.ComputerTypingDelayMs);
        }
    }
}
