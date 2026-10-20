namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class OmniBloxPerms
    {
        private static readonly PermissionGroup G = new("omniblox", "OmniBlox");

        public static readonly PermissionDef OverviewView = G.Define("overview.view", "Overview", PermissionTier.Glance,
            "View the OmniBlox studio overview", "What the studio is doing, its games, owner tasks and headline numbers.", ProfileRank.Admin);

        public static readonly PermissionDef StudioRead = G.Define("studio.read", "Studio", PermissionTier.Read,
            "View games, assets and the Playbook", "Game dossiers, renders, media, the Asset Library, events and lessons.", ProfileRank.Admin,
            implies: new[] { "overview.view" });

        public static readonly PermissionDef StudioAct = G.Define("studio.act", "Studio", PermissionTier.Act,
            "Steer the studio", "Resolve owner tasks, pause or steer games, start a new game, request maintenance.", ProfileRank.Admin,
            implies: new[] { "studio.read" });

        public static readonly PermissionDef StudioManage = G.Define("studio.manage", "Studio", PermissionTier.Critical,
            "Run the studio and approve launches", "Switch the studio and the asset harvester on or off, approve games going public, abandon games.", ProfileRank.Admin,
            implies: new[] { "studio.act" }, sensitive: true);
    }
}
