namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class ProjectsPerms
    {
        private static readonly PermissionGroup G = new("projects", "Projects");

        public static readonly PermissionDef OverviewView = G.Define("overview.view", "Overview", PermissionTier.Glance,
            "View projects overview", "The project list, fleet health, cache health and broadcast status.", ProfileRank.Klives);

        public static readonly PermissionDef DetailsRead = G.Define("details.read", "Projects", PermissionTier.Read,
            "Read project details", "State, events, digests, ledgers, analytics, agents, plans, councils, memory, gates, hooks and artifacts.",
            ProfileRank.Klives, implies: new[] { "overview.view" }, sensitive: true);

        public static readonly PermissionDef EventsStream = G.Define("events.stream", "Projects", PermissionTier.Read,
            "Stream project events", "The live event stream of a project.", ProfileRank.Klives, implies: new[] { "details.read" });

        public static readonly PermissionDef ScreensView = G.Define("screens.view", "Computers", PermissionTier.Read,
            "Watch agent computers", "Live video of the agents' container desktops.", ProfileRank.Klives,
            implies: new[] { "details.read" });

        public static readonly PermissionDef FilesRead = G.Define("files.read", "Files", PermissionTier.Read,
            "Read project files", "List, inspect and download project files and their audit trail.", ProfileRank.Klives,
            implies: new[] { "details.read" });

        public static readonly PermissionDef AgentsMessage = G.Define("agents.message", "Agents", PermissionTier.Act,
            "Message agents", "Message a project's commander or agents and broadcast to every project.", ProfileRank.Klives,
            implies: new[] { "details.read" });

        public static readonly PermissionDef GatesResolve = G.Define("gates.resolve", "Agents", PermissionTier.Act,
            "Resolve approval gates", "Approve or reject what agents ask permission for.", ProfileRank.Klives,
            implies: new[] { "details.read" });

        public static readonly PermissionDef PlanEdit = G.Define("plan.edit", "Planning", PermissionTier.Act,
            "Edit plans and memory", "Add, reorder, activate and close steps, edit project memory and observables, pin results.",
            ProfileRank.Klives, implies: new[] { "details.read" });

        public static readonly PermissionDef FilesWrite = G.Define("files.write", "Files", PermissionTier.Act,
            "Write project files", "Upload, move, copy and organise project files.", ProfileRank.Klives,
            implies: new[] { "files.read" });

        public static readonly PermissionDef FilesDelete = G.Define("files.delete", "Files", PermissionTier.Manage,
            "Delete project files", "Delete project files.", ProfileRank.Klives, implies: new[] { "files.read" });

        public static readonly PermissionDef LifecycleManage = G.Define("lifecycle.manage", "Projects", PermissionTier.Manage,
            "Run projects", "Create, pause, resume, archive and rename projects, change budgets and settings, retire agents.",
            ProfileRank.Klives, implies: new[] { "details.read" });

        public static readonly PermissionDef FleetControl = G.Define("fleet.control", "Fleet", PermissionTier.Critical,
            "Control the whole fleet", "Halt and resume every project, change system-wide settings and reset cache health.",
            ProfileRank.Klives, implies: new[] { "overview.view" });

        public static readonly PermissionDef HooksManage = G.Define("hooks.manage", "Hooks", PermissionTier.Critical,
            "Manage hooks", "Create and delete project hooks and rotate their tokens.", ProfileRank.Klives,
            implies: new[] { "details.read" }, sensitive: true);

        public static readonly PermissionDef ScreensControl = G.Define("screens.control", "Computers", PermissionTier.Critical,
            "Control agent computers", "Send mouse and keyboard input to agent desktops.", ProfileRank.Klives,
            implies: new[] { "screens.view" }, sensitive: true);
    }
}
