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

        private static bool TryParseSharePermissionMode(string? rawMode, out KliveCloud.SharePermissionMode mode)
        {
            mode = KliveCloud.SharePermissionMode.ReadOnly;
            if (string.IsNullOrWhiteSpace(rawMode))
            {
                return true;
            }

            string normalized = rawMode.Trim().Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
            if (string.Equals(normalized, "readonly", StringComparison.OrdinalIgnoreCase) || string.Equals(normalized, "read", StringComparison.OrdinalIgnoreCase))
            {
                mode = KliveCloud.SharePermissionMode.ReadOnly;
                return true;
            }

            if (string.Equals(normalized, "write", StringComparison.OrdinalIgnoreCase))
            {
                mode = KliveCloud.SharePermissionMode.Write;
                return true;
            }

            if (string.Equals(normalized, "writedelete", StringComparison.OrdinalIgnoreCase) || string.Equals(normalized, "delete", StringComparison.OrdinalIgnoreCase))
            {
                mode = KliveCloud.SharePermissionMode.WriteDelete;
                return true;
            }

            return Enum.TryParse(rawMode, true, out mode);
        }

        private async Task<(KliveCloud.ShareLink Link, CloudItem Root)?> ResolveShareScope(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest req)
        {
            string? shareCode = req.userParameters.Get("code");
            if (string.IsNullOrEmpty(shareCode))
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

            var root = parent.GetItemByID(link.ItemID);
            if (root == null)
            {
                await req.ReturnResponse("SharedItemNotFound", code: HttpStatusCode.NotFound);
                return null;
            }

            return (link, root);
        }

        private CloudItem? ResolveSharedFileTarget(KliveCloud.ShareLink link, CloudItem root, string? requestedItemID)
        {
            if (root.ItemType == CloudItemType.File)
            {
                return root;
            }

            if (string.IsNullOrWhiteSpace(requestedItemID))
            {
                return null;
            }

            var requestedItem = parent.GetItemByID(requestedItemID);
            if (requestedItem == null || requestedItem.ItemType != CloudItemType.File || !parent.IsItemWithinSharedScope(link, requestedItem))
            {
                return null;
            }

            return requestedItem;
        }

        private async Task StreamVideoFile(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest req, CloudItem item)
        {
            string filePath = parent.GetFullItemPath(item);
            await StreamVideoPath(req, filePath, parent.GetVideoMimeType(item));
        }

        private async Task StreamVideoPath(Omnipotent.Services.KliveAPI.KliveAPI.UserRequest req, string filePath, string mimeType)
        {
            if (!File.Exists(filePath))
            {
                await req.ReturnResponse("FileNotFoundOnDisk", code: HttpStatusCode.NotFound);
                return;
            }

            var fileInfo = new FileInfo(filePath);
            long fileLength = fileInfo.Length;
            string? rangeHeader = req.req.Headers["Range"];

            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                string rangeValue = rangeHeader.Substring("bytes=".Length);
                string[] parts = rangeValue.Split('-');
                long start = long.Parse(parts[0]);
                long end = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? long.Parse(parts[1]) : fileLength - 1;

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
                    string name = req.userParameters.Get("name") ?? string.Empty;
                    string parentFolderID = req.userParameters.Get("parentFolderID") ?? string.Empty;
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
                    string fileName = req.userParameters.Get("fileName") ?? string.Empty;
                    string parentFolderID = req.userParameters.Get("parentFolderID") ?? string.Empty;
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
                    string permissionModeStr = req.userParameters.Get("permissionMode");

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

                    if (!TryParseSharePermissionMode(permissionModeStr, out var permissionMode))
                    {
                        await req.ReturnResponse("InvalidSharePermissionMode", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    if (item.ItemType != CloudItemType.Folder && permissionMode != KliveCloud.SharePermissionMode.ReadOnly)
                    {
                        await req.ReturnResponse("WritableShareLinksRequireFolder", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    DateTime? expirationDate = null;
                    if (!string.IsNullOrEmpty(expirationHoursStr))
                    {
                        double hours = Convert.ToDouble(expirationHoursStr);
                        expirationDate = DateTime.Now.AddHours(hours);
                    }

                    var shareLink = await parent.CreateShareLink(itemID, req.user.UserID, expirationDate, permissionMode);

                    string downloadUrl = $"https://{KliveAPI.KliveAPI.domainName}:{KliveAPI.KliveAPI.apiPORT}/KliveCloud/DownloadShared?code={shareLink.ShareCode}";

                    var result = new
                    {
                        ShareCode = shareLink.ShareCode,
                        ItemID = shareLink.ItemID,
                        FileName = item.Name,
                        ItemType = item.ItemType.ToString(),
                        DownloadURL = downloadUrl,
                        CreatedDate = shareLink.CreatedDate,
                        ExpirationDate = shareLink.ExpirationDate,
                        SharePermissionMode = shareLink.PermissionMode.ToString(),
                        CanWrite = parent.CanWriteThroughShareLink(shareLink),
                        CanDelete = parent.CanDeleteThroughShareLink(shareLink)
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

            await parent.CreateAPIRoute("/KliveCloud/UpdateShareLinkPermission", async (req) =>
            {
                try
                {
                    string shareCode = req.userParameters.Get("code") ?? string.Empty;
                    string permissionModeStr = req.userParameters.Get("permissionMode") ?? string.Empty;
                    var user = req.user;
                    if (user == null)
                    {
                        await req.ReturnResponse("AccessDenied", code: HttpStatusCode.Unauthorized);
                        return;
                    }

                    var link = parent.GetShareLinkByCode(shareCode);
                    if (link == null)
                    {
                        await req.ReturnResponse("ShareLinkNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    if (link.CreatedByUserID != user.UserID && user.KlivesManagementRank < KMPermissions.Admin)
                    {
                        await req.ReturnResponse("InsufficientPermission", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    if (!TryParseSharePermissionMode(permissionModeStr, out var permissionMode))
                    {
                        await req.ReturnResponse("InvalidSharePermissionMode", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var item = parent.GetItemByID(link.ItemID);
                    if (item == null)
                    {
                        await req.ReturnResponse("SharedItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    if (item.ItemType != CloudItemType.Folder && permissionMode != KliveCloud.SharePermissionMode.ReadOnly)
                    {
                        await req.ReturnResponse("WritableShareLinksRequireFolder", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    await parent.UpdateShareLinkPermission(shareCode, permissionMode);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new
                    {
                        ShareCode = link.ShareCode,
                        ItemID = link.ItemID,
                        SharePermissionMode = link.PermissionMode.ToString(),
                        CanWrite = parent.CanWriteThroughShareLink(link),
                        CanDelete = parent.CanDeleteThroughShareLink(link)
                    }), "application/json");
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

            // Download a file via share link (no authentication required)
            await parent.CreateAPIRoute("/KliveCloud/DownloadShared", async (req) =>
            {
                try
                {
                    var scope = await ResolveShareScope(req);
                    if (scope == null) return;

                    var (link, sharedItem) = scope.Value;

                    var item = sharedItem;
                    if (sharedItem.ItemType == CloudItemType.Folder)
                    {
                        string requestedItemID = req.userParameters.Get("itemID");
                        if (string.IsNullOrEmpty(requestedItemID))
                        {
                            await req.ReturnResponse("SharedFolderRequiresItemID", code: HttpStatusCode.BadRequest);
                            return;
                        }

                        var requestedItem = parent.GetItemByID(requestedItemID);
                        if (requestedItem == null || requestedItem.ItemType != CloudItemType.File || !parent.IsItemWithinSharedScope(link, requestedItem))
                        {
                            await req.ReturnResponse("SharedFileNotFound", code: HttpStatusCode.NotFound);
                            return;
                        }

                        item = requestedItem;
                    }
                    else if (item.ItemType != CloudItemType.File)
                    {
                        await req.ReturnResponse("FileNotFound", code: HttpStatusCode.NotFound);
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
                    var scope = await ResolveShareScope(req);
                    if (scope == null) return;

                    var (link, item) = scope.Value;

                    var effectiveItem = parent.CloneWithEffectivePermission(item);
                    var descendants = item.ItemType == CloudItemType.Folder
                        ? parent.GetFolderDescendantsForShare(item.ItemID)
                        : new List<CloudItem>();
                    var result = new
                    {
                        effectiveItem.ItemID,
                        effectiveItem.Name,
                        effectiveItem.RelativePath,
                        effectiveItem.ParentFolderID,
                        effectiveItem.CreatedByUserID,
                        ItemType = effectiveItem.ItemType.ToString(),
                        effectiveItem.FileSizeBytes,
                        effectiveItem.CreatedDate,
                        effectiveItem.ModifiedDate,
                        MinimumPermissionLevel = effectiveItem.MinimumPermissionLevel.ToString(),
                        IsImage = parent.IsImage(item),
                        IsVideo = parent.IsVideo(item),
                        VideoMimeType = parent.IsVideo(item) ? parent.GetVideoMimeType(item) : null,
                        ShareCode = link.ShareCode,
                        SharePermissionMode = link.PermissionMode.ToString(),
                        CanWrite = parent.CanWriteThroughShareLink(link),
                        CanDelete = parent.CanDeleteThroughShareLink(link),
                        ExpirationDate = link.ExpirationDate,
                        Children = descendants.Select(child => new
                        {
                            child.ItemID,
                            child.Name,
                            child.RelativePath,
                            child.ParentFolderID,
                            child.CreatedDate,
                            child.ModifiedDate,
                            child.CreatedByUserID,
                            ItemType = child.ItemType.ToString(),
                            MinimumPermissionLevel = child.MinimumPermissionLevel.ToString(),
                            child.FileSizeBytes,
                            IsImage = parent.IsImage(child),
                            IsVideo = parent.IsVideo(child),
                            VideoMimeType = parent.IsVideo(child) ? parent.GetVideoMimeType(child) : null
                        }).ToList()
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
                    var scope = await ResolveShareScope(req);
                    if (scope == null) return;

                    var (link, root) = scope.Value;
                    var item = ResolveSharedFileTarget(link, root, req.userParameters.Get("itemID"));
                    if (item == null)
                    {
                        await req.ReturnResponse("SharedFileNotFound", code: HttpStatusCode.NotFound);
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

            await parent.CreateAPIRoute("/KliveCloud/StreamSharedVideoEmbed", async (req) =>
            {
                try
                {
                    var scope = await ResolveShareScope(req);
                    if (scope == null) return;

                    var (link, root) = scope.Value;
                    var item = ResolveSharedFileTarget(link, root, req.userParameters.Get("itemID"));
                    if (item == null)
                    {
                        await req.ReturnResponse("SharedFileNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    if (!parent.IsVideo(item))
                    {
                        await req.ReturnResponse("ItemIsNotAVideo", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    string? compatibleVideoPath = await parent.GetDiscordCompatibleVideoPath(item);
                    if (string.IsNullOrEmpty(compatibleVideoPath))
                    {
                        await req.ReturnResponse("VideoEmbedGenerationFailed", code: HttpStatusCode.InternalServerError);
                        return;
                    }

                    await StreamVideoPath(req, compatibleVideoPath, "video/mp4");
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KMPermissions.Anybody);

            await parent.CreateAPIRoute("/KliveCloud/GetSharedPreview", async (req) =>
            {
                try
                {
                    var scope = await ResolveShareScope(req);
                    if (scope == null) return;

                    var (link, root) = scope.Value;
                    var item = ResolveSharedFileTarget(link, root, req.userParameters.Get("itemID"));
                    if (item == null)
                    {
                        await req.ReturnResponse("SharedFileNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    if (!parent.IsPreviewable(item))
                    {
                        await req.ReturnResponse("ItemNotPreviewable", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    int maxWidth = 320;
                    int maxHeight = 320;
                    string? widthStr = req.userParameters.Get("maxWidth");
                    string? heightStr = req.userParameters.Get("maxHeight");
                    if (!string.IsNullOrEmpty(widthStr) && int.TryParse(widthStr, out int parsedWidth))
                        maxWidth = Math.Clamp(parsedWidth, 16, 1920);
                    if (!string.IsNullOrEmpty(heightStr) && int.TryParse(heightStr, out int parsedHeight))
                        maxHeight = Math.Clamp(parsedHeight, 16, 1920);

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
            }, HttpMethod.Get, KMPermissions.Anybody);

            await parent.CreateAPIRoute("/KliveCloud/CreateSharedFolder", async (req) =>
            {
                try
                {
                    var scope = await ResolveShareScope(req);
                    if (scope == null) return;

                    var (link, root) = scope.Value;
                    if (root.ItemType != CloudItemType.Folder || !parent.CanWriteThroughShareLink(link))
                    {
                        await req.ReturnResponse("SharedFolderWriteNotAllowed", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    string name = req.userParameters.Get("name") ?? string.Empty;
                    string parentFolderID = req.userParameters.Get("parentFolderID") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        await req.ReturnResponse("FolderNameRequired", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(parentFolderID))
                    {
                        parentFolderID = root.ItemID;
                    }

                    var targetFolder = parent.GetItemByID(parentFolderID);
                    if (targetFolder == null || targetFolder.ItemType != CloudItemType.Folder || !parent.IsItemWithinSharedScope(link, targetFolder))
                    {
                        await req.ReturnResponse("SharedFolderNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    var folder = await parent.CreateFolder(name, parentFolderID, $"shared:{link.ShareCode}", targetFolder.MinimumPermissionLevel);
                    await req.ReturnResponse(JsonConvert.SerializeObject(folder), "application/json");
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
                    var scope = await ResolveShareScope(req);
                    if (scope == null) return;

                    var (link, root) = scope.Value;
                    if (root.ItemType != CloudItemType.Folder || !parent.CanWriteThroughShareLink(link))
                    {
                        await req.ReturnResponse("SharedFolderWriteNotAllowed", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    string fileName = req.userParameters.Get("fileName") ?? string.Empty;
                    string parentFolderID = req.userParameters.Get("parentFolderID") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        await req.ReturnResponse("FileNameRequired", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(parentFolderID))
                    {
                        parentFolderID = root.ItemID;
                    }

                    var targetFolder = parent.GetItemByID(parentFolderID);
                    if (targetFolder == null || targetFolder.ItemType != CloudItemType.Folder || !parent.IsItemWithinSharedScope(link, targetFolder))
                    {
                        await req.ReturnResponse("SharedFolderNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    if (req.userMessageBytes == null || req.userMessageBytes.Length == 0)
                    {
                        await req.ReturnResponse("EmptyFileBody", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var file = await parent.UploadFile(fileName, req.userMessageBytes, parentFolderID, $"shared:{link.ShareCode}", targetFolder.MinimumPermissionLevel);
                    await req.ReturnResponse(JsonConvert.SerializeObject(file), "application/json");
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
                    var scope = await ResolveShareScope(req);
                    if (scope == null) return;

                    var (link, root) = scope.Value;
                    if (root.ItemType != CloudItemType.Folder || !parent.CanDeleteThroughShareLink(link))
                    {
                        await req.ReturnResponse("SharedFolderDeleteNotAllowed", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    string itemID = req.userParameters.Get("itemID");
                    var item = parent.GetItemByID(itemID);
                    if (item == null || string.Equals(item.ItemID, root.ItemID, StringComparison.OrdinalIgnoreCase) || !parent.IsItemWithinSharedScope(link, item))
                    {
                        await req.ReturnResponse("SharedItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    bool deleted = await parent.DeleteItem(itemID, $"shared:{link.ShareCode}");
                    await req.ReturnResponse(deleted ? "ItemDeleted" : "DeleteFailed", code: deleted ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMPermissions.Anybody);
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
