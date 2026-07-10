using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.ComputerControl;
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
    public class HostControlManager : OmniService, IComputerController
    {
        private readonly ScreenCapturer capturer = new();
        private EncryptedMemoryStore secrets = null!;
        private SecretSubstituter substituter = null!;
        private ApprovalBroker approvals = null!;

        // Input is a process-global, exclusive resource: only one mutating OS-input action at a time.
        private readonly SemaphoreSlim inputLock = new(1, 1);

        // Held (pressed-and-not-released) mouse buttons / keys, so a press-and-hold can span multiple tool
        // calls (e.g. mouse_down → move → mouse_up, or hold Shift across clicks). A safety watchdog releases
        // anything left held after the run goes idle, so nothing ever stays physically stuck.
        private readonly object holdLock = new();
        private readonly HashSet<Omnipotent.Threading.MouseButton> heldButtons = new();
        private readonly HashSet<string> heldKeys = new(StringComparer.OrdinalIgnoreCase);
        private DateTime lastHoldUtc = DateTime.MinValue;

        // Coordinate frame of the most recent screenshot shown to the model, so its image-space click
        // coordinates map back to physical screen pixels.
        private readonly object frameLock = new();
        private CoordFrame? lastFrame;
        private byte[]? lastFrameJpeg;
        private readonly record struct CoordFrame(double Scale, int OriginX, int OriginY, int ShownW, int ShownH);

        // Open human-intervention handoffs (request_human), keyed by their scoped capability token. A token
        // authorizes the remote stream + input routes only while its handoff is pending (see AuthorizeRemoteAsync).
        private readonly ConcurrentDictionary<string, PendingHandoff> handoffs = new();

        public ApprovalBroker Approvals => approvals;
        public EncryptedMemoryStore Secrets => secrets;
        public ComputerCapabilities Capabilities { get; } = new()
        {
            SupportsOcr = true,
            SupportsWindowControl = true,
            SupportsBrowserControl = true,
            SupportsClipboard = true,
            SupportsAppLaunch = true,
            SupportsRelativeMouse = true,
            SupportsHumanization = true,
            SupportsMotionFrames = true,
        };

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
                _ = Task.Run(HeldInputSafetyLoopAsync);
                await RegisterScreenStreamRouteAsync();
                await RegisterRemoteInputRouteAsync();
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

        // Motion-clip config, refreshed before every action in ExecuteToolAsync (same as osControlEnabled) so
        // toggling a setting takes effect immediately. See ScreenCapturer's clip machinery.
        private volatile bool clipEnabled = true;
        private volatile int clipSampleFps = 12;
        private volatile int clipWindowMs = 1200;
        private volatile int clipMaxFrames = 4;
        private volatile int clipMotionThreshold = 2;

        // Input humanization (curved/eased movement, click dwell, human typing cadence) — reduces the
        // robot-like signal anti-bot/captcha systems score against. Refreshed per action like the clip config.
        private volatile HumanizationLevel humanizationLevel = HumanizationLevel.Balanced;
        private volatile bool humanizationTypos = false;
        /// <summary>Fresh per-action humanization profile (null = humanization off → original instant input).</summary>
        private HumanInputProfile? Human() => HumanInputProfile.Create(humanizationLevel, humanizationTypos);

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
                        // Anybody and authorized here: a Klives ?authorization= password, OR a pending handoff
                        // ?token= (scoped capability for a captcha-solve session).
                        var (auth, handoff) = await AuthorizeRemoteAsync(queryParams, user);
                        if (auth == RemoteAuthKind.Denied)
                        {
                            try { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None); } catch { }
                            return;
                        }
                        // Optional per-connection fps/quality so an interactive viewer (remote desktop / solve
                        // page) can request smoother video, falling back to the idle stream defaults.
                        int? fps = TryQueryInt(queryParams, "fps");
                        int? quality = TryQueryInt(queryParams, "quality");
                        await StreamScreenAsync(socket, fps, quality, handoff);
                    }),
                    KMProfileManager.KMPermissions.Anybody);
                await ServiceLog("[HostControl] Screen-stream WebSocket route registered (/kliveagent/screen/stream).");
            }
            catch (Exception ex) { await ServiceLogError(ex, "[HostControl] Failed to register screen-stream route (non-fatal)."); }
        }

        private async Task StreamScreenAsync(WebSocket socket, int? fpsOverride = null, int? qualityOverride = null, PendingHandoff? handoff = null)
        {
            if (!await ComputerUseEnabledAsync())
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "computer-use disabled", CancellationToken.None); } catch { }
                return;
            }

            int fps = Math.Clamp(fpsOverride ?? await GetIntOmniSetting("KliveAgent_StreamFps", 6), 1, 30);
            int width = Math.Clamp(await GetIntOmniSetting("KliveAgent_StreamWidth", 1366), 320, 3840);
            int quality = Math.Clamp(qualityOverride ?? await GetIntOmniSetting("KliveAgent_StreamQuality", 45), 10, 92);
            int delayMs = Math.Max(25, 1000 / fps);

            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    // A token-scoped (captcha-solve) stream ends the instant its handoff resolves.
                    if (handoff != null && !handoff.IsPending) break;
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

        // ── Remote control (two-way input) ──
        // The view-only stream above is paired with this input channel: a WebSocket that receives the
        // operator's pointer/keyboard events (normalized 0..1 coords) and REPLAYS them on the real machine
        // via NativeInput, under the same exclusive input lock the agent uses. This powers BOTH the Admin
        // remote-desktop (password auth) and the captcha-solve page (token auth) — one route, two callers.
        private enum RemoteAuthKind { Denied, Password, Token }

        /// <summary>Authorize a remote stream/input WebSocket: a Klives ?authorization= password, OR a pending
        /// handoff ?token= (scoped capability). Returns the matched handoff for a token session.</summary>
        private async Task<(RemoteAuthKind kind, PendingHandoff? handoff)> AuthorizeRemoteAsync(NameValueCollection q, KMProfileManager.KMProfile? user)
        {
            var resolved = user;
            if (resolved == null)
            {
                var pw = q["authorization"];
                if (!string.IsNullOrEmpty(pw))
                    resolved = await ExecuteServiceMethod<KMProfileManager>("GetProfileByPassword", pw) as KMProfileManager.KMProfile;
            }
            if (resolved != null && resolved.KlivesManagementRank >= KMProfileManager.KMPermissions.Klives)
                return (RemoteAuthKind.Password, null);

            var token = q["token"];
            if (!string.IsNullOrEmpty(token) && handoffs.TryGetValue(token, out var h) && h.IsPending)
                return (RemoteAuthKind.Token, h);

            return (RemoteAuthKind.Denied, null);
        }

        private static int? TryQueryInt(NameValueCollection q, string key) =>
            int.TryParse(q[key], out var v) ? v : (int?)null;

        private async Task RegisterRemoteInputRouteAsync()
        {
            try
            {
                await ExecuteServiceMethod<Omnipotent.Services.KliveAPI.KliveAPI>("CreateWebSocketRoute",
                    "/kliveagent/remote/input",
                    (Func<HttpListenerContext, WebSocket, NameValueCollection, KMProfileManager.KMProfile?, Task>)(async (context, socket, queryParams, user) =>
                    {
                        var (auth, handoff) = await AuthorizeRemoteAsync(queryParams, user);
                        if (auth == RemoteAuthKind.Denied)
                        {
                            try { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None); } catch { }
                            return;
                        }
                        await HandleRemoteInputAsync(socket, handoff);
                    }),
                    KMProfileManager.KMPermissions.Anybody);
                await ServiceLog("[HostControl] Remote-input WebSocket route registered (/kliveagent/remote/input).");
            }
            catch (Exception ex) { await ServiceLogError(ex, "[HostControl] Failed to register remote-input route (non-fatal)."); }
        }

        /// <summary>Receive loop: each text frame is one JSON input event ({t:"move|down|up|click|dblclick|
        /// scroll|text|key|keydown|keyup|resolve", ...}). Locally-held buttons are released on disconnect so a
        /// drag can never stay stuck.</summary>
        private async Task HandleRemoteInputAsync(WebSocket socket, PendingHandoff? handoff)
        {
            if (!await ComputerUseEnabledAsync())
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "computer-use disabled", CancellationToken.None); } catch { }
                return;
            }

            var buffer = new byte[16 * 1024];
            var sb = new StringBuilder();
            var heldLocal = new HashSet<Omnipotent.Threading.MouseButton>();
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    sb.Clear();
                    WebSocketReceiveResult res;
                    do
                    {
                        res = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (res.MessageType == WebSocketMessageType.Close) return;
                        if (res.MessageType == WebSocketMessageType.Text)
                            sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                    } while (!res.EndOfMessage);

                    var msg = sb.ToString();
                    if (string.IsNullOrWhiteSpace(msg)) continue;
                    JsonElement ev;
                    try { ev = JsonDocument.Parse(msg).RootElement; } catch { continue; }
                    if (ev.ValueKind != JsonValueKind.Object) continue;
                    await ApplyRemoteInputAsync(ev, handoff, heldLocal);
                }
            }
            catch { /* viewer disconnected or a malformed frame → unwind */ }
            finally
            {
                if (heldLocal.Count > 0)
                {
                    var (cx, cy) = NativeInput.GetCursorPosition();
                    await inputLock.WaitAsync();
                    try { foreach (var b in heldLocal) NativeInput.MouseButtonUp(cx, cy, b); } finally { inputLock.Release(); }
                }
                try { if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            }
        }

        private async Task ApplyRemoteInputAsync(JsonElement ev, PendingHandoff? handoff, HashSet<Omnipotent.Threading.MouseButton> heldLocal)
        {
            var t = (Str(ev, "t") ?? string.Empty).ToLowerInvariant();

            // Any event from the operator counts as activity (drives the "idle after interacting" auto-resume).
            if (handoff != null)
            {
                Interlocked.Exchange(ref handoff.Interacted, 1);
                Interlocked.Exchange(ref handoff.LastInputUtcTicks, DateTime.UtcNow.Ticks);
            }

            if (t == "resolve" || t == "done")
            {
                if (handoff != null) ResolveHandoff(handoff, "done");
                return;
            }

            if (!OsControlAvailable || !await ComputerUseEnabledAsync()) return;

            var button = ParseButton(Str(ev, "button"));
            (int px, int py) Pt()
            {
                var (vx, vy, vw, vh) = NativeInput.GetVirtualScreenBounds();
                double nx = Math.Clamp(DblOr(ev, "x", 0), 0, 1);
                double ny = Math.Clamp(DblOr(ev, "y", 0), 0, 1);
                return (vx + (int)Math.Round(nx * Math.Max(1, vw - 1)), vy + (int)Math.Round(ny * Math.Max(1, vh - 1)));
            }

            await inputLock.WaitAsync();
            try
            {
                switch (t)
                {
                    case "move": { var (px, py) = Pt(); NativeInput.MoveMouse(px, py); break; }
                    case "down": { var (px, py) = Pt(); NativeInput.MouseButtonDown(px, py, button); heldLocal.Add(button); break; }
                    case "up": { var (px, py) = Pt(); NativeInput.MouseButtonUp(px, py, button); heldLocal.Remove(button); break; }
                    case "click":
                    {
                        var (px, py) = Pt();
                        int clicks = Math.Max(1, IntOr(ev, "clicks", 1));
                        var mods = NormalizeModifiers(Strs(ev, "modifiers"));
                        WithModifiers(mods, () => NativeInput.Click(px, py, button, clicks));
                        break;
                    }
                    case "dblclick": { var (px, py) = Pt(); NativeInput.Click(px, py, button, 2); break; }
                    case "scroll": { var (px, py) = Pt(); NativeInput.Scroll(px, py, IntOr(ev, "dy", 0), IntOr(ev, "dx", 0)); break; }
                    case "text": NativeInput.TypeUnicode(Str(ev, "text") ?? string.Empty); break;
                    case "key":
                    {
                        var keys = Strs(ev, "keys");
                        if ((keys == null || keys.Length == 0) && Str(ev, "key") is { } single) keys = new[] { single };
                        if (keys != null && keys.Length > 0) NativeInput.TryPressKeys(keys);
                        break;
                    }
                    case "keydown": if (Str(ev, "key") is { } kd) NativeInput.KeyDown(kd); break;
                    case "keyup": if (Str(ev, "key") is { } ku) NativeInput.KeyUp(ku); break;
                }
            }
            finally { inputLock.Release(); }
        }

        private bool ResolveHandoff(PendingHandoff h, string outcome)
        {
            var ok = h.Completion.TrySetResult(outcome);
            handoffs.TryRemove(h.Token, out _);
            return ok;
        }

        private static string MintHandoffToken()
        {
            Span<byte> b = stackalloc byte[32];
            RandomNumberGenerator.Fill(b);
            return Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
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
                // Refresh the motion-clip config likewise (cheap; read once per action, not per sampled frame).
                clipEnabled = await GetBoolOmniSetting("KliveAgent_ClipEnabled", defaultValue: true);
                clipSampleFps = Math.Clamp(await GetIntOmniSetting("KliveAgent_ClipSampleFps", 12), 2, 30);
                clipWindowMs = Math.Clamp(await GetIntOmniSetting("KliveAgent_ClipWindowMs", 1200), 200, 6000);
                clipMaxFrames = Math.Clamp(await GetIntOmniSetting("KliveAgent_ClipMaxFrames", 4), 1, 12);
                clipMotionThreshold = Math.Clamp(await GetIntOmniSetting("KliveAgent_ClipMotionThreshold", 2), 1, 64);
                // Refresh input-humanization config (same cadence) so the level/typo toggles take effect live.
                humanizationLevel = HumanInputProfile.ParseLevel(await GetStringOmniSetting("KliveAgent_HumanizationLevel", defaultValue: "balanced"));
                humanizationTypos = await GetBoolOmniSetting("KliveAgent_HumanizationTypos", defaultValue: false);
                // Any tool call while inputs are held = the drag/hold is still in progress → keep it alive.
                lock (holdLock) { if (heldButtons.Count > 0 || heldKeys.Count > 0) lastHoldUtc = DateTime.UtcNow; }
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

        /// <summary>Target-neutral adapter used by callers that do not care whether the desktop is
        /// Win32 or VNC.  Existing KliveAgent image plumbing remains source-compatible.</summary>
        public async Task<ComputerActionResult> ExecuteComputerActionAsync(ComputerActionRequest request, CancellationToken ct = default)
        {
            var result = await ExecuteToolAsync(request.ToolName, request.ArgumentsJson, ct);
            var frames = result.ModelImageFrames?.Select(f => new ComputerFrame
            {
                Jpeg = f.Jpeg, OffsetMs = f.OffsetMs, IsSettled = f.IsSettled, HasCoordinateGrid = f.HasGrid
            }).ToList() ?? new List<ComputerFrame>();
            return new ComputerActionResult
            {
                Success = result.Success,
                Text = result.Text,
                Error = result.ErrorMessage,
                AuditSummary = ComputerAudit.Describe(request.ToolName, request.ArgumentsJson),
                Observation = result.ModelImageJpeg == null ? null : new ComputerObservation
                {
                    FinalFrameJpeg = result.ModelImageJpeg,
                    Frames = frames,
                    IsSettled = true,
                }
            };
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
                    return await CaptureClipAsync(Str(a, "target") ?? "active", "screenshot", null, null, null, ct);

                case "computer_find_text":
                    return await FindVisibleTextAsync(a, ct, onProgress, click: false);

                case "computer_click_text":
                    return await FindVisibleTextAsync(a, ct, onProgress, click: true);

                case "computer_window_state":
                    return WindowState();

                case "computer_read_screen":
                    return await ReadScreenAsync();

                case "computer_move":
                    return await MutatingAsync(ct, onProgress, "move", () =>
                    {
                        var (px, py) = MapToPhysical(IntOr(a, "x", 0), IntOr(a, "y", 0));
                        NativeInput.MoveMouse(px, py, Human(), ct);
                    }, label: $"move ({IntOr(a, "x", 0)},{IntOr(a, "y", 0)})", tx: IntOr(a, "x", 0), ty: IntOr(a, "y", 0));

                case "computer_mouse_move_relative":
                {
                    int rdx = IntOr(a, "dx", 0), rdy = IntOr(a, "dy", 0);
                    // Smooth the delta into sub-moves so games integrate it as real motion (not one big jump).
                    int rsteps = Math.Clamp(IntOr(a, "steps", Math.Max(1, (Math.Abs(rdx) + Math.Abs(rdy)) / 40)), 1, 200);
                    return await MutatingAsync(ct, onProgress, "mouse_move_relative", () =>
                    {
                        NativeInput.MoveMouseRelative(rdx, rdy, rsteps, 3);
                    }, label: $"mouse Δ({rdx},{rdy})");
                }

                case "computer_click":
                {
                    var mods = NormalizeModifiers(Strs(a, "modifiers"));
                    return await MutatingAsync(ct, onProgress, "click", () =>
                    {
                        var (px, py) = MapToPhysical(IntOr(a, "x", 0), IntOr(a, "y", 0));
                        WithModifiers(mods, () => NativeInput.Click(px, py, ParseButton(Str(a, "button")), Math.Max(1, IntOr(a, "clicks", 1)), Human(), ct));
                    }, label: $"{ModLabel(mods)}click ({IntOr(a, "x", 0)},{IntOr(a, "y", 0)})", tx: IntOr(a, "x", 0), ty: IntOr(a, "y", 0));
                }

                case "computer_drag":
                {
                    var mods = NormalizeModifiers(Strs(a, "modifiers"));
                    var btn = ParseButton(Str(a, "button"));
                    return await MutatingAsync(ct, onProgress, "drag", () =>
                    {
                        var (fx, fy) = MapToPhysical(IntOr(a, "fromX", 0), IntOr(a, "fromY", 0));
                        var (tx2, ty2) = MapToPhysical(IntOr(a, "toX", 0), IntOr(a, "toY", 0));
                        WithModifiers(mods, () => NativeInput.Drag(fx, fy, tx2, ty2, btn, Human(), ct));
                    }, label: $"{ModLabel(mods)}drag", tx: IntOr(a, "toX", 0), ty: IntOr(a, "toY", 0));
                }

                case "computer_mouse_down":
                    return await HoldMouseAsync(a, ct, onProgress, down: true);

                case "computer_mouse_up":
                    return await HoldMouseAsync(a, ct, onProgress, down: false);

                case "computer_key_down":
                    return await HoldKeyAsync(a, ct, onProgress, down: true);

                case "computer_key_up":
                    return await HoldKeyAsync(a, ct, onProgress, down: false);

                case "computer_release_all":
                {
                    await ReleaseHeldInputsAsync(auto: false);
                    var r = await CaptureClipAsync("active", "release all", null, null, null, ct);
                    r.Text = "Released all held mouse buttons and keys. " + r.Text;
                    return r;
                }

                case "computer_scroll":
                {
                    // Intuitive interface: {direction:"down"|"up"|"left"|"right", amount:N notches}. Falls back
                    // to raw dy/dx; defaults to scrolling DOWN so the model never wrestles with the wheel sign.
                    int amount = Math.Abs(IntOr(a, "amount", 5));
                    if (amount == 0) amount = 5;
                    int dy = IntOr(a, "dy", 0), dx = IntOr(a, "dx", 0);
                    switch ((Str(a, "direction") ?? "").Trim().ToLowerInvariant())
                    {
                        case "down": dy = -amount; break;
                        case "up": dy = amount; break;
                        case "right": dx = amount; break;
                        case "left": dx = -amount; break;
                    }
                    if (dy == 0 && dx == 0) dy = -amount; // default: down
                    int fdy = dy, fdx = dx;
                    var dirLabel = fdy < 0 ? "down" : fdy > 0 ? "up" : fdx > 0 ? "right" : "left";
                    return await MutatingAsync(ct, onProgress, "scroll", () =>
                    {
                        int cx = a.TryGetProperty("x", out _) ? IntOr(a, "x", 0) : ScreenCenterX();
                        int cy = a.TryGetProperty("y", out _) ? IntOr(a, "y", 0) : ScreenCenterY();
                        var (px, py) = MapToPhysical(cx, cy);
                        NativeInput.Scroll(px, py, fdy, fdx, Human(), ct);
                    }, label: $"scroll {dirLabel} {Math.Max(Math.Abs(fdy), Math.Abs(fdx))}");
                }

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

                case "request_human":
                    return await RequestHumanAsync(a, ct, onProgress);

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
        // Capture ONE settled frame: the current on-screen state, gridded for the model + annotated for the
        // website, and (under frameLock) the coordinate frame so the model's click coords map to pixels.
        // This is the synchronous building block; CaptureClipAsync wraps it with a motion filmstrip.
        private ComputerToolResult CaptureSettledFrame(string target, string label, int? tx = null, int? ty = null)
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
                AnnotatedJpeg = annotated,
                ModelImageFrames = new List<ClipFrame> { new ClipFrame { Jpeg = modelImage, IsSettled = true, HasGrid = true } }
            };
        }

        private ScreenCapturer.ClipCaptureOptions ClipOpts() => new()
        {
            SampleFps = clipSampleFps,
            WindowMs = clipWindowMs,
            MaxFrames = clipMaxFrames,
            MotionThreshold = clipMotionThreshold,
            SampleMaxWidth = 1000,
            SampleQuality = 55
        };

        /// <summary>
        /// Perception with a short motion "clip": collects the in-between frames sampled across the action
        /// (oldest→newest), then captures the freshest SETTLED frame (gridded) as the final/current state.
        /// A still result collapses to just the settled frame — identical cost to a single screenshot.
        /// Pass a <paramref name="recorder"/> already started BEFORE a mutating action (so the clip spans the
        /// gesture); pass null for pure perception (a fresh short window is sampled on the spot).
        /// </summary>
        private async Task<ComputerToolResult> CaptureClipAsync(string target, string label, int? tx, int? ty, ScreenCapturer.ClipRecorder? recorder, CancellationToken ct)
        {
            if (!clipEnabled)
            {
                recorder?.RequestFinish();
                if (recorder != null) { try { await recorder.FinishAsync(ct); } catch { } }
                return CaptureSettledFrame(target, label, tx, ty);
            }

            recorder ??= capturer.BeginClip(target, ClipOpts());
            List<ScreenCapturer.ClipSample> samples;
            try { samples = await recorder.FinishAsync(ct); }
            catch { samples = new List<ScreenCapturer.ClipSample>(); }

            // Settled frame LAST = freshest/current; also sets lastFrame for coordinate mapping (today's behaviour).
            var settled = CaptureSettledFrame(target, label, tx, ty);
            if (!settled.Success) return settled;

            try
            {
                byte[]? rawSettled;
                lock (frameLock) rawSettled = lastFrameJpeg; // raw (gridless) settled frame, for diffing vs. intermediates
                var settledThumb = rawSettled != null ? ScreenCapturer.GrayThumb(rawSettled) : Array.Empty<byte>();
                var intermediates = capturer.SelectIntermediates(samples, settledThumb, ClipOpts());

                var frames = new List<ClipFrame>(intermediates)
                {
                    new ClipFrame
                    {
                        Jpeg = settled.ModelImageJpeg ?? Array.Empty<byte>(),
                        IsSettled = true,
                        HasGrid = true,
                        OffsetMs = (intermediates.Count > 0 ? intermediates[^1].OffsetMs : 0) + 1
                    }
                };
                settled.ModelImageFrames = frames;
                if (intermediates.Count > 0)
                    settled.Text = $"Clip: {frames.Count} frames captured across this action (oldest→newest); the LAST frame is the current gridded state. The earlier frames show what changed in-between (catch transient toasts/errors/animations there — but read click coordinates only from the LAST frame). " + settled.Text;
            }
            catch { /* keep the single settled frame already set by CaptureSettledFrame */ }

            return settled;
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

        private async Task<ComputerToolResult> FindVisibleTextAsync(JsonElement a, CancellationToken ct,
            Action<HostControlProgress> onProgress, bool click)
        {
            string needle = Str(a, "text") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(needle)) return ComputerToolResult.Fail("Provide visible 'text' to locate.");
            var observed = CaptureSettledFrame("active", click ? "find text" : "find text");
            if (!observed.Success || observed.ModelImageJpeg == null) return observed;
            var matches = await ComputerVision.FindTextAsync(observed.ModelImageJpeg, needle, ct);
            int occurrence = Math.Max(0, IntOr(a, "occurrence", 0));
            if (matches.Count <= occurrence)
            {
                observed.Text = $"No visible OCR match for '{Trim(needle, 80)}' at occurrence {occurrence}. It may be off-screen, obscured, or local OCR data is unavailable. " + observed.Text;
                return observed;
            }
            var match = matches[occurrence];
            string detail = $"OCR match {occurrence}: '{Trim(match.Text, 120)}' centre=({match.CentreX},{match.CentreY}) confidence={match.Confidence:0}.";
            if (!click)
            {
                observed.Text = detail + " " + observed.Text;
                return observed;
            }
            var clickArgs = new Dictionary<string, object?>
            {
                ["x"] = match.CentreX,
                ["y"] = match.CentreY,
                ["button"] = Str(a, "button") ?? "left",
                ["clicks"] = Math.Max(1, IntOr(a, "clicks", 1)),
            };
            var clickDoc = JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(clickArgs));
            var clicked = await RunAsync("computer_click", clickDoc.RootElement, ct, onProgress);
            clicked.Text = detail + " Clicked OCR match. " + clicked.Text;
            return clicked;
        }

        // ── Actuation helpers ──
        private ComputerToolResult? OsGuard() =>
            OsControlAvailable ? null : ComputerToolResult.Fail("OS-level control is turned off. Set KliveAgent_OsControlEnabled=true to let KliveAgent control this machine.");

        private async Task<ComputerToolResult> MutatingAsync(CancellationToken ct, Action<HostControlProgress> onProgress, string kind, Action act, string label, int? tx = null, int? ty = null)
        {
            var guard = OsGuard();
            if (guard != null) return guard;

            // Reaction time: a human notices, THEN acts — a short randomized pause before the gesture (no-op
            // when humanization is off). Cancellable; surfaces as "Action cancelled" upstream if stopped.
            var reactionProfile = Human();
            if (reactionProfile != null) await Task.Delay(reactionProfile.ReactionMs(), ct);

            // Start filming BEFORE the gesture so the clip spans the action itself (a menu that opens then the
            // click closes it, drag motion, a transition). Capture is read-only, so it runs concurrently with
            // act() without taking inputLock. When clips are off, keep the original fixed settle delay.
            var recorder = clipEnabled ? capturer.BeginClip("active", ClipOpts()) : null;
            await inputLock.WaitAsync(ct);
            try
            {
                act();
                if (recorder == null) await Task.Delay(350, ct); // let the UI settle before we observe the result
            }
            finally { inputLock.Release(); }
            recorder?.RequestFinish(); // gesture fired — now watch for the screen to settle (adaptive, bounded)

            onProgress(new HostControlProgress { Note = label, Activity = new AgentActivityEvent { Kind = "action", Text = label } });
            var result = await CaptureClipAsync("active", label, tx, ty, recorder, ct);
            result.Text = $"Did: {label}. " + result.Text;
            return result;
        }

        private async Task<ComputerToolResult> TypeAsync(JsonElement a, CancellationToken ct, Action<HostControlProgress> onProgress)
        {
            var guard = OsGuard();
            if (guard != null) return guard;

            var raw = Str(a, "text") ?? string.Empty;
            var (resolved, used) = await substituter.ResolveAsync(raw);

            // Also resolve {account:<service>/<field>} refs from the global shared account registry.
            // An ambiguous/unknown ref fails the tool loudly rather than typing a literal token into a
            // login form. Account names join `used` so typo-simulation stays hard-off below.
            var registry = GetActiveServices()
                .OfType<Omnipotent.Services.AccountRegistry.AccountRegistry>()
                .FirstOrDefault(s => s.IsServiceActive());
            if (registry != null)
            {
                var acct = registry.TryResolveForTyping(resolved, "KliveAgent");
                if (acct.Error != null) return ComputerToolResult.Fail($"type failed: {acct.Error}");
                if (acct.Used.Count > 0) { resolved = acct.Text; used.AddRange(acct.Used.Select(n => "account:" + n)); }
            }

            var redacted = substituter.Redact(raw);

            var prof = Human();
            // Typo simulation is HARD-OFF for secret-bearing text (used.Count > 0) — a credential must be typed
            // exactly, never with an injected backspace-correction that could lock an account.
            bool allowTypos = humanizationTypos && used.Count == 0;
            if (prof != null) await Task.Delay(prof.ReactionMs(), ct); // reaction before typing

            await inputLock.WaitAsync(ct);
            try
            {
                if (prof != null)
                    await NativeInput.TypeUnicodeHumanAsync(resolved, prof, allowTypos, ct,
                        onTick: () => onProgress(new HostControlProgress { Note = "typing…", Activity = new AgentActivityEvent { Kind = "action", Text = "typing" } }));
                else { NativeInput.TypeUnicode(resolved); await Task.Delay(250, ct); }
            }
            finally { inputLock.Release(); }

            if (used.Count > 0) await ServiceLog($"[HostControl] typed text using secrets: {string.Join(", ", used)}");
            onProgress(new HostControlProgress { Note = $"type \"{Trim(redacted, 40)}\"", Activity = new AgentActivityEvent { Kind = "action", Text = $"type {Trim(redacted, 40)}" } });
            var result = await CaptureClipAsync("active", "type", null, null, null, ct);
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

            // Hold each press briefly so games (which poll input per frame) register it; repeat for "tap N
            // times" (e.g. move a menu cursor down 3). Defaults stay snappy for normal app/browser chords.
            int holdMs = Math.Clamp(IntOr(a, "holdMs", 55), 1, 2000);
            int repeats = Math.Clamp(IntOr(a, "repeats", 1), 1, 50);

            bool ok = true;
            await inputLock.WaitAsync(ct);
            try
            {
                for (int r = 0; r < repeats && ok; r++)
                {
                    ok = NativeInput.TryPressKeys(keys, holdMs);
                    if (r < repeats - 1) await Task.Delay(70, ct); // gap between repeated taps so each is distinct
                }
                await Task.Delay(120, ct);
            }
            finally { inputLock.Release(); }

            if (!ok) return ComputerToolResult.Fail($"Unrecognized key(s): {string.Join("+", keys)}.");
            var label = repeats > 1 ? $"press {string.Join("+", keys)} x{repeats}" : $"press {string.Join("+", keys)}";
            onProgress(new HostControlProgress { Note = label, Activity = new AgentActivityEvent { Kind = "action", Text = label } });
            var result = await CaptureClipAsync("active", label, null, null, null, ct);
            result.Text = $"Pressed {string.Join("+", keys)}{(repeats > 1 ? $" {repeats} times" : "")}. " + result.Text;
            return result;
        }

        private async Task<ComputerToolResult> WaitAsync(JsonElement a, CancellationToken ct, Action<HostControlProgress> onProgress)
        {
            int maxMs = IntOr(a, "maxMs", IntOr(a, "ms", IntOr(a, "forMs", 4000)));
            maxMs = Math.Clamp(maxMs, 100, 600000);
            // We can't OCR for untilText, so any "wait until ready" intent (untilText or untilImageChange)
            // resolves the same way: stop as soon as the screen stops changing → settled, then return.
            bool waitForImageChange = a.TryGetProperty("untilImageChange", out var ic) && ic.ValueKind == JsonValueKind.True;
            string? untilText = Str(a, "untilText");
            bool waitForSettle = waitForImageChange || !string.IsNullOrEmpty(untilText);

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

                if (waitForImageChange && baseline != null)
                {
                    var now = CaptureRawHashSource();
                    if (now != null && !HashEqual(baseline, now)) { resolved = "screen changed"; break; }
                }
                if (!string.IsNullOrEmpty(untilText) && sw.ElapsedMilliseconds % 800 < 420)
                {
                    var frame = CaptureSettledFrame("active", "wait OCR");
                    if (frame.ModelImageJpeg != null && (await ComputerVision.FindTextAsync(frame.ModelImageJpeg, untilText, ct)).Count > 0)
                    {
                        resolved = $"text appeared: {Trim(untilText, 80)}";
                        break;
                    }
                }
            }

            var result = await CaptureClipAsync("active", "after wait", null, null, null, ct);
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
            var result = await CaptureClipAsync("active", "focus window", null, null, null, ct);
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
            var r = await CaptureClipAsync("active", "browser", null, null, null, CancellationToken.None);
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
            var result = await CaptureClipAsync("active", $"navigated {Trim(url, 40)}", null, null, null, ct);
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
                var dr = await CaptureClipAsync("active", "DRY-RUN click " + summary, x, y, null, ct);
                dr.Text = $"DRY-RUN: approved and would have clicked ({x},{y}) [{summary}] but dry-run is on, so nothing was clicked. " + dr.Text;
                return dr;
            }

            return await MutatingAsync(ct, onProgress, "confirm-click", () =>
            {
                var (px, py) = MapToPhysical(x, y);
                NativeInput.Click(px, py, ParseButton(Str(a, "button")), 1, Human(), ct);
            }, label: $"APPROVED click: {Trim(summary, 40)}", tx: x, ty: y);
        }

        // ── Human intervention (request_human) ──
        // The agent hit something it can't or shouldn't do itself (captcha / login / 2FA / genuine
        // uncertainty). Mint a scoped capability token, ping Klive with a remote-desktop deep link + raise
        // the website card, then BLOCK (heartbeating, no hard timeout) until the operator finishes — at which
        // point the agent auto-resumes. Reuses the same screen-stream + input routes as the Admin remote
        // desktop, just authorized by the token instead of a password.
        private async Task<ComputerToolResult> RequestHumanAsync(JsonElement a, CancellationToken ct, Action<HostControlProgress> onProgress)
        {
            var guard = OsGuard();
            if (guard != null) return guard;

            var reason = Str(a, "reason");
            if (string.IsNullOrWhiteSpace(reason))
                reason = "I hit something I need you for (captcha / login / verification). Please take over for a moment.";
            int maxMinutes = Math.Clamp(IntOr(a, "maxMinutes", await GetIntOmniSetting("KliveAgent_HumanHandoffMaxMinutes", 20)), 1, 240);
            int idleResolveMs = Math.Max(2000, await GetIntOmniSetting("KliveAgent_HumanHandoffIdleResolveMs", 4000));

            // Best-effort: keep the obstacle in the foreground so the operator lands right on it.
            try
            {
                var fg = NativeInput.GetForegroundWindowInfo();
                if (fg != null) { await inputLock.WaitAsync(ct); try { NativeInput.FocusWindow(fg.Value.Handle, maximize: false); } finally { inputLock.Release(); } }
            }
            catch { }

            // Mint the scoped token + register the pending handoff (the token IS the dictionary key).
            var token = MintHandoffToken();
            var handoff = new PendingHandoff { Token = token, ApprovalId = Guid.NewGuid().ToString("N"), Reason = reason };
            Interlocked.Exchange(ref handoff.LastInputUtcTicks, DateTime.UtcNow.Ticks);
            handoffs[token] = handoff;
            var solveUrl = $"https://klive.uk/shared/solve?token={token}";

            byte[]? annotated = null;
            try { var cap = capturer.CaptureVirtualScreen(1366, 55); annotated = capturer.Annotate(cap.Jpeg, "NEEDS YOU: " + Trim(reason, 60)); } catch { }

            var card = new PendingApproval
            {
                ApprovalId = handoff.ApprovalId,
                Message = reason,
                FrameBase64 = annotated != null ? Convert.ToBase64String(annotated) : null,
                Status = "pending",
                Kind = "intervention",
                SolveUrl = solveUrl
            };
            onProgress(new HostControlProgress
            {
                Note = "Waiting for you to take over: " + reason,
                Approval = card,
                AnnotatedFrameJpeg = annotated,
                Activity = new AgentActivityEvent { Kind = "approval", Text = "human handoff: " + Trim(reason, 40) }
            });

            await SendDiscordHandoffAsync(reason, solveUrl);
            await ServiceLog($"[HostControl] request_human: handoff opened — {Trim(reason, 80)}");

            var deadline = DateTime.UtcNow.AddMinutes(maxMinutes);
            var sw = Stopwatch.StartNew();
            int lastHeartbeat = 0;
            string outcome = "timeout";
            try
            {
                while (handoff.IsPending)
                {
                    if (ct.IsCancellationRequested) { ResolveHandoff(handoff, "cancelled"); outcome = "cancelled"; break; }
                    if (DateTime.UtcNow >= deadline) { ResolveHandoff(handoff, "timeout"); outcome = "timeout"; break; }

                    await Task.WhenAny(handoff.Completion.Task, Task.Delay(1000));
                    if (handoff.Completion.Task.IsCompletedSuccessfully) { outcome = handoff.Completion.Task.Result; break; }

                    // Auto-resume: once the operator has touched it and then gone idle, they're done — resume.
                    // (If they merely paused, the agent will re-observe a still-present obstacle and can call
                    // request_human again, so an early resume is self-healing, never destructive.)
                    if (Interlocked.CompareExchange(ref handoff.Interacted, 0, 0) == 1)
                    {
                        long idleMs = (DateTime.UtcNow.Ticks - Interlocked.Read(ref handoff.LastInputUtcTicks)) / TimeSpan.TicksPerMillisecond;
                        if (idleMs >= idleResolveMs) { ResolveHandoff(handoff, "done"); outcome = "done"; break; }
                    }

                    if (sw.ElapsedMilliseconds - lastHeartbeat >= 4000)
                    {
                        lastHeartbeat = (int)sw.ElapsedMilliseconds;
                        onProgress(new HostControlProgress
                        {
                            Note = $"Waiting for you to solve it… ({sw.ElapsedMilliseconds / 1000}s).",
                            Approval = card,
                            Activity = new AgentActivityEvent { Kind = "wait", Text = "awaiting human takeover" }
                        });
                    }
                }
            }
            finally
            {
                handoffs.TryRemove(token, out _);
                card.Status = (outcome == "done") ? "approved" : "denied";
                onProgress(new HostControlProgress
                {
                    Note = $"Handoff {outcome}.",
                    Approval = card,
                    Activity = new AgentActivityEvent { Kind = outcome == "done" ? "action" : "error", Text = "handoff " + outcome }
                });
            }

            var result = await CaptureClipAsync("active", "after human handoff", null, null, null, ct);
            result.Text = outcome switch
            {
                "done" => "A human took over and finished (the captcha/login/verification was handled). " + result.Text + " Re-read the screen and CONTINUE the task from here.",
                "cancelled" => "Human handoff cancelled (run stopped).",
                _ => $"No human completed the handoff within {maxMinutes} min — the obstacle may still be present. " + result.Text + " Decide whether to wait again (request_human) or stop and tell the user."
            };
            return result;
        }

        private async Task SendDiscordHandoffAsync(string reason, string solveUrl)
        {
            try
            {
                await ExecuteServiceMethod<Omnipotent.Services.KliveBot_Discord.KliveBotDiscord>("SendMessageToKlives",
                    $"🖐️ **KliveAgent needs you.** {Trim(reason, 200)}\nTake over on the machine (remote desktop): {solveUrl}");
            }
            catch { /* notification is best-effort; the website card is the primary channel */ }
        }

        // ── Coordinate mapping ──
        private (int x, int y) MapToPhysical(int modelX, int modelY)
        {
            CoordFrame? f;
            lock (frameLock) f = lastFrame;
            if (f == null)
            {
                // Establish a frame on demand (the model normally screenshots first). Synchronous single frame
                // — this is only a coordinate-mapping fallback, not a perception result fed to the model.
                CaptureSettledFrame("active", "auto");
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

        /// <summary>A cheap perceptual signature of the WHOLE screen (a 32×32 grayscale thumbnail) for
        /// change detection — used by wait_for's screen watcher. Robust to tiny noise (a ticking clock barely
        /// moves the mean) while catching real changes. Null if capture fails.</summary>
        public byte[]? CaptureScreenSignature()
        {
            try { return ScreenCapturer.GrayThumb(capturer.CaptureVirtualScreen(480, 35).Jpeg); }
            catch { return null; }
        }

        /// <summary>Mean perceptual delta (0..255) between two screen signatures from <see cref="CaptureScreenSignature"/>.</summary>
        public static double ScreenSignatureDelta(byte[] a, byte[] b) => ScreenCapturer.ThumbDelta(a, b);
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

        // ── Held inputs (press-and-hold) + modifier combos ──
        private static string[] NormalizeModifiers(string[]? mods) =>
            mods == null ? Array.Empty<string>() : mods.Where(m => !string.IsNullOrWhiteSpace(m)).ToArray();

        private static string ModLabel(string[] mods) => mods.Length == 0 ? "" : string.Join("+", mods) + "+";

        /// <summary>Hold the given modifier keys for the duration of the action, then release (reverse order).</summary>
        private static void WithModifiers(string[] mods, Action action)
        {
            foreach (var m in mods) NativeInput.KeyDown(m);
            try { action(); }
            finally { for (int i = mods.Length - 1; i >= 0; i--) NativeInput.KeyUp(mods[i]); }
        }

        private async Task<ComputerToolResult> HoldMouseAsync(JsonElement a, CancellationToken ct, Action<HostControlProgress> onProgress, bool down)
        {
            var guard = OsGuard();
            if (guard != null) return guard;
            var button = ParseButton(Str(a, "button"));
            bool hasXY = a.TryGetProperty("x", out _) && a.TryGetProperty("y", out _);
            int x = IntOr(a, "x", 0), y = IntOr(a, "y", 0);

            var prof = Human();
            await inputLock.WaitAsync(ct);
            try
            {
                var (px, py) = hasXY ? MapToPhysical(x, y) : NativeInput.GetCursorPosition();
                if (down) NativeInput.MouseButtonDown(px, py, button, prof, ct); else NativeInput.MouseButtonUp(px, py, button, prof, ct);
                if (prof == null) await Task.Delay(120, ct);
            }
            finally { inputLock.Release(); }

            lock (holdLock) { if (down) heldButtons.Add(button); else heldButtons.Remove(button); lastHoldUtc = DateTime.UtcNow; }
            var label = $"{button.ToString().ToLowerInvariant()} button {(down ? "down" : "up")}";
            onProgress(new HostControlProgress { Note = label, Activity = new AgentActivityEvent { Kind = "action", Text = label } });
            var result = await CaptureClipAsync("active", label, hasXY ? x : (int?)null, hasXY ? y : (int?)null, null, ct);
            result.Text = (down ? $"Pressed and HOLDING the {button.ToString().ToLowerInvariant()} button — move (computer_move) then computer_mouse_up to release. " : $"Released the {button.ToString().ToLowerInvariant()} button. ") + result.Text;
            return result;
        }

        private async Task<ComputerToolResult> HoldKeyAsync(JsonElement a, CancellationToken ct, Action<HostControlProgress> onProgress, bool down)
        {
            var guard = OsGuard();
            if (guard != null) return guard;
            var key = Str(a, "key");
            if (string.IsNullOrWhiteSpace(key)) return ComputerToolResult.Fail("Provide 'key'.");

            bool ok;
            await inputLock.WaitAsync(ct);
            try { ok = down ? NativeInput.KeyDown(key) : NativeInput.KeyUp(key); await Task.Delay(80, ct); }
            finally { inputLock.Release(); }
            if (!ok) return ComputerToolResult.Fail($"Unrecognized key '{key}'.");

            lock (holdLock) { if (down) heldKeys.Add(key); else heldKeys.Remove(key); lastHoldUtc = DateTime.UtcNow; }
            var label = $"key {key} {(down ? "down" : "up")}";
            onProgress(new HostControlProgress { Note = label, Activity = new AgentActivityEvent { Kind = "action", Text = label } });
            var result = await CaptureClipAsync("active", label, null, null, null, ct);
            result.Text = (down ? $"Holding '{key}' down — do actions then computer_key_up to release. " : $"Released '{key}'. ") + result.Text;
            return result;
        }

        /// <summary>Release every held mouse button and key. Used by computer_release_all and by the safety
        /// watchdog when a run leaves something held after going idle, so nothing ever stays physically stuck.</summary>
        public async Task ReleaseHeldInputsAsync(bool auto)
        {
            List<Omnipotent.Threading.MouseButton> btns;
            List<string> keys;
            lock (holdLock)
            {
                btns = heldButtons.ToList();
                keys = heldKeys.ToList();
                heldButtons.Clear();
                heldKeys.Clear();
            }
            if (btns.Count == 0 && keys.Count == 0) return;
            var (cx, cy) = NativeInput.GetCursorPosition();
            await inputLock.WaitAsync();
            try
            {
                foreach (var b in btns) NativeInput.MouseButtonUp(cx, cy, b);
                foreach (var k in keys) NativeInput.KeyUp(k);
            }
            finally { inputLock.Release(); }
            if (auto) await ServiceLog($"[HostControl] Safety-released held inputs (buttons: {btns.Count}, keys: {keys.Count}).");
        }

        private async Task HeldInputSafetyLoopAsync()
        {
            while (true)
            {
                try
                {
                    await Task.Delay(2000);
                    int safetyMs = Math.Max(3000, await GetIntOmniSetting("KliveAgent_HeldInputSafetyMs", 12000));
                    bool stale;
                    lock (holdLock) stale = (heldButtons.Count > 0 || heldKeys.Count > 0) && (DateTime.UtcNow - lastHoldUtc).TotalMilliseconds > safetyMs;
                    if (stale) await ReleaseHeldInputsAsync(auto: true);
                }
                catch { }
            }
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
        private static double DblOr(JsonElement a, string name, double def) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) ? v : def;
        private static string[]? Strs(JsonElement a, string name) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                : null;
        private static string Trim(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? (s ?? "") : s.Substring(0, max) + "…";
    }
}
