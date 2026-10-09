namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class Cs2Perms
    {
        private static readonly PermissionGroup G = new("cs2", "CS2 Arbitrage");

        public static readonly PermissionDef StatusView = G.Define("status.view", "Overview", PermissionTier.Glance,
            "View bot status", "Whether the arbitrage engine is running and how it is doing.", ProfileRank.Guest);

        public static readonly PermissionDef ScansRead = G.Define("scans.read", "Scans", PermissionTier.Read,
            "View scans and opportunities", "Scan analytics, results, opportunities, the liquidity plan and balance history.",
            ProfileRank.Guest, implies: new[] { "status.view" });

        public static readonly PermissionDef ScansRun = G.Define("scans.run", "Scans", PermissionTier.Act,
            "Trigger scans", "Ask the engine to scan the market now.", ProfileRank.Manager, implies: new[] { "scans.read" });
    }
}
