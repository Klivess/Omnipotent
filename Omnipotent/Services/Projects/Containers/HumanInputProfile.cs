namespace Omnipotent.Services.Projects.Containers
{
    public enum ScrollStyle
    {
        /// <summary>Discrete wheel notches with clear gaps — a mouse user.</summary>
        Stepped,
        /// <summary>Longer bursts of smaller, faster notches — a trackpad/precision user.</summary>
        Smooth,
    }

    /// <summary>
    /// A per-desktop "persona": the stable, internally-consistent parameters that make one agent's
    /// physical input look like one particular human operating a computer. Everything here is
    /// derived deterministically from a seed key (the container id), so the same desktop types at
    /// the same speed, moves with the same steadiness, and scrolls the same way across every wake —
    /// consistency over time is itself part of looking human, and it avoids the "fleet clustering"
    /// tell where every agent shares one identical behavioural fingerprint.
    ///
    /// The profile carries only <em>parameters</em>. The live pseudo-random stream that turns those
    /// parameters into concrete jittered events lives in <see cref="HumanizedInput"/> so it advances
    /// continuously across actions (re-seeding per action would make every mouse curve identical —
    /// a tell in its own right). Planning is pure and takes an explicit <see cref="Random"/>, which
    /// keeps the humanisation deterministic and unit-testable.
    /// </summary>
    public sealed class HumanInputProfile
    {
        /// <summary>Global kill switch. Set env <c>PROJECTS_HUMANIZE=0</c> (or false/off/no) to
        /// fall back to the raw robotic transport for debugging.</summary>
        public static readonly bool GloballyEnabled = ParseEnabled(
            Environment.GetEnvironmentVariable("PROJECTS_HUMANIZE"));

        /// <summary>Global intensity knob (env <c>PROJECTS_HUMANIZE_INTENSITY</c>, default 1.0,
        /// clamped 0..1). Scales how far input deviates from the robotic baseline: 1 = fully human,
        /// values below 1 tighten timing/jitter, 0 effectively disables humanisation.</summary>
        public static readonly double GlobalIntensity = ParseIntensity(
            Environment.GetEnvironmentVariable("PROJECTS_HUMANIZE_INTENSITY"));

        public double Intensity { get; }
        public bool Enabled => GloballyEnabled && Intensity > 0.02;

        /// <summary>Effective typing speed for this persona in words/min (5 chars = 1 word).</summary>
        public double TypingWpm { get; }
        /// <summary>Fractional standard deviation applied to inter-keystroke timing.</summary>
        public double TypingVariability { get; }
        /// <summary>Probability that a given letter is mistyped and then corrected with backspace.</summary>
        public double TypoRate { get; }
        /// <summary>0..1 — higher means a calmer hand: less path curvature, tremor and overshoot.</summary>
        public double MouseSteadiness { get; }
        /// <summary>Multiplier on pointer-travel duration; some people simply move faster.</summary>
        public double MoveSpeed { get; }
        public ScrollStyle Scroll { get; }

        /// <summary>Seed for the persona's live input RNG (distinct from the parameter-derivation
        /// seed so behaviour and parameters don't correlate).</summary>
        public int LiveSeed { get; }

        private HumanInputProfile(double intensity, double wpm, double typingVariability, double typoRate,
            double steadiness, double moveSpeed, ScrollStyle scroll, int liveSeed)
        {
            Intensity = Math.Clamp(intensity, 0, 1);
            TypingWpm = wpm;
            TypingVariability = typingVariability;
            TypoRate = typoRate;
            MouseSteadiness = steadiness;
            MoveSpeed = moveSpeed;
            Scroll = scroll;
            LiveSeed = liveSeed;
        }

        /// <summary>Builds the stable persona for a desktop from its container id (or any stable
        /// per-account key). Uses the global intensity unless one is supplied for tests.</summary>
        public static HumanInputProfile ForSeed(string? seedKey, double? intensityOverride = null)
        {
            string key = string.IsNullOrWhiteSpace(seedKey) ? "default-desktop" : seedKey!;
            var r = new Random((int)StableHash(key));
            double intensity = Math.Clamp(intensityOverride ?? GlobalIntensity, 0, 1);
            return new HumanInputProfile(
                intensity: intensity,
                wpm: 42 + r.NextDouble() * 46,                 // 42..88 wpm
                typingVariability: 0.18 + r.NextDouble() * 0.22,
                typoRate: 0.006 + r.NextDouble() * 0.020,       // 0.6%..2.6%
                steadiness: 0.35 + r.NextDouble() * 0.50,       // 0.35..0.85
                moveSpeed: 0.85 + r.NextDouble() * 0.45,        // 0.85..1.30x
                scroll: r.NextDouble() < 0.5 ? ScrollStyle.Stepped : ScrollStyle.Smooth,
                liveSeed: (int)StableHash(key + "live"));
        }

        /// <summary>A neutral, deterministic profile for tests.</summary>
        public static HumanInputProfile Test(int seed = 12345, double intensity = 1.0) =>
            new(intensity, wpm: 60, typingVariability: 0.25, typoRate: 0.02,
                steadiness: 0.5, moveSpeed: 1.0, ScrollStyle.Stepped, liveSeed: seed);

        private static bool ParseEnabled(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return true;
            return raw.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");
        }

        private static double ParseIntensity(string? raw) =>
            double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v)
                ? Math.Clamp(v, 0, 1) : 1.0;

        /// <summary>FNV-1a — a process-stable hash (unlike string.GetHashCode, which is randomised
        /// per run and would give a desktop a different persona on every restart).</summary>
        internal static uint StableHash(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in s) { h ^= c; h *= 16777619; }
                return h == 0 ? 1u : h;
            }
        }
    }
}
