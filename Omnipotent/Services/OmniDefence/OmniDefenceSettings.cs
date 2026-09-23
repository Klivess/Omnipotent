using Newtonsoft.Json;
using Omnipotent.Services.OmniDefence.Fingerprint;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// Durable OmniDefence settings, stored as one JSON document in <c>od_settings</c>.
    /// Before this existed the thresholds lived only in memory and silently reset on restart.
    /// </summary>
    public sealed class OmniDefenceSettings
    {
        public const string StorageKey = "settings";

        // Threat-score escalation (mirrored onto IpThreatTracker).
        public int AutoWatchScore = 50;
        public int AutoBlockScore = 200;
        public int Escalation2 = 50;
        public int Escalation3 = 200;

        // Fingerprinting
        public bool FingerprintEnabled = true;
        /// <summary>Add a one-off threat-score delta when an IP transitions into a malicious class.</summary>
        public bool ClassScoreDeltas = true;
        /// <summary>Class name → action ("None", "Watch", "Tarpit", "Honeypot", "Block"). Everything defaults to None.</summary>
        public Dictionary<string, string> AutoActions = new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(IpClass.Scraper)] = "None",
            [nameof(IpClass.HeadlessAutomation)] = "None",
            [nameof(IpClass.VulnScanner)] = "None",
            [nameof(IpClass.CredentialAttacker)] = "None",
        };
        public double AutoActionMinConfidence = 0.8;

        // Robots trap
        public bool RobotsTrapEnabled = true;
        public string? RobotsTrapPath;

        // Intel sources
        public bool IntelReverseDns = true;
        public bool IntelFeeds = true;
        public bool IntelAbuseIpDb = true;
        public bool IntelGreyNoise = true;
        public int AbuseIpDbDailyBudget = 900;
        public int GreyNoiseDailyBudget = 40;

        // Browser beacon
        public bool BeaconEnabled = true;

        public static readonly string[] AllowedActions = { "None", "Watch", "Tarpit", "Honeypot", "Block" };

        public static readonly HashSet<string> ActionableClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            nameof(IpClass.Scraper), nameof(IpClass.HeadlessAutomation), nameof(IpClass.VulnScanner), nameof(IpClass.CredentialAttacker)
        };

        public string ActionFor(IpClass cls) =>
            AutoActions.TryGetValue(cls.ToString(), out var a) && AllowedActions.Contains(a, StringComparer.OrdinalIgnoreCase) ? a : "None";

        public OmniDefenceSettings Clone() => JsonConvert.DeserializeObject<OmniDefenceSettings>(JsonConvert.SerializeObject(this))!;

        public static OmniDefenceSettings Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new OmniDefenceSettings();
            try
            {
                // Replace (not merge into) the defaults so a stored map fully wins.
                var s = JsonConvert.DeserializeObject<OmniDefenceSettings>(json, new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace });
                if (s == null) return new OmniDefenceSettings();
                s.AutoActions = new Dictionary<string, string>(s.AutoActions ?? new(), StringComparer.OrdinalIgnoreCase);
                return s;
            }
            catch { return new OmniDefenceSettings(); }
        }
    }
}
