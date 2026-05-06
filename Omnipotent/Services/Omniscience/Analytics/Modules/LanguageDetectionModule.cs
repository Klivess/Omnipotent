using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>Trigram-based language guess across a small set. Phase-1 heuristic only.</summary>
    public class LanguageDetectionModule : IPersonAnalyticModule
    {
        public string Name => "language";
        public int Version => 1;

        private static readonly Dictionary<string, string[]> Markers = new()
        {
            { "en", new[]{ " the "," and "," you "," that "," with "," for " } },
            { "es", new[]{ " que "," los "," las "," pero "," como "," para " } },
            { "fr", new[]{ " que "," les "," vous "," pour "," avec "," est " } },
            { "de", new[]{ " der "," die "," und "," ist "," nicht "," das " } },
            { "pt", new[]{ " que "," nao "," para "," voce "," como "," tudo " } },
            { "it", new[]{ " che "," non "," per "," sono "," come "," anche " } },
        };

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            var counts = Markers.Keys.ToDictionary(k => k, _ => 0);
            int analysed = 0;
            foreach (var m in msgs)
            {
                if (string.IsNullOrEmpty(m.Content) || m.Content.Length < 8) continue;
                analysed++;
                string padded = " " + m.Content.ToLowerInvariant() + " ";
                foreach (var kv in Markers)
                    foreach (var token in kv.Value)
                        if (padded.Contains(token)) counts[kv.Key]++;
            }
            string primary = counts.OrderByDescending(kv => kv.Value).First().Key;
            int total = counts.Values.Sum();
            var dist = counts.OrderByDescending(kv => kv.Value).Select(kv => new JObject(
                new JProperty("lang", kv.Key),
                new JProperty("count", kv.Value),
                new JProperty("share", total == 0 ? 0 : (double)kv.Value / total)));

            return Task.FromResult(new JObject(
                new JProperty("primary_language", total == 0 ? null : primary),
                new JProperty("messages_analysed", analysed),
                new JProperty("distribution", new JArray(dist))
            ));
        }
    }
}
