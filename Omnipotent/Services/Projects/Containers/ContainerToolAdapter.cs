using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Omnipotent.Services.ComputerControl;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Visual computer-control implementation for a Project desktop.  It mirrors the host
    /// controller's observe → act → settle loop, but all input remains inside the VNC-connected
    /// container. Its optional terminal path executes only inside that same isolated container;
    /// no host shell or browser-driver API is exposed.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class ContainerToolAdapter : IComputerController
    {
        private readonly VncTransport transport;
        private readonly HumanizedInput human;
        private readonly ContainerDesktopCommandBridge desktop;
        private readonly InputLockCoordinator? inputLock;
        private readonly SemaphoreSlim actionGate;
        private readonly string containerID;
        private readonly string? agentID;
        private readonly Func<string, Task<string>>? resolveSecretsAsync;
        private readonly Func<string, string?, int, CancellationToken, Task<ContainerShellResult>>? terminalAsync;
        private readonly int actionSettleMs;
        private readonly int typingDelayMs;
        private readonly CachedFrameState frameState;

        // Adapters are short-lived (one is constructed for each tool dispatch), while the VNC
        // transport is pooled per desktop.  Keep the last model-facing raw frame with that pooled
        // transport so a click can reuse the screenshot the agent just observed instead of doing a
        // second blocking full-frame capture before input is even sent.
        private static readonly ConditionalWeakTable<VncTransport, CachedFrameState> FrameStates = new();

        // The humanised-input layer must persist with the pooled transport, not the short-lived
        // adapter: its per-desktop random stream has to advance continuously across actions so the
        // "person" driving this desktop keeps one consistent, non-repeating motion/typing signature.
        private static readonly ConditionalWeakTable<VncTransport, HumanizedInput> Humanizers = new();
        private sealed class CachedFrameState
        {
            public readonly object Gate = new();
            public byte[]? Jpeg;
            public int Width;
            public int Height;
            public DateTime CapturedUtc;
        }

        private static readonly HashSet<string> Tools = new(StringComparer.Ordinal)
        {
            "computer_screenshot", "computer_find_text", "computer_click_text", "computer_window_state", "computer_read_screen",
            "computer_move", "computer_mouse_move_relative", "computer_click", "computer_drag", "computer_mouse_down", "computer_mouse_up", "computer_scroll",
            "computer_type", "computer_key", "computer_key_down", "computer_key_up", "computer_release_all", "computer_wait",
            "computer_open_browser", "computer_navigate", "computer_browser_inspect", "computer_browser_action", "computer_click_browser_control", "computer_focus_window", "computer_launch_app",
            "computer_upload_file",
            "computer_terminal",
            "computer_clipboard_get", "computer_clipboard_set",
        };

        internal static IReadOnlySet<string> SupportedToolNames => Tools;

        /// <summary>
        /// How many browser tabs a desktop may keep. Chromium opens a new tab for every navigation,
        /// so an unmanaged desktop reached 15+ tabs; past that, structured inspection and the visible
        /// window disagreed about which page was live and agents looped on stale pages. Navigation
        /// now reuses the foreground tab, and anything blank, duplicated or cold beyond this cap is
        /// closed automatically.
        /// </summary>
        internal const int MaxBrowserTabs = 6;

        public ComputerCapabilities Capabilities { get; } = new()
        {
            SupportsOcr = true,
            SupportsWindowControl = true,
            SupportsBrowserControl = true,
            SupportsClipboard = true,
            SupportsAppLaunch = true,
            SupportsTerminalExecution = true,
            SupportsRelativeMouse = true,
            SupportsHumanization = true,
            SupportsMotionFrames = true,
            SupportedTools = Tools,
        };

        public enum ContainerToolFailureKind { None, Validation, Semantic, Contention, BrowserInspection, Infrastructure, Cancelled }

        /// <summary>Result kept for the existing Project runner. Jpeg is always the final gridded frame.</summary>
        public sealed record ContainerToolResult(bool Success, string Text, byte[]? Jpeg = null)
        {
            public ContainerToolFailureKind FailureKind { get; init; } = Success
                ? ContainerToolFailureKind.None : ContainerToolFailureKind.Semantic;
            public List<ComputerFrame> Frames { get; init; } = new();
            public int Width { get; init; }
            public int Height { get; init; }
            public static ContainerToolResult Ok(string text, byte[]? jpeg = null) => new(true, text, jpeg);
            public static ContainerToolResult Fail(string text, ContainerToolFailureKind kind = ContainerToolFailureKind.Semantic) =>
                new(false, text) { FailureKind = kind };
        }

        public ContainerToolAdapter(VncTransport transport, string containerID, string? agentID,
            SemaphoreSlim actionGate, InputLockCoordinator? inputLock = null,
            Func<ContainerDesktopControlCommand, string?, CancellationToken, Task>? dockerControlAsync = null,
            Func<string, string?, int, CancellationToken, Task<ContainerShellResult>>? terminalAsync = null,
            Func<string, Task<string>>? resolveSecretsAsync = null,
            int actionSettleMs = 350, int typingDelayMs = 18)
        {
            this.transport = transport;
            this.containerID = containerID;
            this.agentID = agentID;
            this.actionGate = actionGate;
            this.inputLock = inputLock;
            this.terminalAsync = terminalAsync;
            this.resolveSecretsAsync = resolveSecretsAsync;
            this.actionSettleMs = Math.Clamp(actionSettleMs, 50, 5000);
            this.typingDelayMs = Math.Clamp(typingDelayMs, 0, 500);
            frameState = FrameStates.GetValue(transport, _ => new CachedFrameState());
            human = Humanizers.GetValue(transport, _ => new HumanizedInput(transport, HumanInputProfile.ForSeed(containerID)));
            desktop = new ContainerDesktopCommandBridge(transport, dockerControlAsync);
        }

        public async Task<ComputerActionResult> ExecuteComputerActionAsync(ComputerActionRequest request, CancellationToken ct = default)
        {
            var result = await ExecuteAsync(request.ToolName, request.ArgumentsJson, ct);
            return new ComputerActionResult
            {
                Success = result.Success,
                Text = result.Text,
                Error = result.Success ? null : result.Text,
                AuditSummary = ComputerAudit.Describe(request.ToolName, request.ArgumentsJson),
                Observation = result.Jpeg == null ? null : new ComputerObservation
                {
                    FinalFrameJpeg = result.Jpeg,
                    Frames = result.Frames,
                    Width = result.Width,
                    Height = result.Height,
                    IsSettled = true,
                }
            };
        }

        public async Task<ContainerToolResult> ExecuteAsync(string tool, string? argsJson, CancellationToken ct = default)
        {
            if (!Capabilities.Supports(tool)) return ContainerToolResult.Fail($"Unsupported container computer tool '{tool}'.", ContainerToolFailureKind.Validation);
            JsonDocument? doc = null;
            JsonElement a;
            try
            {
                doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson!);
                a = doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement : default;
            }
            catch { doc = JsonDocument.Parse("{}"); a = doc.RootElement; }

            // Container-local shell execution does not read or inject VNC state. Let it remain
            // usable while a live viewer or a degraded framebuffer has a visual action queued.
            bool usesVisualGate = tool is not ("computer_terminal" or "computer_window_state" or "computer_browser_inspect");
            if (usesVisualGate) await actionGate.WaitAsync(ct);
            try
            {
                if (IsMutating(tool) && inputLock != null && agentID != null && !inputLock.TryAcquire(containerID, agentID))
                    return ContainerToolResult.Fail($"Desktop is currently controlled by agent {inputLock.CurrentHolder(containerID)}. Wait for its action to finish.", ContainerToolFailureKind.Contention);

                return await DispatchAsync(tool, a, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (usesVisualGate)
                {
                    await TryReleaseAsync();
                    return ContainerToolResult.Fail("Action cancelled; held input was released.", ContainerToolFailureKind.Cancelled);
                }
                return ContainerToolResult.Fail("Container terminal command cancelled.", ContainerToolFailureKind.Cancelled);
            }
            catch (Exception ex)
            {
                return ContainerToolResult.Fail($"{tool} error: {ex.GetType().Name}: {ex.Message}",
                    ex is ArgumentException ? ContainerToolFailureKind.Validation : ContainerToolFailureKind.Infrastructure);
            }
            finally
            {
                doc?.Dispose();
                if (usesVisualGate) actionGate.Release();
            }
        }

        /// <summary>Called by the wake lifecycle so a cancelled/retired agent cannot leave a
        /// shared desktop leased or a modifier held across a later wake.</summary>
        public async Task ReleaseWakeInputsAsync()
        {
            await TryReleaseAsync();
            if (inputLock != null && agentID != null) inputLock.Release(containerID, agentID);
        }

        private async Task<ContainerToolResult> DispatchAsync(string tool, JsonElement a, CancellationToken ct)
        {
            switch (tool)
            {
                case "computer_screenshot": return await ScreenshotAsync("Captured desktop.", ct);
                case "computer_window_state":
                    return await WindowStateAsync(ct);
                case "computer_read_screen":
                    return await ReadScreenAsync(a, ct);
                case "computer_find_text": return await FindTextAsync(a, ct, false);
                case "computer_click_text": return await FindTextAsync(a, ct, true);
                case "computer_move":
                    return await MoveAsync(a, ct);
                case "computer_mouse_move_relative":
                    return await MoveRelativeAsync(a, ct);
                case "computer_click":
                    return await ClickAsync(a, ct);
                case "computer_drag":
                    return await DragAsync(a, ct);
                case "computer_mouse_down":
                    return await MouseDownAsync(a, ct);
                case "computer_mouse_up":
                    return await MouseUpAsync(a, ct);
                case "computer_scroll":
                    return await ScrollAsync(a, ct);
                case "computer_type":
                    return await TypeAsync(a, ct);
                case "computer_key":
                    return await KeyAsync(a, ct);
                case "computer_key_down":
                    await transport.KeyDownAsync(Str(a, "key") ?? throw new ArgumentException("Provide 'key'."), ct);
                    return ContainerToolResult.Ok($"Holding {Str(a, "key")}.");
                case "computer_key_up":
                    return await MutateAsync($"Released {Str(a, "key")}.", () => transport.KeyUpAsync(Str(a, "key") ?? throw new ArgumentException("Provide 'key'."), ct), ct);
                case "computer_release_all":
                    await ReleaseWakeInputsAsync();
                    return await ScreenshotAsync("Released all held inputs and the desktop lease.", ct);
                case "computer_wait": return await WaitAsync(a, ct);
                case "computer_open_browser":
                    return await OpenBrowserAsync(Str(a, "url"), ct);
                case "computer_navigate":
                    return await NavigateAsync(a, ct);
                case "computer_browser_inspect":
                    return await BrowserInspectAsync(a, ct);
                case "computer_browser_action":
                    return await BrowserActionAsync(a, ct);
                case "computer_click_browser_control":
                    return await ClickBrowserControlAsync(a, ct);
                case "computer_upload_file":
                    return await UploadFileAsync(a, ct);
                case "computer_focus_window":
                    return await MutateAsync("Desktop application focused.", () => desktop.FocusAsync(Str(a, "titleContains"), Str(a, "processName"), ct), ct);
                case "computer_launch_app":
                    return await MutateAsync("Desktop application launched.", () => desktop.LaunchAsync(Str(a, "shellName") ?? Str(a, "path"), Str(a, "args"), ct), ct);
                case "computer_terminal":
                    return await TerminalAsync(a, ct);
                case "computer_clipboard_get":
                    return ContainerToolResult.Ok(transport.GetClipboardText() is { } clip ? $"Clipboard: {clip}" : "Clipboard is unavailable until the desktop publishes a selection.");
                case "computer_clipboard_set":
                    return await MutateAsync("Clipboard set.", () => transport.SetClipboardTextAsync(Str(a, "text") ?? string.Empty, ct), ct);
                default: return ContainerToolResult.Fail($"Unsupported container computer tool '{tool}'.", ContainerToolFailureKind.Validation);
            }
        }

        private async Task<ContainerToolResult> WindowStateAsync(CancellationToken ct)
        {
            string header = $"Desktop '{transport.DesktopName}' is {transport.Width}x{transport.Height}px. VNC connected: {transport.Connected}.";
            if (terminalAsync == null) return ContainerToolResult.Ok(header);

            const string command =
                "export DISPLAY=${DISPLAY:-:1}\n" +
                "active=$(xdotool getactivewindow 2>/dev/null || true)\n" +
                "echo \"active-id=$active\"\n" +
                "wmctrl -lxG 2>/dev/null | awk -v active=\"$active\" '{mark=(tolower($1)==tolower(active)?\"*\":\" \"); print mark \" id=\" $1 \" desktop=\" $2 \" x=\" $3 \" y=\" $4 \" w=\" $5 \" h=\" $6 \" class=\" $7 \" title=\" substr($0,index($0,$8))}' | head -80";
            var state = await terminalAsync(command, "/project", 15, ct);
            string body = state.Stdout.Trim();
            if (body.Length == 0)
                body = state.Success ? "No mapped desktop windows were reported." :
                    "Window enumeration was unavailable: " + ComputerAudit.Truncate(state.Stderr, 500);
            return ContainerToolResult.Ok(ComputerAudit.Truncate(header + "\n" + body, 16000));
        }

        private async Task<ContainerToolResult> ReadScreenAsync(JsonElement a, CancellationToken ct)
        {
            int maxItems = Math.Clamp(Int(a, "maxItems", 120), 1, 300);
            var frame = await CaptureFrameWithRetryAsync(ct);
            var raw = EncodeAndCacheDisplayFrame(frame);
            // Keep the model-facing observation compact, but OCR the lossless framebuffer. Small
            // browser/native-UI glyphs are often destroyed by the quality-70 display JPEG.
            byte[] ocrImage = VncFrameEncoder.EncodePng(frame.bgra, frame.width, frame.height);
            var lines = await ComputerVision.ReadTextAsync(ocrImage, ct);
            var text = new StringBuilder();
            text.Append("OCR screen text, ordered top-to-bottom. Bounds are framebuffer pixels; ")
                .Append("use their centres with coordinate actions. Desktop ")
                .Append(raw.width).Append('x').Append(raw.height).AppendLine(".");
            if (lines.Count == 0)
            {
                text.Append("No ordinary OCR text was detected. OCR status: ")
                    .Append(ComputerVision.OcrStatus)
                    .Append(". The screen may be blank, image-only, or using an unsupported script.");
            }
            else
            {
                int index = 0;
                foreach (var line in lines.Take(maxItems))
                {
                    text.Append('[').Append(index++).Append("] text=")
                        .Append(JsonSerializer.Serialize(ComputerAudit.Truncate(line.Text, 500)))
                        .Append(" bounds={x:").Append(line.X).Append(",y:").Append(line.Y)
                        .Append(",width:").Append(line.Width).Append(",height:").Append(line.Height)
                        .Append("} centre={x:").Append(line.CentreX).Append(",y:").Append(line.CentreY)
                        .Append("} confidence:").Append(Math.Round(line.Confidence, 1)).AppendLine();
                }
                if (lines.Count > maxItems)
                    text.Append("TRUNCATED: ").Append(lines.Count - maxItems)
                        .Append(" more OCR row(s); scroll or target a region, then read again.");
            }

            var observed = BuildScreenshotResult("Screen text read by OCR.", raw.jpeg, raw.width, raw.height);
            return new ContainerToolResult(true, ComputerAudit.Truncate(text.ToString(), 24000), observed.Jpeg)
            {
                Frames = observed.Frames,
                Width = observed.Width,
                Height = observed.Height,
            };
        }

        private async Task<ContainerToolResult> OpenBrowserAsync(string? url, CancellationToken ct)
        {
            var launched = await MutateAsync("Browser launch requested.", () => desktop.LaunchAsync("browser", url, ct), ct, settleMs: 800);
            if (!launched.Success) return launched;
            if (terminalAsync == null)
                return new ContainerToolResult(true,
                    "Browser opened visibly. Structured inspection is unavailable; continue with screenshot, OCR, mouse, and keyboard tools.",
                    launched.Jpeg)
                {
                    Frames = launched.Frames, Width = launched.Width, Height = launched.Height,
                };

            using var inspectRequest = JsonDocument.Parse("{\"mode\":\"tabs\",\"maxItems\":1}");
            var verified = await BrowserInspectAsync(inspectRequest.RootElement, ct);
            if (verified.Success)
                return new ContainerToolResult(true, "Browser opened and verified (visible Chromium has an inspectable tab).", launched.Jpeg)
                {
                    Frames = launched.Frames, Width = launched.Width, Height = launched.Height,
                };

            return new ContainerToolResult(true,
                "Browser opened visibly, but optional structured inspection is unavailable. " +
                "Continue through screenshot, OCR, mouse, and keyboard tools. " +
                ComputerAudit.Truncate(verified.Text, 800), launched.Jpeg)
            {
                Frames = launched.Frames, Width = launched.Width, Height = launched.Height,
            };
        }

        private async Task<ContainerToolResult> BrowserInspectAsync(JsonElement a, CancellationToken ct)
        {
            if (terminalAsync == null) return ContainerToolResult.Fail("Structured browser inspection is unavailable for this desktop. The visible desktop remains usable through screenshot/OCR/mouse/keyboard tools.", ContainerToolFailureKind.BrowserInspection);
            string mode = (Str(a, "mode") ?? "dom").Trim().ToLowerInvariant();
            if (mode is not ("tabs" or "dom" or "controls" or "accessibility" or "network"))
                return ContainerToolResult.Fail("mode must be tabs, dom, controls, accessibility, or network.", ContainerToolFailureKind.Validation);
            int maxItems = Math.Clamp(Int(a, "maxItems", 80), 1, 200);
            // -1 means "whatever tab is actually in front". An explicit 0 used to be the default, so
            // every inspection after a couple of navigations described a stale background page.
            int tabIndex = RequestedTabIndex(a);
            ContainerShellResult? last = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                last = await terminalAsync($"python3 /usr/local/bin/browser-inspect.py {mode} {maxItems} {tabIndex}", "/project", 45, ct);
                string stdout = last.Stdout.Trim();
                if (last.Success && stdout.Length > 0 && stdout is not "null" and not "[]")
                    return ContainerToolResult.Ok(ComputerAudit.Truncate(AnnotateInspection(stdout), 24000));
                if (attempt == 1)
                {
                    // Inspection is a nonvisual entry point and can start the browser when no tab
                    // exists. Do not steal focus from a different agent actively driving a shared
                    // desktop; a healthy existing browser never reaches this branch.
                    if (inputLock != null && agentID != null
                        && inputLock.CurrentHolder(containerID) is { } holder && holder != agentID)
                        return ContainerToolResult.Fail(
                            $"No inspectable browser tab is open, and agent {holder} currently controls the shared desktop. Retry after that action finishes.",
                            ContainerToolFailureKind.Contention);
                    await desktop.LaunchAsync("browser", null, ct);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
            string detail = string.Join("\n", new[] { last?.Stderr, last?.Stdout }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            return ContainerToolResult.Fail("Browser inspection failed after retrying the existing visible browser: " +
                ComputerAudit.Truncate(detail, 1600) + " Continue with visible screenshot/OCR/mouse/keyboard tools.", ContainerToolFailureKind.BrowserInspection);
        }

        /// <summary>Explicit tab index, or -1 for "the tab in front of the human".</summary>
        private static int RequestedTabIndex(JsonElement a) =>
            HasInt(a, "tabIndex") ? Math.Clamp(Int(a, "tabIndex", -1), -1, 200) : -1;

        internal static string AnnotateInspection(string inspectionJson)
        {
            try
            {
                using var document = JsonDocument.Parse(inspectionJson);
                var root = document.RootElement;
                string banner = "";
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("humanChallenge", out var challenge)
                    && challenge.ValueKind == JsonValueKind.Object
                    && challenge.TryGetProperty("detected", out var detected)
                    && detected.ValueKind is JsonValueKind.True)
                    banner += "HUMAN_CHALLENGE_DETECTED: CAPTCHA or human verification is visible. " +
                        "Do not retry automated signup controls; preserve the page and request human verification through the commander.\n";
                if (root.ValueKind == JsonValueKind.Object && NativeDialogBanner(root) is { } dialog) banner += dialog;
                return banner + inspectionJson;
            }
            catch (JsonException) { }
            return inspectionJson;
        }

        /// <summary>
        /// The browser's own GTK dialog is a native X window: no DOM, no accessibility tree, no
        /// browser-control geometry. An agent that clicked an upload button sees a page that has
        /// simply stopped responding, which is what produced the repeated "come click Open for me"
        /// requests. Naming the dialog and the tool that clears it turns a stall into one call.
        /// </summary>
        internal static string? NativeDialogBanner(JsonElement root)
        {
            if (!root.TryGetProperty("nativeDialog", out var dialog) || dialog.ValueKind != JsonValueKind.Object) return null;
            if (!dialog.TryGetProperty("open", out var open) || open.ValueKind != JsonValueKind.True) return null;
            string title = "";
            if (dialog.TryGetProperty("windows", out var windows) && windows.ValueKind == JsonValueKind.Array
                && windows.GetArrayLength() > 0 && windows[0].TryGetProperty("title", out var name)
                && name.ValueKind == JsonValueKind.String)
                title = ComputerAudit.Truncate(name.GetString() ?? "", 80);
            bool fileChooser = dialog.TryGetProperty("fileChooser", out var kind) && kind.ValueKind == JsonValueKind.True;
            return fileChooser
                ? $"NATIVE_FILE_DIALOG_OPEN: the browser's own file chooser ('{title}') is on screen and is blocking the page. " +
                  "Call computer_upload_file with the container path of the file (e.g. path:'/project/render/day24.mp4'); it types the path into this dialog and confirms it for you. " +
                  "Do not hunt for the Open button with OCR, and never ask Klives to click it.\n"
                : $"NATIVE_DIALOG_OPEN: a browser-owned dialog ('{title}') is on screen and is blocking page input. " +
                  "Read it in the screenshot and clear it (its visible buttons, or computer_key with key 'escape') before clicking page controls.\n";
        }

        private static bool IsNativeDialogOpen(string json, bool fileChooserOnly = false)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                var probe = root.TryGetProperty("nativeDialog", out var nested) && nested.ValueKind == JsonValueKind.Object
                    ? nested : root;
                string property = fileChooserOnly ? "fileChooser" : "open";
                return probe.TryGetProperty(property, out var flag) && flag.ValueKind == JsonValueKind.True;
            }
            catch (JsonException) { return false; }
        }

        /// <summary>base64url JSON, so no caller has to quote a URL or a path through a shell.</summary>
        private static string EncodePayload(object payload) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private async Task<(bool Ok, string Stdout, string Error)> RunBrowserHelperAsync(
            string mode, object payload, int timeoutSeconds, CancellationToken ct)
        {
            if (terminalAsync == null) return (false, "", "Structured browser control is unavailable for this desktop.");
            var run = await terminalAsync(
                $"python3 /usr/local/bin/browser-inspect.py {mode} {EncodePayload(payload)}", "/project", timeoutSeconds, ct);
            string stdout = run.Stdout.Trim();
            if (run.Success && stdout.Length > 0) return (true, stdout, "");
            return (false, stdout, ComputerAudit.Truncate(string.Join(" ", new[] { run.Stderr, run.Stdout }
                .Where(x => !string.IsNullOrWhiteSpace(x))).Trim(), 1200));
        }

        private async Task<ContainerToolResult> BrowserActionAsync(JsonElement a, CancellationToken ct)
        {
            if (terminalAsync == null)
                return ContainerToolResult.Fail(
                    "Structured browser actions are unavailable for this desktop. Use OCR, keyboard and mouse tools.",
                    ContainerToolFailureKind.BrowserInspection);

            string op = (Str(a, "op") ?? "").Trim().ToLowerInvariant();
            string[] allowed =
            {
                "click", "fill", "type", "select", "check", "uncheck", "focus", "hover",
                "scroll_into_view", "scroll", "press", "wait", "back", "forward", "reload",
                "activate_tab", "close_tab", "script",
            };
            if (!allowed.Contains(op, StringComparer.Ordinal))
                return ContainerToolResult.Fail(
                    "op must be one of: " + string.Join(", ", allowed) + ".",
                    ContainerToolFailureKind.Validation);

            string[] targetOps =
            {
                "click", "fill", "type", "select", "check", "uncheck", "focus", "hover",
                "scroll_into_view", "press",
            };
            bool hasTarget = new[] { "ref", "name", "text", "role", "tag", "css", "label", "placeholder", "testId" }
                .Any(name => !string.IsNullOrWhiteSpace(Str(a, name)));
            if (targetOps.Contains(op, StringComparer.Ordinal) && !hasTarget)
                return ContainerToolResult.Fail(
                    $"op={op} needs a target: pass ref from inspect mode=controls, or a semantic name/text/role/tag/css/label/placeholder/testId.",
                    ContainerToolFailureKind.Validation);

            string value = Str(a, "value") ?? "";
            var values = a.ValueKind == JsonValueKind.Object
                && a.TryGetProperty("values", out var valuesElement)
                && valuesElement.ValueKind == JsonValueKind.Array
                ? valuesElement.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString() ?? "")
                    .Take(100)
                    .ToList()
                : new List<string>();
            if (resolveSecretsAsync != null && op is "fill" or "type" or "select")
            {
                value = await resolveSecretsAsync(value);
                for (int i = 0; i < values.Count; i++)
                    values[i] = await resolveSecretsAsync(values[i]);
            }

            int timeoutMs = Math.Clamp(Int(a, "timeoutMs", 15_000), 100, 120_000);
            string script = Str(a, "script") ?? "";
            if (op == "script" && string.IsNullOrWhiteSpace(script))
                return ContainerToolResult.Fail("op=script requires 'script'.", ContainerToolFailureKind.Validation);
            if (script.Length > 16_000)
                return ContainerToolResult.Fail("Browser script is limited to 16,000 characters.", ContainerToolFailureKind.Validation);

            await desktop.LaunchAsync("browser", null, ct);
            byte[]? before = RecentFrameJpeg();
            var action = await RunBrowserHelperAsync("action", new
            {
                op,
                @ref = Str(a, "ref") ?? "",
                name = Str(a, "name") ?? "",
                text = Str(a, "text") ?? "",
                role = Str(a, "role") ?? "",
                tag = Str(a, "tag") ?? "",
                css = Str(a, "css") ?? "",
                label = Str(a, "label") ?? "",
                placeholder = Str(a, "placeholder") ?? "",
                testId = Str(a, "testId") ?? "",
                exact = Bool(a, "exact"),
                occurrence = Math.Clamp(Int(a, "occurrence", 0), 0, 1000),
                value,
                values,
                key = Str(a, "key") ?? "",
                button = Str(a, "button") ?? "left",
                clicks = Math.Clamp(Int(a, "clicks", 1), 1, 2),
                repeats = Math.Clamp(Int(a, "repeats", 1), 1, 50),
                direction = Str(a, "direction") ?? "",
                amount = Math.Clamp(Int(a, "amount", 600), 1, 100_000),
                waitFor = Str(a, "waitFor") ?? "",
                condition = Str(a, "condition") ?? "",
                timeoutMs,
                tabIndex = RequestedTabIndex(a),
                frameId = Str(a, "frameId") ?? "",
                script,
            }, Math.Clamp((int)Math.Ceiling(timeoutMs / 1000d) + 15, 20, 150), ct);

            string? helperError = BrowserHelperReportedError(action.Stdout);
            if (!action.Ok || helperError != null)
                return ContainerToolResult.Fail(
                    $"Structured browser op={op} failed: {ComputerAudit.Truncate(helperError ?? action.Error, 1800)} " +
                    "Re-inspect mode=controls/tabs before retrying; do not repeat a stale ref.",
                    ContainerToolFailureKind.Semantic);

            string resultText = ComputerAudit.Truncate(AnnotateInspection(action.Stdout), 24000);
            return await ObserveAfterMutationAsync(
                $"Structured browser op={op} completed. {resultText}", before, ct);
        }

        /// <summary>The helper reports semantic failures as bounded JSON while exiting zero so it
        /// can still return the current URL/tab/dialog postcondition. Do not mistake that transport
        /// success for a successful browser action.</summary>
        internal static string? BrowserHelperReportedError(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("ok", out var ok)
                    || ok.ValueKind != JsonValueKind.False)
                    return null;
                if (!root.TryGetProperty("error", out var error))
                    return "The structured browser helper rejected the action.";
                if (error.ValueKind == JsonValueKind.String)
                    return ComputerAudit.Truncate(error.GetString() ?? "Browser action failed.", 1600);
                if (error.ValueKind != JsonValueKind.Object)
                    return "The structured browser helper rejected the action.";
                string code = error.TryGetProperty("code", out var codeValue)
                    && codeValue.ValueKind == JsonValueKind.String ? codeValue.GetString() ?? "" : "";
                string message = error.TryGetProperty("message", out var messageValue)
                    && messageValue.ValueKind == JsonValueKind.String ? messageValue.GetString() ?? "" : "";
                string detail = string.Join(": ", new[] { code, message }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
                return ComputerAudit.Truncate(
                    detail.Length == 0 ? "The structured browser helper rejected the action." : detail,
                    1600);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Navigate the ONE visible browser session. Chromium's launcher opens a fresh tab for every
        /// URL; reusing the foreground tab (and pruning what has gone cold) is what keeps the tab the
        /// agent inspects and the tab the human sees the same page.
        /// </summary>
        private async Task<ContainerToolResult> NavigateAsync(JsonElement a, CancellationToken ct)
        {
            string url = (Str(a, "url") ?? "").Trim();
            if (url.Length == 0)
                return ContainerToolResult.Fail("Provide 'url' — an absolute http(s) URL.", ContainerToolFailureKind.Validation);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return ContainerToolResult.Fail("computer_navigate requires an absolute http(s) URL.", ContainerToolFailureKind.Validation);

            // Idempotent supervisor first: it is a no-op when Chromium is already healthy, and it
            // starts/repairs the session (and focuses its window) when it is not.
            await desktop.LaunchAsync("browser", null, ct);
            if (terminalAsync == null)
                return await MutateAsync("Browser navigated.", () => desktop.NavigateAsync(uri.AbsoluteUri, ct), ct, settleMs: 1000);

            byte[]? before = RecentFrameJpeg();
            var helper = await RunBrowserHelperAsync("navigate", new
            {
                url = uri.AbsoluteUri,
                newTab = Bool(a, "newTab"),
                tabIndex = RequestedTabIndex(a),
                maxTabs = MaxBrowserTabs,
            }, 120, ct);
            if (!helper.Ok)
                return await MutateAsync(
                    "Browser navigated through the desktop launcher; structured navigation was unavailable (" +
                    ComputerAudit.Truncate(helper.Error, 400) + "). Inspect mode='tabs' before clicking.",
                    () => desktop.NavigateAsync(uri.AbsoluteUri, ct), ct, settleMs: 1000);

            await Task.Delay(actionSettleMs, ct);
            return await ObserveAfterMutationAsync(DescribeNavigation(helper.Stdout, uri.AbsoluteUri), before, ct);
        }

        internal static string DescribeNavigation(string json, string requestedUrl)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                bool reused = root.TryGetProperty("reusedTab", out var r) && r.ValueKind == JsonValueKind.True;
                string landed = root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                    ? u.GetString() ?? requestedUrl : requestedUrl;
                var text = new StringBuilder();
                if (NativeDialogBanner(root) is { } banner) text.Append(banner);
                text.Append(reused ? "Navigated the active browser tab to " : "Opened a new browser tab at ")
                    .Append(ComputerAudit.Truncate(landed, 300)).Append('.');
                if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(t.GetString()))
                    text.Append($" Page title: '{ComputerAudit.Truncate(t.GetString()!, 120)}'.");
                if (root.TryGetProperty("tabIndex", out var index) && index.TryGetInt32(out int tabIndex)
                    && root.TryGetProperty("tabCount", out var count) && count.TryGetInt32(out int tabCount))
                    text.Append($" This is tab {tabIndex} of {tabCount} open; inspection defaults to it.");
                if (root.TryGetProperty("closedTabs", out var closed) && closed.ValueKind == JsonValueKind.Array
                    && closed.GetArrayLength() > 0)
                    text.Append($" Closed {closed.GetArrayLength()} blank/duplicate/cold tab(s) automatically so the browser stays readable.");
                return text.ToString();
            }
            catch (JsonException)
            {
                return $"Navigated to {ComputerAudit.Truncate(requestedUrl, 300)}.";
            }
        }

        /// <summary>
        /// Attach a container file to a website upload. Two routes, tried in the order that keeps
        /// the work visible: if the browser's native GTK chooser is already on screen it is driven
        /// with real keystrokes (its location bar takes an absolute path), otherwise the file is
        /// attached to the page's own file input — including the display:none inputs behind styled
        /// "Upload" buttons, which no click can reach. Neither route needs a human.
        /// </summary>
        private async Task<ContainerToolResult> UploadFileAsync(JsonElement a, CancellationToken ct)
        {
            if (terminalAsync == null)
                return ContainerToolResult.Fail(
                    "computer_upload_file needs this desktop's container helper, which is unavailable. " +
                    "Open the site's file picker, then press ctrl+l in the dialog and type the absolute container path followed by enter.",
                    ContainerToolFailureKind.Infrastructure);

            var paths = new List<string>();
            if (Str(a, "path") is { } single && !string.IsNullOrWhiteSpace(single)) paths.Add(single.Trim());
            if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("paths", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        paths.Add(item.GetString()!.Trim());
            paths = paths.Distinct(StringComparer.Ordinal).ToList();
            if (paths.Count == 0)
                return ContainerToolResult.Fail(
                    "Provide 'path' (or 'paths') — the absolute path INSIDE this desktop container, e.g. /project/render/day24.mp4.",
                    ContainerToolFailureKind.Validation);
            if (paths.Count > 8)
                return ContainerToolResult.Fail("Attach at most 8 files in one call.", ContainerToolFailureKind.Validation);
            foreach (string path in paths)
            {
                if (!path.StartsWith('/'))
                    return ContainerToolResult.Fail(
                        $"'{ComputerAudit.Truncate(path, 120)}' is not a container path. Use the absolute path inside this desktop " +
                        "(the shared workspace is /project), not a Windows host path.", ContainerToolFailureKind.Validation);
                if (path.Length > 1024 || path.Any(char.IsControl))
                    return ContainerToolResult.Fail("A file path was too long or contained control characters.", ContainerToolFailureKind.Validation);
            }

            // One probe answers both questions the routing needs: does the file exist in the
            // container, and is a native chooser already blocking the page?
            var probe = await RunBrowserHelperAsync("dialog", new { paths }, 30, ct);
            if (probe.Ok && MissingUploadPaths(probe.Stdout) is { Count: > 0 } missing)
                return ContainerToolResult.Fail(
                    $"Not found inside the desktop container: {ComputerAudit.Truncate(string.Join(", ", missing), 400)}. " +
                    "Check the path with computer_terminal (ls -l) — files written on the host appear under /project.",
                    ContainerToolFailureKind.Semantic);

            var notes = new List<string>();
            bool dialogOpen = probe.Ok && IsNativeDialogOpen(probe.Stdout);
            if (dialogOpen && paths.Count == 1)
            {
                var driven = await DriveFileChooserAsync(paths[0], ct);
                notes.Add(driven.Note);
                if (driven.Closed)
                {
                    var confirmed = await RunBrowserHelperAsync("upload", new { verifyOnly = true, tabIndex = RequestedTabIndex(a) }, 60, ct);
                    string verified = confirmed.Ok && AttachedFileCount(confirmed.Stdout) > 0
                        ? $" The page's file input now holds {AttachedFileCount(confirmed.Stdout)} file(s)."
                        : " The page did not expose a file input to verify against, so confirm the upload visually in the screenshot.";
                    return await ObserveAfterMutationAsync(
                        string.Join(" ", notes) + verified + " Continue the site's own submit/publish step.",
                        RecentFrameJpeg(), ct);
                }
                notes.Add("Falling back to attaching the file directly to the page's file input.");
            }
            else if (dialogOpen)
            {
                notes.Add("A native file chooser was open; it only accepts one path at a time, so it was dismissed in favour of a direct multi-file attach.");
            }
            if (dialogOpen)
            {
                // A chooser left open keeps the renderer modal and would overwrite whatever the CDP
                // route attaches when it finally returns.
                await transport.KeyChordAsync("escape", ct: ct);
                await Task.Delay(400, ct);
                var after = await RunBrowserHelperAsync("dialog", new { }, 20, ct);
                if (after.Ok && IsNativeDialogOpen(after.Stdout))
                {
                    await transport.KeyChordAsync("escape", ct: ct);
                    await Task.Delay(400, ct);
                }
            }

            var attach = await RunBrowserHelperAsync("upload", new
            {
                paths,
                name = Str(a, "name") ?? Str(a, "inputName") ?? "",
                occurrence = Math.Clamp(Int(a, "occurrence", 0), 0, 50),
                tabIndex = RequestedTabIndex(a),
            }, 180, ct);
            if (!attach.Ok)
                return ContainerToolResult.Fail(
                    string.Join(" ", notes.Append("Could not attach the file: " + ComputerAudit.Truncate(attach.Error, 900))),
                    ContainerToolFailureKind.Semantic);

            int attached = AttachedFileCount(attach.Stdout);
            if (attached == 0)
                return ContainerToolResult.Fail(
                    string.Join(" ", notes.Append(
                        "The file input reported no attached file afterwards. Inspect the page (mode='dom' lists fileInputs) and " +
                        "pass 'name' to target the right input, or click the site's upload control first and call this tool again while the dialog is open.")),
                    ContainerToolFailureKind.Semantic);

            notes.Add($"Attached {attached} file(s) to the page's file input ({ComputerAudit.Truncate(string.Join(", ", paths), 300)}); " +
                      "the page received the same change event as a manual selection. Verify the visible upload state and continue with the site's submit/publish step.");
            return await ObserveAfterMutationAsync(string.Join(" ", notes), RecentFrameJpeg(), ct);
        }

        /// <summary>
        /// Drive Chromium's GTK file chooser from the keyboard. ctrl+l opens its location bar, which
        /// takes an absolute path — deterministic, unlike hunting the Open button with OCR at a
        /// coordinate that moves with the dialog. Delete clears GTK's inline completion first, which
        /// would otherwise submit a neighbouring filename.
        /// </summary>
        private async Task<(bool Closed, string Note)> DriveFileChooserAsync(string path, CancellationToken ct)
        {
            await RunBrowserHelperAsync("dialog", new { activate = true }, 20, ct);
            await Task.Delay(300, ct);
            await transport.KeyChordAsync("ctrl+l", ct: ct);
            await Task.Delay(250, ct);
            await transport.TypeTextAsync(path, typingDelayMs, ct);
            await Task.Delay(250, ct);
            await transport.KeyChordAsync("delete", ct: ct);
            await Task.Delay(150, ct);
            await transport.KeyChordAsync("enter", ct: ct);

            for (int attempt = 1; attempt <= 8; attempt++)
            {
                await Task.Delay(500, ct);
                var state = await RunBrowserHelperAsync("dialog", new { }, 20, ct);
                if (state.Ok && !IsNativeDialogOpen(state.Stdout))
                    return (true, "Typed the path into the visible file chooser's location bar and confirmed it; the dialog closed.");
            }
            return (false, "The visible file chooser did not close after the path was entered.");
        }

        private static List<string> MissingUploadPaths(string dialogJson)
        {
            var missing = new List<string>();
            try
            {
                using var document = JsonDocument.Parse(dialogJson);
                if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                    return missing;
                foreach (var file in files.EnumerateArray())
                    if (file.TryGetProperty("exists", out var exists) && exists.ValueKind == JsonValueKind.False
                        && file.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
                        missing.Add(path.GetString() ?? "");
            }
            catch (JsonException) { }
            return missing;
        }

        private static int AttachedFileCount(string uploadJson)
        {
            try
            {
                using var document = JsonDocument.Parse(uploadJson);
                return document.RootElement.TryGetProperty("attached", out var attached) && attached.TryGetInt32(out int count)
                    ? count : 0;
            }
            catch (JsonException) { return 0; }
        }

        private async Task<ContainerToolResult> ClickBrowserControlAsync(JsonElement a, CancellationToken ct)
        {
            if (terminalAsync == null)
                return ContainerToolResult.Fail("Structured browser control is unavailable for this desktop; use OCR or screenshot coordinates.", ContainerToolFailureKind.BrowserInspection);
            string[] targetFields =
            {
                "ref", "name", "text", "role", "tag", "css", "label", "placeholder", "testId",
            };
            if (!targetFields.Any(field => !string.IsNullOrWhiteSpace(Str(a, field))))
                return ContainerToolResult.Fail(
                    "Provide ref from inspect mode=controls, or at least one semantic target: name, text, role, tag, css, label, placeholder, or testId.",
                    ContainerToolFailureKind.Validation);
            if (targetFields.Any(field => (Str(a, field) ?? "").Length > 8_192))
                return ContainerToolResult.Fail("A browser-control selector is too long.", ContainerToolFailureKind.Validation);

            await desktop.LaunchAsync("browser", null, ct);
            (bool Ok, string Stdout, string Error) located = default;
            string? locateSemanticError = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                located = await RunBrowserHelperAsync("action", new
                {
                    op = "locate",
                    @ref = Str(a, "ref") ?? "",
                    name = Str(a, "name") ?? "",
                    text = Str(a, "text") ?? "",
                    role = Str(a, "role") ?? "",
                    tag = Str(a, "tag") ?? "",
                    css = Str(a, "css") ?? "",
                    label = Str(a, "label") ?? "",
                    placeholder = Str(a, "placeholder") ?? "",
                    testId = Str(a, "testId") ?? "",
                    exact = Bool(a, "exact"),
                    occurrence = Math.Clamp(Int(a, "occurrence", 0), 0, 1_000),
                    tabIndex = RequestedTabIndex(a),
                }, 45, ct);
                // Transport/startup failures are worth retrying. A structured semantic response
                // (including ok:false for a stale/ambiguous target) is authoritative immediately.
                locateSemanticError = BrowserHelperReportedError(located.Stdout);
                if (!string.IsNullOrWhiteSpace(located.Stdout)
                    && (located.Ok || locateSemanticError != null))
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
            }
            if (locateSemanticError != null)
                return ContainerToolResult.Fail(
                    locateSemanticError + " Re-inspect mode=controls and use a fresh ref or a more specific semantic target.",
                    ContainerToolFailureKind.Semantic);
            if (!located.Ok || string.IsNullOrWhiteSpace(located.Stdout))
                return ContainerToolResult.Fail("Could not inspect controls in the visible browser: " +
                    ComputerAudit.Truncate(located.Error, 1600) +
                    " Use browser op=inspect mode=controls, OCR, or screenshot coordinates instead.",
                    ContainerToolFailureKind.BrowserInspection);

            try
            {
                using var document = JsonDocument.Parse(located.Stdout);
                JsonElement root = document.RootElement;
                // A native (GTK) modal takes the browser's input grab: the page is still fully
                // inspectable, so the control matches and the click "succeeds" while nothing at all
                // happens. That mismatch is what turned an upload into a repeat-until-guard loop.
                if (NativeDialogBanner(root) is { } blocked)
                    return ContainerToolResult.Fail(blocked.TrimEnd('\n'), ContainerToolFailureKind.Semantic);
                if (!root.TryGetProperty("control", out var control)
                    || control.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    return ContainerToolResult.Fail(
                        "The structured locator returned no control. Re-inspect mode=controls and use a fresh ref.",
                        ContainerToolFailureKind.Semantic);

                string matchedName = control.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string matchedRole = control.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
                if (control.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True)
                    return ContainerToolResult.Fail($"The matched browser control '{ComputerAudit.Truncate(matchedName, 120)}' ({matchedRole}) is disabled; inspect the form for unmet requirements.", ContainerToolFailureKind.Semantic);

                if (!root.TryGetProperty("geometry", out var geometry)
                    || geometry.ValueKind != JsonValueKind.Object)
                    return ContainerToolResult.Fail(
                        "The matched control has no usable physical screen geometry. Use the framebuffer-independent browser op=click, or scroll it into view and locate it again.",
                        ContainerToolFailureKind.Semantic);
                if (geometry.TryGetProperty("intercepted", out var intercepted)
                    && intercepted.ValueKind == JsonValueKind.True)
                {
                    string blocker = geometry.TryGetProperty("interceptedBy", out var by)
                        ? by.GetRawText() : "another visible element";
                    return ContainerToolResult.Fail(
                        $"CONTROL_INTERCEPTED: '{ComputerAudit.Truncate(matchedName, 120)}' ({matchedRole}) is covered by {ComputerAudit.Truncate(blocker, 500)}. Inspect and dismiss or act on that visible blocker first.",
                        ContainerToolFailureKind.Semantic);
                }
                if (root.TryGetProperty("usableForPhysicalClick", out var usable)
                    && usable.ValueKind == JsonValueKind.False)
                    return ContainerToolResult.Fail(
                        "The matched control is not currently usable for a physical click (hidden, off-screen, disabled, or covered). " +
                        "Use browser op=scroll_into_view and re-inspect, or use browser op=click when physical input is unnecessary.",
                        ContainerToolFailureKind.Semantic);
                if (!geometry.TryGetProperty("x", out var xValue) || !xValue.TryGetInt32(out int x)
                    || !geometry.TryGetProperty("y", out var yValue) || !yValue.TryGetInt32(out int y))
                    return ContainerToolResult.Fail("The matched browser control had no usable screen coordinates.", ContainerToolFailureKind.BrowserInspection);

                int clicks = Math.Clamp(Int(a, "clicks", 1), 1, 2);
                // Click within the control's bounds with a slight bias toward centre rather than the
                // exact centre pixel every time (a superhuman tell), using the reported box size.
                int boundsW = 0, boundsH = 0;
                if (geometry.TryGetProperty("bounds", out var box) && box.ValueKind == JsonValueKind.Object)
                {
                    if (box.TryGetProperty("width", out var bw) && bw.TryGetInt32(out var w)) boundsW = w;
                    if (box.TryGetProperty("height", out var bh) && bh.TryGetInt32(out var h)) boundsH = h;
                }
                (x, y) = human.HumanizeClickPoint(x, y, boundsW, boundsH);
                byte[]? before = RecentFrameJpeg();
                return await WithModifiersAsync(a, async () =>
                {
                    var point = await ResolvePointAsync(x, y, ct);
                    await human.ClickAsync(point.X, point.Y, ParseButton(Str(a, "button")), clicks, ct);
                    await Task.Delay(actionSettleMs, ct);
                    return await ObserveAfterMutationAsync(
                        $"Physically clicked visible browser control '{ComputerAudit.Truncate(matchedName, 120)}' ({matchedRole}) at ({x},{y}). Re-inspect to verify the resulting state.",
                        before, ct);
                }, ct);
            }
            catch (JsonException ex)
            {
                return ContainerToolResult.Fail("Browser-control locator returned malformed data: " +
                    ComputerAudit.Truncate(ex.Message, 300), ContainerToolFailureKind.BrowserInspection);
            }
        }

        private async Task<ContainerToolResult> FindTextAsync(JsonElement a, CancellationToken ct, bool click)
        {
            string needle = Str(a, "text") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(needle)) return ContainerToolResult.Fail("Provide visible 'text' to locate.", ContainerToolFailureKind.Validation);
            var frame = await CaptureFrameWithRetryAsync(ct);
            var raw = EncodeAndCacheDisplayFrame(frame);
            var shot = BuildScreenshotResult("OCR searched the desktop.", raw.jpeg, raw.width, raw.height);
            // OCR the clean framebuffer, never the coordinate grid (its lines/labels both hide real
            // text and manufacture false matches).  Feed a lossless PNG rather than the quality-70
            // display JPEG so small UI glyphs survive to Tesseract.
            byte[] ocrImage = VncFrameEncoder.EncodePng(frame.bgra, frame.width, frame.height);
            var matches = await ComputerVision.FindTextAsync(ocrImage, needle, ct);
            int occurrence = Math.Max(0, Int(a, "occurrence", 0));
            if (matches.Count <= occurrence)
                return shot with
                {
                    Success = false,
                    FailureKind = ContainerToolFailureKind.Semantic,
                    Text = $"No visible OCR match for '{ComputerAudit.Truncate(needle, 80)}' at occurrence {occurrence}. " + shot.Text,
                };
            var match = matches[occurrence];
            string text = $"OCR match {occurrence}: '{ComputerAudit.Truncate(match.Text, 120)}' centre=({match.CentreX},{match.CentreY}) confidence={match.Confidence:0}.";
            if (!click) return shot with { Text = text + " " + shot.Text };
            var point = await ResolvePointAsync(match.CentreX, match.CentreY, ct);
            return await WithModifiersAsync(a, async () =>
            {
                await human.ClickAsync(point.X, point.Y, ParseButton(Str(a, "button")), Math.Clamp(Int(a, "clicks", 1), 1, 2), ct);
                await Task.Delay(350, ct);
                return await ObserveAfterMutationAsync(text + " Clicked OCR match.", raw.jpeg, ct);
            }, ct);
        }

        private async Task<ContainerToolResult> MoveAsync(JsonElement a, CancellationToken ct)
        {
            int x = RequiredInt(a, "x"), y = RequiredInt(a, "y");
            return await MutateAsync($"Moved to ({x},{y}).", async () =>
            {
                var point = await ResolvePointAsync(x, y, ct);
                await human.MoveAsync(point.X, point.Y, ct);
            }, ct);
        }

        private async Task<ContainerToolResult> MoveRelativeAsync(JsonElement a, CancellationToken ct)
        {
            int dx = Int(a, "dx", 0), dy = Int(a, "dy", 0);
            if (dx == 0 && dy == 0) return ContainerToolResult.Ok("Relative pointer delta was zero.");
            int steps = Math.Clamp(Int(a, "steps", Math.Max(1, Math.Max(Math.Abs(dx), Math.Abs(dy)) / 25)), 1, 120);
            return await MutateAsync($"Moved pointer by ({dx},{dy}).",
                () => human.Enabled ? human.MoveRelativeAsync(dx, dy, ct) : transport.MoveMouseRelativeAsync(dx, dy, steps, ct), ct);
        }

        private async Task<ContainerToolResult> ClickAsync(JsonElement a, CancellationToken ct)
        {
            int x = RequiredInt(a, "x"), y = RequiredInt(a, "y");
            int clicks = Math.Clamp(Int(a, "clicks", 1), 1, 2);
            return await WithModifiersAsync(a, () => MutateAsync($"Clicked ({x},{y}).", async () =>
            {
                var point = await ResolvePointAsync(x, y, ct);
                await human.ClickAsync(point.X, point.Y, ParseButton(Str(a, "button")), clicks, ct);
            }, ct), ct);
        }

        private async Task<ContainerToolResult> DragAsync(JsonElement a, CancellationToken ct)
        {
            int fromX = RequiredInt(a, "fromX"), fromY = RequiredInt(a, "fromY");
            int toX = RequiredInt(a, "toX"), toY = RequiredInt(a, "toY");
            return await WithModifiersAsync(a, () => MutateAsync("Dragged.", async () =>
            {
                var from = await ResolvePointAsync(fromX, fromY, ct);
                var to = await ResolvePointAsync(toX, toY, ct);
                await human.DragAsync(from.X, from.Y, to.X, to.Y, ParseButton(Str(a, "button")), ct);
            }, ct), ct);
        }

        private async Task<ContainerToolResult> MouseDownAsync(JsonElement a, CancellationToken ct)
        {
            int x = RequiredInt(a, "x"), y = RequiredInt(a, "y");
            var point = await ResolvePointAsync(x, y, ct);
            await transport.MouseDownAsync(point.X, point.Y, ParseButton(Str(a, "button")), ct);
            return ContainerToolResult.Ok($"Mouse button held down at ({x},{y}).");
        }

        private async Task<ContainerToolResult> MouseUpAsync(JsonElement a, CancellationToken ct)
        {
            int x = RequiredInt(a, "x"), y = RequiredInt(a, "y");
            return await MutateAsync("Mouse button released.", async () =>
            {
                var point = await ResolvePointAsync(x, y, ct);
                await transport.MouseUpAsync(point.X, point.Y, ParseButton(Str(a, "button")), ct);
            }, ct);
        }

        private async Task<ContainerToolResult> ScrollAsync(JsonElement a, CancellationToken ct)
        {
            int amount = (int)Math.Clamp(Math.Abs((long)Int(a, "amount", 5)), 1, 100);
            int dy = 0, dx = 0;
            switch ((Str(a, "direction") ?? "down").Trim().ToLowerInvariant())
            {
                case "up": dy = amount; break;
                case "left": dx = -amount; break;
                case "right": dx = amount; break;
                case "down": dy = -amount; break;
                default: throw new ArgumentException("Scroll direction must be up, down, left, or right.");
            }
            await EnsureCoordinateSpaceAsync(ct);
            var size = CoordinateSpace();
            int x = HasInt(a, "x") ? RequiredInt(a, "x") : Math.Max(0, (size.Width - 1) / 2);
            int y = HasInt(a, "y") ? RequiredInt(a, "y") : Math.Max(0, (size.Height - 1) / 2);
            var point = MapPointToFramebuffer(x, y, size.Width, size.Height, transport.Width, transport.Height);
            return await MutateAsync($"Scrolled {Str(a, "direction") ?? "down"}.",
                () => human.ScrollAsync(point.X, point.Y, dy, dx, ct), ct);
        }

        private async Task<ContainerToolResult> TypeAsync(JsonElement a, CancellationToken ct)
        {
            string text = Str(a, "text") ?? string.Empty;
            if (resolveSecretsAsync != null) text = await resolveSecretsAsync(text);
            // Do not place either literal or substituted text in results/events.
            return await MutateAsync("Typed text.",
                () => human.Enabled ? human.TypeTextAsync(text, ct) : transport.TypeTextAsync(text, typingDelayMs, ct), ct);
        }

        private async Task<ContainerToolResult> TerminalAsync(JsonElement a, CancellationToken ct)
        {
            if (terminalAsync == null)
                return ContainerToolResult.Fail("Direct terminal execution is unavailable for this desktop.", ContainerToolFailureKind.Infrastructure);
            string command = Str(a, "command") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command))
                return ContainerToolResult.Fail("Provide 'command' - a Bash command to run inside the desktop container.", ContainerToolFailureKind.Validation);
            if (UsesSharedPlatformRuntime(command))
                return ContainerToolResult.Fail(
                    "PORTABLE_RUNTIME_REQUIRED: do not execute a Python virtualenv or node_modules launcher from /project; that shared tree crosses Windows and Linux. " +
                    "Keep source and lockfiles in /project, create the environment under $KLIVE_AGENT_RUNTIME (mounted at /agent-runtime), and run it there.",
                    ContainerToolFailureKind.Validation);
            int timeoutSeconds = Math.Clamp(Int(a, "timeoutSeconds", 120), 1, 900);
            var result = await terminalAsync(command, Str(a, "workingDirectory"), timeoutSeconds, ct);
            // Never repeat the command in the result. Vault/account placeholders are deliberately
            // NOT resolved here: arbitrary shell stdout could echo them back to the model. Secrets
            // remain confined to computer_type's one-way keystroke substitution path.
            return new ContainerToolResult(result.Success, result.Format())
            {
                // A user command's non-zero exit is not evidence that the desktop, VNC, or image
                // is broken. Timeouts/transport exceptions are surfaced by the outer adapter.
                FailureKind = result.Success ? ContainerToolFailureKind.None : ContainerToolFailureKind.Semantic,
            };
        }

        internal static bool UsesSharedPlatformRuntime(string command)
        {
            foreach (Match match in Regex.Matches(command, @"[^\s'"";|&=]+"))
            {
                string path = match.Value.Trim('(', ')', '[', ']', '{', '}', ',').Replace('\\', '/');
                bool runtimeExecutable = Regex.IsMatch(path,
                    @"(?i)(?:^|/)\.?venv/(?:bin/(?:python(?:3(?:\.\d+)?)?|pip(?:3(?:\.\d+)?)?)|Scripts/(?:python|pip)(?:\.exe)?)$")
                    || Regex.IsMatch(path, @"(?i)(?:^|/)node_modules/\.bin/[A-Za-z0-9_.+-]+$");
                if (!runtimeExecutable) continue;

                if (path.StartsWith("$KLIVE_AGENT_RUNTIME/", StringComparison.Ordinal)
                    || path.StartsWith("${KLIVE_AGENT_RUNTIME}/", StringComparison.Ordinal)
                    || path.StartsWith("/agent-runtime/", StringComparison.Ordinal)
                    || path.StartsWith("/home/agent/", StringComparison.Ordinal))
                    continue;
                // computer_terminal defaults to /project, so a relative runtime path is shared
                // even when the command omitted the explicit /project prefix.
                return path.StartsWith("/project/", StringComparison.Ordinal) || !path.StartsWith('/');
            }
            return false;
        }

        private async Task<ContainerToolResult> KeyAsync(JsonElement a, CancellationToken ct)
        {
            string chord = Str(a, "key") ?? (a.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array
                ? string.Join("+", keys.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString())) : "");
            if (string.IsNullOrWhiteSpace(chord)) return ContainerToolResult.Fail("Provide 'key' or 'keys'.", ContainerToolFailureKind.Validation);
            int holdMs = Math.Clamp(Int(a, "holdMs", 55), 1, 2000);
            int repeats = Math.Clamp(Int(a, "repeats", 1), 1, 50);
            return await MutateAsync($"Pressed {chord}{(repeats > 1 ? $" {repeats} times" : "")}.",
                () => transport.KeyChordAsync(chord, holdMs, repeats, ct), ct);
        }

        private async Task<ContainerToolResult> WaitAsync(JsonElement a, CancellationToken ct)
        {
            int maxMs = Math.Clamp(Int(a, "maxMs", Int(a, "ms", 1000)), 100, 600000);
            bool untilChange = Bool(a, "untilImageChange");
            string? untilText = Str(a, "untilText");

            // A plain timed wait does not need to poll a 4 MB framebuffer every 400ms.  Sleep once
            // and return one final observation, using the prior cached screenshot as the optional
            // motion frame.
            if (!untilChange && string.IsNullOrWhiteSpace(untilText))
            {
                byte[]? cached = RecentFrameJpeg();
                await Task.Delay(maxMs, ct);
                return await ScreenshotAsync($"Waited {maxMs}ms (time elapsed).", ct, cached);
            }

            var before = await CaptureRawAsync(ct);
            var current = before;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string reason = "time elapsed";
            while (sw.ElapsedMilliseconds < maxMs)
            {
                int remaining = Math.Max(1, maxMs - (int)sw.ElapsedMilliseconds);
                await Task.Delay(Math.Min(400, remaining), ct);
                current = await CaptureRawAsync(ct);
                if (untilChange && ComputerVision.FrameDelta(before.jpeg, current.jpeg) >= 3)
                {
                    reason = "screen changed";
                    break;
                }
                if (!string.IsNullOrWhiteSpace(untilText) && (await ComputerVision.FindTextAsync(current.jpeg, untilText, ct)).Count > 0)
                {
                    reason = $"text appeared: {ComputerAudit.Truncate(untilText, 80)}";
                    break;
                }
            }
            // `current` is already the freshest frame; avoid one redundant capture after the
            // condition was satisfied.
            return BuildScreenshotResult($"Waited {sw.ElapsedMilliseconds}ms ({reason}).",
                current.jpeg, current.width, current.height, before.jpeg);
        }

        private async Task<ContainerToolResult> WithModifiersAsync(JsonElement a, Func<Task<ContainerToolResult>> action, CancellationToken ct)
        {
            var modifiers = a.TryGetProperty("modifiers", out var array) && array.ValueKind == JsonValueKind.Array
                ? array.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
            try
            {
                foreach (var modifier in modifiers) await transport.KeyDownAsync(NormalizeModifier(modifier), ct);
                return await action();
            }
            finally
            {
                for (int i = modifiers.Length - 1; i >= 0; i--)
                    try { await transport.KeyUpAsync(NormalizeModifier(modifiers[i]), CancellationToken.None); } catch { }
            }
        }

        private async Task<ContainerToolResult> MutateAsync(string label, Func<Task> action, CancellationToken ct, int? settleMs = null)
        {
            // The agent normally just observed this cached frame.  Re-capturing it here doubled
            // VNC traffic and, worse, prevented the input from being delivered whenever capture
            // was degraded.  Motion clips can use the recent immutable JPEG at zero I/O cost.
            byte[]? before = RecentFrameJpeg();
            await action();
            await Task.Delay(settleMs ?? actionSettleMs, ct);
            return await ObserveAfterMutationAsync(label, before, ct);
        }

        private async Task<ContainerToolResult> ObserveAfterMutationAsync(string label, byte[]? beforeJpeg, CancellationToken ct)
        {
            // Structured browser/CLI paths intentionally provision without connecting VNC. Their
            // returned DOM/tab state is already the postcondition, so do not turn a healthy action
            // into a slow framebuffer connection attempt merely to add an optional image.
            if (!transport.Connected && beforeJpeg == null)
                return ContainerToolResult.Ok(
                    label + " No framebuffer was attached for this operation; the structured text result is authoritative. " +
                    "Vision-capable agents may call desktop op=screenshot separately when pixels add value.");
            try { return await ScreenshotAsync(label, ct, beforeJpeg); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The action has already been written successfully.  Reporting the entire tool as
                // failed encourages a blind retry (dangerous for double-clicks, submits, etc.).
                return ContainerToolResult.Ok(
                    $"{label} The desktop action completed, but its post-action screenshot was unavailable " +
                    $"({ex.GetType().Name}: {ComputerAudit.Truncate(ex.Message, 160)}). Do not repeat the action blindly; " +
                    "call computer_screenshot to observe the current state.");
            }
        }

        private async Task<ContainerToolResult> ScreenshotAsync(string prefix, CancellationToken ct, byte[]? beforeJpeg = null)
        {
            var raw = await CaptureRawAsync(ct);
            return BuildScreenshotResult(prefix, raw.jpeg, raw.width, raw.height, beforeJpeg);
        }

        /// <summary>
        /// When false, a mutating desktop action returns only its settled "after" frame instead of a
        /// before/after pair. The pair is genuinely useful — it is attached only when the screen actually
        /// changed, which is when the comparison is most informative — but it doubles the image tokens of
        /// every productive action, and images are re-sent on every subsequent turn of the wake. Defaults
        /// to true; turn it off per project once the cached-token figures show what it costs.
        /// </summary>
        public bool BeforeFrameEnabled { get; set; } = true;

        private ContainerToolResult BuildScreenshotResult(string prefix, byte[] jpeg, int width, int height,
            byte[]? beforeJpeg = null)
        {
            byte[] grid = ComputerVision.AddCoordinateGrid(jpeg);
            var frames = new List<ComputerFrame>();
            if (BeforeFrameEnabled && beforeJpeg != null && ComputerVision.FrameDelta(beforeJpeg, jpeg) >= 3)
                frames.Add(new ComputerFrame { Jpeg = beforeJpeg, OffsetMs = 0, IsSettled = false, HasCoordinateGrid = false });
            frames.Add(new ComputerFrame { Jpeg = grid, OffsetMs = frames.Count == 0 ? 0 : 1, IsSettled = true, HasCoordinateGrid = true });
            return new ContainerToolResult(true,
                $"{prefix} Desktop is {width}x{height}px. The final image has a coordinate grid; observe before the next click.", grid)
            { Frames = frames, Width = width, Height = height };
        }

        private async Task<(byte[] jpeg, int width, int height)> CaptureRawAsync(CancellationToken ct)
            => EncodeAndCacheDisplayFrame(await CaptureFrameWithRetryAsync(ct));

        private async Task<(byte[] bgra, int width, int height)> CaptureFrameWithRetryAsync(CancellationToken ct)
        {
            try
            {
                return await transport.CaptureFrameAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // VncTransport drops its connection on receive-loop failure. One immediate retry
                // repairs transient docker-proxy/x11vnc disconnects without masking a real outage.
                return await transport.CaptureFrameAsync(ct);
            }
        }

        private (byte[] jpeg, int width, int height) EncodeAndCacheDisplayFrame((byte[] bgra, int width, int height) frame)
        {
            // Tool coordinates are image pixels.  Preserve the framebuffer dimensions here even
            // if a project provisions a desktop wider than 1280px; only the website stream may
            // downscale independently.
            byte[] jpeg = VncFrameEncoder.EncodeJpeg(frame.bgra, frame.width, frame.height, maxWidth: frame.width);
            lock (frameState.Gate)
            {
                frameState.Jpeg = jpeg;
                frameState.Width = frame.width;
                frameState.Height = frame.height;
                frameState.CapturedUtc = DateTime.UtcNow;
            }
            return (jpeg, frame.width, frame.height);
        }

        private byte[]? RecentFrameJpeg()
        {
            if (!transport.Connected) return null;
            lock (frameState.Gate)
            {
                // Old frames are still valid coordinate metadata, but no longer useful as the
                // "before" side of a motion clip.
                return frameState.Jpeg != null && DateTime.UtcNow - frameState.CapturedUtc <= TimeSpan.FromMinutes(2)
                    ? frameState.Jpeg
                    : null;
            }
        }

        private (int Width, int Height) CoordinateSpace()
        {
            lock (frameState.Gate)
            {
                if (frameState.Width > 0 && frameState.Height > 0)
                    return (frameState.Width, frameState.Height);
            }
            return (transport.Width, transport.Height);
        }

        private async Task EnsureCoordinateSpaceAsync(CancellationToken ct)
        {
            var size = CoordinateSpace();
            if (!transport.Connected || transport.Width <= 0 || transport.Height <= 0 || size.Width <= 0 || size.Height <= 0)
                await transport.ConnectAsync(ct);
        }

        private async Task<(int X, int Y)> ResolvePointAsync(int x, int y, CancellationToken ct)
        {
            await EnsureCoordinateSpaceAsync(ct);
            var shown = CoordinateSpace();
            return MapPointToFramebuffer(x, y, shown.Width, shown.Height, transport.Width, transport.Height);
        }

        /// <summary>
        /// Maps coordinates from the exact image shown to the model into the RFB framebuffer.  The
        /// normal project path is identity-sized, but keeping the transform explicit prevents a
        /// future stream/model downscale from silently moving clicks.  Invalid points are rejected
        /// instead of VncTransport clamping them to an unrelated edge control.
        /// </summary>
        internal static (int X, int Y) MapPointToFramebuffer(int x, int y,
            int shownWidth, int shownHeight, int framebufferWidth, int framebufferHeight)
        {
            if (shownWidth <= 0 || shownHeight <= 0 || framebufferWidth <= 0 || framebufferHeight <= 0)
                throw new InvalidOperationException("Desktop coordinate space is unavailable; take computer_screenshot and retry.");
            if (x < 0 || y < 0 || x >= shownWidth || y >= shownHeight)
                throw new ArgumentException(
                    $"Coordinate ({x},{y}) is outside the last screenshot ({shownWidth}x{shownHeight}; valid x=0..{shownWidth - 1}, y=0..{shownHeight - 1}). " +
                    "Take computer_screenshot and choose a point inside the image.");

            int mappedX = shownWidth == framebufferWidth || shownWidth == 1
                ? Math.Min(x, framebufferWidth - 1)
                : (int)Math.Round(x * (framebufferWidth - 1d) / (shownWidth - 1d));
            int mappedY = shownHeight == framebufferHeight || shownHeight == 1
                ? Math.Min(y, framebufferHeight - 1)
                : (int)Math.Round(y * (framebufferHeight - 1d) / (shownHeight - 1d));
            return (mappedX, mappedY);
        }

        private async Task TryReleaseAsync()
        {
            try { await transport.ReleaseAllAsync(CancellationToken.None); } catch { }
        }

        // This predicate controls the shared *input* lease. Terminal execution is independently
        // bounded inside Docker and neither waits for nor retains VNC input state.
        private static bool IsMutating(string tool) => tool is not (
            "computer_screenshot" or "computer_find_text" or "computer_window_state" or
            "computer_read_screen" or "computer_wait" or "computer_browser_inspect" or
            "computer_clipboard_get" or "computer_terminal");
        private static int ParseButton(string? b) => (b ?? "left").Trim().ToLowerInvariant() switch { "middle" => 2, "right" => 3, _ => 1 };
        private static string NormalizeModifier(string value) => value.Trim().ToLowerInvariant() switch { "control" => "ctrl", "win" => "super", _ => value.Trim().ToLowerInvariant() };
        private static string? Str(JsonElement a, string name) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static bool Bool(JsonElement a, string name) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        private static bool HasInt(JsonElement a, string name) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out _);
        private static int RequiredInt(JsonElement a, string name)
        {
            if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var number)) return number;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var text)) return text;
            }
            throw new ArgumentException($"Provide integer '{name}'.");
        }
        private static int Int(JsonElement a, string name, int fallback = 0)
        {
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v)) return fallback;
            return v.ValueKind switch { JsonValueKind.Number when v.TryGetInt32(out var i) => i, JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s, _ => fallback };
        }
    }
}
