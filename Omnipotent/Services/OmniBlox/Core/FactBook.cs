using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniBlox.Core
{
    /// <summary>
    /// Keeps a dossier's key facts tidy. The first studio appended every fact every cycle wrote, split any fact
    /// sent as one string on its commas ("[16, 0.15, 44]" became three facts) and clipped them at 300 characters,
    /// so the briefings filled with fragments and near-copies of the same fact. Now:
    /// a fact with a key replaces the older fact with that key; an unkeyed fact that says nearly the same as an
    /// older one replaces it; facts the owner or the studio wrote are never pushed out by an agent's; and when the
    /// book is full the oldest agent fact goes first. Changed facts move to the end, so the briefing's stable
    /// facts keep their place (and their prompt-cache prefix).
    /// </summary>
    public static class FactBook
    {
        public const string Agent = "agent";
        public const string Owner = "owner";
        public const string Studio = "studio";

        /// <summary>Unkeyed facts at least this similar (word overlap) are the same fact restated.</summary>
        public const double SameFactSimilarity = 0.72;

        private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex KeyPrefix = new(@"^(?<key>[A-Za-z][A-Za-z0-9 _\-/]{1,30}):\s+(?<text>\S.*)$", RegexOptions.Compiled | RegexOptions.Singleline);

        public static string Normalize(string text)
        {
            string s = Spaces.Replace((text ?? "").Trim(), " ");
            // A fact the model wrapped in quotes as a JSON string (the old glitch left a leading \").
            if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Trim();
            if (s.Length > GameDossier.MaxKeyFactLength) s = s[..(GameDossier.MaxKeyFactLength - 1)].TrimEnd() + "…";
            return s;
        }

        public static string NormalizeKey(string? key)
        {
            string s = Spaces.Replace((key ?? "").Trim().ToLowerInvariant(), " ");
            return s.Length > 40 ? s[..40] : s;
        }

        /// <summary>"economy: axes cost 15/35/80" → key "economy". Only short topic-like prefixes count.</summary>
        public static KeyFact Parse(string text, DateTime utc, GameStage? stage, string source = Agent)
        {
            string s = Normalize(text);
            var m = KeyPrefix.Match(s);
            if (m.Success && m.Groups["key"].Value.Count(char.IsWhiteSpace) <= 3)
                return new KeyFact { Key = NormalizeKey(m.Groups["key"].Value), Text = Normalize(m.Groups["text"].Value), Utc = utc, Stage = stage, Source = source };
            return new KeyFact { Text = s, Utc = utc, Stage = stage, Source = source };
        }

        /// <summary>Adds (or replaces) facts and removes dropped keys. Returns how many facts changed.</summary>
        public static int Merge(List<KeyFact> book, IEnumerable<KeyFact> incoming, IEnumerable<string>? dropKeys = null, int capacity = GameDossier.MaxKeyFacts)
        {
            int changed = 0;
            if (dropKeys != null)
            {
                var drop = dropKeys.Select(NormalizeKey).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
                changed += book.RemoveAll(f => f.Source == Agent && drop.Contains(NormalizeKey(f.Key)));
            }
            foreach (var raw in incoming)
            {
                var fact = new KeyFact { Key = NormalizeKey(raw.Key), Text = Normalize(raw.Text), Utc = raw.Utc, Stage = raw.Stage, Source = string.IsNullOrEmpty(raw.Source) ? Agent : raw.Source };
                if (fact.Text.Length < 3) continue;
                int same = FindSame(book, fact);
                if (same >= 0)
                {
                    var existing = book[same];
                    if (existing.Source != Agent && fact.Source == Agent)
                        continue; // an agent restating the owner's (or the studio's) words keeps theirs
                    if (string.Equals(existing.Text, fact.Text, StringComparison.Ordinal) && existing.Key == fact.Key)
                        continue; // nothing new: leave it where it is
                    book.RemoveAt(same);
                }
                book.Add(fact);
                changed++;
            }
            // Full: the oldest agent facts go first; the owner's and the studio's stay.
            while (book.Count > capacity)
            {
                int oldest = book.FindIndex(f => f.Source == Agent);
                if (oldest < 0) oldest = 0;
                book.RemoveAt(oldest);
                changed++;
            }
            return changed;
        }

        private static int FindSame(List<KeyFact> book, KeyFact fact)
        {
            // The very same words first, whoever wrote them: an agent repeating the owner's direction adds nothing.
            string normalized = fact.Text.ToLowerInvariant();
            int exact = book.FindIndex(f => f.Text.ToLowerInvariant() == normalized);
            if (exact >= 0) return exact;
            if (fact.Key.Length > 0)
            {
                int keyed = book.FindIndex(f => f.Key == fact.Key && (f.Source == fact.Source || fact.Source != Agent));
                if (keyed >= 0) return keyed;
            }
            var words = Words(fact.Text);
            if (words.Count < 4) return -1;
            int best = -1;
            double bestScore = 0;
            for (int i = 0; i < book.Count; i++)
            {
                if (book[i].Key.Length > 0 && fact.Key.Length > 0 && book[i].Key != fact.Key) continue;
                double score = Similarity(words, Words(book[i].Text));
                if (score > bestScore) { bestScore = score; best = i; }
            }
            return bestScore >= SameFactSimilarity ? best : -1;
        }

        private static HashSet<string> Words(string text) =>
            Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9_.]+").Select(m => m.Value).Where(w => w.Length > 1).ToHashSet(StringComparer.Ordinal);

        private static double Similarity(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;
            int both = a.Count(b.Contains);
            return both / (double)(a.Count + b.Count - both);
        }
    }
}
