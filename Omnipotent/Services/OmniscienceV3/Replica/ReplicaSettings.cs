using System.Globalization;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>
    /// The replica module's settings (namespaced <c>replica.*</c>, defaults in code: an unset key is never persisted, so a
    /// changed default takes effect at once). Read through <see cref="KernelSettings"/> on every use.
    /// </summary>
    public sealed class ReplicaSettings
    {
        private readonly KernelSettings settings;

        public ReplicaSettings(KernelSettings settings) => this.settings = settings;

        public static readonly IReadOnlyList<SettingDescriptor> Descriptors = new[]
        {
            Int("replica.burst_gap_seconds", 180, "Messages by the same person closer than this, with nobody else in between, are one burst.", 10, 3_600),
            Int("replica.stimulus_window_minutes", 30, "Messages by others this long before a burst are what it answered; without any it is an initiation.", 1, 1_440),
            Int("replica.min_replies_experimental", 100, "Replies needed before a replica can be trained (experimental).", 20, 100_000),
            Int("replica.min_replies_ready", 300, "Replies needed for a replica to count as ready.", 50, 100_000),
            Num("replica.holdout_fraction", 0.10, "Share of eligible replies held out for fidelity evaluation (stratified by era and context).", 0.02, 0.3),
            Int("replica.holdout_max", 300, "Upper bound on held-out replies.", 10, 5_000),
            Int("replica.candidates_interactive", 3, "Candidates generated per chat reply (judged; the best is sent).", 1, 8),
            Int("replica.candidates_fidelity", 6, "Candidates per held-out reply during evaluation.", 1, 12),
            Bool("replica.llm_judge", true, "Rank candidates with an LLM judge on the cached prefix (one extra call per reply)."),
            Bool("replica.auto_retrain", true, "Retrain a replica automatically when enough new messages arrive or it gets old."),
            Int("replica.retrain_min_new_messages", 200, "New authored messages that make a replica stale and trigger a retrain.", 10, 100_000),
            Int("replica.retrain_max_age_days", 30, "A replica older than this is retrained when anything new arrived.", 1, 365),
            Bool("replica.autotune", true, "Tune temperature, retrieval depth and burst sampling per person on held-out replies."),
            Int("replica.eval_pairs", 40, "Held-out replies evaluated per version (test part).", 0, 500),
            Int("replica.tune_pairs", 20, "Held-out replies used to tune (tuning part, never reported).", 0, 300),
            Bool("replica.persona_enabled", true, "Build the persona sheet with the LLM (background lane)."),
            Int("replica.persona_slice_max_days", 120, "Longest span one persona slice covers (as-of replicas only see slices that ended).", 14, 730),
            Int("replica.reflex_max_stimuli", 3_000, "Stimuli classified into reflex classes per version (new ones only; labels are cached).", 0, 50_000),
            Int("replica.version_retention", 3, "Versions whose data is kept (serving, previous for rollback, building).", 2, 10),
            Int("replica.max_messages", 150_000, "Most recent authored messages read per person.", 1_000, 2_000_000),
            Int("replica.reserved_output_tokens", 2_048, "Output tokens reserved when packing a reply prompt.", 256, 16_384),
        };

        private static SettingDescriptor Int(string key, int value, string description, double min, double max) =>
            new(key, SettingTypes.Int, value.ToString(CultureInfo.InvariantCulture), description, min, max);

        private static SettingDescriptor Num(string key, double value, string description, double min, double max) =>
            new(key, SettingTypes.Number, value.ToString(CultureInfo.InvariantCulture), description, min, max);

        private static SettingDescriptor Bool(string key, bool value, string description) =>
            new(key, SettingTypes.Bool, value ? "true" : "false", description);

        private int I(string key, int fallback)
        {
            var d = Descriptors.First(x => x.Key == key);
            int v = settings.Get(key, fallback);
            return (int)Math.Clamp(v, d.Min ?? int.MinValue, d.Max ?? int.MaxValue);
        }

        private double D(string key, double fallback)
        {
            var d = Descriptors.First(x => x.Key == key);
            double v = settings.Get(key, fallback);
            return double.IsFinite(v) ? Math.Clamp(v, d.Min ?? double.MinValue, d.Max ?? double.MaxValue) : fallback;
        }

        public TimeSpan BurstGap => TimeSpan.FromSeconds(I("replica.burst_gap_seconds", 180));
        public TimeSpan StimulusWindow => TimeSpan.FromMinutes(I("replica.stimulus_window_minutes", 30));
        public int MinRepliesExperimental => I("replica.min_replies_experimental", 100);
        public int MinRepliesReady => Math.Max(MinRepliesExperimental, I("replica.min_replies_ready", 300));
        public double HoldoutFraction => D("replica.holdout_fraction", 0.10);
        public int HoldoutMax => I("replica.holdout_max", 300);
        public int CandidatesInteractive => I("replica.candidates_interactive", 3);
        public int CandidatesFidelity => I("replica.candidates_fidelity", 6);
        public bool LlmJudge => settings.Get("replica.llm_judge", true);
        public bool AutoRetrain => settings.Get("replica.auto_retrain", true);
        public int RetrainMinNewMessages => I("replica.retrain_min_new_messages", 200);
        public TimeSpan RetrainMaxAge => TimeSpan.FromDays(I("replica.retrain_max_age_days", 30));
        public bool AutoTune => settings.Get("replica.autotune", true);
        public int EvalPairs => I("replica.eval_pairs", 40);
        public int TunePairs => I("replica.tune_pairs", 20);
        public bool PersonaEnabled => settings.Get("replica.persona_enabled", true);
        public TimeSpan PersonaSliceMax => TimeSpan.FromDays(I("replica.persona_slice_max_days", 120));
        public int ReflexMaxStimuli => I("replica.reflex_max_stimuli", 3_000);
        public int VersionRetention => I("replica.version_retention", 3);
        public int MaxMessages => I("replica.max_messages", 150_000);
        public int ReservedOutputTokens => I("replica.reserved_output_tokens", 2_048);
    }
}
