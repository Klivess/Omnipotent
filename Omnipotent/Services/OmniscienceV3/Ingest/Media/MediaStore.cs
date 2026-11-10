using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Media
{
    /// <summary>States of <c>attachments.download_state</c>.</summary>
    public static class DownloadStates
    {
        /// <summary>Has a source URL and waits for the downloader.</summary>
        public const string Pending = "pending";
        /// <summary>The bytes are in the media store.</summary>
        public const string Done = "done";
        /// <summary>Every attempt failed; the reason is in <c>download_error</c>.</summary>
        public const string Failed = "failed";
        /// <summary>The source names the file but can no longer provide it (expired link, "media omitted").</summary>
        public const string Unavailable = "unavailable";
        /// <summary>Over the size cap for its kind: recorded, not stored.</summary>
        public const string TooLarge = "too_large";
        /// <summary>The bytes are not what they claim to be (corrupt, or a type that is never stored).</summary>
        public const string Quarantined = "quarantined";
    }

    /// <summary>An object in the content-addressed store.</summary>
    public sealed record StoredObject(string Sha256, long SizeBytes, string RelativePath, string? Mime);

    public sealed class MediaTooLargeException : Exception
    {
        public MediaTooLargeException(long limitBytes) : base($"The file is larger than the {limitBytes / (1024 * 1024)} MB limit for its kind.") => LimitBytes = limitBytes;

        public long LimitBytes { get; }
    }

    /// <summary>
    /// The content-addressed media store: one file per distinct content, named by its SHA-256, so the same
    /// image posted a hundred times is stored once (plan I-Media). Content is streamed to disk while it is
    /// hashed — never held in memory — and only moved into place once complete, so a crash leaves no
    /// half-written object under a valid name.
    /// </summary>
    public sealed class MediaStore
    {
        private const string TempFolder = "tmp";
        private readonly StorePaths paths;

        public MediaStore(StorePaths paths)
        {
            this.paths = paths;
            Directory.CreateDirectory(Path.Combine(paths.Media, TempFolder));
        }

        /// <summary><c>ab/cd/abcd…</c>: two levels keep directories small at millions of objects.</summary>
        public static string RelativePathFor(string sha256) => $"{sha256[..2]}/{sha256[2..4]}/{sha256}";

        public string FullPath(string relativePath) => MediaPaths.Resolve(paths, relativePath);

        public bool Exists(string sha256) => File.Exists(FullPath(RelativePathFor(sha256)));

        /// <summary>
        /// Stores <paramref name="content"/> and returns its address. The declared type is never trusted: the
        /// stored type is read from the first bytes when they are recognisable, else <paramref name="declaredMime"/>.
        /// </summary>
        /// <exception cref="MediaTooLargeException">The content is longer than <paramref name="maxBytes"/>; nothing is kept.</exception>
        public async Task<StoredObject> PutAsync(Stream content, string? declaredMime, long maxBytes, CancellationToken ct = default)
        {
            string temp = Path.Combine(paths.Media, TempFolder, Guid.NewGuid().ToString("N") + ".part");
            long total = 0;
            byte[] head = new byte[32];
            int headLength = 0;
            string sha;
            try
            {
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > maxBytes) throw new MediaTooLargeException(maxBytes);
                        if (headLength < head.Length)
                        {
                            int take = Math.Min(head.Length - headLength, read);
                            Array.Copy(buffer, 0, head, headLength, take);
                            headLength += take;
                        }
                        hash.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                    sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }

                string relative = RelativePathFor(sha);
                string final = FullPath(relative);
                if (File.Exists(final))
                {
                    File.Delete(temp);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                    try
                    {
                        File.Move(temp, final);
                    }
                    catch (IOException) when (File.Exists(final))
                    {
                        // Two writers stored the same content at once: the other one's copy is identical.
                        File.Delete(temp);
                    }
                }
                return new StoredObject(sha, total, relative, MediaSniffer.Detect(head.AsSpan(0, headLength)) ?? NullIfBlank(declaredMime));
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }

        public Stream OpenRead(string relativePath) =>
            new FileStream(FullPath(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        /// <summary>Records the object in corpus.db (inside the caller's ingest transaction).</summary>
        public static void Register(SqliteConnection c, SqliteTransaction tx, StoredObject o, long nowMs) =>
            c.Exec(tx, "INSERT OR IGNORE INTO cas_objects(sha256, size_bytes, mime, relative_path, stored_at_ms) VALUES($s,$n,$m,$p,$t)",
                ("$s", o.Sha256), ("$n", o.SizeBytes), ("$m", o.Mime), ("$p", o.RelativePath), ("$t", nowMs));

        /// <summary>Removes part files a crash left behind. Run once at start, before anything writes.</summary>
        public int SweepTemp()
        {
            int removed = 0;
            foreach (var file in Directory.EnumerateFiles(Path.Combine(paths.Media, TempFolder), "*.part"))
                if (TryDelete(file)) removed++;
            return removed;
        }

        private static bool TryDelete(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (IOException)
            {
                // Still open elsewhere; the next sweep takes it.
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>Reads a file's type from its first bytes. Extensions and declared types lie; magic numbers rarely do.</summary>
    public static class MediaSniffer
    {
        public static string? Detect(ReadOnlySpan<byte> head)
        {
            if (Starts(head, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return "image/png";
            if (Starts(head, 0xFF, 0xD8, 0xFF)) return "image/jpeg";
            if (Starts(head, 0x47, 0x49, 0x46, 0x38)) return "image/gif";
            if (Starts(head, 0x42, 0x4D)) return "image/bmp";
            if (IsRiff(head, "WEBP")) return "image/webp";
            if (IsRiff(head, "WAVE")) return "audio/wav";
            if (IsRiff(head, "AVI ")) return "video/x-msvideo";
            if (head.Length >= 12 && head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p')
            {
                string brand = System.Text.Encoding.ASCII.GetString(head.Slice(8, 4));
                if (brand is "heic" or "heix" or "hevc" or "mif1" or "msf1") return "image/heic";
                if (brand is "avif" or "avis") return "image/avif";
                if (brand is "M4A " or "M4B ") return "audio/mp4";
                if (brand is "qt  ") return "video/quicktime";
                return "video/mp4";
            }
            if (Starts(head, 0x1A, 0x45, 0xDF, 0xA3)) return "video/webm";
            if (Starts(head, 0x4F, 0x67, 0x67, 0x53)) return "audio/ogg";
            if (Starts(head, 0x49, 0x44, 0x33) || (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 && head[1] != 0xFF)) return "audio/mpeg";
            if (Starts(head, 0x66, 0x4C, 0x61, 0x43)) return "audio/flac";
            if (Starts(head, 0x25, 0x50, 0x44, 0x46)) return "application/pdf";
            if (Starts(head, 0x50, 0x4B, 0x03, 0x04)) return "application/zip";
            return null;
        }

        private static bool Starts(ReadOnlySpan<byte> head, params byte[] magic) => head.Length >= magic.Length && head[..magic.Length].SequenceEqual(magic);

        private static bool IsRiff(ReadOnlySpan<byte> head, string form) =>
            head.Length >= 12 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
            && head[8] == form[0] && head[9] == form[1] && head[10] == form[2] && head[11] == form[3];
    }
}
