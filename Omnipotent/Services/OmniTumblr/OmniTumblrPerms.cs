namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class OmniTumblrPerms
    {
        private static readonly PermissionGroup G = new("omnitumblr", "OmniTumblr");

        public static readonly PermissionDef OverviewView = G.Define("overview.view", "Overview", PermissionTier.Glance,
            "View OmniTumblr overview", "The autopilot overview and dashboard statistics.", ProfileRank.Guest);

        public static readonly PermissionDef BlogsRead = G.Define("blogs.read", "Blogs", PermissionTier.Read,
            "View blogs and posts", "Blogs, posts, media, the content library, analytics and events.", ProfileRank.Guest,
            implies: new[] { "overview.view" });

        public static readonly PermissionDef PostsAct = G.Define("posts.act", "Posts", PermissionTier.Act,
            "Work on posts", "Create, edit, approve, skip, cancel and retry posts, regenerate captions and upload media.",
            ProfileRank.Admin, implies: new[] { "blogs.read" });

        public static readonly PermissionDef PostsPublish = G.Define("posts.publish", "Posts", PermissionTier.Manage,
            "Publish to Tumblr", "Publish posts immediately and delete published posts from Tumblr.", ProfileRank.Admin,
            implies: new[] { "posts.act" });

        public static readonly PermissionDef LibraryManage = G.Define("library.manage", "Library", PermissionTier.Manage,
            "Manage the content library", "Delete items from the content library.", ProfileRank.Admin,
            implies: new[] { "blogs.read" });

        public static readonly PermissionDef BlogsManage = G.Define("blogs.manage", "Blogs", PermissionTier.Manage,
            "Manage blogs", "Add, edit, remove and refresh blogs and plan their posts now.", ProfileRank.Admin,
            implies: new[] { "blogs.read" });

        public static readonly PermissionDef SettingsView = G.Define("settings.view", "Settings", PermissionTier.Manage,
            "View settings and connections", "App settings and the state of Tumblr connections.", ProfileRank.Admin);

        public static readonly PermissionDef SettingsManage = G.Define("settings.manage", "Settings", PermissionTier.Critical,
            "Change settings and connections", "App keys, connecting and disconnecting Tumblr accounts.", ProfileRank.Admin,
            implies: new[] { "settings.view" }, sensitive: true);
    }
}
