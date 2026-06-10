using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Deduction
{
    /// <summary>
    /// Cross-platform identity resolution: scores pairs of distinct person records that
    /// might be the same human (the WhatsApp "Sarah" who is also Discord "sarah_x").
    /// Three independent signals — name/handle similarity, shared rare URLs, and
    /// stylometric fingerprint distance — combine into a suggestion only a human
    /// approves (feeds the existing /omniscience/persons/merge path). Conservative: a
    /// pair needs corroboration from more than one signal to surface.
    /// </summary>
    public class IdentityLinkEngine
    {
        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private readonly SemaphoreSlim runLock = new(1, 1);

        private const double MinScore = 0.55;
        private static readonly Regex Url = new(@"https?://([\w.-]+/[\w./?=&%-]{6,})", RegexOptions.Compiled);

        public IdentityLinkEngine(Omniscience service, OmniscienceDb db)
        {
            this.service = service;
            this.db = db;
        }

        public async Task<string> RunAsync(CancellationToken ct)
        {
            if (!await runLock.WaitAsync(0, ct)) return "identity linking already running";
            try
            {
                var persons = LoadPersonProfiles();
                // Only consider tracked/watch persons as anchors (don't link the whole archive).
                var anchors = persons.Where(p => p.Tier != "archive").ToList();
                var suggestions = new List<(string A, string B, double Score, string Reason)>();

                for (int i = 0; i < anchors.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var a = anchors[i];
                    foreach (var b in persons)
                    {
                        if (string.CompareOrdinal(a.PersonId, b.PersonId) >= 0) continue; // unordered pairs once
                        if (a.Platforms.Overlaps(b.Platforms)) continue;                   // same platform → not a cross-platform link
                        var (score, reason) = ScorePair(a, b);
                        if (score >= MinScore) suggestions.Add((a.PersonId, b.PersonId, score, reason));
                    }
                }

                int written = await PersistSuggestionsAsync(suggestions, ct);
                string summary = $"{anchors.Count} anchors scanned, {written} link suggestions";
                await service.ServiceLog($"[Omniscience] Identity-link engine: {summary}");
                return summary;
            }
            finally { runLock.Release(); }
        }

        private (double Score, string Reason) ScorePair(PersonProfile a, PersonProfile b)
        {
            var reasons = new List<string>();
            double score = 0;

            // 1. Name / handle similarity.
            double nameSim = a.Names.SelectMany(n1 => b.Names.Select(n2 => NameSimilarity(n1, n2))).DefaultIfEmpty(0).Max();
            if (nameSim >= 0.85) { score += 0.45; reasons.Add($"matching names ({Math.Round(nameSim, 2)})"); }
            else if (nameSim >= 0.6) { score += 0.2; reasons.Add($"similar names ({Math.Round(nameSim, 2)})"); }

            // 2. Shared rare URLs (a unique link both posted is strong corroboration).
            int sharedUrls = a.RareUrls.Intersect(b.RareUrls).Count();
            if (sharedUrls > 0) { score += Math.Min(0.4, 0.2 * sharedUrls); reasons.Add($"{sharedUrls} shared unique link(s)"); }

            // 3. Stylometric fingerprint proximity.
            if (a.StyleVector != null && b.StyleVector != null)
            {
                double dist = EuclideanDistance(a.StyleVector, b.StyleVector);
                if (dist < 0.12) { score += 0.25; reasons.Add($"near-identical writing style ({Math.Round(dist, 3)})"); }
                else if (dist < 0.2) { score += 0.1; reasons.Add($"similar writing style ({Math.Round(dist, 3)})"); }
            }

            // Require corroboration: a single weak signal isn't enough to suggest a merge.
            if (reasons.Count < 2 && nameSim < 0.85 && sharedUrls == 0) return (0, "");
            return (Math.Min(1.0, score), string.Join("; ", reasons));
        }

        private async Task<int> PersistSuggestionsAsync(List<(string A, string B, double Score, string Reason)> suggestions, CancellationToken ct)
        {
            if (suggestions.Count == 0) return 0;
            int written = 0;
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var tx = conn.BeginTransaction();
                foreach (var s in suggestions.OrderByDescending(s => s.Score).Take(50))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    // Don't resurface a rejected pair; refresh score on pending ones.
                    cmd.CommandText = @"INSERT INTO person_link_suggestions(suggestion_id, person_id_a, person_id_b, score, reason, created_at)
                        VALUES($id,$a,$b,$s,$r,$t)
                        ON CONFLICT(person_id_a, person_id_b) DO UPDATE SET
                            score=excluded.score, reason=excluded.reason
                        WHERE person_link_suggestions.status='pending'";
                    cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                    cmd.Parameters.AddWithValue("$a", s.A);
                    cmd.Parameters.AddWithValue("$b", s.B);
                    cmd.Parameters.AddWithValue("$s", Math.Round(s.Score, 3));
                    cmd.Parameters.AddWithValue("$r", s.Reason);
                    cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    cmd.ExecuteNonQuery();
                    written++;
                }
                tx.Commit();
            }
            finally { db.WriteLock.Release(); }
            return written;
        }

        // ── Person feature loading ──

        private class PersonProfile
        {
            public string PersonId = "";
            public string Tier = "archive";
            public HashSet<string> Platforms = new();
            public List<string> Names = new();
            public HashSet<string> RareUrls = new();
            public double[]? StyleVector;
        }

        private List<PersonProfile> LoadPersonProfiles()
        {
            var profiles = new Dictionary<string, PersonProfile>();
            using var conn = db.Open();

            // Base persons + their platforms + names (display + usernames + alt names).
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT p.person_id, p.display_name, p.tier, pi.platform, pi.platform_username, pi.display_name
                    FROM persons p
                    JOIN platform_identities pi ON pi.person_id = p.person_id
                    WHERE p.merged_into_person_id IS NULL";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string pid = r.GetString(0);
                    if (!profiles.TryGetValue(pid, out var prof))
                        profiles[pid] = prof = new PersonProfile { PersonId = pid, Tier = r.IsDBNull(2) ? "archive" : r.GetString(2) };
                    AddName(prof, r.IsDBNull(1) ? null : r.GetString(1));
                    prof.Platforms.Add(r.GetString(3));
                    AddName(prof, r.IsDBNull(4) ? null : r.GetString(4));
                    AddName(prof, r.IsDBNull(5) ? null : r.GetString(5));
                }
            }

            // Stylometry vectors from person_statistics.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT person_id, payload_json FROM person_statistics WHERE module_name='stylometry'";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!profiles.TryGetValue(r.GetString(0), out var prof)) continue;
                    try { prof.StyleVector = StyleVectorFromPayload(JObject.Parse(r.GetString(1))); } catch { }
                }
            }

            // Rare URLs: links posted by few persons (a personal blog, a niche repo).
            var urlPosters = new Dictionary<string, HashSet<string>>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT pi.person_id, m.content FROM messages m
                    JOIN platform_identities pi ON pi.identity_id = m.author_identity_id
                    JOIN persons p ON p.person_id = pi.person_id
                    WHERE p.tier != 'archive' AND m.content LIKE '%http%'";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string pid = r.GetString(0);
                    foreach (Match mm in Url.Matches(r.GetString(1)))
                    {
                        string url = mm.Groups[1].Value.ToLowerInvariant();
                        if (url.Contains("discord") || url.Contains("tenor") || url.Contains("giphy") ||
                            url.Contains("youtube") || url.Contains("youtu.be")) continue; // too common
                        if (!urlPosters.TryGetValue(url, out var set)) urlPosters[url] = set = new HashSet<string>();
                        set.Add(pid);
                    }
                }
            }
            foreach (var (url, posters) in urlPosters)
            {
                if (posters.Count is < 2 or > 3) continue; // shared by exactly a few → meaningful
                foreach (var pid in posters)
                    if (profiles.TryGetValue(pid, out var prof)) prof.RareUrls.Add(url);
            }

            return profiles.Values.ToList();
        }

        private static void AddName(PersonProfile prof, string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (name.Length < 2 || long.TryParse(name, out _)) return;
            if (!prof.Names.Contains(name, StringComparer.OrdinalIgnoreCase)) prof.Names.Add(name);
        }

        private static double[] StyleVectorFromPayload(JObject p)
        {
            double V(string k) => p.Value<double?>(k) ?? 0;
            return new[]
            {
                V("lowercase_start_rate"), V("terminal_period_rate"), V("ellipsis_rate"),
                V("multi_exclaim_rate"), V("all_caps_rate"), V("no_punctuation_rate"),
                V("apostrophe_drop_rate"), Math.Min(1.0, V("avg_message_chars") / 200.0),
            };
        }

        private static double EuclideanDistance(double[] a, double[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; i++) sum += (a[i] - b[i]) * (a[i] - b[i]);
            return Math.Sqrt(sum / a.Length);
        }

        // Normalised name similarity: exact, containment, or character-bigram Dice.
        internal static double NameSimilarity(string a, string b)
        {
            a = a.ToLowerInvariant().Trim();
            b = b.ToLowerInvariant().Trim();
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a == b) return 1.0;
            if (a.Length >= 4 && b.Length >= 4 && (a.Contains(b) || b.Contains(a))) return 0.9;
            return DiceBigram(a, b);
        }

        private static double DiceBigram(string a, string b)
        {
            if (a.Length < 2 || b.Length < 2) return 0;
            var ba = Bigrams(a);
            var bb = Bigrams(b);
            int overlap = 0;
            var bbCopy = new List<string>(bb);
            foreach (var g in ba)
                if (bbCopy.Remove(g)) overlap++;
            return 2.0 * overlap / (ba.Count + bb.Count);
        }

        private static List<string> Bigrams(string s)
        {
            var list = new List<string>();
            for (int i = 0; i < s.Length - 1; i++) list.Add(s.Substring(i, 2));
            return list;
        }
    }
}
