using SkiaSharp;

namespace Omnipotent.Services.OmniBlox.Agents
{
    /// <summary>
    /// Pictures on their way to the model. The renderer's lossless PNGs are made for the dashboard: a 1280×760 world
    /// render is 0.6-1.6 MB, a third more again as base64, and a cycle keeps every picture it was shown (its history
    /// is append-only for the prefix cache). Four renders made a 6.7 MB request, past what AIRouter's front door
    /// accepts (HTTP 413), and the cycle was lost. As JPEG the same renders are 0.1-0.28 MB and read the same.
    /// </summary>
    public static class ModelPictures
    {
        public const int JpegQuality = 85;

        /// <summary>
        /// The picture as the model gets it: an opaque PNG becomes a JPEG when that is smaller. One with transparency
        /// (a library model's thumbnail) is left as it is, since flattening it would change what the model sees; so is
        /// anything that does not decode.
        /// </summary>
        public static byte[] ForModel(byte[] picture)
        {
            if (picture.Length == 0 || IsJpeg(picture)) return picture;
            using var bitmap = SKBitmap.Decode(picture);
            if (bitmap == null || !IsOpaque(bitmap)) return picture;
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            return data != null && data.Size < picture.Length ? data.ToArray() : picture;
        }

        public static string MimeType(byte[] picture) => IsJpeg(picture) ? "image/jpeg" : "image/png";

        private static bool IsJpeg(byte[] b) => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

        private static bool IsOpaque(SKBitmap bitmap)
        {
            if (bitmap.AlphaType == SKAlphaType.Opaque) return true;
            if (bitmap.ColorType is not (SKColorType.Rgba8888 or SKColorType.Bgra8888)) return false;
            // The renderer draws on an RGBA surface, so its pictures carry an alpha channel that is always full.
            var pixels = bitmap.GetPixelSpan();
            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = y * bitmap.RowBytes;
                for (int x = 0; x < bitmap.Width; x++)
                    if (pixels[row + x * 4 + 3] != 255) return false;
            }
            return true;
        }
    }
}
