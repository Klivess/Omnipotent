using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;
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
            "computer_move", "computer_click", "computer_drag", "computer_mouse_down", "computer_mouse_up", "computer_scroll",
            "computer_type", "computer_key", "computer_key_down", "computer_key_up", "computer_release_all", "computer_wait",
            "computer_open_browser", "computer_navigate", "computer_focus_window", "computer_launch_app",
            "computer_terminal",
            "computer_clipboard_get", "computer_clipboard_set",
        };

        public ComputerCapabilities Capabilities { get; } = new()
        {
            SupportsOcr = true,
            SupportsWindowControl = true,
            SupportsBrowserControl = true,
            SupportsClipboard = true,
            SupportsAppLaunch = true,
            SupportsTerminalExecution = true,
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

            // Container-local shell execution does not read or inject VNC state. Let it remain
            // usable while a live viewer or a degraded framebuffer has a visual action queued.
            bool usesVisualGate = tool != "computer_terminal";
            if (usesVisualGate) await actionGate.WaitAsync(ct);
            try
            {
                if (IsMutating(tool) && inputLock != null && agentID != null && !inputLock.TryAcquire(containerID, agentID))
                    return ContainerToolResult.Fail($"Desktop is currently controlled by agent {inputLock.CurrentHolder(containerID)}. Wait for its action to finish.");

                return await DispatchAsync(tool, a, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (usesVisualGate)
                {
                    await TryReleaseAsync();
                    return ContainerToolResult.Fail("Action cancelled; held input was released.");
                }
                return ContainerToolResult.Fail("Container terminal command cancelled.");
            }
            catch (Exception ex)
            {
                return ContainerToolResult.Fail($"{tool} error: {ex.GetType().Name}: {ex.Message}");
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
                    return ContainerToolResult.Ok($"Desktop '{transport.DesktopName}' is {transport.Width}x{transport.Height}px. VNC connected: {transport.Connected}.");
                case "computer_read_screen":
                    return await ScreenshotAsync("Use the screenshot or computer_find_text to read this desktop.", ct);
                case "computer_find_text": return await FindTextAsync(a, ct, false);
                case "computer_click_text": return await FindTextAsync(a, ct, true);
                case "computer_move":
                    return await MoveAsync(a, ct);
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
                    return await MutateAsync("Browser opened.", () => desktop.LaunchAsync("browser", Str(a, "url"), ct), ct, settleMs: 800);
                case "computer_navigate":
                    return await MutateAsync("Browser navigated.", () => desktop.NavigateAsync(Str(a, "url") ?? throw new ArgumentException("Provide 'url'."), ct), ct, settleMs: 1000);
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
                default: return ContainerToolResult.Fail($"Unsupported container computer tool '{tool}'.");
            }
        }

        private async Task<ContainerToolResult> FindTextAsync(JsonElement a, CancellationToken ct, bool click)
        {
            string needle = Str(a, "text") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(needle)) return ContainerToolResult.Fail("Provide visible 'text' to locate.");
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
                return shot with { Text = $"No visible OCR match for '{ComputerAudit.Truncate(needle, 80)}' at occurrence {occurrence}. " + shot.Text };
            var match = matches[occurrence];
            string text = $"OCR match {occurrence}: '{ComputerAudit.Truncate(match.Text, 120)}' centre=({match.CentreX},{match.CentreY}) confidence={match.Confidence:0}.";
            if (!click) return shot with { Text = text + " " + shot.Text };
            var point = await ResolvePointAsync(match.CentreX, match.CentreY, ct);
            await transport.ClickAsync(point.X, point.Y, ParseButton(Str(a, "button")), Math.Clamp(Int(a, "clicks", 1), 1, 2), ct);
            await Task.Delay(350, ct);
            return await ObserveAfterMutationAsync(text + " Clicked OCR match.", raw.jpeg, ct);
        }

        private async Task<ContainerToolResult> MoveAsync(JsonElement a, CancellationToken ct)
        {
            int x = RequiredInt(a, "x"), y = RequiredInt(a, "y");
            return await MutateAsync($"Moved to ({x},{y}).", async () =>
            {
                var point = await ResolvePointAsync(x, y, ct);
                await transport.MoveMouseAsync(point.X, point.Y, ct);
            }, ct);
        }

        private async Task<ContainerToolResult> ClickAsync(JsonElement a, CancellationToken ct)
        {
            int x = RequiredInt(a, "x"), y = RequiredInt(a, "y");
            int clicks = Math.Clamp(Int(a, "clicks", 1), 1, 2);
            return await WithModifiersAsync(a, () => MutateAsync($"Clicked ({x},{y}).", async () =>
            {
                var point = await ResolvePointAsync(x, y, ct);
                await transport.ClickAsync(point.X, point.Y, ParseButton(Str(a, "button")), clicks, ct);
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
                await transport.DragAsync(from.X, from.Y, to.X, to.Y, ParseButton(Str(a, "button")), ct);
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
                () => transport.ScrollAsync(point.X, point.Y, dy, dx, ct), ct);
        }

        private async Task<ContainerToolResult> TypeAsync(JsonElement a, CancellationToken ct)
        {
            string text = Str(a, "text") ?? string.Empty;
            if (resolveSecretsAsync != null) text = await resolveSecretsAsync(text);
            // Do not place either literal or substituted text in results/events.
            return await MutateAsync("Typed text.", () => transport.TypeTextAsync(text, typingDelayMs, ct), ct);
        }

        private async Task<ContainerToolResult> TerminalAsync(JsonElement a, CancellationToken ct)
        {
            if (terminalAsync == null)
                return ContainerToolResult.Fail("Direct terminal execution is unavailable for this desktop.");
            string command = Str(a, "command") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command))
                return ContainerToolResult.Fail("Provide 'command' - a Bash command to run inside the desktop container.");
            int timeoutSeconds = Math.Clamp(Int(a, "timeoutSeconds", 120), 1, 900);
            var result = await terminalAsync(command, Str(a, "workingDirectory"), timeoutSeconds, ct);
            // Never repeat the command in the result. Vault/account placeholders are deliberately
            // NOT resolved here: arbitrary shell stdout could echo them back to the model. Secrets
            // remain confined to computer_type's one-way keystroke substitution path.
            return new ContainerToolResult(result.Success, result.Format());
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

        private static ContainerToolResult BuildScreenshotResult(string prefix, byte[] jpeg, int width, int height,
            byte[]? beforeJpeg = null)
        {
            byte[] grid = ComputerVision.AddCoordinateGrid(jpeg);
            var frames = new List<ComputerFrame>();
            if (beforeJpeg != null && ComputerVision.FrameDelta(beforeJpeg, jpeg) >= 3)
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
        private static bool IsMutating(string tool) => tool is not ("computer_screenshot" or "computer_find_text" or "computer_window_state" or "computer_read_screen" or "computer_wait" or "computer_clipboard_get" or "computer_terminal");
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
