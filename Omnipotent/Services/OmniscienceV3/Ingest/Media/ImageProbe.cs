using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Media
{
    /// <summary>What reading an image established. Width and height are of the upright image.</summary>
    public sealed record ImageFacts(int Width, int Height, int Frames, int Orientation, long PerceptualHash, string? TakenLocal, int? TakenOffsetMinutes,
        byte[] Thumbnail, string ThumbnailMime);

    /// <summary>The bytes are not a usable image. <see cref="Unsupported"/> = a real format this build cannot decode (kept, not quarantined).</summary>
    public sealed class ImageUnreadableException : Exception
    {
        public ImageUnreadableException(string message, bool unsupported = false) : base(message) => Unsupported = unsupported;

        public bool Unsupported { get; }
    }

    /// <summary>
    /// Reads an image once and derives everything later steps need (plan I-Media): its upright size, whether it
    /// is animated, when it was taken, a perceptual hash and a thumbnail.
    /// <list type="bullet">
    /// <item><b>Orientation first.</b> Phone photos are stored sideways with an EXIF note; every consumer (OCR,
    /// faces, captions, the viewer) gets the upright image, so "on the left" means the same thing everywhere.</item>
    /// <item><b>Taken time is not posted time.</b> EXIF DateTimeOriginal is a wall-clock reading; it is returned as
    /// such, with the offset when the camera recorded one, and resolved by the caller.</item>
    /// <item><b>Location is never read.</b> The EXIF GPS directory is not parsed, not even to discard it.</item>
    /// <item><b>Bounded.</b> Images above <see cref="MaxPixels"/> are refused rather than decoded (decompression bombs).</item>
    /// </list>
    /// </summary>
    public static class ImageProbe
    {
        public const long MaxPixels = 120_000_000;
        public const int DefaultThumbnail = 320;

        public static ImageFacts Probe(string path, int thumbnailMax = DefaultThumbnail)
        {
            using var codec = Open(path);
            var info = codec.Info;
            int frames = Math.Max(1, codec.FrameCount);
            var origin = codec.EncodedOrigin;
            using var first = Decode(codec, 0);
            using var upright = Upright(first, origin);

            var (takenLocal, offset) = codec.EncodedFormat == SKEncodedImageFormat.Jpeg ? ExifReader.TakenAt(path) : (null, null);
            return new ImageFacts(upright.Width, upright.Height, frames, (int)origin == 0 ? 1 : (int)origin, DHash(upright), takenLocal, offset,
                Thumbnail(upright, thumbnailMax), "image/webp");
        }

        /// <summary>
        /// Upright frames for vision steps: the image itself, or for an animation up to <paramref name="maxFrames"/>
        /// spread from first to last (a reaction GIF is judged on three frames, not three hundred). The caller disposes them.
        /// </summary>
        public static IReadOnlyList<SKBitmap> LoadFrames(string path, int maxFrames = 3)
        {
            using var codec = Open(path);
            int frames = Math.Max(1, codec.FrameCount);
            var picks = frames <= maxFrames
                ? Enumerable.Range(0, frames).ToList()
                : Enumerable.Range(0, maxFrames).Select(i => (int)Math.Round(i * (frames - 1) / (double)(maxFrames - 1))).Distinct().ToList();
            var result = new List<SKBitmap>(picks.Count);
            try
            {
                foreach (int index in picks)
                {
                    using var raw = Decode(codec, index);
                    result.Add(Upright(raw, codec.EncodedOrigin, copy: true));
                }
                return result;
            }
            catch
            {
                foreach (var b in result) b.Dispose();
                throw;
            }
        }

        private static SKCodec Open(string path)
        {
            SKCodec? codec;
            try
            {
                codec = SKCodec.Create(path, out var result);
                if (codec == null)
                {
                    // Not decodable here: say whether it is a real format we lack (HEIC, AVIF) or just not an image.
                    string? sniffed = Sniff(path);
                    if (sniffed is "image/heic" or "image/avif")
                        throw new ImageUnreadableException($"{sniffed} images cannot be decoded by this build; the file is kept as it is.", unsupported: true);
                    throw new ImageUnreadableException($"The file is not a readable image ({result}).");
                }
            }
            catch (IOException ex)
            {
                throw new ImageUnreadableException("The file could not be read: " + ex.Message);
            }
            int width = codec.Info.Width, height = codec.Info.Height;
            if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            {
                codec.Dispose();
                throw new ImageUnreadableException($"The image claims {width}×{height} pixels, beyond what is decoded.", unsupported: true);
            }
            return codec;
        }

        private static string? Sniff(string path)
        {
            try
            {
                using var f = File.OpenRead(path);
                Span<byte> head = stackalloc byte[32];
                int read = f.Read(head);
                return MediaSniffer.Detect(head[..read]);
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static SKBitmap Decode(SKCodec codec, int frame)
        {
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var bitmap = new SKBitmap(info);
            var result = codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(frame));
            if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput) return bitmap; // a truncated tail still shows most of the picture
            bitmap.Dispose();
            throw new ImageUnreadableException($"The image data is damaged ({result}).");
        }

        /// <summary>Applies the EXIF orientation. Returns <paramref name="source"/> itself when nothing needs doing, unless a copy is asked for.</summary>
        private static SKBitmap Upright(SKBitmap source, SKEncodedOrigin origin, bool copy = false)
        {
            if (origin is SKEncodedOrigin.TopLeft or 0) return copy ? source.Copy() : Borrow(source);
            bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            int w = source.Width, h = source.Height;
            var upright = new SKBitmap(new SKImageInfo(swap ? h : w, swap ? w : h, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(upright);
            canvas.Clear(SKColors.Transparent);
            // Each matrix maps a stored pixel (x, y) to where it belongs in the upright image.
            canvas.SetMatrix(origin switch
            {
                SKEncodedOrigin.TopRight => Matrix(-1, 0, w, 0, 1, 0),      // mirrored
                SKEncodedOrigin.BottomRight => Matrix(-1, 0, w, 0, -1, h),  // upside down
                SKEncodedOrigin.BottomLeft => Matrix(1, 0, 0, 0, -1, h),    // flipped
                SKEncodedOrigin.LeftTop => Matrix(0, 1, 0, 1, 0, 0),        // transposed
                SKEncodedOrigin.RightTop => Matrix(0, -1, h, 1, 0, 0),      // needs a quarter turn clockwise
                SKEncodedOrigin.RightBottom => Matrix(0, -1, h, -1, 0, w),  // transverse
                SKEncodedOrigin.LeftBottom => Matrix(0, 1, 0, -1, 0, w),    // needs a quarter turn anticlockwise
                _ => SKMatrix.Identity,
            });
            canvas.DrawBitmap(source, 0, 0);
            return upright;
        }

        private static SKMatrix Matrix(float scaleX, float skewX, float transX, float skewY, float scaleY, float transY) =>
            new(scaleX, skewX, transX, skewY, scaleY, transY, 0, 0, 1);

        /// <summary>A second handle on the same pixels, so callers can dispose both results uniformly.</summary>
        private static SKBitmap Borrow(SKBitmap source)
        {
            var alias = new SKBitmap();
            source.ExtractSubset(alias, new SKRectI(0, 0, source.Width, source.Height));
            return alias;
        }

        /// <summary>
        /// Difference hash: the image reduced to 9×8 grey pixels, one bit per horizontal neighbour pair. Re-encoded,
        /// resized or lightly cropped copies of an image land within a few bits of each other, which is how a repost
        /// is recognised as the same picture (plan N1 duplicates, N2 near-duplicates).
        /// </summary>
        private static long DHash(SKBitmap image)
        {
            const int Columns = 9, Rows = 8;
            // Each cell is the true mean brightness of its share of the picture (not a sampled pixel), so the same
            // picture at any size or compression lands on nearly the same 72 numbers.
            Span<double> cells = stackalloc double[Columns * Rows];
            int w = image.Width, h = image.Height;
            if (w < Columns || h < Rows)
            {
                using var grown = image.Resize(new SKImageInfo(Columns * 4, Rows * 4, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear))
                                  ?? throw new ImageUnreadableException("The image could not be scaled.");
                return DHash(grown);
            }
            ReadOnlySpan<byte> pixels = image.GetPixelSpan();
            int stride = image.RowBytes;
            for (int cy = 0; cy < Rows; cy++)
            {
                int y0 = cy * h / Rows, y1 = Math.Max(y0 + 1, (cy + 1) * h / Rows);
                for (int cx = 0; cx < Columns; cx++)
                {
                    int x0 = cx * w / Columns, x1 = Math.Max(x0 + 1, (cx + 1) * w / Columns);
                    long sum = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        var row = pixels.Slice(y * stride + x0 * 4, (x1 - x0) * 4);
                        // Premultiplied RGBA: transparent pixels already count as dark.
                        for (int i = 0; i < row.Length; i += 4) sum += row[i] * 299 + row[i + 1] * 587 + row[i + 2] * 114;
                    }
                    cells[cy * Columns + cx] = sum / (double)((y1 - y0) * (x1 - x0));
                }
            }
            ulong hash = 0;
            for (int y = 0; y < Rows; y++)
            {
                for (int x = 0; x < Columns - 1; x++)
                {
                    hash <<= 1;
                    if (cells[y * Columns + x] > cells[y * Columns + x + 1]) hash |= 1;
                }
            }
            return unchecked((long)hash);
        }

        /// <summary>How many bits two perceptual hashes differ in (0 = the same picture, ≤ 10 = very likely a copy).</summary>
        public static int HashDistance(long a, long b) => System.Numerics.BitOperations.PopCount(unchecked((ulong)(a ^ b)));

        private static byte[] Thumbnail(SKBitmap upright, int max)
        {
            double scale = Math.Min(1.0, max / (double)Math.Max(upright.Width, upright.Height));
            int w = Math.Max(1, (int)Math.Round(upright.Width * scale)), h = Math.Max(1, (int)Math.Round(upright.Height * scale));
            using var small = scale < 1.0
                ? upright.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                : upright.Copy();
            if (small == null) throw new ImageUnreadableException("The image could not be scaled.");
            using var data = small.Encode(SKEncodedImageFormat.Webp, 80) ?? throw new ImageUnreadableException("The thumbnail could not be encoded.");
            return data.ToArray();
        }
    }

    /// <summary>
    /// The two EXIF fields v3 reads from a JPEG: when the picture was taken, and the camera's UTC offset at that
    /// moment if it wrote one. It walks IFD0 → the Exif directory and nothing else: the GPS directory's pointer
    /// (tag 0x8825) is never followed, so no location can be read by accident (plan I-Media).
    /// </summary>
    public static class ExifReader
    {
        private const ushort TagExifIfd = 0x8769, TagDateTimeOriginal = 0x9003, TagOffsetTimeOriginal = 0x9011;
        private const int MaxSegmentScan = 1 << 20;

        /// <summary>("2024-02-14T10:31:00", offset minutes or null), or (null, null) when the file says nothing usable.</summary>
        public static (string? TakenLocal, int? OffsetMinutes) TakenAt(string path)
        {
            try
            {
                using var f = File.OpenRead(path);
                byte[] head = new byte[(int)Math.Min(f.Length, MaxSegmentScan)];
                int read = f.Read(head, 0, head.Length);
                return TakenAt(head.AsSpan(0, read));
            }
            catch (IOException)
            {
                return (null, null);
            }
        }

        public static (string? TakenLocal, int? OffsetMinutes) TakenAt(ReadOnlySpan<byte> jpeg)
        {
            var tiff = FindExif(jpeg);
            if (tiff.Length < 8) return (null, null);
            bool little = tiff[0] == 'I' && tiff[1] == 'I';
            if (!little && !(tiff[0] == 'M' && tiff[1] == 'M')) return (null, null);
            if (U16(tiff, 2, little) != 42) return (null, null);

            uint? exifIfd = null;
            foreach (var (tag, _, _, value) in Entries(tiff, U32(tiff, 4, little), little))
                if (tag == TagExifIfd) exifIfd = value;
            if (exifIfd == null) return (null, null);

            string? taken = null, offset = null;
            foreach (var (tag, type, count, value) in Entries(tiff, exifIfd.Value, little))
            {
                if (tag == TagDateTimeOriginal) taken = Ascii(tiff, type, count, value);
                else if (tag == TagOffsetTimeOriginal) offset = Ascii(tiff, type, count, value);
            }
            // "2024:02:14 10:31:00"; cameras with no clock set write zeros or spaces.
            if (taken == null || !DateTime.TryParseExact(taken, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall) || wall.Year < 1990)
                return (null, null);
            return (wall.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), ParseOffset(offset));
        }

        /// <summary>The TIFF block of the first APP1 "Exif" segment, or empty.</summary>
        private static ReadOnlySpan<byte> FindExif(ReadOnlySpan<byte> jpeg)
        {
            if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return default;
            int pos = 2;
            while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
            {
                byte marker = jpeg[pos + 1];
                if (marker == 0xDA || marker == 0xD9) break; // image data: no more metadata
                int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(pos + 2, 2));
                if (length < 2 || pos + 2 + length > jpeg.Length) break;
                var body = jpeg.Slice(pos + 4, length - 2);
                if (marker == 0xE1 && body.Length > 6 && body[0] == 'E' && body[1] == 'x' && body[2] == 'i' && body[3] == 'f' && body[4] == 0 && body[5] == 0)
                    return body[6..];
                pos += 2 + length;
            }
            return default;
        }

        private static List<(ushort Tag, ushort Type, uint Count, uint Value)> Entries(ReadOnlySpan<byte> tiff, uint offset, bool little)
        {
            var result = new List<(ushort, ushort, uint, uint)>();
            if (offset + 2 > tiff.Length) return result;
            int count = U16(tiff, (int)offset, little);
            for (int i = 0; i < count && i < 512; i++)
            {
                int at = (int)offset + 2 + i * 12;
                if (at + 12 > tiff.Length) break;
                result.Add((U16(tiff, at, little), U16(tiff, at + 2, little), U32(tiff, at + 4, little), U32(tiff, at + 8, little)));
            }
            return result;
        }

        private static string? Ascii(ReadOnlySpan<byte> tiff, ushort type, uint count, uint valueOffset)
        {
            if (type != 2 || count == 0 || count > 64 || valueOffset + count > tiff.Length) return null;
            return Encoding.ASCII.GetString(tiff.Slice((int)valueOffset, (int)count)).TrimEnd('\0', ' ');
        }

        private static int? ParseOffset(string? text)
        {
            if (text == null || text.Length != 6 || text[0] is not ('+' or '-') || text[3] != ':') return null;
            if (!int.TryParse(text.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
                || !int.TryParse(text.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) || hours > 14 || minutes > 59) return null;
            int total = hours * 60 + minutes;
            return text[0] == '-' ? -total : total;
        }

        private static ushort U16(ReadOnlySpan<byte> b, int at, bool little) =>
            little ? BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(at, 2)) : BinaryPrimitives.ReadUInt16BigEndian(b.Slice(at, 2));

        private static uint U32(ReadOnlySpan<byte> b, int at, bool little) =>
            little ? BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(at, 4)) : BinaryPrimitives.ReadUInt32BigEndian(b.Slice(at, 4));
    }
}
