using System.Globalization;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Ml.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Monitors
{
    /// <summary>
    /// Where a topic stands against its usual (X43): <c>last24h</c> counts messages that arrived live in the last day;
    /// <c>days</c> are the 28 complete days before today (Klives's days), from which the usual day is the median.
    /// </summary>
    [Api.ApiContract]
    public sealed record TopicStatusDto(string WatchId, string Name, int Last24h, double UsualDay, double RobustZ, bool Surging, int Threshold, IReadOnlyList<int> Days, IReadOnlyList<string> RecentMessageIds);

    public sealed partial class MonitorsModule
    {
        private static double Median(IReadOnlyList<double> values)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        }

        private async Task<string?> SurgesAsync(JobContext job)
        {
            var ct = job.Cancellation;
            int topics = 0, tones = 0;
            foreach (var rule in LoadRules<TopicRuleDefinition>(TopicRuleKind).Where(r => r.Enabled))
            {
                ct.ThrowIfCancellationRequested();
                var status = TopicStatus(rule);
                if (!status.Surging) continue;
                string day = KlivesDay(Context.Clock.NowMs);
                string? guild = rule.Definition.Guilds.Count == 1 && rule.Definition.Conversations.Count == 0 ? rule.Definition.Guilds[0] : null;
                string? conversation = rule.Definition.Conversations.Count == 1 && rule.Definition.Guilds.Count == 0 ? rule.Definition.Conversations[0] : null;
                if (await FireAsync($"x43|{rule.Id}|{day}", "topic_surge", rule.Id, null, guild, conversation, Context.Clock.NowMs,
                        new { ruleId = rule.Id, ruleName = rule.Name, count = status.Last24h, usualDay = status.UsualDay, z = status.RobustZ, threshold = status.Threshold, messageIds = status.RecentMessageIds },
                        NoticeSeverity.Attention, false, ct).ConfigureAwait(false))
                    topics++;
            }
            if (Context.Settings.Get("monitors.klives_tone.enabled", true)) tones = await KlivesToneAsync(ct).ConfigureAwait(false);
            return Json(new { topics, tones });
        }

        /// <summary>Counts a topic: live messages of the last 24 hours, and every message of each of the 28 days before today.</summary>
        internal TopicStatusDto TopicStatus(Rule<TopicRuleDefinition> rule)
        {
            long now = Context.Clock.NowMs;
            var zone = Context.Settings.KlivesZone;
            var today = Zones.LocalDate(now, zone);
            long from = Zones.DayStartUtcMs(today.AddDays(-28), zone), todayStart = Zones.DayStartUtcMs(today, zone);
            var d = rule.Definition;
            var off = AnalysisOff.Load(Context.Stores);
            var hits = new List<(string Id, long At, bool Live)>();
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                var where = new List<string> { "m.sent_at_ms >= $f", "m.deleted_at_ms IS NULL", "m.is_bot=0", "m.is_system=0", "m.is_webhook=0", "COALESCE(i.kind,'')<>'bot'" };
                var args = new List<(string, object?)> { ("$f", Math.Min(from, now - Day)) };
                if (d.Guilds.Count > 0 || d.Conversations.Count > 0)
                {
                    var parts = new List<string>();
                    if (d.Guilds.Count > 0) { var (list, a) = SqlHelpers.InList(d.Guilds, "$g"); parts.Add($"m.guild_id IN ({list})"); args.AddRange(a); }
                    if (d.Conversations.Count > 0) { var (list, a) = SqlHelpers.InList(d.Conversations, "$c"); parts.Add($"m.conversation_id IN ({list})"); args.AddRange(a); }
                    where.Add("(" + string.Join(" OR ", parts) + ")");
                }
                const string Select = "SELECT m.message_id, m.sent_at_ms, m.first_source_kind='live', m.author_identity_id FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id ";
                if (d.Phrase != null)
                {
                    // An FTS5 phrase: the words in order; the phrase is data, never query syntax.
                    args.Add(("$q", "\"" + d.Phrase.Replace("\"", "\"\"") + "\""));
                    hits = corpus.Query(null, Select + "JOIN messages_fts f ON f.rowid = m.rowid WHERE messages_fts MATCH $q AND " + string.Join(" AND ", where) + " LIMIT 200000",
                        r => (Id: r.GetString(0), At: r.GetInt64(1), Live: r.GetInt64(2) == 1, Author: r.GetString(3)), args.ToArray()).Where(x => !off.Author(x.Author)).Select(x => (x.Id, x.At, x.Live)).ToList();
                }
                else
                {
                    // Concept hits are labels of messages; the ones written into the label store since the window began are enough.
                    long seq = FirstChangeAtOrAfter(corpus, Math.Min(from, now - Day) - 7 * Day, ChangeLog.MaxSeq(corpus));
                    List<string> labelled;
                    using (var intel = Context.Stores.Intel.OpenRead())
                        labelled = intel.Query(null, "SELECT item_id FROM labels WHERE task=$t AND label='yes' AND item_kind='message' AND source_seq >= $s", r => r.GetString(0),
                            ("$t", ClassificationModule.ConceptTask(d.ConceptId!)), ("$s", seq));
                    foreach (var chunk in labelled.Distinct(StringComparer.Ordinal).Chunk(400))
                    {
                        var (list, a) = SqlHelpers.InList(chunk, "$k");
                        hits.AddRange(corpus.Query(null, Select + $"WHERE m.message_id IN ({list}) AND " + string.Join(" AND ", where),
                            r => (Id: r.GetString(0), At: r.GetInt64(1), Live: r.GetInt64(2) == 1, Author: r.GetString(3)), a.Concat(args).ToArray())
                            .Where(x => !off.Author(x.Author)).Select(x => (x.Id, x.At, x.Live)));
                    }
                }
            }
            var counts = new int[28];
            foreach (var h in hits.Where(h => h.At >= from && h.At < todayStart))
            {
                int index = Zones.LocalDate(h.At, zone).DayNumber - today.AddDays(-28).DayNumber;
                if (index is >= 0 and < 28) counts[index]++;
            }
            var recent = hits.Where(h => h.Live && h.At >= now - Day && h.At <= now).OrderByDescending(h => h.At).ThenBy(h => h.Id, StringComparer.Ordinal).ToList();
            double median = Median(counts.Select(c => (double)c).ToList());
            double mad = Median(counts.Select(c => Math.Abs(c - median)).ToList());
            double z = Math.Round((recent.Count - median) / (1.4826 * Math.Max(mad, 1)), 2);
            int threshold = (int)Math.Ceiling(Math.Max(d.MinCount ?? Context.Settings.Get("monitors.surge.min_count", 10), (d.Factor ?? Context.Settings.Get("monitors.surge.factor", 3.0)) * Math.Max(median, 1)));
            bool surging = recent.Count >= threshold && z >= 3.5;
            return new TopicStatusDto(rule.Id, rule.Name, recent.Count, median, z, surging, threshold, counts, recent.Take(10).Select(h => h.Id).ToList());
        }

        public TopicStatusDto TopicStatus(string watchId)
        {
            var rule = LoadRules<TopicRuleDefinition>(TopicRuleKind, watchId).FirstOrDefault() ?? throw new KeyNotFoundException($"No topic watch '{watchId}'.");
            return TopicStatus(rule);
        }

        /// <summary>
        /// X47: in each community, the messages of the last hours that were to or about Klives and read as negative (or
        /// positive), against how many such messages a day the community usually has. Direct chats are left to the radar.
        /// </summary>
        private async Task<int> KlivesToneAsync(CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            int windowHours = Context.Settings.Get("monitors.klives_tone.window_hours", 6), min = Context.Settings.Get("monitors.klives_tone.min_messages", 5);
            double factor = Context.Settings.Get("monitors.klives_tone.factor", 3.0);
            long windowMs = windowHours * Hour;
            List<(string Id, string Author, string? Person, string Conversation, string? Guild, string? Kind, long At)> recent;
            HashSet<string> self;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                self = ClassificationModule.SelfIdentities(corpus);
                recent = corpus.Query(null, """
                    SELECT m.message_id, m.author_identity_id, COALESCE(pc.canonical_id, i.person_id), m.conversation_id, m.guild_id, cv.kind, m.sent_at_ms
                    FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
                    LEFT JOIN conversations cv ON cv.conversation_id=m.conversation_id
                    WHERE m.sent_at_ms >= $f AND m.sent_at_ms <= $n AND m.first_source_kind='live' AND m.deleted_at_ms IS NULL AND m.is_bot=0 AND m.is_system=0 AND m.is_webhook=0 AND COALESCE(i.kind,'')<>'bot'
                    LIMIT 100000
                    """, r => (r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetString(3), r.GetStringOrNull(4), r.GetStringOrNull(5), r.GetInt64(6)), ("$f", now - windowMs), ("$n", now));
            }
            var off = AnalysisOff.Load(Context.Stores);
            recent = recent.Where(m => m.Kind != "dm" && !self.Contains(m.Author) && !off.Author(m.Author)).ToList();
            if (recent.Count == 0) return 0;
            var labels = ClassificationReader.ForMessages(Context.Stores, recent.Select(m => m.Id).ToList());
            var bursts = recent.Select(m => (M: m, L: labels.GetValueOrDefault(m.Id)))
                .Where(x => x.L != null && x.L.Answer(ClassificationTasks.AboutKlives.Id) is { } about && about != "none")
                .Select(x => (x.M, Tone: x.L!.Answer(ClassificationTasks.Sentiment.Id)))
                .Where(x => x.Tone is "negative" or "positive")
                .GroupBy(x => (Scope: x.M.Guild != null ? "guild" : "conversation", Key: x.M.Guild ?? x.M.Conversation, x.Tone!))
                .ToList();
            int fired = 0;
            var zone = Context.Settings.KlivesZone;
            var today = Zones.LocalDate(now, zone);
            string baselineFrom = RollupModule.Day(today.AddDays(-28)), baselineTo = RollupModule.Day(today.AddDays(-1));
            foreach (var burst in bursts)
            {
                var (scope, key, tone) = burst.Key;
                int count = burst.Count(), authors = burst.Select(x => x.M.Person ?? x.M.Author).Distinct(StringComparer.Ordinal).Count();
                if (count < min || authors < 2) continue;
                List<ScopeDayRow> days;
                using (var intel = Context.Stores.Intel.OpenRead()) days = RollupReader.ScopeDays(intel, scope, key, baselineFrom, baselineTo);
                var perDay = Enumerable.Range(0, 28).Select(i => RollupModule.Day(today.AddDays(-28 + i)))
                    .Select(day => (double)(days.FirstOrDefault(r => r.Day == day)?.Klives?.Sentiment.GetValueOrDefault(tone) ?? 0)).ToList();
                double expected = Median(perDay) * windowHours / 24.0;
                if (count < factor * Math.Max(expected, 0.5)) continue;
                long bucket = now / windowMs;
                string prefix = $"x47|{key}|{tone}|";
                using (var c = Context.Stores.Intel.OpenRead())
                    if (c.Scalar<long>(null, "SELECT COUNT(*) FROM mon_events WHERE event_id=$e", ("$e", prefix + (bucket - 1).ToString(CultureInfo.InvariantCulture))) > 0) continue; // the same burst, still going
                var ids = burst.OrderByDescending(x => x.M.At).ThenBy(x => x.M.Id, StringComparer.Ordinal).Select(x => x.M.Id).Take(20).ToList();
                if (await FireAsync(prefix + bucket.ToString(CultureInfo.InvariantCulture), "klives_tone", null, null, scope == "guild" ? key : null, scope == "conversation" ? key : null, now,
                        new { tone, count, authors, expected = Math.Round(expected, 2), windowHours, messageIds = ids }, tone == "negative" ? NoticeSeverity.Attention : NoticeSeverity.Info, false, ct)
                        .ConfigureAwait(false))
                    fired++;
            }
            return fired;
        }
    }
}
