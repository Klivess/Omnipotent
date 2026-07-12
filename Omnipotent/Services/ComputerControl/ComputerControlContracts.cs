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
        /// <summary>Target can run a bounded command in its own isolated terminal environment.
        /// False for the real host desktop; true only for Project desktop containers.</summary>
        public bool SupportsTerminalExecution { get; init; }
        public bool SupportsRelativeMouse { get; init; }
        public bool SupportsHumanization { get; init; }
        public bool SupportsMotionFrames { get; init; }
        public IReadOnlySet<string> SupportedTools { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        public bool Supports(string toolName)
        {
            // A non-empty set is an explicit allow-list. When a controller relies on the
            // capability flags instead, derive the optional surface from those flags instead
            // of accidentally advertising every tool.
            if (SupportedTools.Count > 0) return SupportedTools.Contains(toolName);
            return toolName switch
            {
                "computer_find_text" or "computer_click_text" => SupportsOcr,
                "computer_open_browser" or "computer_navigate" or "computer_browser_inspect" => SupportsBrowserControl,
                "computer_focus_window" => SupportsWindowControl,
                "computer_clipboard_get" or "computer_clipboard_set" => SupportsClipboard,
                "computer_launch_app" => SupportsAppLaunch,
                "computer_terminal" => SupportsTerminalExecution,
                _ => true,
            };
        }
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
            "text", "value", "password", "secret", "token", "authorization", "cookie", "clipboard",
            // Terminal input can contain inline credentials. Its exact body belongs neither in
            // the event log nor the audit summary (vault placeholders are not resolved here).
            "command"
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

        /// <summary>
        /// Locates on-screen text and returns clickable centres, ordered top-to-bottom / left-to-right.
        /// The reliability of the old path was poor for three compounding reasons, all fixed here:
        ///   1. Small UI glyphs on a compressed frame — the frame is greyscaled and upscaled so
        ///      Tesseract sees text near the ~30px it recognises best, and read losslessly.
        ///   2. Tesseract's default block layout dropping isolated buttons/labels — sparse-text
        ///      segmentation plus our own row grouping rebuilds phrases from scattered tokens.
        ///   3. Exact whole-line substring matching — a single misread or extra space missed
        ///      everything.  Matching is now whitespace-insensitive with a small edit-distance
        ///      fallback, so "Slgn ln" still resolves "Sign in".
        /// Input pixels are the framebuffer space callers click in; match boxes are mapped back to it.
        /// </summary>
        public static async Task<IReadOnlyList<ComputerTextMatch>> FindTextAsync(byte[] imageBytes, string text, CancellationToken ct = default)
        {
            if (imageBytes == null || imageBytes.Length == 0) return Array.Empty<ComputerTextMatch>();
            string needle = CompactText(text);
            if (needle.Length == 0) return Array.Empty<ComputerTextMatch>();
            var engine = await GetOcrEngineAsync(ct);
            if (engine == null) return Array.Empty<ComputerTextMatch>();
            await OcrGate.WaitAsync(ct);
            try
            {
                byte[] prepared = PreprocessForOcr(imageBytes, out double scale);
                var words = new List<OcrWord>();
                using (var pix = Pix.LoadFromMemory(prepared))
                using (var page = engine.Process(pix, PageSegMode.SparseText))
                using (var iter = page.GetIterator())
                {
                    iter.Begin();
                    do
                    {
                        string? word = iter.GetText(PageIteratorLevel.Word)?.Trim();
                        string compact = CompactText(word ?? string.Empty);
                        if (compact.Length == 0 || !iter.TryGetBoundingBox(PageIteratorLevel.Word, out var box)) continue;
                        // Map boxes out of the upscaled OCR space back into input (framebuffer) pixels.
                        words.Add(new OcrWord(word!.Trim(), compact,
                            (int)Math.Round(box.X1 / scale), (int)Math.Round(box.Y1 / scale),
                            (int)Math.Round(box.Width / scale), (int)Math.Round(box.Height / scale),
                            iter.GetConfidence(PageIteratorLevel.Word)));
                    } while (iter.Next(PageIteratorLevel.Word));
                }
                return MatchWords(words, needle);
            }
            catch { return Array.Empty<ComputerTextMatch>(); }
            finally { OcrGate.Release(); }
        }

        private readonly record struct OcrWord(string Text, string Compact, int X, int Y, int W, int H, float Confidence)
        {
            public int CenterY => Y + H / 2;
        }

        /// <summary>Greyscale + upscale a screen frame and re-encode losslessly for OCR.  Returns the
        /// scale factor so match boxes can be mapped back to input-image pixels.</summary>
        private static byte[] PreprocessForOcr(byte[] imageBytes, out double scale)
        {
            using var input = new MemoryStream(imageBytes);
            using var source = (Bitmap)Image.FromStream(input);
            // Aim for a ~1800px longest edge: 12-16px UI text lands near Tesseract's comfort zone
            // without ballooning already-large desktops or blurring tiny ones past recognition.
            int longest = Math.Max(source.Width, source.Height);
            scale = Math.Clamp(1800.0 / Math.Max(1, longest), 1.0, 3.0);
            int w = Math.Max(1, (int)Math.Round(source.Width * scale));
            int h = Math.Max(1, (int)Math.Round(source.Height * scale));

            using var scaled = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                var greyscale = new ColorMatrix(new[]
                {
                    new[] { 0.299f, 0.299f, 0.299f, 0f, 0f },
                    new[] { 0.587f, 0.587f, 0.587f, 0f, 0f },
                    new[] { 0.114f, 0.114f, 0.114f, 0f, 0f },
                    new[] { 0f, 0f, 0f, 1f, 0f },
                    new[] { 0f, 0f, 0f, 0f, 1f },
                });
                using var attrs = new ImageAttributes();
                attrs.SetColorMatrix(greyscale);
                g.DrawImage(source, new Rectangle(0, 0, w, h), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attrs);
            }
            using var ms = new MemoryStream();
            scaled.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }

        private static IReadOnlyList<ComputerTextMatch> MatchWords(List<OcrWord> words, string needle)
        {
            if (words.Count == 0) return Array.Empty<ComputerTextMatch>();
            int tolerance = Math.Max(1, (int)Math.Ceiling(needle.Length * 0.2));
            var exact = new List<ComputerTextMatch>();
            var fuzzy = new List<(ComputerTextMatch Match, int Distance)>();

            foreach (var row in GroupRows(words))
            {
                // Concatenate the row's tokens so a phrase matches regardless of spacing/word splits.
                string joined = string.Concat(row.Select(x => x.Compact));
                for (int idx = joined.IndexOf(needle, StringComparison.Ordinal); idx >= 0;
                     idx = idx + 1 <= joined.Length - needle.Length ? joined.IndexOf(needle, idx + 1, StringComparison.Ordinal) : -1)
                {
                    var span = SpanWords(row, idx, needle.Length);
                    if (span != null) exact.Add(BuildMatch(span));
                }

                // Slide a window of consecutive tokens and accept a small edit distance so isolated
                // misreads don't zero out the search.  Used only when nothing matched exactly.
                for (int i = 0; i < row.Count; i++)
                {
                    string acc = string.Empty;
                    for (int j = i; j < row.Count && j - i < 8; j++)
                    {
                        acc += row[j].Compact;
                        if (acc.Length < needle.Length - tolerance) continue;
                        if (acc.Length > needle.Length + tolerance) break;
                        int d = Levenshtein(acc, needle, tolerance);
                        if (d <= tolerance) fuzzy.Add((BuildMatch(row.GetRange(i, j - i + 1)), d));
                    }
                }
            }

            IEnumerable<ComputerTextMatch> results = exact.Count > 0
                ? exact
                : fuzzy.OrderBy(f => f.Distance).Select(f => f.Match);
            return results.OrderBy(m => m.Y).ThenBy(m => m.X).ToList();
        }

        /// <summary>Buckets words into visual rows (sorted top-to-bottom, then left-to-right within
        /// a row) so a phrase split into sparse tokens can be reassembled.</summary>
        private static List<List<OcrWord>> GroupRows(List<OcrWord> words)
        {
            var rows = new List<List<OcrWord>>();
            List<OcrWord>? current = null;
            int anchorY = 0, anchorH = 0;
            foreach (var w in words.OrderBy(x => x.CenterY))
            {
                if (current == null || Math.Abs(w.CenterY - anchorY) > Math.Max(anchorH, w.H) * 0.5)
                {
                    current = new List<OcrWord>();
                    rows.Add(current);
                    anchorY = w.CenterY;
                    anchorH = w.H;
                }
                current.Add(w);
            }
            foreach (var r in rows) r.Sort((a, b) => a.X.CompareTo(b.X));
            return rows;
        }

        /// <summary>Maps a character span in a row's concatenated text back to the words covering it.</summary>
        private static List<OcrWord>? SpanWords(List<OcrWord> row, int start, int length)
        {
            int end = start + length, pos = 0, s = -1, e = -1;
            for (int i = 0; i < row.Count; i++)
            {
                int wStart = pos, wEnd = pos + row[i].Compact.Length;
                if (wEnd > start && wStart < end) { if (s < 0) s = i; e = i; }
                pos = wEnd;
            }
            return s < 0 ? null : row.GetRange(s, e - s + 1);
        }

        private static ComputerTextMatch BuildMatch(List<OcrWord> span)
        {
            int x1 = span.Min(w => w.X), y1 = span.Min(w => w.Y);
            int x2 = span.Max(w => w.X + w.W), y2 = span.Max(w => w.Y + w.H);
            return new ComputerTextMatch(string.Join(" ", span.Select(w => w.Text)),
                x1, y1, x2 - x1, y2 - y1, span.Average(w => w.Confidence));
        }

        private static string CompactText(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var chars = new char[s.Length];
            int n = 0;
            foreach (char c in s) if (!char.IsWhiteSpace(c)) chars[n++] = char.ToLowerInvariant(c);
            return new string(chars, 0, n);
        }

        /// <summary>Bounded Levenshtein distance; returns <paramref name="max"/>+1 as soon as the
        /// edit distance is known to exceed the threshold, so long non-matches stay cheap.</summary>
        private static int Levenshtein(string a, string b, int max)
        {
            int n = a.Length, m = b.Length;
            if (Math.Abs(n - m) > max) return max + 1;
            var prev = new int[m + 1];
            var cur = new int[m + 1];
            for (int j = 0; j <= m; j++) prev[j] = j;
            for (int i = 1; i <= n; i++)
            {
                cur[0] = i;
                int rowMin = cur[0];
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                    if (cur[j] < rowMin) rowMin = cur[j];
                }
                if (rowMin > max) return max + 1;
                (prev, cur) = (cur, prev);
            }
            return prev[m];
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
                Tool("computer_mouse_down", "Press and hold a mouse button.", Obj(new { x = Num("X pixel"), y = Num("Y pixel"), button = Str("left | middle | right") }, "x", "y")),
                Tool("computer_mouse_up", "Release a held mouse button.", Obj(new { x = Num("X pixel"), y = Num("Y pixel"), button = Str("left | middle | right") }, "x", "y")),
                Tool("computer_scroll", "Scroll a pane, then observe its changed state.", Obj(new { direction = Str("up | down | left | right"), amount = Num("notches"), x = Num("optional X"), y = Num("optional Y") })),
                Tool("computer_type", "Type at current focus. Vault placeholders are resolved only at input time and never returned.", Obj(new { text = Str("Text to type") }, "text")),
                Tool("computer_key", "Press a key or chord. Supports key/keys, repeats, and holdMs.", Obj(new { key = Str("key or ctrl+l chord"), keys = stringArray, repeats = Num("repeat count"), holdMs = Num("hold duration") })),
                Tool("computer_key_down", "Press and hold one key.", Obj(new { key = Str("key") }, "key")),
                Tool("computer_key_up", "Release one held key.", Obj(new { key = Str("key") }, "key")),
                Tool("computer_release_all", "Release all held inputs and recover from a stuck gesture.", Obj(new { })),
                Tool("computer_wait", "Wait for visual change or a stable UI. maxMs is preferred; ms remains accepted for compatibility.", Obj(new { maxMs = Num("maximum wait milliseconds"), ms = Num("compatibility alias"), untilImageChange = new { type = "boolean" }, untilText = Str("visible text to wait for") })),
                Tool("computer_open_browser", "Open or focus the real browser and return an observed frame.", Obj(new { url = Str("optional URL") })),
                Tool("computer_navigate", "Navigate the real browser by URL, wait for the page to settle, then observe.", Obj(new { url = Str("absolute URL") }, "url")),
                Tool("computer_browser_inspect", "Inspect the isolated browser structurally instead of guessing from pixels. Returns tabs, DOM text/links/forms, accessibility nodes, or recent network resource timings from the live authenticated page.", Obj(new { mode = Str("tabs | dom | accessibility | network (default dom)"), maxItems = Num("Maximum structured items, 1-200; default 80") })),
                Tool("computer_focus_window", "Focus a window by title or process where supported.", Obj(new { titleContains = Str("title fragment"), processName = Str("process name") })),
                Tool("computer_launch_app", "Launch an allowlisted GUI application and observe it.", Obj(new { path = Str("application"), shellName = Str("known application"), args = Str("arguments") })),
                Tool("computer_clipboard_get", "Read clipboard text where supported.", Obj(new { })),
                Tool("computer_clipboard_set", "Set clipboard text where supported.", Obj(new { text = Str("clipboard text") }, "text")),
            };
            if (capabilities.SupportsTerminalExecution && capabilities.Supports("computer_terminal"))
                all.Add(Tool("computer_terminal",
                    "Run a Bash command INSIDE your isolated Linux desktop container, as its agent user - never on the Omnipotent host. Prefer this over typing commands into XFCE Terminal: it is reliable even when screenshots are temporarily unavailable, returns bounded stdout/stderr, and can install container software with sudo. The default working directory is persistent /project. Vault/account placeholders are intentionally NOT available because arbitrary command output could reveal them; use computer_type for secret entry.",
                    Obj(new
                    {
                        command = Str("Bash command to run inside the desktop container."),
                        workingDirectory = Str("Optional absolute directory under /project or /home/agent; default /project."),
                        timeoutSeconds = Num("Timeout from 1 to 900 seconds; default 120."),
                    }, "command")));
            return all.Where(t => capabilities.Supports(t.function.name)).ToList();
        }
    }
}
