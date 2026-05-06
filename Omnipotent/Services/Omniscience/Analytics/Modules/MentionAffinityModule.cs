using Microsoft.Data.Sqlite;
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
    /// "People they like" leaderboard, sourced *only* from message content (not
    /// shared-conversation co-occurrence \u2014 that is what social_graph already does).
    ///
    /// For each known person in the database we collect candidate names (display
    /// name, alt names, platform usernames). We scan the subject person's messages
    /// for textual occurrences and score each surrounding sentence using the
    /// sentiment lexicon. Aggregated positive-mention score \u2192 affinity ranking.
    /// </summary>
    public class MentionAffinityModule : IPersonAnalyticModule
    {
        public string Name => "mention_affinity";
        public int Version => 1;

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();

            // Collect candidate names for every *other* person.
            var candidates = LoadCandidates(conn, excludePersonId: personId);
            if (candidates.Count == 0)
                return Task.FromResult(new JObject(new JProperty("liked_people", new JArray())));

            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            var stats = new Dictionary<string, Stat>();
            // Pre-compile regex per candidate name (cap to avoid pathological regex sets).
            var regexes = candidates
                .SelectMany(p => p.Names.Select(n => (p.PersonId, p.DisplayName, Name: n)))
                .Where(x => x.Name.Length >= 3 && x.Name.Length <= 32)
                .GroupBy(x => x.Name.ToLowerInvariant())
                .Select(g => g.First())
                .ToList();

            foreach (var m in msgs)
            {
                if (string.IsNullOrWhiteSpace(m.Content)) continue;
                string low = m.Content.ToLowerInvariant();
                // Sentence-level sentiment of this message.
                double sent = ScoreSentiment(m.Content);
                foreach (var c in regexes)
                {
                    string needle = c.Name.ToLowerInvariant();
                    if (!low.Contains(needle)) continue;
                    if (!Regex.IsMatch(low, "(^|[^a-z0-9_])" + Regex.Escape(needle) + "($|[^a-z0-9_])")) continue;

                    if (!stats.TryGetValue(c.PersonId, out var s))
                        s = new Stat { PersonId = c.PersonId, DisplayName = c.DisplayName };
                    s.Mentions++;
                    s.TotalSentiment += sent;
                    if (sent > 0.1) s.PositiveMentions++;
                    else if (sent < -0.1) s.NegativeMentions++;
                    if (sent > 0.3 && s.Samples.Count < 3 && m.Content.Length < 240)
                        s.Samples.Add(m.Content);
                    stats[c.PersonId] = s;
                }
            }

            var ranked = stats.Values
                .Where(s => s.Mentions >= 2)
                .Select(s => new
                {
                    s.PersonId, s.DisplayName, s.Mentions, s.PositiveMentions, s.NegativeMentions,
                    AvgSentiment = s.TotalSentiment / Math.Max(1, s.Mentions),
                    AffinityScore = s.PositiveMentions - s.NegativeMentions + (s.TotalSentiment / Math.Max(1, s.Mentions)),
                    s.Samples
                })
                .OrderByDescending(s => s.AffinityScore)
                .Take(20)
                .Select(s => new JObject(
                    new JProperty("person_id", s.PersonId),
                    new JProperty("display_name", s.DisplayName),
                    new JProperty("mentions", s.Mentions),
                    new JProperty("positive_mentions", s.PositiveMentions),
                    new JProperty("negative_mentions", s.NegativeMentions),
                    new JProperty("avg_sentiment", s.AvgSentiment),
                    new JProperty("affinity_score", s.AffinityScore),
                    new JProperty("sample_messages", new JArray(s.Samples))));

            return Task.FromResult(new JObject(new JProperty("liked_people", new JArray(ranked))));
        }

        private static double ScoreSentiment(string content)
        {
            double s = 0; int hits = 0;
            foreach (var t in SentimentModule.Tokenise(content))
            {
                if (SentimentModule.Lex.TryGetValue(t, out double v)) { s += v; hits++; }
            }
            return hits == 0 ? 0 : s / hits;
        }

        private class Stat
        {
            public string PersonId = "";
            public string DisplayName = "";
            public int Mentions;
            public int PositiveMentions;
            public int NegativeMentions;
            public double TotalSentiment;
            public List<string> Samples = new();
        }

        private class Candidate
        {
            public string PersonId = "";
            public string DisplayName = "";
            public List<string> Names = new();
        }

        private static List<Candidate> LoadCandidates(SqliteConnection conn, string excludePersonId)
        {
            var list = new List<Candidate>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT p.person_id, p.display_name,
                                       (SELECT GROUP_CONCAT(COALESCE(platform_username, ''), '|') FROM platform_identities WHERE person_id=p.person_id) as usernames,
                                       (SELECT GROUP_CONCAT(COALESCE(display_name, ''), '|') FROM platform_identities WHERE person_id=p.person_id) as displays
                                FROM persons p
                                WHERE p.merged_into_person_id IS NULL AND p.person_id != $p";
            cmd.Parameters.AddWithValue("$p", excludePersonId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var c = new Candidate
                {
                    PersonId = r.GetString(0),
                    DisplayName = r.IsDBNull(1) ? "" : r.GetString(1),
                };
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void add(string? s)
                {
                    if (string.IsNullOrWhiteSpace(s)) return;
                    s = s.Trim();
                    if (s.Length < 3) return;
                    // Reject pure-numeric names and very generic words.
                    if (long.TryParse(s, out _)) return;
                    if (seen.Add(s)) c.Names.Add(s);
                }
                add(c.DisplayName);
                if (!r.IsDBNull(2)) foreach (var s in r.GetString(2).Split('|')) add(s);
                if (!r.IsDBNull(3)) foreach (var s in r.GetString(3).Split('|')) add(s);
                if (c.Names.Count > 0) list.Add(c);
            }
            return list;
        }
    }
}
