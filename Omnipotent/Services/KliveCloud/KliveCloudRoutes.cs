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

        public async void CreateRoutes()
        {
            var api = await parent.serviceManager.GetKliveAPIService();

            // List items at root or in a specific folder
            await api.CreateRoute("/KliveCloud/ListItems", async (req) =>
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
                        if (folder.MinimumPermissionLevel > userPerm)
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
            await api.CreateRoute("/KliveCloud/GetFolderTree", async (req) =>
            {
                try
                {
                    string folderID = req.userParameters.Get("folderID");
                    KMPermissions userPerm = req.user.KlivesManagementRank;

                    List<CloudItem> allItems;
                    if (string.IsNullOrEmpty(folderID))
                    {
                        allItems = parent.CloudItems
                            .Where(k => k.MinimumPermissionLevel <= userPerm)
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
                        if (folder.MinimumPermissionLevel > userPerm)
                        {
                            await req.ReturnResponse("InsufficientPermission", code: HttpStatusCode.Forbidden);
                            return;
                        }
                        allItems = GetDescendants(folderID, userPerm);
                        allItems.Insert(0, folder);
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
            await api.CreateRoute("/KliveCloud/GetItemInfo", async (req) =>
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
                    if (item.MinimumPermissionLevel > req.user.KlivesManagementRank)
                    {
                        await req.ReturnResponse("InsufficientPermission", code: HttpStatusCode.Forbidden);
                        return;
                    }
                    string json = JsonConvert.SerializeObject(item);
                    await req.ReturnResponse(json, "application/json");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);

            // Create a folder
            await api.CreateRoute("/KliveCloud/CreateFolder", async (req) =>
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
            await api.CreateRoute("/KliveCloud/UploadFile", async (req) =>
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
            await api.CreateRoute("/KliveCloud/DownloadFile", async (req) =>
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
                    if (item.MinimumPermissionLevel > req.user.KlivesManagementRank)
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
            await api.CreateRoute("/KliveCloud/DeleteItem", async (req) =>
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
                    if (item.MinimumPermissionLevel > req.user.KlivesManagementRank)
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
            await api.CreateRoute("/KliveCloud/ChangeItemPermission", async (req) =>
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

                    if (item.MinimumPermissionLevel > req.user.KlivesManagementRank)
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
            await api.CreateRoute("/KliveCloud/GetDriveInfo", async (req) =>
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
            await api.CreateRoute("/KliveCloud/GetPreview", async (req) =>
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
                    if (item.MinimumPermissionLevel > req.user.KlivesManagementRank)
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
            await api.CreateRoute("/KliveCloud/IsPreviewable", async (req) =>
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
                    if (item.MinimumPermissionLevel > req.user.KlivesManagementRank)
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

            // Create a share link for a file
            await api.CreateRoute("/KliveCloud/CreateShareLink", async (req) =>
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
                    if (item.ItemType != CloudItemType.File)
                    {
                        await req.ReturnResponse("ItemIsNotAFile", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    if (item.MinimumPermissionLevel > req.user.KlivesManagementRank)
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

                    var shareLink = await parent.CreateShareLink(itemID, req.user.UserID, expirationDate);

                    string downloadUrl = $"https://{KliveAPI.KliveAPI.domainName}:{KliveAPI.KliveAPI.apiPORT}/KliveCloud/DownloadShared?code={shareLink.ShareCode}";

                    var result = new
                    {
                        ShareCode = shareLink.ShareCode,
                        ItemID = shareLink.ItemID,
                        FileName = item.Name,
                        DownloadURL = downloadUrl,
                        CreatedDate = shareLink.CreatedDate,
                        ExpirationDate = shareLink.ExpirationDate
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
            await api.CreateRoute("/KliveCloud/DeleteShareLink", async (req) =>
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
            await api.CreateRoute("/KliveCloud/ListShareLinks", async (req) =>
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

            // Download a file via share link (no authentication required)
            await api.CreateRoute("/KliveCloud/DownloadShared", async (req) =>
            {
                try
                {
                    string shareCode = req.userParameters.Get("code");
                    if (string.IsNullOrEmpty(shareCode))
                    {
                        await req.ReturnResponse("ShareCodeRequired", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var link = parent.GetShareLinkByCode(shareCode);
                    if (link == null)
                    {
                        await req.ReturnResponse("ShareLinkNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    if (link.ExpirationDate.HasValue && link.ExpirationDate.Value < DateTime.Now)
                    {
                        await parent.DeleteShareLink(shareCode);
                        await req.ReturnResponse("ShareLinkExpired", code: HttpStatusCode.Gone);
                        return;
                    }

                    var item = parent.GetItemByID(link.ItemID);
                    if (item == null || item.ItemType != CloudItemType.File)
                    {
                        await req.ReturnResponse("FileNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    byte[] fileData = await parent.DownloadFile(link.ItemID);
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
            await api.CreateRoute("/KliveCloud/GetSharedItemInfo", async (req) =>
            {
                try
                {
                    string shareCode = req.userParameters.Get("code");
                    if (string.IsNullOrEmpty(shareCode))
                    {
                        await req.ReturnResponse("ShareCodeRequired", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var link = parent.GetShareLinkByCode(shareCode);
                    if (link == null)
                    {
                        await req.ReturnResponse("ShareLinkNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    if (link.ExpirationDate.HasValue && link.ExpirationDate.Value < DateTime.Now)
                    {
                        await parent.DeleteShareLink(shareCode);
                        await req.ReturnResponse("ShareLinkExpired", code: HttpStatusCode.Gone);
                        return;
                    }

                    var item = parent.GetItemByID(link.ItemID);
                    if (item == null || item.ItemType != CloudItemType.File)
                    {
                        await req.ReturnResponse("FileNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    var result = new
                    {
                        item.ItemID,
                        item.Name,
                        item.ItemType,
                        item.FileSizeBytes,
                        item.CreatedDate,
                        item.ModifiedDate,
                        IsImage = parent.IsImage(item),
                        IsVideo = parent.IsVideo(item),
                        ShareCode = link.ShareCode,
                        ExpirationDate = link.ExpirationDate
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
            await api.CreateRoute("/KliveCloud/StreamVideo", async (req) =>
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
                    if (item.MinimumPermissionLevel > req.user.KlivesManagementRank)
                    {
                        await req.ReturnResponse("InsufficientPermission", code: HttpStatusCode.Forbidden);
                        return;
                    }

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
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Guest);
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
