namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Desktops owned by something other than a Project — today, KliveAgent's own computer. They share
    /// the container fleet, its capacity policy, idle suspension and the live-view/remote-control
    /// routes, but there is no project behind them: no event log to append to, no agent to send a
    /// directive to, and no project lifecycle that may reap them.
    /// </summary>
    public static class ExternalDesktopOwners
    {
        /// <summary>The owner id KliveAgent's desktop is registered under (in the ProjectID slot).</summary>
        public const string KliveAgentOwnerID = "kliveagent";

        public static bool IsExternal(string? projectID) =>
            string.Equals(projectID, KliveAgentOwnerID, StringComparison.Ordinal);
    }
}
