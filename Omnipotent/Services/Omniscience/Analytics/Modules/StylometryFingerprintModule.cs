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
    /// Writing fingerprint: consistent misspellings, punctuation/casing habits,
    /// signature tics. Highly identifying — this is the feature set that alt-account
    /// and cross-platform identity matching key on.
    /// </summary>
    public class StylometryFingerprintModule : IPersonAnalyticModule
    {
        public string Name => "stylometry";
        public int Version => 1;

        private static readonly Regex Word = new(@"[a-zA-Z']{3,}", RegexOptions.Compiled);
        // Common deliberate/accidental misspellings worth fingerprinting.
        private static readonly Dictionary<string, string> KnownVariants = new(StringComparer.OrdinalIgnoreCase)
        {
            {"definately","definitely"},{"defo","definitely"},{"prolly","probably"},{"probs","probably"},
            {"tho","though"},{"thou","though"},{"cos","because"},{"cuz","because"},{"bc","because"},
            {"rn","right now"},{"ngl","not gonna lie"},{"tbf","to be fair"},{"tbh","to be honest"},
            {"idk","i don't know"},{"ik","i know"},{"nvm","nevermind"},{"omw","on my way"},
            {"wyd","what are you doing"},{"wya","where are you"},{"icl","i can't lie"},{"sm","so much"},
            {"alot","a lot"},{"abit","a bit"},{"aswell","as well"},{"infront","in front"},
        };

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            return Task.FromResult(AnalyticSplits.Apply(msgs, ComputeFromMessages,
                AnalyticSplits.CompactWithArrays(5, "signature_tokens")));
        }

        internal static JObject ComputeFromMessages(List<AnalyticMessage> msgs)
        {
            int analysed = 0;
            int lowercaseStart = 0, terminalPeriod = 0, ellipsis = 0, multiExclaim = 0, multiQuestion = 0;
            int allCaps = 0, noPunctuation = 0, apostropheDrop = 0, apostropheTotal = 0;
            var variantCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            long totalChars = 0;

            // Apostrophe-dropping check: dont/cant/wont/im/ive vs don't/can't…
            string[] dropped = { "dont", "cant", "wont", "im", "ive", "didnt", "isnt", "youre", "thats", "whats" };
            string[] kept = { "don't", "can't", "won't", "i'm", "i've", "didn't", "isn't", "you're", "that's", "what's" };

            foreach (var m in msgs)
            {
                if (string.IsNullOrWhiteSpace(m.Content)) continue;
                string c = m.Content.Trim();
                if (c.StartsWith("http")) continue;
                analysed++;
                totalChars += c.Length;

                if (char.IsLetter(c[0]) && char.IsLower(c[0])) lowercaseStart++;
                if (c.EndsWith('.') && !c.EndsWith("...")) terminalPeriod++;
                if (c.Contains("...") || c.Contains("…")) ellipsis++;
                if (c.Contains("!!")) multiExclaim++;
                if (c.Contains("??")) multiQuestion++;
                int letters = c.Count(char.IsLetter);
                if (letters >= 6 && c.Count(char.IsUpper) >= 0.8 * letters) allCaps++;
                if (!c.Any(ch => ch is '.' or ',' or '!' or '?' or ';' or ':')) noPunctuation++;

                string low = " " + c.ToLowerInvariant() + " ";
                for (int i = 0; i < dropped.Length; i++)
                {
                    bool hasDropped = low.Contains(" " + dropped[i] + " ");
                    bool hasKept = low.Contains(kept[i]);
                    if (hasDropped || hasKept) apostropheTotal++;
                    if (hasDropped) apostropheDrop++;
                }

                foreach (Match w in Word.Matches(c))
                {
                    if (!KnownVariants.ContainsKey(w.Value)) continue;
                    variantCounts.TryGetValue(w.Value.ToLowerInvariant(), out int n);
                    variantCounts[w.Value.ToLowerInvariant()] = n + 1;
                }
            }

            double Rate(int n) => analysed == 0 ? 0 : Math.Round((double)n / analysed, 3);
            return new JObject(
                new JProperty("messages_analysed", analysed),
                new JProperty("avg_message_chars", analysed == 0 ? 0 : (double)totalChars / analysed),
                new JProperty("lowercase_start_rate", Rate(lowercaseStart)),
                new JProperty("terminal_period_rate", Rate(terminalPeriod)),
                new JProperty("ellipsis_rate", Rate(ellipsis)),
                new JProperty("multi_exclaim_rate", Rate(multiExclaim)),
                new JProperty("multi_question_rate", Rate(multiQuestion)),
                new JProperty("all_caps_rate", Rate(allCaps)),
                new JProperty("no_punctuation_rate", Rate(noPunctuation)),
                new JProperty("apostrophe_drop_rate", apostropheTotal == 0 ? 0 : Math.Round((double)apostropheDrop / apostropheTotal, 3)),
                new JProperty("signature_tokens", new JArray(variantCounts
                    .Where(v => v.Value >= 3)
                    .OrderByDescending(v => v.Value).Take(15)
                    .Select(v => new JObject(new JProperty("token", v.Key), new JProperty("count", v.Value)))))
            );
        }
    }
}
