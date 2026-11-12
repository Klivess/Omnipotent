namespace Omnipotent.Profiles.Permissions
{
    [PermissionSet]
    public static class KliveLLMPerms
    {
        private static readonly PermissionGroup G = new("klivellm", "KliveLLM");

        public static readonly PermissionDef OverviewView = G.Define("overview.view", "Overview", PermissionTier.Glance,
            "View KliveLLM", "Which service uses which router and model, router health, the routing in force and headline usage.",
            ProfileRank.Klives);

        public static readonly PermissionDef AnalyticsRead = G.Define("analytics.read", "Analytics", PermissionTier.Read,
            "Read LLM analytics", "Time series, breakdowns, latency distributions, the cost simulator and forecasts.",
            ProfileRank.Klives, implies: new[] { "overview.view" });

        public static readonly PermissionDef CallsRead = G.Define("calls.read", "Calls", PermissionTier.Read,
            "Read the call log", "Every model call with its service, router, model, tokens, timings, cost and error (no prompt text).",
            ProfileRank.Klives, implies: new[] { "overview.view" });

        public static readonly PermissionDef ContentRead = G.Define("content.read", "Calls", PermissionTier.Read,
            "Read kept prompt and reply text", "The prompts and replies of calls made while text capture was switched on for their service.",
            ProfileRank.Klives, implies: new[] { "calls.read" }, sensitive: true);

        public static readonly PermissionDef RoutingManage = G.Define("routing.manage", "Routing", PermissionTier.Manage,
            "Change model routing", "Assign routers and models to services, set fallbacks, budgets and spending, pause a service, set an override.",
            ProfileRank.Klives, implies: new[] { "overview.view" });

        public static readonly PermissionDef RoutersManage = G.Define("routers.manage", "Routers", PermissionTier.Critical,
            "Manage routers and their keys", "Add, change, test and remove routers, and set their API keys.",
            ProfileRank.Klives, implies: new[] { "overview.view" });

        public static readonly PermissionDef PricesManage = G.Define("prices.manage", "Costs", PermissionTier.Manage,
            "Edit model prices", "The per-token prices calls are costed and simulated with.",
            ProfileRank.Klives, implies: new[] { "overview.view" });

        public static readonly PermissionDef DataExport = G.Define("data.export", "Data", PermissionTier.Manage,
            "Export LLM data", "Download the call log and time series as CSV.",
            ProfileRank.Klives, implies: new[] { "analytics.read", "calls.read" });

        public static readonly PermissionDef SettingsManage = G.Define("settings.manage", "Settings", PermissionTier.Critical,
            "Change KliveLLM ledger settings", "Retention, router probes and the ledger's other settings.",
            ProfileRank.Klives, implies: new[] { "overview.view" });
    }
}
