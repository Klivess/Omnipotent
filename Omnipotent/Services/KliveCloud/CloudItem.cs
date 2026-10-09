using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Omnipotent.Profiles.Permissions.Legacy;

namespace Omnipotent.Services.KliveCloud
{
    public class CloudItem
    {
        public string ItemID;
        public string Name;
        public string RelativePath;
        public string ParentFolderID;
        public DateTime CreatedDate;
        public DateTime ModifiedDate;
        public string CreatedByUserID;

        [JsonConverter(typeof(StringEnumConverter))]
        public CloudItemType ItemType;

        public long FileSizeBytes;

        /// <summary>Who may use this item (see <see cref="CloudAccess"/>).</summary>
        public CloudAccess Access = new();

        /// <summary>
        /// The rank floor items had before access lists. Read once to migrate, then dropped
        /// (null values are not written back).
        /// </summary>
        [JsonProperty("MinimumPermissionLevel", NullValueHandling = NullValueHandling.Ignore)]
        [JsonConverter(typeof(StringEnumConverter))]
        public LegacyRank? LegacyMinimumPermissionLevel;

        public enum CloudItemType
        {
            File,
            Folder
        }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum CloudAccessLevel
    {
        None = 0,
        /// <summary>List, preview, download and stream.</summary>
        Viewer = 1,
        /// <summary>Everything a Viewer can do, plus upload into, rename/move, delete, share and change access.</summary>
        Editor = 2,
    }

    /// <summary>
    /// An item's people list. A folder's list applies to everything inside it (like a shared
    /// drive) unless a child turns <see cref="Inherit"/> off, in which case only the child's own
    /// list counts from there down. The owner always has full access.
    /// </summary>
    public class CloudAccess
    {
        /// <summary>A level every profile gets (null = not shared with everyone).</summary>
        public CloudAccessLevel? Everyone;
        public List<CloudAccessEntry> Entries = new();
        /// <summary>Whether the parent folder's list also applies to this item.</summary>
        public bool Inherit = true;

        public CloudAccess Clone() => new()
        {
            Everyone = Everyone,
            Inherit = Inherit,
            Entries = Entries.Select(e => e.Clone()).ToList(),
        };
    }

    public class CloudAccessEntry
    {
        public string ProfileId = "";
        public CloudAccessLevel Level = CloudAccessLevel.Viewer;
        public string? AddedById;
        public DateTime AddedUtc;

        public CloudAccessEntry Clone() => (CloudAccessEntry)MemberwiseClone();
    }
}
