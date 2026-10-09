namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class KliveAgentPerms
    {
        private static readonly PermissionGroup G = new("kliveagent", "KliveAgent");

        public static readonly PermissionDef StatusView = G.Define("status.view", "Overview", PermissionTier.Glance,
            "View agent status", "Whether the agent is busy, its computer and usage summaries.", ProfileRank.Klives);

        public static readonly PermissionDef HistoryRead = G.Define("history.read", "History", PermissionTier.Read,
            "Read agent history", "Conversations, runs, tasks, jobs, notifications, tools and attachments.", ProfileRank.Klives,
            implies: new[] { "status.view" }, sensitive: true);

        public static readonly PermissionDef ChatUse = G.Define("chat.use", "Chat", PermissionTier.Act,
            "Chat with the agent", "Send messages, steer, cancel and approve runs, resolve handoffs and upload attachments.",
            ProfileRank.Klives, implies: new[] { "history.read" });

        public static readonly PermissionDef JobsManage = G.Define("jobs.manage", "Jobs", PermissionTier.Act,
            "Manage agent jobs", "Create, steer, stop and resume background jobs and cancel tasks.", ProfileRank.Klives,
            implies: new[] { "history.read" });

        public static readonly PermissionDef MemoriesRead = G.Define("memories.read", "Memory", PermissionTier.Read,
            "Read agent memory", "Everything the agent remembers.", ProfileRank.Klives, sensitive: true);

        public static readonly PermissionDef MemoriesManage = G.Define("memories.manage", "Memory", PermissionTier.Manage,
            "Edit agent memory", "Add and delete agent memories.", ProfileRank.Klives, implies: new[] { "memories.read" });

        public static readonly PermissionDef IndexRebuild = G.Define("index.rebuild", "Memory", PermissionTier.Manage,
            "Rebuild the agent index", "Re-index the agent's knowledge.", ProfileRank.Klives);

        public static readonly PermissionDef CapabilitiesExecute = G.Define("capabilities.execute", "Capabilities", PermissionTier.Critical,
            "Run agent capabilities", "Invoke agent capabilities directly.", ProfileRank.Klives, implies: new[] { "history.read" });

        public static readonly PermissionDef CapabilitiesElevated = G.Define("capabilities.elevated", "Capabilities", PermissionTier.Critical,
            "Run elevated capabilities", "Invoke capabilities that need elevated permissions.", ProfileRank.Klives,
            implies: new[] { "capabilities.execute" }, refines: new[] { "/kliveagent/capabilities/execute" });
    }
}
