namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class StratumPerms
    {
        private static readonly PermissionGroup G = new("stratum", "Stratum");

        public static readonly PermissionDef ProjectsRead = G.Define("projects.read", "Projects", PermissionTier.Read,
            "View your designs", "Your Stratum projects, runs, artifacts, attachments and chats.", ProfileRank.Guest);

        public static readonly PermissionDef DesignRun = G.Define("design.run", "Design", PermissionTier.Act,
            "Design with Stratum", "Start and cancel runs, resolve gates, chat with the engineer and upload files.",
            ProfileRank.Guest, implies: new[] { "projects.read" });

        public static readonly PermissionDef ProjectsManage = G.Define("projects.manage", "Projects", PermissionTier.Manage,
            "Manage your designs", "Create, rename and delete your Stratum projects.", ProfileRank.Guest,
            implies: new[] { "projects.read" });
    }
}
