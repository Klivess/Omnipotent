using FFMpegCore;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System.Collections.Concurrent;
using System.Runtime.Serialization;
using static Omnipotent.Profiles.KMProfileManager;
using static Omnipotent.Services.KliveCloud.CloudItem;

namespace Omnipotent.Services.KliveCloud
{
    public class KliveCloud : OmniService
    {
        public List<CloudItem> CloudItems;
        public List<ShareLink> ShareLinks;
        private KliveCloudRoutes routes;
        private string metadataFilePath;
        private string shareLinksFilePath;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> videoEmbedTranscodeLocks = new();

        public KliveCloud()
        {
            name = "KliveCloud";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            string storagePath = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudStorageDirectory);
            string metadataPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudMetadataDirectory);
            string thumbnailsPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudThumbnailsDirectory);
            Directory.CreateDirectory(storagePath);
            Directory.CreateDirectory(metadataPath);
            Directory.CreateDirectory(thumbnailsPath);
            Directory.CreateDirectory(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudVideoEmbedsDirectory));
            metadataFilePath = Path.Combine(metadataPath, "cloud_metadata.json");
            shareLinksFilePath = Path.Combine(metadataPath, "share_links.json");

            GlobalFFOptions.Configure(new FFOptions
            {
                BinaryFolder = OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegDirectory),
                WorkingDirectory = OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegWorkingDirectory),
            });

            await LoadMetadata();
            await LoadShareLinks();

            routes = new KliveCloudRoutes(this);
            routes.CreateRoutes();

            ServiceLog($"KliveCloud service started with {CloudItems.Count} items loaded.");
        }

        private async Task LoadMetadata()
        {
            CloudItems = new List<CloudItem>();
            if (File.Exists(metadataFilePath))
            {
                try
                {
                    string data = await GetDataHandler().ReadDataFromFile(metadataFilePath);
                    CloudItems = JsonConvert.DeserializeObject<List<CloudItem>>(data) ?? new List<CloudItem>();
                }
                catch (Exception ex)
                {
                    ServiceLogError(ex, "Failed to load KliveCloud metadata.");
                    CloudItems = new List<CloudItem>();
                }
            }
        }

        public async Task SaveMetadata()
        {
            string json = JsonConvert.SerializeObject(CloudItems, Formatting.Indented);
            await GetDataHandler().WriteToFile(metadataFilePath, json);
        }

        public class ShareLink
        {
            public string ShareCode;
            public string ItemID;
            public string CreatedByUserID;
            public DateTime CreatedDate;
            public DateTime? ExpirationDate;
            public SharePermissionMode PermissionMode = SharePermissionMode.ReadOnly;

            [JsonProperty("SharePermissionMode", NullValueHandling = NullValueHandling.Ignore)]
            public SharePermissionMode? LegacySharePermissionMode { get; set; }

            [OnDeserialized]
            internal void OnDeserialized(StreamingContext context)
            {
                if (LegacySharePermissionMode.HasValue)
                {
                    PermissionMode = LegacySharePermissionMode.Value;
                    LegacySharePermissionMode = null;
                }
            }
        }

        public enum SharePermissionMode
        {
            ReadOnly,
            Write,
            WriteDelete
        }

        private async Task LoadShareLinks()
        {
            ShareLinks = new List<ShareLink>();
            if (File.Exists(shareLinksFilePath))
            {
                try
                {
                    string data = await GetDataHandler().ReadDataFromFile(shareLinksFilePath);
                    ShareLinks = JsonConvert.DeserializeObject<List<ShareLink>>(data) ?? new List<ShareLink>();
                }
                catch (Exception ex)
                {
                    ServiceLogError(ex, "Failed to load KliveCloud share links.");
                    ShareLinks = new List<ShareLink>();
                }
            }
        }

        public async Task SaveShareLinks()
        {
            string json = JsonConvert.SerializeObject(ShareLinks, Formatting.Indented);
            await GetDataHandler().WriteToFile(shareLinksFilePath, json);
        }

        public async Task<ShareLink> CreateShareLink(string itemID, string createdByUserID, DateTime? expirationDate, SharePermissionMode permissionMode = SharePermissionMode.ReadOnly, bool reuseExisting = true)
        {
            if (reuseExisting)
            {
                var existingLink = await GetReusableShareLink(itemID);
                if (existingLink != null)
                {
                    existingLink.ExpirationDate = expirationDate;
                    existingLink.PermissionMode = permissionMode;
                    await SaveShareLinks();
                    return existingLink;
                }
            }

            var link = new ShareLink
            {
                ShareCode = Guid.NewGuid().ToString("N"),
                ItemID = itemID,
                CreatedByUserID = createdByUserID,
                CreatedDate = DateTime.Now,
                ExpirationDate = expirationDate,
                PermissionMode = permissionMode
            };
            ShareLinks.Add(link);
            await SaveShareLinks();
            ServiceLog($"Share link created for item {itemID} by user {createdByUserID}.");
            return link;
        }

        public async Task<bool> UpdateShareLinkPermission(string shareCode, SharePermissionMode permissionMode)
        {
            var link = GetShareLinkByCode(shareCode);
            if (link == null) return false;

            link.PermissionMode = permissionMode;
            link.LegacySharePermissionMode = null;
            await SaveShareLinks();
            ServiceLog($"Share link {shareCode} permission updated to {permissionMode}.");
            return true;
        }

        public bool CanWriteThroughShareLink(ShareLink link)
        {
            return link.PermissionMode == SharePermissionMode.Write || link.PermissionMode == SharePermissionMode.WriteDelete;
        }

        public bool CanDeleteThroughShareLink(ShareLink link)
        {
            return link.PermissionMode == SharePermissionMode.WriteDelete;
        }

        public bool IsItemWithinSharedScope(ShareLink link, CloudItem item)
        {
            if (string.Equals(link.ItemID, item.ItemID, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var sharedRoot = GetItemByID(link.ItemID);
            return sharedRoot != null && sharedRoot.ItemType == CloudItemType.Folder && IsDescendantOfFolder(sharedRoot.ItemID, item);
        }

        public ShareLink GetShareLinkByCode(string shareCode)
        {
            return ShareLinks.FirstOrDefault(k => k.ShareCode == shareCode);
        }

        public async Task<ShareLink?> GetReusableShareLink(string itemID)
        {
            bool removedExpiredLinks = false;
            foreach (var expiredLink in ShareLinks
                .Where(k => k.ItemID == itemID && k.ExpirationDate.HasValue && k.ExpirationDate.Value < DateTime.Now)
                .ToList())
            {
                ShareLinks.Remove(expiredLink);
                removedExpiredLinks = true;
            }

            if (removedExpiredLinks)
            {
                await SaveShareLinks();
            }

            return ShareLinks.FirstOrDefault(k => k.ItemID == itemID);
        }

        public async Task<bool> DeleteShareLink(string shareCode)
        {
            var link = GetShareLinkByCode(shareCode);
            if (link == null) return false;
            ShareLinks.Remove(link);
            await SaveShareLinks();
            return true;
        }

        private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

        public static void ValidateItemName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be empty.");
            if (name.IndexOfAny(InvalidNameChars) >= 0)
                throw new ArgumentException("Name contains invalid characters.");
            if (name.Contains("..") || name == "." || name == "..")
                throw new ArgumentException("Name contains path traversal sequences.");
        }

        private string GetStorageBasePath()
        {
            return Path.GetFullPath(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudStorageDirectory));
        }

        public string GetFullItemPath(CloudItem item)
        {
            string basePath = GetStorageBasePath();
            string fullPath = Path.GetFullPath(Path.Combine(basePath, item.RelativePath));
            if (!fullPath.StartsWith(basePath + Path.DirectorySeparatorChar) && fullPath != basePath)
                throw new UnauthorizedAccessException("Access denied: path is outside the cloud storage directory.");
            return fullPath;
        }

        public CloudItem GetItemByID(string itemID)
        {
            return CloudItems.FirstOrDefault(k => k.ItemID == itemID);
        }

        public List<CloudItem> GetItemsInFolder(string folderID, KMPermissions userPermission)
        {
            return CloudItems
                .Where(k => k.ParentFolderID == folderID && CanAccessItem(k, userPermission))
                .Select(CloneWithEffectivePermission)
                .ToList();
        }

        public List<CloudItem> GetRootItems(KMPermissions userPermission)
        {
            return CloudItems
                .Where(k => string.IsNullOrEmpty(k.ParentFolderID) && CanAccessItem(k, userPermission))
                .Select(CloneWithEffectivePermission)
                .ToList();
        }

        public bool CanAccessItem(CloudItem item, KMPermissions userPermission)
        {
            return GetEffectiveMinimumPermission(item) <= userPermission;
        }

        public CloudItem CloneWithEffectivePermission(CloudItem item)
        {
            return new CloudItem
            {
                ItemID = item.ItemID,
                Name = item.Name,
                RelativePath = item.RelativePath,
                ParentFolderID = item.ParentFolderID,
                CreatedDate = item.CreatedDate,
                ModifiedDate = item.ModifiedDate,
                CreatedByUserID = item.CreatedByUserID,
                ItemType = item.ItemType,
                MinimumPermissionLevel = GetEffectiveMinimumPermission(item),
                FileSizeBytes = item.FileSizeBytes
            };
        }

        public KMPermissions GetEffectiveMinimumPermission(CloudItem item)
        {
            KMPermissions effectivePermission = item.MinimumPermissionLevel;
            string parentFolderID = item.ParentFolderID;
            HashSet<string> visitedFolderIds = new(StringComparer.OrdinalIgnoreCase);

            while (!string.IsNullOrWhiteSpace(parentFolderID) && visitedFolderIds.Add(parentFolderID))
            {
                var parentFolder = GetItemByID(parentFolderID);
                if (parentFolder == null)
                {
                    break;
                }

                effectivePermission = (KMPermissions)Math.Max((int)effectivePermission, (int)parentFolder.MinimumPermissionLevel);
                parentFolderID = parentFolder.ParentFolderID;
            }

            return effectivePermission;
        }

        public KMPermissions ApplyParentPermissionFloor(string parentFolderID, KMPermissions requestedPermission)
        {
            if (string.IsNullOrWhiteSpace(parentFolderID))
            {
                return requestedPermission;
            }

            var parentFolder = GetItemByID(parentFolderID);
            if (parentFolder == null)
            {
                throw new Exception("Parent folder not found.");
            }

            return (KMPermissions)Math.Max((int)requestedPermission, (int)GetEffectiveMinimumPermission(parentFolder));
        }

        public bool IsDescendantOfFolder(string folderID, CloudItem item)
        {
            string parentFolderID = item.ParentFolderID;
            HashSet<string> visitedFolderIds = new(StringComparer.OrdinalIgnoreCase);

            while (!string.IsNullOrWhiteSpace(parentFolderID) && visitedFolderIds.Add(parentFolderID))
            {
                if (string.Equals(parentFolderID, folderID, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var parentFolder = GetItemByID(parentFolderID);
                if (parentFolder == null)
                {
                    break;
                }

                parentFolderID = parentFolder.ParentFolderID;
            }

            return false;
        }

        public List<CloudItem> GetFolderDescendantsForShare(string folderID)
        {
            return CloudItems
                .Where(item => IsDescendantOfFolder(folderID, item))
                .Select(CloneWithEffectivePermission)
                .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<CloudItem> CreateFolder(string name, string parentFolderID, string createdByUserID, KMPermissions minimumPermission)
        {
            ValidateItemName(name);
            minimumPermission = ApplyParentPermissionFloor(parentFolderID, minimumPermission);

            string relativePath;
            if (string.IsNullOrEmpty(parentFolderID))
            {
                relativePath = name;
            }
            else
            {
                var parentFolder = GetItemByID(parentFolderID);
                if (parentFolder == null || parentFolder.ItemType != CloudItemType.Folder)
                    throw new Exception("Parent folder not found.");
                relativePath = Path.Combine(parentFolder.RelativePath, name);
            }

            string basePath = GetStorageBasePath();
            string fullPath = Path.GetFullPath(Path.Combine(basePath, relativePath));
            if (!fullPath.StartsWith(basePath + Path.DirectorySeparatorChar))
                throw new UnauthorizedAccessException("Access denied: path is outside the cloud storage directory.");
            Directory.CreateDirectory(fullPath);

            CloudItem folder = new CloudItem
            {
                ItemID = RandomGeneration.GenerateRandomLengthOfNumbers(12),
                Name = name,
                RelativePath = relativePath,
                ParentFolderID = parentFolderID ?? "",
                CreatedDate = DateTime.Now,
                ModifiedDate = DateTime.Now,
                CreatedByUserID = createdByUserID,
                ItemType = CloudItemType.Folder,
                MinimumPermissionLevel = minimumPermission,
                FileSizeBytes = 0
            };

            CloudItems.Add(folder);
            await SaveMetadata();
            ServiceLog($"Folder '{name}' created by user {createdByUserID}.");
            return folder;
        }

        public async Task<CloudItem> UploadFile(string fileName, byte[] fileData, string parentFolderID, string createdByUserID, KMPermissions minimumPermission)
        {
            ValidateItemName(fileName);
            minimumPermission = ApplyParentPermissionFloor(parentFolderID, minimumPermission);

            string relativePath;
            if (string.IsNullOrEmpty(parentFolderID))
            {
                relativePath = fileName;
            }
            else
            {
                var parentFolder = GetItemByID(parentFolderID);
                if (parentFolder == null || parentFolder.ItemType != CloudItemType.Folder)
                    throw new Exception("Parent folder not found.");
                relativePath = Path.Combine(parentFolder.RelativePath, fileName);
            }

            string basePath = GetStorageBasePath();
            string fullPath = Path.GetFullPath(Path.Combine(basePath, relativePath));
            if (!fullPath.StartsWith(basePath + Path.DirectorySeparatorChar))
                throw new UnauthorizedAccessException("Access denied: path is outside the cloud storage directory.");
            string directory = Path.GetDirectoryName(fullPath);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            await GetDataHandler().WriteBytesToFile(fullPath, fileData);

            CloudItem file = new CloudItem
            {
                ItemID = RandomGeneration.GenerateRandomLengthOfNumbers(12),
                Name = fileName,
                RelativePath = relativePath,
                ParentFolderID = parentFolderID ?? "",
                CreatedDate = DateTime.Now,
                ModifiedDate = DateTime.Now,
                CreatedByUserID = createdByUserID,
                ItemType = CloudItemType.File,
                MinimumPermissionLevel = minimumPermission,
                FileSizeBytes = fileData.Length
            };

            CloudItems.Add(file);
            await SaveMetadata();
            ServiceLog($"File '{fileName}' ({fileData.Length} bytes) uploaded by user {createdByUserID}.");
            return file;
        }

        public async Task<bool> DeleteItem(string itemID, KMProfile user)
        {
            return await DeleteItemInternal(itemID, user?.Name ?? user?.UserID ?? "Unknown user");
        }

        public async Task<bool> DeleteItem(string itemID, string deletedByLabel)
        {
            return await DeleteItemInternal(itemID, deletedByLabel);
        }

        private async Task<bool> DeleteItemInternal(string itemID, string deletedByLabel)
        {
            var item = GetItemByID(itemID);
            if (item == null) return false;

            if (item.ItemType == CloudItemType.Folder)
            {
                var children = CloudItems.Where(k => k.ParentFolderID == itemID).ToList();
                foreach (var child in children)
                {
                    await DeleteItemInternal(child.ItemID, deletedByLabel);
                }

                string fullPath = GetFullItemPath(item);
                if (Directory.Exists(fullPath))
                    Directory.Delete(fullPath, true);
            }
            else
            {
                string fullPath = GetFullItemPath(item);
                if (File.Exists(fullPath))
                    await GetDataHandler().DeleteFile(fullPath);
            }

            CloudItems.Remove(item);
            await SaveMetadata();
            ServiceLog($"Item '{item.Name}' deleted by {deletedByLabel}.");
            return true;
        }

        public async Task<CloudItem> UpdateItemPermission(string itemID, KMPermissions newPermission)
        {
            var item = GetItemByID(itemID);
            if (item == null) return null;
            item.MinimumPermissionLevel = ApplyParentPermissionFloor(item.ParentFolderID, newPermission);
            item.ModifiedDate = DateTime.Now;
            await SaveMetadata();
            return CloneWithEffectivePermission(item);
        }

        public async Task<byte[]> DownloadFile(string itemID)
        {
            var item = GetItemByID(itemID);
            if (item == null || item.ItemType != CloudItemType.File) return null;
            string fullPath = GetFullItemPath(item);
            if (!File.Exists(fullPath)) return null;
            return await GetDataHandler().ReadBytesFromFile(fullPath, true);
        }

        public CloudItem GetFolderTree(string folderID, KMPermissions userPermission)
        {
            var folder = GetItemByID(folderID);
            if (folder == null || folder.ItemType != CloudItemType.Folder) return null;
            if (folder.MinimumPermissionLevel > userPermission) return null;
            return folder;
        }

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".tif", ".ico", ".svg"
        };

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov", ".qt", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".3g2",
            ".ogv", ".ogg", ".ts", ".mts", ".m2ts", ".vob", ".asf", ".divx", ".mxf"
        };

        private static readonly Dictionary<string, string> VideoMimeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            { ".mp4", "video/mp4" },
            { ".mkv", "video/x-matroska" },
            { ".avi", "video/x-msvideo" },
            { ".mov", "video/quicktime" },
            { ".qt", "video/quicktime" },
            { ".wmv", "video/x-ms-wmv" },
            { ".flv", "video/x-flv" },
            { ".webm", "video/webm" },
            { ".m4v", "video/x-m4v" },
            { ".mpg", "video/mpeg" },
            { ".mpeg", "video/mpeg" },
            { ".3gp", "video/3gpp" },
            { ".3g2", "video/3gpp2" },
            { ".ogv", "video/ogg" },
            { ".ogg", "video/ogg" },
            { ".ts", "video/mp2t" },
            { ".mts", "video/mp2t" },
            { ".m2ts", "video/mp2t" },
            { ".vob", "video/dvd" },
            { ".asf", "video/x-ms-asf" },
            { ".divx", "video/divx" },
            { ".mxf", "application/mxf" }
        };

        public string GetVideoMimeType(CloudItem item)
        {
            string ext = Path.GetExtension(item.Name);
            return VideoMimeTypes.TryGetValue(ext, out string mime) ? mime : "application/octet-stream";
        }

        public bool IsImage(CloudItem item)
        {
            return item.ItemType == CloudItemType.File && ImageExtensions.Contains(Path.GetExtension(item.Name));
        }

        public bool IsVideo(CloudItem item)
        {
            return item.ItemType == CloudItemType.File && VideoExtensions.Contains(Path.GetExtension(item.Name));
        }

        public bool IsPreviewable(CloudItem item)
        {
            return IsImage(item) || IsVideo(item);
        }

        private string GetThumbnailCachePath(string itemID, int width, int height)
        {
            string thumbnailsDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudThumbnailsDirectory);
            return Path.Combine(thumbnailsDir, $"{itemID}_{width}x{height}.jpg");
        }

        private string GetVideoEmbedCachePath(string itemID)
        {
            string embedsDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudVideoEmbedsDirectory);
            Directory.CreateDirectory(embedsDir);
            return Path.Combine(embedsDir, $"{itemID}_discord.mp4");
        }

        private Task RemuxDiscordOptimizedMp4(string sourcePath, string cachePath)
        {
            return FFMpegArguments
                .FromFileInput(sourcePath)
                .OutputToFile(cachePath, true, options => options
                    .WithCustomArgument("-map 0:v:0 -map 0:a? -c copy -movflags +faststart")
                    .ForceFormat("mp4"))
                .ProcessAsynchronously();
        }

        private Task TranscodeDiscordOptimizedMp4(string sourcePath, string cachePath)
        {
            return FFMpegArguments
                .FromFileInput(sourcePath)
                .OutputToFile(cachePath, true, options => options
                    .WithCustomArgument("-map 0:v:0 -map 0:a? -vf \"scale='min(1280,iw)':'min(720,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2\" -c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -c:a aac -b:a 128k -movflags +faststart")
                    .ForceFormat("mp4"))
                .ProcessAsynchronously();
        }

        public async Task<string?> GetDiscordCompatibleVideoPath(CloudItem item)
        {
            string sourcePath = GetFullItemPath(item);
            if (!File.Exists(sourcePath)) return null;

            string sourceExtension = Path.GetExtension(item.Name);
            string cachePath = GetVideoEmbedCachePath(item.ItemID);

            // Fast path: cache exists and is up-to-date - avoid acquiring transcode lock
            // so concurrent Range requests during playback don't serialize behind each other.
            if (File.Exists(cachePath) && File.GetLastWriteTimeUtc(cachePath) >= File.GetLastWriteTimeUtc(sourcePath))
            {
                return cachePath;
            }

            var transcodeLock = videoEmbedTranscodeLocks.GetOrAdd(item.ItemID, _ => new SemaphoreSlim(1, 1));
            await transcodeLock.WaitAsync();
            try
            {
                var sourceWriteTime = File.GetLastWriteTimeUtc(sourcePath);
                if (File.Exists(cachePath) && File.GetLastWriteTimeUtc(cachePath) >= sourceWriteTime)
                {
                    return cachePath;
                }

                if (string.Equals(sourceExtension, ".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await RemuxDiscordOptimizedMp4(sourcePath, cachePath);
                    }
                    catch (Exception ex)
                    {
                        ServiceLogError(ex, $"Failed to remux Discord MP4 for {sourcePath}; retrying with full transcode");
                        if (File.Exists(cachePath))
                        {
                            File.Delete(cachePath);
                        }

                        await TranscodeDiscordOptimizedMp4(sourcePath, cachePath);
                    }
                }
                else
                {
                    await TranscodeDiscordOptimizedMp4(sourcePath, cachePath);
                }

                return File.Exists(cachePath) ? cachePath : null;
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, $"Failed to generate Discord-compatible MP4 for {sourcePath}");
                return null;
            }
            finally
            {
                transcodeLock.Release();
            }
        }

        public async Task<byte[]> GeneratePreview(CloudItem item, int maxWidth, int maxHeight)
        {
            string sourcePath = GetFullItemPath(item);
            if (!File.Exists(sourcePath)) return null;

            string cachePath = GetThumbnailCachePath(item.ItemID, maxWidth, maxHeight);

            if (File.Exists(cachePath))
            {
                var cacheWriteTime = File.GetLastWriteTimeUtc(cachePath);
                var sourceWriteTime = File.GetLastWriteTimeUtc(sourcePath);
                if (cacheWriteTime >= sourceWriteTime)
                {
                    return await File.ReadAllBytesAsync(cachePath);
                }
            }

            if (IsImage(item))
            {
                return await GenerateImageThumbnail(sourcePath, cachePath, maxWidth, maxHeight);
            }
            else if (IsVideo(item))
            {
                return await GenerateVideoThumbnail(sourcePath, cachePath, maxWidth, maxHeight);
            }

            return null;
        }

        private async Task<byte[]> GenerateImageThumbnail(string sourcePath, string cachePath, int maxWidth, int maxHeight)
        {
            try
            {
                await FFMpegArguments
                    .FromFileInput(sourcePath)
                    .OutputToFile(cachePath, true, options => options
                        .WithCustomArgument($"-vf \"scale='min({maxWidth},iw)':min'({maxHeight},ih)':force_original_aspect_ratio=decrease\"")
                        .WithFrameOutputCount(1)
                        .ForceFormat("image2"))
                    .ProcessAsynchronously();

                if (File.Exists(cachePath))
                {
                    return await File.ReadAllBytesAsync(cachePath);
                }
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, $"Failed to generate image thumbnail for {sourcePath}");
            }
            return null;
        }

        private async Task<byte[]> GenerateVideoThumbnail(string sourcePath, string cachePath, int maxWidth, int maxHeight)
        {
            try
            {
                var mediaInfo = await FFProbe.AnalyseAsync(sourcePath);
                TimeSpan captureTime = mediaInfo.Duration.TotalSeconds > 1
                    ? TimeSpan.FromSeconds(1)
                    : TimeSpan.Zero;

                await FFMpegArguments
                    .FromFileInput(sourcePath, false, options => options
                        .Seek(captureTime))
                    .OutputToFile(cachePath, true, options => options
                        .WithCustomArgument($"-vf \"scale='min({maxWidth},iw)':min'({maxHeight},ih)':force_original_aspect_ratio=decrease\"")
                        .WithFrameOutputCount(1)
                        .ForceFormat("image2"))
                    .ProcessAsynchronously();

                if (File.Exists(cachePath))
                {
                    return await File.ReadAllBytesAsync(cachePath);
                }
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, $"Failed to generate video thumbnail for {sourcePath}");
            }
            return null;
        }
    }
}
