namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class KliveLinkPerms
    {
        private static readonly PermissionGroup G = new("klivelink", "KliveLink");

        public static readonly PermissionDef AgentsView = G.Define("agents.view", "Agents", PermissionTier.Glance,
            "View connected computers", "Which remote computers are connected and their status.", ProfileRank.Klives);

        public static readonly PermissionDef AgentsInspect = G.Define("agents.inspect", "Agents", PermissionTier.Read,
            "Inspect computers", "System information, running processes and directory listings.", ProfileRank.Klives,
            implies: new[] { "agents.view" }, sensitive: true);

        public static readonly PermissionDef AgentsFiles = G.Define("agents.files", "Files", PermissionTier.Manage,
            "Transfer files", "Download files from and upload files to remote computers.", ProfileRank.Klives,
            implies: new[] { "agents.inspect" }, sensitive: true);

        public static readonly PermissionDef AgentsControl = G.Define("agents.control", "Control", PermissionTier.Critical,
            "Control computers", "Run processes and terminal commands, kill processes, watch the screen and disconnect.",
            ProfileRank.Klives, implies: new[] { "agents.inspect" }, sensitive: true);

        public static readonly PermissionDef AgentsDestroy = G.Define("agents.destroy", "Control", PermissionTier.Critical,
            "Uninstall agents", "Tell a remote agent to remove itself.", ProfileRank.Klives, implies: new[] { "agents.view" });
    }
}
