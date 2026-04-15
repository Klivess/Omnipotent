using Omnipotent.Data_Handling;
using Omnipotent.Services.OmniGram.Models;

namespace Omnipotent.Services.OmniGram
{
    public class OmniGramMediaManager
    {
        private readonly OmniGram service;
        private static readonly string[] SupportedImageExtensions = { ".jpg", ".jpeg", ".png", ".webp" };
        private static readonly string[] SupportedVideoExtensions = { ".mp4", ".mov" };
        private const long MaxImageSizeBytes = 8 * 1024 * 1024;   // 8 MB
        private const long MaxVideoSizeBytes = 100 * 1024 * 1024;  // 100 MB

        public OmniGramMediaManager(OmniGram service)
        {
            this.service = service;
        }

        public void EnsureDirectories()
        {
            Directory.CreateDirectory(OmniPaths.GlobalPaths.OmniGramMediaDirectory);
            Directory.CreateDirectory(OmniPaths.GlobalPaths.OmniGramUploadsDirectory);
        }

        public async Task<string> StoreUploadedMedia(string sourceFilePath, string accountId)
        {
            if (!File.Exists(sourceFilePath))
                throw new FileNotFoundException($"Source file not found: {sourceFilePath}");

            ValidateMediaFile(sourceFilePath);

            var ext = Path.GetExtension(sourceFilePath).ToLowerInvariant();
            var destDir = Path.Combine(OmniPaths.GlobalPaths.OmniGramMediaDirectory, accountId);
            Directory.CreateDirectory(destDir);

            var destFileName = $"{Guid.NewGuid()}{ext}";
            var destPath = Path.Combine(destDir, destFileName);

            File.Copy(sourceFilePath, destPath);
            await service.ServiceLog($"[OmniGram] Stored media: {destPath}");
            return destPath;
        }

        public async Task<List<string>> CopyFromMemeScraper(string sourceDirectory, string accountId, int maxFiles = 10)
        {
            var storedPaths = new List<string>();
            if (!Directory.Exists(sourceDirectory)) return storedPaths;

            var files = Directory.GetFiles(sourceDirectory)
                .Where(f => IsSupported(f))
                .Take(maxFiles)
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    var stored = await StoreUploadedMedia(file, accountId);
                    storedPaths.Add(stored);
                }
                catch (Exception ex)
                {
                    await service.ServiceLogError(ex, $"[OmniGram] Failed to copy MemeScraper media: {file}");
                }
            }

            return storedPaths;
        }

        public void ValidateMediaFile(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (!IsSupported(filePath))
                throw new InvalidOperationException($"Unsupported media format: {ext}. Supported: {string.Join(", ", SupportedImageExtensions.Concat(SupportedVideoExtensions))}");

            var fileInfo = new FileInfo(filePath);
            bool isVideo = SupportedVideoExtensions.Contains(ext);

            if (isVideo && fileInfo.Length > MaxVideoSizeBytes)
                throw new InvalidOperationException($"Video exceeds max size ({MaxVideoSizeBytes / (1024 * 1024)} MB): {filePath}");

            if (!isVideo && fileInfo.Length > MaxImageSizeBytes)
                throw new InvalidOperationException($"Image exceeds max size ({MaxImageSizeBytes / (1024 * 1024)} MB): {filePath}");
        }

        public OmniGramContentType InferContentType(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return SupportedVideoExtensions.Contains(ext) ? OmniGramContentType.Reel : OmniGramContentType.Photo;
        }

        public bool IsSupported(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return SupportedImageExtensions.Contains(ext) || SupportedVideoExtensions.Contains(ext);
        }

        public async Task CleanupOldMedia(int retentionDays = 30)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
                var mediaDir = OmniPaths.GlobalPaths.OmniGramMediaDirectory;
                if (!Directory.Exists(mediaDir)) return;

                int removed = 0;
                foreach (var file in Directory.GetFiles(mediaDir, "*.*", SearchOption.AllDirectories))
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.CreationTimeUtc < cutoff)
                    {
                        fileInfo.Delete();
                        removed++;
                    }
                }

                if (removed > 0)
                    await service.ServiceLog($"[OmniGram] Cleaned up {removed} media files older than {retentionDays} days.");
            }
            catch (Exception ex)
            {
                await service.ServiceLogError(ex, "[OmniGram] Media cleanup failed");
            }
        }

        public List<ContentFolderFileInfo> ListContentFolder(string folderPath, List<string> usedPaths = null)
        {
            var result = new List<ContentFolderFileInfo>();
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return result;

            usedPaths ??= new List<string>();

            foreach (var file in Directory.GetFiles(folderPath).Where(IsSupported))
            {
                var info = new FileInfo(file);
                result.Add(new ContentFolderFileInfo
                {
                    FileName = info.Name,
                    FullPath = info.FullName,
                    ContentType = InferContentType(file).ToString(),
                    SizeBytes = info.Length,
                    IsUsed = usedPaths.Contains(info.FullName)
                });
            }

            return result;
        }
    }

    public class ContentFolderFileInfo
    {
        public string FileName { get; set; }
        public string FullPath { get; set; }
        public string ContentType { get; set; }
        public long SizeBytes { get; set; }
        public bool IsUsed { get; set; }
    }
}
