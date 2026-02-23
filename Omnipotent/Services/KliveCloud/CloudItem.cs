using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using static Omnipotent.Profiles.KMProfileManager;

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

        [JsonConverter(typeof(StringEnumConverter))]
        public KMPermissions MinimumPermissionLevel;

        public long FileSizeBytes;

        public enum CloudItemType
        {
            File,
            Folder
        }
    }
}
