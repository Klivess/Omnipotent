using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Omnipotent.Services.Omniscience.Analytics
{
    /// <summary>
    /// Recency weighting shared by analytics, profiling and deduction. Floored exponential
    /// decay: weight = max(floor, 0.5^(ageDays/halfLife)). The floor exists because corpora
    /// reach back 6+ years — pure exponential decay would give a 6-year-old message a weight
    /// of ~0.0002 (effectively deleting it); the floor keeps old eras audible in lifetime
    /// metrics while recent behaviour still dominates the "current persona" view.
    /// Configured once at service startup from OmniSettings.
    /// </summary>
    public static class TemporalWeighting
    {
        public static double HalfLifeDays { get; private set; } = 180;
        public static double Floor { get; private set; } = 0.05;

        public static void Configure(double halfLifeDays, double floor)
        {
            HalfLifeDays = Math.Max(1, halfLifeDays);
            Floor = Math.Clamp(floor, 0, 1);
        }

        public static double Weight(DateTime sentAtUtc, DateTime nowUtc)
        {
            double ageDays = Math.Max(0, (nowUtc - sentAtUtc).TotalDays);
            return Math.Max(Floor, Math.Pow(0.5, ageDays / HalfLifeDays));
        }
    }

    /// <summary>
    /// Generic windows + facets decoration for analytic module payloads.
    /// "Windows" answer "what is this person like *recently*" (30/90/365 day slices);
    /// "facets" answer "what is this person like *in this room*" (dm / group dm / per server),
    /// because people behave differently in different contexts. Split payloads are compacted
    /// to headline metrics so person_statistics rows (and the LLM prompts built from them)
    /// stay small.
    /// </summary>
    public static class AnalyticSplits
    {
        public static readonly (string Name, int Days)[] Windows =
        {
            ("last_30d", 30),
            ("last_90d", 90),
            ("last_365d", 365),
        };

        public const int MinFacetMessages = 25;
        public const int MaxFacets = 8;

        /// <summary>Stable facet key for a message's conversational context.</summary>
        public static string FacetKey(AnalyticMessage m)
        {
            return m.ConversationKind switch
            {
                "dm" => "dm",
                "group_dm" => "group_dm",
                _ => "server:" + (string.IsNullOrWhiteSpace(m.GuildName) ? (m.GuildId ?? "unknown") : m.GuildName),
            };
        }

        /// <summary>
        /// Computes the full payload over all messages, then attaches compacted per-window
        /// and per-facet recomputations. <paramref name="compactor"/> lets modules whose
        /// value lives in arrays (interests, topics…) keep a trimmed slice; the default
        /// keeps scalar properties only.
        /// </summary>
        public static JObject Apply(List<AnalyticMessage> msgs, Func<List<AnalyticMessage>, JObject> compute,
            Func<JObject, JObject>? compactor = null)
        {
            compactor ??= Compact;
            var now = DateTime.UtcNow;
            var full = compute(msgs);

            var windows = new JObject();
            foreach (var (name, days) in Windows)
            {
                var subset = msgs.Where(m => (now - m.SentAt).TotalDays <= days).ToList();
                var win = compactor(compute(subset));
                win["message_count"] = subset.Count;
                windows[name] = win;
            }
            full["windows"] = windows;

            var facets = new JObject();
            foreach (var group in msgs.GroupBy(FacetKey)
                                      .Where(g => g.Count() >= MinFacetMessages)
                                      .OrderByDescending(g => g.Count())
                                      .Take(MaxFacets))
            {
                var subset = group.ToList();
                var fac = compactor(compute(subset));
                fac["message_count"] = subset.Count;
                facets[group.Key] = fac;
            }
            full["facets"] = facets;

            return full;
        }

        /// <summary>Default compaction: scalar (non-array, non-object) properties only.</summary>
        public static JObject Compact(JObject full)
        {
            var compact = new JObject();
            foreach (var prop in full.Properties())
            {
                if (prop.Value is JArray || prop.Value is JObject) continue;
                compact[prop.Name] = prop.Value;
            }
            return compact;
        }

        /// <summary>Compact, plus the first <paramref name="take"/> entries of the named arrays.</summary>
        public static Func<JObject, JObject> CompactWithArrays(int take, params string[] arrayNames)
        {
            return full =>
            {
                var compact = Compact(full);
                foreach (var name in arrayNames)
                    if (full[name] is JArray arr)
                        compact[name] = new JArray(arr.Take(take));
                return compact;
            };
        }
    }
}
