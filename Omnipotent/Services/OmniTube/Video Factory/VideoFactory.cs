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
            GlobalFFOptions.Configure(new FFOptions
            {
                BinaryFolder = OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegDirectory),
                WorkingDirectory = OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegWorkingDirectory),
            });
        }

        public async Task ProduceMemeCompilation(List<InstagramScrapeUtilities.InstagramReel> reels, string videoOutputPath = "")
        {
            FFMpeg.Join(videoOutputPath, reels.Select(k => k.InstagramReelVideoFilePath).ToArray());
        }
    }
}
