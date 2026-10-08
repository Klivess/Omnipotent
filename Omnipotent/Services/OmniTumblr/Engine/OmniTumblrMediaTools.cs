using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Globalization;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    public sealed class MediaProbe
    {
        public int? Width { get; set; }
        public int? Height { get; set; }
        public double? DurationSeconds { get; set; }
        public string? VideoCodec { get; set; }
        public bool HasVideoStream { get; set; }
        public bool HasAudio { get; set; }
    }

    /// <summary>
    /// ffprobe/ffmpeg helpers: dimensions + duration (sent to Tumblr and used for filters), preview
    /// thumbnails for the website, and frames for vision captions. Uses the bundled FFmpeg directory,
    /// then PATH. Every process run is bounded by a timeout and killed (with its tree) when it overruns.
    /// </summary>
    internal sealed class OmniTumblrMediaTools
    {
        public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v" };

        private readonly string ffmpeg;
        private readonly string ffprobe;

        public OmniTumblrMediaTools(string? ffmpegDirectory)
        {
            string? bundledFfmpeg = ffmpegDirectory == null ? null : Path.Combine(ffmpegDirectory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            string? bundledProbe = ffmpegDirectory == null ? null : Path.Combine(ffmpegDirectory, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            ffmpeg = bundledFfmpeg != null && File.Exists(bundledFfmpeg) ? bundledFfmpeg : "ffmpeg";
            ffprobe = bundledProbe != null && File.Exists(bundledProbe) ? bundledProbe : "ffprobe";
        }

        public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));
        public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));
        public static bool IsSupported(string path) => IsImage(path) || IsVideo(path);

        public static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream",
        };

        public async Task<MediaProbe?> ProbeAsync(string path, CancellationToken ct)
        {
            if (!File.Exists(path)) return null;
            var (exit, stdout, _) = await RunTextAsync(ffprobe,
                new[] { "-v", "error", "-print_format", "json", "-show_streams", "-show_format", path },
                TimeSpan.FromSeconds(30), ct);
            if (exit != 0 || string.IsNullOrWhiteSpace(stdout)) return null;
            try
            {
                var root = JObject.Parse(stdout);
                var probe = new MediaProbe();
                if (root["streams"] is JArray streams)
                {
                    foreach (var s in streams)
                    {
                        string? type = (string?)s["codec_type"];
                        if (type == "video" && !probe.HasVideoStream)
                        {
                            probe.HasVideoStream = true;
                            probe.VideoCodec = (string?)s["codec_name"];
                            int? w = (int?)s["width"], h = (int?)s["height"];
                            int rotation = ReadRotation(s);
                            if (rotation % 180 != 0 && w.HasValue && h.HasValue) (w, h) = (h, w);
                            probe.Width = w;
                            probe.Height = h;
                        }
                        else if (type == "audio") probe.HasAudio = true;
                    }
                }
                string? duration = (string?)root["format"]?["duration"];
                if (double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                    probe.DurationSeconds = seconds;
                return probe;
            }
            catch
            {
                return null;
            }
        }

        private static int ReadRotation(JToken stream)
        {
            if (int.TryParse((string?)stream["tags"]?["rotate"], out var tagRotation)) return Math.Abs(tagRotation);
            if (stream["side_data_list"] is JArray sideData)
                foreach (var d in sideData)
                    if (d["rotation"] != null && int.TryParse(d["rotation"]!.ToString(), out var r)) return Math.Abs(r);
            return 0;
        }

        /// <summary>Writes a JPEG preview (≤480px wide). Videos use a frame ~1s (or 10%) in.</summary>
        public async Task<bool> CreateThumbnailAsync(string mediaPath, string outputPath, double? durationSeconds, CancellationToken ct)
        {
            if (!File.Exists(mediaPath)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            string temp = outputPath + ".tmp.jpg";
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            if (IsVideo(mediaPath))
            {
                double at = durationSeconds is > 2 ? Math.Min(1.0, durationSeconds.Value * 0.1) : 0;
                args.AddRange(new[] { "-ss", at.ToString("0.###", CultureInfo.InvariantCulture) });
            }
            args.AddRange(new[] { "-i", mediaPath, "-frames:v", "1", "-vf", "scale='min(480,iw)':-2", "-q:v", "5", temp });
            var (exit, _, _) = await RunTextAsync(ffmpeg, args, TimeSpan.FromSeconds(45), ct);
            if (exit != 0 || !File.Exists(temp)) { TryDelete(temp); return false; }
            File.Move(temp, outputPath, overwrite: true);
            return true;
        }

        /// <summary>Evenly spaced JPEG frames (for vision models), each at most <paramref name="maxWidth"/> wide.</summary>
        public async Task<List<byte[]>> ExtractFramesAsync(string videoPath, int count, int maxWidth, double? durationSeconds, CancellationToken ct)
        {
            var frames = new List<byte[]>();
            if (!File.Exists(videoPath) || count <= 0) return frames;
            double duration = durationSeconds ?? (await ProbeAsync(videoPath, ct))?.DurationSeconds ?? 0;
            var times = duration > 0
                ? Enumerable.Range(1, count).Select(i => duration * i / (count + 1)).ToList()
                : new List<double> { 0 };
            foreach (double t in times)
            {
                var args = new[]
                {
                    "-hide_banner", "-loglevel", "error",
                    "-ss", t.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", videoPath, "-frames:v", "1",
                    "-vf", $"scale='min({maxWidth},iw)':-2",
                    "-f", "image2pipe", "-vcodec", "mjpeg", "-q:v", "5", "pipe:1",
                };
                var bytes = await RunBinaryAsync(ffmpeg, args, TimeSpan.FromSeconds(30), ct);
                if (bytes is { Length: > 100 }) frames.Add(bytes);
            }
            return frames;
        }

        // ─────────────────────────────── Process plumbing ───────────────────────────────

        private static async Task<(int Exit, string Stdout, string Stderr)> RunTextAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
        {
            using var process = CreateProcess(exe, args);
            try
            {
                if (!process.Start()) return (-1, "", "could not start");
            }
            catch (Exception ex)
            {
                return (-1, "", ex.Message); // ffmpeg not installed
            }
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!await WaitAsync(process, timeout, ct)) return (-1, "", "timed out");
            return (process.ExitCode, await stdoutTask, await stderrTask);
        }

        private static async Task<byte[]?> RunBinaryAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
        {
            using var process = CreateProcess(exe, args);
            try
            {
                if (!process.Start()) return null;
            }
            catch
            {
                return null;
            }
            using var buffer = new MemoryStream();
            var copyTask = process.StandardOutput.BaseStream.CopyToAsync(buffer);
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!await WaitAsync(process, timeout, ct)) return null;
            try { await copyTask; await stderrTask; } catch { return null; }
            return process.ExitCode == 0 ? buffer.ToArray() : null;
        }

        private static Process CreateProcess(string exe, IEnumerable<string> args)
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in args) info.ArgumentList.Add(a);
            return new Process { StartInfo = info };
        }

        private static async Task<bool> WaitAsync(Process process, TimeSpan timeout, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
