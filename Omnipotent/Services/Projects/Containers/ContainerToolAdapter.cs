using System.Runtime.Versioning;
using System.Text.Json;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// The container-native computer-use tool surface — the VNC sibling of HostControl's
    /// ExecuteToolAsync. Same tool names and argument shapes as the Win32 path (so agent-facing
    /// tool contracts and the model's mental model are identical), but every handler drives a
    /// <see cref="VncTransport"/> instead of native input. HostControl's approval/gating and
    /// secret-substitution concerns are reused conceptually: secret resolution is injected as a
    /// delegate (P3's project-scoped vault provides it), keeping this class free of the vault's
    /// key material.
    ///
    /// One <see cref="VncTransport"/> is pooled per container. The pool is created by the
    /// service (which knows host ports from the registry).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class ContainerToolAdapter
    {
        private readonly VncTransport transport;
        private readonly InputLockCoordinator? inputLock;
        private readonly string containerID;
        private readonly string? agentID;
        /// <summary>Resolves {SECRET} tokens to plaintext immediately before typing (project vault, P3). Optional.</summary>
        private readonly Func<string, Task<string>>? resolveSecretsAsync;

        public ContainerToolAdapter(
            VncTransport transport,
            string containerID,
            string? agentID,
            InputLockCoordinator? inputLock = null,
            Func<string, Task<string>>? resolveSecretsAsync = null)
        {
            this.transport = transport;
            this.containerID = containerID;
            this.agentID = agentID;
            this.inputLock = inputLock;
            this.resolveSecretsAsync = resolveSecretsAsync;
        }

        /// <summary>Result of a container tool: text observation + optional JPEG frame for vision/website.</summary>
        public sealed record ContainerToolResult(bool Success, string Text, byte[]? Jpeg = null)
        {
            public static ContainerToolResult Ok(string text, byte[]? jpeg = null) => new(true, text, jpeg);
            public static ContainerToolResult Fail(string text) => new(false, text);
        }

        /// <summary>Runs one computer_* tool against the container. Mirrors HostControlManager.RunAsync's dispatch.</summary>
        public async Task<ContainerToolResult> ExecuteAsync(string tool, string? argsJson, CancellationToken ct = default)
        {
            JsonElement a;
            try
            {
                var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson!);
                a = doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement : JsonDocument.Parse("{}").RootElement;
            }
            catch { a = JsonDocument.Parse("{}").RootElement; }

            // Shared-desktop input arbitration: any mutating tool requires (and renews) the lock.
            if (IsMutating(tool) && inputLock != null && agentID != null)
            {
                if (!inputLock.TryAcquire(containerID, agentID))
                    return ContainerToolResult.Fail($"Desktop is currently controlled by agent {inputLock.CurrentHolder(containerID)}. Wait for it to release the input lock, or work on another task.");
            }

            try
            {
                switch (tool)
                {
                    case "computer_screenshot":
                        return await ScreenshotAsync("Captured desktop.", ct);

                    case "computer_move":
                        await transport.MoveMouseAsync(Int(a, "x"), Int(a, "y"), ct);
                        return await ScreenshotAsync($"Moved to ({Int(a, "x")},{Int(a, "y")}).", ct);

                    case "computer_click":
                        await transport.ClickAsync(Int(a, "x"), Int(a, "y"), ParseButton(Str(a, "button")), Math.Max(1, Int(a, "clicks", 1)), ct);
                        return await ScreenshotAsync($"Clicked ({Int(a, "x")},{Int(a, "y")}).", ct);

                    case "computer_drag":
                        await transport.DragAsync(Int(a, "fromX"), Int(a, "fromY"), Int(a, "toX"), Int(a, "toY"), ParseButton(Str(a, "button")), ct);
                        return await ScreenshotAsync("Dragged.", ct);

                    case "computer_mouse_down":
                        await transport.MouseDownAsync(Int(a, "x"), Int(a, "y"), ParseButton(Str(a, "button")), ct);
                        return ContainerToolResult.Ok("Mouse button held down.");

                    case "computer_mouse_up":
                        await transport.MouseUpAsync(Int(a, "x"), Int(a, "y"), ParseButton(Str(a, "button")), ct);
                        return await ScreenshotAsync("Mouse button released.", ct);

                    case "computer_scroll":
                    {
                        int amount = Math.Abs(Int(a, "amount", 5)); if (amount == 0) amount = 5;
                        int dy = 0, dx = 0;
                        switch ((Str(a, "direction") ?? "down").Trim().ToLowerInvariant())
                        {
                            case "up": dy = amount; break;
                            case "down": dy = -amount; break;
                            case "left": dx = -amount; break;
                            case "right": dx = amount; break;
                        }
                        await transport.ScrollAsync(Int(a, "x", transport.Width / 2), Int(a, "y", transport.Height / 2), dy, dx, ct);
                        return await ScreenshotAsync($"Scrolled {(Str(a, "direction") ?? "down")}.", ct);
                    }

                    case "computer_type":
                    {
                        string text = Str(a, "text") ?? "";
                        if (resolveSecretsAsync != null) text = await resolveSecretsAsync(text);
                        await transport.TypeTextAsync(text, ct);
                        return await ScreenshotAsync("Typed text.", ct);
                    }

                    case "computer_key":
                    {
                        string chord = Str(a, "key") ?? Str(a, "keys") ?? "";
                        if (string.IsNullOrWhiteSpace(chord)) return ContainerToolResult.Fail("Provide 'key' (e.g. 'enter' or 'ctrl+l').");
                        await transport.KeyChordAsync(chord, ct);
                        return await ScreenshotAsync($"Pressed {chord}.", ct);
                    }

                    case "computer_key_down":
                        await transport.KeyDownAsync(Str(a, "key") ?? "", ct);
                        return ContainerToolResult.Ok($"Holding {Str(a, "key")}.");

                    case "computer_key_up":
                        await transport.KeyUpAsync(Str(a, "key") ?? "", ct);
                        return await ScreenshotAsync($"Released {Str(a, "key")}.", ct);

                    case "computer_release_all":
                        await transport.ReleaseAllAsync(ct);
                        if (inputLock != null && agentID != null) inputLock.Release(containerID, agentID);
                        return await ScreenshotAsync("Released all held inputs and the input lock.", ct);

                    case "computer_wait":
                    {
                        int ms = Math.Clamp(Int(a, "ms", 1000), 0, 30000);
                        await Task.Delay(ms, ct);
                        return await ScreenshotAsync($"Waited {ms}ms.", ct);
                    }

                    default:
                        return ContainerToolResult.Fail($"Unknown or unsupported container tool '{tool}'.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return ContainerToolResult.Fail("Action cancelled.");
            }
            catch (Exception ex)
            {
                return ContainerToolResult.Fail($"{tool} error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task<ContainerToolResult> ScreenshotAsync(string prefix, CancellationToken ct)
        {
            var (bgra, w, h) = await transport.CaptureFrameAsync(ct);
            byte[] jpeg = VncFrameEncoder.EncodeJpeg(bgra, w, h);
            return ContainerToolResult.Ok($"{prefix} Desktop is {w}x{h}px. (0,0 = top-left.)", jpeg);
        }

        private static bool IsMutating(string tool) => tool is not ("computer_screenshot" or "computer_wait");

        private static int ParseButton(string? b) => (b ?? "left").Trim().ToLowerInvariant() switch
        {
            "middle" => 2,
            "right" => 3,
            _ => 1,
        };

        private static string? Str(JsonElement a, string name)
            => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int Int(JsonElement a, string name, int fallback = 0)
        {
            if (!a.TryGetProperty(name, out var v)) return fallback;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetInt32(out int i) ? i : (int)v.GetDouble(),
                JsonValueKind.String => int.TryParse(v.GetString(), out int s) ? s : fallback,
                _ => fallback,
            };
        }
    }
}
