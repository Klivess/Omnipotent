namespace Omnipotent.Services.Demiurge.Containers
{
    /// <summary>
    /// Host-level container plumbing. Per-Steward behavior (whether a Steward uses containers,
    /// which image) lives in StewardSettings — NOT here and NOT in OmniSettings. The only thing
    /// here is the single Docker daemon endpoint (there is exactly one daemon on the host), which
    /// is host infrastructure, resolved from an env var with a sensible default.
    /// </summary>
    public static class StewardContainerConfig
    {
        public const string DockerUriDefault = "npipe://./pipe/docker_engine";
        public const int DefaultStreamFps = 6;

        // Desktop framebuffer geometry. Full-HD by default so agents (and Klives' live view) see
        // far more of a real desktop than the former 1280x800. Host-overridable without a rebuild.
        public const int DefaultDesktopWidth = 1920;
        public const int DefaultDesktopHeight = 1080;

        /// <summary>The Docker daemon endpoint, overridable via the DEMIURGE_DOCKER_URI env var (or its pre-rename PROJECTS_DOCKER_URI).</summary>
        public static string ResolveDockerUri()
        {
            string? fromEnv = DemiurgeLegacyNames.Environment("DOCKER_URI");
            return string.IsNullOrWhiteSpace(fromEnv) ? DockerUriDefault : fromEnv;
        }

        /// <summary>Desktop width in pixels, overridable via DEMIURGE_DESKTOP_WIDTH (clamped 640–3840).</summary>
        public static int ResolveDesktopWidth() => ResolveDimension("DESKTOP_WIDTH", DefaultDesktopWidth);

        /// <summary>Desktop height in pixels, overridable via DEMIURGE_DESKTOP_HEIGHT (clamped 480–2160).</summary>
        public static int ResolveDesktopHeight() => ResolveDimension("DESKTOP_HEIGHT", DefaultDesktopHeight);

        /// <summary>DEMIURGE_<paramref name="suffix"/>, else the PROJECTS_ name it had before the rename.</summary>
        private static int ResolveDimension(string suffix, int fallback)
        {
            string? fromEnv = DemiurgeLegacyNames.Environment(suffix);
            return int.TryParse(fromEnv, out int px) && px >= 480 && px <= 3840 ? px : fallback;
        }
    }
}
