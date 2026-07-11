using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Omnipotent.Services.ComputerControl;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Turns a VNC BGRA framebuffer into a JPEG for the model / website live view. Uses
    /// System.Drawing to match the rest of the codebase's imaging (ScreenCapturer.EncodeJpeg).
    /// Optionally downscales — most consumption is the pixel-diff gate and vision, not a human,
    /// so frames are kept small.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class VncFrameEncoder
    {
        public static byte[] EncodeJpeg(byte[] bgra, int width, int height, int maxWidth = 1280, long quality = 70)
        {
            using var bmp = BgraToBitmap(bgra, width, height);
            if (width > maxWidth)
            {
                int sw = maxWidth;
                int sh = (int)(height * ((double)maxWidth / width));
                using var scaled = new Bitmap(bmp, sw, sh);
                return Encode(scaled, quality);
            }
            return Encode(bmp, quality);
        }

        /// <summary>Encodes the framebuffer losslessly for OCR.  Local text recognition wants the
        /// original pixels, not the quality-70 display JPEG whose ringing around small UI glyphs was
        /// a major cause of missed matches.  Coordinates stay in native framebuffer space.</summary>
        public static byte[] EncodePng(byte[] bgra, int width, int height)
        {
            using var bmp = BgraToBitmap(bgra, width, height);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        private static Bitmap BgraToBitmap(byte[] bgra, int width, int height)
        {
            ArgumentNullException.ThrowIfNull(bgra);
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), $"Invalid framebuffer geometry {width}x{height}.");
            int requiredBytes = checked(width * height * 4);
            if (bgra.Length < requiredBytes)
                throw new ArgumentException($"Framebuffer has {bgra.Length} bytes; {requiredBytes} are required for {width}x{height} BGRA.", nameof(bgra));

            // RFB's fourth byte is padding (BGRX), not a meaningful alpha channel. Treating it as
            // ARGB made servers that emit X=0 look transparent and could encode as black/white
            // blocks. Format32bppRgb consumes the same B,G,R,X byte order but forces opaque pixels.
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            var rect = new Rectangle(0, 0, width, height);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                // BGRX framebuffer matches Format32bppRgb's byte order on little-endian; copy
                // row by row to honour the bitmap's stride.
                int rowBytes = width * 4;
                for (int y = 0; y < height; y++)
                    Marshal.Copy(bgra, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        /// <summary>Encodes a final model-facing frame.  VNC coordinates already match its
        /// framebuffer, so the grid is applied after encoding without any scale translation.</summary>
        public static byte[] EncodeGriddedJpeg(byte[] bgra, int width, int height, int maxWidth = 1280, long quality = 70)
            => ComputerVision.AddCoordinateGrid(EncodeJpeg(bgra, width, height, maxWidth, quality));

        private static byte[] Encode(Bitmap bmp, long quality)
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
