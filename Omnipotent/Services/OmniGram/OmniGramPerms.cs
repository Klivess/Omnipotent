namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class OmniGramPerms
    {
        private static readonly PermissionGroup G = new("omnigram", "OmniGram");

        public static readonly PermissionDef OverviewView = G.Define("overview.view", "Overview", PermissionTier.Glance,
            "View OmniGram overview", "Dashboard statistics across all accounts.", ProfileRank.Guest);

        public static readonly PermissionDef AccountsRead = G.Define("accounts.read", "Accounts", PermissionTier.Read,
            "View accounts", "Instagram accounts with their profiles and configuration.", ProfileRank.Guest);

        public static readonly PermissionDef AccountsAct = G.Define("accounts.act", "Accounts", PermissionTier.Act,
            "Operate accounts", "Pause, resume and re-login accounts and edit their notes.", ProfileRank.Admin,
            implies: new[] { "accounts.read" });

        public static readonly PermissionDef AccountsManage = G.Define("accounts.manage", "Accounts", PermissionTier.Manage,
            "Manage accounts", "Add and remove accounts, edit their Instagram profile and configuration.", ProfileRank.Admin,
            implies: new[] { "accounts.act" }, sensitive: true);

        public static readonly PermissionDef ContentRead = G.Define("content.read", "Content", PermissionTier.Read,
            "View content", "Content folders, posts, the publishing queue, analytics and events.", ProfileRank.Guest);

        public static readonly PermissionDef ContentPublish = G.Define("content.publish", "Content", PermissionTier.Act,
            "Publish content", "Schedule, publish, cancel and draft posts, upload media and take analytics snapshots.",
            ProfileRank.Admin, implies: new[] { "content.read" });
    }
}
