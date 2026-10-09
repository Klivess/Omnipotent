namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class OmniDefencePerms
    {
        private static readonly PermissionGroup G = new("omnidefence", "OmniDefence");

        public static readonly PermissionDef OverviewView = G.Define("overview.view", "Overview", PermissionTier.Glance,
            "View defence overview", "Threat overview, the IP map, fingerprint status and IP classes.", ProfileRank.Klives);

        public static readonly PermissionDef TrafficRead = G.Define("traffic.read", "Traffic", PermissionTier.Read,
            "Read request logs", "Every API request with its IP, profile, page and outcome, plus IP records and fingerprints.",
            ProfileRank.Klives, implies: new[] { "overview.view" }, sensitive: true);

        public static readonly PermissionDef AuthRead = G.Define("auth.read", "Authentication", PermissionTier.Read,
            "Read authentication logs", "Login events, denials and profile actions.", ProfileRank.Klives, sensitive: true);

        public static readonly PermissionDef IpAct = G.Define("ip.act", "IP control", PermissionTier.Act,
            "Annotate and scan IPs", "Add notes to IPs, scan them and change their class.", ProfileRank.Klives,
            implies: new[] { "traffic.read" });

        public static readonly PermissionDef IpBlock = G.Define("ip.block", "IP control", PermissionTier.Manage,
            "Block and release IPs", "Block, unblock, untrap and set the status of IPs.", ProfileRank.Klives,
            implies: new[] { "traffic.read" });

        public static readonly PermissionDef RegionsManage = G.Define("regions.manage", "Rules", PermissionTier.Manage,
            "Manage blocked regions", "View, add and remove geographic blocks.", ProfileRank.Klives);

        public static readonly PermissionDef HoneypotsManage = G.Define("honeypots.manage", "Rules", PermissionTier.Manage,
            "Manage honeypots", "View, add and remove honeypot routes.", ProfileRank.Klives);

        public static readonly PermissionDef DataExport = G.Define("data.export", "Data", PermissionTier.Manage,
            "Export defence data", "Download a full export of OmniDefence's records.", ProfileRank.Klives, sensitive: true);

        public static readonly PermissionDef SettingsManage = G.Define("settings.manage", "Settings", PermissionTier.Critical,
            "Change defence settings", "Read and change OmniDefence settings.", ProfileRank.Klives);
    }
}
