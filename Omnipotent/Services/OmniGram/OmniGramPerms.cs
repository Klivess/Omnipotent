namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class OmniGramPerms
    {
        private static readonly PermissionGroup G = new("omnigram", "OmniGram");

        public static readonly PermissionDef OverviewView = G.Define("overview.view", "Overview", PermissionTier.Glance,
            "View OmniGram overview", "The autopilot overview and dashboard statistics.", ProfileRank.Guest);

        public static readonly PermissionDef AccountsRead = G.Define("accounts.read", "Accounts", PermissionTier.Read,
            "View accounts", "Instagram accounts with their profile, strategy, health and queue.", ProfileRank.Guest,
            implies: new[] { "overview.view" });

        public static readonly PermissionDef AccountsAct = G.Define("accounts.act", "Accounts", PermissionTier.Act,
            "Operate accounts", "Pause and resume accounts, refresh them from Instagram, plan now and edit their notes.", ProfileRank.Admin,
            implies: new[] { "accounts.read" });

        public static readonly PermissionDef AccountsManage = G.Define("accounts.manage", "Accounts", PermissionTier.Manage,
            "Manage accounts", "Switch autopilot, edit strategies, remove accounts and delete published posts from Instagram.", ProfileRank.Admin,
            implies: new[] { "accounts.act" }, sensitive: true);

        public static readonly PermissionDef ContentRead = G.Define("content.read", "Content", PermissionTier.Read,
            "View content", "Posts, media, the content library, analytics, comments and events.", ProfileRank.Guest,
            implies: new[] { "accounts.read" });

        public static readonly PermissionDef ContentPublish = G.Define("content.publish", "Content", PermissionTier.Act,
            "Publish content", "Compose, schedule, edit, approve, publish, skip and cancel posts, regenerate captions and upload media.",
            ProfileRank.Admin, implies: new[] { "content.read" });

        public static readonly PermissionDef CommentsAct = G.Define("comments.act", "Comments", PermissionTier.Act,
            "Moderate comments", "Reply to, hide and delete comments on the accounts' posts.", ProfileRank.Admin,
            implies: new[] { "content.read" });

        public static readonly PermissionDef SettingsView = G.Define("settings.view", "Settings", PermissionTier.Manage,
            "View settings and connections", "The Instagram app settings, account connections, tokens' expiry and engine status.", ProfileRank.Admin);

        public static readonly PermissionDef SettingsManage = G.Define("settings.manage", "Settings", PermissionTier.Critical,
            "Change settings and connections", "The Instagram app secret, connecting and reconnecting accounts, and removing v1 credentials.", ProfileRank.Admin,
            implies: new[] { "settings.view" }, sensitive: true);
    }
}
