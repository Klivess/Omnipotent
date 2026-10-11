using Omnipotent.Services.OmniGram.Engine;
using Omnipotent.Services.OmniGram.Models;
using System.Diagnostics;
using Xunit;

namespace Omnipotent.Tests.OmniGram
{
    /// <summary>Finds an FFmpeg build: OMNIGRAM_FFMPEG_DIR, the app's own download (bin/*/net9.0/FFmpeg), then nothing.</summary>
    internal static class FfmpegLocator
    {
        private static readonly Lazy<string?> Dir = new(() =>
        {
            string? env = Environment.GetEnvironmentVariable("OMNIGRAM_FFMPEG_DIR");
            if (env != null && Works(env)) return env;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                foreach (string config in new[] { "Debug", "Release" })
                {
                    string candidate = Path.Combine(dir.FullName, "Omnipotent", "bin", config, "net9.0", "FFmpeg");
                    if (Works(candidate)) return candidate;
                }
            }
            return null;
        });

        /// <summary>Both tools exist and actually start (a truncated download exists but cannot run).</summary>
        private static bool Works(string dir)
        {
            foreach (string tool in new[] { "ffmpeg", "ffprobe" })
            {
                string exe = Path.Combine(dir, Exe(tool));
                if (!File.Exists(exe)) return false;
                try
                {
                    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
                    if (p == null) return false;
                    p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(15000) || p.ExitCode != 0) return false;
                }
                catch
                {
                    return false;
                }
            }
            return true;
        }

        public static string? Directory => Dir.Value;
        public static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;
    }

    /// <summary>A fact that is skipped (not silently passed) when no FFmpeg build is available.</summary>
    public sealed class FfmpegFactAttribute : FactAttribute
    {
        public FfmpegFactAttribute()
        {
            if (FfmpegLocator.Directory == null) Skip = "FFmpeg not found (set OMNIGRAM_FFMPEG_DIR or run the app once so it downloads FFmpeg).";
        }
    }

    public class OmniGramMediaTests
    {
        // ─────────────────────────────── Pure decisions ───────────────────────────────

        [Fact]
        public void Compliant_videos_only_need_a_remux_and_everything_else_is_named()
        {
            var good = new MediaProbe { HasVideoStream = true, VideoCodec = "h264", PixelFormat = "yuv420p", FrameRate = 30, Width = 1080, HasAudio = true, AudioCodec = "aac", AudioSampleRate = 44100, AudioChannels = 2, BitRate = 4_000_000, FormatName = "mov,mp4,m4a,3gp,3g2,mj2" };
            Assert.Empty(OmniGramMediaTools.TranscodeReasons(good));

            var bad = new MediaProbe { HasVideoStream = true, VideoCodec = "vp9", PixelFormat = "yuv444p", FrameRate = 15, Width = 3840, HasAudio = true, AudioCodec = "opus", AudioSampleRate = 96000, AudioChannels = 6, BitRate = 60_000_000, FormatName = "matroska,webm" };
            var reasons = OmniGramMediaTools.TranscodeReasons(bad);
            Assert.Contains("codec vp9", reasons);
            Assert.Contains("pixel format yuv444p", reasons);
            Assert.Contains("15 fps", reasons);
            Assert.Contains("3840px wide", reasons);
            Assert.Contains("audio opus", reasons);
            Assert.Contains("6-channel audio", reasons);
            Assert.Contains("container matroska,webm", reasons);

            Assert.Contains("no audio track", OmniGramMediaTools.TranscodeReasons(new MediaProbe { HasVideoStream = true, VideoCodec = "h264", FrameRate = 30, Width = 720 }));
        }

        [Theory]
        [InlineData(1080, 2400, false, 1080, 1350)] // too tall for the feed → 4:5
        [InlineData(2400, 1000, false, 1080, 566)]  // too wide → 1.91:1
        [InlineData(1080, 1080, false, 1080, null)] // square is fine
        [InlineData(2000, 1600, false, 1440, null)] // only scaled down
        [InlineData(1080, 1080, true, 1080, 1920)]  // stories get a 9:16 canvas
        [InlineData(1080, 1920, true, 1080, null)]
        public void Images_get_the_canvas_instagram_accepts(int w, int h, bool story, int expectedW, int? expectedH)
        {
            var (width, height, _) = OmniGramMediaTools.ImageTarget(w, h, story);
            Assert.Equal(expectedW, width);
            Assert.Equal(expectedH, height);
        }

        [Fact]
        public void Probe_output_is_parsed_including_rotation_and_attached_covers()
        {
            const string json = @"{""streams"":[
                {""codec_type"":""video"",""codec_name"":""mjpeg"",""width"":300,""height"":300,""disposition"":{""attached_pic"":1}},
                {""codec_type"":""video"",""codec_name"":""h264"",""pix_fmt"":""yuv420p"",""width"":1920,""height"":1080,""avg_frame_rate"":""30000/1001"",""side_data_list"":[{""rotation"":-90}]},
                {""codec_type"":""audio"",""codec_name"":""aac"",""sample_rate"":""48000"",""channels"":2}],
                ""format"":{""format_name"":""mov,mp4,m4a,3gp,3g2,mj2"",""duration"":""12.5"",""bit_rate"":""3500000""}}";
            var probe = OmniGramMediaTools.ParseProbe(json)!;
            Assert.Equal("h264", probe.VideoCodec);
            Assert.Equal(1080, probe.Width);
            Assert.Equal(1920, probe.Height);
            Assert.Equal(29.97, probe.FrameRate!.Value, 2);
            Assert.Equal(12.5, probe.DurationSeconds);
            Assert.Equal(48000, probe.AudioSampleRate);
            Assert.Equal(3_500_000, probe.BitRate);
        }

        [Fact]
        public async Task Without_ffmpeg_mp4_and_jpeg_are_copied_unchecked_and_other_images_wait()
        {
            using var dir = new TempDir();
            var tools = new OmniGramMediaTools(Path.Combine(dir.Path, "missing", "ffmpeg.exe"), Path.Combine(dir.Path, "missing", "ffprobe.exe"));
            var video = await tools.PrepareAsync(dir.File("a.mp4"), PostFormat.Reel, false, ImageFit.BlurPad, Path.Combine(dir.Path, "out", "a"), false, CancellationToken.None);
            Assert.True(video.Ok);
            Assert.EndsWith(".mp4", video.OutputPath);
            Assert.StartsWith("copied unchecked", video.Notes);

            var jpeg = await tools.PrepareAsync(dir.File("b.jpg"), PostFormat.Photo, false, ImageFit.BlurPad, Path.Combine(dir.Path, "out", "b"), false, CancellationToken.None);
            Assert.True(jpeg.Ok);

            var png = await tools.PrepareAsync(dir.File("c.png"), PostFormat.Photo, false, ImageFit.BlurPad, Path.Combine(dir.Path, "out", "c"), false, CancellationToken.None);
            Assert.False(png.Ok);
            Assert.False(png.Permanent);
            Assert.Contains("FFmpeg is needed", png.Error);

            var missing = await tools.PrepareAsync(Path.Combine(dir.Path, "nope.mp4"), PostFormat.Reel, false, ImageFit.BlurPad, Path.Combine(dir.Path, "out", "d"), false, CancellationToken.None);
            Assert.True(missing.Permanent);

            Assert.False(tools.Available);
            Assert.Contains("cannot run", tools.Status);
        }

        [FfmpegFact]
        public async Task A_truncated_ffmpeg_next_to_a_working_ffprobe_counts_as_unavailable()
        {
            // The dev build once had a half-downloaded ffmpeg.exe: present on disk, impossible to start.
            using var dir = new TempDir();
            string broken = dir.File(FfmpegLocator.Exe("ffmpeg"), 4096);
            var tools = new OmniGramMediaTools(broken, Path.Combine(FfmpegLocator.Directory!, FfmpegLocator.Exe("ffprobe")));

            Assert.False(await tools.IsAvailableAsync(CancellationToken.None));
            Assert.StartsWith("ffmpeg cannot run", tools.Status);

            var real = RealTools();
            Assert.True(await real.IsAvailableAsync(CancellationToken.None));
            Assert.StartsWith("ffmpeg version", real.Status);
        }

        // ─────────────────────────────── With a real FFmpeg ───────────────────────────────

        private static OmniGramMediaTools RealTools() => new(FfmpegLocator.Directory);

        private static void Make(params string[] args)
        {
            var info = new ProcessStartInfo(Path.Combine(FfmpegLocator.Directory!, FfmpegLocator.Exe("ffmpeg"))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string a in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args)) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            Assert.True(p.ExitCode == 0, "ffmpeg failed to make test media: " + err);
        }

        /// <summary>Top-level MP4 box types in file order (to check that moov comes before mdat).</summary>
        private static List<string> TopLevelBoxes(string path)
        {
            var boxes = new List<string>();
            using var fs = File.OpenRead(path);
            var header = new byte[16];
            long position = 0;
            while (position + 8 <= fs.Length && boxes.Count < 50)
            {
                fs.Position = position;
                fs.ReadExactly(header, 0, 8);
                long size = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
                string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
                if (size == 1)
                {
                    fs.ReadExactly(header, 8, 8);
                    size = (long)(((ulong)header[8] << 56) | ((ulong)header[9] << 48) | ((ulong)header[10] << 40) | ((ulong)header[11] << 32) | ((ulong)header[12] << 24) | ((ulong)header[13] << 16) | ((ulong)header[14] << 8) | header[15]);
                }
                else if (size == 0) size = fs.Length - position;
                boxes.Add(type);
                if (size < 8) break;
                position += size;
            }
            return boxes;
        }

        [FfmpegFact]
        public async Task A_non_h264_silent_video_is_transcoded_with_audio_and_faststart()
        {
            using var dir = new TempDir();
            string source = Path.Combine(dir.Path, "mpeg4.mp4");
            Make("-f", "lavfi", "-i", "testsrc=size=720x1280:rate=30", "-t", "4", "-c:v", "mpeg4", "-q:v", "5", source);
            var tools = RealTools();

            var result = await tools.PrepareAsync(source, PostFormat.Reel, false, ImageFit.BlurPad, Path.Combine(dir.Path, "out", "p-0"), false, CancellationToken.None);

            Assert.True(result.Ok, result.Error);
            Assert.StartsWith("transcoded to H.264/AAC", result.Notes);
            Assert.Contains("codec mpeg4", result.Notes);
            Assert.Contains("no audio track", result.Notes);
            var probe = await tools.ProbeAsync(result.OutputPath!, CancellationToken.None);
            Assert.Equal("h264", probe!.VideoCodec);
            Assert.Equal("yuv420p", probe.PixelFormat);
            Assert.True(probe.HasAudio);
            Assert.Equal("aac", probe.AudioCodec);
            Assert.Empty(OmniGramMediaTools.TranscodeReasons(probe));
            var boxes = TopLevelBoxes(result.OutputPath!);
            Assert.True(boxes.IndexOf("moov") < boxes.IndexOf("mdat"), "moov must come first: " + string.Join(",", boxes));
        }

        [FfmpegFact]
        public async Task A_compliant_video_is_only_remuxed_and_a_story_is_trimmed_to_a_minute()
        {
            using var dir = new TempDir();
            string source = Path.Combine(dir.Path, "good.mp4");
            Make("-f", "lavfi", "-i", "testsrc=size=720x1280:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
                "-t", "65", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source);
            var tools = RealTools();

            var reel = await tools.PrepareAsync(source, PostFormat.Reel, false, ImageFit.BlurPad, Path.Combine(dir.Path, "out", "r-0"), false, CancellationToken.None);
            Assert.True(reel.Ok, reel.Error);
            Assert.StartsWith("remuxed", reel.Notes);
            var boxes = TopLevelBoxes(reel.OutputPath!);
            Assert.True(boxes.IndexOf("moov") < boxes.IndexOf("mdat"));

            var story = await tools.PrepareAsync(source, PostFormat.Story, false, ImageFit.BlurPad, Path.Combine(dir.Path, "out", "s-0"), false, CancellationToken.None);
            Assert.True(story.Ok, story.Error);
            Assert.InRange(story.DurationSeconds!.Value, 55, 61);

            var forced = await tools.PrepareAsync(source, PostFormat.Reel, false, ImageFit.BlurPad, Path.Combine(dir.Path, "out", "f-0"), true, CancellationToken.None);
            Assert.True(forced.Ok, forced.Error);
            Assert.StartsWith("transcoded", forced.Notes);
        }

        [FfmpegFact]
        public async Task A_video_under_three_seconds_can_never_be_posted()
        {
            using var dir = new TempDir();
            string source = Path.Combine(dir.Path, "short.mp4");
            Make("-f", "lavfi", "-i", "testsrc=size=320x240:rate=30", "-t", "1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", source);
            var result = await RealTools().PrepareAsync(source, PostFormat.Reel, false, ImageFit.BlurPad, Path.Combine(dir.Path, "out", "x"), false, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.True(result.Permanent);
            Assert.Contains("at least 3 seconds", result.Error);
        }

        [FfmpegFact]
        public async Task Images_become_jpegs_inside_instagrams_aspect_limits()
        {
            using var dir = new TempDir();
            string tall = Path.Combine(dir.Path, "tall.png");
            string wide = Path.Combine(dir.Path, "wide.jpg");
            string square = Path.Combine(dir.Path, "square.webp");
            Make("-f", "lavfi", "-i", "testsrc=size=1080x2400", "-frames:v", "1", tall);
            Make("-f", "lavfi", "-i", "testsrc=size=2400x1000", "-frames:v", "1", wide);
            Make("-f", "lavfi", "-i", "testsrc=size=900x900", "-frames:v", "1", square);
            var tools = RealTools();

            var feedTall = await tools.PrepareAsync(tall, PostFormat.Photo, false, ImageFit.BlurPad, Path.Combine(dir.Path, "out", "t"), false, CancellationToken.None);
            Assert.True(feedTall.Ok, feedTall.Error);
            Assert.EndsWith(".jpg", feedTall.OutputPath);
            Assert.Equal((1080, 1350), (feedTall.Width, feedTall.Height));
            Assert.Contains("blurred background", feedTall.Notes);

            var feedWide = await tools.PrepareAsync(wide, PostFormat.Photo, false, ImageFit.Crop, Path.Combine(dir.Path, "out", "w"), false, CancellationToken.None);
            Assert.True(feedWide.Ok, feedWide.Error);
            Assert.Equal((1080, 566), (feedWide.Width, feedWide.Height));

            var story = await tools.PrepareAsync(square, PostFormat.Story, false, ImageFit.Pad, Path.Combine(dir.Path, "out", "s"), false, CancellationToken.None);
            Assert.True(story.Ok, story.Error);
            Assert.Equal((1080, 1920), (story.Width, story.Height));

            var plain = await tools.PrepareAsync(square, PostFormat.Photo, false, ImageFit.BlurPad, Path.Combine(dir.Path, "out", "p"), false, CancellationToken.None);
            Assert.True(plain.Ok, plain.Error);
            Assert.Equal((900, 900), (plain.Width, plain.Height));
            Assert.True(new FileInfo(plain.OutputPath!).Length <= OmniGramMediaTools.MaxImageBytes);
        }
    }
}
