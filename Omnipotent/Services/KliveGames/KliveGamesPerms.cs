namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class KliveGamesPerms
    {
        private static readonly PermissionGroup G = new("klivegames", "KliveGames");

        public static readonly PermissionDef ServersRead = G.Define("servers.read", "Servers", PermissionTier.Read,
            "View game servers", "Games, versions, servers, players, backups, configuration and file listings.", ProfileRank.Klives);

        public static readonly PermissionDef ServersOperate = G.Define("servers.operate", "Servers", PermissionTier.Act,
            "Operate game servers", "Start, stop and restart servers and act on players.", ProfileRank.Klives,
            implies: new[] { "servers.read" });

        public static readonly PermissionDef ConsoleUse = G.Define("console.use", "Console", PermissionTier.Manage,
            "Use server consoles", "Send console commands and watch the live console.", ProfileRank.Klives,
            implies: new[] { "servers.read" });

        public static readonly PermissionDef ServersManage = G.Define("servers.manage", "Servers", PermissionTier.Manage,
            "Manage game servers", "Create, kill and delete servers, change configuration, networking and backups.",
            ProfileRank.Klives, implies: new[] { "servers.operate" });

        public static readonly PermissionDef FilesRead = G.Define("files.read", "Files", PermissionTier.Read,
            "Download server files", "Download server files and backups.", ProfileRank.Klives, implies: new[] { "servers.read" });

        public static readonly PermissionDef FilesWrite = G.Define("files.write", "Files", PermissionTier.Manage,
            "Edit server files", "Upload, edit and delete server files.", ProfileRank.Klives, implies: new[] { "files.read" });
    }
}
