using Newtonsoft.Json;

namespace Omnipotent.Services.KliveGames.Runtime
{
    public sealed class KnownPlayer
    {
        public string Name { get; set; } = "";
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public int Joins { get; set; }
        public double PlayedSeconds { get; set; }
        /// <summary>Set while the player is on the server; the open session's time is added when it closes.</summary>
        public DateTime? OnlineSinceUtc { get; set; }
    }

    /// <summary>
    /// Everyone who has played on one server, with first/last seen, joins and time played, so the players
    /// panel can act on someone who is offline and the page can say who last played and when. Fed by the
    /// roster the orchestrator already keeps (join/leave lines and the periodic list poll): <see cref="Apply"/>
    /// takes the new roster and works out who arrived and who left. Saved to
    /// <c>Instances/{id}/players.json</c> on change, at most every few seconds.
    /// </summary>
    public sealed class PlayerBook
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, KnownPlayer> _players = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _path;
        private DateTime _savedUtc = DateTime.MinValue;
        private bool _dirty;

        public PlayerBook(string path)
        {
            _path = path;
            try
            {
                if (File.Exists(path))
                {
                    var list = JsonConvert.DeserializeObject<List<KnownPlayer>>(File.ReadAllText(path)) ?? new();
                    foreach (var p in list.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
                    {
                        // A session left open by a crash of the app ends at the last moment it was seen.
                        if (p.OnlineSinceUtc is DateTime since)
                        {
                            p.PlayedSeconds += Math.Max(0, (p.LastSeenUtc - since).TotalSeconds);
                            p.OnlineSinceUtc = null;
                        }
                        _players[p.Name] = p;
                    }
                }
            }
            catch { /* an unreadable book starts empty rather than blocking the server */ }
        }

        /// <summary>Applies a new roster. Returns who joined and who left since the previous one (with how
        /// long each leaver's session lasted).</summary>
        public (IReadOnlyList<string> joined, IReadOnlyList<(string name, double seconds)> left) Apply(IEnumerable<string> online, DateTime nowUtc)
        {
            var now = new HashSet<string>(online.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
            var joined = new List<string>();
            var left = new List<(string, double)>();
            lock (_gate)
            {
                foreach (var name in now)
                {
                    if (!_players.TryGetValue(name, out var p))
                    {
                        p = new KnownPlayer { Name = name, FirstSeenUtc = nowUtc };
                        _players[name] = p;
                    }
                    if (p.OnlineSinceUtc == null)
                    {
                        p.OnlineSinceUtc = nowUtc;
                        p.Joins++;
                        joined.Add(p.Name);
                    }
                    p.LastSeenUtc = nowUtc;
                }
                foreach (var p in _players.Values.Where(p => p.OnlineSinceUtc != null && !now.Contains(p.Name)).ToList())
                {
                    double session = Math.Max(0, (nowUtc - p.OnlineSinceUtc!.Value).TotalSeconds);
                    p.PlayedSeconds += session;
                    p.OnlineSinceUtc = null;
                    p.LastSeenUtc = nowUtc;
                    left.Add((p.Name, session));
                }
                if (joined.Count > 0 || left.Count > 0 || now.Count > 0) _dirty = true;
            }
            if (joined.Count > 0 || left.Count > 0) Save(force: true);
            else Save(force: false);
            return (joined, left);
        }

        /// <summary>Closes every open session (the server stopped). Returns how long each had been on.</summary>
        public IReadOnlyList<(string name, double seconds)> CloseAll(DateTime nowUtc)
        {
            var closed = new List<(string, double)>();
            lock (_gate)
            {
                foreach (var p in _players.Values.Where(p => p.OnlineSinceUtc != null))
                {
                    double s = Math.Max(0, (nowUtc - p.OnlineSinceUtc!.Value).TotalSeconds);
                    p.PlayedSeconds += s;
                    p.OnlineSinceUtc = null;
                    p.LastSeenUtc = nowUtc;
                    closed.Add((p.Name, s));
                }
                if (closed.Count > 0) _dirty = true;
            }
            Save(force: true);
            return closed;
        }

        public IReadOnlyList<KnownPlayer> Snapshot()
        {
            lock (_gate)
            {
                return _players.Values
                    .OrderByDescending(p => p.OnlineSinceUtc != null)
                    .ThenByDescending(p => p.LastSeenUtc)
                    .Select(p => new KnownPlayer
                    {
                        Name = p.Name,
                        FirstSeenUtc = p.FirstSeenUtc,
                        LastSeenUtc = p.LastSeenUtc,
                        Joins = p.Joins,
                        PlayedSeconds = p.PlayedSeconds,
                        OnlineSinceUtc = p.OnlineSinceUtc,
                    })
                    .ToList();
            }
        }

        public int Count { get { lock (_gate) return _players.Count; } }

        public DateTime? LastSeenUtc
        {
            get { lock (_gate) return _players.Count == 0 ? null : _players.Values.Max(p => p.LastSeenUtc); }
        }

        private void Save(bool force)
        {
            List<KnownPlayer> copy;
            lock (_gate)
            {
                if (!_dirty) return;
                if (!force && DateTime.UtcNow - _savedUtc < TimeSpan.FromSeconds(30)) return;
                copy = _players.Values.ToList();
                _dirty = false;
                _savedUtc = DateTime.UtcNow;
            }
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(copy, Formatting.Indented));
                File.Move(tmp, _path, overwrite: true);
            }
            catch { lock (_gate) _dirty = true; }
        }
    }
}
