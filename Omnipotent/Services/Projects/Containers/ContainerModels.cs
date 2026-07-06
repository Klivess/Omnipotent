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
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 800;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>Set when the container was found missing/dead during reconciliation.</summary>
        public bool Lost { get; set; }
    }

    /// <summary>Docker labels used to recognise our containers during reconciliation.</summary>
    public static class ContainerLabels
    {
        public const string Owner = "omnipotent.owner";          // always "projects"
        public const string ProjectID = "omnipotent.projectID";
        public const string AgentID = "omnipotent.agentID";
    }
}
