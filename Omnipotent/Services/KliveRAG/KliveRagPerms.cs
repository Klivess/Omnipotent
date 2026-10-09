namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class KliveRagPerms
    {
        private static readonly PermissionGroup G = new("kliverag", "KliveRAG");

        public static readonly PermissionDef IndexView = G.Define("index.view", "Index", PermissionTier.Glance,
            "View index statistics", "Index size and connected sources.", ProfileRank.Klives);

        public static readonly PermissionDef SearchUse = G.Define("search.use", "Search", PermissionTier.Read,
            "Search knowledge", "Search and open documents from every connected source, and search the web.", ProfileRank.Klives,
            implies: new[] { "index.view" }, sensitive: true);

        public static readonly PermissionDef IndexRebuild = G.Define("index.rebuild", "Index", PermissionTier.Manage,
            "Rebuild the index", "Re-index every source.", ProfileRank.Klives, implies: new[] { "index.view" });
    }
}
