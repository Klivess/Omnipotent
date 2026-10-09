namespace Omnipotent.Profiles.Permissions
{
    /// <summary>The two pseudo-keys that are not granted per profile.</summary>
    [PermissionSet]
    public static class Perms
    {
        /// <summary>No authentication at all. Use only for routes that must answer strangers.</summary>
        public static readonly PermissionDef Public = Pseudo("public", PermissionKind.Public,
            "Public", "Anyone, signed in or not.");

        /// <summary>
        /// Any enabled, authenticated profile: their own profile, sessions and presence, /batch and
        /// client telemetry. Suspended and read-only profiles keep these so the site can explain why
        /// everything else is locked.
        /// </summary>
        public static readonly PermissionDef SignedIn = Pseudo("signed-in", PermissionKind.SignedIn,
            "Any signed-in profile", "Every enabled profile, regardless of its permissions.");

        private static PermissionDef Pseudo(string key, PermissionKind kind, string title, string description)
        {
            var def = new PermissionDef(key, "Klives Management", key, "Access", PermissionTier.Glance, title, description,
                ProfileRank.None, kind, Array.Empty<string>(), Array.Empty<string>(), sensitive: false);
            PermissionCatalog.Add(def);
            return def;
        }
    }

    [PermissionSet]
    public static class ProfilesPerms
    {
        private static readonly PermissionGroup G = new("profiles", "Profiles");

        public static readonly PermissionDef DirectoryView = G.Define("directory.view", "Directory", PermissionTier.Glance,
            "View the profile directory", "List profiles with their rank, status and whether they are online.", ProfileRank.Associate);

        public static readonly PermissionDef PermissionsView = G.Define("permissions.view", "Permissions", PermissionTier.Read,
            "View permissions", "See the permission catalog, the routes each permission unlocks and every profile's grants.",
            ProfileRank.Admin, implies: new[] { "directory.view" });

        public static readonly PermissionDef ActivityRead = G.Define("activity.read", "Activity", PermissionTier.Read,
            "View profile activity", "Request history, page views, logins and denials of other profiles.",
            ProfileRank.Klives, implies: new[] { "directory.view" }, sensitive: true);

        public static readonly PermissionDef ActivityLive = G.Define("activity.live", "Activity", PermissionTier.Read,
            "Watch profiles live", "Follow what another profile is doing right now: current page and live requests.",
            ProfileRank.Klives, implies: new[] { "activity.read" }, sensitive: true);

        public static readonly PermissionDef Create = G.Define("lifecycle.create", "Lifecycle", PermissionTier.Manage,
            "Create profiles", "Create new profiles ranked below your own.", ProfileRank.Associate,
            implies: new[] { "directory.view" });

        public static readonly PermissionDef Edit = G.Define("lifecycle.edit", "Lifecycle", PermissionTier.Manage,
            "Edit profiles", "Rename profiles, change their Discord ID and set their rank (below your own).",
            ProfileRank.Admin, implies: new[] { "directory.view" });

        public static readonly PermissionDef PermissionsGrant = G.Define("permissions.grant", "Permissions", PermissionTier.Critical,
            "Grant and revoke permissions", "Change what other profiles may do. Only the owner can hand this out.",
            ProfileRank.Klives, implies: new[] { "permissions.view" });

        public static readonly PermissionDef AccessControl = G.Define("access.control", "Access control", PermissionTier.Critical,
            "Control profile access live", "Suspend profiles, make them read-only, turn login off and sign out their sessions.",
            ProfileRank.Admin, implies: new[] { "directory.view" });

        public static readonly PermissionDef CredentialsReset = G.Define("credentials.reset", "Credentials", PermissionTier.Critical,
            "Reset passwords", "Set a new password for another profile (signs out their sessions).",
            ProfileRank.Klives, implies: new[] { "directory.view" }, sensitive: true);

        public static readonly PermissionDef Delete = G.Define("lifecycle.delete", "Lifecycle", PermissionTier.Critical,
            "Delete profiles", "Permanently delete a profile and end its sessions.", ProfileRank.Klives,
            implies: new[] { "directory.view" });
    }

    [PermissionSet]
    public static class SystemPerms
    {
        private static readonly PermissionGroup G = new("system", "System");

        public static readonly PermissionDef StatusView = G.Define("status.view", "Status", PermissionTier.Glance,
            "View system status", "Front-page statistics and API request statistics.", ProfileRank.Guest);

        public static readonly PermissionDef UptimeView = G.Define("uptime.view", "Status", PermissionTier.Glance,
            "View uptime history", "Service uptime and outage history.", ProfileRank.Admin);

        public static readonly PermissionDef ResourcesRead = G.Define("resources.read", "Resources", PermissionTier.Read,
            "View host resources", "Hardware, running services, processes and browser instances.", ProfileRank.Guest);

        public static readonly PermissionDef LogsRead = G.Define("logs.read", "Logs", PermissionTier.Read,
            "Read service logs", "The live service log and its summaries.", ProfileRank.Admin);

        public static readonly PermissionDef SchedulerRead = G.Define("scheduler.read", "Scheduler", PermissionTier.Read,
            "View scheduled tasks", "Every task waiting in the time manager.", ProfileRank.Guest);

        public static readonly PermissionDef SchedulerRun = G.Define("scheduler.run", "Scheduler", PermissionTier.Manage,
            "Fire scheduled tasks early", "Run a scheduled task now instead of at its due time.", ProfileRank.Associate,
            implies: new[] { "scheduler.read" });

        public static readonly PermissionDef ServicesControl = G.Define("services.control", "Services", PermissionTier.Critical,
            "Restart and stop services", "Restart or terminate any running service.", ProfileRank.Klives,
            implies: new[] { "resources.read" });

        public static readonly PermissionDef UpdateDeploy = G.Define("update.deploy", "Services", PermissionTier.Critical,
            "Update Omnipotent", "Pull and deploy a new build of the whole system.", ProfileRank.Klives);

        public static readonly PermissionDef TerminalUse = G.Define("terminal.use", "Host", PermissionTier.Critical,
            "Use the host terminal", "Run shell commands on the server.", ProfileRank.Klives, sensitive: true);

        public static readonly PermissionDef HostControlView = G.Define("hostcontrol.view", "Host", PermissionTier.Manage,
            "Watch the host screen", "Live video of the server's desktop.", ProfileRank.Klives, sensitive: true);

        public static readonly PermissionDef HostControlUse = G.Define("hostcontrol.use", "Host", PermissionTier.Critical,
            "Control the host desktop", "Send mouse and keyboard input to the server's desktop.", ProfileRank.Klives,
            implies: new[] { "hostcontrol.view" }, sensitive: true);

        public static readonly PermissionDef PortForwardingRead = G.Define("portforwarding.read", "Network", PermissionTier.Read,
            "View port forwarding", "UPnP port mappings on the router.", ProfileRank.Klives);

        public static readonly PermissionDef PortForwardingManage = G.Define("portforwarding.manage", "Network", PermissionTier.Critical,
            "Change port forwarding", "Add, edit and delete router port mappings.", ProfileRank.Klives,
            implies: new[] { "portforwarding.read" });

        public static readonly PermissionDef SettingsRead = G.Define("settings.read", "Settings", PermissionTier.Critical,
            "Read system settings", "Every OmniSetting, including secrets when revealed.", ProfileRank.Klives, sensitive: true);

        public static readonly PermissionDef SettingsWrite = G.Define("settings.write", "Settings", PermissionTier.Critical,
            "Change system settings", "Create, change and delete OmniSettings.", ProfileRank.Klives,
            implies: new[] { "settings.read" }, sensitive: true);

        public static readonly PermissionDef ApiRoutesRead = G.Define("api.routes.read", "API", PermissionTier.Read,
            "List API routes", "Every registered route with its method and permission.", ProfileRank.Associate);

        public static readonly PermissionDef ApiTelemetryRead = G.Define("api.telemetry.read", "API", PermissionTier.Read,
            "View API telemetry", "Latency, traffic, traces and client timings for every route.", ProfileRank.Klives);

        public static readonly PermissionDef ApiCacheManage = G.Define("api.cache.manage", "API", PermissionTier.Manage,
            "Manage the response cache", "Read cache statistics and clear the response cache.", ProfileRank.Klives);
    }
}
