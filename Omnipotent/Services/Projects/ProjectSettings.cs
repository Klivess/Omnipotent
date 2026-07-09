namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Per-project settings — Projects' OWN setting system, deliberately NOT OmniSettings. Each
    /// project carries its own model routing, triage models, and behavior toggles, so one project
    /// can run a cheap text-only fleet while another runs top-tier vision agents, without any
    /// global config coupling them. (Budget/cap live on the Project record; these are the
    /// behavioral knobs.) Seeded with defaults at creation; editable by Klives (website) and, for
    /// model routing, by the Commander to economize.
    /// </summary>
    public class ProjectSettings
    {
        public string ProjectID { get; set; } = "";

        // ── model routing ──
        /// <summary>The Commander's model — the smartest one the project can afford.</summary>
        public string CommanderModel { get; set; } = Defaults.CommanderModel;
        /// <summary>Cheap model for utility work (digest compaction, reports, triage fallback).</summary>
        public string UtilityModel { get; set; } = Defaults.UtilityModel;
        /// <summary>Tier → model map (§6.1). The tier list doubles as a price list.</summary>
        public string TierTextModel { get; set; } = Defaults.TierTextModel;
        public string TierTextImageModel { get; set; } = Defaults.TierTextImageModel;
        public string TierTextImageVideoModel { get; set; } = Defaults.TierTextImageVideoModel;
        public string TierTextImageVideoAudioModel { get; set; } = Defaults.TierTextImageVideoAudioModel;

        // ── stimulus triage ──
        /// <summary>Free omni model for stimulus triage (§5.3).</summary>
        public string StimulusFreeModel { get; set; } = Defaults.StimulusFreeModel;
        /// <summary>Cheap paid model that steps in when the free tier throttles.</summary>
        public string StimulusFallbackModel { get; set; } = Defaults.StimulusFallbackModel;

        // ── behavior ──
        /// <summary>Whether video-tier agents get screenshots fed back to the model.</summary>
        public bool VisionEnabled { get; set; } = true;
        /// <summary>Whether this project may spin up desktop containers at all (text-only projects: false).</summary>
        public bool ContainersEnabled { get; set; } = false;
        /// <summary>Desktop image for this project's containers.</summary>
        public string DesktopImage { get; set; } = Defaults.DesktopImage;
        /// <summary>Post-action visual settle delay for this project's VNC desktops.</summary>
        public int ComputerActionSettleMs { get; set; } = Defaults.ComputerActionSettleMs;
        /// <summary>Delay between VNC text keystrokes; slower values help fragile web UIs.</summary>
        public int ComputerTypingDelayMs { get; set; } = Defaults.ComputerTypingDelayMs;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public string ModelForTier(ProjectAgentTier tier) => tier switch
        {
            ProjectAgentTier.Text => TierTextModel,
            ProjectAgentTier.TextImage => TierTextImageModel,
            ProjectAgentTier.TextImageVideo => TierTextImageVideoModel,
            ProjectAgentTier.TextImageVideoAudio => TierTextImageVideoAudioModel,
            _ => TierTextModel,
        };

        /// <summary>Applies a named setting from a string value (Klives/Commander edit). Returns false if unknown.</summary>
        public bool TrySet(string key, string value)
        {
            switch (key.Trim().ToLowerInvariant())
            {
                case "commandermodel": CommanderModel = value; break;
                case "utilitymodel": UtilityModel = value; break;
                case "tiertextmodel": TierTextModel = value; break;
                case "tiertextimagemodel": TierTextImageModel = value; break;
                case "tiertextimagevideomodel": TierTextImageVideoModel = value; break;
                case "tiertextimagevideoaudiomodel": TierTextImageVideoAudioModel = value; break;
                case "stimulusfreemodel": StimulusFreeModel = value; break;
                case "stimulusfallbackmodel": StimulusFallbackModel = value; break;
                case "visionenabled": VisionEnabled = ParseBool(value); break;
                case "containersenabled": ContainersEnabled = ParseBool(value); break;
                case "desktopimage": DesktopImage = value; break;
                case "computeractionsettlems": ComputerActionSettleMs = Math.Clamp(ParseInt(value, Defaults.ComputerActionSettleMs), 50, 5000); break;
                case "computertypingdelayms": ComputerTypingDelayMs = Math.Clamp(ParseInt(value, Defaults.ComputerTypingDelayMs), 0, 500); break;
                default: return false;
            }
            return true;
        }

        private static bool ParseBool(string v) => v.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";
        private static int ParseInt(string v, int fallback) => int.TryParse(v, out var parsed) ? parsed : fallback;

        public static class Defaults
        {
            public const string CommanderModel = "anthropic/claude-sonnet-4.5";
            public const string UtilityModel = "openai/gpt-4.1-mini";
            public const string TierTextModel = "openai/gpt-4.1-mini";
            public const string TierTextImageModel = "openai/gpt-4.1";
            public const string TierTextImageVideoModel = "anthropic/claude-sonnet-4.5";
            public const string TierTextImageVideoAudioModel = "google/gemini-2.5-pro";
            public const string StimulusFreeModel = "openai/gpt-4.1-mini";
            public const string StimulusFallbackModel = "openai/gpt-4.1-mini";
            public const string DesktopImage = "omnipotent/projects-desktop:latest";
            public const int ComputerActionSettleMs = 350;
            public const int ComputerTypingDelayMs = 18;
        }
    }
}
