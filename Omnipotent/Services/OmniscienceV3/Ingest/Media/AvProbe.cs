using System.Text.Json;
using FFMpegCore;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Media
{
    /// <summary>What ffprobe says about an audio or video file. Unknown values are null.</summary>
    public sealed record AvFacts(long? DurationMs, int? Width, int? Height, bool HasAudio, bool HasVideo);

    /// <summary>Reads an audio or video file's length and picture size. The seam tests replace.</summary>
    public interface IAvProbe
    {
        /// <summary>
        /// The file's facts; null when no reader is installed here (the file is then left as it is, not judged).
        /// Throws <see cref="ImageUnreadableException"/> when the reader is there but cannot make sense of the file.
        /// </summary>
        Task<AvFacts?> ProbeAsync(string path, CancellationToken ct);
    }

    /// <summary>ffprobe from the ffmpeg folder ModelHub keeps, through FFMpegCore (the same binaries transcription cuts audio with).</summary>
    public sealed class FfprobeAvProbe : IAvProbe
    {
        private readonly string folder;

        public FfprobeAvProbe(string ffmpegFolder) => folder = ffmpegFolder;

        public async Task<AvFacts?> ProbeAsync(string path, CancellationToken ct)
        {
            if (!Directory.Exists(folder) || !Directory.EnumerateFiles(folder, "ffprobe*").Any()) return null;
            IMediaAnalysis analysis;
            try
            {
                analysis = await FFProbe.AnalyseAsync(path, new FFOptions { BinaryFolder = folder }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FFMpegCore.Exceptions.FFMpegException or IOException or System.ComponentModel.Win32Exception or JsonException)
            {
                throw new ImageUnreadableException("ffprobe could not read the file: " + ex.Message, unsupported: true);
            }
            var video = analysis.PrimaryVideoStream;
            long? duration = analysis.Duration > TimeSpan.Zero ? (long)analysis.Duration.TotalMilliseconds : null;
            return new AvFacts(duration, video?.Width > 0 ? video.Width : null, video?.Height > 0 ? video.Height : null, analysis.PrimaryAudioStream != null, video != null);
        }
    }
}
