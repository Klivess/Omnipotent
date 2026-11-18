using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.KliveAPI;
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

        private Task<SendResult> StreamVideoFile(UserRequest req, CloudItem item)
        {
            string filePath = parent.GetFullItemPath(item);
            return StreamFileToClient(req, filePath, parent.GetVideoMimeType(item), null);
        }

        private static string BuildAttachmentDisposition(string fileName) => KliveCloudTransfer.Disposition(fileName, inline: false);

        /// <summary>What sending a file came to: whether the body started, how much of it left, and why it stopped.</summary>
        internal readonly record struct SendResult(bool Started, bool Partial, long BytesSent, KliveCloudTransfer.PumpOutcome Outcome);

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
        /// Accept-Ranges alone doesn't let a browser resume: it also needs a validator to
        /// send back in If-Range, so every file goes out with an ETag and Last-Modified.
        /// Before they were added, an interrupted 241 MB download started again from zero
        /// five times in a row (8 Nov 2026).
        ///
        /// Once the body has started, nothing is thrown or written as an error any more. A
        /// client that hangs up (the usual way a download ends early) used to reach the
        /// route's catch, which wrote a 500 into the half-sent response: telemetry counted a
        /// cancelled download as a server failure and the log took a stack trace for it.
        /// </summary>
        private async Task<SendResult> StreamFileToClient(
            UserRequest req, string filePath, string mimeType,
            string? fileName, bool inline = false, NameValueCollection? extraHeaders = null)
        {
            if (!File.Exists(filePath))
            {
                await req.ReturnResponse("FileNotFoundOnDisk", code: HttpStatusCode.NotFound);
                return default;
            }

            var fileInfo = new FileInfo(filePath);
            long fileLength = fileInfo.Length;
            DateTime lastWriteUtc = fileInfo.LastWriteTimeUtc;
            string etag = KliveCloudTransfer.StrongETag(fileLength, lastWriteUtc);
            bool isHeadRequest = string.Equals(req.req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);
            var plan = KliveCloudTransfer.PlanRange(fileLength, req.req.Headers["Range"], req.req.Headers["If-Range"], etag, lastWriteUtc);

            if (plan.Kind == KliveCloudTransfer.RangeKind.Unsatisfiable)
            {
                NameValueCollection rangeErrHeaders = new();
                rangeErrHeaders.Add("Accept-Ranges", "bytes");
                rangeErrHeaders.Add("Content-Range", $"bytes */{fileLength}");
                await req.ReturnBinaryResponse(Array.Empty<byte>(), mimeType, (HttpStatusCode)416, rangeErrHeaders);
                return default;
            }

            bool partial = plan.Kind == KliveCloudTransfer.RangeKind.Partial;
            long start = partial ? plan.Start : 0;
            long contentLength = partial ? plan.Length : fileLength;

            NameValueCollection headers = new();
            headers.Add("Accept-Ranges", "bytes");
            headers.Add("ETag", etag);
            headers.Add("Last-Modified", KliveCloudTransfer.HttpDate(lastWriteUtc));
            if (partial) headers.Add("Content-Range", $"bytes {plan.Start}-{plan.End}/{fileLength}");
            if (fileName != null) headers.Add("Content-Disposition", KliveCloudTransfer.Disposition(fileName, inline));
            if (extraHeaders != null) headers.Add(extraHeaders);

            // Open the file before the response starts, so one that can't be read still gets a real error.
            FileStream fs;
            try
            {
                fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, KliveCloudTransfer.BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Error(req, "FileUnreadable", HttpStatusCode.ServiceUnavailable, "That file can't be read right now. Try again in a moment.");
                return default;
            }

            await using (fs)
            {
                if (start > 0) fs.Seek(start, SeekOrigin.Begin);
                Stream output = req.PrepareStreamResponse(mimeType, contentLength, partial ? (HttpStatusCode)206 : HttpStatusCode.OK, headers);
                var pumped = isHeadRequest
                    ? new KliveCloudTransfer.PumpResult(0, KliveCloudTransfer.PumpOutcome.Completed, null)
                    : await KliveCloudTransfer.PumpAsync(fs, output, contentLength);

                bool gone = pumped.Outcome == KliveCloudTransfer.PumpOutcome.ClientGone;
                try
                {
                    output.Dispose();
                }
                catch (Exception ex) when (KliveCloudTransfer.IsClientGone(ex))
                {
                    gone = true;
                }
                catch (InvalidOperationException)
                {
                    // Closing a body that ended short of its Content-Length; the connection is dropped either way.
                    gone = true;
                }

                if (gone && req.Trace != null) req.Trace.ClientDisconnected = true;
                if (pumped.Outcome == KliveCloudTransfer.PumpOutcome.SourceFailed && pumped.Error != null)
                {
                    await parent.ServiceLogError(pumped.Error, $"Reading '{Path.GetFileName(filePath)}' failed after {pumped.BytesSent:N0} of {contentLength:N0} bytes had been sent");
                }
                return new SendResult(true, partial, pumped.BytesSent, gone ? KliveCloudTransfer.PumpOutcome.ClientGone : pumped.Outcome);
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

        /// <summary>
        /// A folder (or a selection inside it) as one ZIP for a signed-in profile: the share route's
        /// archive, scoped to what that profile can see rather than to a link's creator.
        /// </summary>
        private async Task SendFolderZip(UserRequest req, KMProfile profile, CloudItem folder, string? itemIDs)
        {
            var scope = new KliveCloud.ShareLink { ShareCode = "", ItemID = folder.ItemID, CreatedByUserID = profile.UserID };
            List<CloudItem>? selection = null;
            if (!string.IsNullOrWhiteSpace(itemIDs))
            {
                selection = new List<CloudItem>();
                foreach (string id in itemIDs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var picked = parent.GetItemByID(id);
                    if (picked == null || !parent.IsDescendantOfFolder(folder.ItemID, picked) || !parent.LinkCanServe(scope, picked))
                    {
                        await Error(req, "ItemNotFound", HttpStatusCode.NotFound, "One of those items isn't in that folder any more.");
                        return;
                    }
                    selection.Add(picked);
                }
            }

            var plan = parent.PlanShareZip(scope, folder, selection);
            string archiveName = selection is { Count: 1 } && selection[0].ItemType == CloudItemType.Folder ? selection[0].Name : folder.Name;
            var headers = new NameValueCollection
            {
                { "Content-Disposition", BuildAttachmentDisposition(archiveName + ".zip") },
                { "Cache-Control", "no-store" },
                { "X-Content-Type-Options", "nosniff" },
            };
            using Stream output = req.PrepareStreamResponse("application/zip", -1, HttpStatusCode.OK, headers);
            if (string.Equals(req.req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                await KliveCloud.WriteZipAsync(output, plan);
            }
            catch (Exception ex) when (KliveCloudTransfer.IsClientGone(ex))
            {
                if (req.Trace != null) req.Trace.ClientDisconnected = true;
            }
            catch (Exception ex)
            {
                await parent.ServiceLogError(ex, "KliveCloud folder ZIP failed partway");
            }
        }

        private static readonly long ProcessStartTicks = Environment.TickCount64 ^ DateTime.UtcNow.Ticks;

        private static bool IsYes(string? value) => value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

        private static string ShortCode(string code) => code.Length > 6 ? code[..6] + "…" : code;

        private static readonly Dictionary<KliveCloud.ShareLinkProblem, string> ProblemText = new()
        {
            [KliveCloud.ShareLinkProblem.Expired] = "Expired: it stops working on the next visit.",
            [KliveCloud.ShareLinkProblem.ItemDeleted] = "What it shared was deleted.",
            [KliveCloud.ShareLinkProblem.NoOwner] = "Nobody owns it, and a link only serves what its owner can see.",
            [KliveCloud.ShareLinkProblem.OwnerLostAccess] = "Its owner can no longer see what it shares.",
        };

        /// <summary>One row of the link list: the link, what it shares, whether it opens and how much it is used.</summary>
        private object ShareLinkDto(KliveCloud.ShareLink k, DateTime now)
        {
            var item = parent.GetItemByID(k.ItemID);
            var problem = parent.DiagnoseShareLink(k, now);
            var usage = parent.ShareStats.Get(k.ShareCode);
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
                ItemPath = item == null ? null : Slash(item.RelativePath),
                ItemBytes = item?.ItemType == CloudItemType.File ? item.FileSizeBytes : (long?)null,
                Servable = problem == KliveCloud.ShareLinkProblem.None,
                Problem = problem == KliveCloud.ShareLinkProblem.None ? null : problem.ToString(),
                ProblemText = ProblemText.TryGetValue(problem, out var text) ? text : null,
                Usage = new
                {
                    Opens = usage?.Opens ?? 0,
                    Downloads = usage?.Downloads ?? 0,
                    BytesServed = usage?.BytesServed ?? 0,
                    LastOpenedUtc = usage?.LastOpenedUtc,
                    LastDownloadUtc = usage?.LastDownloadUtc,
                    CountingSinceUtc = parent.ShareStats.CountingSinceUtc,
                },
            };
        }

        /// <summary>Who is visiting a share, for counting an open once per visitor: hashed in memory, never stored.</summary>
        private static string Visitor(UserRequest req)
            => Omnipotent.Services.OmniDefence.OmniDefence.ExtractClientIp(req.req) + "|" + (req.req.UserAgent ?? "");

        /// <summary>A download of a share starting from its first byte (a resume or a seek isn't another download).</summary>
        private static bool IsFreshDownload(UserRequest req)
        {
            if (string.Equals(req.req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase)) return false;
            string? range = req.req.Headers["Range"];
            return string.IsNullOrEmpty(range) || range.StartsWith("bytes=0-", StringComparison.OrdinalIgnoreCase);
        }

        private static Task Json(UserRequest req, object payload, HttpStatusCode code = HttpStatusCode.OK)
            => req.ReturnResponse(JsonConvert.SerializeObject(payload), "application/json", null, code);

        private static Task Error(UserRequest req, string error, HttpStatusCode code, string? message = null)
            => req.ReturnResponse(JsonConvert.SerializeObject(new { error, message = message ?? error }), "application/json", null, code);

        /// <summary>Private and short-lived: a revoked viewer must not keep seeing thumbnails for an hour.</summary>
        private const string PreviewCacheControl = "private, max-age=300";

        /// <summary>
        /// A thumbnail with a validator. Before it had one, a page shown again after five minutes
        /// downloaded every thumbnail again, and the server read (or remade) each one; now an
        /// unchanged one is a body-less 304. X-KliveCloud-Preview says whether it came from memory,
        /// disk or ffmpeg, and the trace has the same as spans (preview-memory/disk/queue/make).
        /// </summary>
        private async Task SendPreview(UserRequest req, CloudItem item, int maxWidth, int maxHeight)
        {
            // Weak: a remade JPEG looks the same but isn't byte-identical. The bytes behind an id never change.
            string etag = $"W/\"p-{item.ItemID}-{maxWidth}x{maxHeight}-{item.FileSizeBytes:x}\"";
            if (HttpResponseHelpers.ETagMatches(req.req.Headers["If-None-Match"], etag))
            {
                // The loader only runs where a 304 can't be sent (inside /batch).
                await req.ReturnValidatedBinaryResponse(etag, async () => (await parent.GetPreviewAsync(item, maxWidth, maxHeight)).Data ?? Array.Empty<byte>(),
                    "image/jpeg", PreviewCacheControl);
                return;
            }

            var preview = await parent.GetPreviewAsync(item, maxWidth, maxHeight);
            if (preview.Data == null)
            {
                // Only a thumbnail that could not be made is the server's fault. A file that was moved or
                // deleted under the page, or one with no picture to show, is not: those were 500s before.
                switch (preview.Source)
                {
                    case KliveCloud.PreviewSource.Missing:
                        await Error(req, "SourceMissing", HttpStatusCode.NotFound, "The file is no longer on disk (moved or deleted).");
                        return;
                    case KliveCloud.PreviewSource.Unsupported:
                        await Error(req, "NoPreview", HttpStatusCode.UnsupportedMediaType, "Only images and videos have thumbnails.");
                        return;
                    default:
                        await req.ReturnResponse("PreviewGenerationFailed", code: HttpStatusCode.InternalServerError);
                        return;
                }
            }
            var headers = new NameValueCollection { { "X-KliveCloud-Preview", preview.Source.ToString().ToLowerInvariant() } };
            await req.ReturnValidatedBinaryResponse(etag, () => Task.FromResult(preview.Data), "image/jpeg", PreviewCacheControl, headers);
        }

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

            // A few bytes that change whenever anything a KliveCloud page shows changes (items, access,
            // links, link usage), so an open page can ask "anything new?" instead of re-reading the
            // whole tree. The process start is part of it: the counters restart with the process.
            await parent.CreateAPIRoute("/KliveCloud/Version", async (req) =>
            {
                CacheDeps.MarkUncacheable("version probe");
                long items = CacheDeps.CurrentVersion(KliveCloud.ItemsCacheKey);
                long links = CacheDeps.CurrentVersion(KliveCloud.ShareLinksCacheKey);
                long usage = CacheDeps.CurrentVersion(KliveCloudShareStats.CacheKey);
                await Json(req, new
                {
                    Stamp = $"{ProcessStartTicks:x}-{items:x}-{links:x}-{usage:x}",
                    Items = $"{ProcessStartTicks:x}-{items:x}",
                    Links = $"{ProcessStartTicks:x}-{links:x}-{usage:x}",
                });
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

                    await SendPreview(req, item, maxWidth, maxHeight);
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

                    if (string.IsNullOrEmpty(req.user?.UserID))
                    {
                        await Error(req, "NoOwner", HttpStatusCode.BadRequest, "A share link must belong to a profile, or it can never open.");
                        return;
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
                    DateTime now = DateTime.Now;
                    var links = parent.ShareLinks
                        // A link with no creator is nobody's: only someone who manages every link sees it.
                        .Where(k => any || (!string.IsNullOrEmpty(k.CreatedByUserID) && k.CreatedByUserID == req.user.UserID))
                        .Select(k => ShareLinkDto(k, now))
                        .ToList();
                    await Json(req, links);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, KliveCloudPerms.SharingManage);

            // Change a link in place (same address): permissionMode, expirationHours ("0" or "never" = no expiry),
            // adopt=1 (serve your access instead of its creator's, for a link whose creator is gone or lost access).
            await parent.CreateAPIRoute("/KliveCloud/UpdateShareLink", async (req) =>
            {
                try
                {
                    bool any = req.Can(KliveCloudPerms.SharingManageAny);
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    string shareCode = req.userParameters.Get("code") ?? string.Empty;
                    var link = parent.GetShareLinkByCode(shareCode);
                    if (link == null)
                    {
                        await Error(req, "ShareLinkNotFound", HttpStatusCode.NotFound, "That link no longer exists.");
                        return;
                    }

                    bool adopt = IsYes(req.userParameters.Get("adopt"));
                    bool mine = !string.IsNullOrEmpty(link.CreatedByUserID) && link.CreatedByUserID == req.user.UserID;
                    if (!mine && !any)
                    {
                        await Error(req, "NotYourLink", HttpStatusCode.Forbidden, "Only the person who created this link can change it.");
                        return;
                    }

                    var item = parent.GetItemByID(link.ItemID);
                    if (item == null)
                    {
                        await Error(req, "SharedItemNotFound", HttpStatusCode.Conflict, "The item this link shared was deleted. Delete the link instead.");
                        return;
                    }

                    if (adopt)
                    {
                        // The link will serve what the adopter can see, so it needs the reach creating it would.
                        if (!await RequireLevel(req, item, CloudAccessLevel.Editor, all)) return;
                        if (parent.FirstNonEditableDescendant(item, req.user, all) != null)
                        {
                            await Error(req, "ContainsProtectedItems", HttpStatusCode.Forbidden,
                                $"“{item.Name}” contains items you can't edit, so you can't take this link over.");
                            return;
                        }
                    }

                    KliveCloud.SharePermissionMode? mode = null;
                    string? modeText = req.userParameters.Get("permissionMode");
                    if (!string.IsNullOrWhiteSpace(modeText))
                    {
                        if (!TryParseSharePermissionMode(modeText, out var parsed))
                        {
                            await Error(req, "InvalidSharePermissionMode", HttpStatusCode.BadRequest, $"'{modeText}' is not ReadOnly, Write or WriteDelete.");
                            return;
                        }
                        if (item.ItemType != CloudItemType.Folder && parsed != KliveCloud.SharePermissionMode.ReadOnly)
                        {
                            await Error(req, "WritableShareLinksRequireFolder", HttpStatusCode.BadRequest, "Only a folder's link can let visitors add or delete files.");
                            return;
                        }
                        mode = parsed;
                    }

                    DateTime? expiry = null;
                    bool clearExpiry = false;
                    string? hoursText = req.userParameters.Get("expirationHours");
                    if (!string.IsNullOrWhiteSpace(hoursText))
                    {
                        if (string.Equals(hoursText, "never", StringComparison.OrdinalIgnoreCase)) clearExpiry = true;
                        else if (double.TryParse(hoursText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double hours) && hours >= 0 && hours <= 24 * 3650)
                        {
                            if (hours == 0) clearExpiry = true;
                            else expiry = DateTime.Now.AddHours(hours);
                        }
                        else
                        {
                            await Error(req, "InvalidExpiry", HttpStatusCode.BadRequest, "expirationHours must be a number of hours, 0 or never.");
                            return;
                        }
                    }

                    var updated = await parent.UpdateShareLink(shareCode, mode, expiry, clearExpiry, adopt ? req.user.UserID : null);
                    if (updated == null)
                    {
                        await Error(req, "ShareLinkNotFound", HttpStatusCode.NotFound, "That link no longer exists.");
                        return;
                    }
                    if (adopt) await parent.ServiceLog($"{req.user?.Name} took over the share link for '{item.Name}' ({ShortCode(shareCode)}).");
                    await Json(req, ShareLinkDto(updated, DateTime.Now));
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveCloudPerms.SharingManage);

            // Rename a file or folder where it is.
            await parent.CreateAPIRoute("/KliveCloud/RenameItem", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    var item = parent.GetItemByID(req.userParameters.Get("itemID"));
                    if (item == null)
                    {
                        await Error(req, "ItemNotFound", HttpStatusCode.NotFound, "That item doesn't exist any more.");
                        return;
                    }
                    if (!await RequireLevel(req, item, CloudAccessLevel.Editor, all)) return;

                    var renamed = await parent.RenameItem(item.ItemID, req.userParameters.Get("name") ?? "", req.user?.Name ?? req.user?.UserID ?? "someone");
                    if (renamed == null)
                    {
                        await Error(req, "ItemNotFound", HttpStatusCode.NotFound, "That item doesn't exist any more.");
                        return;
                    }
                    var level = parent.EffectiveLevel(renamed, req.user, all);
                    await Json(req, ItemDto(renamed, level, req.user, all, req.Can(KliveCloudPerms.SharingManage) && level == CloudAccessLevel.Editor));
                }
                catch (IOException ex)
                {
                    await Error(req, "NameTaken", HttpStatusCode.Conflict, ex.Message);
                }
                catch (ArgumentException ex)
                {
                    await Error(req, "InvalidName", HttpStatusCode.BadRequest, ex.Message);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveCloudPerms.FilesOrganize);

            // A short-lived key for opening files without an Authorization header (<img>, <video>, a
            // download link). itemID, or itemIDs (comma-separated, up to 50). A folder's ticket opens as a ZIP.
            await parent.CreateAPIRoute("/KliveCloud/Ticket", async (req) =>
            {
                try
                {
                    bool all = req.Can(KliveCloudPerms.FilesAll);
                    string? many = req.userParameters.Get("itemIDs");
                    var ids = (string.IsNullOrWhiteSpace(many) ? new[] { req.userParameters.Get("itemID") ?? "" } : many.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (ids.Count == 0 || ids.Count > 50)
                    {
                        await Error(req, "ItemIDsRequired", HttpStatusCode.BadRequest, "Give one to fifty file ids.");
                        return;
                    }
                    if (string.IsNullOrEmpty(req.user?.UserID))
                    {
                        await Error(req, "NoProfile", HttpStatusCode.Forbidden, "Tickets are issued to a profile.");
                        return;
                    }

                    DateTime now = DateTime.UtcNow;
                    var issued = new List<object>();
                    var refused = new List<string>();
                    foreach (string id in ids)
                    {
                        var item = parent.GetItemByID(id);
                        if (item == null || parent.EffectiveLevel(item, req.user, all) < CloudAccessLevel.Viewer)
                        {
                            refused.Add(id);
                            continue;
                        }
                        var ticket = parent.Tickets.Issue(item.ItemID, req.user.UserID, now);
                        issued.Add(new
                        {
                            item.ItemID,
                            Ticket = ticket.Token,
                            Path = $"/KliveCloud/Open?t={ticket.Token}",
                            Inline = parent.InlineMimeType(item) != null,
                            ExpiresUtc = ticket.ExpiresUtc,
                        });
                    }
                    if (issued.Count == 0)
                    {
                        await Error(req, "ItemNotFound", HttpStatusCode.NotFound, ids.Count == 1 ? "That file doesn't exist or hasn't been shared with you." : "None of those files can be opened by you.");
                        return;
                    }
                    await Json(req, new { Tickets = issued, Refused = refused, LifetimeSeconds = (int)KliveCloudTickets.Lifetime.TotalSeconds });
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KliveCloudPerms.FilesDownload);

            // A file through a ticket. download=1 always saves it; otherwise images, video, audio, PDF and
            // text open inline (text as plain text) and anything else downloads. as=mp4 plays a video a
            // browser can't (MKV, AVI...) through the same MP4 a share link's embed uses. A folder's ticket
            // sends a ZIP of what the profile can see in it (itemIDs narrows it to a selection).
            await parent.CreateAPIRoute("/KliveCloud/Open", async (req) =>
            {
                try
                {
                    // The ticket is checked on every use: a cached answer would outlive it.
                    CacheDeps.MarkUncacheable("ticket");
                    var ticket = parent.Tickets.Resolve(req.userParameters.Get("t"), DateTime.UtcNow);
                    var profile = ticket == null ? null : parent.GetProfile(ticket.ProfileId);
                    var item = ticket == null ? null : parent.GetItemByID(ticket.ItemId);
                    // Access is checked again on every use: a ticket outlives nothing it was issued against.
                    bool allowed = profile != null && item != null
                        && AccessEvaluator.Can(profile, KliveCloudPerms.FilesDownload)
                        && parent.EffectiveLevel(item, profile, AccessEvaluator.Can(profile, KliveCloudPerms.FilesAll)) >= CloudAccessLevel.Viewer;
                    if (!allowed)
                    {
                        await Error(req, "TicketExpired", HttpStatusCode.NotFound, "This file link has expired. Open the file again from KliveCloud.");
                        return;
                    }

                    if (item!.ItemType == CloudItemType.Folder)
                    {
                        await SendFolderZip(req, profile!, item, req.userParameters.Get("itemIDs"));
                        return;
                    }

                    // as=jpg: the thumbnail (or, with w/h up to 1920, a JPEG of an image a browser can't show).
                    if (string.Equals(req.userParameters.Get("as"), "jpg", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!parent.IsPreviewable(item))
                        {
                            await Error(req, "ItemNotPreviewable", HttpStatusCode.BadRequest, "There is no picture of this file.");
                            return;
                        }
                        int width = int.TryParse(req.userParameters.Get("w"), out int w) ? Math.Clamp(w, 16, 1920) : 320;
                        int height = int.TryParse(req.userParameters.Get("h"), out int h) ? Math.Clamp(h, 16, 1920) : 320;
                        await SendPreview(req, item, width, height);
                        return;
                    }

                    string? path = parent.TryGetReadableFilePath(item);
                    if (path == null)
                    {
                        await Error(req, "FileNotFoundOnDisk", HttpStatusCode.NotFound, "The file is missing from storage.");
                        return;
                    }

                    string? inlineType = parent.InlineMimeType(item);
                    bool download = IsYes(req.userParameters.Get("download")) || inlineType == null;
                    var safety = new NameValueCollection
                    {
                        { "X-Content-Type-Options", "nosniff" },
                        { "Cache-Control", "private, max-age=600" },
                        { "Referrer-Policy", "no-referrer" },
                    };
                    // Whatever an inline file contains can't script the API's origin (PDF keeps its viewer).
                    if (!download && inlineType != "application/pdf") safety.Add("Content-Security-Policy", "sandbox; default-src 'none'; img-src 'self' data:; media-src 'self'; style-src 'unsafe-inline'");

                    if (!download && parent.IsVideo(item) && string.Equals(req.userParameters.Get("as"), "mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        string? playable = await parent.GetDiscordCompatibleVideoPath(item);
                        if (string.IsNullOrEmpty(playable))
                        {
                            await Error(req, "VideoConversionFailed", HttpStatusCode.InternalServerError, "This video couldn't be converted to play in a browser. Download it instead.");
                            return;
                        }
                        await StreamFileToClient(req, playable, "video/mp4", Path.ChangeExtension(item.Name, ".mp4"), inline: true, extraHeaders: safety);
                        return;
                    }

                    await StreamFileToClient(req, path, download ? "application/octet-stream" : inlineType!, item.Name, inline: !download, extraHeaders: safety);
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, Perms.Public);

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

                    bool fresh = IsFreshDownload(req);
                    var sent = await StreamFileToClient(req, sharedFilePath, "application/octet-stream", item.Name);
                    if (sent.Started)
                    {
                        if (fresh) parent.ShareStats.NoteDownload(link.ShareCode, DateTime.UtcNow);
                        parent.ShareStats.NoteBytes(link.ShareCode, sent.BytesSent);
                    }
                }
                catch (Exception ex)
                {
                    await req.ReturnResponse(new ErrorInformation(ex).FullFormattedMessage, code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Get, Perms.Public);

            // A shared folder (or a selection inside it) as one ZIP, built while it streams.
            // folderID defaults to the shared folder; itemIDs (comma-separated) narrows it to a selection.
            await parent.CreateAPIRoute("/KliveCloud/DownloadSharedZip", async (req) =>
            {
                bool streaming = false;
                try
                {
                    var scope = await ResolveShareScope(req);
                    if (scope == null) return;

                    var (link, root) = scope.Value;
                    if (root.ItemType != CloudItemType.Folder)
                    {
                        await req.ReturnResponse("SharedZipRequiresFolder", code: HttpStatusCode.BadRequest);
                        return;
                    }

                    string? folderID = req.userParameters.Get("folderID");
                    var folder = string.IsNullOrWhiteSpace(folderID) ? root : parent.GetItemByID(folderID);
                    if (folder == null || folder.ItemType != CloudItemType.Folder || !parent.LinkCanServe(link, folder))
                    {
                        await req.ReturnResponse("SharedFolderNotFound", code: HttpStatusCode.NotFound);
                        return;
                    }

                    List<CloudItem>? selection = null;
                    string? itemIDs = req.userParameters.Get("itemIDs");
                    if (!string.IsNullOrWhiteSpace(itemIDs))
                    {
                        selection = new List<CloudItem>();
                        foreach (string id in itemIDs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            var item = parent.GetItemByID(id);
                            if (item == null || !parent.LinkCanServe(link, item) || !parent.IsDescendantOfFolder(folder.ItemID, item))
                            {
                                await req.ReturnResponse("SharedItemNotFound", code: HttpStatusCode.NotFound);
                                return;
                            }
                            selection.Add(item);
                        }
                    }

                    var plan = parent.PlanShareZip(link, folder, selection);
                    string archiveName = selection is { Count: 1 } && selection[0].ItemType == CloudItemType.Folder ? selection[0].Name : folder.Name;
                    var headers = new NameValueCollection
                    {
                        { "Content-Disposition", BuildAttachmentDisposition(archiveName + ".zip") },
                        { "Cache-Control", "no-store" },
                    };

                    streaming = true;
                    using Stream output = req.PrepareStreamResponse("application/zip", -1, HttpStatusCode.OK, headers);
                    if (string.Equals(req.req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase)) return;
                    parent.ShareStats.NoteDownload(link.ShareCode, DateTime.UtcNow);
                    var counted = new KliveCloudTransfer.CountingStream(output);
                    try
                    {
                        await KliveCloud.WriteZipAsync(counted, plan);
                    }
                    finally
                    {
                        parent.ShareStats.NoteBytes(link.ShareCode, counted.BytesWritten);
                    }
                }
                catch (Exception ex) when (streaming)
                {
                    // Headers are gone; all that's left is to stop. Usually the client cancelled.
                    if (KliveCloudTransfer.IsClientGone(ex))
                    {
                        if (req.Trace != null) req.Trace.ClientDisconnected = true;
                    }
                    else
                    {
                        await parent.ServiceLogError(ex, "Shared ZIP download failed partway");
                    }
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
                    parent.ShareStats.NoteOpen(link.ShareCode, Visitor(req), DateTime.UtcNow);

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
                        // Tells the site this server has /DownloadSharedZip.
                        CanDownloadZip = item.ItemType == CloudItemType.Folder,
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

                    var sent = await StreamVideoFile(req, item);
                    parent.ShareStats.NoteBytes(link.ShareCode, sent.BytesSent);
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

                    var sent = await StreamFileToClient(req, compatibleVideoPath, "video/mp4", null);
                    parent.ShareStats.NoteBytes(link.ShareCode, sent.BytesSent);
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

                    await SendPreview(req, item, maxWidth, maxHeight);
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
