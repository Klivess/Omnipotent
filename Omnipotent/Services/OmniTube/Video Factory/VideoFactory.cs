using FFMpegCore;
using Omnipotent.Data_Handling;
using Omnipotent.Services.MemeScraper;
using System.Drawing;
using System.Net;
using System.Text;

namespace Omnipotent.Services.OmniTube.Video_Factory
{
    public class VideoFactory
    {
        OmniTube parent;
        public string ffmpegPath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegDirectory), "ffmpeg.exe");
        public string ffmpegProbePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegDirectory), "ffprobe.exe");
        public VideoFactory(OmniTube parent)
        {
            this.parent = parent;
            // Constructor logic if needed
            EnsureFFmpegInstalled();
        }

        private void EnsureFFmpegInstalled()
        {
            if (File.Exists(ffmpegPath) == false)
            {
                parent.ServiceLog("FFmpeg not found, installing...");
                string zipDownloadURL = "https://github.com/ffbinaries/ffbinaries-prebuilt/releases/download/v6.1/ffmpeg-6.1-win-64.zip";
                WebClient wc = new();
                string zipFilePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegDirectory), "ffmpeg.zip");
                wc.DownloadFile(zipDownloadURL, zipFilePath);
                // Unzip the file
                System.IO.Compression.ZipFile.ExtractToDirectory(zipFilePath, OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegDirectory));
                // Delete the zip file
                if (File.Exists(zipFilePath))
                {
                    File.Delete(zipFilePath);
                }
                parent.ServiceLog("FFmpeg has been installed successfully.");
            }
            else
            {
                parent.ServiceLog("FFmpeg is already installed.");
            }

            if (File.Exists(ffmpegProbePath) == false)
            {
                parent.ServiceLog("FFmpeg not found, installing...");
                string zipDownloadURL = "https://github.com/ffbinaries/ffbinaries-prebuilt/releases/download/v6.1/ffprobe-6.1-win-64.zip";
                WebClient wc = new();
                string zipFilePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegDirectory), "ffprobe.zip");
                wc.DownloadFile(zipDownloadURL, zipFilePath);
                // Unzip the file
                System.IO.Compression.ZipFile.ExtractToDirectory(zipFilePath, OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegDirectory));
                // Delete the zip file
                if (File.Exists(zipFilePath))
                {
                    File.Delete(zipFilePath);
                }
                parent.ServiceLog("FFmpeg Probe has been installed successfully.");
            }
            else
            {
                parent.ServiceLog("FFmpeg Probe is already installed.");
            }

            GlobalFFOptions.Configure(new FFOptions
            {
                BinaryFolder = OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegDirectory),
                WorkingDirectory = OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegWorkingDirectory),
            });
        }

        public async Task<bool> ProduceMemeCompilation(List<InstagramScrapeUtilities.InstagramReel> reels, string videoOutputPath = "")
        {
            parent.ServiceLog("Starting meme compilation video production...");
            string tempDir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegWorkingDirectory), "OmniTubeMemeCompilation_" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);

            var standardWidth = 1080;
            var standardHeight = 1920;
            var standardFramerate = 30;
            var standardFormat = "mp4";
            var standardCodec = "libx264";
            var standardAudioCodec = "aac";
            var convertedFiles = new List<string>();

            try
            {
                foreach (var reel in reels)
                {
                    string inputPath = reel.GetInstagramReelVideoFilePath();
                    string outputPath = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(inputPath)}_converted.{standardFormat}");

                    var conversion = await FFMpegArguments
                        .FromFileInput(inputPath)
                        .OutputToFile(outputPath, true, options => options
                            .WithVideoCodec(standardCodec)
                            .WithAudioCodec(standardAudioCodec)
                            .WithCustomArgument($"-vf scale={standardWidth}:{standardHeight}")
                            .WithCustomArgument($"-r {standardFramerate}")
                            .WithCustomArgument("-preset veryfast")
                            .ForceFormat(standardFormat))
                        .ProcessAsynchronously();

                    convertedFiles.Add(outputPath);
                }

                parent.ServiceLog("Meme compilation video joined.");
                return FFMpeg.Join(videoOutputPath, convertedFiles.ToArray());
            }
            finally
            {
                // Clean up temp files
                foreach (var file in convertedFiles)
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                if (Directory.Exists(tempDir))
                {

                    Directory.Delete(tempDir, true);
                }
            }
        }
    }
}
