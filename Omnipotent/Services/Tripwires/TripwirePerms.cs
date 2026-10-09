namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class TripwirePerms
    {
        private static readonly PermissionGroup G = new("tripwires", "Tripwires");

        public static readonly PermissionDef WiresRead = G.Define("wires.read", "Tripwires", PermissionTier.Read,
            "View tripwires", "Tripwires, the hits they recorded and their summaries.", ProfileRank.Klives, sensitive: true);

        public static readonly PermissionDef AlertsTest = G.Define("alerts.test", "Alerts", PermissionTier.Act,
            "Send test alerts", "Send a test notification for a tripwire.", ProfileRank.Klives, implies: new[] { "wires.read" });

        public static readonly PermissionDef WiresManage = G.Define("wires.manage", "Tripwires", PermissionTier.Manage,
            "Manage tripwires", "Create, edit and delete tripwires and clear their history.", ProfileRank.Klives,
            implies: new[] { "wires.read" });
    }
}
