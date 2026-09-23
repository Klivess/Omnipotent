using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.OmniDefence.Fingerprint
{
    /// <summary>One validated browser beacon, reduced to what the classifier uses.</summary>
    public sealed class BeaconObservation
    {
        public string? DeviceId;
        public bool Webdriver;
        public string? WebglRenderer;
        public bool SoftwareRenderer;
        public string? Timezone;
        public bool? TimezoneMatchesGeo;
        public string? Screen;
        public string? Platform;
        public string? Languages;
        public int? Cores;
        public double? MemoryGb;
        public string? Brands;
        public bool UaMismatch;
        public List<string> Anomalies = new();
        public long Pointer;
        public long Keys;
        public long Scroll;
        public long Touch;
        public long Untrusted;
        public double? PointerEntropy;
        /// <summary>Coarse, hash-only record kept per device (no raw canvas/audio data).</summary>
        public string? CompactJson;
    }

    /// <summary>
    /// Validates and interprets the KM website's fingerprint beacon. Checks are the classic
    /// automation tells (webdriver, software GL, permission inconsistency, missing
    /// window.chrome on Chrome, empty languages) plus consistency against the HTTP
    /// User-Agent and the IP's geolocated timezone.
    /// </summary>
    public static class BeaconAnalyzer
    {
        private static readonly string[] SoftwareRenderers = { "swiftshader", "llvmpipe", "softpipe", "mesa offscreen", "basic render driver", "virtualbox", "vmware svga" };

        public static BeaconObservation? Analyze(JObject payload, string? headerUserAgent, string? geoTimezone)
        {
            var env = payload["env"] as JObject;
            if (env == null) return null;
            var input = payload["input"] as JObject;
            var obs = new BeaconObservation();

            obs.Webdriver = env.Value<bool?>("webdriver") ?? false;

            var webgl = env["webgl"] as JObject;
            obs.WebglRenderer = Cap(webgl?.Value<string?>("renderer"), 160);
            string gl = (obs.WebglRenderer ?? "").ToLowerInvariant();
            obs.SoftwareRenderer = gl.Length > 0 && SoftwareRenderers.Any(gl.Contains);
            if (webgl == null || string.IsNullOrEmpty(obs.WebglRenderer)) obs.Anomalies.Add("No WebGL renderer exposed");

            obs.Timezone = Cap(env.Value<string?>("tz"), 64);
            obs.TimezoneMatchesGeo = TimezonesAgree(obs.Timezone, geoTimezone);

            var screen = env["screen"] as JObject;
            int sw = screen?.Value<int?>("w") ?? 0, sh = screen?.Value<int?>("h") ?? 0;
            double dpr = screen?.Value<double?>("dpr") ?? 0;
            obs.Screen = sw > 0 ? $"{sw}x{sh}@{dpr:0.##}" : null;
            if (sw == 0 || sh == 0) obs.Anomalies.Add("Zero-size screen");
            else if (sw == 800 && sh == 600) obs.Anomalies.Add("Default headless screen size 800x600");

            var langs = env["languages"] as JArray;
            obs.Languages = langs == null ? null : Cap(string.Join(",", langs.Select(l => l.ToString())), 64);
            if (langs == null || langs.Count == 0) obs.Anomalies.Add("navigator.languages is empty");

            obs.Cores = env.Value<int?>("cores");
            obs.MemoryGb = env.Value<double?>("mem");

            var uaData = env["uaData"] as JObject;
            var brands = uaData?["brands"] as JArray;
            obs.Brands = brands == null ? null : Cap(string.Join(", ", brands.Select(b => $"{b.Value<string>("brand")} {b.Value<string>("version")}")), 160);
            obs.Platform = Cap(uaData?.Value<string?>("platform") ?? env.Value<string?>("platform"), 32);

            string? jsUa = env.Value<string?>("ua");
            var claimed = UserAgentParser.Parse(headerUserAgent);
            if (!string.IsNullOrEmpty(jsUa) && !string.IsNullOrEmpty(headerUserAgent) && !string.Equals(jsUa, headerUserAgent, StringComparison.Ordinal))
            {
                obs.UaMismatch = true;
                obs.Anomalies.Add("navigator.userAgent differs from the User-Agent header");
            }
            if (brands != null && claimed.Kind == UaKind.Browser)
            {
                bool brandChromium = brands.Any(b => (b.Value<string>("brand") ?? "").Contains("Chrom", StringComparison.OrdinalIgnoreCase));
                if (brandChromium != claimed.IsChromium && claimed.OperatingSystem != "iOS")
                {
                    obs.UaMismatch = true;
                    obs.Anomalies.Add("Client-hint brands disagree with the claimed browser");
                }
            }
            if (!string.IsNullOrEmpty(obs.Platform) && claimed.OperatingSystem is "Windows" or "macOS" or "Android" or "Linux")
            {
                string p = obs.Platform.ToLowerInvariant();
                bool agrees = claimed.OperatingSystem switch
                {
                    "Windows" => p.StartsWith("win"),
                    "macOS" => p.StartsWith("mac"),
                    "Android" => p.Contains("android") || p.Contains("linux"),
                    "Linux" => p.Contains("linux") || p.Contains("x11") || p.Contains("chrome os") || p.Contains("cros"),
                    _ => true
                };
                if (!agrees)
                {
                    obs.UaMismatch = true;
                    obs.Anomalies.Add($"Platform '{obs.Platform}' contradicts User-Agent OS {claimed.OperatingSystem}");
                }
            }

            // Classic headless tells.
            string? notif = env.Value<string?>("notifPerm");
            string? permQuery = env.Value<string?>("permNotif");
            if (notif == "denied" && permQuery == "prompt") obs.Anomalies.Add("Notification permission inconsistent (headless tell)");
            if (claimed.IsChromium && claimed.OperatingSystem is "Windows" or "macOS" or "Linux")
            {
                if (env.Value<bool?>("chrome") == false) obs.Anomalies.Add("window.chrome missing on desktop Chrome");
                if ((env.Value<int?>("plugins") ?? -1) == 0) obs.Anomalies.Add("No plugins on desktop Chrome (headless tell)");
            }
            if (env.Value<bool?>("cdp") == true) obs.Anomalies.Add("DevTools protocol side effects detected");

            if (input != null)
            {
                obs.Pointer = Clamp(input.Value<long?>("pointer"));
                obs.Keys = Clamp(input.Value<long?>("keys"));
                obs.Scroll = Clamp(input.Value<long?>("scroll"));
                obs.Touch = Clamp(input.Value<long?>("touch"));
                obs.Untrusted = Clamp(input.Value<long?>("untrusted"));
                double? entropy = input.Value<double?>("entropy");
                obs.PointerEntropy = entropy is >= 0 and <= 10 ? entropy : null;
            }

            // Device id: stable high-entropy components, hashed server-side so the client can't pick it.
            string? canvas = Cap(env.Value<string?>("canvas"), 64);
            string? audio = Cap(env.Value<string?>("audio"), 64);
            string? fonts = Cap(env.Value<string?>("fonts"), 64);
            if (!string.IsNullOrEmpty(canvas) || !string.IsNullOrEmpty(obs.WebglRenderer))
            {
                obs.DeviceId = RequestSignals.Hash12(string.Join("|", canvas, obs.WebglRenderer, audio, fonts, obs.Screen, obs.Cores, obs.MemoryGb, obs.Platform, obs.Timezone));
            }

            obs.CompactJson = JsonConvert.SerializeObject(new
            {
                screen = obs.Screen,
                platform = obs.Platform,
                tz = obs.Timezone,
                langs = obs.Languages,
                cores = obs.Cores,
                mem = obs.MemoryGb,
                gl = obs.WebglRenderer,
                brands = obs.Brands,
                webdriver = obs.Webdriver,
                anomalies = obs.Anomalies
            });
            return obs;
        }

        /// <summary>Folds an observation into the IP's beacon summary. Engine thread.</summary>
        public static void Merge(IpFingerprint fp, BeaconObservation obs, int ipsForDevice)
        {
            var b = fp.Beacon ??= new BeaconSummary();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            b.Count++;
            if (b.FirstMs == 0) b.FirstMs = now;
            b.LastMs = now;
            b.Webdriver |= obs.Webdriver;
            b.SoftwareRenderer |= obs.SoftwareRenderer;
            b.WebglRenderer = obs.WebglRenderer ?? b.WebglRenderer;
            b.Timezone = obs.Timezone ?? b.Timezone;
            if (obs.TimezoneMatchesGeo.HasValue) b.TimezoneMatchesGeo = obs.TimezoneMatchesGeo;
            b.Screen = obs.Screen ?? b.Screen;
            b.Platform = obs.Platform ?? b.Platform;
            b.Languages = obs.Languages ?? b.Languages;
            b.Cores = obs.Cores ?? b.Cores;
            b.MemoryGb = obs.MemoryGb ?? b.MemoryGb;
            b.Brands = obs.Brands ?? b.Brands;
            b.UaMismatch |= obs.UaMismatch;
            foreach (var a in obs.Anomalies)
            {
                if (!b.Anomalies.Contains(a) && b.Anomalies.Count < 12) b.Anomalies.Add(a);
            }
            // The client sends deltas since its last beacon, so these simply accumulate.
            b.TrustedPointer += obs.Pointer;
            b.TrustedKeys += obs.Keys;
            b.TrustedScroll += obs.Scroll;
            b.TrustedTouch += obs.Touch;
            b.UntrustedEvents += obs.Untrusted;
            if (obs.PointerEntropy.HasValue) b.PointerEntropy = Math.Max(b.PointerEntropy ?? 0, obs.PointerEntropy.Value);
            if (!string.IsNullOrEmpty(obs.DeviceId) && (b.DeviceIds.Count < 32 || b.DeviceIds.Contains(obs.DeviceId))) b.DeviceIds.Add(obs.DeviceId);
            if (ipsForDevice > b.MaxIpsPerDevice) b.MaxIpsPerDevice = ipsForDevice;
        }

        /// <summary>Compares two IANA zones by their current UTC offset (names differ for the same place).</summary>
        public static bool? TimezonesAgree(string? browserTz, string? geoTz)
        {
            if (string.IsNullOrWhiteSpace(browserTz) || string.IsNullOrWhiteSpace(geoTz)) return null;
            if (string.Equals(browserTz, geoTz, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                var a = TimeZoneInfo.FindSystemTimeZoneById(browserTz);
                var b = TimeZoneInfo.FindSystemTimeZoneById(geoTz);
                var now = DateTime.UtcNow;
                return a.GetUtcOffset(now) == b.GetUtcOffset(now);
            }
            catch { return null; }
        }

        private static long Clamp(long? v) => Math.Clamp(v ?? 0, 0, 100_000);
        private static string? Cap(string? s, int max) => s == null ? null : s.Length <= max ? s : s.Substring(0, max);
    }
}
