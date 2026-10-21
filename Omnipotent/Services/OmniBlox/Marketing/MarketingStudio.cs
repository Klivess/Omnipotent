using System.Diagnostics;
using System.Numerics;
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

        // The look that wins on Roblox's front page with children: saturated colour, one bold subject, sunburst rays,
        // chunky warm-gradient lettering with a thick outline, die-cut stickers, sparkles. The first studio's art was
        // a grey-blue wash with a dark gradient over the world, which read as gloomy at phone size.
        private static readonly SKColor HeadlineTop = new(255, 246, 92);
        private static readonly SKColor HeadlineBottom = new(255, 160, 28);
        private static readonly SKColor Ink = new(28, 18, 70);

        public byte[] MakeIcon(ArtSpec spec)
        {
            const int size = 512;
            using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
            var c = surface.Canvas;
            Backdrop(c, size, size, spec);
            var accent = Vivid(spec.Accent);
            bool headline = !string.IsNullOrWhiteSpace(spec.Headline);
            if (spec.Featured.Count > 0)
            {
                var focus = new SKPoint(size * 0.5f, headline ? size * 0.4f : size * 0.5f);
                Rays(c, focus, size * 0.75f, accent.WithAlpha(120), 14, 0.2f);
                Sticker(c, spec.Featured[0], new SKRect(size * 0.12f, focus.Y - size * 0.34f, size * 0.88f, focus.Y + size * 0.34f), -4, outline: 7);
            }
            if (headline)
            {
                using (var foot = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, size * 0.55f), new SKPoint(0, size), new[] { accent.WithAlpha(0), accent.WithAlpha(150) }, null, SKShaderTileMode.Clamp) })
                    c.DrawRect(new SKRect(0, size * 0.55f, size, size), foot);
                ChunkyText(c, spec.Headline, new SKRect(22, size * 0.62f, size - 22, size - 16), Heavy, 104, 2, -3, SKTextAlign.Center);
            }
            Sparkle(c, size * 0.86f, size * 0.16f, 22, SKColors.White);
            Sparkle(c, size * 0.12f, size * 0.52f, 14, HeadlineTop);
            if (!string.IsNullOrWhiteSpace(spec.Badge)) Burst(c, spec.Badge!, new SKPoint(size * 0.83f, size * 0.15f), 62, 10);
            return Encode(surface);
        }

        public byte[] MakeThumbnail(ArtSpec spec)
        {
            const int w = 1920, h = 1080;
            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var c = surface.Canvas;
            Backdrop(c, w, h, spec);
            var accent = Vivid(spec.Accent);
            // A wash of the game's own accent colour behind the words (not a dark fade): readable and still bright.
            using (var wash = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w * 0.6f, 0), new[] { accent.WithAlpha(170), accent.WithAlpha(0) }, null, SKShaderTileMode.Clamp) })
                c.DrawRect(new SKRect(0, 0, w * 0.6f, h), wash);
            var main = new SKRect(w * 0.56f, h * 0.08f, w * 0.97f, h * 0.84f);
            if (spec.Featured.Count > 0) Rays(c, new SKPoint(main.MidX, main.MidY), h * 0.95f, SKColors.White.WithAlpha(70), 18, 0.1f);
            else Rays(c, new SKPoint(w * 0.3f, h * 0.45f), h * 0.9f, SKColors.White.WithAlpha(45), 18, 0.1f);
            var slots = new[] { main, new SKRect(w * 0.5f, h * 0.6f, w * 0.7f, h * 0.97f), new SKRect(w * 0.8f, h * 0.62f, w * 0.99f, h * 0.98f) };
            for (int i = Math.Min(3, spec.Featured.Count) - 1; i >= 0; i--) Sticker(c, spec.Featured[i], slots[i], i == 0 ? 4 : (i == 1 ? -7 : 8), outline: i == 0 ? 12 : 9);
            ChunkyText(c, spec.Headline, new SKRect(80, 130, w * 0.58f, 640), Heavy, 220, 2, -3, SKTextAlign.Left);
            if (!string.IsNullOrWhiteSpace(spec.Subtitle)) Pill(c, spec.Subtitle!, new SKRect(96, 690, w * 0.57f, 800), Regular, 70, accent);
            Sparkle(c, 70, 120, 34, SKColors.White);
            Sparkle(c, w * 0.55f, 170, 26, HeadlineTop);
            Sparkle(c, w * 0.6f, h * 0.88f, 30, SKColors.White);
            if (!string.IsNullOrWhiteSpace(spec.Badge)) Burst(c, spec.Badge!, new SKPoint(w - 190, 170), 140, 12);
            else Sparkle(c, w * 0.95f, h * 0.1f, 40, HeadlineTop);
            return Encode(surface);
        }

        /// <summary>
        /// Renders the shots through the real world (terrain, models, lighting, in the vivid store look) and encodes an
        /// H.264 MP4. Returns null (with the reason) when FFmpeg is unavailable.
        /// </summary>
        public async Task<(string? Path, string Message)> MakeTrailerAsync(RenderWorld world, string title,
            IReadOnlyList<TrailerShot> shots, string outputPath, CancellationToken ct, int width = 960, int height = 540, int fps = 15)
        {
            if (!CanMakeVideo) return (null, "FFmpeg is not installed (OmniTube installs it under SavedData/FFmpeg); the trailer is skipped.");
            if (world.Parts.Count == 0 && world.Terrain == null) return (null, "the world is empty");
            string frames = Path.Combine(Path.GetTempPath(), "omniblox-trailer-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(frames);
            try
            {
                var (min, max) = WorldRenderer.PlayBounds(world);
                var center = (min + max) * 0.5f;
                var extent = max - min;
                float radius = MathF.Max(10, extent.Length() * 0.5f);
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
                            var camera = CameraFor(shot.Kind, t, world.Parts, center, extent, radius);
                            png = await Task.Run(() => WorldRenderer.Render(world, new RenderRequest { Width = width, Height = height, Caption = false, Marketing = true, Supersample = 1, Camera = camera }), ct);
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

        private static (Vector3 Eye, Vector3 Target, float Fov) CameraFor(string kind, float t, IReadOnlyList<WorldPart> parts, Vector3 center, Vector3 extent, float radius)
        {
            switch (kind)
            {
                case "flyover":
                    {
                        // Glide along the world's long axis at a low height, looking ahead.
                        bool alongZ = extent.Z >= extent.X;
                        float half = (alongZ ? extent.Z : extent.X) * 0.45f;
                        float s = -half + t * half * 2;
                        float height = MathF.Max(25, radius * 0.22f);
                        var eye = alongZ
                            ? new Vector3(center.X + extent.X * 0.25f, center.Y + height, center.Z - s)
                            : new Vector3(center.X + s, center.Y + height, center.Z + extent.Z * 0.25f);
                        var ahead = alongZ ? eye + new Vector3(-extent.X * 0.25f, -height * 0.8f, -radius * 0.6f) : eye + new Vector3(radius * 0.6f, -height * 0.8f, -extent.Z * 0.25f);
                        return (eye, ahead, 60);
                    }
                case "orbit":
                    {
                        float angle = 0.6f + t * MathF.PI * 0.6f;
                        var eye = center + new Vector3(MathF.Cos(angle) * radius * 1.1f, radius * 0.55f, MathF.Sin(angle) * radius * 1.1f);
                        return (eye, center, 50);
                    }
                default:
                    {
                        var focus = parts.Where(p => p.ObjectId.Equals(kind, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (focus.Count == 0) goto case "orbit";
                        var fmin = new Vector3(focus.Min(p => p.CFrame.Position.X - p.Size.X / 2), focus.Min(p => p.CFrame.Position.Y - p.Size.Y / 2), focus.Min(p => p.CFrame.Position.Z - p.Size.Z / 2));
                        var fmax = new Vector3(focus.Max(p => p.CFrame.Position.X + p.Size.X / 2), focus.Max(p => p.CFrame.Position.Y + p.Size.Y / 2), focus.Max(p => p.CFrame.Position.Z + p.Size.Z / 2));
                        var fc = (fmin + fmax) * 0.5f;
                        float fr = MathF.Max(6, (fmax - fmin).Length() * 0.6f);
                        float dolly = 3.2f - t * 1.2f;
                        return (fc + new Vector3(fr * dolly * 0.7f, fr * 0.8f, -fr * dolly), fc, 45);
                    }
            }
        }

        // ─────────────── drawing helpers ───────────────

        private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.Linear);

        /// <summary>The world (a marketing render) over a bright sky gradient, graded more vivid for the store.</summary>
        private static void Backdrop(SKCanvas c, int w, int h, ArtSpec spec)
        {
            using (var sky = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h), new[] { new SKColor(64, 186, 255), new SKColor(140, 226, 255) }, null, SKShaderTileMode.Clamp) })
                c.DrawRect(new SKRect(0, 0, w, h), sky);
            if (spec.SceneRender is not { Length: > 0 } render) return;
            using var bitmap = SKBitmap.Decode(render);
            if (bitmap == null) return;
            // Cover: scale to fill, crop the overflow evenly.
            float scale = Math.Max(w / (float)bitmap.Width, h / (float)bitmap.Height);
            float dw = bitmap.Width * scale, dh = bitmap.Height * scale;
            using var image = SKImage.FromBitmap(bitmap);
            using var grade = new SKPaint { ColorFilter = StoreGrade() };
            c.DrawImage(image, new SKRect((w - dw) / 2, (h - dh) / 2, (w + dw) / 2, (h + dh) / 2), Smooth, grade);
        }

        /// <summary>More saturation, a little contrast and a lift: the render as a store thumbnail, not a screenshot.</summary>
        internal static SKColorFilter StoreGrade(float saturation = 1.32f, float contrast = 1.08f, float lift = 0.035f)
        {
            const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
            float s = saturation, k = contrast;
            float sr = (1 - s) * lr, sg = (1 - s) * lg, sb = (1 - s) * lb;
            float t = 0.5f * (1 - k) + lift; // Skia's matrix translation is in 0..1
            return SKColorFilter.CreateColorMatrix(new[]
            {
                k * (sr + s), k * sg, k * sb, 0, t,
                k * sr, k * (sg + s), k * sb, 0, t,
                k * sr, k * sg, k * (sb + s), 0, t,
                0, 0, 0, 1, 0,
            });
        }

        /// <summary>The accent pushed to a vivid version of itself (a theme's muted colour would look dull on the store).</summary>
        internal static SKColor Vivid(RbxColor3uint8 color)
        {
            new SKColor(color.R, color.G, color.B).ToHsv(out float hue, out float sat, out float val);
            return SKColor.FromHsv(hue, Math.Max(sat, 72), Math.Max(val, 88));
        }

        /// <summary>A model as a die-cut sticker: a thick white outline around its shape and a soft shadow, tilted.</summary>
        private static void Sticker(SKCanvas c, byte[] png, SKRect box, float degrees, float outline = 8)
        {
            using var decoded = SKBitmap.Decode(png);
            if (decoded == null) return;
            using var bitmap = CutOut(decoded);
            using var image = SKImage.FromBitmap(bitmap);
            float scale = Math.Min(box.Width / bitmap.Width, box.Height / bitmap.Height);
            float dw = bitmap.Width * scale, dh = bitmap.Height * scale;
            var rect = new SKRect(box.MidX - dw / 2, box.MidY - dh / 2, box.MidX + dw / 2, box.MidY + dh / 2);
            c.Save();
            c.RotateDegrees(degrees, box.MidX, box.MidY);
            using (var white = SKImageFilter.CreateColorFilter(SKColorFilter.CreateBlendMode(SKColors.White, SKBlendMode.SrcIn), SKImageFilter.CreateDilate(outline, outline)))
            using (var cut = SKImageFilter.CreateDropShadow(outline * 0.6f, outline * 1.2f, outline, outline, new SKColor(20, 10, 60, 120), white))
            using (var backing = new SKPaint { ImageFilter = cut })
                c.DrawImage(image, rect, Smooth, backing);
            c.DrawImage(image, rect, Smooth);
            c.Restore();
        }

        /// <summary>
        /// A model picture on a plain background (no transparency anywhere on its border) gets that background keyed out,
        /// flood-filling inwards from the edges, so it can still be a die-cut sticker. A picture that already has
        /// transparency is returned as it is.
        /// </summary>
        internal static SKBitmap CutOut(SKBitmap source)
        {
            var bitmap = source.Copy(SKColorType.Rgba8888) ?? source.Copy();
            int w = bitmap.Width, h = bitmap.Height;
            if (w < 8 || h < 8) return bitmap;
            var pixels = bitmap.Pixels;
            var border = new List<int>();
            for (int x = 0; x < w; x++) { border.Add(x); border.Add((h - 1) * w + x); }
            for (int y = 1; y < h - 1; y++) { border.Add(y * w); border.Add(y * w + w - 1); }
            if (border.Any(i => pixels[i].Alpha < 250)) return bitmap;
            var corners = new[] { pixels[0], pixels[w - 1], pixels[(h - 1) * w], pixels[h * w - 1] };
            static int Distance(SKColor a, SKColor b) => Math.Max(Math.Abs(a.Red - b.Red), Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue)));
            if (corners.Any(a => corners.Any(b => Distance(a, b) > 24))) return bitmap; // not one plain background
            var background = corners[0];
            var cleared = new bool[pixels.Length];
            var queue = new Queue<int>();
            foreach (int i in border)
                if (!cleared[i] && Distance(pixels[i], background) <= 36) { cleared[i] = true; queue.Enqueue(i); }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                pixels[i] = pixels[i].WithAlpha(0);
                int x = i % w, y = i / w;
                void Visit(int n)
                {
                    if (cleared[n] || Distance(pixels[n], background) > 36) return;
                    cleared[n] = true;
                    queue.Enqueue(n);
                }
                if (x > 0) Visit(i - 1);
                if (x < w - 1) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y < h - 1) Visit(i + w);
            }
            bitmap.Pixels = pixels;
            return bitmap;
        }

        /// <summary>Sunburst rays from a point, fading out with distance (screen-blended, so they brighten).</summary>
        private static void Rays(SKCanvas c, SKPoint center, float radius, SKColor color, int count, float rotation)
        {
            using var path = new SKPath();
            float step = MathF.PI * 2 / count;
            for (int i = 0; i < count; i++)
            {
                float a0 = rotation + i * step, a1 = a0 + step * 0.5f;
                path.MoveTo(center);
                path.LineTo(center.X + MathF.Cos(a0) * radius, center.Y + MathF.Sin(a0) * radius);
                path.LineTo(center.X + MathF.Cos(a1) * radius, center.Y + MathF.Sin(a1) * radius);
                path.Close();
            }
            using var paint = new SKPaint
            {
                IsAntialias = true,
                BlendMode = SKBlendMode.Screen,
                Shader = SKShader.CreateRadialGradient(center, radius, new[] { color, color.WithAlpha((byte)(color.Alpha / 3)), color.WithAlpha(0) }, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp),
            };
            c.DrawPath(path, paint);
        }

        /// <summary>A four-pointed sparkle with a soft glow.</summary>
        private static void Sparkle(SKCanvas c, float x, float y, float r, SKColor color)
        {
            using var path = new SKPath();
            path.MoveTo(x, y - r);
            path.QuadTo(x, y, x + r * 0.7f, y);
            path.QuadTo(x, y, x, y + r);
            path.QuadTo(x, y, x - r * 0.7f, y);
            path.QuadTo(x, y, x, y - r);
            path.Close();
            using (var glow = new SKPaint { Color = color.WithAlpha(140), IsAntialias = true, ImageFilter = SKImageFilter.CreateBlur(r * 0.35f, r * 0.35f) })
                c.DrawPath(path, glow);
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            c.DrawPath(path, paint);
        }

        /// <summary>A starburst badge ("NEW!", "UPDATE"): yellow points, red outline, tilted text.</summary>
        private static void Burst(SKCanvas c, string text, SKPoint center, float radius, int points)
        {
            using var path = new SKPath();
            for (int i = 0; i < points * 2; i++)
            {
                float a = -MathF.PI / 2 + i * MathF.PI / points;
                float r = i % 2 == 0 ? radius : radius * 0.78f;
                var p = new SKPoint(center.X + MathF.Cos(a) * r, center.Y + MathF.Sin(a) * r);
                if (i == 0) path.MoveTo(p); else path.LineTo(p);
            }
            path.Close();
            using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 90), IsAntialias = true, ImageFilter = SKImageFilter.CreateBlur(radius * 0.06f, radius * 0.06f) })
            {
                c.Save();
                c.Translate(radius * 0.04f, radius * 0.08f);
                c.DrawPath(path, shadow);
                c.Restore();
            }
            using (var fill = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(center, radius, new[] { new SKColor(255, 240, 80), new SKColor(255, 196, 0) }, null, SKShaderTileMode.Clamp) })
                c.DrawPath(path, fill);
            using (var edge = new SKPaint { Color = new SKColor(230, 40, 60), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = radius * 0.07f, StrokeJoin = SKStrokeJoin.Round })
                c.DrawPath(path, edge);
            c.Save();
            c.RotateDegrees(-12, center.X, center.Y);
            float inner = radius * 0.72f;
            FittedText(c, text.ToUpperInvariant(), new SKRect(center.X - inner, center.Y - inner * 0.55f, center.X + inner, center.Y + inner * 0.55f), Heavy, radius * 0.6f,
                new SKColor(230, 40, 60), SKColors.White, 1, SKTextAlign.Center);
            c.Restore();
        }

        /// <summary>
        /// Store lettering: as large as fits the box, a warm yellow-to-orange gradient, a thick dark outline and a soft
        /// drop shadow, tilted a little.
        /// </summary>
        internal static void ChunkyText(SKCanvas c, string text, SKRect box, SKTypeface typeface, float maxSize, int maxLines, float degrees, SKTextAlign align)
        {
            text = (text ?? "").Trim().ToUpperInvariant();
            if (text.Length == 0) return;
            for (float size = maxSize; size >= 12; size -= 2)
            {
                using var font = new SKFont(typeface, size);
                var lines = Wrap(text, font, box.Width - size * 0.3f, maxLines);
                if (lines == null) continue;
                float lineHeight = size * 1.02f;
                if (lines.Count * lineHeight > box.Height) continue;
                c.Save();
                c.RotateDegrees(degrees, box.MidX, box.MidY);
                float y = box.Top + (box.Height - lines.Count * lineHeight) / 2 + size * 0.86f;
                float x = align switch { SKTextAlign.Center => box.MidX, SKTextAlign.Right => box.Right, _ => box.Left + size * 0.12f };
                float stroke = Math.Max(4, size * 0.17f);
                using var shadow = new SKPaint { Color = Ink.WithAlpha(150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, StrokeJoin = SKStrokeJoin.Round, ImageFilter = SKImageFilter.CreateBlur(size * 0.06f, size * 0.06f) };
                using var outline = new SKPaint { Color = Ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, StrokeJoin = SKStrokeJoin.Round };
                foreach (var line in lines)
                {
                    using var fill = new SKPaint
                    {
                        IsAntialias = true,
                        Shader = SKShader.CreateLinearGradient(new SKPoint(0, y - size * 0.78f), new SKPoint(0, y + size * 0.05f), new[] { HeadlineTop, HeadlineBottom }, null, SKShaderTileMode.Clamp),
                    };
                    c.DrawText(line, x + size * 0.03f, y + size * 0.09f, align, font, shadow);
                    c.DrawText(line, x, y, align, font, outline);
                    c.DrawText(line, x, y, align, font, fill);
                    y += lineHeight;
                }
                c.Restore();
                return;
            }
        }

        /// <summary>A subtitle on a rounded pill of the accent colour, white lettering with a dark outline.</summary>
        private static void Pill(SKCanvas c, string text, SKRect box, SKTypeface typeface, float maxSize, SKColor color)
        {
            text = text.Trim();
            for (float size = maxSize; size >= 14; size -= 2)
            {
                using var font = new SKFont(typeface, size);
                float width = font.MeasureText(text);
                float pad = size * 0.55f;
                if (width + pad * 2 > box.Width || size * 1.5f > box.Height) continue;
                var pill = new SKRect(box.Left, box.MidY - size * 0.75f, box.Left + width + pad * 2, box.MidY + size * 0.75f);
                c.Save();
                c.RotateDegrees(-2, pill.MidX, pill.MidY);
                using (var shadow = new SKPaint { Color = Ink.WithAlpha(120), IsAntialias = true, ImageFilter = SKImageFilter.CreateBlur(size * 0.12f, size * 0.12f) })
                    c.DrawRoundRect(new SKRect(pill.Left + 4, pill.Top + 8, pill.Right + 4, pill.Bottom + 8), pill.Height / 2, pill.Height / 2, shadow);
                using (var fill = new SKPaint { Color = color, IsAntialias = true })
                    c.DrawRoundRect(pill, pill.Height / 2, pill.Height / 2, fill);
                using (var rim = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(3, size * 0.08f) })
                    c.DrawRoundRect(pill, pill.Height / 2, pill.Height / 2, rim);
                float baseline = pill.MidY + size * 0.36f;
                using (var outline = new SKPaint { Color = Ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(3, size * 0.14f), StrokeJoin = SKStrokeJoin.Round })
                    c.DrawText(text, pill.Left + pad, baseline, SKTextAlign.Left, font, outline);
                using (var ink = new SKPaint { Color = SKColors.White, IsAntialias = true })
                    c.DrawText(text, pill.Left + pad, baseline, SKTextAlign.Left, font, ink);
                c.Restore();
                return;
            }
            // Too long for one line on a pill: plain outlined lettering instead.
            FittedText(c, text, box, typeface, maxSize, SKColors.White, Ink, 2);
        }

        /// <summary>Draws text as large as fits the box (up to <paramref name="maxLines"/> lines), with a thick outline.</summary>
        internal static void FittedText(SKCanvas c, string text, SKRect box, SKTypeface typeface, float maxSize, SKColor fill, SKColor outline, int maxLines, SKTextAlign align = SKTextAlign.Left)
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
                float y = align == SKTextAlign.Left
                    ? box.Bottom - (lines.Count - 1) * lineHeight - size * 0.18f
                    : box.Top + (box.Height - lines.Count * lineHeight) / 2 + size * 0.86f;
                float x = align switch { SKTextAlign.Center => box.MidX, SKTextAlign.Right => box.Right, _ => box.Left };
                using var stroke = new SKPaint { Color = outline, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(3, size * 0.12f), StrokeJoin = SKStrokeJoin.Round };
                using var ink = new SKPaint { Color = fill, IsAntialias = true };
                foreach (var line in lines)
                {
                    c.DrawText(line, x, y, align, font, stroke);
                    c.DrawText(line, x, y, align, font, ink);
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
