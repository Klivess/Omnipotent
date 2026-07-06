namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Host-level container plumbing. Per-project behavior (whether a project uses containers,
    /// which image) lives in ProjectSettings — NOT here and NOT in OmniSettings. The only thing
    /// here is the single Docker daemon endpoint (there is exactly one daemon on the host), which
    /// is host infrastructure, resolved from an env var with a sensible default.
    /// </summary>
    public static class ProjectContainerConfig
    {
        public const string DockerUriDefault = "npipe://./pipe/docker_engine";
        public const int DefaultStreamFps = 6;

        /// <summary>The Docker daemon endpoint, overridable via the PROJECTS_DOCKER_URI env var.</summary>
        public static string ResolveDockerUri()
        {
            string? fromEnv = Environment.GetEnvironmentVariable("PROJECTS_DOCKER_URI");
            return string.IsNullOrWhiteSpace(fromEnv) ? DockerUriDefault : fromEnv;
        }
    }
}
