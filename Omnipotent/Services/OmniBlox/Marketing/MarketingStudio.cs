using System.Diagnostics;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Rbx;
using SkiaSharp;

namespace Omnipotent.Services.OmniBlox.Marketing
{
    public sealed class ArtSpec
    {
        public string Headline { get; init; } = "";
        public string? Subtitle { get; init; }
        public string? Badge { get; init; }
        /// <summary>A render of the game's own world (the backdrop).</summary>
        public byte[]? SceneRender { get; init; }
        /// <summary>Roblox's thumbnails of models actually used in the game (shown as stickers).</summary>
        public List<byte[]> Featured { get; init; } = new();
        public RbxColor3uint8 Accent { get; init; } = new(76, 175, 255);
    }

    public sealed class TrailerShot
    {
        /// <summary>orbit, flyover, title, or an object id to close in on.</summary>
        public string Kind { get; init; } = "orbit";
        public double Seconds { get; init; } = 4;
        public string? Caption { get; init; }
    }

    /// <summary>
    /// Makes the game's own marketing content from the game itself, with free local tools: icons and
    /// thumbnails composed (SkiaSharp) from renders of the real world and Roblox's thumbnails of the models
    /// actually placed in it, plus a short trailer of camera moves through the world encoded with FFmpeg.
    /// Every picture shows the real game, so it stays honest.
    /// </summary>
    public sealed class MarketingStudio
    {
        private readonly string? ffmpegPath;

        public MarketingStudio(string? ffmpegPath) => this.ffmpegPath = ffmpegPath;

        public bool CanMakeVideo => ffmpegPath != null && File.Exists(ffmpegPath);

        private static SKTypeface Heavy => SKTypeface.FromFamilyName("Arial Black", SKFontStyle.Bold)
            ?? SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;

        private static SKTypeface Regular => SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;

        public byte[] MakeIcon(ArtSpec spec)
        {
            const int size = 512;
            using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
            var c = surface.Canvas;
            Backdrop(c, size, size, spec);
            if (spec.Featured.Count > 0)
                Sticker(c, spec.Featured[0], new SKRect(size * 0.2f, size * 0.08f, size * 0.8f, size * 0.68f), -4);
            using (var shade = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, size * 0.45f), new SKPoint(0, size), new[] { SKColors.Transparent, new SKColor(0, 0, 0, 200) }, null, SKShaderTileMode.Clamp) })
                c.DrawRect(new SKRect(0, size * 0.45f, size, size), shade);
            FittedText(c, spec.Headline, new SKRect(24, size * 0.66f, size - 24, size - 22), Heavy, 92, SKColors.White, new SKColor(15, 18, 28), 2);
            if (!string.IsNullOrWhiteSpace(spec.Badge)) Badge(c, spec.Badge!, 18, 18, 34, spec.Accent);
            return Encode(surface);
        }

        public byte[] MakeThumbnail(ArtSpec spec)
        {
            const int w = 1920, h = 1080;
            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var c = surface.Canvas;
            Backdrop(c, w, h, spec);
            using (var shade = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w * 0.62f, 0), new[] { new SKColor(8, 10, 18, 215), SKColors.Transparent }, null, SKShaderTileMode.Clamp) })
                c.DrawRect(new SKRect(0, 0, w * 0.62f, h), shade);
            FittedText(c, spec.Headline, new SKRect(90, 250, w * 0.56f, 640), Heavy, 190, SKColors.White, new SKColor(12, 14, 24), 2);
            if (!string.IsNullOrWhiteSpace(spec.Subtitle))
                FittedText(c, spec.Subtitle!, new SKRect(96, 660, w * 0.56f, 790), Regular, 76, new SKColor(255, 214, 92), new SKColor(12, 14, 24), 2);
            var slots = new[] { new SKRect(w * 0.62f, h * 0.14f, w * 0.95f, h * 0.72f), new SKRect(w * 0.56f, h * 0.55f, w * 0.78f, h * 0.95f), new SKRect(w * 0.78f, h * 0.6f, w * 0.98f, h * 0.97f) };
            for (int i = 0; i < Math.Min(3, spec.Featured.Count); i++) Sticker(c, spec.Featured[i], slots[i], i == 0 ? 3 : (i == 1 ? -6 : 7));
            if (!string.IsNullOrWhiteSpace(spec.Badge)) Badge(c, spec.Badge!, 70, 70, 64, spec.Accent);
            return Encode(surface);
        }

        /// <summary>Renders the shots as frames and encodes an H.264 MP4. Returns null (with the reason) when FFmpeg is unavailable.</summary>
        public async Task<(string? Path, string Message)> MakeTrailerAsync(IReadOnlyList<WorldPart> parts, SceneDocument scene, string title,
            IReadOnlyList<TrailerShot> shots, string outputPath, CancellationToken ct, int width = 1280, int height = 720, int fps = 24)
        {
            if (!CanMakeVideo) return (null, "FFmpeg is not installed (OmniTube installs it under SavedData/FFmpeg); the trailer is skipped.");
            if (parts.Count == 0) return (null, "the world is empty");
            string frames = Path.Combine(Path.GetTempPath(), "omniblox-trailer-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(frames);
            try
            {
                var prims = SceneRenderer.Primitives(parts, scene);
                var (min, max) = SceneRenderer.PlayBounds(parts);
                var center = (min + max) * 0.5f;
                var extent = max - min;
                float radius = MathF.Max(10, extent.Magnitude * 0.5f);
                int frame = 0;
                foreach (var shot in shots.Take(8))
                {
                    int count = (int)Math.Clamp(shot.Seconds * fps, fps, fps * 8);
                    for (int i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        float t = count <= 1 ? 0 : i / (float)(count - 1);
                        byte[] png;
                        if (shot.Kind == "title")
                        {
                            png = TitleCard(title, shot.Caption, width, height, t);
                        }
                        else
                        {
                            var camera = CameraFor(shot.Kind, t, parts, center, extent, radius);
                            png = SchematicRenderer.RenderPng(prims, camera, new RenderOptions { Width = width, Height = height, Grid = false, Sky = true });
                            if (!string.IsNullOrWhiteSpace(shot.Caption)) png = Caption(png, shot.Caption!, t);
                        }
                        await File.WriteAllBytesAsync(Path.Combine(frames, $"f{++frame:D5}.png"), png, ct);
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
                var psi = new ProcessStartInfo(ffmpegPath!, $"-y -loglevel error -framerate {fps} -i \"{Path.Combine(frames, "f%05d.png")}\" -c:v libx264 -pix_fmt yuv420p -crf 22 -movflags +faststart \"{outputPath}\"")
                {
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi)!;
                string err = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                return process.ExitCode == 0 && File.Exists(outputPath)
                    ? (outputPath, $"{frame} frames, {frame / (double)fps:0.#} s")
                    : (null, "FFmpeg failed: " + err.Trim());
            }
            finally
            {
                try { Directory.Delete(frames, true); } catch { }
            }
        }

        private static RenderCamera CameraFor(string kind, float t, IReadOnlyList<WorldPart> parts, RbxVector3 center, RbxVector3 extent, float radius)
        {
            switch (kind)
            {
                case "flyover":
                    {
                        // Glide along the world's long axis at a low height, looking ahead.
                        bool alongZ = extent.Z >= extent.X;
                        float half = (alongZ ? extent.Z : extent.X) * 0.5f;
                        float s = -half + t * half * 2;
                        var eye = alongZ
                            ? new RbxVector3(center.X + extent.X * 0.3f, center.Y + radius * 0.35f, center.Z - s)
                            : new RbxVector3(center.X + s, center.Y + radius * 0.35f, center.Z + extent.Z * 0.3f);
                        var ahead = alongZ ? eye + new RbxVector3(-extent.X * 0.3f, -radius * 0.3f, -radius) : eye + new RbxVector3(radius, -radius * 0.3f, -extent.Z * 0.3f);
                        return new RenderCamera { Eye = eye, Target = ahead, FieldOfView = 60 };
                    }
                case "orbit":
                    {
                        float angle = 0.6f + t * MathF.PI * 0.9f;
                        var eye = center + new RbxVector3(MathF.Cos(angle) * radius * 1.5f, radius * 0.9f, MathF.Sin(angle) * radius * 1.5f);
                        return new RenderCamera { Eye = eye, Target = center, FieldOfView = 45 };
                    }
                default:
                    {
                        var focus = parts.Where(p => p.ObjectId.Equals(kind, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (focus.Count == 0) goto case "orbit";
                        var (fmin, fmax) = SchematicRenderer.Bounds(focus.Select(p => new RenderPrimitive(p.CFrame, p.Size, p.Shape, p.Color, 0, false)).ToList());
                        var fc = (fmin + fmax) * 0.5f;
                        float fr = MathF.Max(5, (fmax - fmin).Magnitude * 0.6f);
                        float dolly = 3.2f - t * 1.2f;
                        return new RenderCamera { Eye = fc + new RbxVector3(fr * dolly * 0.7f, fr * 0.8f, -fr * dolly), Target = fc, FieldOfView = 45 };
                    }
            }
        }

        // ─────────────── drawing helpers ───────────────

        private static void Backdrop(SKCanvas c, int w, int h, ArtSpec spec)
        {
            var a = spec.Accent;
            using (var bg = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h), new[] { new SKColor(a.R, a.G, a.B), new SKColor((byte)(a.R / 3), (byte)(a.G / 3), (byte)(a.B / 3)) }, null, SKShaderTileMode.Clamp) })
                c.DrawRect(new SKRect(0, 0, w, h), bg);
            if (spec.SceneRender is { Length: > 0 } render)
            {
                using var bitmap = SKBitmap.Decode(render);
                if (bitmap != null)
                {
                    // Cover: scale to fill, crop the overflow evenly.
                    float scale = Math.Max(w / (float)bitmap.Width, h / (float)bitmap.Height);
                    float dw = bitmap.Width * scale, dh = bitmap.Height * scale;
                    using var image = SKImage.FromBitmap(bitmap);
                    c.DrawImage(image, new SKRect((w - dw) / 2, (h - dh) / 2, (w + dw) / 2, (h + dh) / 2), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                }
            }
        }

        private static void Sticker(SKCanvas c, byte[] png, SKRect box, float degrees)
        {
            using var bitmap = SKBitmap.Decode(png);
            if (bitmap == null) return;
            using var image = SKImage.FromBitmap(bitmap);
            float scale = Math.Min(box.Width / bitmap.Width, box.Height / bitmap.Height);
            float dw = bitmap.Width * scale, dh = bitmap.Height * scale;
            var rect = new SKRect(box.MidX - dw / 2, box.MidY - dh / 2, box.MidX + dw / 2, box.MidY + dh / 2);
            c.Save();
            c.RotateDegrees(degrees, box.MidX, box.MidY);
            using (var shadow = new SKPaint { ImageFilter = SKImageFilter.CreateDropShadow(8, 12, 14, 14, new SKColor(0, 0, 0, 150)) })
                c.DrawImage(image, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), shadow);
            c.Restore();
        }

        private static void Badge(SKCanvas c, string text, float x, float y, float fontSize, RbxColor3uint8 accent)
        {
            using var font = new SKFont(Heavy, fontSize);
            float w = font.MeasureText(text) + fontSize * 0.9f;
            using var fill = new SKPaint { Color = new SKColor(255, 205, 64), IsAntialias = true };
            c.DrawRoundRect(new SKRect(x, y, x + w, y + fontSize * 1.45f), fontSize * 0.35f, fontSize * 0.35f, fill);
            using var ink = new SKPaint { Color = new SKColor(25, 25, 30), IsAntialias = true };
            c.DrawText(text, x + fontSize * 0.45f, y + fontSize * 1.08f, SKTextAlign.Left, font, ink);
        }

        /// <summary>Draws text as large as fits the box (up to <paramref name="maxLines"/> lines), white with a thick outline.</summary>
        internal static void FittedText(SKCanvas c, string text, SKRect box, SKTypeface typeface, float maxSize, SKColor fill, SKColor outline, int maxLines)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            for (float size = maxSize; size >= 12; size -= 2)
            {
                using var font = new SKFont(typeface, size);
                var lines = Wrap(text, font, box.Width, maxLines);
                if (lines == null) continue;
                float lineHeight = size * 1.08f;
                if (lines.Count * lineHeight > box.Height) continue;
                float y = box.Bottom - (lines.Count - 1) * lineHeight - size * 0.18f;
                using var stroke = new SKPaint { Color = outline, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(3, size * 0.12f), StrokeJoin = SKStrokeJoin.Round };
                using var ink = new SKPaint { Color = fill, IsAntialias = true };
                foreach (var line in lines)
                {
                    c.DrawText(line, box.Left, y, SKTextAlign.Left, font, stroke);
                    c.DrawText(line, box.Left, y, SKTextAlign.Left, font, ink);
                    y += lineHeight;
                }
                return;
            }
        }

        private static List<string>? Wrap(string text, SKFont font, float width, int maxLines)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var lines = new List<string>();
            string current = "";
            foreach (var word in words)
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (font.MeasureText(candidate) <= width) current = candidate;
                else
                {
                    if (current.Length == 0) return null; // a single word too wide at this size
                    lines.Add(current);
                    current = word;
                    if (font.MeasureText(current) > width) return null;
                }
            }
            if (current.Length > 0) lines.Add(current);
            return lines.Count <= maxLines ? lines : null;
        }

        private static byte[] TitleCard(string title, string? caption, int w, int h, float t)
        {
            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var c = surface.Canvas;
            using (var bg = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h), new[] { new SKColor(20, 30, 60), new SKColor(8, 10, 20) }, null, SKShaderTileMode.Clamp) })
                c.DrawRect(new SKRect(0, 0, w, h), bg);
            byte alpha = (byte)Math.Clamp((int)(t * 4 * 255), 0, 255);
            FittedText(c, title, new SKRect(w * 0.08f, h * 0.25f, w * 0.92f, h * 0.62f), Heavy, h * 0.16f, new SKColor(255, 255, 255, alpha), new SKColor(0, 0, 0, alpha), 2);
            if (!string.IsNullOrWhiteSpace(caption))
                FittedText(c, caption!, new SKRect(w * 0.08f, h * 0.66f, w * 0.92f, h * 0.8f), Regular, h * 0.06f, new SKColor(255, 214, 92, alpha), new SKColor(0, 0, 0, alpha), 1);
            return Encode(surface);
        }

        private static byte[] Caption(byte[] png, string caption, float t)
        {
            using var bitmap = SKBitmap.Decode(png);
            using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var c = surface.Canvas;
            c.DrawBitmap(bitmap, 0, 0);
            using (var band = new SKPaint { Color = new SKColor(0, 0, 0, 120) })
                c.DrawRect(new SKRect(0, bitmap.Height * 0.8f, bitmap.Width, bitmap.Height), band);
            FittedText(c, caption, new SKRect(bitmap.Width * 0.05f, bitmap.Height * 0.82f, bitmap.Width * 0.95f, bitmap.Height * 0.97f), Regular, bitmap.Height * 0.08f, SKColors.White, new SKColor(0, 0, 0), 1);
            return Encode(surface);
        }

        private static byte[] Encode(SKSurface surface)
        {
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }
}
