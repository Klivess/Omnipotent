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
        private KliveCloudRoutes routes;
        private string metadataFilePath;

        public KliveCloud()
        {
            name = "KliveCloud";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            string storagePath = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudStorageDirectory);
            string metadataPath = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudMetadataDirectory);
            Directory.CreateDirectory(storagePath);
            Directory.CreateDirectory(metadataPath);
            metadataFilePath = Path.Combine(metadataPath, "cloud_metadata.json");

            await LoadMetadata();

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
    }
}
