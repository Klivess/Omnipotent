using Newtonsoft.Json;
using Omnipotent.Profiles;
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

                    var resp = req.context.Response;
                    resp.ContentType = "application/octet-stream";
                    resp.Headers.Add("Content-Disposition", $"attachment; filename=\"{item.Name}\"");
                    resp.Headers.Add("Access-Control-Allow-Origin", "*");
                    resp.ContentLength64 = fileData.Length;
                    resp.StatusCode = (int)HttpStatusCode.OK;
                    using Stream ros = resp.OutputStream;
                    ros.Write(fileData, 0, fileData.Length);
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
