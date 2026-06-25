using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Omnipotent.Threading;

namespace Omnipotent.Services.HostControl
{
    /// <summary>
    /// Captures the screen (active window or full virtual desktop) into a downscaled JPEG for the vision
    /// model, and produces annotated copies (action label + click target) for the human video stream.
    /// The capture's coordinate frame (origin + scale) is returned so the caller can map the model's
    /// image-space click coordinates back to physical screen pixels for SendInput.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class ScreenCapturer
    {
        /// <param name="Jpeg">Encoded JPEG bytes (the shown image).</param>
        /// <param name="ShownWidth">Width of the encoded image (model's coordinate space).</param>
        /// <param name="OriginX">Physical-screen X of the captured region's top-left.</param>
        /// <param name="Scale">shownPixels / physicalPixels. physicalX = OriginX + modelX / Scale.</param>
        public sealed record CaptureResult(byte[] Jpeg, int ShownWidth, int ShownHeight, int OriginX, int OriginY, double Scale, string Description);

        public CaptureResult CaptureActiveWindow(int maxWidth = 1920, long quality = 72)
        {
            var info = NativeInput.GetForegroundWindowInfo();
            if (info == null || info.Value.Width <= 0 || info.Value.Height <= 0)
            {
                var (vx, vy, vw, vh) = NativeInput.GetVirtualScreenBounds();
                return CaptureRegion(vx, vy, vw, vh, maxWidth, quality, "full screen (no active window)");
            }
            var w = info.Value;
            return CaptureRegion(w.Left, w.Top, w.Width, w.Height, maxWidth, quality, $"active window \"{w.Title}\"");
        }

        public CaptureResult CaptureVirtualScreen(int maxWidth = 1920, long quality = 72)
        {
            var (vx, vy, vw, vh) = NativeInput.GetVirtualScreenBounds();
            return CaptureRegion(vx, vy, vw, vh, maxWidth, quality, "full screen");
        }

        private CaptureResult CaptureRegion(int x, int y, int w, int h, int maxWidth, long quality, string desc)
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            using var full = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

            double scale = (maxWidth > 0 && w > maxWidth) ? (double)maxWidth / w : 1.0;
            if (scale >= 1.0)
                return new CaptureResult(EncodeJpeg(full, quality), w, h, x, y, 1.0, desc);

            int sw = Math.Max(1, (int)Math.Round(w * scale));
            int sh = Math.Max(1, (int)Math.Round(h * scale));
            using var scaled = new Bitmap(sw, sh, PixelFormat.Format24bppRgb);
            using (var g2 = Graphics.FromImage(scaled))
            {
                g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g2.DrawImage(full, 0, 0, sw, sh);
            }
            return new CaptureResult(EncodeJpeg(scaled, quality), sw, sh, x, y, scale, desc);
        }

        /// <summary>Re-encode an arbitrary image (e.g. a Selenium PNG) to a downscaled JPEG.</summary>
        public byte[] ReencodeToJpeg(byte[] image, int maxWidth = 1400, long quality = 70)
        {
            using var src = (Bitmap)Image.FromStream(new MemoryStream(image));
            double scale = (maxWidth > 0 && src.Width > maxWidth) ? (double)maxWidth / src.Width : 1.0;
            if (scale >= 1.0) return EncodeJpeg(src, quality);
            int sw = Math.Max(1, (int)(src.Width * scale));
            int sh = Math.Max(1, (int)(src.Height * scale));
            using var scaled = new Bitmap(sw, sh, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, sw, sh);
            }
            return EncodeJpeg(scaled, quality);
        }

        /// <summary>Draw an action label and (optionally) a click-target marker in the image's own
        /// coordinate space. Returns a fresh annotated JPEG.</summary>
        public byte[] Annotate(byte[] jpeg, string label, int? targetX = null, int? targetY = null)
        {
            try
            {
                using var src = (Bitmap)Image.FromStream(new MemoryStream(jpeg));
                using var bmp = new Bitmap(src);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    if (targetX.HasValue && targetY.HasValue)
                    {
                        const int r = 16;
                        using var pen = new Pen(Color.FromArgb(255, 0, 200, 255), 3);
                        g.DrawEllipse(pen, targetX.Value - r, targetY.Value - r, r * 2, r * 2);
                        g.DrawLine(pen, targetX.Value, targetY.Value - r - 8, targetX.Value, targetY.Value - r);
                    }
                    if (!string.IsNullOrEmpty(label))
                    {
                        using var font = new Font("Segoe UI", 12, FontStyle.Bold);
                        var sz = g.MeasureString(label, font);
                        using var bg = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
                        g.FillRectangle(bg, 6, 6, sz.Width + 12, sz.Height + 6);
                        g.DrawString(label, font, Brushes.White, 12, 9);
                    }
                }
                return EncodeJpeg(bmp, 75);
            }
            catch
            {
                return jpeg; // annotation is best-effort; never fail an action over it
            }
        }

        /// <summary>
        /// Overlay a semi-transparent coordinate "measuring ruler" grid (labeled in the image's own pixel
        /// space) so the vision model can read off exact x,y to click. Lines every <paramref name="step"/> px;
        /// brighter lines + edge labels every 5 steps. The labels match the coordinates computer_click expects.
        /// </summary>
        public byte[] WithGrid(byte[] jpeg, int step = 100)
        {
            try
            {
                using var src = (Bitmap)Image.FromStream(new MemoryStream(jpeg));
                using var bmp = new Bitmap(src);
                int w = bmp.Width, h = bmp.Height;
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.None;
                    using var minor = new Pen(Color.FromArgb(45, 0, 210, 255), 1);
                    using var major = new Pen(Color.FromArgb(105, 0, 225, 255), 1);
                    using var font = new Font("Consolas", 8.5f, FontStyle.Bold);
                    using var labelBg = new SolidBrush(Color.FromArgb(165, 0, 0, 0));
                    using var labelFg = new SolidBrush(Color.FromArgb(255, 130, 235, 255));
                    int major5 = step * 5;

                    for (int x = step; x < w; x += step)
                    {
                        bool maj = x % major5 == 0;
                        g.DrawLine(maj ? major : minor, x, 0, x, h);
                        if (maj || step >= 100)
                        {
                            var s = x.ToString();
                            var sz = g.MeasureString(s, font);
                            g.FillRectangle(labelBg, x + 1, 0, sz.Width, sz.Height);
                            g.DrawString(s, font, labelFg, x + 1, 0);
                        }
                    }
                    for (int y = step; y < h; y += step)
                    {
                        bool maj = y % major5 == 0;
                        g.DrawLine(maj ? major : minor, 0, y, w, y);
                        if (maj || step >= 100)
                        {
                            var s = y.ToString();
                            var sz = g.MeasureString(s, font);
                            g.FillRectangle(labelBg, 0, y + 1, sz.Width, sz.Height);
                            g.DrawString(s, font, labelFg, 0, y + 1);
                        }
                    }
                    // Corner hint so the model knows the origin + the image's pixel extents.
                    var dim = $"{w}x{h}px  (0,0 top-left)";
                    using var hintFont = new Font("Consolas", 9, FontStyle.Bold);
                    var dsz = g.MeasureString(dim, hintFont);
                    g.FillRectangle(labelBg, w - dsz.Width - 4, h - dsz.Height - 3, dsz.Width + 4, dsz.Height + 2);
                    g.DrawString(dim, hintFont, labelFg, w - dsz.Width - 2, h - dsz.Height - 2);
                }
                return EncodeJpeg(bmp, 80);
            }
            catch { return jpeg; } // grid is best-effort; never fail an action over it
        }

        public static byte[] EncodeJpeg(Bitmap bmp, long quality)
        {
            var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 1, 100));
            using var ms = new MemoryStream();
            bmp.Save(ms, encoder, ep);
            return ms.ToArray();
        }

        // ───────────────────────── Motion clips ("filmstrips") ─────────────────────────
        // A single post-action screenshot misses everything that happens DURING an action — a toast that
        // flashes and vanishes, a menu that opens then the click closes it, a page transition, drag motion.
        // A clip samples a short burst of frames across the action and keeps only the ones that visibly
        // changed (motion-gated), so the model sees what happened — at ~today's cost when nothing moved.

        private const int ThumbW = 32, ThumbH = 32;

        // Serializes GDI CopyFromScreen across a background clip sampler and any other capture, so they don't
        // contend on the screen DC. Separate from HostControl's inputLock (capture is read-only).
        private readonly object captureLock = new();

        public sealed class ClipCaptureOptions
        {
            public int SampleFps { get; init; } = 12;        // internal sampling rate
            public int WindowMs { get; init; } = 1200;       // max settle window after the action
            public int MaxFrames { get; init; } = 4;         // total frames incl. the settled one
            public int MotionThreshold { get; init; } = 2;   // mean abs luma delta (0..255) to count as "changed"
            public int SampleMaxWidth { get; init; } = 1000; // intermediate frames are downscaled (gridless, cheap)
            public long SampleQuality { get; init; } = 55;
        }

        /// <summary>One sampled frame: the (small, gridless) JPEG, a 32×32 grayscale thumbnail for diffing,
        /// and its offset from clip start.</summary>
        public sealed record ClipSample(byte[] Jpeg, byte[] Thumb, int OffsetMs);

        /// <summary>Begin sampling a clip of the given target ("fullscreen" or "active") on a background task.
        /// Call <see cref="ClipRecorder.RequestFinish"/> once the action has fired, then await
        /// <see cref="ClipRecorder.FinishAsync"/> to stop on settle and collect the samples.</summary>
        public ClipRecorder BeginClip(string target, ClipCaptureOptions opts)
        {
            int x, y, w, h;
            if (string.Equals(target, "fullscreen", StringComparison.OrdinalIgnoreCase))
            {
                (x, y, w, h) = NativeInput.GetVirtualScreenBounds();
            }
            else
            {
                var info = NativeInput.GetForegroundWindowInfo();
                if (info != null && info.Value.Width > 0 && info.Value.Height > 0)
                    (x, y, w, h) = (info.Value.Left, info.Value.Top, info.Value.Width, info.Value.Height);
                else
                    (x, y, w, h) = NativeInput.GetVirtualScreenBounds();
            }
            var rec = new ClipRecorder(this, x, y, w, h, opts);
            rec.Start();
            return rec;
        }

        /// <summary>Greedily pick the distinct in-between states from a clip's raw samples: walk oldest→newest
        /// keeping a frame only when it differs from the last kept one by ≥ threshold, drop any that are
        /// redundant with the settled (final) frame, then cap to MaxFrames-1 keeping the most-changed. Returns
        /// the intermediate motion frames (gridless); the caller appends the gridded settled frame last.</summary>
        public List<ClipFrame> SelectIntermediates(List<ClipSample> samples, byte[] settledThumb, ClipCaptureOptions opts)
        {
            var result = new List<ClipFrame>();
            if (samples == null || samples.Count == 0) return result;
            int thr = Math.Max(1, opts.MotionThreshold);

            // 1) distinct-state walk
            var kept = new List<ClipSample>();
            byte[]? lastThumb = null;
            foreach (var s in samples)
                if (lastThumb == null || ThumbDelta(lastThumb, s.Thumb) >= thr) { kept.Add(s); lastThumb = s.Thumb; }

            // 2) drop frames redundant with the settled frame (the model already gets that one, gridded)
            if (settledThumb != null && settledThumb.Length > 0)
                kept = kept.Where(s => ThumbDelta(s.Thumb, settledThumb) >= thr).ToList();

            if (kept.Count == 0) return result; // nothing moved → settled frame alone conveys the state

            // 3) cap intermediates to MaxFrames-1, keeping the most-informative (largest change vs. predecessor)
            int maxInt = Math.Max(0, opts.MaxFrames - 1);
            if (kept.Count > maxInt) kept = TrimToMostInformative(kept, maxInt);

            foreach (var s in kept)
                result.Add(new ClipFrame { Jpeg = s.Jpeg, OffsetMs = s.OffsetMs, IsSettled = false, HasGrid = false });
            return result;
        }

        private static List<ClipSample> TrimToMostInformative(List<ClipSample> kept, int max)
        {
            if (max <= 0) return new List<ClipSample>();
            if (kept.Count <= max) return kept;
            var scored = new List<(ClipSample s, double score)>();
            byte[]? prev = null;
            foreach (var s in kept) { double sc = prev == null ? double.MaxValue : ThumbDelta(prev, s.Thumb); scored.Add((s, sc)); prev = s.Thumb; }
            return scored.OrderByDescending(t => t.score).Take(max).Select(t => t.s).OrderBy(t => t.OffsetMs).ToList();
        }

        /// <summary>Capture a screen region into a small gridless JPEG plus a 32×32 grayscale diff thumbnail.</summary>
        private (byte[] jpeg, byte[] thumb) CaptureSampleRegion(int x, int y, int w, int h, int maxWidth, long quality)
        {
            w = Math.Max(1, w); h = Math.Max(1, h);
            using var full = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

            var thumb = GrayThumb(full);
            double scale = (maxWidth > 0 && w > maxWidth) ? (double)maxWidth / w : 1.0;
            if (scale >= 1.0) return (EncodeJpeg(full, quality), thumb);

            int sw = Math.Max(1, (int)Math.Round(w * scale));
            int sh = Math.Max(1, (int)Math.Round(h * scale));
            using var scaled = new Bitmap(sw, sh, PixelFormat.Format24bppRgb);
            using (var g2 = Graphics.FromImage(scaled)) { g2.InterpolationMode = InterpolationMode.HighQualityBicubic; g2.DrawImage(full, 0, 0, sw, sh); }
            return (EncodeJpeg(scaled, quality), thumb);
        }

        /// <summary>32×32 grayscale thumbnail of an encoded JPEG, for cheap perceptual frame-difference.</summary>
        public static byte[] GrayThumb(byte[] jpeg)
        {
            try { using var src = (Bitmap)Image.FromStream(new MemoryStream(jpeg)); return GrayThumb(src); }
            catch { return new byte[ThumbW * ThumbH]; }
        }

        private static byte[] GrayThumb(Bitmap src)
        {
            var thumb = new byte[ThumbW * ThumbH];
            using var small = new Bitmap(ThumbW, ThumbH, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(src, 0, 0, ThumbW, ThumbH); }
            var data = small.LockBits(new Rectangle(0, 0, ThumbW, ThumbH), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = data.Stride;
                var buf = new byte[stride * ThumbH];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                for (int y = 0; y < ThumbH; y++)
                    for (int x = 0; x < ThumbW; x++)
                    {
                        int o = y * stride + x * 3; // BGR
                        thumb[y * ThumbW + x] = (byte)((buf[o + 2] * 299 + buf[o + 1] * 587 + buf[o] * 114) / 1000);
                    }
            }
            finally { small.UnlockBits(data); }
            return thumb;
        }

        /// <summary>Mean absolute luma delta between two equal-size thumbnails (0 = identical, 255 = inverted).</summary>
        public static double ThumbDelta(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length == 0 || a.Length != b.Length) return 255;
            long sum = 0;
            for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
            return (double)sum / a.Length;
        }

        /// <summary>Background sampler for one clip. Samples a fixed region at the configured fps; once
        /// <see cref="RequestFinish"/> is signalled (the action has fired) it keeps sampling until the screen
        /// goes stable (settled) or the window cap elapses — adaptive, bounded, never a hard task timeout.</summary>
        public sealed class ClipRecorder
        {
            private readonly ScreenCapturer cap;
            private readonly int x, y, w, h;
            private readonly ClipCaptureOptions opts;
            private readonly List<ClipSample> samples = new();
            private readonly object gate = new();
            private readonly Stopwatch sw = Stopwatch.StartNew();
            private readonly CancellationTokenSource cts = new();
            private volatile bool finishing;
            private Task? loop;

            internal ClipRecorder(ScreenCapturer cap, int x, int y, int w, int h, ClipCaptureOptions opts)
            { this.cap = cap; this.x = x; this.y = y; this.w = w; this.h = h; this.opts = opts; }

            internal void Start() => loop = Task.Run(RunAsync);

            private async Task RunAsync()
            {
                int interval = Math.Max(20, 1000 / Math.Clamp(opts.SampleFps, 2, 30));
                int rawCap = Math.Clamp(opts.WindowMs / interval + 6, 4, 60);
                int settleStable = 0;
                long finishAtMs = -1;
                byte[]? prevThumb = null;

                while (!cts.IsCancellationRequested)
                {
                    (byte[] jpeg, byte[] thumb) s;
                    try { lock (cap.captureLock) s = cap.CaptureSampleRegion(x, y, w, h, opts.SampleMaxWidth, opts.SampleQuality); }
                    catch { break; }

                    bool full;
                    lock (gate) { if (samples.Count < rawCap) samples.Add(new ClipSample(s.jpeg, s.thumb, (int)sw.ElapsedMilliseconds)); full = samples.Count >= rawCap; }

                    if (finishing)
                    {
                        if (finishAtMs < 0) finishAtMs = sw.ElapsedMilliseconds;
                        double d = prevThumb == null ? 255 : ThumbDelta(prevThumb, s.thumb);
                        if (d < Math.Max(1, opts.MotionThreshold)) settleStable++; else settleStable = 0;
                        bool settled = settleStable >= 2;
                        bool windowElapsed = sw.ElapsedMilliseconds - finishAtMs >= opts.WindowMs;
                        if (settled || windowElapsed || full) break;
                    }
                    prevThumb = s.thumb;
                    try { await Task.Delay(interval, cts.Token); } catch { break; }
                }
            }

            /// <summary>Signal that the action has fired — start watching for the screen to settle.</summary>
            public void RequestFinish() => finishing = true;

            /// <summary>Stop the sampler (after the settle window) and return all captured samples.</summary>
            public async Task<List<ClipSample>> FinishAsync(CancellationToken ct)
            {
                finishing = true;
                try { if (loop != null) await loop.WaitAsync(ct); }
                catch { cts.Cancel(); try { if (loop != null) await loop; } catch { } }
                lock (gate) return new List<ClipSample>(samples);
            }
        }
    }
}
