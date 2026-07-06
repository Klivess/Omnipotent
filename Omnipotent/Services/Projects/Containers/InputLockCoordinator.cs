using System.Collections.Concurrent;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Arbitrates the input lock for shared-desktop projects (§4): when several agents share one
    /// desktop, only the agent holding the lock may inject input. The lock auto-expires so a
    /// crashed/stuck agent never wedges the desktop forever — consistent with the no-hard-kill /
    /// no-deadlock stance. Per-agent containers don't use this (no contention).
    /// </summary>
    public class InputLockCoordinator
    {
        private sealed class Holder
        {
            public string AgentID = "";
            public DateTime ExpiresUtc;
        }

        private readonly ConcurrentDictionary<string, Holder> holders = new(StringComparer.Ordinal); // keyed by containerID
        private readonly object gate = new();
        private static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Tries to acquire (or renew) the input lock on a container for an agent. Returns true
        /// if the caller now holds it. A held lease held by another agent that has expired is
        /// reclaimable.
        /// </summary>
        public bool TryAcquire(string containerID, string agentID, TimeSpan? lease = null)
        {
            lock (gate)
            {
                var now = DateTime.UtcNow;
                if (holders.TryGetValue(containerID, out var h))
                {
                    if (h.AgentID != agentID && h.ExpiresUtc > now) return false; // someone else holds a live lease
                }
                holders[containerID] = new Holder { AgentID = agentID, ExpiresUtc = now + (lease ?? DefaultLease) };
                return true;
            }
        }

        /// <summary>True if the agent currently holds a live lease on the container.</summary>
        public bool Holds(string containerID, string agentID)
        {
            lock (gate)
            {
                return holders.TryGetValue(containerID, out var h)
                    && h.AgentID == agentID
                    && h.ExpiresUtc > DateTime.UtcNow;
            }
        }

        public void Release(string containerID, string agentID)
        {
            lock (gate)
            {
                if (holders.TryGetValue(containerID, out var h) && h.AgentID == agentID)
                    holders.TryRemove(containerID, out _);
            }
        }

        /// <summary>Who holds the lock right now (for UI/telemetry), or null.</summary>
        public string? CurrentHolder(string containerID)
        {
            lock (gate)
            {
                return holders.TryGetValue(containerID, out var h) && h.ExpiresUtc > DateTime.UtcNow ? h.AgentID : null;
            }
        }
    }
}
