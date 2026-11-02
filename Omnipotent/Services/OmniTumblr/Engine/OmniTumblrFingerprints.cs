using Omnipotent.Services.OmniTumblr.Models;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    /// <summary>Reads a media file's fingerprint (ffmpeg in production, a fake in tests).</summary>
    internal interface IMediaFingerprinter
    {
        Task<MediaFingerprint?> FingerprintAsync(string path, CancellationToken ct);
    }

    /// <summary>
    /// A frame's 64-bit difference hash (bit set where a 9×8 cell is brighter than its right neighbour) and
    /// which bits mean something: neighbours that differ by at least <see cref="OmniTumblrFingerprintMath.InformativeStep"/>.
    /// Bits between near-equal cells (black bars, white caption areas) are noise and are not compared.
    /// </summary>
    internal readonly record struct FrameHash(ulong Bits, ulong Mask)
    {
        /// <summary>A flat frame (black, white, a fade): nothing to recognise.</summary>
        public bool IsFlat => Bits == 0;
    }

    /// <summary>
    /// Builds and compares <see cref="MediaFingerprint"/>s: difference hashes of a frame every half second, both
    /// of the whole frame and of the part of the picture that moves. Reposts often add a static caption bar, a
    /// border or padding; none of that changes over time, so the moving region crops it away and the repost
    /// hashes like the original. Two clips are the same when most frames agree on the bits that mean something,
    /// allowing up to 2 s trimmed off either one, on the whole frame or the moving region, as is or mirrored.
    /// <para>
    /// Calibrated on 733 real MemeScraper reels (Oct–Nov 2026, with scratch scripts on the same ffmpeg output).
    /// Re-encoded, trimmed, quieter and mirrored copies of 16 sample reels were all recognised, and so were
    /// captioned copies of every moving one. A still picture under a new caption bar is not, and neither is a
    /// repost that re-crops or zooms the clip. Across all 261,726 pairs, the only matches were genuine: one clip
    /// that one account posted 51 times. Three pairs of different letterboxed or still clips matched until the
    /// informative-bit masks and the strict still-clip rule were added. Matching on the loudness envelope was
    /// tried and dropped: it flagged 748 pairs of different memes that use the same trending sound, and their
    /// beat-synced cuts made even their motion agree.
    /// </para>
    /// </summary>
    internal static class OmniTumblrFingerprintMath
    {
        public const int FramesPerSecond = 2;
        /// <summary>Frames are read at this size (portrait, like reels) to find the moving region.</summary>
        public const int GridWidth = 72, GridHeight = 128;
        /// <summary>Only the first two minutes are read: enough to recognise a clip, cheap to compare.</summary>
        public const int MaxSeconds = 120;

        internal const int FlatRange = 12;
        /// <summary>Neighbouring cells closer than this in brightness give a noise bit.</summary>
        internal const double InformativeStep = 4;
        /// <summary>A pair of frames with fewer meaningful bits than this between them says nothing either way.</summary>
        internal const int MinInformativeBits = 8;
        /// <summary>Two frames are alike when at most this share of their meaningful bits differ.</summary>
        internal const double BitsTolerance = 0.16;
        /// <summary>
        /// A still clip (a picture held over music: at most this many distinct frames) rests on one frame's 64
        /// bits rather than on many frames agreeing, so it must match almost exactly. Two different night-skyline
        /// pictures were within 3 of 21 meaningful bits; true copies of still clips were within 0.
        /// </summary>
        internal const int StillDistinctFrames = 2;
        internal const int StillMinInformativeBits = 16;
        internal const double StillBitsTolerance = 0.05;
        internal const double FrameAgreement = 0.6;
        internal const int MaxFrameShift = 2 * FramesPerSecond;
        /// <summary>A pixel whose brightness varies more than this (standard deviation) over the clip is part of the moving picture.</summary>
        internal const double ActivePixelSpread = 4;
        /// <summary>A row or column is inside the moving picture when this share of its pixels moves.</summary>
        internal const double ActiveLineShare = 0.1;
        /// <summary>A moving region smaller than this share of the frame is not trusted (a sticker, a blinking cursor).</summary>
        internal const double MinActiveArea = 0.15;

        /// <summary>The hash of 9×8 greyscale cells.</summary>
        public static FrameHash DifferenceHash(ReadOnlySpan<byte> gray9x8)
        {
            Span<double> cells = stackalloc double[72];
            for (int i = 0; i < 72; i++) cells[i] = gray9x8[i];
            return DifferenceHash(cells);
        }

        private static FrameHash DifferenceHash(ReadOnlySpan<double> cells)
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (double v in cells)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
            if (max - min < FlatRange) return default;
            ulong bits = 0, mask = 0;
            int bit = 0;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++, bit++)
                {
                    double step = cells[y * 9 + x] - cells[y * 9 + x + 1];
                    if (step > 0) bits |= 1UL << bit;
                    if (Math.Abs(step) >= InformativeStep) mask |= 1UL << bit;
                }
            return new FrameHash(bits == 0 ? 1UL : bits, mask); // Bits 0 is reserved for flat frames
        }

        /// <summary>
        /// Consecutive raw <see cref="GridWidth"/>×<see cref="GridHeight"/> greyscale frames (ffmpeg rawvideo) →
        /// the hashes of each whole frame, and of each frame's moving region (one rectangle for the whole clip).
        /// </summary>
        public static (FrameHash[] Whole, FrameHash[] Region) HashFrames(ReadOnlySpan<byte> raw)
        {
            const int size = GridWidth * GridHeight;
            int count = raw.Length / size;
            var region = MovingRegion(raw[..(count * size)], count);
            var whole = new FrameHash[count];
            var moving = new FrameHash[count];
            for (int f = 0; f < count; f++)
            {
                var frame = raw.Slice(f * size, size);
                whole[f] = HashArea(frame, (0, GridHeight, 0, GridWidth));
                moving[f] = HashArea(frame, region);
            }
            return (whole, moving);
        }

        /// <summary>Averages the rectangle into 9×8 cells and hashes them.</summary>
        private static FrameHash HashArea(ReadOnlySpan<byte> frame, (int Top, int Bottom, int Left, int Right) area)
        {
            Span<double> cells = stackalloc double[72];
            int height = area.Bottom - area.Top, width = area.Right - area.Left;
            for (int row = 0; row < 8; row++)
            {
                int y0 = area.Top + Edge(row, 8, height), y1 = Math.Max(area.Top + Edge(row + 1, 8, height), y0 + 1);
                for (int col = 0; col < 9; col++)
                {
                    int x0 = area.Left + Edge(col, 9, width), x1 = Math.Max(area.Left + Edge(col + 1, 9, width), x0 + 1);
                    int sum = 0;
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++) sum += frame[y * GridWidth + x];
                    cells[row * 9 + col] = (double)sum / ((y1 - y0) * (x1 - x0));
                }
            }
            return DifferenceHash(cells);
        }

        private static int Edge(int index, int bins, int length) => (int)Math.Round((double)index * length / bins);

        /// <summary>
        /// The rectangle (rows top..bottom, columns left..right, exclusive ends) that changes over the clip:
        /// what is left once static bars, borders and captions are cut away. The whole frame when too little
        /// moves to tell, as for a still image or a picture slideshow.
        /// </summary>
        internal static (int Top, int Bottom, int Left, int Right) MovingRegion(ReadOnlySpan<byte> frames, int count)
        {
            const int size = GridWidth * GridHeight;
            var whole = (0, GridHeight, 0, GridWidth);
            if (count < 3) return whole;
            var sum = new double[size];
            var squares = new double[size];
            for (int f = 0; f < count; f++)
                for (int i = 0; i < size; i++)
                {
                    double v = frames[f * size + i];
                    sum[i] += v;
                    squares[i] += v * v;
                }
            var rowMoving = new int[GridHeight];
            var colMoving = new int[GridWidth];
            double limit = ActivePixelSpread * ActivePixelSpread;
            for (int i = 0; i < size; i++)
            {
                double mean = sum[i] / count;
                if (squares[i] / count - mean * mean <= limit) continue;
                rowMoving[i / GridWidth]++;
                colMoving[i % GridWidth]++;
            }
            int top = Array.FindIndex(rowMoving, n => n >= ActiveLineShare * GridWidth);
            int bottom = Array.FindLastIndex(rowMoving, n => n >= ActiveLineShare * GridWidth) + 1;
            int left = Array.FindIndex(colMoving, n => n >= ActiveLineShare * GridHeight);
            int right = Array.FindLastIndex(colMoving, n => n >= ActiveLineShare * GridHeight) + 1;
            if (top < 0 || left < 0 || (bottom - top) * (right - left) < MinActiveArea * size) return whole;
            return (top, bottom, left, right);
        }

        /// <summary>The hash the horizontally mirrored frame would have: each row's bits reversed (and inverted, for the brightness bits).</summary>
        public static FrameHash Mirror(FrameHash hash)
        {
            if (hash.IsFlat) return hash;
            ulong bits = 0, mask = 0;
            for (int row = 0; row < 8; row++)
            {
                int shift = row * 8;
                ulong rowBits = (hash.Bits >> shift) & 0xFF, rowMask = (hash.Mask >> shift) & 0xFF;
                ulong reversedBits = 0, reversedMask = 0;
                for (int x = 0; x < 8; x++)
                {
                    if ((rowBits & (1UL << x)) != 0) reversedBits |= 1UL << (7 - x);
                    if ((rowMask & (1UL << x)) != 0) reversedMask |= 1UL << (7 - x);
                }
                bits |= (~reversedBits & 0xFF) << shift;
                mask |= reversedMask << shift;
            }
            return new FrameHash(bits == 0 ? 1UL : bits, mask);
        }

        public static MediaFingerprint Create(double? durationSeconds, FrameHash[] whole, FrameHash[] region) => new()
        {
            DurationSeconds = durationSeconds,
            Frames = Encode(whole),
            Region = Encode(region),
        };

        internal static string Encode(FrameHash[] hashes)
        {
            var bytes = new byte[hashes.Length * 16];
            for (int i = 0; i < hashes.Length; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 16), hashes[i].Bits);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 16 + 8), hashes[i].Mask);
            }
            return Convert.ToBase64String(bytes);
        }

        internal static FrameHash[] Decode(string? base64)
        {
            if (string.IsNullOrEmpty(base64)) return Array.Empty<FrameHash>();
            byte[] bytes = Convert.FromBase64String(base64);
            var hashes = new FrameHash[bytes.Length / 16];
            for (int i = 0; i < hashes.Length; i++)
                hashes[i] = new FrameHash(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i * 16)), BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i * 16 + 8)));
            return hashes;
        }

        /// <summary>Whether two fingerprints are the same clip (re-encoded, resized, trimmed a little, re-captioned or mirrored).</summary>
        public static bool IsSameMedia(MediaFingerprint a, MediaFingerprint b)
        {
            if (!SimilarLength(a, b)) return false;
            bool still = a.IsStill || b.IsStill;
            return FrameSimilarity(a.WholeHashes, b.WholeHashes, still) >= FrameAgreement
                || FrameSimilarity(a.RegionHashes, b.RegionHashes, still) >= FrameAgreement
                || FrameSimilarity(a.WholeHashes, b.MirroredWhole, still) >= FrameAgreement
                || FrameSimilarity(a.RegionHashes, b.MirroredRegion, still) >= FrameAgreement;
        }

        internal static bool SimilarLength(MediaFingerprint a, MediaFingerprint b) =>
            a.DurationSeconds is not double da || b.DurationSeconds is not double db
            || Math.Abs(da - db) <= Math.Max(3, 0.3 * Math.Max(da, db));

        /// <summary>
        /// Two frames agree on most of the bits that mean something to either (strictly, for <paramref name="still"/>
        /// clips). Null when they say too little to tell.
        /// </summary>
        internal static bool? Alike(FrameHash a, FrameHash b, bool still = false)
        {
            if (a.IsFlat || b.IsFlat) return null;
            ulong meaningful = a.Mask | b.Mask;
            int bits = BitOperations.PopCount(meaningful);
            if (bits < (still ? StillMinInformativeBits : MinInformativeBits)) return null;
            return BitOperations.PopCount((a.Bits ^ b.Bits) & meaningful) <= (still ? StillBitsTolerance : BitsTolerance) * bits;
        }

        /// <summary>How many visibly different frames a clip has: a new one each time a frame stops being alike the last kept one.</summary>
        internal static int DistinctFrames(FrameHash[] hashes)
        {
            int distinct = 0;
            FrameHash? last = null;
            foreach (var hash in hashes)
            {
                if (hash.IsFlat) continue;
                if (last is FrameHash previous && Alike(previous, hash) != false) continue;
                last = hash;
                distinct++;
            }
            return distinct;
        }

        /// <summary>
        /// The best share of alike frames over the allowed shifts, counting only shifts that compare at least
        /// half the longer clip. 0 when no shift compares enough.
        /// </summary>
        internal static double FrameSimilarity(FrameHash[] a, FrameHash[] b, bool still = false)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            int needed = Math.Max(Math.Min(3, Math.Min(a.Length, b.Length)), (int)Math.Ceiling(Math.Max(a.Length, b.Length) * 0.5));
            double best = 0;
            for (int shift = -MaxFrameShift; shift <= MaxFrameShift; shift++)
            {
                int compared = 0, alike = 0;
                for (int i = Math.Max(0, -shift); i < a.Length && i + shift < b.Length; i++)
                {
                    if (Alike(a[i], b[i + shift], still) is not bool same) continue;
                    compared++;
                    if (same) alike++;
                }
                if (compared >= needed) best = Math.Max(best, (double)alike / compared);
            }
            return best;
        }
    }

    /// <summary>
    /// Remembers each content key's fingerprint (persisted in fingerprints.json), computing it once from the
    /// media file. A file that can't be read is not retried for six hours. Unreadable media is judged by its
    /// key alone, so if ffmpeg stops working the repeat check quietly does nothing: a run of failures is
    /// therefore written to the event log.
    /// </summary>
    internal sealed class OmniTumblrFingerprints
    {
        /// <summary>A fingerprint is typically 1–3 KB of JSON (10 KB for a two-minute clip), so this keeps the file around 10 MB.</summary>
        public const int MaxStored = 5000;
        private const int FailuresBeforeWarning = 5;
        private static readonly TimeSpan RetryFailedAfter = TimeSpan.FromHours(6);
        private static readonly TimeSpan WarnEvery = TimeSpan.FromHours(12);

        private readonly OmniTumblrStore store;
        private readonly IMediaFingerprinter fingerprinter;
        private readonly Func<DateTime> clock;
        private readonly ConcurrentDictionary<string, DateTime> failedUtc = new(StringComparer.Ordinal);
        private int failuresInARow;
        private DateTime? warnedUtc;

        public OmniTumblrFingerprints(OmniTumblrStore store, IMediaFingerprinter fingerprinter, Func<DateTime> clock)
        {
            this.store = store;
            this.fingerprinter = fingerprinter;
            this.clock = clock;
        }

        /// <summary>The key a post's media is fingerprinted under: its content key, or the post itself for hand-made posts.</summary>
        public static string KeyOf(OmniTumblrPost post) => post.Content?.Key is { Length: > 0 } key ? key : "post:" + post.PostId;

        public MediaFingerprint? Cached(string key) => store.Read(s => s.Fingerprints.TryGetValue(key, out var f) ? f : null);

        /// <summary>Known, or not worth trying now (no file, or it failed recently).</summary>
        public bool Settled(string key, string? path) =>
            Cached(key) != null || path == null || !File.Exists(path)
            || (failedUtc.TryGetValue(key, out var failed) && clock() - failed < RetryFailedAfter);

        /// <summary>The fingerprint of <paramref name="key"/>'s media, computed from <paramref name="path"/> if needed. Null when unreadable.</summary>
        public async Task<MediaFingerprint?> GetAsync(string key, string? path, CancellationToken ct)
        {
            if (Cached(key) is MediaFingerprint cached) return cached;
            if (Settled(key, path)) return null;
            MediaFingerprint? fingerprint;
            try { fingerprint = await fingerprinter.FingerprintAsync(path!, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { fingerprint = null; }
            if (fingerprint == null)
            {
                DateTime now = clock();
                failedUtc[key] = now;
                if (Interlocked.Increment(ref failuresInARow) >= FailuresBeforeWarning && (warnedUtc is not DateTime warned || now - warned > WarnEvery))
                {
                    warnedUtc = now;
                    store.Mutate(s => s.AddEvent(EventLevel.Warning, "content.fingerprint",
                        $"Could not read the last {failuresInARow} videos with ffmpeg (last: {Path.GetFileName(path)}), so the same clip reposted under " +
                        "another reel can't be recognised; content is told apart by its key alone until ffmpeg works.", utc: now));
                }
                return null;
            }
            Interlocked.Exchange(ref failuresInARow, 0);
            fingerprint.ComputedUtc = clock();
            store.Mutate(s =>
            {
                s.Fingerprints[key] = fingerprint;
                if (s.Fingerprints.Count > MaxStored) Prune(s);
                s.MarkFingerprints();
            });
            return fingerprint;
        }

        /// <summary>Drops the oldest tenth of fingerprints that no post refers to (candidates looked at and passed over).</summary>
        private static void Prune(OmniTumblrState s)
        {
            var referenced = s.AllPosts().Select(KeyOf).ToHashSet(StringComparer.Ordinal);
            foreach (string key in s.Fingerprints.Where(f => !referenced.Contains(f.Key)).OrderBy(f => f.Value.ComputedUtc)
                         .Take(MaxStored / 10).Select(f => f.Key).ToList())
                s.Fingerprints.Remove(key);
        }
    }
}
