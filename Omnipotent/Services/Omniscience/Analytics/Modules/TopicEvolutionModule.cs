using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Interest evolution over time: the InterestInference categories computed per year,
    /// with gained/dropped flags ("got into crypto in 2024, out by 2025"). Old eras stay
    /// visible — this is exactly the history that recency weighting must not erase.
    /// </summary>
    public class TopicEvolutionModule : IPersonAnalyticModule
    {
        public string Name => "topic_evolution";
        public int Version => 1;

        private const double PresenceShare = 0.06; // category share to count as "into it"

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);

            var byYear = msgs.Where(m => !string.IsNullOrEmpty(m.Content))
                             .GroupBy(m => m.SentAt.Year)
                             .OrderBy(g => g.Key)
                             .ToList();
            if (byYear.Count == 0)
                return Task.FromResult(new JObject(new JProperty("years", new JArray())));

            var years = new JArray();
            var presentByYear = new Dictionary<int, HashSet<string>>();
            foreach (var year in byYear)
            {
                ct.ThrowIfCancellationRequested();
                if (year.Count() < 50) continue; // too sparse to call interests
                var interests = InterestInferenceModule.ComputeFromMessages(year.ToList());
                var present = new HashSet<string>();
                var top = new JArray();
                foreach (var cat in (interests["categories"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    double share = cat.Value<double?>("share") ?? 0;
                    if (share < PresenceShare || (cat.Value<int?>("hits") ?? 0) < 5) continue;
                    present.Add(cat.Value<string>("category") ?? "");
                    if (top.Count < 6)
                        top.Add(new JObject(
                            new JProperty("category", cat.Value<string>("category")),
                            new JProperty("share", Math.Round(share, 3))));
                }
                presentByYear[year.Key] = present;
                years.Add(new JObject(
                    new JProperty("year", year.Key),
                    new JProperty("messages", year.Count()),
                    new JProperty("interests", top)));
            }

            // Gained/dropped transitions between consecutive analysed years.
            var transitions = new JArray();
            var analysedYears = presentByYear.Keys.OrderBy(y => y).ToList();
            for (int i = 1; i < analysedYears.Count; i++)
            {
                int prev = analysedYears[i - 1], curr = analysedYears[i];
                foreach (var gained in presentByYear[curr].Except(presentByYear[prev]))
                    transitions.Add(new JObject(new JProperty("year", curr), new JProperty("change", "gained"), new JProperty("category", gained)));
                foreach (var dropped in presentByYear[prev].Except(presentByYear[curr]))
                    transitions.Add(new JObject(new JProperty("year", curr), new JProperty("change", "dropped"), new JProperty("category", dropped)));
            }

            return Task.FromResult(new JObject(
                new JProperty("years", years),
                new JProperty("transitions", transitions)
            ));
        }
    }
}
