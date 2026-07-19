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
        public void CapabilityFlags_DoNotAdvertiseUnsupportedOptionalTools()
        {
            var names = VisualComputerToolCatalog.Build(new ComputerCapabilities
            {
                SupportsOcr = false,
                SupportsBrowserControl = false,
                SupportsClipboard = false,
                SupportsAppLaunch = false,
                SupportsWindowControl = false,
            }).Select(t => t.function.name).ToHashSet();

            Assert.DoesNotContain("computer_find_text", names);
            Assert.DoesNotContain("computer_open_browser", names);
            Assert.DoesNotContain("computer_clipboard_get", names);
            Assert.DoesNotContain("computer_launch_app", names);
            Assert.DoesNotContain("computer_focus_window", names);
            Assert.Contains("computer_screenshot", names);
        }

        [Fact]
        public void BrowserCapability_AdvertisesStructuredInspection()
        {
            var names = VisualComputerToolCatalog.Build(new ComputerCapabilities
            {
                SupportsBrowserControl = true,
            }).Select(t => t.function.name).ToHashSet();

            Assert.Contains("computer_browser_inspect", names);
            string scriptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "Omnipotent", "Services", "Projects", "Containers", "browser-inspect.py"));
            string script = File.ReadAllText(scriptPath);
            Assert.Contains("Accessibility.getFullAXTree", script);
            Assert.Contains("performance.getEntriesByType", script);
            Assert.DoesNotContain("x.value", script);
            var inspect = VisualComputerToolCatalog.Build(new ComputerCapabilities { SupportsBrowserControl = true })
                .Single(t => t.function.name == "computer_browser_inspect");
            Assert.Contains("tabIndex", System.Text.Json.JsonSerializer.Serialize(inspect.function.parameters));
            Assert.Contains("interceptedBy", script);
            Assert.Contains("elementFromPoint", script);
            Assert.DoesNotContain("Input.dispatch", script);
        }

        [Fact]
        public void ProjectCatalog_OffersPhysicalSemanticBrowserClick()
        {
            var tool = ProjectCommanderAgent.BuildComputerToolDefinitions()
                .Single(t => t.function.name == "computer_click_browser_control");
            string schema = System.Text.Json.JsonSerializer.Serialize(tool.function.parameters);
            Assert.Contains("name", schema);
            Assert.Contains("role", schema);
            Assert.Contains("modifiers", schema);

            var router = new ProjectTierRouter(new ProjectSettingsStore());
            Assert.True(router.IsToolAllowed(ProjectAgentTier.Text, "computer_click_browser_control"));
        }

        [Fact]
        public void ClickTextContract_AcceptsKeyboardModifiers()
        {
            var clickText = VisualComputerToolCatalog.Build(new ComputerCapabilities { SupportsOcr = true })
                .Single(t => t.function.name == "computer_click_text");
            string schema = System.Text.Json.JsonSerializer.Serialize(clickText.function.parameters);

            Assert.Contains("modifiers", schema);
            Assert.Contains("array", schema);
        }

        [Fact]
        public void PublishedDesktopContext_ContainsEveryDockerCopyDependency()
        {
            string context = Omnipotent.Services.Projects.Containers.ContainerDesktopManager.ResolveBuildContextDirectory();
            foreach (string name in Omnipotent.Services.Projects.Containers.ContainerOrchestrator.DesktopBuildContextFiles)
                Assert.True(File.Exists(Path.Combine(context, name)), $"Missing desktop build asset: {name} in {context}");
            Assert.Contains("browser-inspect.py",
                Omnipotent.Services.Projects.Containers.ContainerOrchestrator.DesktopBuildContextFiles);
        }

        [Fact]
        public void DesktopImage_BootsACompleteHumanDesktop_NotAStandaloneFramebuffer()
        {
            string context = Omnipotent.Services.Projects.Containers.ContainerDesktopManager.ResolveBuildContextDirectory();
            string entrypoint = File.ReadAllText(Path.Combine(context, "desktop-entrypoint.sh"));
            string dockerfile = File.ReadAllText(Path.Combine(context, "desktop.Dockerfile"));

            Assert.Contains("xfce4-session", entrypoint);
            Assert.Contains("xfdesktop", entrypoint);
            Assert.Contains("xfce4-panel", entrypoint);
            Assert.Contains("thunar mousepad ristretto", dockerfile);
            Assert.Contains("\"imageVersion\":\"7\"", dockerfile);
            Assert.Contains("\"desktop-shell\"", dockerfile);
            // The image ships a realistic font set (a thin font list is a browser-fingerprint tell).
            Assert.Contains("fonts-liberation", dockerfile);
            Assert.Contains("fonts-noto-color-emoji", dockerfile);
            // The main-world fingerprint extension is baked in for the launcher to load.
            Assert.Contains("/usr/local/share/klive-fp/patch.js", dockerfile);
            Assert.DoesNotContain("deliberately do NOT run xfdesktop", entrypoint);
        }

        [Fact]
        public void HeldMouseTools_RequireCoordinates()
        {
            var tools = VisualComputerToolCatalog.Build(new ComputerCapabilities());
            foreach (string name in new[] { "computer_mouse_down", "computer_mouse_up" })
            {
                var tool = tools.Single(t => t.function.name == name);
                string schema = System.Text.Json.JsonSerializer.Serialize(tool.function.parameters);
                Assert.Contains("\"required\":[\"x\",\"y\"]", schema);
            }
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

        [Fact]
        public void ProjectSettings_UseRenewableWorkSlices_AndRetireContinuationCap()
        {
            var settings = new ProjectSettings();
            Assert.True(settings.TrySet("workSliceToolCalls", "80"));
            Assert.True(settings.TrySet("workSliceModelTurns", "50"));
            Assert.True(settings.TrySet("maxConsecutiveContinuations", "0")); // accepted legacy no-op

            Assert.Equal(80, settings.WorkSliceToolCalls);
            Assert.Equal(50, settings.WorkSliceModelTurns);
            Assert.Null(typeof(ProjectSettings).GetProperty("MaxConsecutiveContinuations"));
        }

        [Fact]
        public void LegacyHardCapSettings_MigrateToRolloverBoundaries()
        {
            var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<ProjectSettings>(
                "{\"MaxToolCallsPerWake\":73,\"MaxModelTurnsPerWake\":31,\"MaxLoopTripsPerWake\":4}")!;

            Assert.Equal(73, settings.WorkSliceToolCalls);
            Assert.Equal(31, settings.WorkSliceModelTurns);
            Assert.Equal(4, settings.MaxConvergenceTripsPerSlice);
            Assert.DoesNotContain("MaxToolCallsPerWake", Newtonsoft.Json.JsonConvert.SerializeObject(settings));
        }
    }
}
