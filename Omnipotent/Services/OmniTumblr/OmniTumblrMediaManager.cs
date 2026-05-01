using Omnipotent.Data_Handling;
using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Services.OmniTumblr
{
    public class OmniTumblrMediaManager
    {
        private readonly OmniTumblr service;

        private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp", ".gif"
        };

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mov", ".avi", ".mkv"
        };

        public OmniTumblrMediaManager(OmniTumblr service)
        {
            this.service = service;
        }

        public async Task InitializeAsync()
        {
            EnsureDirectories();
            await service.ServiceLog("[OmniTumblr] MediaManager initialised.");
        }

        private void EnsureDirectories()
        {
            Directory.CreateDirectory(OmniPaths.GlobalPaths.OmniTumblrMediaDirectory);
            Directory.CreateDirectory(OmniPaths.GlobalPaths.OmniTumblrUploadsDirectory);
        }

        public bool IsSupported(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return PhotoExtensions.Contains(ext) || VideoExtensions.Contains(ext);
        }

        public OmniTumblrPostType InferPostType(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return VideoExtensions.Contains(ext) ? OmniTumblrPostType.Video : OmniTumblrPostType.Photo;
        }

        public async Task<string> StoreUploadedMedia(string sourcePath, string accountId)
        {
            var accountMediaDir = Path.Combine(OmniPaths.GlobalPaths.OmniTumblrMediaDirectory, accountId);
            Directory.CreateDirectory(accountMediaDir);

            var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Path.GetFileName(sourcePath)}";
            var destPath = Path.Combine(accountMediaDir, fileName);

            if (sourcePath != destPath)
                File.Copy(sourcePath, destPath, overwrite: true);

            return destPath;
        }

        public async Task<string> StoreUploadedMediaFromBytes(byte[] bytes, string originalFileName, string accountId)
        {
            var accountMediaDir = Path.Combine(OmniPaths.GlobalPaths.OmniTumblrMediaDirectory, accountId);
            Directory.CreateDirectory(accountMediaDir);

            var safeFileName = Path.GetFileName(originalFileName);
            var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{safeFileName}";
            var destPath = Path.Combine(accountMediaDir, fileName);

            await File.WriteAllBytesAsync(destPath, bytes);
            return destPath;
        }

        public List<OmniTumblrContentFolderFileInfo> GetContentFolderFiles(string folderPath, List<string> usedContentPaths)
        {
            if (!Directory.Exists(folderPath)) return new();

            return Directory.GetFiles(folderPath)
                .Where(IsSupported)
                .Select(f => new OmniTumblrContentFolderFileInfo
                {
                    FileName = Path.GetFileName(f),
                    FullPath = f,
                    PostType = InferPostType(f).ToString(),
                    SizeBytes = new FileInfo(f).Length,
                    IsUsed = usedContentPaths != null && usedContentPaths.Contains(f)
                })
                .ToList();
        }

        public async Task CleanupOldMedia(int retentionDays = 30)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
                var mediaRoot = OmniPaths.GlobalPaths.OmniTumblrMediaDirectory;
                int deleted = 0;

                foreach (var file in Directory.EnumerateFiles(mediaRoot, "*.*", SearchOption.AllDirectories))
                {
                    if (!IsSupported(file)) continue;
                    var fi = new FileInfo(file);
                    if (fi.CreationTimeUtc < cutoff)
                    {
                        fi.Delete();
                        deleted++;
                    }
                }

                if (deleted > 0)
                    await service.ServiceLog($"[OmniTumblr] Media cleanup removed {deleted} files older than {retentionDays} days.");
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, "[OmniTumblr] Media cleanup failed");
            }
        }
    }
}
