using Newtonsoft.Json;

namespace Omnipotent.Services.KliveGames.Runtime
{
    /// <summary>What happened to a server. String constants rather than an enum so a record written by a
    /// newer build still loads (and still shows) on an older page.</summary>
    public static class ActivityKinds
    {
        public const string Created = "created";
        public const string Provisioned = "provisioned";
        public const string ProvisionFailed = "provision-failed";
        public const string Starting = "starting";
        public const string Running = "running";
        public const string Stalled = "stalled";
        public const string Stopped = "stopped";
        public const string Crashed = "crashed";
        public const string AutoRestart = "auto-restart";
        public const string Killed = "killed";
        public const string StartFailed = "start-failed";
        public const string Adopted = "adopted";
        public const string PlayerJoin = "player-join";
        public const string PlayerLeave = "player-leave";
        public const string PlayerAction = "player-action";
        public const string BackupCreated = "backup-created";
        public const string BackupFailed = "backup-failed";
        public const string BackupRestored = "backup-restored";
        public const string BackupDeleted = "backup-deleted";
        public const string BackupPruned = "backup-pruned";
        public const string ConfigChanged = "config-changed";
        public const string PublicChanged = "public-changed";
        public const string ForwardRestored = "forward-restored";
        public const string Renamed = "renamed";
    }

    public sealed class ServerActivityEntry
    {
        public DateTime AtUtc { get; set; }
        public string ServerId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
        /// <summary>The profile that asked for it, when a person did (null for the server's own doing).</summary>
        public string? Actor { get; set; }
        public string? Player { get; set; }
        public int? ExitCode { get; set; }
        /// <summary>A duration that belongs to the event: start-up time, backup time, session length.</summary>
        public double? Seconds { get; set; }
        public long? Bytes { get; set; }
    }

    /// <summary>
    /// Per-server history: starts, stops, crashes, backups, joins and leaves, kept so a page can answer
    /// "when did this last crash", "who played" and "is it backed up" — none of which the instance record
    /// alone can. One append-only JSONL file per server (<c>Instances/{id}/activity.jsonl</c>), trimmed to the
    /// newest <see cref="KeepPerServer"/> lines once it grows past <see cref="TrimAt"/>; a memory copy of the
    /// same lines serves every read, so the routes never touch the disk.
    /// </summary>
    public sealed class ServerActivityLog
    {
        public const int KeepPerServer = 1000;
        public const int TrimAt = 1500;

        private static readonly JsonSerializerSettings Json = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        };

        private sealed class Book
        {
            public readonly object Gate = new();
            public readonly List<ServerActivityEntry> Entries = new();
            public string Path = "";
            public int LinesOnDisk;
        }

        private readonly Dictionary<string, Book> _books = new();
        private readonly object _booksGate = new();
        private readonly Func<string, string> _pathFor;
        private readonly Action<Exception, string>? _logError;
        private long _version;

        /// <param name="pathFor">Maps a server id to its activity file.</param>
        public ServerActivityLog(Func<string, string> pathFor, Action<Exception, string>? logError = null)
        {
            _pathFor = pathFor;
            _logError = logError;
        }

        /// <summary>Bumps on every record — a cheap "did anything change" for callers that build views.</summary>
        public long Version => Interlocked.Read(ref _version);

        /// <summary>Reads a server's file into memory (once, at load). A torn last line is skipped.</summary>
        public void Load(string serverId)
        {
            var book = BookFor(serverId);
            lock (book.Gate)
            {
                book.Entries.Clear();
                book.LinesOnDisk = 0;
                if (!File.Exists(book.Path)) return;
                try
                {
                    foreach (var line in File.ReadLines(book.Path))
                    {
                        book.LinesOnDisk++;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var entry = JsonConvert.DeserializeObject<ServerActivityEntry>(line, Json);
                            if (entry != null && !string.IsNullOrEmpty(entry.Kind))
                            {
                                entry.ServerId = serverId;
                                book.Entries.Add(entry);
                            }
                        }
                        catch { /* a half-written line from a crash: skip it */ }
                    }
                    if (book.Entries.Count > KeepPerServer)
                        book.Entries.RemoveRange(0, book.Entries.Count - KeepPerServer);
                }
                catch (Exception ex) { _logError?.Invoke(ex, $"[KliveGames] Could not read the activity of {serverId}."); }
            }
        }

        public ServerActivityEntry Record(string serverId, string kind, string text, Action<ServerActivityEntry>? fill = null, DateTime? atUtc = null)
        {
            var entry = new ServerActivityEntry
            {
                AtUtc = atUtc ?? DateTime.UtcNow,
                ServerId = serverId,
                Kind = kind,
                Text = text,
            };
            fill?.Invoke(entry);

            var book = BookFor(serverId);
            lock (book.Gate)
            {
                book.Entries.Add(entry);
                if (book.Entries.Count > KeepPerServer) book.Entries.RemoveRange(0, book.Entries.Count - KeepPerServer);
                try
                {
                    string? dir = System.IO.Path.GetDirectoryName(book.Path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        File.AppendAllText(book.Path, JsonConvert.SerializeObject(entry, Json) + "\n");
                        book.LinesOnDisk++;
                        if (book.LinesOnDisk > TrimAt) RewriteLocked(book);
                    }
                }
                catch (Exception ex) { _logError?.Invoke(ex, $"[KliveGames] Could not save activity for {serverId}."); }
            }
            Interlocked.Increment(ref _version);
            return entry;
        }

        /// <summary>Newest first. <paramref name="beforeUtc"/> pages backwards.</summary>
        public IReadOnlyList<ServerActivityEntry> Recent(string serverId, int limit, DateTime? beforeUtc = null, ISet<string>? kinds = null)
        {
            Book? book;
            lock (_booksGate) _books.TryGetValue(serverId, out book);
            if (book == null) return Array.Empty<ServerActivityEntry>();
            lock (book.Gate)
            {
                var result = new List<ServerActivityEntry>(Math.Min(limit, book.Entries.Count));
                for (int i = book.Entries.Count - 1; i >= 0 && result.Count < limit; i--)
                {
                    var e = book.Entries[i];
                    if (beforeUtc is DateTime before && e.AtUtc >= before) continue;
                    if (kinds != null && !kinds.Contains(e.Kind)) continue;
                    result.Add(e);
                }
                return result;
            }
        }

        /// <summary>Every server's history merged, newest first.</summary>
        public IReadOnlyList<ServerActivityEntry> RecentAll(int limit, DateTime? beforeUtc = null, ISet<string>? kinds = null)
        {
            List<string> ids;
            lock (_booksGate) ids = _books.Keys.ToList();
            return ids.SelectMany(id => Recent(id, limit, beforeUtc, kinds))
                .OrderByDescending(e => e.AtUtc)
                .Take(limit)
                .ToList();
        }

        /// <summary>The newest entry of any of the given kinds.</summary>
        public ServerActivityEntry? Last(string serverId, params string[] kinds)
            => Recent(serverId, 1, null, new HashSet<string>(kinds)).FirstOrDefault();

        public int Count(string serverId, string kind, DateTime sinceUtc)
        {
            Book? book;
            lock (_booksGate) _books.TryGetValue(serverId, out book);
            if (book == null) return 0;
            lock (book.Gate) return book.Entries.Count(e => e.Kind == kind && e.AtUtc >= sinceUtc);
        }

        public void Forget(string serverId)
        {
            lock (_booksGate) _books.Remove(serverId);
            Interlocked.Increment(ref _version);
        }

        private Book BookFor(string serverId)
        {
            lock (_booksGate)
            {
                if (!_books.TryGetValue(serverId, out var book))
                {
                    book = new Book { Path = _pathFor(serverId) };
                    _books[serverId] = book;
                }
                return book;
            }
        }

        private void RewriteLocked(Book book)
        {
            string tmp = book.Path + ".tmp";
            using (var writer = new StreamWriter(tmp, false))
            {
                foreach (var e in book.Entries) writer.Write(JsonConvert.SerializeObject(e, Json) + "\n");
            }
            File.Move(tmp, book.Path, overwrite: true);
            book.LinesOnDisk = book.Entries.Count;
        }
    }
}
