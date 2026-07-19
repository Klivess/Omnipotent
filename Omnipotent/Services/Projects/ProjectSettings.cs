using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;

namespace Omnipotent.Services.Projects
{
    /// <summary>Independent per-project routing and behavior settings.</summary>
    public class ProjectSettings
    {
        public string ProjectID { get; set; } = "";

        // Ordered, explicit routes. Index 0 is preferred; later entries are attempted in order.
        // A route never borrows a model from another role.
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> CommanderRoutes { get; set; } = [Defaults.CommanderModel];
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> UtilityRoutes { get; set; } = [Defaults.UtilityModel];
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> CouncilRoutes { get; set; } = [Defaults.CouncilModel];
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> TierTextRoutes { get; set; } = [Defaults.TierTextModel];
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> TierTextImageRoutes { get; set; } = [Defaults.TierTextImageModel];
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> TierTextImageVideoRoutes { get; set; } = [Defaults.TierTextImageVideoModel];
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> TierTextImageVideoAudioRoutes { get; set; } = [Defaults.TierTextImageVideoAudioModel];
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> StimulusFreeRoutes { get; set; } = [Defaults.StimulusFreeModel];
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> StimulusFallbackRoutes { get; set; } = [Defaults.StimulusFallbackModel];

        // Scalar compatibility facades migrate old documents and API clients. A save emits only
        // the arrays above. Legacy hidden fallback properties are ignored by Json.NET because they
        // no longer exist, so an old implicit GPT fallback can never enter a new route list.
        [JsonProperty("CommanderModel")] public string CommanderModel { get => First(CommanderRoutes); set => SetPrimary(CommanderRoutes, value); }
        [JsonProperty("UtilityModel")] public string UtilityModel { get => First(UtilityRoutes); set => SetPrimary(UtilityRoutes, value); }
        [JsonProperty("CouncilModel")] public string CouncilModel { get => First(CouncilRoutes); set => SetPrimary(CouncilRoutes, value); }
        [JsonProperty("TierTextModel")] public string TierTextModel { get => First(TierTextRoutes); set => SetPrimary(TierTextRoutes, value); }
        [JsonProperty("TierTextImageModel")] public string TierTextImageModel { get => First(TierTextImageRoutes); set => SetPrimary(TierTextImageRoutes, value); }
        [JsonProperty("TierTextImageVideoModel")] public string TierTextImageVideoModel { get => First(TierTextImageVideoRoutes); set => SetPrimary(TierTextImageVideoRoutes, value); }
        [JsonProperty("TierTextImageVideoAudioModel")] public string TierTextImageVideoAudioModel { get => First(TierTextImageVideoAudioRoutes); set => SetPrimary(TierTextImageVideoAudioRoutes, value); }
        [JsonProperty("StimulusFreeModel")] public string StimulusFreeModel { get => First(StimulusFreeRoutes); set => SetPrimary(StimulusFreeRoutes, value); }
        [JsonProperty("StimulusFallbackModel")] public string StimulusFallbackModel { get => First(StimulusFallbackRoutes); set => SetPrimary(StimulusFallbackRoutes, value); }

        public bool ShouldSerializeCommanderModel() => false;
        public bool ShouldSerializeUtilityModel() => false;
        public bool ShouldSerializeCouncilModel() => false;
        public bool ShouldSerializeTierTextModel() => false;
        public bool ShouldSerializeTierTextImageModel() => false;
        public bool ShouldSerializeTierTextImageVideoModel() => false;
        public bool ShouldSerializeTierTextImageVideoAudioModel() => false;
        public bool ShouldSerializeStimulusFreeModel() => false;
        public bool ShouldSerializeStimulusFallbackModel() => false;

        public int CommanderMaxOutputTokens { get; set; } = Defaults.CommanderMaxOutputTokens;
        public int SubAgentMaxOutputTokens { get; set; } = Defaults.SubAgentMaxOutputTokens;
        public int UtilityMaxOutputTokens { get; set; } = Defaults.UtilityMaxOutputTokens;

        public int WorkSliceToolCalls { get; set; } = Defaults.WorkSliceToolCalls;
        public int WorkSliceModelTurns { get; set; } = Defaults.WorkSliceModelTurns;
        public int WorkSliceTokenBudget { get; set; } = Defaults.WorkSliceTokenBudget;
        public int MaxConvergenceTripsPerSlice { get; set; } = Defaults.MaxConvergenceTripsPerSlice;

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

        public bool VisionEnabled { get; set; } = true;
        public bool ContainersEnabled { get; set; } = Defaults.ContainersEnabled;
        public bool DesktopFirstWebsiteInteraction { get; set; } = Defaults.DesktopFirstWebsiteInteraction;
        public string DesktopImage { get; set; } = Defaults.DesktopImage;
        public int ComputerActionSettleMs { get; set; } = Defaults.ComputerActionSettleMs;
        public int ComputerTypingDelayMs { get; set; } = Defaults.ComputerTypingDelayMs;
        public int CouncilMaxPerDay { get; set; } = Defaults.CouncilMaxPerDay;
        public int CouncilMaxPerWake { get; set; } = Defaults.CouncilMaxPerWake;
        public double CouncilMaxCostUsd { get; set; } = Defaults.CouncilMaxCostUsd;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public IReadOnlyList<string> RoutesForTier(ProjectAgentTier tier) => tier switch
        {
            ProjectAgentTier.Text => TierTextRoutes,
            ProjectAgentTier.TextImage => TierTextImageRoutes,
            ProjectAgentTier.TextImageVideo => TierTextImageVideoRoutes,
            ProjectAgentTier.TextImageVideoAudio => TierTextImageVideoAudioRoutes,
            _ => TierTextRoutes,
        };

        public string ModelForTier(ProjectAgentTier tier) => First(RoutesForTier(tier));

        public void NormalizeRoutes()
        {
            CommanderRoutes = Normalize(CommanderRoutes, Defaults.CommanderModel);
            UtilityRoutes = Normalize(UtilityRoutes, Defaults.UtilityModel);
            CouncilRoutes = Normalize(CouncilRoutes, Defaults.CouncilModel);
            TierTextRoutes = Normalize(TierTextRoutes, Defaults.TierTextModel);
            TierTextImageRoutes = Normalize(TierTextImageRoutes, Defaults.TierTextImageModel);
            TierTextImageVideoRoutes = Normalize(TierTextImageVideoRoutes, Defaults.TierTextImageVideoModel);
            TierTextImageVideoAudioRoutes = Normalize(TierTextImageVideoAudioRoutes, Defaults.TierTextImageVideoAudioModel);
            StimulusFreeRoutes = Normalize(StimulusFreeRoutes, Defaults.StimulusFreeModel);
            StimulusFallbackRoutes = Normalize(StimulusFallbackRoutes, Defaults.StimulusFallbackModel);

            // Migrate the original small-slice triplet as a unit. Those values were emitted into
            // every existing project settings file, so changing only the hardcoded defaults would
            // leave old projects stuck on the defective 40-call/24-turn/64k policy forever. A
            // project with any customised value is left untouched.
            if (WorkSliceToolCalls == 40 && WorkSliceModelTurns == 24 && WorkSliceTokenBudget == 64_000)
            {
                WorkSliceToolCalls = Defaults.WorkSliceToolCalls;
                WorkSliceModelTurns = Defaults.WorkSliceModelTurns;
                WorkSliceTokenBudget = Defaults.WorkSliceTokenBudget;
            }
        }

        public bool TrySet(string key, string value) => TrySet(key, new JValue(value));

        public bool TrySet(string key, JToken value)
        {
            switch (key.Trim().ToLowerInvariant())
            {
                case "commanderroutes": return TryReplaceRoutes(value, routes => CommanderRoutes = routes);
                case "utilityroutes": return TryReplaceRoutes(value, routes => UtilityRoutes = routes);
                case "councilroutes": return TryReplaceRoutes(value, routes => CouncilRoutes = routes);
                case "tiertextroutes": return TryReplaceRoutes(value, routes => TierTextRoutes = routes);
                case "tiertextimageroutes": return TryReplaceRoutes(value, routes => TierTextImageRoutes = routes);
                case "tiertextimagevideoroutes": return TryReplaceRoutes(value, routes => TierTextImageVideoRoutes = routes);
                case "tiertextimagevideoaudioroutes": return TryReplaceRoutes(value, routes => TierTextImageVideoAudioRoutes = routes);
                case "stimulusfreeroutes": return TryReplaceRoutes(value, routes => StimulusFreeRoutes = routes);
                case "stimulusfallbackroutes": return TryReplaceRoutes(value, routes => StimulusFallbackRoutes = routes);
                case "commandermodel": CommanderModel = Text(value); break;
                case "utilitymodel": UtilityModel = Text(value); break;
                case "councilmodel": CouncilModel = Text(value); break;
                case "tiertextmodel": TierTextModel = Text(value); break;
                case "tiertextimagemodel": TierTextImageModel = Text(value); break;
                case "tiertextimagevideomodel": TierTextImageVideoModel = Text(value); break;
                case "tiertextimagevideoaudiomodel": TierTextImageVideoAudioModel = Text(value); break;
                case "stimulusfreemodel": StimulusFreeModel = Text(value); break;
                case "stimulusfallbackmodel": StimulusFallbackModel = Text(value); break;
                // Retired hidden fallback keys are accepted as no-ops so old clients cannot
                // accidentally resurrect them and do not fail an otherwise valid patch.
                case "commanderfallbackmodel": case "subagentfallbackmodel": case "utilityfallbackmodel":
                case "councilfallbackmodel": case "automaticmodelfallbackenabled": return true;
                case "councilmaxperday": CouncilMaxPerDay = Math.Clamp(ParseInt(Text(value), Defaults.CouncilMaxPerDay), 0, 24); break;
                case "councilmaxperwake": CouncilMaxPerWake = Math.Clamp(ParseInt(Text(value), Defaults.CouncilMaxPerWake), 0, 5); break;
                case "councilmaxcostusd": CouncilMaxCostUsd = Math.Clamp(ParseDouble(Text(value), Defaults.CouncilMaxCostUsd), 0.01, 5); break;
                case "commandermaxoutputtokens": CommanderMaxOutputTokens = Math.Clamp(ParseInt(Text(value), Defaults.CommanderMaxOutputTokens), 512, 32768); break;
                case "subagentmaxoutputtokens": SubAgentMaxOutputTokens = Math.Clamp(ParseInt(Text(value), Defaults.SubAgentMaxOutputTokens), 512, 32768); break;
                case "utilitymaxoutputtokens": UtilityMaxOutputTokens = Math.Clamp(ParseInt(Text(value), Defaults.UtilityMaxOutputTokens), 256, 8192); break;
                case "workslicetoolcalls": case "maxtoolcallsperwake":
                    WorkSliceToolCalls = Math.Clamp(ParseInt(Text(value), Defaults.WorkSliceToolCalls), 0, 2_000); break;
                case "workslicemodelturns": case "maxmodelturnsperwake":
                    WorkSliceModelTurns = Math.Clamp(ParseInt(Text(value), Defaults.WorkSliceModelTurns), 0, 1_000); break;
                case "workslicetokenbudget":
                    WorkSliceTokenBudget = Math.Clamp(ParseInt(Text(value), Defaults.WorkSliceTokenBudget), 16_000, 2_000_000); break;
                case "maxconvergencetripsperslice": case "maxlooptripsperwake":
                    MaxConvergenceTripsPerSlice = Math.Clamp(ParseInt(Text(value), Defaults.MaxConvergenceTripsPerSlice), 1, 20); break;
                case "maxconsecutivecontinuations": break;
                case "visionenabled": VisionEnabled = ParseBool(Text(value)); break;
                case "containersenabled": ContainersEnabled = ParseBool(Text(value)); break;
                case "desktopfirstwebsiteinteraction": DesktopFirstWebsiteInteraction = ParseBool(Text(value)); break;
                case "desktopimage": DesktopImage = Text(value); break;
                case "computeractionsettlems": ComputerActionSettleMs = Math.Clamp(ParseInt(Text(value), Defaults.ComputerActionSettleMs), 50, 5000); break;
                case "computertypingdelayms": ComputerTypingDelayMs = Math.Clamp(ParseInt(Text(value), Defaults.ComputerTypingDelayMs), 0, 500); break;
                default: return false;
            }
            return true;
        }

        private static bool TryReplaceRoutes(JToken value, Action<List<string>> replace)
        {
            IEnumerable<string?> values = value.Type == JTokenType.Array
                ? value.Values<string?>()
                : Text(value).Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
            var normalized = Normalize(values, "", permitFallback: false);
            if (normalized.Count == 0) return false;
            replace(normalized);
            return true;
        }

        private static List<string> Normalize(IEnumerable<string?>? routes, string fallback, bool permitFallback = true)
        {
            var result = (routes ?? []).Select(x => (x ?? "").Trim())
                .Where(x => x.Length is > 0 and <= 300)
                // One primary (`model`) plus OpenRouter's three native `models` fallback entries.
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
            if (result.Count == 0 && permitFallback && !string.IsNullOrWhiteSpace(fallback)) result.Add(fallback);
            return result;
        }

        private static string First(IEnumerable<string>? routes) => routes?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";
        private static void SetPrimary(List<string> routes, string? value)
        {
            string model = (value ?? "").Trim();
            if (model.Length == 0) return;
            if (routes.Count == 0) routes.Add(model); else routes[0] = model;
        }
        private static string Text(JToken value) => value.Type == JTokenType.String ? value.Value<string>() ?? "" : value.ToString();
        private static bool ParseBool(string v) => v.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";
        private static int ParseInt(string v, int fallback) => int.TryParse(v, out var parsed) ? parsed : fallback;
        private static double ParseDouble(string v, double fallback) =>
            double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

        public static class Defaults
        {
            public const string CommanderModel = "anthropic/claude-sonnet-4.5";
            public const string UtilityModel = "openai/gpt-4.1-mini";
            public const string CouncilModel = CommanderModel;
            public const string TierTextModel = "openai/gpt-4.1-mini";
            public const string TierTextImageModel = "openai/gpt-4.1";
            public const string TierTextImageVideoModel = "anthropic/claude-sonnet-4.5";
            public const string TierTextImageVideoAudioModel = "google/gemini-2.5-pro";
            public const string StimulusFreeModel = "openai/gpt-4.1-mini";
            public const string StimulusFallbackModel = "openai/gpt-4.1-mini";
            public const int CouncilMaxPerDay = 6;
            public const int CouncilMaxPerWake = 2;
            public const double CouncilMaxCostUsd = 0.10;
            public const int CommanderMaxOutputTokens = 8192;
            public const int SubAgentMaxOutputTokens = 6144;
            public const int UtilityMaxOutputTokens = 1800;
            // Zero disables arbitrary call/turn rollover. The measured live context is the primary
            // boundary; convergence, budget and cancellation guards still stop unproductive work.
            public const int WorkSliceToolCalls = 0;
            public const int WorkSliceModelTurns = 0;
            // Leaves an output/tool-result reserve for common 200k-class routes while retaining far
            // more history than the legacy 64k setting. Larger-window routes can opt higher per project.
            public const int WorkSliceTokenBudget = 180_000;
            public const int MaxConvergenceTripsPerSlice = 5;
            public const bool ContainersEnabled = true;
            public const bool DesktopFirstWebsiteInteraction = true;
            public const string DesktopImage = "omnipotent/projects-desktop:latest";
            public const int ComputerActionSettleMs = 350;
            public const int ComputerTypingDelayMs = 18;
        }
    }
}
