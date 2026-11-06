namespace Omnipotent.Profiles.Permissions
{
    /// <summary>
    /// Omniscience keys added by v3. They share the "omniscience" service key with the v2 keys in
    /// <c>OmnisciencePerms</c> (overview, sources, persons, messages, review, analysis, replica), which v3 keeps
    /// using, so existing grants carry over at cutover. At cutover the v2 file moves into the v3 folder.
    /// </summary>
    [PermissionSet]
    public static class OmniscienceV3Perms
    {
        private static readonly PermissionGroup G = new("omniscience", "Omniscience");

        public static readonly PermissionDef EngineManage = G.Define("engine.manage", "Engine", PermissionTier.Manage,
            "Manage the intelligence engine", "Enable and disable modules, change engine settings, cancel and retry jobs.",
            ProfileRank.Klives, implies: new[] { "overview.view" });

        public static readonly PermissionDef TacticsRun = G.Define("tactics.run", "Tactics", PermissionTier.Act,
            "Run tactics and inquiries", "Ask any question and have it answered from all the evidence, and derive new data from selected data.",
            ProfileRank.Klives, implies: new[] { "persons.read" }, sensitive: true);

        public static readonly PermissionDef FacesRead = G.Define("faces.read", "Faces", PermissionTier.Read,
            "View faces", "Detected faces, face clusters and their labels (never shown for minors).",
            ProfileRank.Klives, sensitive: true);

        public static readonly PermissionDef FacesLabel = G.Define("faces.label", "Faces", PermissionTier.Act,
            "Label faces", "Name face clusters, and confirm or reject labels propagated to other images.",
            ProfileRank.Klives, implies: new[] { "faces.read" }, sensitive: true);

        public static readonly PermissionDef DataExport = G.Define("data.export", "Data", PermissionTier.Read,
            "Export data", "Stream complete exports (CSV or JSON lines) of any dataset with its filters applied.",
            ProfileRank.Klives, sensitive: true);

        public static readonly PermissionDef AlertsManage = G.Define("alerts.manage", "Alerts", PermissionTier.Act,
            "Manage alerts", "Create, edit, mute and delete alert rules and watchlists.",
            ProfileRank.Klives, implies: new[] { "persons.read" });

        public static readonly PermissionDef OutboundConfirm = G.Define("outbound.confirm", "Outbound", PermissionTier.Manage,
            "Confirm outbound actions", "Approve anything a third party would see. Nothing is ever sent without this confirmation.",
            ProfileRank.Klives, sensitive: true);
    }
}
