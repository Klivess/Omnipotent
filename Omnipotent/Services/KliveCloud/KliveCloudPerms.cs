namespace Omnipotent.Profiles.Permissions
{
    /// <summary>
    /// What a profile may do in KliveCloud at all. Which items they may do it to is decided per item
    /// by its access list (Viewer / Editor) — both must allow an action.
    /// </summary>
    [PermissionSet]
    public static class KliveCloudPerms
    {
        private static readonly PermissionGroup G = new("klivecloud", "KliveCloud");

        public static readonly PermissionDef DriveView = G.Define("drive.view", "Drive", PermissionTier.Glance,
            "View drive capacity", "How much of the cloud drive is used and free.", ProfileRank.Guest);

        public static readonly PermissionDef FilesBrowse = G.Define("files.browse", "Files", PermissionTier.Read,
            "Browse files", "List folders and files shared with you, with previews.", ProfileRank.Guest,
            implies: new[] { "drive.view" });

        public static readonly PermissionDef FilesDownload = G.Define("files.download", "Files", PermissionTier.Read,
            "Download files", "Download and stream files shared with you.", ProfileRank.Guest,
            implies: new[] { "files.browse" });

        public static readonly PermissionDef FilesUpload = G.Define("files.upload", "Files", PermissionTier.Act,
            "Upload files", "Upload files and create folders where you are an editor.", ProfileRank.Guest,
            implies: new[] { "files.browse" });

        public static readonly PermissionDef FilesOrganize = G.Define("files.organize", "Files", PermissionTier.Act,
            "Move files", "Move items between folders where you are an editor.", ProfileRank.Guest,
            implies: new[] { "files.browse" });

        public static readonly PermissionDef FilesDelete = G.Define("files.delete", "Files", PermissionTier.Manage,
            "Delete files", "Delete items where you are an editor, including everything inside folders.", ProfileRank.Guest,
            implies: new[] { "files.browse" });

        public static readonly PermissionDef SharingManage = G.Define("sharing.manage", "Sharing", PermissionTier.Manage,
            "Share files", "Choose who can access items you edit, and create public share links.", ProfileRank.Guest,
            implies: new[] { "files.browse" });

        public static readonly PermissionDef SharingManageAny = G.Define("sharing.manage-any", "Sharing", PermissionTier.Manage,
            "Manage anyone's share links", "List, change and delete share links other profiles created.", ProfileRank.Admin,
            implies: new[] { "sharing.manage" }, refines: new[] { "/KliveCloud/ListShareLinks", "/KliveCloud/DeleteShareLink", "/KliveCloud/UpdateShareLinkPermission" });

        public static readonly PermissionDef FilesAll = G.Define("files.all", "Files", PermissionTier.Critical,
            "Access every file", "Ignore access lists: see and edit every item in KliveCloud.", ProfileRank.Klives,
            implies: new[] { "files.download", "files.upload", "files.organize", "files.delete", "sharing.manage-any" },
            refines: new[] { "/KliveCloud/ListItems", "/KliveCloud/GetItemInfo", "/KliveCloud/DownloadFile" }, sensitive: true);
    }
}
