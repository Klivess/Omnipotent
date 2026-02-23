using FFMpegCore;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
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

        public async Task<ShareLink> CreateShareLink(string itemID, string createdByUserID, DateTime? expirationDate)
        {
            var link = new ShareLink
            {
                ShareCode = Guid.NewGuid().ToString("N"),
                ItemID = itemID,
                CreatedByUserID = createdByUserID,
                CreatedDate = DateTime.Now,
                ExpirationDate = expirationDate
            };
            ShareLinks.Add(link);
            await SaveShareLinks();
            ServiceLog($"Share link created for item {itemID} by user {createdByUserID}.");
            return link;
        }

        public ShareLink GetShareLinkByCode(string shareCode)
        {
            return ShareLinks.FirstOrDefault(k => k.ShareCode == shareCode);
        }

        public async Task<bool> DeleteShareLink(string shareCode)
        {
            var link = GetShareLinkByCode(shareCode);
            if (link == null) return false;
            ShareLinks.Remove(link);
            await SaveShareLinks();
            return true;
        }

        public string GetFullItemPath(CloudItem item)
        {
            string basePath = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudStorageDirectory);
            return Path.Combine(basePath, item.RelativePath);
        }

        public CloudItem GetItemByID(string itemID)
        {
            return CloudItems.FirstOrDefault(k => k.ItemID == itemID);
        }

        public List<CloudItem> GetItemsInFolder(string folderID, KMPermissions userPermission)
        {
            return CloudItems
                .Where(k => k.ParentFolderID == folderID && k.MinimumPermissionLevel <= userPermission)
                .ToList();
        }

        public List<CloudItem> GetRootItems(KMPermissions userPermission)
        {
            return CloudItems
                .Where(k => string.IsNullOrEmpty(k.ParentFolderID) && k.MinimumPermissionLevel <= userPermission)
                .ToList();
        }

        public async Task<CloudItem> CreateFolder(string name, string parentFolderID, string createdByUserID, KMPermissions minimumPermission)
        {
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

            string fullPath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudStorageDirectory), relativePath);
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

            string fullPath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudStorageDirectory), relativePath);
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
            var item = GetItemByID(itemID);
            if (item == null) return false;

            if (item.ItemType == CloudItemType.Folder)
            {
                var children = CloudItems.Where(k => k.ParentFolderID == itemID).ToList();
                foreach (var child in children)
                {
                    await DeleteItem(child.ItemID, user);
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
            ServiceLog($"Item '{item.Name}' deleted by user {user.Name}.");
            return true;
        }

        public async Task<CloudItem> UpdateItemPermission(string itemID, KMPermissions newPermission)
        {
            var item = GetItemByID(itemID);
            if (item == null) return null;
            item.MinimumPermissionLevel = newPermission;
            item.ModifiedDate = DateTime.Now;
            await SaveMetadata();
            return item;
        }

        public async Task<byte[]> DownloadFile(string itemID)
        {
            var item = GetItemByID(itemID);
            if (item == null || item.ItemType != CloudItemType.File) return null;
            string fullPath = GetFullItemPath(item);
            if (!File.Exists(fullPath)) return null;
            return await GetDataHandler().ReadBytesFromFile(fullPath);
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
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp"
        };

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
