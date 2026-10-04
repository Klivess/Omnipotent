using System.Net;

namespace Omnipotent.Services.MemeScraper.Scraping
{
    public sealed record ReelDownloadOutcome(bool Success, string? Url, long Bytes, string Error);

    /// <summary>
    /// Downloads reel videos with validation. Tries each candidate URL in order (raw Instagram CDN,
    /// alternate renditions, inflact mirrors) and only accepts a file that is actually a video: the old
    /// WebClient path would happily save a 302 page, an HTML error or a zero-byte body as ".mp4".
    /// Writes to a temp file and moves it into place, so a crash never leaves a truncated video
    /// under the final name.
    /// </summary>
    public sealed class ReelDownloader : IDisposable
    {
        public const long MinimumVideoBytes = 8 * 1024;
        public const long MaximumVideoBytes = 1024L * 1024 * 1024;

        private readonly HttpClient http;

        public ReelDownloader(HttpMessageHandler? handler = null)
        {
            handler ??= new SocketsHttpHandler
            {
                AllowAutoRedirect = true, // Instagram CDN URLs 302 to video.xx.fbcdn.net
                MaxAutomaticRedirections = 10,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(20),
            };
            http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "video/mp4,video/*;q=0.9,*/*;q=0.5");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        }

        /// <summary>Downloads the first candidate that yields a real video to <paramref name="destinationPath"/>.</summary>
        public async Task<ReelDownloadOutcome> DownloadAsync(IEnumerable<string> candidateUrls, string destinationPath, CancellationToken ct)
        {
            var errors = new List<string>();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
            string temp = destinationPath + ".part";
            foreach (var url in candidateUrls.Where(ScrapeJson.IsHttpUrl).Distinct())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (url.Contains("inflact.com", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.Referrer = new Uri("https://inflact.com/");
                    }
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        errors.Add($"{Host(url)}: HTTP {(int)response.StatusCode}");
                        continue;
                    }
                    var declared = response.Content.Headers.ContentLength;
                    if (declared is > MaximumVideoBytes)
                    {
                        errors.Add($"{Host(url)}: too large ({declared} bytes)");
                        continue;
                    }

                    long written;
                    await using (var source = await response.Content.ReadAsStreamAsync(ct))
                    await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        written = await CopyBoundedAsync(source, target, MaximumVideoBytes, ct);
                    }

                    var problem = ValidateVideoFile(temp, written, declared);
                    if (problem != null)
                    {
                        errors.Add($"{Host(url)}: {problem}");
                        TryDelete(temp);
                        continue;
                    }
                    File.Move(temp, destinationPath, overwrite: true);
                    return new ReelDownloadOutcome(true, url, written, "");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDelete(temp);
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{Host(url)}: {ex.GetType().Name}: {ex.Message}");
                    TryDelete(temp);
                }
            }
            return new ReelDownloadOutcome(false, null, 0, errors.Count == 0 ? "no usable URL" : string.Join("; ", errors));
        }

        /// <summary>Null when the file is a plausible complete video; otherwise why not.</summary>
        public static string? ValidateVideoFile(string path, long written, long? declaredLength)
        {
            if (written < MinimumVideoBytes) return $"body too small ({written} bytes)";
            if (declaredLength.HasValue && declaredLength.Value > 0 && declaredLength.Value != written)
                return $"truncated ({written} of {declaredLength} bytes)";
            var head = new byte[12];
            using (var fs = File.OpenRead(path))
            {
                if (fs.Read(head, 0, head.Length) < head.Length) return "unreadable header";
            }
            return LooksLikeVideo(head) ? null : "not a video (header " + BitConverter.ToString(head, 0, 8) + ")";
        }

        /// <summary>ISO-BMFF (mp4/mov: "ftyp" at offset 4) or Matroska/WebM (EBML magic).</summary>
        public static bool LooksLikeVideo(ReadOnlySpan<byte> head)
        {
            if (head.Length >= 8 && head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p') return true;
            if (head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3) return true;
            return false;
        }

        private static async Task<long> CopyBoundedAsync(Stream source, Stream target, long limit, CancellationToken ct)
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                total += read;
                if (total > limit) throw new InvalidDataException($"exceeded {limit} bytes");
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            return total;
        }

        private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "?";

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public void Dispose() => http.Dispose();
    }
}
