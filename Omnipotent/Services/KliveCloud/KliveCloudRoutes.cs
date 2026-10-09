using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.KliveAPI.Caching;
using System.Collections.Specialized;
using System.Net;
using static Omnipotent.Profiles.KMProfileManager;
using static Omnipotent.Services.KliveCloud.CloudItem;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.KliveCloud
{
    /// <summary>
    /// The /KliveCloud/* HTTP surface. Every authenticated route is gated by a klivecloud.*
    /// permission at the pipeline, and every handler then checks the caller's level on the
    /// specific item (Viewer / Editor) from the item's access list.
    /// </summary>
    public class KliveCloudRoutes
    {
        private KliveCloud parent;

        /// <summary>
        /// How far past its expiry a cached share response may still be served. Share
        /// revocation is exact (it bumps <see cref="KliveCloud.ShareLinksCacheKey"/>);
        /// only unattended lapsing is quantized.
        /// </summary>
        private static readonly TimeSpan ShareExpiryPrecision = TimeSpan.FromSeconds(15);

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

        private async Task StreamVideoFile(UserRequest req, CloudItem item)
        {
            string filePath = parent.GetFullItemPath(item);
            await StreamVideoPath(req, filePath, parent.GetVideoMimeType(item));
        }

        private Task StreamVideoPath(UserRequest req, string filePath, string mimeType)
        {
            return StreamFileToClient(req, filePath, mimeType, null);
        }

        /// <summary>
        /// Builds a Content-Disposition that survives a non-ASCII name. The plain
        /// filename stays for old clients; modern browsers prefer the RFC 5987
        /// filename* form and get the name exactly as stored.
        /// </summary>
        private static string BuildAttachmentDisposition(string fileName)
        {
            string ascii = new string(fileName.Select(c => c < 32 || c > 126 || c == '"' ? '_' : c).ToArray());
            return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
        }

        /// <summary>
        /// Sends a file from disk to the client a buffer at a time, honouring HTTP Range.
        ///
        /// Every download route funnels through here rather than reading the file into a
        /// byte[] first. Buffering meant the client received nothing until the whole file
        /// had been read off disk, the server held a full copy of it in memory (a second
        /// copy again for the response-cache tee), and a file over 2GB could not be sent
        /// at all. Streaming makes time-to-first-byte independent of file size and keeps
        /// server memory flat.
        ///
        /// Advertising Accept-Ranges is what lets a browser or download manager resume a
        /// large transfer instead of restarting it from zero when the connection drops.
        /// </summary>
        private async Task StreamFileToClient(
            UserRequest req, string filePath, string mimeType,
            string? attachmentFileName)
        {
            if (!File.Exists(filePath))
            {
                await req.ReturnResponse("FileNotFoundOnDisk", code: HttpStatusCode.NotFound);
                return;
            }

            var fileInfo = new FileInfo(filePath);
            long fileLength = fileInfo.Length;
            string? rangeHeader = req.req.Headers["Range"];
            bool isHeadRequest = string.Equals(req.req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                string rangeValue = rangeHeader.Substring("bytes=".Length);
                string[] parts = rangeValue.Split('-');

                if (parts.Length != 2)
                {
                    NameValueCollection rangeErrHeaders = new();
                    rangeErrHeaders.Add("Accept-Ranges", "bytes");
                    rangeErrHeaders.Add("Content-Range", $"bytes */{fileLength}");
                    await req.ReturnBinaryResponse(Array.Empty<byte>(), mimeType, (HttpStatusCode)416, rangeErrHeaders);
                    return;
                }

                long start;
                long end;
                if (string.IsNullOrEmpty(parts[0]) && long.TryParse(parts[1], out long suffixLength))
                {
                    suffixLength = Math.Min(suffixLength, fileLength);
                    start = fileLength - suffixLength;
                    end = fileLength - 1;
                }
                else
                {
                    start = long.TryParse(parts[0], out long parsedStart) ? parsedStart : -1;
                    end = !string.IsNullOrEmpty(parts[1]) && long.TryParse(parts[1], out long parsedEnd) ? parsedEnd : fileLength - 1;
                }

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
                if (attachmentFileName != null)
                {
                    rangeHeaders.Add("Content-Disposition", BuildAttachmentDisposition(attachmentFileName));
                }

                using Stream output = req.PrepareStreamResponse(mimeType, contentLength, (HttpStatusCode)206, rangeHeaders);
                if (isHeadRequest)
                {
                    return;
                }

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 262144, FileOptions.Asynchronous | FileOptions.SequentialScan);
                fs.Seek(start, SeekOrigin.Begin);
                byte[] buffer = new byte[262144];
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
                if (attachmentFileName != null)
                {
                    fullHeaders.Add("Content-Disposition", BuildAttachmentDisposition(attachmentFileName));
                }

                using Stream output = req.PrepareStreamResponse(mimeType, fileLength, HttpStatusCode.OK, fullHeaders);
                if (isHeadRequest)
                {
                    return;
                }

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 262144, FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] buffer = new byte[262144];
                int bytesRead;
                while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await output.WriteAsync(buffer, 0, bytesRead);
                }
            }
        }

        private async Task<(KliveCloud.ShareLink Link, CloudItem Root)?> ResolveShareScope(UserRequest req)
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

            if (link.ExpirationDate.HasValue)
            {
                // Expiry is wall-clock: no write happens when a link lapses, so nothing
                // would bump a version and a cached 200 could outlive the link. Anchor
                // the fill to a short clock bucket instead — a cached share response can
                // then survive at most one bucket past its expiry.
                CacheDeps.NoteTimeBucket(ShareExpiryPrecision);

                if (link.ExpirationDate.Value < DateTime.Now)
                {
                    await parent.DeleteShareLink(shareCode);
                    await req.ReturnResponse("ShareLinkExpired", code: HttpStatusCode.Gone);
                    return null;
                }
            }

            var root = parent.GetItemByID(link.ItemID);
            // A link only ever serves what its creator can still see.
            if (root == null || !parent.LinkCanServe(link, root))
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
            if (requestedItem == null || requestedItem.ItemType != CloudItemType.File || !parent.LinkCanServe(link, requestedItem))
            {
                return null;
            }

            return requestedItem;
        }

        // ───────────────────────────── DTOs & checks ─────────────────────────────

        private static string Slash(string? path) => (path ?? "").Replace('\\', '/');

        /// <summary>"Everyone", "3 people", "Only you" — shown beside each item.</summary>
        private string AccessSummary(CloudItem item)
        {
            var access = item.Access ?? new CloudAccess();
            if (access.Everyone != null) return "Everyone";
            int people = access.Entries.Count;
            if (people == 0) return access.Inherit && !string.IsNullOrEmpty(item.ParentFolderID) ? "Inherited" : "Only Klives";
            return people == 1 ? "1 person" : $"{people} people";
        }

        private object ItemDto(CloudItem item, CloudAccessLevel level, KMProfile? viewer, bool allFiles, bool includePeople)
        {
            var path = parent.VisibleAncestry(item, viewer, allFiles);
            var access = item.Access ?? new CloudAccess();
            return new
            {
                item.ItemID,
                item.Name,
                RelativePath = string.Join("/", path.Select(p => p.Name)),
                item.ParentFolderID,
                item.CreatedDate,
                item.ModifiedDate,
                item.CreatedByUserID,
                CreatedByName = item.CreatedByUserID != null && item.CreatedByUserID.StartsWith("shared:", StringComparison.Ordinal)
                    ? "Share link" : parent.GetProfile(item.CreatedByUserID)?.Name,
                ItemType = item.ItemType.ToString(),
                item.FileSizeBytes,
                MyLevel = level.ToString(),
                // Kept for older clients that displayed the retired rank floor.
                MinimumPermissionLevel = AccessSummary(item),
                Access = includePeople ? new
                {
                    Everyone = access.Everyone?.ToString(),
                    access.Inherit,
                    People = access.Entries.Select(e => new
                    {
                        e.ProfileId,
                        Name = parent.GetProfile(e.ProfileId)?.Name ?? "Deleted profile",
                        Level = e.Level.ToString(),
                    }).ToList(),
                    Summary = AccessSummary(item),
                } : null,
            };
        }

        /// <summary>Viewer of the item? (403 with an explanation if not.)</summary>
        private async Task<bool> RequireLevel(UserRequest req, CloudItem item, CloudAccessLevel needed, bool allFiles)
        {
            var level = parent.EffectiveLevel(item, req.user, allFiles);
            if (level >= needed) return true;
            string message = needed == CloudAccessLevel.Editor
                ? $"You can only view “{item.Name}”. Ask an editor to change it."
                : $"“{item.Name}” hasn't been shared with you.";
            await req.ReturnResponse(JsonConvert.SerializeObject(new
            {
                error = "ItemAccessDenied",
                reason = needed == CloudAccessLevel.Editor ? "NotEditor" : "NotShared",
                itemID = item.ItemID,
                myLevel = level.ToString(),
                message,
            }), "application/json", null, HttpStatusCode.Forbidden);
            return false;
        }

        private static Task Json(UserRequest req, object payload, HttpStatusCode code = HttpStatusCode.OK)
            => req.ReturnResponse(JsonConvert.SerializeObject(payload), "application/json", null, code);

        private static Task Error(UserRequest req, string error, HttpStatusCode code, string? message = null)
            => req.ReturnResponse(JsonConvert.SerializeObject(new { error, message = message ?? error }), "application/json", null, code);

        private static readonly NameValueCollection PrivatePreviewHeaders = new() { ["Cache-Control"] = "private, max-age=300" };

        // ───────────────────────────── routes ─────────────────────────────

        public async void CreateRoutes()
        {
            // Folder contents with breadcrumbs, the caller's level and the "Shared with me" count.
            await parent.CreateAPIRoute("/KliveCloud/Browse", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    string? folderID = req.userParameters.Get("folderID");
                    var me = req.user;
                    bool canShare = req.Can(KliveCloudPerms.SharingManage);

                    if (string.Equals(folderID, KliveCloud.SharedWithMeFolderID, StringComparison.OrdinalIgnoreCase))
                    {
                        var shared = parent.GetSharedWithMe(me, all);
                        await Json(req, new
                        {
                            Folder = (object?)null,
                            Virtual = KliveCloud.SharedWithMeFolderID,
                            Path = Array.Empty<object>(),
                            MyLevel = CloudAccessLevel.Viewer.ToString(),
                            Items = shared.Select(s => ItemDto(s.Item, s.Level, me, all, canShare && s.Level == CloudAccessLevel.Editor)).ToList(),
                            SharedWithMeCount = shared.Count,
                        });
                        return;
                    }

                    CloudItem? folder = null;
                    CloudAccessLevel folderLevel = CloudAccessLevel.Editor; // the root: anyone may add their own items
                    if (!string.IsNullOrEmpty(folderID))
                    {
                        folder = parent.GetItemByID(folderID);
                        if (folder == null || folder.ItemType != CloudItemType.Folder)
                        {
                            await Error(req, "FolderNotFound", HttpStatusCode.NotFound, "That folder doesn't exist any more.");
                            return;
                        }
                        if (!await RequireLevel(req, folder, CloudAccessLevel.Viewer, all)) return;
                        folderLevel = parent.EffectiveLevel(folder, me, all);
                    }

                    var children = parent.GetVisibleChildren(folderID, me, all)
                        .OrderByDescending(c => c.Item.ItemType == CloudItemType.Folder)
                        .ThenBy(c => c.Item.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    int sharedCount = string.IsNullOrEmpty(folderID) ? parent.GetSharedWithMe(me, all).Count : 0;
                    await Json(req, new
                    {
                        Folder = folder == null ? null : ItemDto(folder, folderLevel, me, all, canShare && folderLevel == CloudAccessLevel.Editor),
                        Virtual = (string?)null,
                        // Starts at the first folder the caller can see: a ParentFolderID on the first
                        // entry means it sits inside something hidden (reached via "Shared with me").
                        Path = folder == null ? new List<object>() : parent.VisibleAncestry(folder, me, all)
                            .Select(p => (object)new { p.ItemID, p.Name, p.ParentFolderID }).ToList(),
                        MyLevel = folderLevel.ToString(),
                        Items = children.Select(c => ItemDto(c.Item, c.Level, me, all, canShare && c.Level == CloudAccessLevel.Editor)).ToList(),
                        SharedWithMeCount = sharedCount,
                    });
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveCloudPerms.FilesBrowse);

            // List items at root or in a specific folder (kept for older clients; see Browse).
            await parent.CreateAPIRoute("/KliveCloud/ListItems", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    string folderID = req.userParameters.Get("folderID");
                    List<(CloudItem Item, CloudAccessLevel Level)> items;
                    if (string.Equals(folderID, KliveCloud.SharedWithMeFolderID, StringComparison.OrdinalIgnoreCase))
                    {
                        items = parent.GetSharedWithMe(req.user, all);
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(folderID))
                        {
                            var folder = parent.GetItemByID(folderID);
                            if (folder == null || folder.ItemType != CloudItemType.Folder)
                            {
                                await req.ReturnResponse("FolderNotFound", code: HttpStatusCode.NotFound);
                                return;
                            }
                            if (!await RequireLevel(req, folder, CloudAccessLevel.Viewer, all)) return;
                        }
                        items = parent.GetVisibleChildren(folderID, req.user, all);
                    }
                    await Json(req, items.Select(i => ItemDto(i.Item, i.Level, req.user, all, false)).ToList());
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveCloudPerms.FilesBrowse);

            // Every item the caller can see under a folder (or everywhere).
            await parent.CreateAPIRoute("/KliveCloud/GetFolderTree", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    string folderID = req.userParameters.Get("folderID");
                    IEnumerable<CloudItem> scope;
                    CloudItem? folder = null;
                    if (string.IsNullOrEmpty(folderID))
                    {
                        scope = parent.CloudItems;
                    }
                    else
                    {
                        folder = parent.GetItemByID(folderID);
                        if (folder == null || folder.ItemType != CloudItemType.Folder)
                        {
                            await req.ReturnResponse("FolderNotFound", code: HttpStatusCode.NotFound);
                            return;
                        }
                        if (!await RequireLevel(req, folder, CloudAccessLevel.Viewer, all)) return;
                        scope = new[] { folder }.Concat(parent.GetDescendants(folder.ItemID));
                    }
                    var visible = scope
                        .Select(i => (Item: i, Level: parent.EffectiveLevel(i, req.user, all)))
                        .Where(t => t.Level >= CloudAccessLevel.Viewer)
                        .Select(t => ItemDto(t.Item, t.Level, req.user, all, false))
                        .ToList();
                    await Json(req, visible);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveCloudPerms.FilesBrowse);

            // Get info about a specific item
            await parent.CreateAPIRoute("/KliveCloud/GetItemInfo", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    var item = parent.GetItemByID(req.userParameters.Get("itemID"));
                    if (item == null)
                    {
                        await req.ReturnResponse("ItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (!await RequireLevel(req, item, CloudAccessLevel.Viewer, all)) return;
                    var level = parent.EffectiveLevel(item, req.user, all);
                    await Json(req, ItemDto(item, level, req.user, all, req.Can(KliveCloudPerms.SharingManage) && level == CloudAccessLevel.Editor));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveCloudPerms.FilesBrowse);

            // Who has access to an item: its own list, and what it inherits from each folder above.
            await parent.CreateAPIRoute("/KliveCloud/GetItemAccess", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    var item = parent.GetItemByID(req.userParameters.Get("itemID"));
                    if (item == null)
                    {
                        await Error(req, "ItemNotFound", HttpStatusCode.NotFound);
                        return;
                    }
                    if (!await RequireLevel(req, item, CloudAccessLevel.Viewer, all)) return;

                    var own = item.Access ?? new CloudAccess();
                    var inherited = new List<object>();
                    if (own.Inherit)
                    {
                        var node = string.IsNullOrEmpty(item.ParentFolderID) ? null : parent.GetItemByID(item.ParentFolderID);
                        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        while (node != null && visited.Add(node.ItemID))
                        {
                            var a = node.Access ?? new CloudAccess();
                            bool canSee = parent.CanView(node, req.user, all);
                            if (a.Everyone != null)
                                inherited.Add(new { FromItemID = canSee ? node.ItemID : null, FromName = canSee ? node.Name : "a folder above", ProfileId = (string?)null, Name = "Everyone", Level = a.Everyone.ToString() });
                            foreach (var e in a.Entries)
                                inherited.Add(new { FromItemID = canSee ? node.ItemID : null, FromName = canSee ? node.Name : "a folder above", e.ProfileId, Name = parent.GetProfile(e.ProfileId)?.Name ?? "Deleted profile", Level = e.Level.ToString() });
                            if (!a.Inherit || string.IsNullOrEmpty(node.ParentFolderID)) break;
                            node = parent.GetItemByID(node.ParentFolderID);
                        }
                    }
                    await Json(req, new
                    {
                        item.ItemID,
                        item.Name,
                        ItemType = item.ItemType.ToString(),
                        MyLevel = parent.EffectiveLevel(item, req.user, all).ToString(),
                        CanManage = req.Can(KliveCloudPerms.SharingManage) && parent.CanEdit(item, req.user, all),
                        HasParent = !string.IsNullOrEmpty(item.ParentFolderID),
                        own.Inherit,
                        Everyone = own.Everyone?.ToString(),
                        Entries = own.Entries.Select(e => new
                        {
                            e.ProfileId,
                            Name = parent.GetProfile(e.ProfileId)?.Name ?? "Deleted profile",
                            Level = e.Level.ToString(),
                            e.AddedById,
                            AddedByName = e.AddedById == "migration" ? "Rank conversion" : parent.GetProfile(e.AddedById)?.Name,
                            e.AddedUtc,
                        }).ToList(),
                        Inherited = inherited,
                        OwnerAlwaysHasAccess = true,
                    });
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveCloudPerms.FilesBrowse);

            // Replace an item's own access list. Body: {itemID, inherit, everyone: null|"Viewer"|"Editor", entries: [{profileId, level}]}.
            await parent.CreateAPIRoute("/KliveCloud/SetItemAccess", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    JObject body;
                    try { body = JObject.Parse(string.IsNullOrWhiteSpace(req.userMessageContent) ? "{}" : req.userMessageContent); }
                    catch (JsonException) { await Error(req, "InvalidBody", HttpStatusCode.BadRequest, "The request body must be JSON."); return; }

                    var item = parent.GetItemByID(body.Value<string>("itemID"));
                    if (item == null)
                    {
                        await Error(req, "ItemNotFound", HttpStatusCode.NotFound);
                        return;
                    }
                    if (!await RequireLevel(req, item, CloudAccessLevel.Editor, all)) return;

                    var access = new CloudAccess { Inherit = body.Value<bool?>("inherit") ?? true };
                    string? everyone = body.Value<string?>("everyone");
                    if (!string.IsNullOrWhiteSpace(everyone))
                    {
                        if (!Enum.TryParse<CloudAccessLevel>(everyone, true, out var level) || level == CloudAccessLevel.None)
                        {
                            await Error(req, "InvalidLevel", HttpStatusCode.BadRequest, $"'{everyone}' is not Viewer or Editor.");
                            return;
                        }
                        access.Everyone = level;
                    }
                    var existing = (item.Access ?? new CloudAccess()).Entries.ToDictionary(e => e.ProfileId, StringComparer.Ordinal);
                    foreach (var entry in body["entries"] as JArray ?? new JArray())
                    {
                        string? profileId = entry.Value<string>("profileId");
                        string? levelText = entry.Value<string>("level");
                        if (string.IsNullOrWhiteSpace(profileId)) continue;
                        if (parent.GetProfile(profileId) == null)
                        {
                            await Error(req, "UnknownProfile", HttpStatusCode.BadRequest, "One of those people no longer has a profile.");
                            return;
                        }
                        if (!Enum.TryParse<CloudAccessLevel>(levelText ?? "Viewer", true, out var level) || level == CloudAccessLevel.None) level = CloudAccessLevel.Viewer;
                        if (access.Entries.Any(e => e.ProfileId == profileId)) continue;
                        access.Entries.Add(existing.TryGetValue(profileId, out var kept) && kept.Level == level
                            ? kept
                            : new CloudAccessEntry { ProfileId = profileId, Level = level, AddedById = req.user?.UserID, AddedUtc = DateTime.UtcNow });
                    }
                    if (string.IsNullOrEmpty(item.ParentFolderID)) access.Inherit = true;

                    var updated = await parent.SetAccess(item.ItemID, access);
                    await parent.ServiceLog($"{req.user?.Name} changed who can access '{item.Name}' ({AccessSummary(updated!)}).");
                    var myLevel = parent.EffectiveLevel(updated!, req.user, all);
                    await Json(req, ItemDto(updated!, myLevel, req.user, all, true));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveCloudPerms.SharingManage);

            // The people an item can be shared with (for the share dialog).
            await parent.CreateAPIRoute("/KliveCloud/People", async (req) =>
            {
                var people = parent.AllProfiles()
                    .OrderByDescending(p => p.IsOwner).ThenByDescending(p => (int)p.Rank).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new { userId = p.UserID, name = p.Name, rank = p.Rank.ToString(), isOwner = p.IsOwner, isYou = p.UserID == req.user?.UserID })
                    .ToList();
                await Json(req, people);
            }, HttpMethod.Get, KliveCloudPerms.SharingManage);

            // Create a folder (in a folder you edit, or at the root, where it starts private to you).
            await parent.CreateAPIRoute("/KliveCloud/CreateFolder", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    string name = req.userParameters.Get("name") ?? string.Empty;
                    string parentFolderID = req.userParameters.Get("parentFolderID") ?? string.Empty;

                    if (string.IsNullOrEmpty(name))
                    {
                        await req.ReturnResponse("FolderNameRequired", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    if (!string.IsNullOrEmpty(parentFolderID))
                    {
                        var target = parent.GetItemByID(parentFolderID);
                        if (target == null || target.ItemType != CloudItemType.Folder)
                        {
                            await req.ReturnResponse("ParentFolderNotFound", code: HttpStatusCode.NotFound);
                            return;
                        }
                        if (!await RequireLevel(req, target, CloudAccessLevel.Editor, all)) return;
                    }

                    var folder = await parent.CreateFolder(name, parentFolderID, req.user.UserID);
                    await Json(req, ItemDto(folder, parent.EffectiveLevel(folder, req.user, all), req.user, all, false));
                }
                catch (ArgumentException ex)
                {
                    await Error(req, "InvalidName", HttpStatusCode.BadRequest, ex.Message);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveCloudPerms.FilesUpload);

            // Upload a file (file bytes sent as request body).
            // Streaming: the body goes socket -> disk a buffer at a time. As a buffered
            // route the pipeline first had to materialise the whole file as a byte[] and
            // then UTF-8-decode it into a string it would never use, which on a large
            // upload dominated the entire request.
            await parent.CreateStreamingAPIRoute("/KliveCloud/UploadFile", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    string fileName = req.userParameters.Get("fileName") ?? string.Empty;
                    string parentFolderID = req.userParameters.Get("parentFolderID") ?? string.Empty;

                    if (string.IsNullOrEmpty(fileName))
                    {
                        await req.ReturnResponse("FileNameRequired", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    if (!string.IsNullOrEmpty(parentFolderID))
                    {
                        var target = parent.GetItemByID(parentFolderID);
                        if (target == null || target.ItemType != CloudItemType.Folder)
                        {
                            await req.ReturnResponse("ParentFolderNotFound", code: HttpStatusCode.NotFound);
                            return;
                        }
                        if (!await RequireLevel(req, target, CloudAccessLevel.Editor, all)) return;
                    }

                    var file = await parent.UploadFileFromStream(
                        fileName, req.RequestBodyStream, req.req.ContentLength64,
                        parentFolderID, req.user.UserID);
                    await Json(req, ItemDto(file, parent.EffectiveLevel(file, req.user, all), req.user, all, false));
                }
                catch (InvalidDataException)
                {
                    await req.ReturnResponse("EmptyFileBody", code: HttpStatusCode.BadRequest);
                }
                catch (Omnipotent.Services.KliveAPI.KliveAPI.RequestBodyTooLargeException)
                {
                    // Let the pipeline emit its consistent 413 and audit outcome.
                    throw;
                }
                catch (ArgumentException ex)
                {
                    await Error(req, "InvalidName", HttpStatusCode.BadRequest, ex.Message);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveCloudPerms.FilesUpload, KliveCloud.MaxUploadBytes);

            // Download a file
            await parent.CreateAPIRoute("/KliveCloud/DownloadFile", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    var item = parent.GetItemByID(req.userParameters.Get("itemID"));
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
                    if (!await RequireLevel(req, item, CloudAccessLevel.Viewer, all)) return;

                    string filePath = parent.TryGetReadableFilePath(item);
                    if (filePath == null)
                    {
                        await req.ReturnResponse("FileNotFoundOnDisk", code: HttpStatusCode.NotFound);
                        return;
                    }

                    await StreamFileToClient(req, filePath, "application/octet-stream", item.Name);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveCloudPerms.FilesDownload);

            // Delete a file or folder (you must be able to edit everything inside it).
            await parent.CreateAPIRoute("/KliveCloud/DeleteItem", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    string itemID = req.userParameters.Get("itemID");
                    var item = parent.GetItemByID(itemID);
                    if (item == null)
                    {
                        await req.ReturnResponse("ItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (!await RequireLevel(req, item, CloudAccessLevel.Editor, all)) return;
                    var blocker = parent.FirstNonEditableDescendant(item, req.user, all);
                    if (blocker != null)
                    {
                        await Error(req, "ContainsProtectedItems", HttpStatusCode.Forbidden,
                            $"“{item.Name}” contains items you can't edit, so it can't be deleted.");
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
            }, HttpMethod.Post, KliveCloudPerms.FilesDelete);

            // Move a file or folder to a new parent folder
            await parent.CreateAPIRoute("/KliveCloud/MoveItem", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    string itemID = req.userParameters.Get("itemID") ?? string.Empty;
                    string newParentFolderID = req.userParameters.Get("newParentFolderID") ?? string.Empty;

                    if (string.IsNullOrEmpty(itemID))
                    {
                        await req.ReturnResponse("ItemIDRequired", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var item = parent.GetItemByID(itemID);
                    if (item == null)
                    {
                        await req.ReturnResponse("ItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (!await RequireLevel(req, item, CloudAccessLevel.Editor, all)) return;
                    // Moving changes what everything inside inherits, so it needs the same reach as deleting.
                    if (parent.FirstNonEditableDescendant(item, req.user, all) != null)
                    {
                        await Error(req, "ContainsProtectedItems", HttpStatusCode.Forbidden,
                            $"“{item.Name}” contains items you can't edit, so it can't be moved.");
                        return;
                    }

                    if (!string.IsNullOrEmpty(newParentFolderID))
                    {
                        var newParent = parent.GetItemByID(newParentFolderID);
                        if (newParent == null || newParent.ItemType != CloudItemType.Folder)
                        {
                            await req.ReturnResponse("NewParentFolderNotFound", code: HttpStatusCode.NotFound);
                            return;
                        }
                        if (!await RequireLevel(req, newParent, CloudAccessLevel.Editor, all)) return;
                    }

                    bool success = await parent.MoveItem(itemID, newParentFolderID, req.user.UserID);
                    if (success)
                    {
                        await req.ReturnResponse("ItemMoved", code: HttpStatusCode.OK);
                    }
                    else
                    {
                        await req.ReturnResponse("MoveFailed", code: HttpStatusCode.InternalServerError);
                    }
                }
                catch (IOException ex)
                {
                    await Error(req, "NameTaken", HttpStatusCode.Conflict, ex.Message);
                }
                catch (ArgumentException ex)
                {
                    await Error(req, "InvalidMove", HttpStatusCode.BadRequest, ex.Message);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveCloudPerms.FilesOrganize);

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
            }, HttpMethod.Get, KliveCloudPerms.DriveView);

            // Get a preview/thumbnail image for an image or video file
            await parent.CreateAPIRoute("/KliveCloud/GetPreview", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    string widthStr = req.userParameters.Get("maxWidth");
                    string heightStr = req.userParameters.Get("maxHeight");

                    int maxWidth = 300;
                    int maxHeight = 300;
                    if (!string.IsNullOrEmpty(widthStr) && int.TryParse(widthStr, out int w)) maxWidth = Math.Clamp(w, 16, 1920);
                    if (!string.IsNullOrEmpty(heightStr) && int.TryParse(heightStr, out int h)) maxHeight = Math.Clamp(h, 16, 1920);

                    var item = parent.GetItemByID(req.userParameters.Get("itemID"));
                    if (item == null)
                    {
                        await req.ReturnResponse("ItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (!await RequireLevel(req, item, CloudAccessLevel.Viewer, all)) return;
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

                    // Private and short-lived: a revoked viewer must not keep seeing thumbnails for an hour.
                    await req.ReturnBinaryResponse(thumbnailData, "image/jpeg", HttpStatusCode.OK, new NameValueCollection(PrivatePreviewHeaders));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveCloudPerms.FilesBrowse);

            // Check if an item is previewable (image or video)
            await parent.CreateAPIRoute("/KliveCloud/IsPreviewable", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    var item = parent.GetItemByID(req.userParameters.Get("itemID"));
                    if (item == null)
                    {
                        await req.ReturnResponse("ItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (!await RequireLevel(req, item, CloudAccessLevel.Viewer, all)) return;

                    var result = new
                    {
                        ItemID = item.ItemID,
                        IsPreviewable = parent.IsPreviewable(item),
                        IsImage = parent.IsImage(item),
                        IsVideo = parent.IsVideo(item),
                        MediaType = parent.IsImage(item) ? "Image" : parent.IsVideo(item) ? "Video" : "None"
                    };

                    await Json(req, result);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveCloudPerms.FilesBrowse);

            // Create a share link for a file or folder (you must be able to edit everything it exposes).
            await parent.CreateAPIRoute("/KliveCloud/CreateShareLink", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    string itemID = req.userParameters.Get("itemID");
                    string expirationHoursStr = req.userParameters.Get("expirationHours");
                    string permissionModeStr = req.userParameters.Get("permissionMode");

                    var item = parent.GetItemByID(itemID);
                    if (item == null)
                    {
                        await req.ReturnResponse("ItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    if (!await RequireLevel(req, item, CloudAccessLevel.Editor, all)) return;
                    if (parent.FirstNonEditableDescendant(item, req.user, all) != null)
                    {
                        await Error(req, "ContainsProtectedItems", HttpStatusCode.Forbidden,
                            $"“{item.Name}” contains items you can't edit, so you can't share it publicly.");
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
                    if (!string.IsNullOrEmpty(expirationHoursStr) && double.TryParse(expirationHoursStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double hours) && hours > 0)
                    {
                        expirationDate = DateTime.Now.AddHours(hours);
                    }

                    bool isVideoShare = item.ItemType == CloudItemType.File && parent.IsVideo(item);
                    bool embedVideoReady = false;
                    if (isVideoShare)
                    {
                        string? compatibleVideoPath = await parent.GetDiscordCompatibleVideoPath(item);
                        if (string.IsNullOrEmpty(compatibleVideoPath))
                        {
                            await req.ReturnResponse("VideoEmbedGenerationFailed", code: HttpStatusCode.InternalServerError);
                            return;
                        }

                        embedVideoReady = true;
                    }

                    var shareLink = await parent.CreateShareLink(itemID, req.user.UserID, expirationDate, permissionMode, reuseExisting: !isVideoShare);

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
                        EmbedVideoReady = embedVideoReady,
                        CanWrite = parent.CanWriteThroughShareLink(shareLink),
                        CanDelete = parent.CanDeleteThroughShareLink(shareLink)
                    };

                    await Json(req, result);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveCloudPerms.SharingManage);

            // Delete a share link (your own, or anyone's with sharing.manage-any)
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

                    if (link.CreatedByUserID != req.user.UserID && !req.Can(KliveCloudPerms.SharingManageAny))
                    {
                        await Error(req, "NotYourLink", HttpStatusCode.Forbidden, "Only the person who created this link can remove it.");
                        return;
                    }

                    await parent.DeleteShareLink(shareCode);
                    await req.ReturnResponse("ShareLinkDeleted", code: HttpStatusCode.OK);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveCloudPerms.SharingManage);

            await parent.CreateAPIRoute("/KliveCloud/UpdateShareLinkPermission", async (req) =>
            {
                try
                {
                    string shareCode = req.userParameters.Get("code") ?? string.Empty;
                    string permissionModeStr = req.userParameters.Get("permissionMode") ?? string.Empty;
                    var user = req.user;

                    var link = parent.GetShareLinkByCode(shareCode);
                    if (link == null)
                    {
                        await req.ReturnResponse("ShareLinkNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    if (link.CreatedByUserID != user.UserID && !req.Can(KliveCloudPerms.SharingManageAny))
                    {
                        await Error(req, "NotYourLink", HttpStatusCode.Forbidden, "Only the person who created this link can change it.");
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
                    await Json(req, new
                    {
                        ShareCode = link.ShareCode,
                        ItemID = link.ItemID,
                        SharePermissionMode = link.PermissionMode.ToString(),
                        CanWrite = parent.CanWriteThroughShareLink(link),
                        CanDelete = parent.CanDeleteThroughShareLink(link)
                    });
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveCloudPerms.SharingManage);

            // Share links you created (everyone's with sharing.manage-any)
            await parent.CreateAPIRoute("/KliveCloud/ListShareLinks", async (req) =>
            {
                try
                {
                    bool any = req.Can(KliveCloudPerms.SharingManageAny);
                    var links = parent.ShareLinks
                        .Where(k => any || k.CreatedByUserID == req.user.UserID)
                        .Select(k =>
                        {
                            var item = parent.GetItemByID(k.ItemID);
                            return new
                            {
                                k.ShareCode,
                                k.ItemID,
                                k.CreatedByUserID,
                                CreatedByName = parent.GetProfile(k.CreatedByUserID)?.Name,
                                k.CreatedDate,
                                k.ExpirationDate,
                                PermissionMode = k.PermissionMode.ToString(),
                                ItemName = item?.Name,
                                ItemType = item?.ItemType.ToString(),
                                Servable = item != null && parent.LinkCanServe(k, item),
                            };
                        })
                        .ToList();
                    await Json(req, links);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveCloudPerms.SharingManage);

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
                        if (requestedItem == null || requestedItem.ItemType != CloudItemType.File || !parent.LinkCanServe(link, requestedItem))
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

                    string sharedFilePath = parent.TryGetReadableFilePath(item);
                    if (sharedFilePath == null)
                    {
                        await req.ReturnResponse("FileNotFoundOnDisk", code: HttpStatusCode.NotFound);
                        return;
                    }

                    await StreamFileToClient(req, sharedFilePath, "application/octet-stream", item.Name);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, Perms.Public);

            // Get file info via share link (no authentication required)
            await parent.CreateAPIRoute("/KliveCloud/GetSharedItemInfo", async (req) =>
            {
                try
                {
                    var scope = await ResolveShareScope(req);
                    if (scope == null) return;

                    var (link, item) = scope.Value;

                    // Pre-warm Discord-compatible MP4 cache (covers existing share links re-shared into Discord).
                    if (item.ItemType == CloudItemType.File && parent.IsVideo(item))
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await parent.GetDiscordCompatibleVideoPath(item); }
                            catch (Exception ex) { await parent.ServiceLogError(ex, "Failed to pre-warm Discord embed transcode on shared item info"); }
                        });
                    }

                    // Paths are relative to the shared item, so a link never reveals the folders above it.
                    string rootPrefix = Slash(item.RelativePath);
                    string RelativeToShare(CloudItem i)
                    {
                        string p = Slash(i.RelativePath);
                        string parentOfRoot = rootPrefix.Contains('/') ? rootPrefix[..rootPrefix.LastIndexOf('/')] : "";
                        return parentOfRoot.Length > 0 && p.StartsWith(parentOfRoot + "/", StringComparison.OrdinalIgnoreCase) ? p[(parentOfRoot.Length + 1)..] : p;
                    }

                    var descendants = item.ItemType == CloudItemType.Folder
                        ? parent.GetDescendants(item.ItemID).Where(d => parent.LinkCanServe(link, d)).OrderBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase).ToList()
                        : new List<CloudItem>();
                    var result = new
                    {
                        item.ItemID,
                        item.Name,
                        RelativePath = RelativeToShare(item),
                        ParentFolderID = (string?)null,
                        ItemType = item.ItemType.ToString(),
                        item.FileSizeBytes,
                        item.CreatedDate,
                        item.ModifiedDate,
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
                            RelativePath = RelativeToShare(child),
                            child.ParentFolderID,
                            child.CreatedDate,
                            child.ModifiedDate,
                            ItemType = child.ItemType.ToString(),
                            child.FileSizeBytes,
                            IsImage = parent.IsImage(child),
                            IsVideo = parent.IsVideo(child),
                            VideoMimeType = parent.IsVideo(child) ? parent.GetVideoMimeType(child) : null
                        }).ToList()
                    };

                    await Json(req, result);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, Perms.Public);

            // Stream a video file with HTTP Range support
            await parent.CreateAPIRoute("/KliveCloud/StreamVideo", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    var item = parent.GetItemByID(req.userParameters.Get("itemID"));
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
                    if (!await RequireLevel(req, item, CloudAccessLevel.Viewer, all)) return;

                    await StreamVideoFile(req, item);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveCloudPerms.FilesDownload);

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
            }, HttpMethod.Get, Perms.Public);

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
            }, HttpMethod.Get, Perms.Public);

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

                    await req.ReturnBinaryResponse(thumbnailData, "image/jpeg", HttpStatusCode.OK, new NameValueCollection(PrivatePreviewHeaders));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, Perms.Public);

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
                    if (targetFolder == null || targetFolder.ItemType != CloudItemType.Folder || !parent.LinkCanServe(link, targetFolder))
                    {
                        await req.ReturnResponse("SharedFolderNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    var folder = await parent.CreateFolder(name, parentFolderID, $"shared:{link.ShareCode}");
                    await Json(req, new { folder.ItemID, folder.Name, folder.ParentFolderID, ItemType = folder.ItemType.ToString(), folder.CreatedDate });
                }
                catch (ArgumentException ex)
                {
                    await Error(req, "InvalidName", HttpStatusCode.BadRequest, ex.Message);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, Perms.Public);

            // Streaming for the same reason as /KliveCloud/UploadFile above.
            await parent.CreateStreamingAPIRoute("/KliveCloud/UploadShared", async (req) =>
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
                    if (targetFolder == null || targetFolder.ItemType != CloudItemType.Folder || !parent.LinkCanServe(link, targetFolder))
                    {
                        await req.ReturnResponse("SharedFolderNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    var file = await parent.UploadFileFromStream(
                        fileName, req.RequestBodyStream, req.req.ContentLength64,
                        parentFolderID, $"shared:{link.ShareCode}");
                    await Json(req, new { file.ItemID, file.Name, file.ParentFolderID, ItemType = file.ItemType.ToString(), file.FileSizeBytes, file.CreatedDate });
                }
                catch (InvalidDataException)
                {
                    await req.ReturnResponse("EmptyFileBody", code: HttpStatusCode.BadRequest);
                }
                catch (Omnipotent.Services.KliveAPI.KliveAPI.RequestBodyTooLargeException)
                {
                    // Let the pipeline emit its consistent 413 and audit outcome.
                    throw;
                }
                catch (ArgumentException ex)
                {
                    await Error(req, "InvalidName", HttpStatusCode.BadRequest, ex.Message);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, Perms.Public, KliveCloud.MaxUploadBytes);

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
                    if (item == null || string.Equals(item.ItemID, root.ItemID, StringComparison.OrdinalIgnoreCase) || !parent.LinkCanServe(link, item))
                    {
                        await req.ReturnResponse("SharedItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }
                    // Never delete what the link can't see.
                    if (item.ItemType == CloudItemType.Folder && parent.GetDescendants(item.ItemID).Any(d => !parent.LinkCanServe(link, d)))
                    {
                        await req.ReturnResponse("SharedFolderDeleteNotAllowed", code: HttpStatusCode.Forbidden);
                        return;
                    }

                    bool deleted = await parent.DeleteItem(itemID, $"shared:{link.ShareCode}");
                    await req.ReturnResponse(deleted ? "ItemDeleted" : "DeleteFailed", code: deleted ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, Perms.Public);

            await parent.CreateAPIRoute("/KliveCloud/MoveSharedItem", async (req) =>
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

                    string itemID = req.userParameters.Get("itemID") ?? string.Empty;
                    string newParentFolderID = req.userParameters.Get("newParentFolderID") ?? string.Empty;

                    if (string.IsNullOrEmpty(itemID))
                    {
                        await req.ReturnResponse("ItemIDRequired", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var item = parent.GetItemByID(itemID);
                    if (item == null || string.Equals(item.ItemID, root.ItemID, StringComparison.OrdinalIgnoreCase) || !parent.LinkCanServe(link, item))
                    {
                        await req.ReturnResponse("SharedItemNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    if (string.IsNullOrEmpty(newParentFolderID))
                    {
                        await req.ReturnResponse("NewParentFolderRequired", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    var newParent = parent.GetItemByID(newParentFolderID);
                    if (newParent == null || newParent.ItemType != CloudItemType.Folder || !parent.LinkCanServe(link, newParent))
                    {
                        await req.ReturnResponse("NewParentFolderNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    bool success = await parent.MoveItem(itemID, newParentFolderID, $"shared:{link.ShareCode}");
                    if (success)
                    {
                        await req.ReturnResponse("ItemMoved", code: HttpStatusCode.OK);
                    }
                    else
                    {
                        await req.ReturnResponse("MoveFailed", code: HttpStatusCode.InternalServerError);
                    }
                }
                catch (IOException ex)
                {
                    await req.ReturnResponse(ex.Message, code: HttpStatusCode.Conflict);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, Perms.Public);
        }
    }
}
