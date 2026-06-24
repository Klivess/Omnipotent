using System.Collections.Specialized;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Profiles;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAgent.Models;
using Omnipotent.Services.Notifications;
using Omnipotent.Threading;

namespace Omnipotent.Services.HostControl
{
    /// <summary>
    /// Owns ALL host (desktop) interaction for KliveAgent's computer-use tools: perception (screen
    /// capture), actuation (SendInput mouse/keyboard, window focus/launch), the global exclusive input
    /// lock, the encrypted-credential vault + secret substitution, and the human approval gate. The single
    /// chokepoint for safety, gating, and audit. Windows-only by construction.
    ///
    /// The web is driven the SAME way a human does it: the agent screenshots the real system browser and
    /// clicks/types/scrolls — NO Selenium, NO scripted browser API. It is invoked ONLY as native LLM tools
    /// (KliveAgentBrain → ExecuteToolAsync). No hard wall-clock timeouts: slow/blocked actions heartbeat
    /// through onProgress so the stall watchdog never aborts legitimate work.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class HostControlManager : OmniService
    {
        private readonly ScreenCapturer capturer = new();
        private EncryptedMemoryStore secrets = null!;
        private SecretSubstituter substituter = null!;
        private ApprovalBroker approvals = null!;

        // Input is a process-global, exclusive resource: only one mutating OS-input action at a time.
        private readonly SemaphoreSlim inputLock = new(1, 1);

        // Coordinate frame of the most recent screenshot shown to the model, so its image-space click
        // coordinates map back to physical screen pixels.
        private readonly object frameLock = new();
        private CoordFrame? lastFrame;
        private byte[]? lastFrameJpeg;
        private readonly record struct CoordFrame(double Scale, int OriginX, int OriginY, int ShownW, int ShownH);

        public ApprovalBroker Approvals => approvals;
        public EncryptedMemoryStore Secrets => secrets;

        public HostControlManager()
        {
            name = "HostControlManager";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            try
            {
                NativeInput.TryMarkProcessDpiAware();
                secrets = new EncryptedMemoryStore(this);
                await secrets.InitializeAsync();
                substituter = new SecretSubstituter(secrets);
                approvals = new ApprovalBroker(this);
                osControlEnabled = await GetBoolOmniSetting("KliveAgent_OsControlEnabled", defaultValue: true);
                await RegisterScreenStreamRouteAsync();
                await ServiceLog($"[HostControl] Ready. OS-level control {(osControlEnabled ? "ENABLED" : "disabled via KliveAgent_OsControlEnabled")}. Interactive session: {Environment.UserInteractive}, server: {OmniPaths.CheckIfOnServer()}.");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "[HostControl] Initialization failed.");
            }
        }

        // ── Capability / environment ──
        // OS-level control is ON by default — KliveAgent drives whatever machine it runs on. It is NOT
        // gated on CheckIfOnServer() ("server" doesn't imply headless) nor on Environment.UserInteractive;
        // the only switch is the KliveAgent_OsControlEnabled setting (default true), refreshed before every
        // action in ExecuteToolAsync.
        private volatile bool osControlEnabled = true;
        private bool OsControlAvailable => osControlEnabled;

        // ── OmniSettings-backed secret storage helpers (used by EncryptedMemoryStore) ──
        internal async Task SetSettingRaw(string key, string value) =>
            await ExecuteServiceMethod<OmniGlobalSettingsManager>("SetStringOmniSetting", key, value, this.serviceID, this.name);

        internal async Task<string> GetSettingRaw(string key) =>
            await GetStringOmniSetting(key, defaultValue: "", sensitive: true);

        // ── Discord approval channel (reuses the existing blocking tracked-prompt mechanism) ──
        internal async Task<bool> SendDiscordApprovalAsync(string approvalId, string message, CancellationToken ct)
        {
            try
            {
                var reply = await ExecuteServiceMethod<NotificationsService>(
                    "SendTextPromptToKlivesDiscordTracked",
                    approvalId,
                    "KliveAgent needs your approval",
                    message + "\n\nReply 'approve' to proceed; anything else denies.",
                    TimeSpan.FromDays(7),
                    "Approve this action?",
                    "approve / deny");
                var s = (reply as string ?? string.Empty).Trim().ToLowerInvariant();
                return s.StartsWith("approve") || s == "yes" || s == "y" || s == "ok" || s == "confirm" || s == "allow";
            }
            catch { return false; }
        }

        internal async Task CancelDiscordApprovalAsync(string approvalId)
        {
            try { await ExecuteServiceMethod<NotificationsService>("CancelTrackedTextPrompt", approvalId, "Resolved on the website."); }
            catch { }
        }

        // ── Live screen video stream (continuous, independent of the agent's discrete actions) ──
        // A KliveAPI WebSocket route that, per connected viewer, captures the whole desktop at a configurable
        // frame rate and pushes raw JPEG frames. The website's LiveScreen renders these as a live video. The
        // capture loop is read-only (no input lock), so the video keeps flowing while the agent acts.
        private async Task RegisterScreenStreamRouteAsync()
        {
            try
            {
                await ExecuteServiceMethod<Omnipotent.Services.KliveAPI.KliveAPI>("CreateWebSocketRoute",
                    "/kliveagent/screen/stream",
                    (Func<HttpListenerContext, WebSocket, NameValueCollection, KMProfileManager.KMProfile?, Task>)(async (context, socket, queryParams, user) =>
                    {
                        // Browsers can't set an Authorization header on a WebSocket, so this is registered as
                        // Anybody and authorized here from the ?authorization= query param (Klives only).
                        var resolved = user;
                        if (resolved == null)
                        {
                            var pw = queryParams["authorization"];
                            if (!string.IsNullOrEmpty(pw))
                                resolved = await ExecuteServiceMethod<KMProfileManager>("GetProfileByPassword", pw) as KMProfileManager.KMProfile;
                        }
                        if (resolved == null || resolved.KlivesManagementRank < KMProfileManager.KMPermissions.Klives)
                        {
                            try { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None); } catch { }
                            return;
                        }
                        await StreamScreenAsync(socket);
                    }),
                    KMProfileManager.KMPermissions.Anybody);
                await ServiceLog("[HostControl] Screen-stream WebSocket route registered (/kliveagent/screen/stream).");
            }
            catch (Exception ex) { await ServiceLogError(ex, "[HostControl] Failed to register screen-stream route (non-fatal)."); }
        }

        private async Task StreamScreenAsync(WebSocket socket)
        {
            if (!await ComputerUseEnabledAsync())
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "computer-use disabled", CancellationToken.None); } catch { }
                return;
            }

            int fps = Math.Clamp(await GetIntOmniSetting("KliveAgent_StreamFps", 6), 1, 30);
            int width = Math.Clamp(await GetIntOmniSetting("KliveAgent_StreamWidth", 1366), 320, 3840);
            int quality = Math.Clamp(await GetIntOmniSetting("KliveAgent_StreamQuality", 45), 10, 92);
            int delayMs = Math.Max(25, 1000 / fps);

            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    byte[] jpeg;
                    try { jpeg = capturer.CaptureVirtualScreen(width, quality).Jpeg; }
                    catch { await Task.Delay(delayMs); continue; }
                    await socket.SendAsync(new ArraySegment<byte>(jpeg), WebSocketMessageType.Binary, true, CancellationToken.None);
                    await Task.Delay(delayMs);
                }
            }
            catch { /* viewer disconnected or send failed → end the loop */ }
            finally
            {
                try { if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            }
        }

        // ── Public entry points ──

        /// <summary>Brain entry point: run a computer_* tool, returning text + frames for the model/website.</summary>
        public async Task<ComputerToolResult> ExecuteToolAsync(string toolName, string? argsJson, CancellationToken ct, Action<HostControlProgress>? onProgress = null)
        {
            onProgress ??= _ => { };
            // Always resolve to an Object element so TryGetProperty is safe everywhere (an Undefined
            // element throws). The JsonDocument is intentionally left undisposed so the element stays valid.
            JsonElement args;
            try
            {
                var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson!);
                args = doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement : JsonDocument.Parse("{}").RootElement;
            }
            catch { args = JsonDocument.Parse("{}").RootElement; }

            try
            {
                if (!await ComputerUseEnabledAsync())
                    return ComputerToolResult.Fail("Computer-use is disabled (set KliveAgent_ComputerUseEnabled).");
                // Refresh the OS-control switch each action so toggling the setting takes effect immediately.
                osControlEnabled = await GetBoolOmniSetting("KliveAgent_OsControlEnabled", defaultValue: true);
                return await RunAsync(toolName, args, ct, onProgress);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return ComputerToolResult.Fail("Action cancelled (run stopped).");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, $"[HostControl] {toolName} failed.");
                return ComputerToolResult.Fail($"{toolName} error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>ScriptGlobals entry point: run a tool and return only its text observation (no image plumbing).</summary>
        public async Task<string> ExecuteToolTextAsync(string toolName, string? argsJson, CancellationToken ct)
        {
            var r = await ExecuteToolAsync(toolName, argsJson, ct, null);
            return r.Text;
        }

        public async Task<bool> ComputerUseEnabledAsync() => await GetBoolOmniSetting("KliveAgent_ComputerUseEnabled", defaultValue: true);
        private async Task<bool> DryRunAsync() => await GetBoolOmniSetting("KliveAgent_ComputerUseDryRun", defaultValue: false);

        // ── Encrypted memory (vault) API surface ──
        public Task SaveEncryptedMemoryAsync(string name, string value) => secrets.SaveAsync(name, value);
        public List<string> ListEncryptedMemoryNames() => secrets.ListNames();
        public Task<bool> DeleteEncryptedMemoryAsync(string name) => secrets.DeleteAsync(name);

        // ── The action dispatcher ──
        private async Task<ComputerToolResult> RunAsync(string tool, JsonElement a, CancellationToken ct, Action<HostControlProgress> onProgress)
        {
            switch (tool)
            {
                case "computer_screenshot":
                    return CaptureFrameResult(Str(a, "target") ?? "active", "screenshot");

                case "computer_window_state":
                    return WindowState();

                case "computer_read_screen":
                    return await ReadScreenAsync();

                case "computer_move":
                    return await MutatingAsync(ct, onProgress, "move", () =>
                    {
                        var (px, py) = MapToPhysical(IntOr(a, "x", 0), IntOr(a, "y", 0));
                        NativeInput.MoveMouse(px, py);
                    }, label: $"move ({IntOr(a, "x", 0)},{IntOr(a, "y", 0)})", tx: IntOr(a, "x", 0), ty: IntOr(a, "y", 0));

                case "computer_click":
                    return await MutatingAsync(ct, onProgress, "click", () =>
                    {
                        var (px, py) = MapToPhysical(IntOr(a, "x", 0), IntOr(a, "y", 0));
                        NativeInput.Click(px, py, ParseButton(Str(a, "button")), Math.Max(1, IntOr(a, "clicks", 1)));
                    }, label: $"click ({IntOr(a, "x", 0)},{IntOr(a, "y", 0)})", tx: IntOr(a, "x", 0), ty: IntOr(a, "y", 0));

                case "computer_drag":
                    return await MutatingAsync(ct, onProgress, "drag", () =>
                    {
                        var (fx, fy) = MapToPhysical(IntOr(a, "fromX", 0), IntOr(a, "fromY", 0));
                        var (tx2, ty2) = MapToPhysical(IntOr(a, "toX", 0), IntOr(a, "toY", 0));
                        NativeInput.Drag(fx, fy, tx2, ty2);
                    }, label: "drag", tx: IntOr(a, "toX", 0), ty: IntOr(a, "toY", 0));

                case "computer_scroll":
                    return await MutatingAsync(ct, onProgress, "scroll", () =>
                    {
                        int cx = a.TryGetProperty("x", out _) ? IntOr(a, "x", 0) : ScreenCenterX();
                        int cy = a.TryGetProperty("y", out _) ? IntOr(a, "y", 0) : ScreenCenterY();
                        var (px, py) = MapToPhysical(cx, cy);
                        NativeInput.Scroll(px, py, IntOr(a, "dy", 0), IntOr(a, "dx", 0));
                    }, label: $"scroll dy={IntOr(a, "dy", 0)}");

                case "computer_type":
                    return await TypeAsync(a, ct, onProgress);

                case "computer_key":
                    return await KeyAsync(a, ct, onProgress);

                case "computer_wait":
                    return await WaitAsync(a, ct, onProgress);

                case "computer_focus_window":
                    return await FocusWindowAsync(a, ct, onProgress);

                case "computer_launch_app":
                    return await LaunchAppAsync(a);

                case "computer_clipboard_get":
                    return OsGuard() ?? ComputerToolResult.Ok($"Clipboard: {NativeInput.GetClipboardText()}");

                case "computer_clipboard_set":
                    return OsGuard() ?? (NativeInput.SetClipboardText(Str(a, "text") ?? string.Empty)
                        ? ComputerToolResult.Ok("Clipboard set.")
                        : ComputerToolResult.Fail("Could not set clipboard."));

                case "computer_open_browser":
                    return await OpenBrowserAsync(a);

                case "computer_navigate":
                    return await NavigateAsync(a, ct, onProgress);

                case "computer_confirm_action":
                    return await ConfirmActionAsync(a, ct, onProgress);

                case "computer_confirm_and_click":
                    return await ConfirmAndClickAsync(a, ct, onProgress);

                case "save_encrypted_memory":
                {
                    var n = Str(a, "name"); var v = Str(a, "value");
                    if (string.IsNullOrWhiteSpace(n) || v == null) return ComputerToolResult.Fail("Provide 'name' and 'value'.");
                    await secrets.SaveAsync(n, v);
                    await ServiceLog($"[HostControl] saved encrypted memory '{n}'.");
                    return ComputerToolResult.Ok($"Saved encrypted memory '{n}'. Reference it as {{{n}}} inside computer_type text; its value is never shown to you.");
                }
                case "list_encrypted_memories":
                {
                    var names = secrets.ListNames();
                    return ComputerToolResult.Ok(names.Count == 0 ? "No encrypted memories stored." : "Encrypted memories: " + string.Join(", ", names));
                }
                case "delete_encrypted_memory":
                {
                    var n = Str(a, "name");
                    if (string.IsNullOrWhiteSpace(n)) return ComputerToolResult.Fail("Provide 'name'.");
                    return await secrets.DeleteAsync(n) ? ComputerToolResult.Ok($"Deleted encrypted memory '{n}'.") : ComputerToolResult.Fail($"No encrypted memory named '{n}'.");
                }

                default:
                    return ComputerToolResult.Fail($"Unknown computer tool '{tool}'.");
            }
        }

        // ── Perception ──
        private ComputerToolResult CaptureFrameResult(string target, string label, int? tx = null, int? ty = null)
        {
            var guard = OsGuard();
            if (guard != null) return guard;

            ScreenCapturer.CaptureResult cap = target == "fullscreen"
                ? capturer.CaptureVirtualScreen()
                : capturer.CaptureActiveWindow();

            lock (frameLock)
            {
                lastFrame = new CoordFrame(cap.Scale, cap.OriginX, cap.OriginY, cap.ShownWidth, cap.ShownHeight);
                lastFrameJpeg = cap.Jpeg;
            }

            // The model image carries a labeled coordinate-ruler grid so the model can MEASURE the exact
            // pixel to click. The website fallback frame keeps the cleaner action annotation.
            var modelImage = capturer.WithGrid(cap.Jpeg);
            var annotated = capturer.Annotate(cap.Jpeg, label, tx, ty);
            return new ComputerToolResult
            {
                Success = true,
                Text = $"Captured {cap.Description} ({cap.ShownWidth}x{cap.ShownHeight} px shown). The image has a coordinate-ruler grid — read the gridlines to measure the exact x,y you pass to computer_click/move. (0,0 = top-left.)",
                ModelImageJpeg = modelImage,
                AnnotatedJpeg = annotated
            };
        }

        private ComputerToolResult WindowState()
        {
            var sb = new StringBuilder();
            var (vx, vy, vw, vh) = NativeInput.GetVirtualScreenBounds();
            sb.AppendLine($"Virtual screen: origin=({vx},{vy}) size={vw}x{vh}.");
            var fg = NativeInput.GetForegroundWindowInfo();
            if (fg != null)
                sb.AppendLine($"Active window: \"{fg.Value.Title}\" (pid {fg.Value.ProcessId}) at ({fg.Value.Left},{fg.Value.Top}) {fg.Value.Width}x{fg.Value.Height}.");
            sb.AppendLine($"OS-level control: {(OsControlAvailable ? "available" : "DISABLED")}.");
            return ComputerToolResult.Ok(sb.ToString().TrimEnd());
        }

        private Task<ComputerToolResult> ReadScreenAsync()
        {
            var sb = new StringBuilder();
            var fg = NativeInput.GetForegroundWindowInfo();
            if (fg != null) sb.AppendLine($"Active window: \"{fg.Value.Title}\" {fg.Value.Width}x{fg.Value.Height}.");
            sb.AppendLine("Visible windows:");
            foreach (var w in NativeInput.EnumerateVisibleWindows().Take(25))
                sb.AppendLine($"  \"{w.Title}\" (pid {w.ProcessId}) {w.Width}x{w.Height}");
            sb.AppendLine("\n(To read page/app content, call computer_screenshot — the image has a pixel grid you read visually.)");
            return Task.FromResult(ComputerToolResult.Ok(sb.ToString().TrimEnd()));
        }

        // ── Actuation helpers ──
        private ComputerToolResult? OsGuard() =>
            OsControlAvailable ? null : ComputerToolResult.Fail("OS-level control is turned off. Set KliveAgent_OsControlEnabled=true to let KliveAgent control this machine.");

        private async Task<ComputerToolResult> MutatingAsync(CancellationToken ct, Action<HostControlProgress> onProgress, string kind, Action act, string label, int? tx = null, int? ty = null)
        {
            var guard = OsGuard();
            if (guard != null) return guard;

            await inputLock.WaitAsync(ct);
            try
            {
                act();
                await Task.Delay(350, ct); // let the UI settle before we observe the result
            }
            finally { inputLock.Release(); }

            onProgress(new HostControlProgress { Note = label, Activity = new AgentActivityEvent { Kind = "action", Text = label } });
            var result = CaptureFrameResult("active", label, tx, ty);
            result.Text = $"Did: {label}. " + result.Text;
            return result;
        }

        private async Task<ComputerToolResult> TypeAsync(JsonElement a, CancellationToken ct, Action<HostControlProgress> onProgress)
        {
            var guard = OsGuard();
            if (guard != null) return guard;

            var raw = Str(a, "text") ?? string.Empty;
            var (resolved, used) = await substituter.ResolveAsync(raw);
            var redacted = substituter.Redact(raw);

            await inputLock.WaitAsync(ct);
            try { NativeInput.TypeUnicode(resolved); await Task.Delay(250, ct); }
            finally { inputLock.Release(); }

            if (used.Count > 0) await ServiceLog($"[HostControl] typed text using secrets: {string.Join(", ", used)}");
            onProgress(new HostControlProgress { Note = $"type \"{Trim(redacted, 40)}\"", Activity = new AgentActivityEvent { Kind = "action", Text = $"type {Trim(redacted, 40)}" } });
            var result = CaptureFrameResult("active", "type");
            result.Text = $"Typed \"{redacted}\". " + result.Text;
            return result;
        }

        private async Task<ComputerToolResult> KeyAsync(JsonElement a, CancellationToken ct, Action<HostControlProgress> onProgress)
        {
            var guard = OsGuard();
            if (guard != null) return guard;

            var keys = Strs(a, "keys");
            if ((keys == null || keys.Length == 0) && a.TryGetProperty("key", out var single) && single.ValueKind == JsonValueKind.String)
                keys = new[] { single.GetString()! };
            if (keys == null || keys.Length == 0) return ComputerToolResult.Fail("Provide 'key' or 'keys'.");

            bool ok;
            await inputLock.WaitAsync(ct);
            try { ok = NativeInput.TryPressKeys(keys); await Task.Delay(250, ct); }
            finally { inputLock.Release(); }

            if (!ok) return ComputerToolResult.Fail($"Unrecognized key(s): {string.Join("+", keys)}.");
            var label = $"press {string.Join("+", keys)}";
            onProgress(new HostControlProgress { Note = label, Activity = new AgentActivityEvent { Kind = "action", Text = label } });
            var result = CaptureFrameResult("active", label);
            result.Text = $"Pressed {string.Join("+", keys)}. " + result.Text;
            return result;
        }

        private async Task<ComputerToolResult> WaitAsync(JsonElement a, CancellationToken ct, Action<HostControlProgress> onProgress)
        {
            int maxMs = IntOr(a, "maxMs", IntOr(a, "forMs", 4000));
            maxMs = Math.Clamp(maxMs, 100, 600000);
            // We can't OCR for untilText, so any "wait until ready" intent (untilText or untilImageChange)
            // resolves the same way: stop as soon as the screen stops changing → settled, then return.
            bool waitForSettle = (a.TryGetProperty("untilImageChange", out var ic) && ic.ValueKind == JsonValueKind.True)
                                 || !string.IsNullOrEmpty(Str(a, "untilText"));

            byte[]? baseline = waitForSettle ? CaptureRawHashSource() : null;
            var sw = Stopwatch.StartNew();
            string resolved = "time elapsed";
            int lastHeartbeat = 0;

            while (sw.ElapsedMilliseconds < maxMs)
            {
                if (ct.IsCancellationRequested) break;
                await Task.Delay(400, ct);

                // Heartbeat every ~3s so the stall watchdog sees progress during a legitimate wait.
                if (sw.ElapsedMilliseconds - lastHeartbeat >= 3000)
                {
                    lastHeartbeat = (int)sw.ElapsedMilliseconds;
                    onProgress(new HostControlProgress { Note = $"waiting… ({sw.ElapsedMilliseconds / 1000}s)", Activity = new AgentActivityEvent { Kind = "wait", Text = $"waiting {sw.ElapsedMilliseconds / 1000}s" } });
                }

                if (waitForSettle && baseline != null)
                {
                    var now = CaptureRawHashSource();
                    if (now != null && !HashEqual(baseline, now)) { resolved = "screen changed"; break; }
                }
            }

            var result = CaptureFrameResult("active", "after wait");
            result.Text = $"Waited {sw.ElapsedMilliseconds}ms ({resolved}). " + result.Text;
            return result;
        }

        private async Task<ComputerToolResult> FocusWindowAsync(JsonElement a, CancellationToken ct, Action<HostControlProgress> onProgress)
        {
            var guard = OsGuard();
            if (guard != null) return guard;
            string? title = Str(a, "titleContains");
            string? proc = Str(a, "processName");
            var windows = NativeInput.EnumerateVisibleWindows();
            var match = windows.FirstOrDefault(w =>
                (title != null && w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)));
            if (match.Handle == IntPtr.Zero && proc != null)
            {
                var pids = Process.GetProcessesByName(proc.Replace(".exe", "")).Select(p => (uint)p.Id).ToHashSet();
                match = windows.FirstOrDefault(w => pids.Contains(w.ProcessId));
            }
            if (match.Handle == IntPtr.Zero) return ComputerToolResult.Fail("No matching visible window found.");

            await inputLock.WaitAsync(ct);
            // Maximize on focus so the agent gets the biggest possible working area (and a readable screenshot).
            try { NativeInput.FocusWindow(match.Handle, maximize: true); await Task.Delay(350, ct); }
            finally { inputLock.Release(); }

            onProgress(new HostControlProgress { Activity = new AgentActivityEvent { Kind = "action", Text = $"focus \"{Trim(match.Title, 30)}\"" } });
            var result = CaptureFrameResult("active", "focus window");
            result.Text = $"Focused + maximized \"{match.Title}\". " + result.Text;
            return result;
        }

        /// <summary>Open or focus the user's real system browser (maximized), so the agent can drive it with
        /// mouse/keyboard. If a browser window is already open it is brought to the front and maximized.</summary>
        private async Task<ComputerToolResult> OpenBrowserAsync(JsonElement a)
        {
            var guard = OsGuard();
            if (guard != null) return guard;
            await EnsureBrowserFocusedAsync(Str(a, "url"));
            await Task.Delay(300);
            var r = CaptureFrameResult("active", "browser");
            r.Text = "Browser is focused and maximized. To go to a page, use computer_navigate(url) — one step, no fiddling with the address bar. " + r.Text;
            return r;
        }

        /// <summary>Bring the user's real browser to the front (maximized), launching the default browser to
        /// <paramref name="landingUrl"/> if none is open. Shared by computer_open_browser and computer_navigate.</summary>
        private async Task EnsureBrowserFocusedAsync(string? landingUrl)
        {
            string[] browserProcs = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };
            var open = NativeInput.EnumerateVisibleWindows()
                .FirstOrDefault(w => browserProcs.Any(p => string.Equals(SafeProcName(w.ProcessId), p, StringComparison.OrdinalIgnoreCase)));

            if (open.Handle != IntPtr.Zero)
            {
                await inputLock.WaitAsync();
                try { NativeInput.FocusWindow(open.Handle, maximize: true); await Task.Delay(400); }
                finally { inputLock.Release(); }
                return;
            }

            var landing = string.IsNullOrWhiteSpace(landingUrl) ? "https://www.google.com"
                : (landingUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? landingUrl : "https://" + landingUrl);
            try
            {
                Process.Start(new ProcessStartInfo { FileName = landing, UseShellExecute = true });
                await Task.Delay(2800);
                var fg = NativeInput.GetForegroundWindowInfo();
                if (fg != null) { await inputLock.WaitAsync(); try { NativeInput.FocusWindow(fg.Value.Handle, maximize: true); } finally { inputLock.Release(); } }
            }
            catch { /* best-effort */ }
        }

        /// <summary>Atomic, reliable browser navigation: focus/open the browser, focus the address bar (Ctrl+L),
        /// type the URL, Enter, then wait for the page to settle. One step instead of a brittle click+type dance.</summary>
        private async Task<ComputerToolResult> NavigateAsync(JsonElement a, CancellationToken ct, Action<HostControlProgress> onProgress)
        {
            var guard = OsGuard();
            if (guard != null) return guard;
            var url = Str(a, "url");
            if (string.IsNullOrWhiteSpace(url)) return ComputerToolResult.Fail("Provide 'url'.");
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;

            onProgress(new HostControlProgress { Note = $"navigate {url}", Activity = new AgentActivityEvent { Kind = "action", Text = $"navigate {Trim(url, 50)}" } });
            await EnsureBrowserFocusedAsync(url);

            await inputLock.WaitAsync(ct);
            try
            {
                NativeInput.TryPressKeys(new[] { "ctrl", "l" }); // focus the address bar
                await Task.Delay(250, ct);
                NativeInput.TypeUnicode(url);
                await Task.Delay(150, ct);
                NativeInput.TryPressKeys(new[] { "enter" });
            }
            finally { inputLock.Release(); }

            await SettleAsync(ct, onProgress, 9000);
            var result = CaptureFrameResult("active", $"navigated {Trim(url, 40)}");
            result.Text = $"Navigated the browser to {url}. " + result.Text;
            return result;
        }

        /// <summary>Wait until the screen stops changing (page settled) or maxMs elapses, heartbeating so the
        /// stall watchdog stays calm during a legitimate load. No hard timeout that could kill real work.</summary>
        private async Task SettleAsync(CancellationToken ct, Action<HostControlProgress> onProgress, int maxMs)
        {
            var sw = Stopwatch.StartNew();
            int lastHeartbeat = 0, stableMs = 0;
            byte[]? prev = null;
            while (sw.ElapsedMilliseconds < maxMs && stableMs < 900)
            {
                if (ct.IsCancellationRequested) break;
                await Task.Delay(300, ct);
                var cur = CaptureRawHashSource();
                if (prev != null && cur != null && HashEqual(prev, cur)) stableMs += 300; else stableMs = 0;
                prev = cur;
                if (sw.ElapsedMilliseconds - lastHeartbeat >= 3000)
                {
                    lastHeartbeat = (int)sw.ElapsedMilliseconds;
                    onProgress(new HostControlProgress { Note = $"loading… ({sw.ElapsedMilliseconds / 1000}s)", Activity = new AgentActivityEvent { Kind = "wait", Text = "loading page" } });
                }
            }
        }

        private static string SafeProcName(uint pid)
        {
            try { return Process.GetProcessById((int)pid).ProcessName; }
            catch { return string.Empty; }
        }

        private async Task<ComputerToolResult> LaunchAppAsync(JsonElement a)
        {
            var guard = OsGuard();
            if (guard != null) return guard;
            var target = Str(a, "path") ?? Str(a, "shellName");
            if (string.IsNullOrWhiteSpace(target)) return ComputerToolResult.Fail("Provide 'path' or 'shellName'.");

            // Browsers are always allowed (use computer_open_browser for the default browser). Everything
            // else must be in KliveAgent_AppAllowList.
            string[] browsers = { "chrome", "msedge", "edge", "firefox", "brave", "opera", "vivaldi" };
            bool isBrowser = browsers.Any(b => target.Contains(b, StringComparison.OrdinalIgnoreCase));
            if (!isBrowser)
            {
                var allow = await ParseListSettingAsync("KliveAgent_AppAllowList");
                if (allow.Count == 0)
                    return ComputerToolResult.Fail("App launching is disabled: KliveAgent_AppAllowList is empty. Add the app to the allow-list first (browsers are always allowed).");
                if (!allow.Any(x => target.Contains(x, StringComparison.OrdinalIgnoreCase)))
                    return ComputerToolResult.Fail($"\"{target}\" is not in KliveAgent_AppAllowList. Refusing.");
            }

            try
            {
                var psi = new ProcessStartInfo { FileName = target, Arguments = Str(a, "args") ?? string.Empty, UseShellExecute = true };
                Process.Start(psi);
                await Task.Delay(2000);
                var fg = NativeInput.GetForegroundWindowInfo();
                if (fg != null) NativeInput.FocusWindow(fg.Value.Handle, maximize: true);
                await ServiceLog($"[HostControl] launched app: {target}");
                return WindowState();
            }
            catch (Exception ex) { return ComputerToolResult.Fail($"Launch failed: {ex.Message}"); }
        }

        // ── Gated actions ──
        private async Task<ComputerToolResult> ConfirmActionAsync(JsonElement a, CancellationToken ct, Action<HostControlProgress> onProgress)
        {
            var summary = Str(a, "summary") ?? "Proceed with an irreversible action?";
            byte[]? frame;
            lock (frameLock) frame = lastFrameJpeg;
            var annotated = frame != null ? capturer.Annotate(frame, "AWAITING APPROVAL: " + Trim(summary, 60)) : null;

            await ServiceLog($"[HostControl] approval requested: {summary}");
            bool approved = await approvals.RequestAsync(summary, annotated, ct, onProgress);
            return approved
                ? ComputerToolResult.Ok($"APPROVED: {summary}. Proceed with the action now.")
                : ComputerToolResult.Fail($"DENIED: {summary}. Do not proceed; stop and report this to the user.");
        }

        private async Task<ComputerToolResult> ConfirmAndClickAsync(JsonElement a, CancellationToken ct, Action<HostControlProgress> onProgress)
        {
            var guard = OsGuard();
            if (guard != null) return guard;
            int x = IntOr(a, "x", 0), y = IntOr(a, "y", 0);
            var summary = Str(a, "summary") ?? $"Click at ({x},{y})";

            byte[]? frame;
            lock (frameLock) frame = lastFrameJpeg;
            var annotated = frame != null ? capturer.Annotate(frame, "APPROVE? " + Trim(summary, 50), x, y) : null;

            await ServiceLog($"[HostControl] gated click approval requested: {summary}");
            bool approved = await approvals.RequestAsync(summary, annotated, ct, onProgress);
            if (!approved)
                return ComputerToolResult.Fail($"DENIED: {summary}. The click was NOT performed. Stop and report this to the user.");

            if (await DryRunAsync())
            {
                await ServiceLog($"[HostControl] DRY-RUN: would have clicked ({x},{y}) — {summary}");
                var dr = CaptureFrameResult("active", "DRY-RUN click " + summary, x, y);
                dr.Text = $"DRY-RUN: approved and would have clicked ({x},{y}) [{summary}] but dry-run is on, so nothing was clicked. " + dr.Text;
                return dr;
            }

            return await MutatingAsync(ct, onProgress, "confirm-click", () =>
            {
                var (px, py) = MapToPhysical(x, y);
                NativeInput.Click(px, py, ParseButton(Str(a, "button")));
            }, label: $"APPROVED click: {Trim(summary, 40)}", tx: x, ty: y);
        }

        // ── Coordinate mapping ──
        private (int x, int y) MapToPhysical(int modelX, int modelY)
        {
            CoordFrame? f;
            lock (frameLock) f = lastFrame;
            if (f == null)
            {
                // Establish a frame on demand (the model normally screenshots first).
                CaptureFrameResult("active", "auto");
                lock (frameLock) f = lastFrame;
            }
            if (f == null) return (modelX, modelY);
            var frame = f.Value;
            int px = frame.OriginX + (int)Math.Round(modelX / frame.Scale);
            int py = frame.OriginY + (int)Math.Round(modelY / frame.Scale);
            return (px, py);
        }

        private int ScreenCenterX() { lock (frameLock) return lastFrame is { } f ? f.ShownW / 2 : 400; }
        private int ScreenCenterY() { lock (frameLock) return lastFrame is { } f ? f.ShownH / 2 : 300; }

        private byte[]? CaptureRawHashSource()
        {
            try { return capturer.CaptureActiveWindow(640, 40).Jpeg; } catch { return null; }
        }
        private static bool HashEqual(byte[] a, byte[] b)
        {
            using var sha = SHA1.Create();
            return sha.ComputeHash(a).AsSpan().SequenceEqual(sha.ComputeHash(b));
        }

        // ── Settings / parsing helpers ──
        private async Task<List<string>> ParseListSettingAsync(string settingName)
        {
            var raw = await GetStringOmniSetting(settingName, defaultValue: "") ?? "";
            return raw.Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        private static Omnipotent.Threading.MouseButton ParseButton(string? b) => (b ?? "left").ToLowerInvariant() switch
        {
            "right" => Omnipotent.Threading.MouseButton.Right,
            "middle" => Omnipotent.Threading.MouseButton.Middle,
            _ => Omnipotent.Threading.MouseButton.Left,
        };

        private static string? Str(JsonElement a, string name) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        private static int IntOr(JsonElement a, string name, int def) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var e) && e.TryGetInt32(out var v) ? v : def;
        private static string[]? Strs(JsonElement a, string name) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                : null;
        private static string Trim(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? (s ?? "") : s.Substring(0, max) + "…";
    }
}
