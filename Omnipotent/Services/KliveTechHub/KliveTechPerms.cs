namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class KliveTechPerms
    {
        private static readonly PermissionGroup G = new("klivetech", "KliveTech");

        public static readonly PermissionDef GadgetsRead = G.Define("gadgets.read", "Gadgets", PermissionTier.Read,
            "View gadgets", "Every gadget and its state.", ProfileRank.Klives);

        public static readonly PermissionDef GadgetsAct = G.Define("gadgets.act", "Gadgets", PermissionTier.Act,
            "Operate gadgets", "Run gadget actions.", ProfileRank.Klives, implies: new[] { "gadgets.read" });

        public static readonly PermissionDef StreamablesRead = G.Define("streamables.read", "Streamables", PermissionTier.Read,
            "View live telemetry", "Streamable values, their history and the live feed.", ProfileRank.Klives);

        public static readonly PermissionDef StreamablesControl = G.Define("streamables.control", "Streamables", PermissionTier.Act,
            "Control streamables", "Start, stop and adjust streamables.", ProfileRank.Klives, implies: new[] { "streamables.read" });

        public static readonly PermissionDef FirmwareRead = G.Define("firmware.read", "Firmware", PermissionTier.Read,
            "View firmware", "Firmware configuration, projects and build jobs.", ProfileRank.Klives);

        public static readonly PermissionDef FirmwareDeploy = G.Define("firmware.deploy", "Firmware", PermissionTier.Critical,
            "Build and flash firmware", "Compile firmware and push updates to devices.", ProfileRank.Klives,
            implies: new[] { "firmware.read" });
    }
}
