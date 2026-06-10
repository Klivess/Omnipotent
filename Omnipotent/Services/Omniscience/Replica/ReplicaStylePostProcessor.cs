using Microsoft.Data.Sqlite;
using Omnipotent.Services.Omniscience.Analytics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.Omniscience.Replica
{
    /// <summary>
    /// Deterministic style enforcement after generation: instead of hoping the LLM
    /// complies with the voice rulebook, measure the person's actual habits (casing,
    /// terminal punctuation, laughter tokens) from their recent corpus and apply them
    /// mechanically. Conservative by design — only transforms with strong statistical
    /// backing (≥75% habit rates) are applied.
    /// </summary>
    public class ReplicaStylePostProcessor
    {
        public class StyleFingerprint
        {
            public int SampleSize;
            public double LowercaseStartRate;
            public double TerminalPeriodRate;
            public double DoubleNewlineRate;
            public string? DominantLaughFamily;
            public DateTime ComputedAt;
        }

        private static readonly Regex LaughToken = new(
            @"\b(lo+l+|lmao+|rofl|(?:ha){2,}|(?:he){2,}|ke+k+|x+d+)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly OmniscienceDb db;
        private readonly ConcurrentDictionary<string, StyleFingerprint> cache = new();

        public ReplicaStylePostProcessor(OmniscienceDb db) { this.db = db; }

        public string Apply(string personId, string draft)
        {
            if (string.IsNullOrWhiteSpace(draft)) return draft;
            var fp = GetFingerprint(personId);
            if (fp.SampleSize < 50) return draft; // not enough data to enforce anything

            string text = draft.Trim();

            // Casing: if they almost never start with a capital, neither does the replica.
            if (fp.LowercaseStartRate >= 0.75)
            {
                text = string.Join("\n", text.Split('\n').Select(line =>
                {
                    string t = line.TrimStart();
                    return t.Length > 0 && char.IsUpper(t[0]) && !t.StartsWith("I ") && !t.StartsWith("I'")
                        ? line.Replace(t, char.ToLowerInvariant(t[0]) + t[1..])
                        : line;
                }));
            }

            // Terminal punctuation: chat-casual people don't end with a full stop.
            if (fp.TerminalPeriodRate <= 0.15 && text.EndsWith('.') && !text.EndsWith("..."))
                text = text[..^1];

            // Laughter: swap generated laugh tokens to their dominant family.
            if (!string.IsNullOrEmpty(fp.DominantLaughFamily))
            {
                string replacement = fp.DominantLaughFamily switch
                {
                    "lol" => "lol",
                    "lmao" => "lmao",
                    "haha" => "haha",
                    "hehe" => "hehe",
                    "kek" => "kek",
                    "xd" => "xd",
                    "rofl" => "rofl",
                    _ => "",
                };
                if (replacement.Length > 0)
                    text = LaughToken.Replace(text, m =>
                        Classify(m.Value) == fp.DominantLaughFamily ? m.Value : replacement);
            }

            return text;
        }

        private static string Classify(string token)
        {
            string low = token.ToLowerInvariant();
            if (low.StartsWith("lol")) return "lol";
            if (low.StartsWith("lmao")) return "lmao";
            if (low.StartsWith("rofl")) return "rofl";
            if (low.StartsWith("ha")) return "haha";
            if (low.StartsWith("he")) return "hehe";
            if (low.StartsWith("ke")) return "kek";
            return "xd";
        }

        public StyleFingerprint GetFingerprint(string personId)
        {
            if (cache.TryGetValue(personId, out var cached) && DateTime.UtcNow - cached.ComputedAt < TimeSpan.FromHours(6))
                return cached;
            var fp = ComputeFingerprint(personId);
            cache[personId] = fp;
            return fp;
        }

        private StyleFingerprint ComputeFingerprint(string personId)
        {
            var fp = new StyleFingerprint { ComputedAt = DateTime.UtcNow };
            try
            {
                using var conn = db.Open();
                var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
                if (idents.Count == 0) return fp;

                var laughCounts = new Dictionary<string, int>();
                int lowercaseStarts = 0, terminalPeriods = 0, doubleNewlines = 0, samples = 0;
                using var cmd = conn.CreateCommand();
                string inC = AnalyticHelpers.BindInClause(cmd, "i", idents);
                cmd.CommandText = $@"SELECT content FROM messages
                    WHERE author_identity_id IN ({inC}) AND content IS NOT NULL AND length(content) BETWEEN 3 AND 600
                    ORDER BY sent_at DESC LIMIT 2000";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string c = r.GetString(0).Trim();
                    if (c.Length < 3 || c.StartsWith("http")) continue;
                    samples++;
                    char first = c[0];
                    if (char.IsLetter(first) && char.IsLower(first)) lowercaseStarts++;
                    if (c.EndsWith('.') && !c.EndsWith("...")) terminalPeriods++;
                    if (c.Contains("\n\n")) doubleNewlines++;
                    foreach (Match m in LaughToken.Matches(c))
                    {
                        string fam = Classify(m.Value);
                        laughCounts.TryGetValue(fam, out int n);
                        laughCounts[fam] = n + 1;
                    }
                }

                fp.SampleSize = samples;
                if (samples > 0)
                {
                    fp.LowercaseStartRate = (double)lowercaseStarts / samples;
                    fp.TerminalPeriodRate = (double)terminalPeriods / samples;
                    fp.DoubleNewlineRate = (double)doubleNewlines / samples;
                }
                if (laughCounts.Count > 0)
                {
                    var top = laughCounts.OrderByDescending(kv => kv.Value).First();
                    // Only enforce when clearly dominant.
                    if (top.Value >= 5 && top.Value >= 0.6 * laughCounts.Values.Sum())
                        fp.DominantLaughFamily = top.Key;
                }
            }
            catch { }
            return fp;
        }
    }
}
