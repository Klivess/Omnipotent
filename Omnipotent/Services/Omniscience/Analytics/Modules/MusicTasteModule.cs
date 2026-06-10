using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Music taste from Discord presence — zero Spotify API needed. Spotify activity
    /// rides along in PRESENCE_UPDATE (activity name 'Spotify': details=track,
    /// state=artist). Aggregates top artists/tracks, play counts and listening hours.
    /// </summary>
    public class MusicTasteModule : IPersonAnalyticModule
    {
        public string Name => "music_taste";
        public int Version => 1;

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var userIds = AnalyticHelpers.GetPersonPlatformUserIds(conn, personId);
            if (userIds.Count == 0) return Task.FromResult(Empty());

            var now = DateTime.UtcNow;
            var artists = new Dictionary<string, (int plays, double weighted)>(StringComparer.OrdinalIgnoreCase);
            var tracks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int[] listeningHours = new int[24];
            int playEvents = 0;
            string? lastTrack = null;
            long? lastTrackAt = null;
            double listeningMs = 0;

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

                string? track = null, artist = null;
                foreach (var a in acts.OfType<JObject>())
                {
                    if (!string.Equals(a.Value<string>("name"), "Spotify", StringComparison.OrdinalIgnoreCase)) continue;
                    track = a.Value<string>("details");
                    artist = a.Value<string>("state");
                    break;
                }

                if (track == null)
                {
                    // Track ended — close the listening stretch.
                    if (lastTrackAt.HasValue) listeningMs += Math.Min(capturedAt - lastTrackAt.Value, 10 * 60_000);
                    lastTrack = null; lastTrackAt = null;
                    continue;
                }

                var dt = DateTimeOffset.FromUnixTimeMilliseconds(capturedAt).UtcDateTime;
                listeningHours[dt.Hour]++;
                if (track != lastTrack)
                {
                    playEvents++;
                    tracks.TryGetValue(track, out int tc); tracks[track] = tc + 1;
                    if (!string.IsNullOrWhiteSpace(artist))
                    {
                        // Spotify joins multiple artists with ';'.
                        foreach (var art in artist.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            artists.TryGetValue(art, out var cur);
                            artists[art] = (cur.plays + 1, cur.weighted + TemporalWeighting.Weight(dt, now));
                        }
                    }
                    if (lastTrackAt.HasValue) listeningMs += Math.Min(capturedAt - lastTrackAt.Value, 10 * 60_000);
                    lastTrack = track;
                    lastTrackAt = capturedAt;
                }
            }

            if (playEvents == 0) return Task.FromResult(Empty());
            return Task.FromResult(new JObject(
                new JProperty("play_events", playEvents),
                new JProperty("estimated_listening_hours", Math.Round(listeningMs / 3_600_000.0, 1)),
                new JProperty("top_artists", new JArray(artists.OrderByDescending(a => a.Value.plays).Take(15)
                    .Select(a => new JObject(new JProperty("artist", a.Key), new JProperty("plays", a.Value.plays))))),
                new JProperty("current_top_artists", new JArray(artists.OrderByDescending(a => a.Value.weighted).Take(8)
                    .Select(a => new JObject(new JProperty("artist", a.Key), new JProperty("weighted_score", Math.Round(a.Value.weighted, 2)))))),
                new JProperty("top_tracks", new JArray(tracks.OrderByDescending(t => t.Value).Take(15)
                    .Select(t => new JObject(new JProperty("track", t.Key), new JProperty("plays", t.Value))))),
                new JProperty("listening_hour_histogram", new JArray(listeningHours.Select(h => (JToken)h)))
            ));
        }

        private static JObject Empty() => new(
            new JProperty("play_events", 0),
            new JProperty("estimated_listening_hours", 0.0),
            new JProperty("top_artists", new JArray()),
            new JProperty("current_top_artists", new JArray()),
            new JProperty("top_tracks", new JArray()),
            new JProperty("listening_hour_histogram", new JArray(Enumerable.Repeat((JToken)0, 24))));
    }
}
