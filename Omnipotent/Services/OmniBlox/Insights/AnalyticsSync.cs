using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Insights
{
    /// <summary>
    /// Pulls Roblox's own numbers for each live game (Open Cloud analytics: players, visits, session length,
    /// retention, revenue, payers, peak concurrency, impressions and clicks) into the dossier, one day per row. Needs
    /// the API key with universe-analytics read; without it the dossier keeps only the game's own telemetry.
    /// </summary>
    public sealed class AnalyticsSync
    {
        /// <summary>
        /// Optional metrics are ones Roblox does not serve for every experience: peak concurrency is a performance
        /// metric, and the first live game got "Query is not available for Universe" for it on every sync, which sat in
        /// LastSyncError for good and would have hidden a real failure. A refusal of an optional metric is no data.
        /// </summary>
        private static readonly (string Metric, Action<DailyMetrics, double> Apply, bool Optional)[] Metrics =
        {
            ("DailyActiveUsers", (d, v) => d.DailyActiveUsers = v, false),
            ("Visits", (d, v) => d.Visits = v, false),
            ("AverageSessionLengthMinutes", (d, v) => d.AvgSessionMinutes = v, false),
            ("TotalPlayTimeHours", (d, v) => d.PlayHours = v, false),
            ("DailyRevenue", (d, v) => d.RevenueRobux = v, false),
            ("PayingUsers", (d, v) => d.PayingUsers = v, false),
            ("ForwardD1Retention", (d, v) => d.D1Retention = v, false),
            ("ForwardD7Retention", (d, v) => d.D7Retention = v, false),
            ("PeakConcurrentPlayers", (d, v) => d.PeakConcurrent = Math.Max(d.PeakConcurrent ?? 0, v), true),
            // Whether Roblox shows the game to anyone, and whether those who see it click: what separates "nobody sees
            // it" from "nobody wants it" when the visits are low.
            ("UniqueUsersWithImpressions", (d, v) => d.Impressions = v, true),
            ("UniqueUsersWithClicks", (d, v) => d.Clicks = v, true),
        };

        private readonly RobloxCloudClient cloud;
        private readonly OmniBloxStore store;
        private readonly Func<DateTime> clock;

        public AnalyticsSync(RobloxCloudClient cloud, OmniBloxStore store, Func<DateTime> clock)
        {
            this.cloud = cloud;
            this.store = store;
            this.clock = clock;
        }

        public async Task<string> SyncAsync(string gameId, CancellationToken ct, int days = 14)
        {
            var (slot, published) = store.Read(s => (s.Game(gameId)?.Slot, s.Game(gameId)?.Publish.FirstPublishedUtc));
            if (slot == null) return "not published yet";
            if (!cloud.HasKey) return "no API key";
            var now = clock();
            var end = now.Date.AddDays(1);
            var start = end.AddDays(-Math.Clamp(days, 1, 60));
            if (published is DateTime first && first.Date > start) start = first.Date;

            var rows = new SortedDictionary<DateTime, DailyMetrics>();
            var errors = new List<string>();
            foreach (var (metric, apply, optional) in Metrics)
            {
                try
                {
                    var series = await cloud.QueryMetricAsync(slot.UniverseId, new MetricQuery { Metric = metric, Granularity = "OneDay", StartUtc = start, EndUtc = end }, ct);
                    foreach (var point in series.SelectMany(s => s.Points))
                    {
                        var day = point.Time.Date;
                        if (!rows.TryGetValue(day, out var row)) rows[day] = row = new DailyMetrics { Date = day };
                        apply(row, point.Value);
                    }
                }
                catch (RobloxApiException ex) when (ex.IsAuth)
                {
                    errors.Add("the API key cannot read analytics (needs universe-analytics read)");
                    break;
                }
                catch (RobloxApiException ex) when (optional && ex.Status == System.Net.HttpStatusCode.BadRequest)
                {
                    // Not served for this experience: no data, not a failure.
                }
                catch (Exception ex) when (ex is RobloxApiException or TimeoutException or HttpRequestException)
                {
                    errors.Add($"{metric}: {ex.Message}");
                }
            }
            foreach (var row in rows.Values)
                if (row.Impressions is > 0 && row.Clicks is double clicks) row.ClickThrough = clicks / row.Impressions.Value;

            store.Mutate(s =>
            {
                var game = s.Game(gameId);
                if (game == null) return;
                foreach (var (day, row) in rows)
                {
                    var existing = game.Stats.Daily.FirstOrDefault(d => d.Date == day);
                    if (existing == null) game.Stats.Daily.Add(row);
                    else
                    {
                        existing.DailyActiveUsers = row.DailyActiveUsers ?? existing.DailyActiveUsers;
                        existing.Visits = row.Visits ?? existing.Visits;
                        existing.AvgSessionMinutes = row.AvgSessionMinutes ?? existing.AvgSessionMinutes;
                        existing.PlayHours = row.PlayHours ?? existing.PlayHours;
                        existing.RevenueRobux = row.RevenueRobux ?? existing.RevenueRobux;
                        existing.PayingUsers = row.PayingUsers ?? existing.PayingUsers;
                        existing.D1Retention = row.D1Retention ?? existing.D1Retention;
                        existing.D7Retention = row.D7Retention ?? existing.D7Retention;
                        existing.PeakConcurrent = row.PeakConcurrent ?? existing.PeakConcurrent;
                        existing.Impressions = row.Impressions ?? existing.Impressions;
                        existing.Clicks = row.Clicks ?? existing.Clicks;
                        existing.ClickThrough = row.ClickThrough ?? existing.ClickThrough;
                    }
                }
                game.Stats.Daily = game.Stats.Daily.OrderBy(d => d.Date).TakeLast(400).ToList();
                game.Stats.LastSyncUtc = now;
                game.Stats.LastSyncError = errors.Count > 0 ? string.Join("; ", errors) : null;
                s.Touch(game, now);
            });
            return errors.Count == 0 ? $"synced {rows.Count} day(s)" : $"synced {rows.Count} day(s) with problems: {string.Join("; ", errors)}";
        }

        /// <summary>Plain-text summary of the last N days for an agent (led by any reason players cannot join).</summary>
        public static string Describe(GameDossier game, int days = 14)
        {
            string reach = PlayerReach.Describe(game) is { } closed ? closed + "\n" : "";
            var rows = game.Stats.Daily.OrderBy(d => d.Date).TakeLast(days).ToList();
            if (rows.Count == 0) return reach + "No Roblox analytics yet" + (game.Stats.LastSyncError != null ? $" ({game.Stats.LastSyncError})" : "") + ".";
            string F(double? v, string fmt = "0.##") => v is double d ? d.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture) : "–";
            // Retention may arrive as a fraction or as a percentage; show it as a percentage either way.
            double? Pct(double? v) => v is double d ? (d <= 1 ? d * 100 : d) : null;
            string Reach(DailyMetrics d) => d.Impressions is null ? "" : $", impressions {F(d.Impressions, "0")}, clicks {F(d.Clicks, "0")}" + (d.ClickThrough is double ctr ? $" ({ctr * 100:0.#}% click-through)" : "");
            var lines = rows.Select(d => $"{d.Date:MM-dd}: DAU {F(d.DailyActiveUsers, "0")}, visits {F(d.Visits, "0")}, session {F(d.AvgSessionMinutes, "0.#")} min, D1 {F(Pct(d.D1Retention), "0.#")}%, D7 {F(Pct(d.D7Retention), "0.#")}%, revenue {F(d.RevenueRobux, "0")} R$, payers {F(d.PayingUsers, "0")}, peak CCU {F(d.PeakConcurrent, "0")}{Reach(d)}");
            double revenue = rows.Sum(d => d.RevenueRobux ?? 0);
            double dau = rows.Average(d => d.DailyActiveUsers ?? 0);
            return reach + $"Last {rows.Count} day(s): average DAU {dau:0.#}, revenue {revenue:0} Robux.\n" + string.Join("\n", lines);
        }
    }
}
