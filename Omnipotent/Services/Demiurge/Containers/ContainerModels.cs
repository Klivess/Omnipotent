namespace Omnipotent.Services.Demiurge.Containers
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
        public string StewardID { get; set; } = "";
        /// <summary>Owning agent for per-agent allocation; null for a shared Steward desktop.</summary>
        public string? AgentID { get; set; }
        /// <summary>Host port (bound to 127.0.0.1) where the container's VNC server is reachable.</summary>
        public int VncHostPort { get; set; }
        /// <summary>
        /// Host port (bound to 127.0.0.1) for the container's browser-helper service, or 0 on a
        /// container built before it existed. Non-zero is what lets a browser action skip Docker
        /// entirely; zero falls back to `docker exec` and the stalls that come with it.
        /// </summary>
        public int BrowserServiceHostPort { get; set; }
        /// <summary>Framebuffer geometry requested at creation.</summary>
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>Last time an agent actually acquired this desktop (stamped on desktop
        /// acquisition, persisted lazily). Drives idle-desktop reaping independent of overall
        /// Steward activity, so a busy text-tier Steward doesn't pin an unused desktop forever.</summary>
        public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
        /// <summary>Set when the container was found missing/dead during reconciliation.</summary>
        public bool Lost { get; set; }
        /// <summary>Intentionally stopped for idle resource savings; keep its writable filesystem.</summary>
        public bool Suspended { get; set; }
        /// <summary>SHA-256 of the desktop image's build context this container was created from
        /// (copied from the image's context-hash label at creation). Compared against the current
        /// image's hash to detect a container running a now-stale image, so it can be recreated
        /// rather than silently keeping tools the newer image would have provided. Empty on legacy
        /// records created before this field existed — treated as stale so they're recreated once.</summary>
        public string ImageContextHash { get; set; } = "";
    }

    /// <summary>
    /// Result of a desktop-readiness preflight: whether the container is up with the baked
    /// human-usable shell/VNC stack present, plus a one-line human summary and the raw capability
    /// probe. Turns "a tool silently missing → the agent flails for a whole wake" into "the
    /// preflight says exactly what's present in seconds," and its facts seed later wakes.
    /// </summary>
    public sealed class DesktopReadiness
    {
        /// <summary>True when the human-usable desktop is up. Optional browser CDP is not required.</summary>
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

    /// <summary>
    /// The Docker daemon did not answer inside the budget the caller allowed it.
    ///
    /// Distinct from a command timing out INSIDE a container (which comes back as a normal
    /// <see cref="ContainerShellResult"/> with exit 124): this says the host-side call never
    /// returned, so nothing is known about whether the work ran. Its own type because the two need
    /// opposite responses — a slow page is the agent's problem, an unresponsive daemon is not, and
    /// telling an agent to "retry the selector" when the host is wedged sends it in circles.
    /// </summary>
    public sealed class ContainerDaemonTimeoutException : Exception
    {
        public ContainerDaemonTimeoutException(string message) : base(message) { }
    }

    /// <summary>
    /// Docker labels used to recognise our containers during reconciliation. Desktops and images
    /// made before Projects became Demiurge carry the pre-rename owner value and keys; they are
    /// still recognised everywhere, so an old desktop is adopted, reaped or replaced like any other
    /// and the existing image is never mistaken for an unbuilt one.
    /// </summary>
    public static class ContainerLabels
    {
        public const string Owner = "omnipotent.owner";
        /// <summary>The <see cref="Owner"/> value on every Demiurge desktop.</summary>
        public const string OwnerValue = "demiurge";
        public const string LegacyOwnerValue = "projects";
        public const string StewardID = "omnipotent.stewardID";
        public const string LegacyStewardID = "omnipotent.projectID";
        public const string AgentID = "omnipotent.agentID";
        public const string Width = "omnipotent.desktop.width";
        public const string Height = "omnipotent.desktop.height";
        /// <summary>Build-context SHA of the image the container was created from. Same key the
        /// image itself carries, so a container and its image can be compared for staleness.</summary>
        public const string ContextHash = "omnipotent.demiurge.context-hash";
        public const string LegacyContextHash = "omnipotent.projects.context-hash";

        public static bool IsOwnedByDemiurge(IDictionary<string, string>? labels) =>
            labels != null && labels.TryGetValue(Owner, out string? owner)
            && (owner == OwnerValue || owner == LegacyOwnerValue);

        /// <summary>A label by its current key, else by the key it had before the rename.</summary>
        public static bool TryGet(IDictionary<string, string>? labels, string key, out string? value)
        {
            value = null;
            if (labels == null) return false;
            if (labels.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value)) return true;
            string? legacy = key switch { StewardID => LegacyStewardID, ContextHash => LegacyContextHash, _ => null };
            return legacy != null && labels.TryGetValue(legacy, out value) && !string.IsNullOrWhiteSpace(value);
        }
    }
}
