using System.Globalization;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.OmniBlox.Insights
{
    public sealed class TelemetrySummary
    {
        public DateTime SinceUtc { get; set; }
        public long Events { get; set; }
        public long Sessions { get; set; }
        public double AvgSessionSeconds { get; set; }
        public double MedianSessionSeconds { get; set; }
        public double DeathsPerSession { get; set; }
        public long Purchases { get; set; }
        public long PurchaseRobux { get; set; }
        public Dictionary<string, long> PurchasesByKey { get; set; } = new();
        public Dictionary<int, long> FeedbackByRating { get; set; } = new();
        public Dictionary<string, long> FeedbackByReason { get; set; } = new();
        public List<(string Message, long Count)> TopErrors { get; set; } = new();
        /// <summary>Funnel step → sessions that reached it (obby stages, onboarding…).</summary>
        public SortedDictionary<int, long> Funnel { get; set; } = new();
        public Dictionary<string, long> EventCounts { get; set; } = new();
        public long PeakPlayersPerServer { get; set; }
        public Dictionary<string, long> SessionsByDay { get; set; } = new();
        public DateTime? LastEventUtc { get; set; }
    }

    /// <summary>
    /// Receives the anonymous gameplay feed each game's Analytics module posts (sessions, funnels, economy,
    /// purchases, deaths, errors, feedback) and answers "how is this game doing" for maintenance and
    /// post-mortems. Per-game rate limits keep a misbehaving server from flooding it; data older than
    /// 90 days is pruned.
    /// </summary>
    public sealed class TelemetryStore : IDisposable
    {
        public const int MaxEventsPerBatch = 600;
        public const int MaxEventsPerMinute = 3000;
        private static readonly TimeSpan Retention = TimeSpan.FromDays(90);

        private readonly string path;
        private readonly object sync = new();
        private readonly Dictionary<string, Queue<(DateTime At, int Count)>> rate = new(StringComparer.Ordinal);
        private SqliteConnection? db;

        public TelemetryStore(string path) => this.path = Path.GetFullPath(path);

        public void Open()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var conn = new SqliteConnection($"Data Source={path};Cache=Shared");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;
CREATE TABLE IF NOT EXISTS events (
  id INTEGER PRIMARY KEY,
  game_id TEXT NOT NULL,
  utc INTEGER NOT NULL,
  name TEXT NOT NULL,
  session TEXT,
  server TEXT,
  fields TEXT
);
CREATE INDEX IF NOT EXISTS ix_events_game_time ON events(game_id, utc);
CREATE INDEX IF NOT EXISTS ix_events_game_name ON events(game_id, name, utc);";
            cmd.ExecuteNonQuery();
            lock (sync) db = conn;
        }

        public void Dispose()
        {
            lock (sync)
            {
                db?.Dispose();
                db = null;
            }
        }

        private SqliteConnection Db => db ?? throw new InvalidOperationException("Telemetry store is not open.");

        /// <summary>Stores one batch. Returns how many events were kept (0 when rate-limited or malformed).</summary>
        public int Ingest(string gameId, JObject payload, DateTime receivedUtc)
        {
            if (payload["events"] is not JArray events) return 0;
            string server = Clip((string?)payload["server"] ?? "", 64);
            int accepted = 0;
            lock (sync)
            {
                if (!rate.TryGetValue(gameId, out var window)) rate[gameId] = window = new Queue<(DateTime, int)>();
                while (window.Count > 0 && receivedUtc - window.Peek().At > TimeSpan.FromMinutes(1)) window.Dequeue();
                int recent = window.Sum(w => w.Count);
                int room = Math.Min(MaxEventsPerBatch, MaxEventsPerMinute - recent);
                if (room <= 0) return 0;

                using var tx = Db.BeginTransaction();
                foreach (var e in events.OfType<JObject>().Take(room))
                {
                    string name = (string?)e["n"] ?? "";
                    if (name.Length == 0 || name.Length > 40 || !name.All(c => char.IsLetterOrDigit(c) || c == '_')) continue;
                    long t = (long?)e["t"] ?? new DateTimeOffset(receivedUtc).ToUnixTimeSeconds();
                    long now = new DateTimeOffset(receivedUtc).ToUnixTimeSeconds();
                    if (t > now + 300 || t < now - 7 * 86400) t = now; // clocks lie; never trust far-off stamps
                    string? fields = e["f"] is JObject f ? Clip(f.ToString(Formatting.None), 2000) : null;
                    using var cmd = Db.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO events (game_id, utc, name, session, server, fields) VALUES ($g, $t, $n, $s, $srv, $f)";
                    cmd.Parameters.AddWithValue("$g", gameId);
                    cmd.Parameters.AddWithValue("$t", t);
                    cmd.Parameters.AddWithValue("$n", name);
                    cmd.Parameters.AddWithValue("$s", (object?)Clip((string?)e["s"] ?? "", 64) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$srv", server);
                    cmd.Parameters.AddWithValue("$f", (object?)fields ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                    accepted++;
                }
                tx.Commit();
                window.Enqueue((receivedUtc, accepted));
            }
            return accepted;
        }

        public TelemetrySummary Summarize(string gameId, DateTime sinceUtc)
        {
            var summary = new TelemetrySummary { SinceUtc = sinceUtc };
            long since = new DateTimeOffset(DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
            var sessionLengths = new List<double>();
            var errorCounts = new Dictionary<string, long>();
            var funnelSessions = new Dictionary<int, HashSet<string>>();
            long deaths = 0;
            lock (sync)
            {
                using var cmd = Db.CreateCommand();
                cmd.CommandText = "SELECT utc, name, session, fields FROM events WHERE game_id = $g AND utc >= $since ORDER BY utc";
                cmd.Parameters.AddWithValue("$g", gameId);
                cmd.Parameters.AddWithValue("$since", since);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    long utc = r.GetInt64(0);
                    string name = r.GetString(1);
                    string? session = r.IsDBNull(2) ? null : r.GetString(2);
                    JObject? f = null;
                    if (!r.IsDBNull(3)) { try { f = JObject.Parse(r.GetString(3)); } catch (JsonException) { } }
                    summary.Events++;
                    summary.EventCounts[name] = summary.EventCounts.GetValueOrDefault(name) + 1;
                    var when = DateTimeOffset.FromUnixTimeSeconds(utc).UtcDateTime;
                    summary.LastEventUtc = when;
                    switch (name)
                    {
                        case "session_start":
                            summary.Sessions++;
                            string day = when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                            summary.SessionsByDay[day] = summary.SessionsByDay.GetValueOrDefault(day) + 1;
                            break;
                        case "session_end":
                            if ((double?)f?["seconds"] is double seconds && seconds >= 0 && seconds < 86400) sessionLengths.Add(seconds);
                            break;
                        case "death":
                            deaths++;
                            break;
                        case "purchase":
                            summary.Purchases++;
                            summary.PurchaseRobux += (long?)f?["robux"] ?? 0;
                            string key = (string?)f?["key"] ?? "?";
                            summary.PurchasesByKey[key] = summary.PurchasesByKey.GetValueOrDefault(key) + 1;
                            break;
                        case "feedback":
                            int rating = (int?)f?["rating"] ?? 0;
                            if (rating is >= 1 and <= 3) summary.FeedbackByRating[rating] = summary.FeedbackByRating.GetValueOrDefault(rating) + 1;
                            string reason = (string?)f?["reason"] ?? "";
                            if (reason.Length > 0) summary.FeedbackByReason[reason] = summary.FeedbackByReason.GetValueOrDefault(reason) + 1;
                            break;
                        case "error":
                            string message = (string?)f?["message"] ?? "?";
                            errorCounts[message] = errorCounts.GetValueOrDefault(message) + Math.Max(1, (long?)f?["count"] is long c && c > 1 ? 25 : 1);
                            break;
                        case "funnel":
                            int step = (int?)f?["step"] ?? 0;
                            if (step > 0 && session != null)
                            {
                                if (!funnelSessions.TryGetValue(step, out var set)) funnelSessions[step] = set = new HashSet<string>();
                                set.Add(session);
                            }
                            break;
                        case "heartbeat":
                            summary.PeakPlayersPerServer = Math.Max(summary.PeakPlayersPerServer, (long?)f?["players"] ?? 0);
                            break;
                    }
                }
            }
            if (sessionLengths.Count > 0)
            {
                summary.AvgSessionSeconds = Math.Round(sessionLengths.Average(), 1);
                var sorted = sessionLengths.OrderBy(x => x).ToList();
                summary.MedianSessionSeconds = sorted[sorted.Count / 2];
            }
            summary.DeathsPerSession = summary.Sessions > 0 ? Math.Round(deaths / (double)summary.Sessions, 2) : 0;
            summary.TopErrors = errorCounts.OrderByDescending(kv => kv.Value).Take(10).Select(kv => (kv.Key, kv.Value)).ToList();
            foreach (var (step, set) in funnelSessions) summary.Funnel[step] = set.Count;
            return summary;
        }

        /// <summary>Plain-text digest for an agent.</summary>
        public static string Describe(TelemetrySummary s)
        {
            var lines = new List<string>
            {
                $"Since {s.SinceUtc:yyyy-MM-dd HH:mm} UTC: {s.Events} events, {s.Sessions} sessions, average session {s.AvgSessionSeconds / 60:0.#} min (median {s.MedianSessionSeconds / 60:0.#}), {s.DeathsPerSession:0.##} deaths/session, peak {s.PeakPlayersPerServer} players in a server.",
                $"Purchases: {s.Purchases} ({s.PurchaseRobux} Robux)" + (s.PurchasesByKey.Count > 0 ? " — " + string.Join(", ", s.PurchasesByKey.Select(kv => $"{kv.Key}×{kv.Value}")) : ""),
            };
            if (s.FeedbackByRating.Count > 0)
                lines.Add("Feedback: " + string.Join(", ", s.FeedbackByRating.OrderBy(kv => kv.Key).Select(kv => $"{(kv.Key == 1 ? "bad" : kv.Key == 2 ? "okay" : "great")} {kv.Value}"))
                          + (s.FeedbackByReason.Count > 0 ? "; reasons: " + string.Join(", ", s.FeedbackByReason.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")) : ""));
            if (s.Funnel.Count > 0)
            {
                long first = s.Funnel.First().Value;
                lines.Add("Funnel (step: sessions reaching it): " + string.Join(", ", s.Funnel.Select(kv => $"{kv.Key}: {kv.Value}" + (first > 0 ? $" ({kv.Value * 100.0 / first:0}%)" : ""))));
            }
            if (s.TopErrors.Count > 0) lines.Add("Top errors: " + string.Join(" | ", s.TopErrors.Select(e => $"{e.Count}× {e.Message}")));
            return string.Join("\n", lines);
        }

        public int Prune(DateTime nowUtc)
        {
            long cutoff = new DateTimeOffset(nowUtc - Retention).ToUnixTimeSeconds();
            lock (sync)
            {
                using var cmd = Db.CreateCommand();
                cmd.CommandText = "DELETE FROM events WHERE utc < $c";
                cmd.Parameters.AddWithValue("$c", cutoff);
                return cmd.ExecuteNonQuery();
            }
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
