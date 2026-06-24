using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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

        public static byte[] EncodeJpeg(Bitmap bmp, long quality)
        {
            var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 1, 100));
            using var ms = new MemoryStream();
            bmp.Save(ms, encoder, ep);
            return ms.ToArray();
        }
    }
}
