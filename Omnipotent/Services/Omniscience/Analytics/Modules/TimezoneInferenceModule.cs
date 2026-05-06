using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Best-guess UTC offset by fitting the person's UTC hour-of-day histogram against
    /// a typical "awake at 09:00\u201301:00 local" mask. Also scans message content for
    /// explicit location/country mentions ("from London", "in Australia").
    /// Output is intentionally conservative \u2014 we surface a confidence number so the
    /// LLM dossier can hedge appropriately.
    /// </summary>
    public class TimezoneInferenceModule : IPersonAnalyticModule
    {
        public string Name => "timezone_inference";
        public int Version => 1;

        // Cities/countries to detect in free text. Matched as whole words with optional
        // trailing punctuation. Keep small and high-precision.
        private static readonly (string Phrase, int OffsetMinutes, string Region)[] LocationHints =
        {
            ("london",          0,    "United Kingdom"),
            ("uk",              0,    "United Kingdom"),
            ("britain",         0,    "United Kingdom"),
            ("ireland",         0,    "Ireland"),
            ("dublin",          0,    "Ireland"),
            ("paris",          60,    "France"),
            ("france",         60,    "France"),
            ("berlin",         60,    "Germany"),
            ("germany",        60,    "Germany"),
            ("madrid",         60,    "Spain"),
            ("spain",          60,    "Spain"),
            ("rome",           60,    "Italy"),
            ("italy",          60,    "Italy"),
            ("athens",        120,    "Greece"),
            ("istanbul",      180,    "Turkey"),
            ("moscow",        180,    "Russia"),
            ("dubai",         240,    "UAE"),
            ("india",         330,    "India"),
            ("delhi",         330,    "India"),
            ("mumbai",        330,    "India"),
            ("bangkok",       420,    "Thailand"),
            ("singapore",     480,    "Singapore"),
            ("china",         480,    "China"),
            ("beijing",       480,    "China"),
            ("hong kong",     480,    "Hong Kong"),
            ("japan",         540,    "Japan"),
            ("tokyo",         540,    "Japan"),
            ("korea",         540,    "South Korea"),
            ("seoul",         540,    "South Korea"),
            ("sydney",        600,    "Australia"),
            ("australia",     600,    "Australia"),
            ("melbourne",     600,    "Australia"),
            ("nz",            780,    "New Zealand"),
            ("new zealand",   780,    "New Zealand"),
            ("hawaii",       -600,    "Hawaii"),
            ("alaska",       -540,    "Alaska"),
            ("california",   -480,    "USA (Pacific)"),
            ("seattle",      -480,    "USA (Pacific)"),
            ("la",           -480,    "USA (Pacific)"),
            ("denver",       -420,    "USA (Mountain)"),
            ("texas",        -360,    "USA (Central)"),
            ("chicago",      -360,    "USA (Central)"),
            ("nyc",          -300,    "USA (Eastern)"),
            ("new york",     -300,    "USA (Eastern)"),
            ("toronto",      -300,    "Canada"),
            ("brazil",       -180,    "Brazil"),
            ("argentina",    -180,    "Argentina"),
            ("portugal",        0,    "Portugal"),
            ("south africa",  120,    "South Africa"),
        };

        private static readonly Regex LocationLead = new(
            @"\b(?:i(?:'m|\s*am)?\s*(?:from|in|live|currently|based)\s*(?:in|at|near)?|i\s+live\s+in)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);

            // 1. Build UTC hour histogram restricted to text-bearing messages so a stray
            //    auto-DM doesn't dominate.
            int[] hours = new int[24];
            int totalActive = 0;
            foreach (var m in msgs)
            {
                if (string.IsNullOrWhiteSpace(m.Content)) continue;
                hours[m.SentAt.Hour]++;
                totalActive++;
            }

            // 2. Fit offset: compute, for each candidate offset, the share of messages
            //    that fall in the local awake window 09:00\u201301:00 (17 h). Pick the max.
            int bestOffsetH = 0; double bestShare = 0;
            int[] localShare = new int[24];
            for (int off = -12; off <= 14; off++)
            {
                int inWindow = 0;
                for (int h = 0; h < 24; h++)
                {
                    int local = ((h + off) % 24 + 24) % 24;
                    if (local >= 9 || local < 1) inWindow += hours[h];
                }
                double share = totalActive == 0 ? 0 : (double)inWindow / totalActive;
                if (share > bestShare) { bestShare = share; bestOffsetH = off; }
            }
            // Build local-hour histogram for the chosen offset for display.
            for (int h = 0; h < 24; h++)
            {
                int local = ((h + bestOffsetH) % 24 + 24) % 24;
                localShare[local] += hours[h];
            }

            // 3. Location hints from message bodies.
            var hintCounts = new Dictionary<(string Region, int Off), int>();
            int leadHits = 0;
            foreach (var m in msgs)
            {
                if (string.IsNullOrWhiteSpace(m.Content) || m.Content.Length < 4) continue;
                string low = m.Content.ToLowerInvariant();
                bool hasLead = LocationLead.IsMatch(low);
                foreach (var hint in LocationHints)
                {
                    if (!low.Contains(hint.Phrase)) continue;
                    if (!Regex.IsMatch(low, "(^|[^a-z0-9])" + Regex.Escape(hint.Phrase) + "($|[^a-z0-9])")) continue;
                    int weight = hasLead ? 3 : 1;
                    if (hasLead) leadHits++;
                    var k = (hint.Region, hint.OffsetMinutes);
                    hintCounts.TryGetValue(k, out int c);
                    hintCounts[k] = c + weight;
                }
            }
            var topRegions = hintCounts
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => new JObject(
                    new JProperty("region", kv.Key.Region),
                    new JProperty("offset_minutes", kv.Key.Off),
                    new JProperty("score", kv.Value)));

            // 4. Reconcile: if location hint is strong, prefer its offset.
            int? finalOffsetMinutes = bestOffsetH * 60;
            string? finalRegion = null;
            double finalConfidence = bestShare; // 0..1 from histogram fit
            if (hintCounts.Count > 0)
            {
                var top = hintCounts.OrderByDescending(kv => kv.Value).First();
                if (top.Value >= 2)
                {
                    finalOffsetMinutes = top.Key.Off;
                    finalRegion = top.Key.Region;
                    finalConfidence = Math.Min(1.0, finalConfidence + 0.15 + 0.05 * top.Value);
                }
            }

            return Task.FromResult(new JObject(
                new JProperty("inferred_utc_offset_minutes", finalOffsetMinutes),
                new JProperty("inferred_region", finalRegion),
                new JProperty("confidence", finalConfidence),
                new JProperty("activity_window_share", bestShare),
                new JProperty("messages_analysed", totalActive),
                new JProperty("location_lead_phrase_hits", leadHits),
                new JProperty("local_hour_histogram", new JArray(localShare.Select(x => (JToken)x))),
                new JProperty("top_region_hints", new JArray(topRegions))
            ));
        }
    }
}
