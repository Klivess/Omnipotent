using Newtonsoft.Json;
using Omnipotent.Profiles;
using System.Collections.Specialized;
using System.Net;
using static Omnipotent.Profiles.KMProfileManager;
using static Omnipotent.Services.KliveCloud.CloudItem;

namespace Omnipotent.Services.KliveCloud
{
    public class KliveCloudRoutes
    {
        private KliveCloud parent;

        public KliveCloudRoutes(KliveCloud parent)
        {
            this.parent = parent;
        }

        private bool TryParseSharePermissionMode(string rawValue, out KliveCloud.SharePermissionMode permissionMode)
        {
            permissionMode = KliveCloud.SharePermissionMode.ReadOnly;

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return true;
            }

            if (int.TryParse(rawValue, out int numericValue) && Enum.IsDefined(typeof(KliveCloud.SharePermissionMode), numericValue))
            {
                permissionMode = (KliveCloud.SharePermissionMode)numericValue;
                return true;
            }

            return Enum.TryParse(rawValue, true, out permissionMode);
        }

        private async Task<(KliveCloud.ShareLink Link, CloudItem SharedItem)?> ResolveShareScope(global::Omnipotent.Services.KliveAPI.KliveAPI.UserRequest req, string shareCode)
        {
            if (string.IsNullOrWhiteSpace(shareCode))
            {
                await req.ReturnResponse("ShareCodeRequired", code: HttpStatusCode.BadRequest);
                return null;
            }

            var link = parent.GetShareLinkByCode(shareCode);
            if (link == null)
            {
                await req.ReturnResponse("ShareLinkNotFound", code: HttpStatusCode.NotFound);
                return null;
            }

            if (link.ExpirationDate.HasValue && link.ExpirationDate.Value < DateTime.Now)
            {
                await parent.DeleteShareLink(shareCode);
                await req.ReturnResponse("ShareLinkExpired", code: HttpStatusCode.Gone);
                return null;
            }

            var sharedItem = parent.GetItemByID(link.ItemID);
            if (sharedItem == null)
            {
                await req.ReturnResponse("SharedItemNotFound", code: HttpStatusCode.NotFound);
                return null;
            }

            return (link, sharedItem);
        }

        private async Task<CloudItem?> ResolveSharedTargetFolder(global::Omnipotent.Services.KliveAPI.KliveAPI.UserRequest req, CloudItem sharedItem, string requestedFolderID)
        {
            if (sharedItem.ItemType != CloudItemType.Folder)
            {
                await req.ReturnResponse("SharedLinkIsNotAFolder", code: HttpStatusCode.BadRequest);
                return null;
            }

            if (string.IsNullOrWhiteSpace(requestedFolderID))
            {
                return sharedItem;
            }

            var targetFolder = parent.GetItemByID(requestedFolderID);
            if (targetFolder == null || targetFolder.ItemType != CloudItemType.Folder || !parent.IsItemWithinSharedScope(sharedItem, targetFolder))
            {
                await req.ReturnResponse("SharedFolderTargetNotFound", code: HttpStatusCode.NotFound);
                return null;
            }

            return targetFolder;
        }

        private async Task<CloudItem?> ResolveSharedFileTarget(global::Omnipotent.Services.KliveAPI.KliveAPI.UserRequest req, CloudItem sharedItem, string requestedItemID)
        {
            if (sharedItem.ItemType == CloudItemType.File)
            {
                return sharedItem;
            }

            if (string.IsNullOrWhiteSpace(requestedItemID))
            {
                await req.ReturnResponse("SharedFolderRequiresItemID", code: HttpStatusCode.BadRequest);
                return null;
            }

            var requestedItem = parent.GetItemByID(requestedItemID);
            if (requestedItem == null || requestedItem.ItemType != CloudItemType.File || !parent.IsItemWithinSharedScope(sharedItem, requestedItem, includeSharedItem: false))
            {
                await req.ReturnResponse("SharedFileNotFound", code: HttpStatusCode.NotFound);
                return null;
            }

            return requestedItem;
        }

        private async Task<CloudItem?> ResolveSharedDeleteTarget(global::Omnipotent.Services.KliveAPI.KliveAPI.UserRequest req, CloudItem sharedItem, string itemID)
        {
            if (string.IsNullOrWhiteSpace(itemID))
            {
                await req.ReturnResponse("ItemIDRequired", code: HttpStatusCode.BadRequest);
                return null;
            }

            var targetItem = parent.GetItemByID(itemID);
            if (targetItem == null || !parent.IsItemWithinSharedScope(sharedItem, targetItem, includeSharedItem: false))
            {
                await req.ReturnResponse("SharedItemNotFound", code: HttpStatusCode.NotFound);
                return null;
            }

            return targetItem;
        }

        private async Task StreamVideoFile(global::Omnipotent.Services.KliveAPI.KliveAPI.UserRequest req, CloudItem item)
        {
            string filePath = parent.GetFullItemPath(item);
            if (!File.Exists(filePath))
            {
                await req.ReturnResponse("FileNotFoundOnDisk", code: HttpStatusCode.NotFound);
                return;
            }

            var fileInfo = new FileInfo(filePath);
            long fileLength = fileInfo.Length;
            string mimeType = parent.GetVideoMimeType(item);

            string rangeHeader = req.req.Headers["Range"];

            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                string rangeValue = rangeHeader.Substring("bytes=".Length);
                string[] parts = rangeValue.Split('-');
                long start = long.Parse(parts[0]);
                long end = !string.IsNullOrEmpty(parts[1]) ? long.Parse(parts[1]) : fileLength - 1;

                if (start >= fileLength || end >= fileLength || start > end)
                {
                    NameValueCollection rangeErrHeaders = new();
                    rangeErrHeaders.Add("Accept-Ranges", "bytes");
                    rangeErrHeaders.Add("Content-Range", $"bytes */{fileLength}");
                    await req.ReturnBinaryResponse(Array.Empty<byte>(), mimeType, (HttpStatusCode)416, rangeErrHeaders);
                    return;
                }

                long contentLength = end - start + 1;
                NameValueCollection rangeHeaders = new();
                rangeHeaders.Add("Accept-Ranges", "bytes");
                rangeHeaders.Add("Content-Range", $"bytes {start}-{end}/{fileLength}");

                using Stream output = req.PrepareStreamResponse(mimeType, contentLength, (HttpStatusCode)206, rangeHeaders);
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Seek(start, SeekOrigin.Begin);
                byte[] buffer = new byte[65536];
                long remaining = contentLength;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int bytesRead = await fs.ReadAsync(buffer, 0, toRead);
                    if (bytesRead == 0) break;
                    await output.WriteAsync(buffer, 0, bytesRead);
                    remaining -= bytesRead;
                }
            }
            else
            {
                NameValueCollection fullHeaders = new();
                fullHeaders.Add("Accept-Ranges", "bytes");

                using Stream output = req.PrepareStreamResponse(mimeType, fileLength, HttpStatusCode.OK, fullHeaders);
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] buffer = new byte[65536];
                int bytesRead;
                while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await output.WriteAsync(buffer, 0, bytesRead);
                }
            }
        }

        public async void CreateRoutes()
        {
            // List items at root or in a specific folder
            await parent.CreateAPIRoute("/KliveCloud/ListItems", async (req) =>
            {
                try
                {
                    string folderID = req.userParameters.Get("folderID");
                    KMPermissions userPerm = req.user != null ? req.user.KlivesManagementRank : KMPermissions.Anybody;

                    List<CloudItem> items;
                    if (string.IsNullOrEmpty(folderID))
                    {
                        items = parent.GetRootItems(userPerm);
                    }
                    else
                    {
                        var folder = parent.GetItemByID(folderID);
                        if (folder == null || folder.ItemType != CloudItemType.Folder)
                        {
                            await req.ReturnResponse("FolderNotFound", code: HttpStatusCode.NotFound);
                            return;
                        }
                        if (!parent.CanAccessItem(folder, userPerm))
                        {
                            await req.ReturnResponse("InsufficientPermission", code: HttpStatusCode.Forbidden);
                            return;
                        }
                        items = parent.GetItemsInFolder(folderID, userPerm);
                    }

                    string json = JsonConvert.SerializeObject(items);
                    await req.ReturnResponse(json, "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            // Get full folder tree structure recursively
            await parent.CreateAPIRoute("/KliveCloud/GetFolderTree", async (req) =>
            {
                try
                {
                    string folderID = req.userParameters.Get("folderID");
                    KMPermissions userPerm = req.user.KlivesManagementRank;

                    List<CloudItem> allItems;
                    if (string.IsNullOrEmpty(folderID))
                    {
                        allItems = parent.CloudItems
                            .Where(k => parent.CanAccessItem(k, userPerm))
                            .Select(parent.CloneWithEffectivePermission)
                            .ToList();
                    }
                    else
                    {
                        var folder = parent.GetItemByID(folderID);
                        if (folder == null || folder.ItemType != CloudItemType.Folder)
                        {
                            await req.ReturnResponse("FolderNotFound", code: HttpStatusCode.NotFound);
                            return;
                        }
                        if (!parent.CanAccessItem(folder, userPerm))
                        {
                            await req.ReturnResponse("InsufficientPermission", code: HttpStatusCode.Forbidden);
                            return;
                        }
                        allItems = GetDescendants(folderID, userPerm);
                        allItems.Insert(0, parent.CloneWithEffectivePermission(folder));
                    }

                    string json = JsonConvert.SerializeObject(allItems);
                    await req.ReturnResponse(json, "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            // Get info about a specific item
            await parent.CreateAPIRoute("/KliveCloud/GetItemInfo", async (req) =>
            {
                try
                {
                    string itemID = req.userParameters.Get("itemID");
                    var item = parent.GetItemByID(itemID);
                    if (item == null)
                    {
                        await req.ReturnResponse("ItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (!parent.CanAccessItem(item, req.user.KlivesManagementRank))
                    {
                        await req.ReturnResponse("InsufficientPermission", code: HttpStatusCode.Forbidden);
                        return;
                    }
                    string json = JsonConvert.SerializeObject(parent.CloneWithEffectivePermission(item));
                    await req.ReturnResponse(json, "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            // Create a folder
            await parent.CreateAPIRoute("/KliveCloud/CreateFolder", async (req) =>
            {
                try
                {
                    string name = req.userParameters.Get("name");
                    string parentFolderID = req.userParameters.Get("parentFolderID");
                    string permLevelStr = req.userParameters.Get("permissionLevel");

                    if (string.IsNullOrEmpty(name))
                    {
                        await req.ReturnResponse("FolderNameRequired", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    KMPermissions permLevel = KMPermissions.Guest;
                    if (!string.IsNullOrEmpty(permLevelStr))
                    {
                        permLevel = (KMPermissions)Convert.ToInt32(permLevelStr);
                    }

                    if (permLevel > req.user.KlivesManagementRank)
                    {
                        await req.ReturnResponse("PermissionLevelTooHigh", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    var folder = await parent.CreateFolder(name, parentFolderID, req.user.UserID, permLevel);
                    string json = JsonConvert.SerializeObject(folder);
                    await req.ReturnResponse(json, "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Guest);

            // Upload a file (file bytes sent as request body)
            await parent.CreateAPIRoute("/KliveCloud/UploadFile", async (req) =>
            {
                try
                {
                    string fileName = req.userParameters.Get("fileName");
                    string parentFolderID = req.userParameters.Get("parentFolderID");
                    string permLevelStr = req.userParameters.Get("permissionLevel");

                    if (string.IsNullOrEmpty(fileName))
                    {
                        await req.ReturnResponse("FileNameRequired", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    KMPermissions permLevel = KMPermissions.Guest;
                    if (!string.IsNullOrEmpty(permLevelStr))
                    {
                        permLevel = (KMPermissions)Convert.ToInt32(permLevelStr);
                    }

                    if (permLevel > req.user.KlivesManagementRank)
                    {
                        await req.ReturnResponse("PermissionLevelTooHigh", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    // Use the raw bytes already read by the server listen loop
                    byte[] fileData = req.userMessageBytes;

                    if (fileData.Length == 0)
                    {
                        await req.ReturnResponse("EmptyFileBody", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var file = await parent.UploadFile(fileName, fileData, parentFolderID, req.user.UserID, permLevel);
                    string json = JsonConvert.SerializeObject(file);
                    await req.ReturnResponse(json, "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Guest);

            // Download a file
            await parent.CreateAPIRoute("/KliveCloud/DownloadFile", async (req) =>
            {
                try
                {
                    string itemID = req.userParameters.Get("itemID");
                    var item = parent.GetItemByID(itemID);
                    if (item == null)
                    {
                        await req.ReturnResponse("ItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (item.ItemType != CloudItemType.File)
                    {
                        await req.ReturnResponse("ItemIsNotAFile", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    if (!parent.CanAccessItem(item, req.user.KlivesManagementRank))
                    {
                        await req.ReturnResponse("InsufficientPermission", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    byte[] fileData = await parent.DownloadFile(itemID);
                    if (fileData == null)
                    {
                        await req.ReturnResponse("FileNotFoundOnDisk", code: HttpStatusCode.NotFound);
                        return;
                    }

                    NameValueCollection dlHeaders = new();
                    dlHeaders.Add("Content-Disposition", $"attachment; filename=\"{item.Name}\"");
                    await req.ReturnBinaryResponse(fileData, "application/octet-stream", HttpStatusCode.OK, dlHeaders);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            // Delete a file or folder
            await parent.CreateAPIRoute("/KliveCloud/DeleteItem", async (req) =>
            {
                try
                {
                    string itemID = req.userParameters.Get("itemID");
                    var item = parent.GetItemByID(itemID);
                    if (item == null)
                    {
                        await req.ReturnResponse("ItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (!parent.CanAccessItem(item, req.user.KlivesManagementRank))
                    {
                        await req.ReturnResponse("InsufficientPermission", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    bool success = await parent.DeleteItem(itemID, req.user);
                    if (success)
                    {
                        await req.ReturnResponse("ItemDeleted", code: HttpStatusCode.OK);
                    }
                    else
                    {
                        await req.ReturnResponse("DeleteFailed", code: HttpStatusCode.InternalServerError);
                    }
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Guest);

            // Change permission level of a file or folder
            await parent.CreateAPIRoute("/KliveCloud/ChangeItemPermission", async (req) =>
            {
                try
                {
                    string itemID = req.userParameters.Get("itemID");
                    string permLevelStr = req.userParameters.Get("permissionLevel");

                    var item = parent.GetItemByID(itemID);
                    if (item == null)
                    {
                        await req.ReturnResponse("ItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    KMPermissions newPermLevel = (KMPermissions)Convert.ToInt32(permLevelStr);

                    if (newPermLevel > req.user.KlivesManagementRank)
                    {
                        await req.ReturnResponse("PermissionLevelTooHigh", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    if (!parent.CanAccessItem(item, req.user.KlivesManagementRank))
                    {
                        await req.ReturnResponse("InsufficientPermission", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    var updated = await parent.UpdateItemPermission(itemID, newPermLevel);
                    string json = JsonConvert.SerializeObject(updated);
                    await req.ReturnResponse(json, "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Guest);

            // Get drive capacity info for the drive the application is running on
            await parent.CreateAPIRoute("/KliveCloud/GetDriveInfo", async (req) =>
            {
                try
                {
                    string appDriveLetter = Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory);
                    DriveInfo drive = new DriveInfo(appDriveLetter);

                    var driveInfo = new
                    {
                        DriveName = drive.Name,
                        TotalCapacityBytes = drive.TotalSize,
                        UsedCapacityBytes = drive.TotalSize - drive.AvailableFreeSpace,
                        FreeCapacityBytes = drive.AvailableFreeSpace,
                        TotalCapacityGB = Math.Round(drive.TotalSize / 1073741824.0, 2),
                        UsedCapacityGB = Math.Round((drive.TotalSize - drive.AvailableFreeSpace) / 1073741824.0, 2),
                        FreeCapacityGB = Math.Round(drive.AvailableFreeSpace / 1073741824.0, 2),
                        UsagePercentage = Math.Round((drive.TotalSize - drive.AvailableFreeSpace) / (double)drive.TotalSize * 100, 2),
                        DriveFormat = drive.DriveFormat
                    };

                    string json = JsonConvert.SerializeObject(driveInfo);
                    await req.ReturnResponse(json, "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            // Get a preview/thumbnail image for an image or video file
            await parent.CreateAPIRoute("/KliveCloud/GetPreview", async (req) =>
            {
                try
                {
                    string itemID = req.userParameters.Get("itemID");
                    string widthStr = req.userParameters.Get("maxWidth");
                    string heightStr = req.userParameters.Get("maxHeight");

                    int maxWidth = 300;
                    int maxHeight = 300;
                    if (!string.IsNullOrEmpty(widthStr)) maxWidth = Math.Clamp(Convert.ToInt32(widthStr), 16, 1920);
                    if (!string.IsNullOrEmpty(heightStr)) maxHeight = Math.Clamp(Convert.ToInt32(heightStr), 16, 1920);

                    var item = parent.GetItemByID(itemID);
                    if (item == null)
                    {
                        await req.ReturnResponse("ItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (!parent.CanAccessItem(item, req.user.KlivesManagementRank))
                    {
                        await req.ReturnResponse("InsufficientPermission", code: HttpStatusCode.Forbidden);
                        return;
                    }
                    if (!parent.IsPreviewable(item))
                    {
                        await req.ReturnResponse("ItemNotPreviewable", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    byte[] thumbnailData = await parent.GeneratePreview(item, maxWidth, maxHeight);
                    if (thumbnailData == null)
                    {
                        await req.ReturnResponse("PreviewGenerationFailed", code: HttpStatusCode.InternalServerError);
                        return;
                    }

                    NameValueCollection previewHeaders = new();
                    previewHeaders.Add("Cache-Control", "public, max-age=3600");
                    await req.ReturnBinaryResponse(thumbnailData, "image/jpeg", HttpStatusCode.OK, previewHeaders);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            // Check if an item is previewable (image or video)
            await parent.CreateAPIRoute("/KliveCloud/IsPreviewable", async (req) =>
            {
                try
                {
                    string itemID = req.userParameters.Get("itemID");
                    var item = parent.GetItemByID(itemID);
                    if (item == null)
                    {
                        await req.ReturnResponse("ItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (!parent.CanAccessItem(item, req.user.KlivesManagementRank))
                    {
                        await req.ReturnResponse("InsufficientPermission", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    var result = new
                    {
                        ItemID = item.ItemID,
                        IsPreviewable = parent.IsPreviewable(item),
                        IsImage = parent.IsImage(item),
                        IsVideo = parent.IsVideo(item),
                        MediaType = parent.IsImage(item) ? "Image" : parent.IsVideo(item) ? "Video" : "None"
                    };

                    string json = JsonConvert.SerializeObject(result);
                    await req.ReturnResponse(json, "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            // Create a share link for a file or folder
            await parent.CreateAPIRoute("/KliveCloud/CreateShareLink", async (req) =>
            {
                try
                {
                    string itemID = req.userParameters.Get("itemID");
                    string expirationHoursStr = req.userParameters.Get("expirationHours");

                    var item = parent.GetItemByID(itemID);
                    if (item == null)
                    {
                        await req.ReturnResponse("ItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (!parent.CanAccessItem(item, req.user.KlivesManagementRank))
                    {
                        await req.ReturnResponse("InsufficientPermission", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    DateTime? expirationDate = null;
                    if (!string.IsNullOrEmpty(expirationHoursStr))
                    {
                        double hours = Convert.ToDouble(expirationHoursStr);
                        expirationDate = DateTime.Now.AddHours(hours);
                    }

                    KliveCloud.SharePermissionMode sharePermissionMode = KliveCloud.SharePermissionMode.ReadOnly;
                    if (item.ItemType == CloudItemType.Folder)
                    {
                        string sharePermissionModeStr = req.userParameters.Get("sharePermissionMode");
                        if (!TryParseSharePermissionMode(sharePermissionModeStr, out sharePermissionMode))
                        {
                            await req.ReturnResponse("InvalidSharePermissionMode", code: HttpStatusCode.BadRequest);
                            return;
                        }
                    }

                    var shareLink = await parent.CreateShareLink(itemID, req.user.UserID, expirationDate, sharePermissionMode);

                    string downloadUrl = $"https://{KliveAPI.KliveAPI.domainName}:{KliveAPI.KliveAPI.apiPORT}/KliveCloud/DownloadShared?code={shareLink.ShareCode}";

                    var result = new
                    {
                        ShareCode = shareLink.ShareCode,
                        ItemID = shareLink.ItemID,
                        FileName = item.Name,
                        ItemType = item.ItemType,
                        DownloadURL = downloadUrl,
                        CreatedDate = shareLink.CreatedDate,
                        ExpirationDate = shareLink.ExpirationDate,
                        SharePermissionMode = shareLink.PermissionMode,
                        CanWrite = parent.CanWriteThroughShareLink(shareLink, item),
                        CanDelete = parent.CanDeleteThroughShareLink(shareLink, item)
                    };

                    string json = JsonConvert.SerializeObject(result);
                    await req.ReturnResponse(json, "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Guest);

            // Delete a share link
            await parent.CreateAPIRoute("/KliveCloud/DeleteShareLink", async (req) =>
            {
                try
                {
                    string shareCode = req.userParameters.Get("code");
                    var link = parent.GetShareLinkByCode(shareCode);
                    if (link == null)
                    {
                        await req.ReturnResponse("ShareLinkNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    if (link.CreatedByUserID != req.user.UserID && req.user.KlivesManagementRank < KMPermissions.Admin)
                    {
                        await req.ReturnResponse("InsufficientPermission", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    await parent.DeleteShareLink(shareCode);
                    await req.ReturnResponse("ShareLinkDeleted", code: HttpStatusCode.OK);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Guest);

            // List all share links created by the current user (Admins and Klives see all)
            await parent.CreateAPIRoute("/KliveCloud/ListShareLinks", async (req) =>
            {
                try
                {
                    List<KliveCloud.ShareLink> links;
                    if (req.user.KlivesManagementRank >= KMPermissions.Admin)
                    {
                        links = parent.ShareLinks.ToList();
                    }
                    else
                    {
                        links = parent.ShareLinks.Where(k => k.CreatedByUserID == req.user.UserID).ToList();
                    }

                    string json = JsonConvert.SerializeObject(links);
                    await req.ReturnResponse(json, "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await parent.CreateAPIRoute("/KliveCloud/CreateSharedFolder", async (req) =>
            {
                try
                {
                    var shareScope = await ResolveShareScope(req, req.userParameters.Get("code"));
                    if (!shareScope.HasValue)
                    {
                        return;
                    }

                    var (link, sharedItem) = shareScope.Value;
                    if (!parent.CanWriteThroughShareLink(link, sharedItem))
                    {
                        await req.ReturnResponse("SharedLinkDoesNotAllowWrite", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    string name = req.userParameters.Get("name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        await req.ReturnResponse("FolderNameRequired", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var targetFolder = await ResolveSharedTargetFolder(req, sharedItem, req.userParameters.Get("parentFolderID"));
                    if (targetFolder == null)
                    {
                        return;
                    }

                    var folder = await parent.CreateFolder(
                        name,
                        targetFolder.ItemID,
                        $"share:{link.ShareCode}",
                        parent.GetEffectiveMinimumPermission(targetFolder));

                    await req.ReturnResponse(JsonConvert.SerializeObject(parent.CloneWithEffectivePermission(folder)), "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Anybody);

            await parent.CreateAPIRoute("/KliveCloud/UploadShared", async (req) =>
            {
                try
                {
                    var shareScope = await ResolveShareScope(req, req.userParameters.Get("code"));
                    if (!shareScope.HasValue)
                    {
                        return;
                    }

                    var (link, sharedItem) = shareScope.Value;
                    if (!parent.CanWriteThroughShareLink(link, sharedItem))
                    {
                        await req.ReturnResponse("SharedLinkDoesNotAllowWrite", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    string fileName = req.userParameters.Get("fileName");
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        await req.ReturnResponse("FileNameRequired", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    byte[] fileData = req.userMessageBytes;
                    if (fileData == null || fileData.Length == 0)
                    {
                        await req.ReturnResponse("EmptyFileBody", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var targetFolder = await ResolveSharedTargetFolder(req, sharedItem, req.userParameters.Get("parentFolderID"));
                    if (targetFolder == null)
                    {
                        return;
                    }

                    var file = await parent.UploadFile(
                        fileName,
                        fileData,
                        targetFolder.ItemID,
                        $"share:{link.ShareCode}",
                        parent.GetEffectiveMinimumPermission(targetFolder));

                    await req.ReturnResponse(JsonConvert.SerializeObject(parent.CloneWithEffectivePermission(file)), "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Anybody);

            await parent.CreateAPIRoute("/KliveCloud/DeleteSharedItem", async (req) =>
            {
                try
                {
                    var shareScope = await ResolveShareScope(req, req.userParameters.Get("code"));
                    if (!shareScope.HasValue)
                    {
                        return;
                    }

                    var (link, sharedItem) = shareScope.Value;
                    if (!parent.CanDeleteThroughShareLink(link, sharedItem))
                    {
                        await req.ReturnResponse("SharedLinkDoesNotAllowDelete", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    var targetItem = await ResolveSharedDeleteTarget(req, sharedItem, req.userParameters.Get("itemID"));
                    if (targetItem == null)
                    {
                        return;
                    }

                    bool success = await parent.DeleteItem(targetItem.ItemID, $"shared link {link.ShareCode}");
                    await req.ReturnResponse(success ? "ItemDeleted" : "DeleteFailed", code: success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Anybody);

            // Download a file via share link (no authentication required)
            await parent.CreateAPIRoute("/KliveCloud/DownloadShared", async (req) =>
            {
                try
                {
                    var shareScope = await ResolveShareScope(req, req.userParameters.Get("code"));
                    if (!shareScope.HasValue)
                    {
                        return;
                    }

                    var (_, sharedItem) = shareScope.Value;
                    var item = await ResolveSharedFileTarget(req, sharedItem, req.userParameters.Get("itemID"));
                    if (item == null)
                    {
                        return;
                    }

                    byte[] fileData = await parent.DownloadFile(item.ItemID);
                    if (fileData == null)
                    {
                        await req.ReturnResponse("FileNotFoundOnDisk", code: HttpStatusCode.NotFound);
                        return;
                    }

                    NameValueCollection sharedHeaders = new();
                    sharedHeaders.Add("Content-Disposition", $"attachment; filename=\"{item.Name}\"");
                    await req.ReturnBinaryResponse(fileData, "application/octet-stream", HttpStatusCode.OK, sharedHeaders);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Anybody);

            // Get file info via share link (no authentication required)
            await parent.CreateAPIRoute("/KliveCloud/GetSharedItemInfo", async (req) =>
            {
                try
                {
                    var shareScope = await ResolveShareScope(req, req.userParameters.Get("code"));
                    if (!shareScope.HasValue)
                    {
                        return;
                    }

                    var (link, item) = shareScope.Value;

                    var effectiveItem = parent.CloneWithEffectivePermission(item);
                    var result = new
                    {
                        effectiveItem.ItemID,
                        effectiveItem.Name,
                        effectiveItem.ItemType,
                        effectiveItem.FileSizeBytes,
                        effectiveItem.CreatedDate,
                        effectiveItem.ModifiedDate,
                        effectiveItem.MinimumPermissionLevel,
                        IsImage = parent.IsImage(item),
                        IsVideo = parent.IsVideo(item),
                        VideoMimeType = parent.IsVideo(item) ? parent.GetVideoMimeType(item) : null,
                        ShareCode = link.ShareCode,
                        SharePermissionMode = link.PermissionMode,
                        CanWrite = parent.CanWriteThroughShareLink(link, item),
                        CanDelete = parent.CanDeleteThroughShareLink(link, item),
                        ExpirationDate = link.ExpirationDate,
                        Children = item.ItemType == CloudItemType.Folder
                            ? parent.GetFolderDescendantsForShare(item.ItemID)
                            : new List<CloudItem>()
                    };

                    string json = JsonConvert.SerializeObject(result);
                    await req.ReturnResponse(json, "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Anybody);

            // Stream a video file with HTTP Range support
            await parent.CreateAPIRoute("/KliveCloud/StreamVideo", async (req) =>
            {
                try
                {
                    string itemID = req.userParameters.Get("itemID");
                    var item = parent.GetItemByID(itemID);
                    if (item == null)
                    {
                        await req.ReturnResponse("ItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (!parent.IsVideo(item))
                    {
                        await req.ReturnResponse("ItemIsNotAVideo", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    if (!parent.CanAccessItem(item, req.user.KlivesManagementRank))
                    {
                        await req.ReturnResponse("InsufficientPermission", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    await StreamVideoFile(req, item);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            await parent.CreateAPIRoute("/KliveCloud/StreamSharedVideo", async (req) =>
            {
                try
                {
                    var shareScope = await ResolveShareScope(req, req.userParameters.Get("code"));
                    if (!shareScope.HasValue)
                    {
                        return;
                    }

                    var (_, sharedItem) = shareScope.Value;
                    var item = await ResolveSharedFileTarget(req, sharedItem, req.userParameters.Get("itemID"));
                    if (item == null)
                    {
                        return;
                    }

                    if (!parent.IsVideo(item))
                    {
                        await req.ReturnResponse("ItemIsNotAVideo", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    await StreamVideoFile(req, item);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Anybody);
        }

        private List<CloudItem> GetDescendants(string folderID, KMPermissions userPerm)
        {
            var result = new List<CloudItem>();
            var children = parent.GetItemsInFolder(folderID, userPerm);
            foreach (var child in children)
            {
                result.Add(child);
                if (child.ItemType == CloudItemType.Folder)
                {
                    result.AddRange(GetDescendants(child.ItemID, userPerm));
                }
            }
            return result;
        }
    }
}
