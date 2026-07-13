namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// One desktop container known to the platform. Persisted in the container registry so
    /// containers outlive Omnipotent restarts (§4): on boot the registry is reconciled against
    /// real Docker state and agents reattach to still-running desktops, keeping logins/tabs.
    /// </summary>
    public class DesktopContainerRecord
    {
        /// <summary>Docker container ID.</summary>
        public string ContainerID { get; set; } = "";
        public string ProjectID { get; set; } = "";
        /// <summary>Owning agent for per-agent allocation; null for a shared project desktop.</summary>
        public string? AgentID { get; set; }
        /// <summary>Host port (bound to 127.0.0.1) where the container's VNC server is reachable.</summary>
        public int VncHostPort { get; set; }
        /// <summary>Framebuffer geometry requested at creation.</summary>
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>Last time an agent actually acquired this desktop (stamped on desktop
        /// acquisition, persisted lazily). Drives idle-desktop reaping independent of overall
        /// project activity, so a busy text-tier project doesn't pin an unused desktop forever.</summary>
        public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
        /// <summary>Set when the container was found missing/dead during reconciliation.</summary>
        public bool Lost { get; set; }
        /// <summary>SHA-256 of the desktop image's build context this container was created from
        /// (copied from the image's context-hash label at creation). Compared against the current
        /// image's hash to detect a container running a now-stale image, so it can be recreated
        /// rather than silently keeping tools the newer image would have provided. Empty on legacy
        /// records created before this field existed — treated as stale so they're recreated once.</summary>
        public string ImageContextHash { get; set; } = "";
    }

    /// <summary>
    /// Result of a desktop-readiness preflight: whether the container is up with the baked
    /// visible-browser/structured-inspection stack present, plus a one-line human summary and the raw capability
    /// probe. Turns "a tool silently missing → the agent flails for a whole wake" into "the
    /// preflight says exactly what's present in seconds," and its facts seed later wakes.
    /// </summary>
    public sealed class DesktopReadiness
    {
        /// <summary>True when the desktop is up AND every required capability is present.</summary>
        public bool Ok { get; init; }
        /// <summary>One-line summary suitable for a tool result and a checkpoint fact.</summary>
        public string Summary { get; init; } = "";
        /// <summary>The container the readiness refers to (null when none could be provisioned).</summary>
        public string? ContainerID { get; init; }
        /// <summary>Baked image capability version from /etc/klive-desktop.json, or "" if unstamped.</summary>
        public string ImageVersion { get; init; } = "";
        /// <summary>Probed capability name → "yes"/"no"/"up"/"down". Empty when the probe couldn't run.</summary>
        public Dictionary<string, string> Capabilities { get; init; } = new(StringComparer.Ordinal);
    }

    /// <summary>Docker labels used to recognise our containers during reconciliation.</summary>
    public static class ContainerLabels
    {
        public const string Owner = "omnipotent.owner";          // always "projects"
        public const string ProjectID = "omnipotent.projectID";
        public const string AgentID = "omnipotent.agentID";
        /// <summary>Build-context SHA of the image the container was created from. Same key the
        /// image itself carries, so a container and its image can be compared for staleness.</summary>
        public const string ContextHash = "omnipotent.projects.context-hash";
    }
}
