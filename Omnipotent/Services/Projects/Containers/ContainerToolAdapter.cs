using System.Runtime.Versioning;
using System.Text.Json;
using Omnipotent.Services.ComputerControl;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Visual computer-control implementation for a Project desktop.  It mirrors the host
    /// controller's observe → act → settle loop, but all input remains inside the VNC-connected
    /// container.  No generic shell or browser-driver API is exposed.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class ContainerToolAdapter : IComputerController
    {
        private readonly VncTransport transport;
        private readonly ContainerDesktopCommandBridge desktop;
        private readonly InputLockCoordinator? inputLock;
        private readonly SemaphoreSlim actionGate;
        private readonly string containerID;
        private readonly string? agentID;
        private readonly Func<string, Task<string>>? resolveSecretsAsync;
        private readonly int actionSettleMs;
        private readonly int typingDelayMs;

        private static readonly HashSet<string> Tools = new(StringComparer.Ordinal)
        {
            "computer_screenshot", "computer_find_text", "computer_click_text", "computer_window_state", "computer_read_screen",
            "computer_move", "computer_click", "computer_drag", "computer_mouse_down", "computer_mouse_up", "computer_scroll",
            "computer_type", "computer_key", "computer_key_down", "computer_key_up", "computer_release_all", "computer_wait",
            "computer_open_browser", "computer_navigate", "computer_focus_window", "computer_launch_app",
            "computer_clipboard_get", "computer_clipboard_set",
        };

        public ComputerCapabilities Capabilities { get; } = new()
        {
            SupportsOcr = true,
            SupportsWindowControl = true,
            SupportsBrowserControl = true,
            SupportsClipboard = true,
            SupportsAppLaunch = true,
            SupportsHumanization = true,
            SupportsMotionFrames = true,
            SupportedTools = Tools,
        };

        /// <summary>Result kept for the existing Project runner. Jpeg is always the final gridded frame.</summary>
        public sealed record ContainerToolResult(bool Success, string Text, byte[]? Jpeg = null)
        {
            public List<ComputerFrame> Frames { get; init; } = new();
            public int Width { get; init; }
            public int Height { get; init; }
            public static ContainerToolResult Ok(string text, byte[]? jpeg = null) => new(true, text, jpeg);
            public static ContainerToolResult Fail(string text) => new(false, text);
        }

        public ContainerToolAdapter(VncTransport transport, string containerID, string? agentID,
            SemaphoreSlim actionGate, InputLockCoordinator? inputLock = null,
            Func<ContainerDesktopControlCommand, string?, CancellationToken, Task>? dockerControlAsync = null,
            Func<string, Task<string>>? resolveSecretsAsync = null,
            int actionSettleMs = 350, int typingDelayMs = 18)
        {
            this.transport = transport;
            this.containerID = containerID;
            this.agentID = agentID;
            this.actionGate = actionGate;
            this.inputLock = inputLock;
            this.resolveSecretsAsync = resolveSecretsAsync;
            this.actionSettleMs = Math.Clamp(actionSettleMs, 50, 5000);
            this.typingDelayMs = Math.Clamp(typingDelayMs, 0, 500);
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
            if (!Capabilities.Supports(tool)) return ContainerToolResult.Fail($"Unsupported container computer tool '{tool}'.");
            JsonDocument? doc = null;
            JsonElement a;
            try
            {
                doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson!);
                a = doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement : default;
            }
            catch { doc = JsonDocument.Parse("{}"); a = doc.RootElement; }

            await actionGate.WaitAsync(ct);
            try
            {
                if (IsMutating(tool) && inputLock != null && agentID != null && !inputLock.TryAcquire(containerID, agentID))
                    return ContainerToolResult.Fail($"Desktop is currently controlled by agent {inputLock.CurrentHolder(containerID)}. Wait for its action to finish.");

                return await DispatchAsync(tool, a, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await TryReleaseAsync();
                return ContainerToolResult.Fail("Action cancelled; held input was released.");
            }
            catch (Exception ex)
            {
                return ContainerToolResult.Fail($"{tool} error: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                doc?.Dispose();
                actionGate.Release();
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
                    return ContainerToolResult.Ok($"Desktop '{transport.DesktopName}' is {transport.Width}x{transport.Height}px. VNC connected: {transport.Connected}.");
                case "computer_read_screen":
                    return await ScreenshotAsync("Use the screenshot or computer_find_text to read this desktop.", ct);
                case "computer_find_text": return await FindTextAsync(a, ct, false);
                case "computer_click_text": return await FindTextAsync(a, ct, true);
                case "computer_move":
                    return await MutateAsync($"Moved to ({Int(a, "x")},{Int(a, "y")}).", () => transport.MoveMouseAsync(Int(a, "x"), Int(a, "y"), ct), ct);
                case "computer_click":
                    return await ClickAsync(a, ct);
                case "computer_drag":
                    return await WithModifiersAsync(a, () => MutateAsync("Dragged.", () => transport.DragAsync(Int(a, "fromX"), Int(a, "fromY"), Int(a, "toX"), Int(a, "toY"), ParseButton(Str(a, "button")), ct), ct), ct);
                case "computer_mouse_down":
                    await transport.MouseDownAsync(Int(a, "x"), Int(a, "y"), ParseButton(Str(a, "button")), ct);
                    return ContainerToolResult.Ok("Mouse button held down.");
                case "computer_mouse_up":
                    return await MutateAsync("Mouse button released.", () => transport.MouseUpAsync(Int(a, "x"), Int(a, "y"), ParseButton(Str(a, "button")), ct), ct);
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
                    return await MutateAsync("Browser opened.", () => desktop.LaunchAsync("browser", Str(a, "url"), ct), ct, settleMs: 800);
                case "computer_navigate":
                    return await MutateAsync("Browser navigated.", () => desktop.NavigateAsync(Str(a, "url") ?? throw new ArgumentException("Provide 'url'."), ct), ct, settleMs: 1000);
                case "computer_focus_window":
                    return await MutateAsync("Desktop application focused.", () => desktop.FocusAsync(Str(a, "titleContains"), Str(a, "processName"), ct), ct);
                case "computer_launch_app":
                    return await MutateAsync("Desktop application launched.", () => desktop.LaunchAsync(Str(a, "shellName") ?? Str(a, "path"), Str(a, "args"), ct), ct);
                case "computer_clipboard_get":
                    return ContainerToolResult.Ok(transport.GetClipboardText() is { } clip ? $"Clipboard: {clip}" : "Clipboard is unavailable until the desktop publishes a selection.");
                case "computer_clipboard_set":
                    return await MutateAsync("Clipboard set.", () => transport.SetClipboardTextAsync(Str(a, "text") ?? string.Empty, ct), ct);
                default: return ContainerToolResult.Fail($"Unsupported container computer tool '{tool}'.");
            }
        }

        private async Task<ContainerToolResult> FindTextAsync(JsonElement a, CancellationToken ct, bool click)
        {
            string needle = Str(a, "text") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(needle)) return ContainerToolResult.Fail("Provide visible 'text' to locate.");
            var shot = await ScreenshotAsync("OCR searched the desktop.", ct);
            if (shot.Jpeg == null) return shot;
            var matches = await ComputerVision.FindTextAsync(shot.Jpeg, needle, ct);
            int occurrence = Math.Max(0, Int(a, "occurrence", 0));
            if (matches.Count <= occurrence)
                return shot with { Text = $"No visible OCR match for '{ComputerAudit.Truncate(needle, 80)}' at occurrence {occurrence}. " + shot.Text };
            var match = matches[occurrence];
            string text = $"OCR match {occurrence}: '{ComputerAudit.Truncate(match.Text, 120)}' centre=({match.CentreX},{match.CentreY}) confidence={match.Confidence:0}.";
            if (!click) return shot with { Text = text + " " + shot.Text };
            await transport.ClickAsync(match.CentreX, match.CentreY, ParseButton(Str(a, "button")), Math.Max(1, Int(a, "clicks", 1)), ct);
            await Task.Delay(350, ct);
            return await ScreenshotAsync(text + " Clicked OCR match.", ct, shot.Jpeg);
        }

        private async Task<ContainerToolResult> ClickAsync(JsonElement a, CancellationToken ct)
        {
            return await WithModifiersAsync(a, () => MutateAsync($"Clicked ({Int(a, "x")},{Int(a, "y")}).",
                () => transport.ClickAsync(Int(a, "x"), Int(a, "y"), ParseButton(Str(a, "button")), Math.Max(1, Int(a, "clicks", 1)), ct), ct), ct);
        }

        private async Task<ContainerToolResult> ScrollAsync(JsonElement a, CancellationToken ct)
        {
            int amount = Math.Max(1, Math.Abs(Int(a, "amount", 5)));
            int dy = 0, dx = 0;
            switch ((Str(a, "direction") ?? "down").Trim().ToLowerInvariant())
            {
                case "up": dy = amount; break;
                case "left": dx = -amount; break;
                case "right": dx = amount; break;
                default: dy = -amount; break;
            }
            return await MutateAsync($"Scrolled {Str(a, "direction") ?? "down"}.",
                () => transport.ScrollAsync(Int(a, "x", Math.Max(0, transport.Width / 2)), Int(a, "y", Math.Max(0, transport.Height / 2)), dy, dx, ct), ct);
        }

        private async Task<ContainerToolResult> TypeAsync(JsonElement a, CancellationToken ct)
        {
            string text = Str(a, "text") ?? string.Empty;
            if (resolveSecretsAsync != null) text = await resolveSecretsAsync(text);
            // Do not place either literal or substituted text in results/events.
            return await MutateAsync("Typed text.", () => transport.TypeTextAsync(text, typingDelayMs, ct), ct);
        }

        private async Task<ContainerToolResult> KeyAsync(JsonElement a, CancellationToken ct)
        {
            string chord = Str(a, "key") ?? (a.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array
                ? string.Join("+", keys.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString())) : "");
            if (string.IsNullOrWhiteSpace(chord)) return ContainerToolResult.Fail("Provide 'key' or 'keys'.");
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
            var before = await CaptureRawAsync(ct);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string reason = "time elapsed";
            while (sw.ElapsedMilliseconds < maxMs)
            {
                await Task.Delay(400, ct);
                var current = await CaptureRawAsync(ct);
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
            var result = await ScreenshotAsync($"Waited {sw.ElapsedMilliseconds}ms ({reason}).", ct, before.jpeg);
            return result;
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
            var before = await CaptureRawAsync(ct);
            await action();
            await Task.Delay(settleMs ?? actionSettleMs, ct);
            return await ScreenshotAsync(label, ct, before.jpeg);
        }

        private async Task<ContainerToolResult> ScreenshotAsync(string prefix, CancellationToken ct, byte[]? beforeJpeg = null)
        {
            var raw = await CaptureRawAsync(ct);
            byte[] grid = ComputerVision.AddCoordinateGrid(raw.jpeg);
            var frames = new List<ComputerFrame>();
            if (beforeJpeg != null && ComputerVision.FrameDelta(beforeJpeg, raw.jpeg) >= 3)
                frames.Add(new ComputerFrame { Jpeg = beforeJpeg, OffsetMs = 0, IsSettled = false, HasCoordinateGrid = false });
            frames.Add(new ComputerFrame { Jpeg = grid, OffsetMs = frames.Count == 0 ? 0 : 1, IsSettled = true, HasCoordinateGrid = true });
            return new ContainerToolResult(true,
                $"{prefix} Desktop is {raw.width}x{raw.height}px. The final image has a coordinate grid; observe before the next click.", grid)
            { Frames = frames, Width = raw.width, Height = raw.height };
        }

        private async Task<(byte[] jpeg, int width, int height)> CaptureRawAsync(CancellationToken ct)
        {
            try
            {
                var (bgra, width, height) = await transport.CaptureFrameAsync(ct);
                return (VncFrameEncoder.EncodeJpeg(bgra, width, height), width, height);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // VncTransport drops its connection on receive-loop failure. One immediate retry
                // repairs transient docker-proxy/x11vnc disconnects without masking a real outage.
                var (bgra, width, height) = await transport.CaptureFrameAsync(ct);
                return (VncFrameEncoder.EncodeJpeg(bgra, width, height), width, height);
            }
        }

        private async Task TryReleaseAsync()
        {
            try { await transport.ReleaseAllAsync(CancellationToken.None); } catch { }
        }

        private static bool IsMutating(string tool) => tool is not ("computer_screenshot" or "computer_find_text" or "computer_window_state" or "computer_read_screen" or "computer_wait" or "computer_clipboard_get");
        private static int ParseButton(string? b) => (b ?? "left").Trim().ToLowerInvariant() switch { "middle" => 2, "right" => 3, _ => 1 };
        private static string NormalizeModifier(string value) => value.Trim().ToLowerInvariant() switch { "control" => "ctrl", "win" => "super", _ => value.Trim().ToLowerInvariant() };
        private static string? Str(JsonElement a, string name) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static bool Bool(JsonElement a, string name) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        private static int Int(JsonElement a, string name, int fallback = 0)
        {
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v)) return fallback;
            return v.ValueKind switch { JsonValueKind.Number when v.TryGetInt32(out var i) => i, JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s, _ => fallback };
        }
    }
}
