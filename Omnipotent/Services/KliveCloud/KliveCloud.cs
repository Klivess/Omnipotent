using FFMpegCore;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Profiles;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Profiles.Permissions.Legacy;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI.Caching;
using System.Collections.Concurrent;
using System.Runtime.Serialization;
using static Omnipotent.Profiles.KMProfileManager;
using static Omnipotent.Services.KliveCloud.CloudItem;

namespace Omnipotent.Services.KliveCloud
{
    /// <summary>
    /// Personal cloud storage. What a profile may do here is decided twice for every action: the
    /// route's <c>klivecloud.*</c> permission (may they upload / delete / share at all?) and the
    /// item's access list (are they a Viewer or Editor of <i>this</i> item?).
    ///
    /// <see cref="CloudItems"/> and <see cref="ShareLinks"/> are copy-on-write: every change builds
    /// a new list under <see cref="metadataLock"/>, so request handlers can enumerate them without
    /// locking and never see a half-applied change.
    /// </summary>
    public class KliveCloud : OmniService
    {
        public List<CloudItem> CloudItems = new();
        public List<ShareLink> ShareLinks = new();
        private Dictionary<string, CloudItem> itemsById = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim metadataLock = new(1, 1);
        private KliveCloudRoutes routes;
        private string metadataFilePath;
        private string shareLinksFilePath;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> videoEmbedTranscodeLocks = new();
        private KMProfileManager? profileManager;

        /// <summary>Virtual folder id listing items shared with the caller whose folder they can't see.</summary>
        public const string SharedWithMeFolderID = "shared-with-me";

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
            await MigrateLegacyAccessAsync();

            routes = new KliveCloudRoutes(this);
            routes.CreateRoutes();

            ServiceLog($"KliveCloud service started with {CloudItems.Count} items loaded.");
        }

        private async Task LoadMetadata()
        {
            var items = new List<CloudItem>();
            if (File.Exists(metadataFilePath))
            {
                try
                {
                    string data = await GetDataHandler().ReadDataFromFile(metadataFilePath);
                    items = JsonConvert.DeserializeObject<List<CloudItem>>(data) ?? new List<CloudItem>();
                }
                catch (Exception ex)
                {
                    ServiceLogError(ex, "Failed to load KliveCloud metadata.");
                    items = new List<CloudItem>();
                }
            }
            foreach (var item in items) item.Access ??= new CloudAccess();
            PublishItems(items);
        }

        // Response-cache dataset keys. CloudItems and ShareLinks are held in memory and
        // read directly by route handlers, so the DataUtil file bump behind SaveMetadata /
        // SaveShareLinks is NOT enough on its own: a handler that reads the in-memory list
        // notes no dependency on it, and a cached response would then survive a revocation.
        // These keys make the in-memory reads visible to the dependency tracker.
        internal const string ItemsCacheKey = "klivecloud:items";
        internal const string ShareLinksCacheKey = "klivecloud:sharelinks";

        /// <summary>Swaps in a new item list and rebuilds the id index (call under the metadata lock).</summary>
        private void PublishItems(List<CloudItem> items)
        {
            var index = new Dictionary<string, CloudItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.ItemID)) index[item.ItemID] = item;
            }
            CloudItems = items;
            itemsById = index;
        }

        public async Task SaveMetadata()
        {
            string json = JsonConvert.SerializeObject(CloudItems, Formatting.Indented);
            await GetDataHandler().WriteToFile(metadataFilePath, json);
            CacheDeps.Bump(ItemsCacheKey); // after the write is visible
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
            CacheDeps.Bump(ShareLinksCacheKey); // revocations/permission changes invalidate cached share responses
        }

        // ───────────────────────────── profiles ─────────────────────────────

        /// <summary>Test seam: resolve profiles from this manager instead of the running services.</summary>
        internal void UseProfileManager(KMProfileManager manager) => profileManager = manager;

        /// <summary>Test seam: replaces the item list (as loading does).</summary>
        internal void SetItemsForTests(List<CloudItem> items) => PublishItems(items);

        private KMProfileManager? Profiles()
        {
            if (profileManager != null) return profileManager;
            try { profileManager = GetActiveServices().ToArray().OfType<KMProfileManager>().FirstOrDefault(); }
            catch (InvalidOperationException) { }
            return profileManager;
        }

        public KMProfile? GetProfile(string? userId) => string.IsNullOrEmpty(userId) ? null : Profiles()?.GetProfileByIDFast(userId);

        public IReadOnlyList<KMProfile> AllProfiles() => Profiles()?.Profiles ?? Array.Empty<KMProfile>();

        // ───────────────────────────── access ─────────────────────────────

        /// <summary>
        /// The caller's level on an item: their own entries and "everyone" on the item and, while
        /// <see cref="CloudAccess.Inherit"/> holds, on each folder above it. The owner, and anyone
        /// holding <c>klivecloud.files.all</c> (<paramref name="allFiles"/>), is an Editor everywhere.
        /// </summary>
        public CloudAccessLevel EffectiveLevel(CloudItem item, KMProfile? profile, bool allFiles = false)
        {
            if (item == null || profile == null) return CloudAccessLevel.None;
            if (profile.IsOwner || allFiles) return CloudAccessLevel.Editor;
            var best = CloudAccessLevel.None;
            var node = item;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (node != null && visited.Add(node.ItemID))
            {
                var access = node.Access ?? new CloudAccess();
                if (access.Everyone is CloudAccessLevel everyone && everyone > best) best = everyone;
                foreach (var entry in access.Entries)
                {
                    if (entry.ProfileId == profile.UserID && entry.Level > best) best = entry.Level;
                }
                if (best == CloudAccessLevel.Editor || !access.Inherit || string.IsNullOrEmpty(node.ParentFolderID)) break;
                node = GetItemByID(node.ParentFolderID);
            }
            return best;
        }

        public bool CanView(CloudItem item, KMProfile? profile, bool allFiles = false)
            => EffectiveLevel(item, profile, allFiles) >= CloudAccessLevel.Viewer;

        public bool CanEdit(CloudItem item, KMProfile? profile, bool allFiles = false)
            => EffectiveLevel(item, profile, allFiles) >= CloudAccessLevel.Editor;

        public List<CloudItem> GetDescendants(string folderID)
        {
            CacheDeps.NoteRead(ItemsCacheKey);
            return CloudItems.Where(item => IsDescendantOfFolder(folderID, item)).ToList();
        }

        /// <summary>
        /// Deleting or sharing a folder acts on everything inside it, so the caller must be an
        /// Editor of every descendant. Returns the first item they aren't an editor of.
        /// </summary>
        public CloudItem? FirstNonEditableDescendant(CloudItem folder, KMProfile? profile, bool allFiles)
        {
            if (folder.ItemType != CloudItemType.Folder || profile?.IsOwner == true || allFiles) return null;
            return GetDescendants(folder.ItemID).FirstOrDefault(d => !CanEdit(d, profile, allFiles));
        }

        /// <summary>Items in a folder (or at the root) the caller can see, each with their level.</summary>
        public List<(CloudItem Item, CloudAccessLevel Level)> GetVisibleChildren(string? folderID, KMProfile? profile, bool allFiles)
        {
            CacheDeps.NoteRead(ItemsCacheKey);
            string parent = folderID ?? "";
            return CloudItems
                .Where(k => string.Equals(k.ParentFolderID ?? "", parent, StringComparison.OrdinalIgnoreCase))
                .Select(k => (k, EffectiveLevel(k, profile, allFiles)))
                .Where(t => t.Item2 >= CloudAccessLevel.Viewer)
                .ToList();
        }

        /// <summary>
        /// Items shared with the caller that they can't reach by browsing, because they can't see
        /// the folder above them. Shown in the "Shared with me" virtual folder.
        /// </summary>
        public List<(CloudItem Item, CloudAccessLevel Level)> GetSharedWithMe(KMProfile? profile, bool allFiles)
        {
            CacheDeps.NoteRead(ItemsCacheKey);
            if (profile == null || profile.IsOwner || allFiles) return new();
            var result = new List<(CloudItem, CloudAccessLevel)>();
            foreach (var item in CloudItems)
            {
                if (string.IsNullOrEmpty(item.ParentFolderID)) continue;
                var level = EffectiveLevel(item, profile, allFiles);
                if (level < CloudAccessLevel.Viewer) continue;
                var parent = GetItemByID(item.ParentFolderID);
                if (parent != null && CanView(parent, profile, allFiles)) continue;
                result.Add((item, level));
            }
            return result;
        }

        /// <summary>
        /// Path segments from the highest folder the caller can see down to the item, so a shared
        /// item never reveals the names of folders above it that the caller can't open.
        /// </summary>
        public List<CloudItem> VisibleAncestry(CloudItem item, KMProfile? profile, bool allFiles)
        {
            var chain = new List<CloudItem> { item };
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { item.ItemID };
            var node = item;
            while (!string.IsNullOrEmpty(node.ParentFolderID))
            {
                var parent = GetItemByID(node.ParentFolderID);
                if (parent == null || !visited.Add(parent.ItemID) || !CanView(parent, profile, allFiles)) break;
                chain.Insert(0, parent);
                node = parent;
            }
            return chain;
        }

        public async Task<CloudItem?> SetAccess(string itemID, CloudAccess access)
        {
            await metadataLock.WaitAsync();
            try
            {
                var item = GetItemByID(itemID);
                if (item == null) return null;
                item.Access = access;
                item.ModifiedDate = DateTime.Now;
                await SaveMetadata();
                return item;
            }
            finally { metadataLock.Release(); }
        }

        /// <summary>
        /// One-time conversion of rank floors into access lists, preserving exactly who could see
        /// each item: an item visible to every signed-in profile (Guest floor or lower) becomes
        /// "everyone: Editor"; otherwise the profiles whose rank reached the item's effective floor
        /// become Editors. A child only gets its own list when that set differs from its parent's.
        /// Editor matches the old behaviour, where anyone who could see an item could change it.
        /// </summary>
        private async Task MigrateLegacyAccessAsync()
        {
            if (!CloudItems.Any(i => i.LegacyMinimumPermissionLevel != null)) return;

            var manager = Profiles();
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while ((manager == null || !manager.IsLoaded) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
                manager = Profiles();
            }
            if (manager == null || !manager.IsLoaded)
            {
                await ServiceLogError("KliveCloud: profiles were not available, so item access was not converted. " +
                    "Until it is, only the owner can see KliveCloud items. It will be retried on the next start.");
                return;
            }

            await metadataLock.WaitAsync();
            try
            {
                try
                {
                    string backup = metadataFilePath + ".v1.bak";
                    if (File.Exists(metadataFilePath) && !File.Exists(backup)) File.Copy(metadataFilePath, backup);
                }
                catch { }

                int own = ApplyLegacyAccessMigration(CloudItems, manager.Profiles, GetItemByID, DateTime.UtcNow);
                await SaveMetadata();
                ServiceLog($"KliveCloud: converted {CloudItems.Count} items from rank floors to access lists ({own} with their own list).");
            }
            finally { metadataLock.Release(); }
        }

        /// <summary>
        /// The pure part of the rank-floor migration (tested directly). Returns how many items got
        /// their own list rather than inheriting.
        /// </summary>
        internal static int ApplyLegacyAccessMigration(IReadOnlyList<CloudItem> items, IEnumerable<KMProfile> allProfiles,
            Func<string, CloudItem?> lookup, DateTime now)
        {
            var profiles = allProfiles.Where(p => !p.IsOwner).ToList();
            var effective = new Dictionary<string, LegacyRank>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            LegacyRank Effective(CloudItem item)
            {
                if (effective.TryGetValue(item.ItemID, out var known)) return known;
                var rank = item.LegacyMinimumPermissionLevel ?? LegacyRank.Guest;
                var parent = string.IsNullOrEmpty(item.ParentFolderID) ? null : lookup(item.ParentFolderID);
                if (parent != null && visiting.Add(item.ItemID))
                {
                    var parentRank = Effective(parent);
                    if (parentRank > rank) rank = parentRank;
                }
                effective[item.ItemID] = rank;
                return rank;
            }

            // Before access lists, every KliveCloud route needed at least Guest, so an
            // Anybody/Guest floor meant "every signed-in profile".
            string Signature(LegacyRank floor) => floor <= LegacyRank.Guest
                ? "*"
                : string.Join(",", profiles.Where(p => (int)p.Rank >= (int)floor).Select(p => p.UserID).OrderBy(x => x, StringComparer.Ordinal));

            int own = 0;
            foreach (var item in items)
            {
                var floor = Effective(item);
                var parent = string.IsNullOrEmpty(item.ParentFolderID) ? null : lookup(item.ParentFolderID);
                bool sameAsParent = parent != null && Signature(Effective(parent)) == Signature(floor);
                var access = new CloudAccess { Inherit = true };
                if (!sameAsParent)
                {
                    access.Inherit = parent == null;
                    if (floor <= LegacyRank.Guest)
                    {
                        access.Everyone = CloudAccessLevel.Editor;
                    }
                    else
                    {
                        access.Entries = profiles.Where(p => (int)p.Rank >= (int)floor).Select(p => new CloudAccessEntry
                        {
                            ProfileId = p.UserID,
                            Level = CloudAccessLevel.Editor,
                            AddedById = "migration",
                            AddedUtc = now,
                        }).ToList();
                    }
                    own++;
                }
                item.Access = access;
            }
            foreach (var item in items) item.LegacyMinimumPermissionLevel = null;
            return own;
        }

        // ───────────────────────────── share links ─────────────────────────────

        /// <summary>
        /// Creates a link owned by its creator. A creator's own link for the same item and mode is
        /// reused (with the new expiry); someone else's link is never touched.
        /// </summary>
        public async Task<ShareLink> CreateShareLink(string itemID, string createdByUserID, DateTime? expirationDate, SharePermissionMode permissionMode = SharePermissionMode.ReadOnly, bool reuseExisting = true)
        {
            await metadataLock.WaitAsync();
            try
            {
                DateTime now = DateTime.Now;
                var live = ShareLinks.Where(k => !(k.ExpirationDate.HasValue && k.ExpirationDate.Value < now)).ToList();
                if (reuseExisting)
                {
                    var mine = live.FirstOrDefault(k => k.ItemID == itemID && k.CreatedByUserID == createdByUserID && k.PermissionMode == permissionMode);
                    if (mine != null)
                    {
                        mine.ExpirationDate = expirationDate;
                        ShareLinks = live;
                        await SaveShareLinks();
                        return mine;
                    }
                }

                var link = new ShareLink
                {
                    ShareCode = Guid.NewGuid().ToString("N"),
                    ItemID = itemID,
                    CreatedByUserID = createdByUserID,
                    CreatedDate = now,
                    ExpirationDate = expirationDate,
                    PermissionMode = permissionMode
                };
                ShareLinks = live.Append(link).ToList();
                await SaveShareLinks();
                ServiceLog($"Share link created for item {itemID} by user {createdByUserID}.");
                return link;
            }
            finally { metadataLock.Release(); }
        }

        public async Task<bool> UpdateShareLinkPermission(string shareCode, SharePermissionMode permissionMode)
        {
            await metadataLock.WaitAsync();
            try
            {
                var link = GetShareLinkByCode(shareCode);
                if (link == null) return false;

                link.PermissionMode = permissionMode;
                link.LegacySharePermissionMode = null;
                await SaveShareLinks();
                ServiceLog($"Share link {shareCode} permission updated to {permissionMode}.");
                return true;
            }
            finally { metadataLock.Release(); }
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

        /// <summary>
        /// A share link never serves more than its creator can still see: an item inside a shared
        /// folder that the creator has no access to (or lost access to) is invisible through the link.
        /// Links written through share links ("shared:…") act for the link that created them.
        /// </summary>
        public bool LinkCanServe(ShareLink link, CloudItem item)
        {
            if (!IsItemWithinSharedScope(link, item)) return false;
            if (link.CreatedByUserID != null && link.CreatedByUserID.StartsWith("shared:", StringComparison.Ordinal)) return true;
            var creator = GetProfile(link.CreatedByUserID);
            if (creator == null) return false;
            bool allFiles = AccessEvaluator.Can(creator, KliveCloudPerms.FilesAll);
            return CanView(item, creator, allFiles);
        }

        public ShareLink GetShareLinkByCode(string shareCode)
        {
            CacheDeps.NoteRead(ShareLinksCacheKey);
            return ShareLinks.FirstOrDefault(k => k.ShareCode == shareCode);
        }

        public async Task<bool> DeleteShareLink(string shareCode)
        {
            await metadataLock.WaitAsync();
            try
            {
                var link = GetShareLinkByCode(shareCode);
                if (link == null) return false;
                ShareLinks = ShareLinks.Where(k => k.ShareCode != shareCode).ToList();
                await SaveShareLinks();
                return true;
            }
            finally { metadataLock.Release(); }
        }

        // ───────────────────────────── storage ─────────────────────────────

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
            CacheDeps.NoteRead(ItemsCacheKey);
            if (string.IsNullOrEmpty(itemID)) return null;
            if (itemsById.TryGetValue(itemID, out var hit)) return hit;
            // Callers that populate CloudItems directly (tests) bypass the index.
            return CloudItems.FirstOrDefault(k => k.ItemID == itemID);
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

        /// <summary>
        /// A name no sibling (in metadata or on disk) already uses: "report.pdf" becomes
        /// "report (1).pdf". Two items must never share a disk path, or deleting one destroys the other.
        /// </summary>
        private string UniqueSiblingName(string desired, string? parentFolderID, CloudItemType type, string parentRelativePath)
        {
            string parent = parentFolderID ?? "";
            var taken = new HashSet<string>(CloudItems
                .Where(k => string.Equals(k.ParentFolderID ?? "", parent, StringComparison.OrdinalIgnoreCase))
                .Select(k => k.Name), StringComparer.OrdinalIgnoreCase);
            string basePath = GetStorageBasePath();
            bool ExistsOnDisk(string candidate)
            {
                string full = Path.GetFullPath(Path.Combine(basePath, parentRelativePath, candidate));
                return File.Exists(full) || Directory.Exists(full);
            }
            if (!taken.Contains(desired) && !ExistsOnDisk(desired)) return desired;

            string stem = type == CloudItemType.File ? Path.GetFileNameWithoutExtension(desired) : desired;
            string ext = type == CloudItemType.File ? Path.GetExtension(desired) : "";
            for (int i = 1; i < 10_000; i++)
            {
                string candidate = $"{stem} ({i}){ext}";
                if (!taken.Contains(candidate) && !ExistsOnDisk(candidate)) return candidate;
            }
            throw new IOException("Could not find a free name for this item.");
        }

        private string ParentRelativePath(string? parentFolderID)
        {
            if (string.IsNullOrEmpty(parentFolderID)) return "";
            var parentFolder = GetItemByID(parentFolderID);
            if (parentFolder == null || parentFolder.ItemType != CloudItemType.Folder)
                throw new Exception("Parent folder not found.");
            return parentFolder.RelativePath;
        }

        /// <summary>New items inherit their folder's access; the creator also becomes an Editor.</summary>
        private static CloudAccess NewItemAccess(string createdByUserID)
        {
            var access = new CloudAccess { Inherit = true };
            if (!string.IsNullOrEmpty(createdByUserID) && !createdByUserID.StartsWith("shared:", StringComparison.Ordinal))
            {
                access.Entries.Add(new CloudAccessEntry
                {
                    ProfileId = createdByUserID,
                    Level = CloudAccessLevel.Editor,
                    AddedById = createdByUserID,
                    AddedUtc = DateTime.UtcNow,
                });
            }
            return access;
        }

        public async Task<CloudItem> CreateFolder(string name, string parentFolderID, string createdByUserID)
        {
            ValidateItemName(name);
            await metadataLock.WaitAsync();
            try
            {
                string parentPath = ParentRelativePath(parentFolderID);
                name = UniqueSiblingName(name, parentFolderID, CloudItemType.Folder, parentPath);
                string relativePath = string.IsNullOrEmpty(parentPath) ? name : Path.Combine(parentPath, name);

                string basePath = GetStorageBasePath();
                string fullPath = Path.GetFullPath(Path.Combine(basePath, relativePath));
                if (!fullPath.StartsWith(basePath + Path.DirectorySeparatorChar))
                    throw new UnauthorizedAccessException("Access denied: path is outside the cloud storage directory.");
                Directory.CreateDirectory(fullPath);

                CloudItem folder = new CloudItem
                {
                    ItemID = NewItemId(),
                    Name = name,
                    RelativePath = relativePath,
                    ParentFolderID = parentFolderID ?? "",
                    CreatedDate = DateTime.Now,
                    ModifiedDate = DateTime.Now,
                    CreatedByUserID = createdByUserID,
                    ItemType = CloudItemType.Folder,
                    Access = NewItemAccess(createdByUserID),
                    FileSizeBytes = 0
                };

                PublishItems(CloudItems.Append(folder).ToList());
                await SaveMetadata();
                ServiceLog($"Folder '{name}' created by user {createdByUserID}.");
                return folder;
            }
            finally { metadataLock.Release(); }
        }

        private string NewItemId()
        {
            string id;
            do { id = RandomGeneration.GenerateRandomLengthOfNumbers(12); } while (itemsById.ContainsKey(id));
            return id;
        }

        /// <summary>
        /// Chunk size for streaming a payload to or from disk. Large enough that a
        /// multi-gigabyte transfer is not paced by syscall overhead, small enough that
        /// the buffers stay off the large object heap's worst behaviour.
        /// </summary>
        internal const int TransferBufferBytes = 1024 * 1024;

        /// <summary>
        /// Ceiling on a single uploaded file. Effectively unbounded for a personal
        /// cloud -- the real limit is free disk space -- but the API pipeline needs a
        /// declared number to reject an absurd Content-Length before reading anything.
        /// </summary>
        public const long MaxUploadBytes = 64L * 1024 * 1024 * 1024;

        /// <summary>
        /// Copies an upload straight from the request socket to disk, one buffer at a
        /// time, and only then records it. Holds one buffer regardless of file size and
        /// overlaps network with disk. Writes land on a sibling temp file that is moved
        /// into place at the end, so an aborted or failed upload never leaves a
        /// half-written file visible under the item's name. A name already used in the
        /// folder gets a " (1)" suffix rather than overwriting the other item's bytes.
        /// </summary>
        public async Task<CloudItem> UploadFileFromStream(
            string fileName, Stream source, long declaredLength, string parentFolderID,
            string createdByUserID, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            ValidateItemName(fileName);

            // Reserve the name under the lock, write outside it (uploads can take minutes),
            // then register under the lock again.
            string relativePath, fullPath;
            await metadataLock.WaitAsync(cancellationToken);
            try
            {
                string parentPath = ParentRelativePath(parentFolderID);
                fileName = UniqueSiblingName(fileName, parentFolderID, CloudItemType.File, parentPath);
                relativePath = string.IsNullOrEmpty(parentPath) ? fileName : Path.Combine(parentPath, fileName);
                string basePath = GetStorageBasePath();
                fullPath = Path.GetFullPath(Path.Combine(basePath, relativePath));
                if (!fullPath.StartsWith(basePath + Path.DirectorySeparatorChar))
                    throw new UnauthorizedAccessException("Access denied: path is outside the cloud storage directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                // Placeholder so a concurrent upload of the same name picks a different one.
                using (File.Create(fullPath)) { }
            }
            finally { metadataLock.Release(); }

            long written;
            try
            {
                written = await StreamToFileAtomically(fullPath, source, declaredLength, cancellationToken);
            }
            catch
            {
                try { if (File.Exists(fullPath) && new FileInfo(fullPath).Length == 0) File.Delete(fullPath); } catch { }
                throw;
            }
            DataUtil.NoteExternalFileWrite(fullPath);

            await metadataLock.WaitAsync(CancellationToken.None);
            try
            {
                CloudItem file = new CloudItem
                {
                    ItemID = NewItemId(),
                    Name = fileName,
                    RelativePath = relativePath,
                    ParentFolderID = parentFolderID ?? "",
                    CreatedDate = DateTime.Now,
                    ModifiedDate = DateTime.Now,
                    CreatedByUserID = createdByUserID,
                    ItemType = CloudItemType.File,
                    Access = NewItemAccess(createdByUserID),
                    FileSizeBytes = written
                };
                PublishItems(CloudItems.Append(file).ToList());
                await SaveMetadata();
                ServiceLog($"File '{fileName}' ({written} bytes) uploaded by user {createdByUserID}.");
                return file;
            }
            finally { metadataLock.Release(); }
        }

        /// <summary>
        /// Copies <paramref name="source"/> onto a sibling temp file one buffer at a time
        /// and moves it into place, returning the byte count written. Nothing appears at
        /// <paramref name="destinationPath"/> unless the whole payload arrived, so an
        /// aborted transfer leaves the previous file (or no file) rather than a truncated one.
        ///
        /// <paramref name="declaredLength"/> is the sender's Content-Length, or a negative
        /// value when the length is unknown (a chunked upload). When it is known it both
        /// preallocates the file and is enforced as an exact byte count on completion.
        /// </summary>
        internal static async Task<long> StreamToFileAtomically(
            string destinationPath, Stream source, long declaredLength,
            CancellationToken cancellationToken = default)
        {
            string directory = Path.GetDirectoryName(destinationPath);
            string tempPath = Path.Combine(
                directory, "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".uploading");

            long written = 0;
            try
            {
                await using (var destination = new FileStream(
                    tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    // Preallocating stops NTFS extending (and fragmenting) the file on
                    // every buffer when the client told us how big the payload is.
                    if (declaredLength > TransferBufferBytes)
                    {
                        try { destination.SetLength(declaredLength); } catch { }
                    }

                    byte[] buffer = new byte[TransferBufferBytes];
                    while (true)
                    {
                        int read = await source.ReadAsync(buffer, cancellationToken);
                        if (read == 0) break;
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        written += read;
                    }

                    // A short upload must not leave the preallocated tail behind as
                    // trailing zero bytes.
                    if (destination.Length != written) destination.SetLength(written);
                }

                if (written == 0)
                {
                    throw new InvalidDataException("Upload contained no data.");
                }

                // A client that vanishes mid-transfer can end the body stream early. The
                // truncated bytes must never be promoted into place as a complete file,
                // so when a length was declared it has to match exactly.
                if (declaredLength >= 0 && written != declaredLength)
                {
                    throw new EndOfStreamException(
                        $"Upload ended early: received {written} of {declaredLength} declared bytes.");
                }

                File.Move(tempPath, destinationPath, overwrite: true);
                return written;
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }

        public async Task<bool> DeleteItem(string itemID, KMProfile user)
        {
            return await DeleteItemInternal(itemID, user?.Name ?? user?.UserID ?? "Unknown user");
        }

        public async Task<bool> DeleteItem(string itemID, string deletedByLabel)
        {
            return await DeleteItemInternal(itemID, deletedByLabel);
        }

        /// <summary>
        /// Deletes an item and everything under it, together with its thumbnails, video embeds
        /// and share links. Callers check the caller may edit every descendant first.
        /// </summary>
        private async Task<bool> DeleteItemInternal(string itemID, string deletedByLabel)
        {
            await metadataLock.WaitAsync();
            CloudItem? item;
            List<CloudItem> removed;
            try
            {
                item = GetItemByID(itemID);
                if (item == null) return false;

                removed = new List<CloudItem> { item };
                if (item.ItemType == CloudItemType.Folder) removed.AddRange(CloudItems.Where(k => IsDescendantOfFolder(itemID, k)));

                string fullPath = GetFullItemPath(item);
                if (item.ItemType == CloudItemType.Folder)
                {
                    if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
                    DataUtil.NoteExternalFileWrite(fullPath);
                }
                else if (File.Exists(fullPath))
                {
                    await GetDataHandler().DeleteFile(fullPath);
                }

                var removedIds = new HashSet<string>(removed.Select(r => r.ItemID), StringComparer.OrdinalIgnoreCase);
                PublishItems(CloudItems.Where(k => !removedIds.Contains(k.ItemID)).ToList());
                await SaveMetadata();

                if (ShareLinks.Any(l => removedIds.Contains(l.ItemID)))
                {
                    ShareLinks = ShareLinks.Where(l => !removedIds.Contains(l.ItemID)).ToList();
                    await SaveShareLinks();
                }
            }
            finally { metadataLock.Release(); }

            foreach (var gone in removed) DeleteDerivedFiles(gone.ItemID);
            ServiceLog($"Item '{item.Name}' deleted by {deletedByLabel} ({removed.Count} item(s)).");
            return true;
        }

        /// <summary>Thumbnails and Discord embeds are derived from an item and go with it.</summary>
        private void DeleteDerivedFiles(string itemID)
        {
            try
            {
                string thumbnailsDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveCloudThumbnailsDirectory);
                if (Directory.Exists(thumbnailsDir))
                {
                    foreach (var thumb in Directory.EnumerateFiles(thumbnailsDir, itemID + "_*.jpg")) File.Delete(thumb);
                }
                string embed = GetVideoEmbedCachePath(itemID);
                if (File.Exists(embed)) File.Delete(embed);
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, $"Could not clean up previews for deleted item {itemID}.");
            }
        }

        public async Task<bool> MoveItem(string itemID, string newParentFolderID, string userID)
        {
            await metadataLock.WaitAsync();
            try
            {
                var item = GetItemByID(itemID);
                if (item == null) return false;

                newParentFolderID = newParentFolderID ?? "";

                // Cannot move an item into itself
                if (string.Equals(itemID, newParentFolderID, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Cannot move a folder into itself.");

                // Cannot move a folder into one of its descendants
                if (item.ItemType == CloudItemType.Folder && !string.IsNullOrEmpty(newParentFolderID))
                {
                    var targetFolder = GetItemByID(newParentFolderID);
                    if (targetFolder != null && IsDescendantOfFolder(itemID, targetFolder))
                    {
                        throw new ArgumentException("Cannot move a folder into one of its descendants.");
                    }
                }

                if (CloudItems.Any(k => k.ItemID != itemID
                    && string.Equals(k.ParentFolderID ?? "", newParentFolderID, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(k.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new IOException($"An item called '{item.Name}' already exists in the destination.");
                }

                string oldRelativePath = item.RelativePath;
                string newRelativePath;
                if (string.IsNullOrEmpty(newParentFolderID))
                {
                    newRelativePath = item.Name;
                }
                else
                {
                    var newParent = GetItemByID(newParentFolderID);
                    if (newParent == null || newParent.ItemType != CloudItemType.Folder)
                        throw new Exception("New parent folder not found.");
                    newRelativePath = Path.Combine(newParent.RelativePath, item.Name);
                }

                string basePath = GetStorageBasePath();
                string oldFullPath = Path.GetFullPath(Path.Combine(basePath, oldRelativePath));
                string newFullPath = Path.GetFullPath(Path.Combine(basePath, newRelativePath));

                if (!oldFullPath.StartsWith(basePath + Path.DirectorySeparatorChar) && oldFullPath != basePath)
                    throw new UnauthorizedAccessException("Access denied: path is outside the cloud storage directory.");
                if (!newFullPath.StartsWith(basePath + Path.DirectorySeparatorChar) && newFullPath != basePath)
                    throw new UnauthorizedAccessException("Access denied: path is outside the cloud storage directory.");

                // Check if the destination path already exists
                if (item.ItemType == CloudItemType.Folder)
                {
                    if (Directory.Exists(newFullPath) && !string.Equals(oldFullPath, newFullPath, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("A folder with this name already exists in the destination.");
                }
                else
                {
                    if (File.Exists(newFullPath) && !string.Equals(oldFullPath, newFullPath, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("A file with this name already exists in the destination.");
                }

                // Perform physical move on disk
                if (item.ItemType == CloudItemType.Folder)
                {
                    if (Directory.Exists(oldFullPath) && !string.Equals(oldFullPath, newFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.Move(oldFullPath, newFullPath);
                    }
                }
                else
                {
                    if (File.Exists(oldFullPath) && !string.Equals(oldFullPath, newFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        string destDir = Path.GetDirectoryName(newFullPath);
                        if (!Directory.Exists(destDir))
                            Directory.CreateDirectory(destDir);
                        File.Move(oldFullPath, newFullPath);
                    }
                }

                // Descendants are collected before the item's own parent changes.
                var descendants = item.ItemType == CloudItemType.Folder
                    ? CloudItems.Where(k => IsDescendantOfFolder(itemID, k)).ToList()
                    : new List<CloudItem>();

                // Update item metadata
                item.ParentFolderID = newParentFolderID;
                item.RelativePath = newRelativePath;
                item.ModifiedDate = DateTime.Now;

                // If it is a folder, update all descendant relative paths
                foreach (var descendant in descendants)
                {
                    // Find the subpath from the old folder root
                    string relativeSubPath = descendant.RelativePath.Substring(oldRelativePath.Length);
                    if (relativeSubPath.StartsWith(Path.DirectorySeparatorChar.ToString()) || relativeSubPath.StartsWith("/"))
                    {
                        relativeSubPath = relativeSubPath.Substring(1);
                    }
                    descendant.RelativePath = Path.Combine(newRelativePath, relativeSubPath);
                }

                PublishItems(CloudItems.ToList());
                await SaveMetadata();
                ServiceLog($"Item '{item.Name}' moved to parent '{newParentFolderID}' by user {userID}.");
                return true;
            }
            finally { metadataLock.Release(); }
        }

        /// <summary>
        /// Resolves a file item to a path on disk without reading it. Download routes
        /// stream from this path; nothing ever buffers a whole file in memory.
        /// </summary>
        public string TryGetReadableFilePath(CloudItem item)
        {
            if (item == null || item.ItemType != CloudItemType.File) return null;
            string fullPath = GetFullItemPath(item);
            return File.Exists(fullPath) ? fullPath : null;
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
