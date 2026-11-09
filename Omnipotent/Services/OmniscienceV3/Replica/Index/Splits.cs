using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>One era of a person's writing: a stretch between change-points in their measured style and activity.</summary>
    public sealed record Era(int Index, long FromMs, long ToMs, int Pairs, IReadOnlyDictionary<string, double> Means);

    /// <summary>
    /// Change-point eras (F282/F312): monthly feature vectors in the person's local months (their zone when known, else
    /// UTC) — length, casing, punctuation, emoji, stretched letters, burst size, script mix, activity — standardised and
    /// segmented by binary segmentation with a BIC-style penalty. Calendar years are never eras; a stable writer is one era.
    /// </summary>
    public static class EraDetector
    {
        public static readonly string[] Features = { "logChars", "lowerStart", "noEndPunct", "emoji", "stretch", "burst", "latin", "activity" };

        public static IReadOnlyList<Era> Detect(IReadOnlyList<ReplicaPair> pairs, Func<long, TimeZoneInfo?> zoneAt, int minMonthPairs = 8, int minSegmentMonths = 3, int maxEras = 8)
        {
            if (pairs.Count == 0) return Array.Empty<Era>();
            long first = pairs.Min(p => p.ReplyAtMs), last = pairs.Max(p => p.ReplyEndMs);
            var months = pairs.GroupBy(p => MonthKey(p.ReplyAtMs, zoneAt(p.ReplyAtMs))).OrderBy(g => g.Key).ToList();
            var usable = months.Where(g => g.Count() >= minMonthPairs).ToList();
            if (usable.Count < 2 * minSegmentMonths) return new[] { new Era(0, first, last + 1, pairs.Count, MeansOf(pairs)) };

            var matrix = usable.Select(g => Vector(g.ToList())).ToList();
            Standardise(matrix);
            var cuts = new List<int>();
            Segment(matrix, 0, matrix.Count, minSegmentMonths, cuts, maxEras - 1);
            cuts.Sort();

            var boundaries = new List<long> { first };
            foreach (int c in cuts) boundaries.Add(MonthStartMs(usable[c].Key, zoneAt));
            boundaries.Add(last + 1);
            var eras = new List<Era>();
            for (int i = 0; i + 1 < boundaries.Count; i++)
            {
                long from = boundaries[i], to = boundaries[i + 1];
                var inside = pairs.Where(p => p.ReplyAtMs >= from && p.ReplyAtMs < to).ToList();
                if (inside.Count == 0) continue;
                eras.Add(new Era(eras.Count, from, to, inside.Count, MeansOf(inside)));
            }
            return eras;
        }

        public static int EraOf(IReadOnlyList<Era> eras, long atMs)
        {
            for (int i = eras.Count - 1; i >= 0; i--) if (atMs >= eras[i].FromMs) return eras[i].Index;
            return 0;
        }

        private static (int Year, int Month) MonthKey(long ms, TimeZoneInfo? zone)
        {
            var local = zone == null ? Instant.ToUtcDateTime(ms) : Zones.ToLocal(ms, zone);
            return (local.Year, local.Month);
        }

        private static long MonthStartMs((int Year, int Month) key, Func<long, TimeZoneInfo?> zoneAt)
        {
            long utcStart = Instant.FromUtc(new DateTime(key.Year, key.Month, 1, 0, 0, 0, DateTimeKind.Utc));
            var zone = zoneAt(utcStart);
            return zone == null ? utcStart : Zones.DayStartUtcMs(new DateOnly(key.Year, key.Month, 1), zone);
        }

        private static double[] Vector(List<ReplicaPair> pairs)
        {
            var m = MeansOf(pairs);
            return Features.Select(f => m.GetValueOrDefault(f)).ToArray();
        }

        internal static IReadOnlyDictionary<string, double> MeansOf(IReadOnlyCollection<ReplicaPair> pairs)
        {
            double n = 0, logChars = 0, lower = 0, lettered = 0, noEnd = 0, emoji = 0, stretch = 0, burst = 0, latin = 0, letters = 0;
            foreach (var p in pairs)
            {
                burst += p.ReplyCount;
                foreach (var line in p.ReplyLines)
                {
                    string prose = TextFeatures.StripMarkup(line);
                    n++;
                    logChars += Math.Log(1 + prose.Trim().Length);
                    var s = StyleLine.Of(line);
                    if (s.HasLetters) { lettered++; if (s.LowercaseStart) lower++; }
                    if (s.Ending == LineEnding.None) noEnd++;
                    if (s.EmojiCount > 0) emoji++;
                    if (s.Stretched) stretch++;
                    latin += s.LatinLetters;
                    letters += s.Letters;
                }
            }
            n = Math.Max(1, n);
            return new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["logChars"] = logChars / n,
                ["lowerStart"] = lettered == 0 ? 0 : lower / lettered,
                ["noEndPunct"] = noEnd / n,
                ["emoji"] = emoji / n,
                ["stretch"] = stretch / n,
                ["burst"] = pairs.Count == 0 ? 0 : burst / pairs.Count,
                ["latin"] = letters == 0 ? 1 : latin / letters,
                ["activity"] = Math.Log(1 + pairs.Count),
            };
        }

        private static void Standardise(List<double[]> rows)
        {
            int d = rows[0].Length;
            for (int j = 0; j < d; j++)
            {
                double mean = rows.Average(r => r[j]);
                double sd = Math.Sqrt(rows.Average(r => (r[j] - mean) * (r[j] - mean)));
                foreach (var r in rows) r[j] = sd < 1e-9 ? 0 : (r[j] - mean) / sd;
            }
        }

        private static double Cost(List<double[]> rows, int from, int to)
        {
            int d = rows[0].Length, n = to - from;
            if (n <= 0) return 0;
            double cost = 0;
            for (int j = 0; j < d; j++)
            {
                double mean = 0;
                for (int i = from; i < to; i++) mean += rows[i][j];
                mean /= n;
                for (int i = from; i < to; i++) cost += (rows[i][j] - mean) * (rows[i][j] - mean);
            }
            return cost;
        }

        /// <summary>Recursive binary segmentation; accepts a cut only when it explains more variance than the penalty.</summary>
        private static void Segment(List<double[]> rows, int from, int to, int minLen, List<int> cuts, int budget)
        {
            if (cuts.Count >= budget || to - from < 2 * minLen) return;
            double whole = Cost(rows, from, to);
            int d = rows[0].Length;
            double penalty = d * (2 + Math.Log(rows.Count));
            int best = -1;
            double bestGain = 0;
            for (int c = from + minLen; c <= to - minLen; c++)
            {
                double gain = whole - Cost(rows, from, c) - Cost(rows, c, to);
                if (gain > bestGain) { bestGain = gain; best = c; }
            }
            if (best < 0 || bestGain <= penalty) return;
            cuts.Add(best);
            Segment(rows, from, best, minLen, cuts, budget);
            Segment(rows, best, to, minLen, cuts, budget);
        }
    }

    /// <summary>
    /// The fidelity holdout (plan "Evaluation", F321–F323): stratified by era × context kind (never "the 30 most recent"),
    /// chosen by a stable hash so the same pairs stay held out across versions (comparable fidelity trends), split into a
    /// tuning part (auto-tune) and a test part (reported), and made leakage-safe: every pair whose context shows a
    /// held-out reply, and every near-duplicate of a held-out reply, becomes a <see cref="PairSplits.Shadow"/> excluded
    /// from every prompt-visible source together with the holdout.
    /// </summary>
    public static class HoldoutPlanner
    {
        public sealed record Options(double Fraction = 0.1, int Min = 20, int Max = 300, double TuneShare = 0.35, double NearDuplicateJaccard = 0.85);

        public static bool Eligible(ReplicaPair p) =>
            !p.IsInitiation && p.StimulusPlain.Length > 0 && (p.Flags & (PairFlags.MediaOnly | PairFlags.LinkOnly)) == 0
            && TextFeatures.HasLetters(TextFeatures.StripMarkup(p.ReplyText));

        /// <summary>pairId → split for every pair (train unless held out or shadowed).</summary>
        public static Dictionary<string, string> Assign(IReadOnlyList<ReplicaPair> pairs, Options o)
        {
            var result = pairs.ToDictionary(p => p.PairId, _ => PairSplits.Train, StringComparer.Ordinal);
            var eligible = pairs.Where(Eligible).ToList();
            if (eligible.Count < 8) return result;
            int target = (int)Math.Round(o.Fraction * eligible.Count);
            target = Math.Clamp(target, Math.Min(o.Min, eligible.Count / 4), o.Max);
            if (target <= 0) return result;

            var strata = eligible.GroupBy(p => (p.EraIndex, p.ContextKind)).OrderBy(g => g.Key.EraIndex).ThenBy(g => g.Key.ContextKind, StringComparer.Ordinal)
                .Select(g => g.OrderBy(p => TextFeatures.Unit("holdout|" + p.PairId)).ToList()).ToList();
            var quota = strata.Select(s => (int)Math.Floor((double)s.Count * target / eligible.Count)).ToArray();
            int left = target - quota.Sum();
            foreach (int i in strata.Select((s, i) => (i, frac: (double)s.Count * target / eligible.Count - quota[i])).OrderByDescending(x => x.frac).ThenBy(x => x.i).Select(x => x.i))
            {
                if (left == 0) break;
                if (quota[i] < strata[i].Count) { quota[i]++; left--; }
            }
            var held = strata.SelectMany((s, i) => s.Take(quota[i])).ToList();
            foreach (var h in held) result[h.PairId] = TextFeatures.Unit("tune|" + h.PairId) < o.TuneShare ? PairSplits.Tune : PairSplits.Test;
            // Both parts non-empty when there is anything to split.
            if (held.Count >= 4)
            {
                if (held.All(h => result[h.PairId] == PairSplits.Test)) result[held[0].PairId] = PairSplits.Tune;
                if (held.All(h => result[h.PairId] == PairSplits.Tune)) result[held[^1].PairId] = PairSplits.Test;
            }

            // Shadows: context that shows a held-out reply, and near-duplicates of held-out replies.
            var heldReplyIds = held.SelectMany(h => h.ReplyIds).ToHashSet(StringComparer.Ordinal);
            foreach (var p in pairs)
            {
                if (result[p.PairId] != PairSplits.Train) continue;
                if (p.StimulusIds.Any(heldReplyIds.Contains) || p.ContextIds.Any(heldReplyIds.Contains)) result[p.PairId] = PairSplits.Shadow;
            }
            foreach (var shadow in NearDuplicates(pairs.Where(p => result[p.PairId] == PairSplits.Train).ToList(), held, o.NearDuplicateJaccard))
                result[shadow] = PairSplits.Shadow;
            return result;
        }

        /// <summary>Training pairs whose reply nearly duplicates a held-out reply (exact normalised copies of distinctive
        /// text, or 3-gram Jaccard above the threshold), found through an inverted shingle index.</summary>
        internal static IEnumerable<string> NearDuplicates(IReadOnlyList<ReplicaPair> train, IReadOnlyList<ReplicaPair> held, double threshold)
        {
            var normalized = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var shingles = new List<HashSet<string>>(train.Count);
            var inverted = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < train.Count; i++)
            {
                string norm = TextFeatures.Normalize(train[i].ReplyText);
                if (!normalized.TryGetValue(norm, out var list)) normalized[norm] = list = new List<string>();
                list.Add(train[i].PairId);
                var sh = norm.Length >= 20 ? TextFeatures.Shingles(norm) : new HashSet<string>(StringComparer.Ordinal);
                shingles.Add(sh);
                foreach (var s in sh)
                {
                    if (!inverted.TryGetValue(s, out var posting)) inverted[s] = posting = new List<int>();
                    posting.Add(i);
                }
            }
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in held)
            {
                string norm = TextFeatures.Normalize(h.ReplyText);
                if (norm.Length >= 12 && normalized.TryGetValue(norm, out var copies)) foreach (var c in copies) found.Add(c);
                if (norm.Length < 20) continue;
                var hs = TextFeatures.Shingles(norm);
                var overlap = new Dictionary<int, int>();
                foreach (var s in hs)
                    if (inverted.TryGetValue(s, out var posting))
                        foreach (int i in posting) overlap[i] = overlap.GetValueOrDefault(i) + 1;
                foreach (var (i, shared) in overlap)
                {
                    if (shared < hs.Count * threshold * 0.5) continue;
                    if (TextFeatures.Jaccard(hs, shingles[i]) >= threshold) found.Add(train[i].PairId);
                }
            }
            return found;
        }
    }

    /// <summary>A persona extraction slice: contiguous visible pairs that fill one LLM call, inside one era and at most
    /// a few months long. Items extracted from it become visible to an as-of only once the slice has ended (plan T6:
    /// an as-of replica never sees a persona line written from evidence after its cutoff, F288/F289).</summary>
    public sealed record SlicePlan(int Index, long FromMs, long ToMs, IReadOnlyList<string> PairIds, int EstimatedTokens, string ContentHash);

    public static class SlicePlanner
    {
        public static IReadOnlyList<SlicePlan> Plan(IReadOnlyList<ReplicaPair> visible, int tokenBudget, TimeSpan maxDuration, TokenEstimator estimator, string promptVersion)
        {
            var slices = new List<SlicePlan>();
            var current = new List<ReplicaPair>();
            int tokens = 0;
            void Close()
            {
                if (current.Count == 0) return;
                string hash = TextFeatures.Hash(promptVersion + "|" + string.Join("|", current.Select(p => p.PairId + ":" + p.TextHash)));
                slices.Add(new SlicePlan(slices.Count, current[0].ReplyAtMs, current.Max(p => p.ReplyEndMs), current.Select(p => p.PairId).ToList(), tokens, hash));
                current = new List<ReplicaPair>();
                tokens = 0;
            }
            foreach (var p in visible.OrderBy(p => p.ReplyAtMs).ThenBy(p => p.PairId, StringComparer.Ordinal))
            {
                int cost = estimator.Estimate(p.Rendered) + 6;
                bool overBudget = current.Count > 0 && tokens + cost > tokenBudget;
                bool newEra = current.Count > 0 && p.EraIndex != current[^1].EraIndex;
                bool tooLong = current.Count > 0 && p.ReplyAtMs - current[0].ReplyAtMs > (long)maxDuration.TotalMilliseconds;
                if (overBudget || newEra || tooLong) Close();
                current.Add(p);
                tokens += cost;
            }
            Close();
            return slices;
        }
    }
}
