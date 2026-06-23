namespace Omnipotent.Services.KliveGames.Models
{
    /// <summary>
    /// Live lifecycle state of a server instance. Persisted as a hint but always recomputed on boot
    /// (a running status across an app restart is never trusted; see KliveGames boot reconciliation).
    /// </summary>
    public enum GameServerStatus
    {
        /// <summary>Provisioned and idle — no process running.</summary>
        Stopped = 0,
        /// <summary>Downloading jars / Java / running a loader installer.</summary>
        Provisioning = 1,
        /// <summary>Process launched, world loading, not yet accepting players.</summary>
        Starting = 2,
        /// <summary>Fully started ("Done (Xs)!") and accepting players.</summary>
        Running = 3,
        /// <summary>Graceful stop in progress (stop command sent, waiting for exit).</summary>
        Stopping = 4,
        /// <summary>Process exited unexpectedly (non-zero / unrequested).</summary>
        Crashed = 5,
        /// <summary>Console went silent while Starting — surfaced to UI, NOT auto-killed.</summary>
        Stalled = 6,
    }
}
