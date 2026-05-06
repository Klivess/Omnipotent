using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Trigram-marker language guess across a small set. Surfaces a primary +
    /// secondary language, confidence (share of dominant marker), counts, and
    /// per-language sample messages so the dossier can show *what* mixed-language
    /// content looks like, not just a one-letter code.
    /// </summary>
    public class LanguageDetectionModule : IPersonAnalyticModule
    {
        public string Name => "language";
        public int Version => 2;

        private static readonly Dictionary<string, string[]> Markers = new()
        {
            { "en", new[]{ " the "," and "," you "," that "," with "," for ", " this ", " have " } },
            { "es", new[]{ " que "," los "," las "," pero "," como "," para ", " esto ", " tambien " } },
            { "fr", new[]{ " que "," les "," vous "," pour "," avec "," est ", " mais ", " avec " } },
            { "de", new[]{ " der "," die "," und "," ist "," nicht "," das ", " auch ", " mit " } },
            // 'pt' previously contained ' for ' which collided with English; replaced with portuguese-specific markers.
            { "pt", new[]{ " que "," nao "," voce "," como "," tudo ", " uma ", " porque ", " obrigado " } },
            { "it", new[]{ " che "," non "," per "," sono "," come "," anche ", " molto ", " quando " } },
        };

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            var counts = Markers.Keys.ToDictionary(k => k, _ => 0);
            var samplesByLang = Markers.Keys.ToDictionary(k => k, _ => new List<string>());
            int analysed = 0;
            foreach (var m in msgs)
            {
                if (string.IsNullOrEmpty(m.Content) || m.Content.Length < 8) continue;
                analysed++;
                string padded = " " + m.Content.ToLowerInvariant() + " ";
                string? bestLang = null; int bestHits = 0;
                foreach (var kv in Markers)
                {
                    int hits = 0;
                    foreach (var token in kv.Value)
                        if (padded.Contains(token)) hits++;
                    if (hits > 0) counts[kv.Key] += hits;
                    if (hits > bestHits) { bestHits = hits; bestLang = kv.Key; }
                }
                if (bestLang != null && bestHits >= 2 && samplesByLang[bestLang].Count < 4 && m.Content.Length < 200)
                    samplesByLang[bestLang].Add(m.Content);
            }
            int total = counts.Values.Sum();
            var ordered = counts.OrderByDescending(kv => kv.Value).ToList();
            string? primary = total == 0 ? null : ordered[0].Key;
            string? secondary = total == 0 || ordered.Count < 2 || ordered[1].Value == 0 ? null : ordered[1].Key;
            double confidence = total == 0 ? 0 : (double)ordered[0].Value / total;
            var dist = ordered.Select(kv => new JObject(
                new JProperty("lang", kv.Key),
                new JProperty("count", kv.Value),
                new JProperty("share", total == 0 ? 0 : (double)kv.Value / total),
                new JProperty("samples", new JArray(samplesByLang[kv.Key]))));

            return Task.FromResult(new JObject(
                new JProperty("primary_language", primary),
                new JProperty("secondary_language", secondary),
                new JProperty("confidence", confidence),
                new JProperty("messages_analysed", analysed),
                new JProperty("distribution", new JArray(dist))
            ));
        }
    }
}

