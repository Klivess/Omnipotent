using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Projects;
using Omnipotent.Services.Projects.Containers;
using Omnipotent.Services.ComputerControl;
using System.Runtime.Versioning;
using System.Text;

namespace Omnipotent.Tests.Projects;

/// <summary>
/// Pins the project-agent capability contract when raw image input is disabled. Vision is an
/// optional observation channel: removing it must not quietly remove browser, OCR, CLI or input
/// capabilities, nor leave an unreachable operation hidden behind the provider-facing facades.
/// </summary>
[Collection("ProjectsSerial")]
[SupportedOSPlatform("windows")]
public class NonVisionProjectCapabilityTests
{
    private static readonly string[] NonVisualSurface =
    [
        "computer_window_state",
        "computer_read_screen",
        "computer_scroll",
        "computer_click",
        "computer_browser_action",
        "computer_terminal",
        "execute_csharp",
        "http_request",
        "read_file",
        "send_agent_message",
    ];

    private static readonly string[] BrowserActionOps =
    [
        "click", "fill", "type", "select", "check", "uncheck", "focus", "hover",
        "scroll_into_view", "scroll", "press", "wait", "back", "forward", "reload",
        "activate_tab", "close_tab", "script",
    ];

    private static ProjectTierRouter Router() => new(new ProjectSettingsStore());

    [Fact]
    public void CommanderVisionDisabled_RemovesOnlyRawScreenshot()
    {
        var visual = Names(ProjectAgentToolCatalog.BuildCommanderCanonical(visionEnabled: true));
        var nonVisual = Names(ProjectAgentToolCatalog.BuildCommanderCanonical(visionEnabled: false));

        Assert.Equal(new[] { "computer_screenshot" },
            visual.Except(nonVisual, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Empty(nonVisual.Except(visual, StringComparer.Ordinal));
        AssertRetainsNonVisualSurface(nonVisual);
    }

    [Theory]
    [InlineData(ProjectAgentTier.Text)]
    [InlineData(ProjectAgentTier.TextImage)]
    [InlineData(ProjectAgentTier.TextImageVideo)]
    [InlineData(ProjectAgentTier.TextImageVideoAudio)]
    public void EveryWorkerTierVisionDisabled_RetainsFullNonVisualSurface(
        ProjectAgentTier tier)
    {
        var router = Router();
        var visual = Names(ProjectAgentToolCatalog.BuildWorkerCanonical(
            router, tier, visionEnabled: true));
        var nonVisual = Names(ProjectAgentToolCatalog.BuildWorkerCanonical(
            router, tier, visionEnabled: false));

        AssertRetainsNonVisualSurface(nonVisual);
        Assert.DoesNotContain("computer_screenshot", nonVisual);
        Assert.Empty(nonVisual.Except(visual, StringComparer.Ordinal));

        var removed = visual.Except(nonVisual, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        if (tier == ProjectAgentTier.Text)
            Assert.Empty(removed);
        else
            Assert.Equal(new[] { "computer_screenshot" }, removed);
    }

    [Theory]
    [InlineData(ProjectAgentTier.Text, false)]
    [InlineData(ProjectAgentTier.TextImage, true)]
    [InlineData(ProjectAgentTier.TextImageVideo, true)]
    [InlineData(ProjectAgentTier.TextImageVideoAudio, true)]
    public void VisionEnabled_OffersScreenshotOnlyToImageCapableTiers(
        ProjectAgentTier tier, bool expected)
    {
        var names = Names(ProjectAgentToolCatalog.BuildWorkerCanonical(
            Router(), tier, visionEnabled: true));

        Assert.Equal(expected, names.Contains("computer_screenshot"));
    }

    [Fact]
    public void FoldedSurface_AllTierAndVisionProfilesStayReachableDistinctAndUnderLimit()
    {
        var router = Router();
        var profiles = new List<(string Name, IReadOnlyList<HFWrapper.HFTool> Canonical, bool Vision)>
        {
            ("commander/nonvisual", ProjectAgentToolCatalog.BuildCommanderCanonical(false), false),
            ("commander/visual", ProjectAgentToolCatalog.BuildCommanderCanonical(true), true),
        };
        foreach (var tier in Enum.GetValues<ProjectAgentTier>())
        {
            profiles.Add(($"worker/{tier}/nonvisual",
                ProjectAgentToolCatalog.BuildWorkerCanonical(router, tier, false), false));
            profiles.Add(($"worker/{tier}/visual",
                ProjectAgentToolCatalog.BuildWorkerCanonical(router, tier, true), true));
        }

        foreach (var profile in profiles)
        {
            var offered = ProjectToolFacade.Fold(profile.Canonical);
            var offeredNames = offered.Select(t => t.function.name).ToArray();
            Assert.Equal(offeredNames.Length,
                offeredNames.Distinct(StringComparer.Ordinal).Count());

            var browser = Assert.Single(offered, t => t.function.name == "browser");
            var desktop = Assert.Single(offered, t => t.function.name == "desktop");
            AssertDistinctOps(browser, profile.Name);
            AssertDistinctOps(desktop, profile.Name);
            Assert.Equal(
                profile.Canonical.Any(t => t.function.name == "computer_screenshot"),
                OpsOf(desktop).Contains("screenshot", StringComparer.Ordinal));

            foreach (var canonical in profile.Canonical.Select(t => t.function.name)
                         .Where(IsBrowserOrDesktopCanonical))
            {
                Assert.True(IsReachable(canonical, offered),
                    $"{profile.Name}: canonical capability '{canonical}' is unreachable.");
            }

            if (!profile.Vision)
            {
                Assert.DoesNotContain("computer_screenshot",
                    profile.Canonical.Select(t => t.function.name));
                Assert.DoesNotContain("screenshot", OpsOf(desktop));
            }
        }
    }

    [Fact]
    public void ComputerCatalogRouterAndAdapterStayInParity()
    {
        var canonical = ProjectAgentToolCatalog.BuildCommanderCanonical(visionEnabled: true)
            .Select(tool => tool.function.name)
            .Where(name => name == "ensure_desktop_ready"
                           || name.StartsWith("computer_", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            ProjectTierRouter.KnownComputerToolNames.Order(StringComparer.Ordinal),
            canonical.Order(StringComparer.Ordinal));

        var centrallyDispatched = new HashSet<string>(StringComparer.Ordinal)
        {
            "ensure_desktop_ready",
            "computer_confirm_action",
            "computer_confirm_and_click",
        };
        Assert.Equal(
            canonical.Except(centrallyDispatched, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
            ContainerToolAdapter.SupportedToolNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void BrowserSchemas_ExposeControlsSemanticTargetsAndActionEnum()
    {
        var canonical = ProjectAgentToolCatalog.BuildCommanderCanonical(visionEnabled: false);
        var inspect = SchemaOf(Assert.Single(canonical,
            t => t.function.name == "computer_browser_inspect"));
        var action = SchemaOf(Assert.Single(canonical,
            t => t.function.name == "computer_browser_action"));
        var click = SchemaOf(Assert.Single(canonical,
            t => t.function.name == "computer_click_browser_control"));

        Assert.Contains("controls", inspect["properties"]?["mode"]?["description"]?.Value<string>());
        Assert.NotNull(inspect["properties"]?["maxItems"]);
        Assert.NotNull(inspect["properties"]?["tabIndex"]);
        Assert.Equal("op", (string?)(action["required"] as JArray)?.Single());

        var actionOps = action["properties"]?["op"]?["enum"]?.Values<string>()
            .Where(value => value != null).Select(value => value!).ToArray() ?? [];
        Assert.Equal(actionOps.Length, actionOps.Distinct(StringComparer.Ordinal).Count());
        foreach (string op in BrowserActionOps)
            Assert.Contains(op, actionOps);

        foreach (string target in new[]
                 {
                     "ref", "name", "text", "role", "tag", "css", "label",
                     "placeholder", "testId", "exact", "occurrence",
                 })
        {
            Assert.NotNull(action["properties"]?[target]);
            Assert.NotNull(click["properties"]?[target]);
        }

        var foldedBrowser = Assert.Single(ProjectToolFacade.Fold(canonical),
            t => t.function.name == "browser");
        var foldedOps = OpsOf(foldedBrowser).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("inspect", foldedOps);
        Assert.Contains("physical_click", foldedOps);
        foreach (string op in BrowserActionOps)
            Assert.Contains(op, foldedOps);

        var structuredClick = ProjectToolFacade.Unfold(
            "browser", """{"op":"click","ref":"kref"}""");
        Assert.True(structuredClick.IsValid, structuredClick.ErrorText);
        Assert.Equal("computer_browser_action", structuredClick.ToolName);
        Assert.Equal("click", (string?)JObject.Parse(structuredClick.ArgumentsJson)["op"]);

        var physicalClick = ProjectToolFacade.Unfold(
            "browser", """{"op":"physical_click","ref":"kref"}""");
        Assert.True(physicalClick.IsValid, physicalClick.ErrorText);
        Assert.Equal("computer_click_browser_control", physicalClick.ToolName);
        Assert.Null(JObject.Parse(physicalClick.ArgumentsJson)["op"]);
    }

    [Fact]
    public void UploadContract_AcceptsPathsWithoutLegacySingularPath()
    {
        var tools = ProjectAgentToolCatalog.BuildCommanderCanonical(visionEnabled: false);
        var result = ProjectToolContract.ValidateAndNormalize(
            "computer_upload_file",
            """{"paths":["/project/outputs/one.png","/project/outputs/two.png"]}""",
            tools);

        Assert.True(result.IsValid, result.ErrorText);
        var normalized = JObject.Parse(result.NormalizedArgumentsJson!);
        Assert.Null(normalized["path"]);
        Assert.Equal(
            new[] { "/project/outputs/one.png", "/project/outputs/two.png" },
            normalized["paths"]!.Values<string>());
    }

    [Fact]
    public void CommanderPrompt_MatchesItsEffectivePerceptionProfile()
    {
        var project = new Project
        {
            ProjectID = "prompt-contract",
            Name = "Prompt contract",
            Goal = "Exercise the available tools.",
            Status = ProjectStatus.Active,
        };

        string nonVisual = ProjectCommanderAgent.BuildSystemPrompt(project, visionEnabled: false);
        Assert.Contains("NON-VISUAL CONTROL (authoritative capability profile)", nonVisual);
        Assert.Contains("Raw screenshots are deliberately not attached", nonVisual);
        Assert.Contains("desktop op=window_state/read_screen", nonVisual);
        Assert.Contains("browser op=inspect mode=controls", nonVisual);
        // Negative capability warnings may name screenshots/grids; positive pixel instructions may not.
        Assert.DoesNotContain("observe with desktop op=screenshot", nonVisual,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("final frame is current and gridded", nonVisual,
            StringComparison.OrdinalIgnoreCase);

        string visual = ProjectCommanderAgent.BuildSystemPrompt(project, visionEnabled: true);
        Assert.Contains("VISUAL + STRUCTURED CONTROL", visual);
        Assert.Contains("observe with desktop op=screenshot", visual,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkerPrompt_UsesTheSameVisionSeamAsItsToolCatalog()
    {
        var project = new Project { Goal = "Inspect and operate a website." };
        var agent = new ProjectAgentRecord
        {
            AgentID = "worker-1",
            ProjectID = "prompt-contract",
            Role = "operator",
            Tier = ProjectAgentTier.TextImage,
        };

        string nonVisual = ProjectSubAgentRunner.BuildSystemPrompt(
            project, agent, visionEnabled: false);
        Assert.Contains("RAW SCREENSHOTS ARE NOT VISIBLE TO THIS MODEL", nonVisual);
        Assert.Contains("desktop op=window_state/read_screen", nonVisual);
        Assert.Contains("cross-frame/shadow-DOM refs", nonVisual);
        Assert.DoesNotContain("Screenshots are available as an extra observation channel",
            nonVisual);
        Assert.DoesNotContain("final frame is current and gridded", nonVisual,
            StringComparison.OrdinalIgnoreCase);

        string visual = ProjectSubAgentRunner.BuildSystemPrompt(
            project, agent, visionEnabled: true);
        Assert.Contains("Screenshots are available as an extra observation channel", visual);
    }

    [Fact]
    public async Task MixedFallbackRoutes_DisableOptionalImageInputConservatively()
    {
        static ModelCapabilities Capabilities(bool imageInput) => new(
            NativeToolCalling: true,
            ImageInput: imageInput,
            AudioInput: false,
            DocumentInput: false,
            PromptCaching: false,
            Reasoning: false);

        var byRoute = new Dictionary<string, ModelCapabilities>(StringComparer.Ordinal)
        {
            ["primary/image"] = Capabilities(true),
            ["fallback/image"] = Capabilities(true),
            ["fallback/text"] = Capabilities(false),
        };
        Task<ModelCapabilities> Lookup(string route, CancellationToken _) =>
            Task.FromResult(byRoute[route]);

        Assert.True(await ProjectAgentToolCatalog.AllRoutesSupportImageInputAsync(
            ["primary/image", "fallback/image"], Lookup));
        Assert.False(await ProjectAgentToolCatalog.AllRoutesSupportImageInputAsync(
            ["primary/image", "fallback/text"], Lookup));
        Assert.False(await ProjectAgentToolCatalog.AllRoutesSupportImageInputAsync(
            ["primary/image", "missing/route"], Lookup));
    }

    [Theory]
    [InlineData("""{"ok":false,"error":{"code":"stale-ref","message":"The element moved."}}""",
        "stale-ref: The element moved.")]
    [InlineData("""{"ok":false}""",
        "The structured browser helper rejected the action.")]
    [InlineData("""{"ok":true,"result":{"clicked":true}}""", null)]
    [InlineData("not-json", null)]
    public void BrowserHelperTransportSuccess_DoesNotHideSemanticFailure(
        string helperJson, string? expectedError)
    {
        Assert.Equal(expectedError,
            ContainerToolAdapter.BrowserHelperReportedError(helperJson));
    }

    [Fact]
    public async Task StructuredFill_UsesTheActionHelperAndOneWayResolvedValueWithoutVnc()
    {
        using var transport = new VncTransport("127.0.0.1", 1, _ => { });
        using var gate = new SemaphoreSlim(1, 1);
        JObject? payload = null;
        string? shellCommand = null;
        bool resolverCalled = false;
        var adapter = new ContainerToolAdapter(
            transport, "container", "worker", gate,
            dockerControlAsync: (_, _, _) => Task.CompletedTask,
            terminalAsync: (command, workingDirectory, _, _) =>
            {
                shellCommand = command;
                Assert.Equal("/project", workingDirectory);
                payload = DecodeBrowserHelperPayload(command, "action");
                return Task.FromResult(new ContainerShellResult(
                    0,
                    """{"ok":true,"op":"fill","url":"https://example.test/form","title":"Form","readyState":"complete"}""",
                    "", false, false));
            },
            resolveSecretsAsync: value =>
            {
                resolverCalled = true;
                return Task.FromResult(value.Replace("{form_secret}", "resolved-secret"));
            },
            actionSettleMs: 50);

        var result = await adapter.ExecuteAsync("computer_browser_action",
            """{"op":"fill","ref":"kref1_test","value":"{form_secret}","tabIndex":2}""");

        Assert.True(result.Success, result.Text);
        Assert.True(resolverCalled);
        Assert.False(transport.Connected);
        Assert.NotNull(payload);
        Assert.Equal("fill", (string?)payload!["op"]);
        Assert.Equal("kref1_test", (string?)payload["ref"]);
        Assert.Equal("resolved-secret", (string?)payload["value"]);
        Assert.Equal(2, (int?)payload["tabIndex"]);
        Assert.DoesNotContain("resolved-secret", shellCommand);
        Assert.DoesNotContain("resolved-secret", result.Text);
    }

    [Fact]
    public async Task StructuredHelperErrorEnvelope_IsSemanticAndIsNotRetriedAsStartupFailure()
    {
        using var transport = new VncTransport("127.0.0.1", 1, _ => { });
        using var gate = new SemaphoreSlim(1, 1);
        int calls = 0;
        var adapter = new ContainerToolAdapter(
            transport, "container", "worker", gate,
            dockerControlAsync: (_, _, _) => Task.CompletedTask,
            terminalAsync: (command, _, _, _) =>
            {
                calls++;
                var payload = DecodeBrowserHelperPayload(command, "action");
                Assert.Equal("fill", (string?)payload["op"]);
                return Task.FromResult(new ContainerShellResult(
                    2,
                    """{"ok":false,"error":{"code":"stale-ref","message":"Inspect controls again."}}""",
                    "", false, false));
            },
            actionSettleMs: 50);

        var result = await adapter.ExecuteAsync("computer_browser_action",
            """{"op":"fill","ref":"kref1_stale","value":"new text"}""");

        Assert.False(result.Success);
        Assert.Equal(ContainerToolAdapter.ContainerToolFailureKind.Semantic, result.FailureKind);
        Assert.Contains("stale-ref", result.Text);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PhysicalBrowserClick_UsesCrossFrameLocatorAndSelectedTabBeforeVncInput()
    {
        using var transport = new VncTransport("127.0.0.1", 1, _ => { });
        using var gate = new SemaphoreSlim(1, 1);
        int calls = 0;
        var adapter = new ContainerToolAdapter(
            transport, "container", "worker", gate,
            dockerControlAsync: (_, _, _) => Task.CompletedTask,
            terminalAsync: (command, _, _, _) =>
            {
                calls++;
                var payload = DecodeBrowserHelperPayload(command, "action");
                Assert.Equal("locate", (string?)payload["op"]);
                Assert.Equal("kref1_cross_frame", (string?)payload["ref"]);
                Assert.Equal(3, (int?)payload["tabIndex"]);
                // A structured error stops before any VNC coordinate operation.
                return Task.FromResult(new ContainerShellResult(
                    2,
                    """{"ok":false,"error":{"code":"stale-ref","message":"The frame navigated."}}""",
                    "", false, false));
            },
            actionSettleMs: 50);

        var result = await adapter.ExecuteAsync("computer_click_browser_control",
            """{"ref":"kref1_cross_frame","tabIndex":3}""");

        Assert.False(result.Success);
        Assert.Contains("stale-ref", result.Text);
        Assert.False(transport.Connected);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void BrowserHelper_ContainsNonVisualTraversalRefsActionsAndSensitiveRedaction()
    {
        string scriptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Omnipotent", "Services", "Projects", "Containers",
            "browser-inspect.py"));
        string script = File.ReadAllText(scriptPath);

        // Frames and open shadow roots are traversed into bounded isolated worlds.
        Assert.Contains("Page.getFrameTree", script);
        Assert.Contains("Page.createIsolatedWorld", script);
        Assert.Contains("shadowRoot", script);
        Assert.Contains("MAX_FRAMES", script);

        // Returned controls carry opaque, stale-detecting references rather than raw selectors.
        Assert.Contains("REF_PREFIX", script);
        Assert.Contains("encode_ref", script);
        Assert.Contains("decode_ref", script);
        Assert.Contains("\"stale-ref\"", script);

        // Action support uses the selected control and includes guarded waits and browser scripts.
        Assert.Matches(@"def\s+do_control\s*\(", script);
        Assert.Matches(@"[""']control[""']\s*:\s*do_control", script);
        Assert.Contains("globalThis.__kliveSelected", script);
        Assert.Contains("dispatchKeyEvent", script);
        Assert.Contains("dispatchMouseEvent", script);
        Assert.Contains("wait_for_control", script);
        Assert.Contains("run_guarded_script", script);
        foreach (string op in BrowserActionOps)
            Assert.Contains(op, script);

        // URLs and last-resort script results are bounded/redacted; direct secret surfaces and
        // hidden network exfiltration are rejected by markers, not merely discouraged in prompts.
        Assert.Contains("SENSITIVE_URL_KEY", script);
        Assert.Contains("SENSITIVE_RESULT_KEY", script);
        Assert.Contains("SCRIPT_BLOCKS", script);
        Assert.Contains("document.cookie", script);
        Assert.Contains("localStorage", script);
        Assert.Contains("XMLHttpRequest", script);
        Assert.Contains("<redacted>", script);
    }

    private static JObject DecodeBrowserHelperPayload(string command, string expectedMode)
    {
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(parts.Length >= 4, command);
        Assert.Equal(expectedMode, parts[^2]);
        string encoded = parts[^1].Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        return JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
    }

    private static HashSet<string> Names(IEnumerable<HFWrapper.HFTool> tools) =>
        tools.Select(t => t.function.name).ToHashSet(StringComparer.Ordinal);

    private static void AssertRetainsNonVisualSurface(IReadOnlySet<string> names)
    {
        Assert.DoesNotContain("computer_screenshot", names);
        foreach (string required in NonVisualSurface)
            Assert.Contains(required, names);
    }

    private static bool IsBrowserOrDesktopCanonical(string name) =>
        name == "ensure_desktop_ready" || name.StartsWith("computer_", StringComparison.Ordinal);

    private static bool IsReachable(
        string canonicalName, IReadOnlyList<HFWrapper.HFTool> offered)
    {
        if (offered.Any(t => t.function.name == canonicalName))
            return true;

        foreach (var folded in offered.Where(t =>
                     ProjectToolFacade.FoldedToolNames.Contains(t.function.name)))
        {
            foreach (string op in OpsOf(folded))
            {
                string arguments = new JObject { ["op"] = op }.ToString(Formatting.None);
                var unfolded = ProjectToolFacade.Unfold(folded.function.name, arguments);
                if (unfolded.IsValid && unfolded.ToolName == canonicalName)
                    return true;
            }
        }
        return false;
    }

    private static void AssertDistinctOps(HFWrapper.HFTool folded, string profileName)
    {
        var ops = OpsOf(folded).ToArray();
        Assert.NotEmpty(ops);
        Assert.True(ops.Length == ops.Distinct(StringComparer.Ordinal).Count(),
            $"{profileName}/{folded.function.name} contains duplicate op selectors.");
    }

    private static JObject SchemaOf(HFWrapper.HFTool tool) =>
        tool.function.parameters as JObject
        ?? JObject.Parse(JsonConvert.SerializeObject(tool.function.parameters));

    private static IEnumerable<string> OpsOf(HFWrapper.HFTool tool) =>
        SchemaOf(tool)["properties"]?["op"]?["enum"]?.Values<string>()
            .Where(value => value != null).Select(value => value!)
        ?? Enumerable.Empty<string>();
}
