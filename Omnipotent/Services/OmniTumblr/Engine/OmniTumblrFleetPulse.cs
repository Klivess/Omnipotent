using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Models;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    public sealed class EdgeDayDto
    {
        /// <summary>UTC calendar day, yyyy-MM-dd.</summary>
        public string Day { get; set; } = "";
        public double BlockedMinutes { get; set; }
        public int Blocks { get; set; }
        /// <summary>False for a day before the event log reaches back, or one only partly covered.</summary>
        public bool Covered { get; set; } = true;
    }

    public sealed class EdgeSpanDto
    {
        public DateTime FromUtc { get; set; }
        /// <summary>Null while the block is still going on.</summary>
        public DateTime? ToUtc { get; set; }
        /// <summary>The call Tumblr refused first, when the event recorded it (from 13 Nov 2026).</summary>
        public string? Trigger { get; set; }
    }

    public sealed class CountDto
    {
        public string Key { get; set; } = "";
        public int Count { get; set; }
    }

    /// <summary>
    /// How often Tumblr's edge refused this server, from the blocked/cleared events. In Nov 2026 it refused the
    /// server 69 % of the time over 16 days (298 blocks, 15–20 h a day), while the page only ever showed the
    /// block going on at that moment.
    /// </summary>
    public sealed class EdgeHistoryDto
    {
        /// <summary>The oldest event kept: nothing before it is known.</summary>
        public DateTime? CoverageStartUtc { get; set; }
        public double? AnsweredPct24h { get; set; }
        public double? AnsweredPct7d { get; set; }
        public double BlockedHours24h { get; set; }
        public double BlockedHours7d { get; set; }
        public int Blocks24h { get; set; }
        public int Blocks7d { get; set; }
        public double? MedianBlockMinutes { get; set; }
        public double? LongestBlockMinutes { get; set; }
        /// <summary>Median time Tumblr answered between two blocks, over the last 7 days.</summary>
        public double? MedianOpenMinutes { get; set; }
        public List<EdgeDayDto> Days { get; set; } = new();
        /// <summary>Blocks touching the last 72 hours, oldest first (the schedule's bands).</summary>
        public List<EdgeSpanDto> Spans { get; set; } = new();
        public List<CountDto> Triggers { get; set; } = new();
    }

    /// <summary>One blog's last seven days, from the event log: what went out and what did not.</summary>
    public sealed class BlogWeekDto
    {
        public string BlogId { get; set; } = "";
        public int Published { get; set; }
        public int Failed { get; set; }
        public int Missed { get; set; }
        /// <summary>Times a due post had to wait for Tumblr's edge.</summary>
        public int EdgeWaits { get; set; }
        public int ReblogsPicked { get; set; }
        /// <summary>Curator passes that found nothing worth reblogging.</summary>
        public int ReblogsEmpty { get; set; }
        public int TagsDropped { get; set; }
    }

    internal static class OmniTumblrFleetPulse
    {
        public const string BlockedKind = "tumblr.edge-blocked";
        public const string ClearedKind = "tumblr.edge-cleared";
        private static readonly Regex TriggerPattern = new(@"First refused: ([^\s.]+)", RegexOptions.Compiled);

        /// <summary>The refused call named in a blocked event ("tagged", "blog/posts:create"…), or null.</summary>
        internal static string? TriggerOf(string message)
        {
            var match = TriggerPattern.Match(message ?? "");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Pairs blocked/cleared events into spans. A block still open at the end is taken to last until
        /// <paramref name="now"/> when the gate says it is blocked, and to the next event otherwise.
        /// </summary>
        internal static List<EdgeSpanDto> Spans(IEnumerable<OmniTumblrEvent> events, DateTime now, TumblrEdgeGateStatus? gate)
        {
            var spans = new List<EdgeSpanDto>();
            EdgeSpanDto? open = null;
            foreach (var e in events.Where(e => e.Kind is BlockedKind or ClearedKind).OrderBy(e => e.Utc))
            {
                if (e.Kind == BlockedKind)
                {
                    if (open != null) continue; // a repeat while blocked (a restart): the block goes on
                    open = new EdgeSpanDto { FromUtc = e.Utc, Trigger = TriggerOf(e.Message) };
                }
                else if (open != null)
                {
                    open.ToUtc = e.Utc;
                    spans.Add(open);
                    open = null;
                }
            }
            if (open != null)
            {
                // Since restarts reopen the gate, an unclosed block with the gate open ended at an unknown time:
                // count it to the start of the gate's current open state if known, else leave it to "now".
                if (gate is { Blocked: false, LastClearedUtc: DateTime cleared } && cleared > open.FromUtc) open.ToUtc = cleared;
                else if (gate is { Blocked: false }) open.ToUtc = open.FromUtc; // unknown: count nothing
                spans.Add(open);
            }
            return spans;
        }

        public static EdgeHistoryDto EdgeHistory(IReadOnlyList<OmniTumblrEvent> events, DateTime now, TumblrEdgeGateStatus? gate, int days = 14)
        {
            var history = new EdgeHistoryDto();
            if (events.Count == 0) return history;
            DateTime coverage = events.Min(e => e.Utc);
            history.CoverageStartUtc = coverage;
            var spans = Spans(events, now, gate);

            double BlockedMinutes(DateTime from, DateTime to) => spans.Sum(sp =>
            {
                DateTime a = sp.FromUtc > from ? sp.FromUtc : from;
                DateTime end = sp.ToUtc ?? now;
                DateTime b = end < to ? end : to;
                return b > a ? (b - a).TotalMinutes : 0;
            });

            double? AnsweredPct(TimeSpan window)
            {
                DateTime from = now - window;
                if (from < coverage) from = coverage;
                double minutes = (now - from).TotalMinutes;
                if (minutes < 60) return null; // under an hour of record says nothing
                return Math.Round(100 * (1 - BlockedMinutes(from, now) / minutes), 1);
            }

            history.AnsweredPct24h = AnsweredPct(TimeSpan.FromHours(24));
            history.AnsweredPct7d = AnsweredPct(TimeSpan.FromDays(7));
            history.BlockedHours24h = Math.Round(BlockedMinutes(now.AddHours(-24), now) / 60, 1);
            history.BlockedHours7d = Math.Round(BlockedMinutes(now.AddDays(-7), now) / 60, 1);
            history.Blocks24h = spans.Count(sp => sp.FromUtc >= now.AddHours(-24));
            history.Blocks7d = spans.Count(sp => sp.FromUtc >= now.AddDays(-7));

            var closed = spans.Where(sp => sp.ToUtc != null && sp.FromUtc >= now.AddDays(-7)).Select(sp => (sp.ToUtc!.Value - sp.FromUtc).TotalMinutes).OrderBy(m => m).ToList();
            if (closed.Count > 0)
            {
                history.MedianBlockMinutes = Math.Round(closed[closed.Count / 2], 1);
                history.LongestBlockMinutes = Math.Round(closed[^1], 1);
            }
            var week = spans.Where(sp => sp.FromUtc >= now.AddDays(-7)).ToList();
            var gaps = week.Zip(week.Skip(1), (a, b) => a.ToUtc is DateTime end ? (b.FromUtc - end).TotalMinutes : -1).Where(m => m >= 0).OrderBy(m => m).ToList();
            if (gaps.Count > 0) history.MedianOpenMinutes = Math.Round(gaps[gaps.Count / 2], 1);

            for (int i = days - 1; i >= 0; i--)
            {
                DateTime day = now.Date.AddDays(-i);
                DateTime next = day.AddDays(1);
                history.Days.Add(new EdgeDayDto
                {
                    Day = day.ToString("yyyy-MM-dd"),
                    BlockedMinutes = Math.Round(BlockedMinutes(day, next < now ? next : now)),
                    Blocks = spans.Count(sp => sp.FromUtc >= day && sp.FromUtc < next),
                    Covered = day >= coverage.Date && (day > coverage || coverage.TimeOfDay < TimeSpan.FromHours(1)),
                });
            }

            DateTime since = now.AddHours(-72);
            history.Spans = spans.Where(sp => (sp.ToUtc ?? now) >= since && sp.ToUtc != sp.FromUtc).ToList();
            history.Triggers = spans.Where(sp => sp.FromUtc >= now.AddDays(-7) && sp.Trigger != null)
                .GroupBy(sp => sp.Trigger!).Select(g => new CountDto { Key = g.Key, Count = g.Count() })
                .OrderByDescending(c => c.Count).ToList();
            return history;
        }

        public static List<BlogWeekDto> Week(OmniTumblrState s, DateTime now)
        {
            DateTime from = now.AddDays(-7);
            var byBlog = s.Blogs.Keys.ToDictionary(id => id, id => new BlogWeekDto { BlogId = id }, StringComparer.Ordinal);
            foreach (var e in s.Events)
            {
                if (e.Utc < from || e.BlogId == null || !byBlog.TryGetValue(e.BlogId, out var week)) continue;
                switch (e.Kind)
                {
                    case "publish.ok": week.Published++; break;
                    case "publish.failed": week.Failed++; break;
                    case "publish.missed": week.Missed++; break;
                    case "publish.edge-wait": week.EdgeWaits++; break;
                    case "reblog.picked": week.ReblogsPicked++; break;
                    case "reblog.none": week.ReblogsEmpty++; break;
                    case "tags.dropped": week.TagsDropped++; break;
                }
            }
            return byBlog.Values.ToList();
        }
    }
}
