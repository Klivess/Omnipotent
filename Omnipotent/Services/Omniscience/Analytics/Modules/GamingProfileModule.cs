using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Gaming profile from presence: which games, estimated hours, play schedule.
    /// Activity type 0 ('Playing') in PRESENCE_UPDATE payloads.
    /// </summary>
    public class GamingProfileModule : IPersonAnalyticModule
    {
        public string Name => "gaming_profile";
        public int Version => 1;

        private const long MaxSessionGapMs = 30 * 60_000; // presence change-points; cap inferred stretches

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var userIds = AnalyticHelpers.GetPersonPlatformUserIds(conn, personId);
            if (userIds.Count == 0) return Task.FromResult(Empty());

            var games = new Dictionary<string, (int sightings, double ms, double weighted)>(StringComparer.OrdinalIgnoreCase);
            int[] playingHours = new int[24];
            string? lastGame = null;
            long? lastAt = null;
            var now = DateTime.UtcNow;

            using var cmd = conn.CreateCommand();
            string inC = AnalyticHelpers.BindInClause(cmd, "u", userIds);
            cmd.CommandText = $@"SELECT activities_json, captured_at FROM presence_events
                WHERE platform_user_id IN ({inC}) AND activities_json IS NOT NULL
                ORDER BY captured_at ASC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                long capturedAt = r.GetInt64(1);
                JArray acts;
                try { acts = JArray.Parse(r.GetString(0)); } catch { continue; }

                string? game = acts.OfType<JObject>()
                    .Where(a => a.Value<int?>("type") == 0 && !string.IsNullOrWhiteSpace(a.Value<string>("name")))
                    .Select(a => a.Value<string>("name"))
                    .FirstOrDefault();

                // Close the previous stretch (game change or stopped playing).
                if (lastGame != null && lastAt.HasValue)
                {
                    long stretch = Math.Min(capturedAt - lastAt.Value, MaxSessionGapMs);
                    var cur = games[lastGame];
                    games[lastGame] = (cur.sightings, cur.ms + stretch, cur.weighted);
                }

                if (game != null)
                {
                    var dt = DateTimeOffset.FromUnixTimeMilliseconds(capturedAt).UtcDateTime;
                    playingHours[dt.Hour]++;
                    if (!games.TryGetValue(game, out var cur)) cur = (0, 0, 0);
                    games[game] = (cur.sightings + 1, cur.ms, cur.weighted + TemporalWeighting.Weight(dt, now));
                    lastGame = game;
                    lastAt = capturedAt;
                }
                else
                {
                    lastGame = null;
                    lastAt = null;
                }
            }

            if (games.Count == 0) return Task.FromResult(Empty());
            return Task.FromResult(new JObject(
                new JProperty("games_seen", games.Count),
                new JProperty("total_estimated_hours", Math.Round(games.Values.Sum(g => g.ms) / 3_600_000.0, 1)),
                new JProperty("top_games", new JArray(games.OrderByDescending(g => g.Value.ms).Take(15)
                    .Select(g => new JObject(
                        new JProperty("game", g.Key),
                        new JProperty("sightings", g.Value.sightings),
                        new JProperty("estimated_hours", Math.Round(g.Value.ms / 3_600_000.0, 1)))))),
                new JProperty("current_top_games", new JArray(games.OrderByDescending(g => g.Value.weighted).Take(8)
                    .Select(g => new JObject(new JProperty("game", g.Key), new JProperty("weighted_score", Math.Round(g.Value.weighted, 2)))))),
                new JProperty("playing_hour_histogram", new JArray(playingHours.Select(h => (JToken)h)))
            ));
        }

        private static JObject Empty() => new(
            new JProperty("games_seen", 0),
            new JProperty("total_estimated_hours", 0.0),
            new JProperty("top_games", new JArray()),
            new JProperty("current_top_games", new JArray()),
            new JProperty("playing_hour_histogram", new JArray(Enumerable.Repeat((JToken)0, 24))));
    }
}
