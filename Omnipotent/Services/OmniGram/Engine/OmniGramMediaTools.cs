using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniGram.Models;
using System.Diagnostics;
using System.Globalization;

namespace Omnipotent.Services.OmniGram.Engine
{
    public sealed class MediaProbe
    {
        public int? Width { get; set; }
        public int? Height { get; set; }
        public double? DurationSeconds { get; set; }
        public string? FormatName { get; set; }
        public string? VideoCodec { get; set; }
        public string? PixelFormat { get; set; }
        public double? FrameRate { get; set; }
        public bool HasVideoStream { get; set; }
        public bool HasAudio { get; set; }
        public string? AudioCodec { get; set; }
        public int? AudioSampleRate { get; set; }
        public int? AudioChannels { get; set; }
        public long? BitRate { get; set; }
    }

    public sealed class PrepareResult
    {
        public bool Ok { get; set; }
        public string? OutputPath { get; set; }
        public string MimeType { get; set; } = "application/octet-stream";
        public long Bytes { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public double? DurationSeconds { get; set; }
        public string Notes { get; set; } = "";
        public string? Error { get; set; }
        /// <summary>The file can never be made acceptable (too short, unreadable…): don't retry it.</summary>
        public bool Permanent { get; set; }
    }

    /// <summary>
    /// ffprobe/ffmpeg helpers: dimensions and duration, preview thumbnails for the website, frames for vision
    /// captions, and <see cref="PrepareAsync"/>, which makes the Instagram-ready copy that is actually served
    /// to Instagram (MP4 with the moov atom first and no edit lists, H.264/HEVC + AAC, 23–60 fps; JPEG images
    /// within 4:5–1.91:1). Uses the bundled FFmpeg directory, then PATH. Every process run is bounded by a
    /// timeout and killed (with its tree) when it overruns.
    /// </summary>
    internal sealed class OmniGramMediaTools
    {
        public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v" };

        public const long MaxImageBytes = 8L * 1024 * 1024;
        public const long MaxReelBytes = 300L * 1024 * 1024;
        public const long MaxStoryVideoBytes = 100L * 1024 * 1024;
        public const double MinVideoSeconds = 3;
        public const double MaxReelSeconds = 15 * 60;
        public const double MaxStorySeconds = 60;
        public const double MinFeedAspect = 4.0 / 5.0;
        public const double MaxFeedAspect = 1.91;

        private readonly string ffmpeg;
        private readonly string ffprobe;
        private bool? available;

        public OmniGramMediaTools(string? ffmpegDirectory)
        {
            string? bundledFfmpeg = ffmpegDirectory == null ? null : Path.Combine(ffmpegDirectory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            string? bundledProbe = ffmpegDirectory == null ? null : Path.Combine(ffmpegDirectory, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            ffmpeg = bundledFfmpeg != null && File.Exists(bundledFfmpeg) ? bundledFfmpeg : "ffmpeg";
            ffprobe = bundledProbe != null && File.Exists(bundledProbe) ? bundledProbe : "ffprobe";
        }

        /// <summary>Explicit executables (tests point these at nothing to simulate a server without FFmpeg).</summary>
        internal OmniGramMediaTools(string ffmpegExe, string ffprobeExe)
        {
            ffmpeg = ffmpegExe;
            ffprobe = ffprobeExe;
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

        /// <summary>What the last <see cref="IsAvailableAsync"/> found, for the settings page.</summary>
        public string Status { get; private set; } = "not checked yet";

        /// <summary>The last check's answer; null before the first check.</summary>
        public bool? Available => available;

        /// <summary>
        /// Whether both ffmpeg and ffprobe actually run. A truncated download exists on disk but cannot start, so
        /// existence is not enough. A missing or broken install is re-checked every 10 minutes: another service
        /// (OmniTube, KliveCloud) may install FFmpeg into the shared folder after OmniGram started.
        /// </summary>
        public async Task<bool> IsAvailableAsync(CancellationToken ct)
        {
            if (available == true || (available == false && DateTime.UtcNow - checkedUtc < TimeSpan.FromMinutes(10))) return available.Value;
            var version = new[] { "-version" };
            var (probeExit, _, probeError) = await RunTextAsync(ffprobe, version, TimeSpan.FromSeconds(15), ct);
            var (ffmpegExit, ffmpegOut, ffmpegError) = await RunTextAsync(ffmpeg, version, TimeSpan.FromSeconds(15), ct);
            available = probeExit == 0 && ffmpegExit == 0;
            checkedUtc = DateTime.UtcNow;
            Status = available.Value
                ? (ffmpegOut.Split('\n').FirstOrDefault()?.Split(" Copyright")[0].Trim() is { Length: > 0 } line ? line : "ready") + $" ({ffmpeg})"
                : ffmpegExit != 0
                    ? $"ffmpeg cannot run ({ffmpeg}): {Short(ffmpegError, ffmpegExit)}"
                    : $"ffprobe cannot run ({ffprobe}): {Short(probeError, probeExit)}";
            return available.Value;

            static string Short(string error, int exit) => string.IsNullOrWhiteSpace(error) ? $"exit code {exit}" : error.Trim().Split('\n')[0];
        }

        private DateTime checkedUtc;

        public async Task<MediaProbe?> ProbeAsync(string path, CancellationToken ct)
        {
            if (!File.Exists(path)) return null;
            var (exit, stdout, _) = await RunTextAsync(ffprobe,
                new[] { "-v", "error", "-print_format", "json", "-show_streams", "-show_format", path },
                TimeSpan.FromSeconds(30), ct);
            if (exit != 0 || string.IsNullOrWhiteSpace(stdout)) return null;
            return ParseProbe(stdout);
        }

        internal static MediaProbe? ParseProbe(string json)
        {
            try
            {
                var root = JObject.Parse(json);
                var probe = new MediaProbe { FormatName = (string?)root["format"]?["format_name"] };
                if (root["streams"] is JArray streams)
                {
                    foreach (var s in streams)
                    {
                        string? type = (string?)s["codec_type"];
                        if (type == "video" && !probe.HasVideoStream && (string?)s["disposition"]?["attached_pic"] != "1")
                        {
                            probe.HasVideoStream = true;
                            probe.VideoCodec = (string?)s["codec_name"];
                            probe.PixelFormat = (string?)s["pix_fmt"];
                            int? w = (int?)s["width"], h = (int?)s["height"];
                            int rotation = ReadRotation(s);
                            if (rotation % 180 != 0 && w.HasValue && h.HasValue) (w, h) = (h, w);
                            probe.Width = w;
                            probe.Height = h;
                            probe.FrameRate = ParseRate((string?)s["avg_frame_rate"]) ?? ParseRate((string?)s["r_frame_rate"]);
                        }
                        else if (type == "audio" && !probe.HasAudio)
                        {
                            probe.HasAudio = true;
                            probe.AudioCodec = (string?)s["codec_name"];
                            if (int.TryParse((string?)s["sample_rate"], out var sr)) probe.AudioSampleRate = sr;
                            probe.AudioChannels = (int?)s["channels"];
                        }
                    }
                }
                string? duration = (string?)root["format"]?["duration"];
                if (double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                    probe.DurationSeconds = seconds;
                if (long.TryParse((string?)root["format"]?["bit_rate"], out var br)) probe.BitRate = br;
                return probe;
            }
            catch
            {
                return null;
            }
        }

        private static double? ParseRate(string? rate)
        {
            if (string.IsNullOrEmpty(rate)) return null;
            string[] parts = rate.Split('/');
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 && n > 0)
                return n / d;
            return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
        }

        private static int ReadRotation(JToken stream)
        {
            if (int.TryParse((string?)stream["tags"]?["rotate"], out var tagRotation)) return Math.Abs(tagRotation);
            if (stream["side_data_list"] is JArray sideData)
                foreach (var d in sideData)
                    if (d["rotation"] != null && int.TryParse(d["rotation"]!.ToString(), out var r)) return Math.Abs(r);
            return 0;
        }

        // ─────────────────────────────── Instagram-ready copies ───────────────────────────────

        /// <summary>
        /// Writes the copy of <paramref name="sourcePath"/> Instagram will download, to
        /// <paramref name="outputStem"/> + ".mp4"/".jpg". Compliant videos are only remuxed (faststart, no
        /// edit lists); anything outside Instagram's specs is transcoded; images become sRGB JPEGs within the
        /// format's size and aspect limits. Without FFmpeg, files that are already JPEG/MP4 and small enough
        /// are copied unchecked. <paramref name="forceTranscode"/> re-encodes even a compliant file (after
        /// Instagram refused a repackaged copy).
        /// </summary>
        public async Task<PrepareResult> PrepareAsync(string sourcePath, PostFormat format, bool carouselItem, ImageFit fit, string outputStem, bool forceTranscode, CancellationToken ct)
        {
            if (!File.Exists(sourcePath)) return new PrepareResult { Error = "The media file is missing: " + sourcePath, Permanent = true };
            Directory.CreateDirectory(Path.GetDirectoryName(outputStem)!);
            return IsVideo(sourcePath)
                ? await PrepareVideoAsync(sourcePath, format, outputStem + ".mp4", forceTranscode, ct)
                : await PrepareImageAsync(sourcePath, format, carouselItem, fit, outputStem + ".jpg", forceTranscode, ct);
        }

        private async Task<PrepareResult> PrepareVideoAsync(string source, PostFormat format, string output, bool forceTranscode, CancellationToken ct)
        {
            bool story = format == PostFormat.Story;
            long maxBytes = story ? MaxStoryVideoBytes : MaxReelBytes;
            var probe = await ProbeAsync(source, ct);
            if (probe == null)
            {
                if (!await IsAvailableAsync(ct))
                {
                    long size = new FileInfo(source).Length;
                    if (Path.GetExtension(source).Equals(".mp4", StringComparison.OrdinalIgnoreCase) && size <= maxBytes)
                        return CopyAs(source, output, "video/mp4", "copied unchecked (FFmpeg is not installed, so it was not verified or remuxed)");
                    return new PrepareResult { Error = "FFmpeg is needed to convert this video for Instagram, and it is not installed on the server." };
                }
                return new PrepareResult { Error = "The video could not be read (corrupt or not a video).", Permanent = true };
            }
            if (!probe.HasVideoStream) return new PrepareResult { Error = "The file has no video stream.", Permanent = true };
            double duration = probe.DurationSeconds ?? 0;
            if (duration > 0 && duration < MinVideoSeconds)
                return new PrepareResult { Error = $"Instagram needs at least {MinVideoSeconds:0} seconds of video; this one is {duration:0.#} s.", Permanent = true };
            if (!story && duration > MaxReelSeconds)
                return new PrepareResult { Error = $"Reels can be at most 15 minutes long; this video is {duration / 60:0.#} minutes.", Permanent = true };

            var reasons = TranscodeReasons(probe);
            if (forceTranscode && reasons.Count == 0) reasons.Add("Instagram refused the repackaged copy");
            bool trim = story && duration > MaxStorySeconds;
            string notes;
            int exit;
            string stderr;
            string temp = output + ".tmp.mp4";
            if (reasons.Count == 0)
            {
                var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", source, "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy" };
                if (trim) args.AddRange(new[] { "-t", MaxStorySeconds.ToString(CultureInfo.InvariantCulture) });
                args.AddRange(new[] { "-avoid_negative_ts", "make_zero", "-use_editlist", "0", "-movflags", "+faststart", "-f", "mp4", temp });
                (exit, _, stderr) = await RunTextAsync(ffmpeg, args, TimeSpan.FromMinutes(5), ct);
                notes = trim ? "remuxed (faststart), trimmed to 60 s for a story" : "remuxed (faststart, no edit lists)";
                if (exit != 0)
                {
                    // A container quirk copy can't fix: fall back to a full transcode.
                    reasons.Add("remux failed");
                }
            }
            else
            {
                exit = -1;
                stderr = "";
                notes = "";
            }

            if (reasons.Count > 0)
            {
                var args = BuildTranscodeArgs(source, temp, probe, trim);
                (exit, _, stderr) = await RunTextAsync(ffmpeg, args, TimeSpan.FromMinutes(12), ct);
                notes = $"transcoded to H.264/AAC ({string.Join(", ", reasons)}){(trim ? ", trimmed to 60 s for a story" : "")}";
            }

            if (exit != 0 || !File.Exists(temp))
            {
                TryDelete(temp);
                return new PrepareResult { Error = "FFmpeg could not convert the video: " + Clip(LastLine(stderr), 200) };
            }
            File.Move(temp, output, overwrite: true);
            long bytes = new FileInfo(output).Length;
            if (bytes > maxBytes)
            {
                TryDelete(output);
                return new PrepareResult { Error = $"The converted video is {bytes / (1024 * 1024)} MB; Instagram allows {maxBytes / (1024 * 1024)} MB.", Permanent = true };
            }
            var outProbe = await ProbeAsync(output, ct);
            return new PrepareResult
            {
                Ok = true,
                OutputPath = output,
                MimeType = "video/mp4",
                Bytes = bytes,
                Width = outProbe?.Width ?? probe.Width,
                Height = outProbe?.Height ?? probe.Height,
                DurationSeconds = outProbe?.DurationSeconds ?? probe.DurationSeconds,
                Notes = notes,
            };
        }

        /// <summary>Why a video can't be sent as it is (empty = a remux is enough).</summary>
        internal static List<string> TranscodeReasons(MediaProbe p)
        {
            var reasons = new List<string>();
            if (p.VideoCodec is not ("h264" or "hevc")) reasons.Add($"codec {p.VideoCodec ?? "unknown"}");
            if (p.PixelFormat != null && p.PixelFormat is not ("yuv420p" or "yuvj420p")) reasons.Add($"pixel format {p.PixelFormat}");
            if (p.FrameRate is double fps && (fps < 23 || fps > 60.5)) reasons.Add($"{fps:0.#} fps");
            if (p.Width > 1920) reasons.Add($"{p.Width}px wide");
            if (p.HasAudio && p.AudioCodec != "aac") reasons.Add($"audio {p.AudioCodec ?? "unknown"}");
            if (p.HasAudio && p.AudioSampleRate > 48000) reasons.Add($"{p.AudioSampleRate} Hz audio");
            if (p.HasAudio && p.AudioChannels > 2) reasons.Add($"{p.AudioChannels}-channel audio");
            if (!p.HasAudio) reasons.Add("no audio track");
            if (p.BitRate > 25_000_000) reasons.Add($"{p.BitRate / 1_000_000} Mbps");
            if (p.FormatName != null && !p.FormatName.Contains("mp4") && !p.FormatName.Contains("mov")) reasons.Add($"container {p.FormatName}");
            return reasons;
        }

        private static List<string> BuildTranscodeArgs(string source, string output, MediaProbe probe, bool trim)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", source };
            bool silent = !probe.HasAudio;
            if (silent) args.AddRange(new[] { "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=44100" });
            args.AddRange(new[] { "-map", "0:v:0", "-map", silent ? "1:a:0" : "0:a:0" });
            var filters = new List<string> { "scale='if(gt(iw,ih),min(1920,iw),min(1080,iw))':-2:flags=lanczos" };
            if (probe.FrameRate is double fps && (fps < 23 || fps > 60.5)) filters.Add("fps=30");
            filters.Add("format=yuv420p");
            args.AddRange(new[]
            {
                "-vf", string.Join(",", filters),
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-profile:v", "high", "-level", "4.1",
                "-g", "60", "-keyint_min", "30", "-sc_threshold", "0", "-flags", "+cgop",
                "-maxrate", "12M", "-bufsize", "24M",
                "-c:a", "aac", "-b:a", "128k", "-ar", "44100", "-ac", "2",
            });
            if (silent) args.Add("-shortest");
            if (trim) args.AddRange(new[] { "-t", MaxStorySeconds.ToString(CultureInfo.InvariantCulture) });
            args.AddRange(new[] { "-use_editlist", "0", "-movflags", "+faststart", "-f", "mp4", output });
            return args;
        }

        private async Task<PrepareResult> PrepareImageAsync(string source, PostFormat format, bool carouselItem, ImageFit fit, string output, bool forceTranscode, CancellationToken ct)
        {
            bool story = format == PostFormat.Story;
            var probe = await ProbeAsync(source, ct);
            bool isJpeg = Path.GetExtension(source).ToLowerInvariant() is ".jpg" or ".jpeg";
            long size = new FileInfo(source).Length;
            if (probe == null || probe.Width is not int w || probe.Height is not int h || w <= 0 || h <= 0)
            {
                if (!await IsAvailableAsync(ct))
                {
                    if (isJpeg && size <= MaxImageBytes)
                        return CopyAs(source, output, "image/jpeg", "copied unchecked (FFmpeg is not installed, so size and aspect ratio were not verified)");
                    return new PrepareResult { Error = "FFmpeg is needed to convert this image to a JPEG for Instagram, and it is not installed on the server." };
                }
                return new PrepareResult { Error = "The image could not be read.", Permanent = true };
            }

            var (targetW, targetH, notes) = ImageTarget(w, h, story);
            bool needsCanvas = targetH != null;
            bool resize = w > 1440 || w < 320 || needsCanvas;
            if (!forceTranscode && isJpeg && !resize && size <= MaxImageBytes && probe.PixelFormat is null or "yuvj420p" or "yuvj444p" or "yuvj422p" or "gray")
                return CopyAs(source, output, "image/jpeg", "used as is (already a compliant JPEG)", w, h);

            string filter;
            if (needsCanvas)
            {
                int W = targetW, H = targetH!.Value;
                filter = fit switch
                {
                    ImageFit.Crop => $"scale={W}:{H}:force_original_aspect_ratio=increase,crop={W}:{H},setsar=1",
                    ImageFit.Pad => $"scale={W}:{H}:force_original_aspect_ratio=decrease,pad={W}:{H}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1",
                    _ => $"split=2[bg][fg];[bg]scale={W}:{H}:force_original_aspect_ratio=increase,crop={W}:{H},boxblur=24:2,eq=brightness=-0.06[bgb];" +
                         $"[fg]scale={W}:{H}:force_original_aspect_ratio=decrease[fgs];[bgb][fgs]overlay=(W-w)/2:(H-h)/2,setsar=1",
                };
                notes += fit switch { ImageFit.Crop => " (cropped)", ImageFit.Pad => " (padded)", _ => " (blurred background)" };
            }
            else
            {
                int W = Math.Clamp(w, 320, 1440);
                filter = $"scale={W}:-2:flags=lanczos,setsar=1";
                notes = w > 1440 ? "scaled down to 1440 px wide" : w < 320 ? "scaled up to 320 px wide" : "converted to JPEG";
            }

            string temp = output + ".tmp.jpg";
            foreach (int quality in new[] { 2, 4, 7 })
            {
                var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", source, "-frames:v", "1" };
                args.AddRange(needsCanvas && fit == ImageFit.BlurPad ? new[] { "-filter_complex", filter } : new[] { "-vf", filter });
                args.AddRange(new[] { "-pix_fmt", "yuvj420p", "-q:v", quality.ToString(CultureInfo.InvariantCulture), "-f", "image2", temp });
                var (exit, _, stderr) = await RunTextAsync(ffmpeg, args, TimeSpan.FromSeconds(90), ct);
                if (exit != 0 || !File.Exists(temp))
                {
                    TryDelete(temp);
                    return new PrepareResult { Error = "FFmpeg could not convert the image: " + Clip(LastLine(stderr), 200) };
                }
                if (new FileInfo(temp).Length <= MaxImageBytes) break;
            }
            long bytes = new FileInfo(temp).Length;
            if (bytes > MaxImageBytes)
            {
                TryDelete(temp);
                return new PrepareResult { Error = "The image is still over 8 MB after compression.", Permanent = true };
            }
            File.Move(temp, output, overwrite: true);
            var outProbe = await ProbeAsync(output, ct);
            return new PrepareResult
            {
                Ok = true,
                OutputPath = output,
                MimeType = "image/jpeg",
                Bytes = bytes,
                Width = outProbe?.Width,
                Height = outProbe?.Height,
                Notes = notes,
            };
        }

        /// <summary>
        /// The canvas an image needs: feed images must sit within 4:5–1.91:1 (outside it they get a canvas of
        /// that limit); stories get a 1080×1920 canvas unless they are already close to 9:16. Height null = no
        /// canvas, just resize to 320–1440 px wide.
        /// </summary>
        internal static (int Width, int? Height, string Notes) ImageTarget(int w, int h, bool story)
        {
            double aspect = (double)w / h;
            if (story)
            {
                double target = 9.0 / 16.0;
                return Math.Abs(aspect - target) / target > 0.06 ? (1080, 1920, "fitted to a 9:16 story canvas") : (Math.Clamp(w, 320, 1440), null, "");
            }
            if (aspect < MinFeedAspect) return (1080, 1350, $"fitted to 4:5 (was {aspect:0.##}:1, taller than Instagram allows)");
            if (aspect > MaxFeedAspect) return (1080, 566, $"fitted to 1.91:1 (was {aspect:0.##}:1, wider than Instagram allows)");
            return (Math.Clamp(w, 320, 1440), null, "");
        }

        private static PrepareResult CopyAs(string source, string output, string mime, string notes, int? width = null, int? height = null)
        {
            File.Copy(source, output, overwrite: true);
            return new PrepareResult { Ok = true, OutputPath = output, MimeType = mime, Bytes = new FileInfo(output).Length, Notes = notes, Width = width, Height = height };
        }

        // ─────────────────────────────── Previews + frames ───────────────────────────────

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

        private static string LastLine(string? text) =>
            (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "unknown error";

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
