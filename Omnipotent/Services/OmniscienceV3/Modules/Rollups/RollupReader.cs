using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.Rollups
{
    public sealed record PersonDayRow(string Day, string Zone, bool ZoneKnown, int Msgs, int Authored, int Deleted, int Edited, int Replies, int Forwarded, int NonAuthored,
        int WithAttachments, int WithLinks, long Chars, long Words, int DmMsgs, int GroupMsgs, int ServerMsgs, int DayOnly, int ReactionsGiven, int MentionsMade,
        long? FirstMs, long? LastMs, int[] Hours, Dictionary<string, Dictionary<string, int>> Labels, Dictionary<string, int> Conversations, Dictionary<string, int> Guilds,
        int? Labelset);

    public sealed record PairTotals(string Partner, int RepliesOut, int RepliesIn, int MentionsOut, int MentionsIn, int DmOut, int DmIn, int ReactionsOut, int ReactionsIn,
        int[] LatencyOut, int[] LatencyIn, int ActiveDays, Dictionary<string, Dictionary<string, int>> LabelsOut, Dictionary<string, Dictionary<string, int>> LabelsIn)
    {
        public int Interactions => RepliesOut + RepliesIn + MentionsOut + MentionsIn + DmOut + DmIn + ReactionsOut + ReactionsIn;
    }

    public sealed record ScopeDayRow(string Day, int Msgs, int Authored, int Deleted, int People, Dictionary<string, int> Persons, Dictionary<string, Dictionary<string, int>> Labels,
        Dictionary<string, int> Platforms, long? FirstMs, long? LastMs, int? Labelset);

    /// <summary>Read API over the rollup tables for projections, analytics and tactics (days are "yyyy-MM-dd", ranges inclusive).</summary>
    public static class RollupReader
    {
        private static readonly JsonSerializerOptions Json = new();

        private static T Parse<T>(string s) where T : new() => string.IsNullOrEmpty(s) ? new T() : JsonSerializer.Deserialize<T>(s, Json) ?? new T();

        private static int[] Ints(string s) => string.IsNullOrEmpty(s) ? Array.Empty<int>() : JsonSerializer.Deserialize<int[]>(s, Json) ?? Array.Empty<int>();

        public static List<PersonDayRow> PersonDays(SqliteConnection intel, string person, string? fromDay = null, string? toDay = null) =>
            intel.Query(null, """
                SELECT day, zone, zone_known, msgs, authored, deleted, edited, replies, forwarded, non_authored, with_attachments, with_links, chars, words, dm_msgs,
                       group_msgs, server_msgs, day_only, reactions_given, mentions_made, first_ms, last_ms, hours, labels, conversations, guilds, labelset
                FROM rollup_person_day WHERE person_id=$p AND ($a IS NULL OR day >= $a) AND ($b IS NULL OR day <= $b) ORDER BY day
                """, r => new PersonDayRow(r.GetString(0), r.GetString(1), r.GetInt64(2) == 1, r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7),
                    r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetInt32(11), r.GetInt64(12), r.GetInt64(13), r.GetInt32(14), r.GetInt32(15), r.GetInt32(16),
                    r.GetInt32(17), r.GetInt32(18), r.GetInt32(19), r.GetInt64OrNull(20), r.GetInt64OrNull(21), Ints(r.GetString(22)) is { Length: 24 } h ? h : new int[24],
                    Parse<Dictionary<string, Dictionary<string, int>>>(r.GetString(23)), Parse<Dictionary<string, int>>(r.GetString(24)),
                    Parse<Dictionary<string, int>>(r.GetString(25)), (int?)r.GetInt64OrNull(26)),
                ("$p", person), ("$a", fromDay), ("$b", toDay));

        /// <summary>Interactions with every partner in a range, both directions (outgoing from the person's cells, incoming from partners').</summary>
        public static List<PairTotals> Pairs(SqliteConnection intel, string person, string? fromDay = null, string? toDay = null)
        {
            var acc = new Dictionary<string, (int[] Counts, int[] LatOut, int[] LatIn, HashSet<string> Days, Dictionary<string, Dictionary<string, int>> LOut, Dictionary<string, Dictionary<string, int>> LIn)>(StringComparer.Ordinal);
            (int[] Counts, int[] LatOut, int[] LatIn, HashSet<string> Days, Dictionary<string, Dictionary<string, int>> LOut, Dictionary<string, Dictionary<string, int>> LIn) Get(string p) =>
                acc.TryGetValue(p, out var v) ? v : acc[p] = (new int[8], new int[16], new int[16], new HashSet<string>(StringComparer.Ordinal), new(), new());
            void Merge(Dictionary<string, Dictionary<string, int>> into, string json)
            {
                foreach (var (task, labels) in Parse<Dictionary<string, Dictionary<string, int>>>(json))
                {
                    if (!into.TryGetValue(task, out var t)) into[task] = t = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var (l, n) in labels) t[l] = t.GetValueOrDefault(l) + n;
                }
            }
            const string Columns = "partner_id, person_id, day, replies, mentions, dm_msgs, reactions, latency_explicit, latency_implicit, labels";
            foreach (var r in intel.Query(null, $"SELECT {Columns} FROM rollup_pair_day WHERE person_id=$p AND ($a IS NULL OR day >= $a) AND ($b IS NULL OR day <= $b)",
                         x => (Other: x.GetString(0), Day: x.GetString(2), R: x.GetInt32(3), M: x.GetInt32(4), D: x.GetInt32(5), Re: x.GetInt32(6), Le: x.GetString(7), Li: x.GetString(8), L: x.GetString(9)),
                         ("$p", person), ("$a", fromDay), ("$b", toDay)))
            {
                var a = Get(r.Other);
                a.Counts[0] += r.R; a.Counts[2] += r.M; a.Counts[4] += r.D; a.Counts[6] += r.Re;
                foreach (var (i, n) in Ints(r.Le).Select((n, i) => (i, n))) a.LatOut[i] += n;
                foreach (var (i, n) in Ints(r.Li).Select((n, i) => (i, n))) a.LatOut[i] += n;
                a.Days.Add(r.Day);
                Merge(a.LOut, r.L);
            }
            foreach (var r in intel.Query(null, $"SELECT {Columns} FROM rollup_pair_day WHERE partner_id=$p AND ($a IS NULL OR day >= $a) AND ($b IS NULL OR day <= $b)",
                         x => (Other: x.GetString(1), Day: x.GetString(2), R: x.GetInt32(3), M: x.GetInt32(4), D: x.GetInt32(5), Re: x.GetInt32(6), Le: x.GetString(7), Li: x.GetString(8), L: x.GetString(9)),
                         ("$p", person), ("$a", fromDay), ("$b", toDay)))
            {
                var a = Get(r.Other);
                a.Counts[1] += r.R; a.Counts[3] += r.M; a.Counts[5] += r.D; a.Counts[7] += r.Re;
                foreach (var (i, n) in Ints(r.Le).Select((n, i) => (i, n))) a.LatIn[i] += n;
                foreach (var (i, n) in Ints(r.Li).Select((n, i) => (i, n))) a.LatIn[i] += n;
                a.Days.Add(r.Day);
                Merge(a.LIn, r.L);
            }
            return acc.Select(kv => new PairTotals(kv.Key, kv.Value.Counts[0], kv.Value.Counts[1], kv.Value.Counts[2], kv.Value.Counts[3], kv.Value.Counts[4],
                    kv.Value.Counts[5], kv.Value.Counts[6], kv.Value.Counts[7], kv.Value.LatOut, kv.Value.LatIn, kv.Value.Days.Count, kv.Value.LOut, kv.Value.LIn))
                .OrderByDescending(p => p.Interactions).ThenBy(p => p.Partner, StringComparer.Ordinal).ToList();
        }

        public static List<ScopeDayRow> ScopeDays(SqliteConnection intel, string scope, string key, string? fromDay = null, string? toDay = null) =>
            intel.Query(null, """
                SELECT day, msgs, authored, deleted, people, persons, labels, platforms, first_ms, last_ms, labelset FROM rollup_scope_day
                WHERE scope=$s AND key=$k AND ($a IS NULL OR day >= $a) AND ($b IS NULL OR day <= $b) ORDER BY day
                """, r => new ScopeDayRow(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), Parse<Dictionary<string, int>>(r.GetString(5)),
                    Parse<Dictionary<string, Dictionary<string, int>>>(r.GetString(6)), Parse<Dictionary<string, int>>(r.GetString(7)), r.GetInt64OrNull(8), r.GetInt64OrNull(9),
                    (int?)r.GetInt64OrNull(10)),
                ("$s", scope), ("$k", key), ("$a", fromDay), ("$b", toDay));

        public static List<ZoneMonth> Zones(SqliteConnection intel, string person) =>
            intel.Query(null, "SELECT month, zone, offset_minutes, confidence, method, alternatives FROM rollup_zones WHERE person_id=$p ORDER BY month",
                r => new ZoneMonth(r.GetString(0), r.GetStringOrNull(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.GetDouble(3), r.GetString(4),
                    JsonSerializer.Deserialize<List<string>>(r.GetStringOrNull(5) ?? "[]") ?? new List<string>()),
                ("$p", person));

        /// <summary>Sums per-task label counts over day rows (computed with <paramref name="labelset"/> only, so versions never mix).</summary>
        public static Dictionary<string, Dictionary<string, int>> SumLabels(IEnumerable<(Dictionary<string, Dictionary<string, int>> Labels, int? Labelset)> rows, int? labelset)
        {
            var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (var (labels, ls) in rows)
            {
                if (labelset != null && ls != labelset) continue;
                foreach (var (task, counts) in labels)
                {
                    if (!result.TryGetValue(task, out var t)) result[task] = t = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var (l, n) in counts) t[l] = t.GetValueOrDefault(l) + n;
                }
            }
            return result;
        }
    }
}
