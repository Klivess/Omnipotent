using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveLLM;
using Tesseract;

namespace Omnipotent.Services.ComputerControl
{
    /// <summary>
    /// Target-neutral contract for visual computer control.  The host desktop and project VNC
    /// desktops deliberately expose the same vocabulary while advertising the few operations
    /// that are unavailable on a particular target.
    /// </summary>
    public interface IComputerController
    {
        ComputerCapabilities Capabilities { get; }
        Task<ComputerActionResult> ExecuteComputerActionAsync(ComputerActionRequest request, CancellationToken ct = default);
    }

    public sealed class ComputerCapabilities
    {
        public bool SupportsOcr { get; init; } = true;
        public bool SupportsWindowControl { get; init; }
        public bool SupportsBrowserControl { get; init; }
        public bool SupportsClipboard { get; init; }
        public bool SupportsAppLaunch { get; init; }
        public bool SupportsRelativeMouse { get; init; }
        public bool SupportsHumanization { get; init; }
        public bool SupportsMotionFrames { get; init; }
        public IReadOnlySet<string> SupportedTools { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        public bool Supports(string toolName) => SupportedTools.Count == 0 || SupportedTools.Contains(toolName);
    }

    public sealed record ComputerActionRequest(string ToolName, string ArgumentsJson = "{}", string? ActorId = null, string? WakeId = null);

    public sealed class ComputerObservation
    {
        public byte[]? FinalFrameJpeg { get; init; }
        public IReadOnlyList<ComputerFrame> Frames { get; init; } = Array.Empty<ComputerFrame>();
        public int Width { get; init; }
        public int Height { get; init; }
        public bool IsSettled { get; init; }
    }

    public sealed class ComputerFrame
    {
        public byte[] Jpeg { get; init; } = Array.Empty<byte>();
        public int OffsetMs { get; init; }
        public bool IsSettled { get; init; }
        public bool HasCoordinateGrid { get; init; }
    }

    public sealed class ComputerActionResult
    {
        public bool Success { get; init; }
        public string Text { get; init; } = string.Empty;
        public string? Error { get; init; }
        public string AuditSummary { get; init; } = string.Empty;
        public ComputerObservation? Observation { get; init; }

        public static ComputerActionResult Fail(string text, string? auditSummary = null) => new()
        {
            Success = false, Text = text, Error = text, AuditSummary = auditSummary ?? text
        };
    }

    public sealed record ComputerTextMatch(string Text, int X, int Y, int Width, int Height, float Confidence)
    {
        public int CentreX => X + Width / 2;
        public int CentreY => Y + Height / 2;
    }

    /// <summary>Shared redaction and argument helpers.  Tool logs must not turn a vault-backed
    /// computer_type call into a second secret store.</summary>
    public static class ComputerAudit
    {
        private static readonly HashSet<string> SensitiveFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "text", "value", "password", "secret", "token", "authorization", "cookie", "clipboard"
        };

        public static string Describe(string toolName, string? argumentsJson, int max = 220)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return toolName;
                var parts = doc.RootElement.EnumerateObject().Take(5).Select(p =>
                    SensitiveFields.Contains(p.Name) ? $"{p.Name}=<redacted>" :
                    string.Equals(p.Name, "url", StringComparison.OrdinalIgnoreCase) ? $"url={RedactUrl(p.Value.ToString())}" :
                    $"{p.Name}={Truncate(p.Value.ToString(), 60)}");
                return Truncate($"{toolName}({string.Join(", ", parts)})", max);
            }
            catch { return toolName; }
        }

        public static string Truncate(string? value, int max) => string.IsNullOrEmpty(value) ? string.Empty :
            value.Length <= max ? value : value[..max] + "…";

        private static string RedactUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return Truncate(value, 60);
            if (string.IsNullOrEmpty(uri.Query)) return Truncate(uri.GetLeftPart(UriPartial.Path), 60);
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part =>
            {
                int equals = part.IndexOf('=');
                string key = Uri.UnescapeDataString(equals < 0 ? part : part[..equals]);
                return SensitiveFields.Contains(key) ? Uri.EscapeDataString(key) + "=<redacted>" : part;
            });
            return Truncate(uri.GetLeftPart(UriPartial.Path) + "?" + string.Join("&", query), 60);
        }
    }

    /// <summary>Image utilities shared by the Win32 and VNC paths.  Grid rendering intentionally
    /// happens after a screenshot has been captured: input coordinates always stay in image space.</summary>
    public static class ComputerVision
    {
        private static readonly SemaphoreSlim OcrGate = new(1, 1);
        private static TesseractEngine? ocrEngine;
        private static bool ocrUnavailable;

        public static byte[] AddCoordinateGrid(byte[] jpeg, int step = 100)
        {
            if (jpeg == null || jpeg.Length == 0) return jpeg ?? Array.Empty<byte>();
            try
            {
                using var input = new MemoryStream(jpeg);
                using var source = (Bitmap)Image.FromStream(input);
                using var bmp = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
                using var g = Graphics.FromImage(bmp);
                g.DrawImage(source, 0, 0, bmp.Width, bmp.Height);
                g.SmoothingMode = SmoothingMode.None;
                using var faint = new Pen(Color.FromArgb(115, 0, 210, 255), 1);
                using var strong = new Pen(Color.FromArgb(205, 255, 210, 0), 2);
                using var font = new Font(FontFamily.GenericMonospace, 10, FontStyle.Bold);
                for (int x = 0; x < bmp.Width; x += Math.Max(20, step))
                {
                    bool major = x % (step * 5) == 0;
                    g.DrawLine(major ? strong : faint, x, 0, x, bmp.Height);
                    if (major) DrawLabel(g, x.ToString(), x + 2, 2, font);
                }
                for (int y = 0; y < bmp.Height; y += Math.Max(20, step))
                {
                    bool major = y % (step * 5) == 0;
                    g.DrawLine(major ? strong : faint, 0, y, bmp.Width, y);
                    if (major) DrawLabel(g, y.ToString(), 2, y + 2, font);
                }
                using var ms = new MemoryStream();
                var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(Encoder.Quality, 80L);
                bmp.Save(ms, encoder, ep);
                return ms.ToArray();
            }
            catch { return jpeg; }
        }

        public static double FrameDelta(byte[]? first, byte[]? second)
        {
            if (first == null || second == null || first.Length == 0 || second.Length == 0) return 255;
            try
            {
                using var aStream = new MemoryStream(first);
                using var bStream = new MemoryStream(second);
                using var a = new Bitmap(Image.FromStream(aStream), new Size(32, 32));
                using var b = new Bitmap(Image.FromStream(bStream), new Size(32, 32));
                double sum = 0;
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 32; x++)
                    {
                        var ac = a.GetPixel(x, y); var bc = b.GetPixel(x, y);
                        double al = ac.R * .299 + ac.G * .587 + ac.B * .114;
                        double bl = bc.R * .299 + bc.G * .587 + bc.B * .114;
                        sum += Math.Abs(al - bl);
                    }
                return sum / (32 * 32);
            }
            catch { return 255; }
        }

        public static async Task<IReadOnlyList<ComputerTextMatch>> FindTextAsync(byte[] jpeg, string text, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text) || jpeg == null || jpeg.Length == 0) return Array.Empty<ComputerTextMatch>();
            var engine = await GetOcrEngineAsync(ct);
            if (engine == null) return Array.Empty<ComputerTextMatch>();
            await OcrGate.WaitAsync(ct);
            try
            {
                using var pix = Pix.LoadFromMemory(jpeg);
                using var page = engine.Process(pix);
                using var iter = page.GetIterator();
                var matches = new List<ComputerTextMatch>();
                iter.Begin();
                do
                {
                    string? line = iter.GetText(PageIteratorLevel.TextLine)?.Trim();
                    if (string.IsNullOrWhiteSpace(line) || !line.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var box)) continue;
                    matches.Add(new ComputerTextMatch(line, box.X1, box.Y1, box.Width, box.Height,
                        iter.GetConfidence(PageIteratorLevel.TextLine)));
                } while (iter.Next(PageIteratorLevel.TextLine));
                return matches;
            }
            catch { return Array.Empty<ComputerTextMatch>(); }
            finally { OcrGate.Release(); }
        }

        private static void DrawLabel(Graphics g, string text, int x, int y, Font font)
        {
            var size = g.MeasureString(text, font);
            g.FillRectangle(Brushes.Black, x, y, size.Width + 3, size.Height + 1);
            g.DrawString(text, font, Brushes.White, x + 1, y);
        }

        private static async Task<TesseractEngine?> GetOcrEngineAsync(CancellationToken ct)
        {
            if (ocrEngine != null) return ocrEngine;
            if (ocrUnavailable) return null;
            await OcrGate.WaitAsync(ct);
            try
            {
                if (ocrEngine != null) return ocrEngine;
                string dataDir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniscienceDirectory), "ocr", "tessdata");
                if (!File.Exists(Path.Combine(dataDir, "eng.traineddata")))
                {
                    ocrUnavailable = true;
                    return null;
                }
                ocrEngine = new TesseractEngine(dataDir, "eng", EngineMode.Default);
                return ocrEngine;
            }
            catch { ocrUnavailable = true; return null; }
            finally { OcrGate.Release(); }
        }
    }

    /// <summary>Canonical visual tool definitions.  Controllers filter this catalogue using their
    /// capabilities; host-only approval and secret-management tools remain in KliveAgentBrain.</summary>
    public static class VisualComputerToolCatalog
    {
        public static List<HFWrapper.HFTool> Build(ComputerCapabilities capabilities)
        {
            HFWrapper.HFTool Tool(string name, string description, object parameters) => new()
            {
                function = new HFWrapper.HFFunctionDefinition { name = name, description = description, parameters = parameters }
            };
            object Obj(object properties, params string[] required) => new { type = "object", properties, required };
            object Str(string description) => new { type = "string", description };
            object Num(string description) => new { type = "integer", description };
            var stringArray = new { type = "array", items = new { type = "string" } };
            var all = new List<HFWrapper.HFTool>
            {
                Tool("computer_screenshot", "Capture the current visual state. The final image has a coordinate grid; measure from it before clicking.", Obj(new { target = Str("active | fullscreen | browser where supported") })),
                Tool("computer_find_text", "Use local OCR on a fresh screenshot. Returns matching visible text and its clickable centre coordinates; never use it to solve a CAPTCHA.", Obj(new { text = Str("Visible text to find."), occurrence = Num("0-based match index, default 0.") }, "text")),
                Tool("computer_click_text", "Find visible text with local OCR and click its centre. Reversible UI action only; use the normal approval policy for send/pay/submit actions.", Obj(new { text = Str("Visible text to click."), occurrence = Num("0-based match index, default 0."), button = Str("left | middle | right"), clicks = Num("1 or 2") }, "text")),
                Tool("computer_move", "Move pointer to screenshot coordinates.", Obj(new { x = Num("X pixel"), y = Num("Y pixel") }, "x", "y")),
                Tool("computer_click", "Click screenshot coordinates after observing them.", Obj(new { x = Num("X pixel"), y = Num("Y pixel"), button = Str("left | middle | right"), clicks = Num("1 or 2"), modifiers = stringArray }, "x", "y")),
                Tool("computer_drag", "Drag between screenshot coordinates.", Obj(new { fromX = Num("Start X"), fromY = Num("Start Y"), toX = Num("End X"), toY = Num("End Y"), button = Str("left | middle | right"), modifiers = stringArray }, "fromX", "fromY", "toX", "toY")),
                Tool("computer_mouse_down", "Press and hold a mouse button.", Obj(new { x = Num("X pixel"), y = Num("Y pixel"), button = Str("left | middle | right") })),
                Tool("computer_mouse_up", "Release a held mouse button.", Obj(new { x = Num("X pixel"), y = Num("Y pixel"), button = Str("left | middle | right") })),
                Tool("computer_scroll", "Scroll a pane, then observe its changed state.", Obj(new { direction = Str("up | down | left | right"), amount = Num("notches"), x = Num("optional X"), y = Num("optional Y") })),
                Tool("computer_type", "Type at current focus. Vault placeholders are resolved only at input time and never returned.", Obj(new { text = Str("Text to type") }, "text")),
                Tool("computer_key", "Press a key or chord. Supports key/keys, repeats, and holdMs.", Obj(new { key = Str("key or ctrl+l chord"), keys = stringArray, repeats = Num("repeat count"), holdMs = Num("hold duration") })),
                Tool("computer_key_down", "Press and hold one key.", Obj(new { key = Str("key") }, "key")),
                Tool("computer_key_up", "Release one held key.", Obj(new { key = Str("key") }, "key")),
                Tool("computer_release_all", "Release all held inputs and recover from a stuck gesture.", Obj(new { })),
                Tool("computer_wait", "Wait for visual change or a stable UI. maxMs is preferred; ms remains accepted for compatibility.", Obj(new { maxMs = Num("maximum wait milliseconds"), ms = Num("compatibility alias"), untilImageChange = new { type = "boolean" }, untilText = Str("visible text to wait for") })),
                Tool("computer_open_browser", "Open or focus the real browser and return an observed frame.", Obj(new { url = Str("optional URL") })),
                Tool("computer_navigate", "Navigate the real browser by URL, wait for the page to settle, then observe.", Obj(new { url = Str("absolute URL") }, "url")),
                Tool("computer_focus_window", "Focus a window by title or process where supported.", Obj(new { titleContains = Str("title fragment"), processName = Str("process name") })),
                Tool("computer_launch_app", "Launch an allowlisted GUI application and observe it.", Obj(new { path = Str("application"), shellName = Str("known application"), args = Str("arguments") })),
                Tool("computer_clipboard_get", "Read clipboard text where supported.", Obj(new { })),
                Tool("computer_clipboard_set", "Set clipboard text where supported.", Obj(new { text = Str("clipboard text") }, "text")),
            };
            return all.Where(t => capabilities.Supports(t.function.name)).ToList();
        }
    }
}
