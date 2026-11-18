namespace Omnipotent.Services.Demiurge.Containers
{
    /// <summary>
    /// Desktops owned by something other than a Steward — today, KliveAgent's own computer. They share
    /// the container fleet, its capacity policy, idle suspension and the live-view/remote-control
    /// routes, but there is no Steward behind them: no event log to append to, no agent to send a
    /// directive to, and no Steward lifecycle that may reap them.
    /// </summary>
    public static class ExternalDesktopOwners
    {
        /// <summary>The owner id KliveAgent's desktop is registered under (in the StewardID slot).</summary>
        public const string KliveAgentOwnerID = "kliveagent";

        public static bool IsExternal(string? stewardID) =>
            string.Equals(stewardID, KliveAgentOwnerID, StringComparison.Ordinal);
    }
}
