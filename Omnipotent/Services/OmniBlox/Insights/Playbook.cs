using Omnipotent.Services.OmniBlox.Core;

namespace Omnipotent.Services.OmniBlox.Insights
{
    /// <summary>
    /// The studio's memory of what worked: lessons from post-mortems, maintenance and builds. Research reads
    /// it before choosing an idea; Build and Launch read it before making choices. Lessons that repeat are
    /// merged and gain confidence instead of piling up.
    /// </summary>
    public sealed class Playbook
    {
        public const int MaxLessons = 600;

        public static readonly string[] Topics = { "genre", "mechanic", "pricing", "marketing", "build", "test", "maintenance", "audience" };

        private readonly OmniBloxStore store;
        private readonly Func<DateTime> clock;

        public Playbook(OmniBloxStore store, Func<DateTime> clock)
        {
            this.store = store;
            this.clock = clock;
        }

        /// <summary>Adds a lesson, or strengthens a near-duplicate. Returns the stored lesson's id.</summary>
        public string Add(string topic, string text, string evidence, string outcome, double confidence, IEnumerable<string>? tags, string? gameId)
        {
            text = text.Trim();
            if (text.Length < 8) throw new ArgumentException("A lesson needs a real sentence.");
            topic = Topics.Contains(topic.Trim().ToLowerInvariant()) ? topic.Trim().ToLowerInvariant() : "build";
            outcome = outcome is "worked" or "failed" ? outcome : "neutral";
            var tagList = (tags ?? Enumerable.Empty<string>()).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().Take(10).ToList();
            return store.Mutate(s =>
            {
                var words = Words(text);
                var twin = s.Playbook.FirstOrDefault(l => l.Topic == topic && Similarity(words, Words(l.Text)) >= 0.7);
                if (twin != null)
                {
                    twin.Confidence = Math.Min(0.95, twin.Confidence + 0.1);
                    if (!string.IsNullOrWhiteSpace(evidence) && !twin.Evidence.Contains(evidence, StringComparison.Ordinal))
                        twin.Evidence = Clip((twin.Evidence + " | " + evidence).Trim(' ', '|'), 800);
                    foreach (var t in tagList) if (!twin.Tags.Contains(t)) twin.Tags.Add(t);
                    twin.Utc = clock();
                    s.MarkPlaybook();
                    return twin.LessonId;
                }
                var lesson = new PlaybookLesson
                {
                    LessonId = "l_" + Guid.NewGuid().ToString("N")[..10],
                    Utc = clock(),
                    GameId = gameId,
                    Topic = topic,
                    Text = Clip(text, 500),
                    Evidence = Clip(evidence ?? "", 800),
                    Outcome = outcome,
                    Confidence = Math.Clamp(confidence, 0.05, 0.95),
                    Tags = tagList,
                };
                s.Playbook.Add(lesson);
                if (s.Playbook.Count > MaxLessons)
                {
                    // Drop the weakest, oldest lessons first.
                    var drop = s.Playbook.OrderBy(l => l.Confidence + l.TimesUsed * 0.02).ThenBy(l => l.Utc).Take(s.Playbook.Count - MaxLessons).ToList();
                    foreach (var l in drop) s.Playbook.Remove(l);
                }
                s.MarkPlaybook();
                return lesson.LessonId;
            });
        }

        public List<PlaybookLesson> Search(string query, string? topic = null, int limit = 8)
        {
            var q = Words(query);
            return store.Read(s => s.Playbook
                .Where(l => topic == null || l.Topic == topic)
                .Select(l => (Lesson: l, Score: Score(l, q)))
                .Where(x => q.Count == 0 || x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Lesson.Confidence)
                .Take(limit)
                .Select(x => OmniBloxStore.Clone(x.Lesson))
                .ToList());
        }

        /// <summary>The lessons a stage should read, biased to the game's genre/template.</summary>
        public List<PlaybookLesson> ForStage(GameStage stage, string? genreOrTemplate, int limit = 6)
        {
            var topics = stage switch
            {
                GameStage.Research => new[] { "genre", "audience", "pricing", "marketing" },
                GameStage.Build => new[] { "build", "mechanic", "test" },
                GameStage.Test => new[] { "test", "build" },
                GameStage.Launch => new[] { "marketing", "pricing" },
                _ => new[] { "maintenance", "mechanic", "pricing", "marketing" },
            };
            var genreWords = Words(genreOrTemplate ?? "");
            var chosen = store.Read(s => s.Playbook
                .Where(l => topics.Contains(l.Topic))
                .OrderByDescending(l => l.Confidence + (genreWords.Count > 0 && (Words(string.Join(" ", l.Tags) + " " + l.Text).Overlaps(genreWords)) ? 0.5 : 0) + (l.Outcome == "failed" ? 0.05 : 0))
                .ThenByDescending(l => l.Utc)
                .Take(limit)
                .Select(OmniBloxStore.Clone)
                .ToList());
            if (chosen.Count > 0)
            {
                var ids = chosen.Select(l => l.LessonId).ToHashSet();
                store.Mutate(s =>
                {
                    foreach (var l in s.Playbook.Where(l => ids.Contains(l.LessonId))) l.TimesUsed++;
                    s.MarkPlaybook();
                });
            }
            return chosen;
        }

        public static string Format(IEnumerable<PlaybookLesson> lessons) =>
            string.Join("\n", lessons.Select(l => $"- [{l.Topic}, {l.Outcome}, confidence {l.Confidence:0.##}] {l.Text}" + (string.IsNullOrWhiteSpace(l.Evidence) ? "" : $" (evidence: {Clip(l.Evidence, 160)})")));

        private static double Score(PlaybookLesson l, HashSet<string> query)
        {
            if (query.Count == 0) return l.Confidence;
            var words = Words(l.Text + " " + string.Join(" ", l.Tags) + " " + l.Topic);
            int hits = query.Count(words.Contains);
            return hits == 0 ? 0 : hits / (double)query.Count + l.Confidence * 0.3;
        }

        internal static HashSet<string> Words(string text)
        {
            var stop = new HashSet<string> { "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "is", "it", "be", "with", "that", "this", "are", "was", "at", "as", "by" };
            return text.ToLowerInvariant()
                .Split(new[] { ' ', ',', '.', ';', ':', '!', '?', '(', ')', '"', '\'', '-', '/', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !stop.Contains(w))
                .Select(w => w.TrimEnd('s'))
                .ToHashSet();
        }

        private static double Similarity(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;
            int common = a.Count(b.Contains);
            return common / (double)Math.Max(a.Count, b.Count);
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
