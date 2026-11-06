using System.Globalization;
using System.Text.Json.Serialization;

namespace Omnipotent.Services.OmniscienceV3.Kernel.Tactics
{
    /// <summary>What a selection holds (plan X-engine "accepts"). Ids are corpus natural keys (CorpusIds).</summary>
    public enum SelectionKind
    {
        Person,
        Message,
        Conversation,
        Guild,
        /// <summary>An image or other attachment (attachment id).</summary>
        Attachment,
        Face,
        FaceCluster,
        /// <summary>A fact or inference (assertion id).</summary>
        Assertion,
        /// <summary>A half-open UTC window, id <c>"{fromMs}..{toMs}"</c>.</summary>
        TimeRange,
        /// <summary>A drill token from an Explore result (the exact records behind a chart point or range).</summary>
        ExploreDrill,
        Dataset,
        /// <summary>A free-text question (the item's label is the text).</summary>
        Question,
    }

    /// <summary>One member of a selection, with an optional score (relevance, probability, rank value).</summary>
    public sealed record SelectionItem(string Id, double? Score = null, string? Label = null);

    /// <summary>
    /// A typed set: the input and output of every tactic, so any output feeds any other tactic (chaining).
    /// <see cref="Sampled"/> discloses that a too-large selection was stratified-sampled down from
    /// <see cref="TotalBeforeSampling"/> items (plan X-engine edge cases).
    /// </summary>
    public sealed record Selection(SelectionKind Kind, IReadOnlyList<SelectionItem> Items, string? Origin = null, bool Sampled = false, long? TotalBeforeSampling = null)
    {
        [JsonIgnore]
        public int Count => Items.Count;

        public static Selection Of(SelectionKind kind, IEnumerable<string> ids, string? origin = null) =>
            new(kind, ids.Distinct(StringComparer.Ordinal).Select(i => new SelectionItem(i)).ToList(), origin);

        public static Selection Window(long fromMs, long toMs) =>
            fromMs < toMs ? new(SelectionKind.TimeRange, new[] { new SelectionItem(TimeRangeId(fromMs, toMs)) })
                          : throw new ArgumentException("A time range must end after it starts.");

        public static string TimeRangeId(long fromMs, long toMs) =>
            fromMs.ToString(CultureInfo.InvariantCulture) + ".." + toMs.ToString(CultureInfo.InvariantCulture);

        public static bool TryParseTimeRange(string id, out long fromMs, out long toMs)
        {
            fromMs = toMs = 0;
            int sep = id.IndexOf("..", StringComparison.Ordinal);
            return sep > 0
                   && long.TryParse(id.AsSpan(0, sep), NumberStyles.Integer, CultureInfo.InvariantCulture, out fromMs)
                   && long.TryParse(id.AsSpan(sep + 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out toMs)
                   && fromMs < toMs;
        }
    }

    /// <summary>Set operations over two selections of the same kind (X27).</summary>
    public enum SetOperation { Union, Intersect, Difference }

    public enum SortOrder { ScoreDescending, ScoreAscending, Label, Id }

    /// <summary>
    /// Set algebra over selections (plan "Set algebra works on any two sets: union / intersect / difference /
    /// filter / sort / top-k"). People are compared by their canonical id, so a merged person is one member, not
    /// two. Scores: a union keeps the higher score, an intersection the lower (both sets must agree), a difference
    /// keeps the left side's.
    /// </summary>
    public static class SetAlgebra
    {
        public static Selection Combine(Selection a, Selection b, SetOperation op, Func<string, string>? canonicalPerson = null)
        {
            if (a.Kind != b.Kind) throw new ArgumentException($"Cannot combine a {a.Kind} selection with a {b.Kind} selection.");
            if (a.Kind == SelectionKind.TimeRange) return CombineRanges(a, b, op);
            Func<string, string> key = a.Kind == SelectionKind.Person && canonicalPerson != null ? canonicalPerson : id => id;
            var left = Normalize(a, key);
            var right = Normalize(b, key);
            IEnumerable<SelectionItem> items = op switch
            {
                SetOperation.Union => left.Values.Concat(right.Values).GroupBy(i => i.Id, StringComparer.Ordinal)
                    .Select(g => g.Aggregate((x, y) => new SelectionItem(x.Id, Max(x.Score, y.Score), x.Label ?? y.Label))),
                SetOperation.Intersect => left.Values.Where(i => right.ContainsKey(i.Id))
                    .Select(i => new SelectionItem(i.Id, Min(i.Score, right[i.Id].Score), i.Label ?? right[i.Id].Label)),
                SetOperation.Difference => left.Values.Where(i => !right.ContainsKey(i.Id)),
                _ => throw new ArgumentOutOfRangeException(nameof(op)),
            };
            return new Selection(a.Kind, items.ToList(), $"{op.ToString().ToLowerInvariant()}({a.Origin ?? "a"}, {b.Origin ?? "b"})",
                a.Sampled || b.Sampled, a.Sampled || b.Sampled ? (a.TotalBeforeSampling ?? a.Count) + (b.TotalBeforeSampling ?? b.Count) : null);
        }

        public static Selection Sort(Selection s, SortOrder order) => s with
        {
            Items = (order switch
            {
                SortOrder.ScoreDescending => s.Items.OrderByDescending(i => i.Score ?? double.NegativeInfinity).ThenBy(i => i.Id, StringComparer.Ordinal),
                SortOrder.ScoreAscending => s.Items.OrderBy(i => i.Score ?? double.PositiveInfinity).ThenBy(i => i.Id, StringComparer.Ordinal),
                SortOrder.Label => s.Items.OrderBy(i => i.Label ?? i.Id, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id, StringComparer.Ordinal),
                _ => s.Items.OrderBy(i => i.Id, StringComparer.Ordinal),
            }).ToList(),
        };

        /// <summary>The <paramref name="k"/> highest-scoring items (ties broken by id, so the result is stable).</summary>
        public static Selection TopK(Selection s, int k)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(k);
            var sorted = Sort(s, SortOrder.ScoreDescending);
            return sorted with { Items = sorted.Items.Take(k).ToList() };
        }

        public static Selection Filter(Selection s, Func<SelectionItem, bool> keep) => s with { Items = s.Items.Where(keep).ToList() };

        /// <summary>
        /// Shrinks a selection to at most <paramref name="max"/> items while keeping each stratum's share (for example
        /// messages by month, so a long history is not represented only by its busiest period). Deterministic for a
        /// seed; the result discloses the sampling.
        /// </summary>
        public static Selection Stratify(Selection s, int max, Func<SelectionItem, string> stratum, int seed = 17)
        {
            if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max));
            if (s.Count <= max) return s;
            var rng = new Random(seed);
            var groups = s.Items.GroupBy(stratum).Select(g => g.OrderBy(_ => rng.Next()).ToList()).OrderBy(g => stratum(g[0]), StringComparer.Ordinal).ToList();
            var quota = groups.Select(g => (int)Math.Floor((double)g.Count * max / s.Count)).ToArray();
            int left = max - quota.Sum();
            // Hand out the remainder by largest fractional share, so every stratum gets its fair rounding.
            foreach (int i in groups.Select((g, i) => (i, frac: (double)g.Count * max / s.Count - quota[i])).OrderByDescending(x => x.frac).ThenBy(x => x.i).Select(x => x.i))
            {
                if (left == 0) break;
                if (quota[i] < groups[i].Count) { quota[i]++; left--; }
            }
            var items = groups.SelectMany((g, i) => g.Take(quota[i])).ToList();
            return s with { Items = items, Sampled = true, TotalBeforeSampling = s.TotalBeforeSampling ?? s.Count };
        }

        private static Dictionary<string, SelectionItem> Normalize(Selection s, Func<string, string> key)
        {
            var map = new Dictionary<string, SelectionItem>(StringComparer.Ordinal);
            foreach (var item in s.Items)
            {
                string id = key(item.Id);
                map[id] = map.TryGetValue(id, out var existing)
                    ? new SelectionItem(id, Max(existing.Score, item.Score), existing.Label ?? item.Label)
                    : item with { Id = id };
            }
            return map;
        }

        /// <summary>Time ranges combine as interval sets: union merges overlaps, intersection overlaps them, difference cuts.</summary>
        private static Selection CombineRanges(Selection a, Selection b, SetOperation op)
        {
            var left = Ranges(a);
            var right = Ranges(b);
            var result = op switch
            {
                SetOperation.Union => Merge(left.Concat(right)),
                SetOperation.Intersect => Merge(left.SelectMany(l => right.Select(r => (From: Math.Max(l.From, r.From), To: Math.Min(l.To, r.To)))).Where(x => x.From < x.To)),
                SetOperation.Difference => Merge(right.Aggregate((IEnumerable<(long From, long To)>)Merge(left), (acc, cut) => acc.SelectMany(x => Cut(x, cut)).ToList())),
                _ => throw new ArgumentOutOfRangeException(nameof(op)),
            };
            return new Selection(SelectionKind.TimeRange, result.Select(r => new SelectionItem(Selection.TimeRangeId(r.From, r.To))).ToList(),
                $"{op.ToString().ToLowerInvariant()}({a.Origin ?? "a"}, {b.Origin ?? "b"})");
        }

        private static List<(long From, long To)> Ranges(Selection s) =>
            s.Items.Select(i => Selection.TryParseTimeRange(i.Id, out long f, out long t) ? (f, t) : throw new ArgumentException($"'{i.Id}' is not a time range.")).ToList();

        private static List<(long From, long To)> Merge(IEnumerable<(long From, long To)> ranges)
        {
            var result = new List<(long From, long To)>();
            foreach (var r in ranges.OrderBy(r => r.From))
            {
                if (result.Count > 0 && r.From <= result[^1].To) result[^1] = (result[^1].From, Math.Max(result[^1].To, r.To));
                else result.Add(r);
            }
            return result;
        }

        private static IEnumerable<(long From, long To)> Cut((long From, long To) x, (long From, long To) cut)
        {
            if (cut.To <= x.From || cut.From >= x.To) { yield return x; yield break; }
            if (cut.From > x.From) yield return (x.From, cut.From);
            if (cut.To < x.To) yield return (cut.To, x.To);
        }

        private static double? Max(double? a, double? b) => a == null ? b : b == null ? a : Math.Max(a.Value, b.Value);
        private static double? Min(double? a, double? b) => a == null ? b : b == null ? a : Math.Min(a.Value, b.Value);
    }
}
