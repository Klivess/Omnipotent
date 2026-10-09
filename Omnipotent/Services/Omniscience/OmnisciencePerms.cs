namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class OmnisciencePerms
    {
        private static readonly PermissionGroup G = new("omniscience", "Omniscience");

        public static readonly PermissionDef OverviewView = G.Define("overview.view", "Overview", PermissionTier.Glance,
            "View Omniscience status", "Collection statistics, schedules and deduction status.", ProfileRank.Klives);

        public static readonly PermissionDef SourcesView = G.Define("sources.view", "Sources", PermissionTier.Read,
            "View data sources", "The channels and accounts Omniscience collects from.", ProfileRank.Klives);

        public static readonly PermissionDef PersonsRead = G.Define("persons.read", "People", PermissionTier.Read,
            "Read people profiles", "Deduced profiles, facts, relationships, eras, watchlists, radar alerts and the review queue.",
            ProfileRank.Klives, sensitive: true);

        public static readonly PermissionDef MessagesRead = G.Define("messages.read", "Messages", PermissionTier.Read,
            "Read raw messages", "Conversations and full message history, including semantic search.", ProfileRank.Klives,
            sensitive: true);

        public static readonly PermissionDef PersonsAsk = G.Define("persons.ask", "People", PermissionTier.Act,
            "Ask about people", "Ask questions answered from a person's message history.", ProfileRank.Klives,
            implies: new[] { "persons.read" }, sensitive: true);

        public static readonly PermissionDef ReviewAct = G.Define("review.act", "Review", PermissionTier.Act,
            "Review deductions", "Resolve the review queue, dismiss targets, link identities, observe people, set tiers and eras, edit watchlists.",
            ProfileRank.Klives, implies: new[] { "persons.read" });

        public static readonly PermissionDef AnalysisRun = G.Define("analysis.run", "Analysis", PermissionTier.Act,
            "Run analysis", "Recompute profiles, run deduction and briefings, and trigger scheduled jobs now.", ProfileRank.Klives,
            implies: new[] { "overview.view" });

        public static readonly PermissionDef SourcesManage = G.Define("sources.manage", "Sources", PermissionTier.Manage,
            "Manage sources", "Add, remove and backfill sources, merge people and set profile targets.", ProfileRank.Klives,
            implies: new[] { "sources.view", "persons.read" });

        public static readonly PermissionDef ReplicaRead = G.Define("replica.read", "Replica", PermissionTier.Read,
            "View replicas", "Replica status, training jobs, chats and fidelity reports.", ProfileRank.Klives, sensitive: true);

        public static readonly PermissionDef ReplicaChat = G.Define("replica.chat", "Replica", PermissionTier.Act,
            "Chat with replicas", "Create, rename and delete replica chats and send messages.", ProfileRank.Klives,
            implies: new[] { "replica.read" });

        public static readonly PermissionDef ReplicaTrain = G.Define("replica.train", "Replica", PermissionTier.Manage,
            "Train replicas", "Start replica training and fidelity runs.", ProfileRank.Klives, implies: new[] { "replica.read" });
    }
}
