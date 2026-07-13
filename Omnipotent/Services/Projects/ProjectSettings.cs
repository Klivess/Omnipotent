using Newtonsoft.Json;

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
        /// <summary>Model for adversarial council panelists + Chair. Defaults to the Commander's model —
        /// councils fire rarely and only at high-stakes moments, so a weak panel defeats the purpose.</summary>
        public string CouncilModel { get; set; } = Defaults.CouncilModel;
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

        // ── bounded project turns / fallback routing ──
        /// <summary>
        /// Explicit output caps for Projects calls. Project agents must never inherit the global
        /// provider maximum: a global 64K setting can turn a routine wake into an unaffordable
        /// request even while the project's own budget is healthy.
        /// </summary>
        public int CommanderMaxOutputTokens { get; set; } = Defaults.CommanderMaxOutputTokens;
        public int SubAgentMaxOutputTokens { get; set; } = Defaults.SubAgentMaxOutputTokens;
        public int UtilityMaxOutputTokens { get; set; } = Defaults.UtilityMaxOutputTokens;
        /// <summary>Capability-compatible fallback used after the primary route fails.</summary>
        public string CommanderFallbackModel { get; set; } = Defaults.CommanderFallbackModel;
        /// <summary>Capability-compatible fallback for worker tiers.</summary>
        public string SubAgentFallbackModel { get; set; } = Defaults.SubAgentFallbackModel;
        /// <summary>Utility-model fallback.</summary>
        public string UtilityFallbackModel { get; set; } = Defaults.UtilityFallbackModel;
        public string CouncilFallbackModel { get; set; } = Defaults.CouncilFallbackModel;
        /// <summary>
        /// Keeps old settings documents resilient: historical projects persisted empty fallback
        /// fields, so an unavailable or unaffordable primary model could halt the whole project.
        /// When enabled, an empty/same-as-primary route is replaced at runtime by another configured
        /// project model. Set this false to deliberately require only explicitly configured routes.
        /// </summary>
        public bool AutomaticModelFallbackEnabled { get; set; } = Defaults.AutomaticModelFallbackEnabled;

        // ── wake convergence guardrails ──
        /// <summary>Context-rollover boundary, not a work limit. Productive work automatically
        /// continues in a fresh wake with a durable resume checkpoint.</summary>
        public int WorkSliceToolCalls { get; set; } = Defaults.WorkSliceToolCalls;
        /// <summary>Model-turn boundary for refreshing context, not a lifetime/wake cap.</summary>
        public int WorkSliceModelTurns { get; set; } = Defaults.WorkSliceModelTurns;
        /// <summary>Measured prompt+completion-token boundary for refreshing context. This keeps
        /// repeated full-history requests from turning one wake into a million-token session;
        /// productive work continues from its typed resume checkpoint in a fresh wake.</summary>
        public int WorkSliceTokenBudget { get; set; } = Defaults.WorkSliceTokenBudget;
        /// <summary>Only convergence failures are bounded: repeated identical actions must change
        /// strategy instead of consuming an unlimited budget. Novel productive work is unlimited.</summary>
        public int MaxConvergenceTripsPerSlice { get; set; } = Defaults.MaxConvergenceTripsPerSlice;

        // Read old persisted setting documents without re-emitting retired hard-cap fields.
        [JsonProperty("MaxToolCallsPerWake", NullValueHandling = NullValueHandling.Ignore)]
        private int? LegacyMaxToolCallsPerWake
        {
            get => null;
            set { if (value.HasValue) WorkSliceToolCalls = Math.Clamp(value.Value, 5, 200); }
        }
        [JsonProperty("MaxModelTurnsPerWake", NullValueHandling = NullValueHandling.Ignore)]
        private int? LegacyMaxModelTurnsPerWake
        {
            get => null;
            set { if (value.HasValue) WorkSliceModelTurns = Math.Clamp(value.Value, 2, 100); }
        }
        [JsonProperty("MaxLoopTripsPerWake", NullValueHandling = NullValueHandling.Ignore)]
        private int? LegacyMaxLoopTripsPerWake
        {
            get => null;
            set { if (value.HasValue) MaxConvergenceTripsPerSlice = Math.Clamp(value.Value, 1, 20); }
        }

        // ── behavior ──
        /// <summary>Whether video-tier agents get screenshots fed back to the model.</summary>
        public bool VisionEnabled { get; set; } = true;
        /// <summary>Whether this project may spin up desktop containers at all. Projects are
        /// operators by default, so their computers are enabled unless Klives explicitly creates
        /// a text-only project.</summary>
        public bool ContainersEnabled { get; set; } = Defaults.ContainersEnabled;
        /// <summary>
        /// Enforces visible, stateful computer_* interaction for websites. Scripts and terminals
        /// remain available for installs, files, diagnostics and software work, but may not become
        /// a hidden headless-browser substitute for operating an external account.
        /// </summary>
        public bool DesktopFirstWebsiteInteraction { get; set; } = Defaults.DesktopFirstWebsiteInteraction;
        /// <summary>Desktop image for this project's containers.</summary>
        public string DesktopImage { get; set; } = Defaults.DesktopImage;
        /// <summary>Post-action visual settle delay for this project's VNC desktops.</summary>
        public int ComputerActionSettleMs { get; set; } = Defaults.ComputerActionSettleMs;
        /// <summary>Delay between VNC text keystrokes; slower values help fragile web UIs.</summary>
        public int ComputerTypingDelayMs { get; set; } = Defaults.ComputerTypingDelayMs;

        // ── councils ──
        /// <summary>Max adversarial councils the Commander may convene per calendar day (cost guardrail).</summary>
        public int CouncilMaxPerDay { get; set; } = Defaults.CouncilMaxPerDay;
        /// <summary>Max councils per single Commander wake — blocks a council-happy loop.</summary>
        public int CouncilMaxPerWake { get; set; } = Defaults.CouncilMaxPerWake;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public string ModelForTier(ProjectAgentTier tier) => tier switch
        {
            ProjectAgentTier.Text => TierTextModel,
            ProjectAgentTier.TextImage => TierTextImageModel,
            ProjectAgentTier.TextImageVideo => TierTextImageVideoModel,
            ProjectAgentTier.TextImageVideoAudio => TierTextImageVideoAudioModel,
            _ => TierTextModel,
        };

        public string CommanderFallbackRoute() => ResolveFallback(
            CommanderFallbackModel, CommanderModel, UtilityModel, TierTextImageModel, TierTextModel);

        public string CouncilFallbackRoute() => ResolveFallback(
            CouncilFallbackModel, CouncilModel, UtilityModel, TierTextImageModel, TierTextModel);

        public string UtilityFallbackRoute() => ResolveFallback(
            UtilityFallbackModel, UtilityModel, CommanderModel, TierTextImageModel, TierTextModel);

        public string SubAgentFallbackRoute(ProjectAgentTier tier) => ResolveFallback(
            SubAgentFallbackModel, ModelForTier(tier), UtilityModel, CommanderModel,
            TierTextImageModel, TierTextImageVideoModel, TierTextModel);

        private string ResolveFallback(string? configured, string? primary, params string?[] alternatives)
        {
            string primaryRoute = (primary ?? "").Trim();
            string explicitRoute = (configured ?? "").Trim();
            if (explicitRoute.Length > 0 && !string.Equals(explicitRoute, primaryRoute, StringComparison.OrdinalIgnoreCase))
                return explicitRoute;
            if (!AutomaticModelFallbackEnabled) return "";
            return alternatives.Select(x => (x ?? "").Trim())
                .FirstOrDefault(x => x.Length > 0 && !string.Equals(x, primaryRoute, StringComparison.OrdinalIgnoreCase)) ?? "";
        }

        /// <summary>Applies a named setting from a string value (Klives/Commander edit). Returns false if unknown.</summary>
        public bool TrySet(string key, string value)
        {
            switch (key.Trim().ToLowerInvariant())
            {
                case "commandermodel": CommanderModel = value; break;
                case "utilitymodel": UtilityModel = value; break;
                case "councilmodel": CouncilModel = value; break;
                case "councilmaxperday": CouncilMaxPerDay = Math.Clamp(ParseInt(value, Defaults.CouncilMaxPerDay), 0, 24); break;
                case "councilmaxperwake": CouncilMaxPerWake = Math.Clamp(ParseInt(value, Defaults.CouncilMaxPerWake), 0, 5); break;
                case "tiertextmodel": TierTextModel = value; break;
                case "tiertextimagemodel": TierTextImageModel = value; break;
                case "tiertextimagevideomodel": TierTextImageVideoModel = value; break;
                case "tiertextimagevideoaudiomodel": TierTextImageVideoAudioModel = value; break;
                case "stimulusfreemodel": StimulusFreeModel = value; break;
                case "stimulusfallbackmodel": StimulusFallbackModel = value; break;
                case "commandermaxoutputtokens": CommanderMaxOutputTokens = Math.Clamp(ParseInt(value, Defaults.CommanderMaxOutputTokens), 512, 32768); break;
                case "subagentmaxoutputtokens": SubAgentMaxOutputTokens = Math.Clamp(ParseInt(value, Defaults.SubAgentMaxOutputTokens), 512, 32768); break;
                case "utilitymaxoutputtokens": UtilityMaxOutputTokens = Math.Clamp(ParseInt(value, Defaults.UtilityMaxOutputTokens), 256, 8192); break;
                case "commanderfallbackmodel": CommanderFallbackModel = value.Trim(); break;
                case "subagentfallbackmodel": SubAgentFallbackModel = value.Trim(); break;
                case "utilityfallbackmodel": UtilityFallbackModel = value.Trim(); break;
                case "councilfallbackmodel": CouncilFallbackModel = value.Trim(); break;
                case "automaticmodelfallbackenabled": AutomaticModelFallbackEnabled = ParseBool(value); break;
                case "workslicetoolcalls":
                case "maxtoolcallsperwake": // legacy setting name: now interpreted as a rollover boundary
                    WorkSliceToolCalls = Math.Clamp(ParseInt(value, Defaults.WorkSliceToolCalls), 5, 200); break;
                case "workslicemodelturns":
                case "maxmodelturnsperwake": // legacy setting name: now interpreted as a rollover boundary
                    WorkSliceModelTurns = Math.Clamp(ParseInt(value, Defaults.WorkSliceModelTurns), 2, 100); break;
                case "workslicetokenbudget":
                    WorkSliceTokenBudget = Math.Clamp(ParseInt(value, Defaults.WorkSliceTokenBudget), 16_000, 256_000); break;
                case "maxconvergencetripsperslice":
                case "maxlooptripsperwake":
                    MaxConvergenceTripsPerSlice = Math.Clamp(ParseInt(value, Defaults.MaxConvergenceTripsPerSlice), 1, 20); break;
                case "maxconsecutivecontinuations": break; // retired: productive continuations are intentionally unlimited
                case "visionenabled": VisionEnabled = ParseBool(value); break;
                case "containersenabled": ContainersEnabled = ParseBool(value); break;
                case "desktopfirstwebsiteinteraction": DesktopFirstWebsiteInteraction = ParseBool(value); break;
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
            public const string CouncilModel = CommanderModel;
            public const int CouncilMaxPerDay = 6;
            public const int CouncilMaxPerWake = 2;
            public const string TierTextModel = "openai/gpt-4.1-mini";
            public const string TierTextImageModel = "openai/gpt-4.1";
            public const string TierTextImageVideoModel = "anthropic/claude-sonnet-4.5";
            public const string TierTextImageVideoAudioModel = "google/gemini-2.5-pro";
            public const string StimulusFreeModel = "openai/gpt-4.1-mini";
            public const string StimulusFallbackModel = "openai/gpt-4.1-mini";
            public const int CommanderMaxOutputTokens = 8192;
            public const int SubAgentMaxOutputTokens = 6144;
            public const int UtilityMaxOutputTokens = 1800;
            public const string CommanderFallbackModel = UtilityModel;
            public const string SubAgentFallbackModel = UtilityModel;
            public const string UtilityFallbackModel = CommanderModel;
            public const string CouncilFallbackModel = UtilityModel;
            public const bool AutomaticModelFallbackEnabled = true;
            public const int WorkSliceToolCalls = 40;
            public const int WorkSliceModelTurns = 24;
            public const int WorkSliceTokenBudget = 64_000;
            public const int MaxConvergenceTripsPerSlice = 5;
            public const bool ContainersEnabled = true;
            public const bool DesktopFirstWebsiteInteraction = true;
            public const string DesktopImage = "omnipotent/projects-desktop:latest";
            public const int ComputerActionSettleMs = 350;
            public const int ComputerTypingDelayMs = 18;
        }
    }
}
