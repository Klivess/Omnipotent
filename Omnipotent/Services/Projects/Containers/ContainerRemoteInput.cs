using System.Text.Json;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Klives' remote-control input protocol for container desktops — the container sibling of
    /// HostControl's /kliveagent/remote/input handler, so the website can reuse one wire format
    /// for both surfaces. Each WebSocket text frame is one JSON event:
    ///   {t:"move|down|up|click|dblclick|scroll|text|key|keydown|keyup", x/y normalized 0..1,
    ///    button:"left|middle|right", clicks, dy/dx (scroll notches), text, keys:[..]}.
    /// Parsing is separated from application so the protocol is unit-testable without an RFB socket.
    /// </summary>
    public static class ContainerRemoteInput
    {
        public sealed record InputEvent(string Type, double X, double Y, int Button, int Clicks,
            int Dy, int Dx, string? Text, string[] Keys);

        /// <summary>One JSON text frame → a typed event, or null for anything malformed. A null
        /// is ignored by the session loop — a bad frame must never end Klives' control session.</summary>
        public static InputEvent? Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            JsonElement ev;
            try { ev = JsonDocument.Parse(json).RootElement; } catch { return null; }
            if (ev.ValueKind != JsonValueKind.Object) return null;
            string type = (Str(ev, "t") ?? "").Trim().ToLowerInvariant();
            if (type.Length == 0) return null;
            string[] keys = Strs(ev, "keys");
            if (keys.Length == 0 && Str(ev, "key") is { Length: > 0 } single) keys = new[] { single };
            return new InputEvent(
                Type: type,
                X: Math.Clamp(Dbl(ev, "x", 0), 0, 1),
                Y: Math.Clamp(Dbl(ev, "y", 0), 0, 1),
                Button: ParseButton(Str(ev, "button")),
                Clicks: Math.Clamp(Int(ev, "clicks", 1), 1, 3),
                Dy: Math.Clamp(Int(ev, "dy", 0), -20, 20),
                Dx: Math.Clamp(Int(ev, "dx", 0), -20, 20),
                Text: Str(ev, "text"),
                Keys: keys);
        }

        /// <summary>Normalized [0,1] viewer coordinates → container framebuffer pixels.</summary>
        public static (int X, int Y) ToPixels(double nx, double ny, int width, int height) =>
            ((int)Math.Round(Math.Clamp(nx, 0, 1) * Math.Max(0, width - 1)),
             (int)Math.Round(Math.Clamp(ny, 0, 1) * Math.Max(0, height - 1)));

        /// <summary>Mouse button number from the website's name; unknown names fall back to left.</summary>
        internal static int ParseButton(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
        {
            "middle" => 2,
            "right" => 3,
            _ => 1,
        };

        /// <summary>
        /// Replays one parsed event on the container's VNC transport. Returns false when the event
        /// type is unknown (silently ignored). May throw for transport failures or unknown key
        /// names — callers should swallow per-event and keep the session alive.
        /// </summary>
        public static async Task<bool> ApplyAsync(VncTransport transport, InputEvent ev, CancellationToken ct)
        {
            var (x, y) = ToPixels(ev.X, ev.Y, transport.Width, transport.Height);
            switch (ev.Type)
            {
                case "move": await transport.MoveMouseAsync(x, y, ct); return true;
                case "down": await transport.MouseDownAsync(x, y, ev.Button, ct); return true;
                case "up": await transport.MouseUpAsync(x, y, ev.Button, ct); return true;
                case "click": await transport.ClickAsync(x, y, ev.Button, ev.Clicks, ct); return true;
                case "dblclick": await transport.ClickAsync(x, y, ev.Button, 2, ct); return true;
                case "scroll": await transport.ScrollAsync(x, y, ev.Dy, ev.Dx, ct); return true;
                case "text":
                    if (!string.IsNullOrEmpty(ev.Text)) await transport.TypeTextAsync(ev.Text, charDelayMs: 8, ct: ct);
                    return true;
                case "key":
                    if (ev.Keys.Length > 0) await transport.KeyChordAsync(string.Join('+', ev.Keys), ct: ct);
                    return true;
                case "keydown":
                    if (ev.Keys.Length > 0) await transport.KeyDownAsync(ev.Keys[0], ct);
                    return true;
                case "keyup":
                    if (ev.Keys.Length > 0) await transport.KeyUpAsync(ev.Keys[0], ct);
                    return true;
                default: return false;
            }
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string[] Strs(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s) list.Add(s);
            return list.ToArray();
        }

        private static double Dbl(JsonElement e, string name, double fallback) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : fallback;

        private static int Int(JsonElement e, string name, int fallback) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;
    }
}
