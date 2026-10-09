using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Omnipotent.Profiles.Activity
{
    /// <summary>One open website tab, connected over the SessionWatch socket.</summary>
    public sealed class PresenceConnection
    {
        public string ConnectionId { get; init; } = "";
        public string ProfileId { get; init; } = "";
        public string? SessionId { get; init; }
        public string? Ip { get; init; }
        public string? DeviceLabel { get; init; }
        public DateTime ConnectedUtc { get; init; }
        public DateTime LastHeartbeatUtc { get; internal set; }
        public string? Path { get; internal set; }
        public string? Title { get; internal set; }
        public bool Visible { get; internal set; } = true;
        public DateTime PathSinceUtc { get; internal set; }

        /// <summary>Messages queued for the tab; drained by the socket's send loop.</summary>
        internal Channel<string> Outbox { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    }

    /// <summary>A profile's live state, as shown in the directory and on the profile page.</summary>
    public sealed class ProfilePresence
    {
        /// <summary>online (a visible tab), idle (only background tabs) or offline.</summary>
        public string State { get; set; } = "offline";
        public int Connections { get; set; }
        public string? CurrentPath { get; set; }
        public string? CurrentTitle { get; set; }
        public DateTime? OnPageSinceUtc { get; set; }
        public DateTime? LastHeartbeatUtc { get; set; }
        public List<PresenceTab> Tabs { get; set; } = new();
    }

    public sealed class PresenceTab
    {
        public string ConnectionId { get; set; } = "";
        public string? SessionId { get; set; }
        public string? Path { get; set; }
        public string? Title { get; set; }
        public bool Visible { get; set; }
        public string? Ip { get; set; }
        public string? Device { get; set; }
        public DateTime ConnectedUtc { get; set; }
        public DateTime LastHeartbeatUtc { get; set; }
    }

    public sealed class PresenceChange
    {
        public string ProfileId { get; init; } = "";
        public string Kind { get; init; } = "";  // connected | disconnected | navigated | visibility
        public PresenceConnection Connection { get; init; } = null!;
    }

    /// <summary>
    /// Who is on the website right now, and on which page. Purely in memory: the source of truth is
    /// the set of open SessionWatch sockets, so a server restart simply starts empty.
    /// </summary>
    public sealed class PresenceTracker
    {
        /// <summary>A tab that hasn't sent a heartbeat for this long no longer counts as active.</summary>
        public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(90);

        private readonly ConcurrentDictionary<string, PresenceConnection> connections = new(StringComparer.Ordinal);

        public event Action<PresenceChange>? Changed;

        /// <summary>Raised when a tab leaves a page (navigation or disconnect) with how long it stayed.</summary>
        public event Action<PresenceConnection, string, DateTime, long>? PageLeft;

        public PresenceConnection Register(string profileId, string? sessionId, string? ip, string? deviceLabel, DateTime nowUtc)
        {
            var conn = new PresenceConnection
            {
                ConnectionId = Guid.NewGuid().ToString("N"),
                ProfileId = profileId,
                SessionId = sessionId,
                Ip = ip,
                DeviceLabel = deviceLabel,
                ConnectedUtc = nowUtc,
                LastHeartbeatUtc = nowUtc,
                PathSinceUtc = nowUtc,
            };
            connections[conn.ConnectionId] = conn;
            Raise(conn, "connected");
            return conn;
        }

        public void Unregister(PresenceConnection conn, DateTime nowUtc)
        {
            if (!connections.TryRemove(conn.ConnectionId, out _)) return;
            conn.Outbox.Writer.TryComplete();
            ClosePage(conn, nowUtc);
            Raise(conn, "disconnected");
        }

        /// <summary>Applies a presence message from a tab. Returns true if the page changed.</summary>
        public bool Update(PresenceConnection conn, string? path, string? title, bool? visible, DateTime nowUtc)
        {
            conn.LastHeartbeatUtc = nowUtc;
            bool navigated = false;
            if (!string.IsNullOrWhiteSpace(path) && !string.Equals(path, conn.Path, StringComparison.Ordinal))
            {
                ClosePage(conn, nowUtc);
                conn.Path = Trim(path, 512);
                conn.Title = Trim(title, 256);
                conn.PathSinceUtc = nowUtc;
                navigated = true;
            }
            else if (!string.IsNullOrWhiteSpace(title))
            {
                conn.Title = Trim(title, 256);
            }
            bool visibilityChanged = visible.HasValue && visible.Value != conn.Visible;
            if (visible.HasValue) conn.Visible = visible.Value;
            if (navigated) Raise(conn, "navigated");
            else if (visibilityChanged) Raise(conn, "visibility");
            return navigated;
        }

        private void ClosePage(PresenceConnection conn, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(conn.Path)) return;
            long dwell = (long)Math.Max(0, (nowUtc - conn.PathSinceUtc).TotalMilliseconds);
            try { PageLeft?.Invoke(conn, conn.Path!, conn.PathSinceUtc, dwell); } catch { }
        }

        public IReadOnlyList<PresenceConnection> ForProfile(string profileId)
            => connections.Values.Where(c => c.ProfileId == profileId).ToList();

        public IReadOnlyList<PresenceConnection> All => connections.Values.ToList();

        public ProfilePresence Summarize(string profileId, DateTime nowUtc)
        {
            var tabs = connections.Values.Where(c => c.ProfileId == profileId).ToList();
            var presence = new ProfilePresence { Connections = tabs.Count };
            if (tabs.Count == 0) return presence;

            var live = tabs.Where(t => nowUtc - t.LastHeartbeatUtc <= HeartbeatTimeout).ToList();
            var focus = live.Where(t => t.Visible).OrderByDescending(t => t.LastHeartbeatUtc).FirstOrDefault()
                ?? live.OrderByDescending(t => t.LastHeartbeatUtc).FirstOrDefault()
                ?? tabs.OrderByDescending(t => t.LastHeartbeatUtc).First();
            presence.State = live.Any(t => t.Visible) ? "online" : live.Count > 0 ? "idle" : "offline";
            presence.CurrentPath = focus.Path;
            presence.CurrentTitle = focus.Title;
            presence.OnPageSinceUtc = focus.PathSinceUtc;
            presence.LastHeartbeatUtc = tabs.Max(t => t.LastHeartbeatUtc);
            presence.Tabs = tabs.OrderByDescending(t => t.LastHeartbeatUtc).Select(t => new PresenceTab
            {
                ConnectionId = t.ConnectionId,
                SessionId = t.SessionId,
                Path = t.Path,
                Title = t.Title,
                Visible = t.Visible,
                Ip = t.Ip,
                Device = t.DeviceLabel,
                ConnectedUtc = t.ConnectedUtc,
                LastHeartbeatUtc = t.LastHeartbeatUtc,
            }).ToList();
            return presence;
        }

        public int SendToProfile(string profileId, string json)
        {
            int sent = 0;
            foreach (var c in connections.Values)
            {
                if (c.ProfileId == profileId && c.Outbox.Writer.TryWrite(json)) sent++;
            }
            return sent;
        }

        public int SendToSession(string sessionId, string json)
        {
            int sent = 0;
            foreach (var c in connections.Values)
            {
                if (c.SessionId == sessionId && c.Outbox.Writer.TryWrite(json)) sent++;
            }
            return sent;
        }

        private void Raise(PresenceConnection conn, string kind)
        {
            try { Changed?.Invoke(new PresenceChange { ProfileId = conn.ProfileId, Kind = kind, Connection = conn }); } catch { }
        }

        private static string? Trim(string? s, int max) => s == null || s.Length <= max ? s : s[..max];
    }
}
