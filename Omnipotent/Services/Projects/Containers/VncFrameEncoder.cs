using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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
            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                // BGRA framebuffer matches Format32bppArgb's byte order on little-endian; copy
                // row by row to honour the bitmap's stride.
                int rowBytes = width * 4;
                for (int y = 0; y < height; y++)
                    Marshal.Copy(bgra, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
            }
            finally { bmp.UnlockBits(data); }

            if (width > maxWidth)
            {
                int sw = maxWidth;
                int sh = (int)(height * ((double)maxWidth / width));
                using var scaled = new Bitmap(bmp, sw, sh);
                return Encode(scaled, quality);
            }
            return Encode(bmp, quality);
        }

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
