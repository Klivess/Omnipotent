using Newtonsoft.Json;

namespace Omnipotent.Services.OmniBlox.Core
{
    /// <summary>
    /// The live state. Only touched inside <see cref="OmniBloxStore.Read{T}"/> / <see cref="OmniBloxStore.Mutate{T}"/>,
    /// which hold the store lock; anything used after the lock is cloned out first.
    /// </summary>
    public sealed class OmniBloxState
    {
        internal OmniBloxState(OmniBloxStore store) => this.store = store;
        private readonly OmniBloxStore store;

        public StudioState Studio { get; internal set; } = new();
        public Dictionary<string, GameDossier> Games { get; } = new(StringComparer.Ordinal);
        public List<PlaybookLesson> Playbook { get; internal set; } = new();
        public List<OmniBloxEvent> Events { get; } = new();

        public GameDossier? Game(string? gameId) => gameId != null && Games.TryGetValue(gameId, out var g) ? g : null;

        public void MarkStudio() => store.MarkDirty("studio");
        public void MarkPlaybook() => store.MarkDirty("playbook");
        public void MarkGame(string gameId) => store.MarkDirty("game:" + gameId);

        public void Touch(GameDossier game, DateTime nowUtc)
        {
            game.UpdatedUtc = nowUtc;
            MarkGame(game.GameId);
        }

        public OmniBloxEvent AddEvent(EventLevel level, string kind, string message, string? gameId = null, DateTime? utc = null)
        {
            var ev = new OmniBloxEvent { Utc = utc ?? DateTime.UtcNow, Level = level, Kind = kind, Message = message, GameId = gameId };
            Events.Add(ev);
            if (Events.Count > OmniBloxStore.MaxRecentEvents) Events.RemoveRange(0, Events.Count - OmniBloxStore.MaxRecentEvents);
            store.QueueEventAppend(ev);
            return ev;
        }

        /// <summary>Files an owner task unless an open one with the same dedupe key exists. Returns the open task.</summary>
        public OwnerTask FileOwnerTask(OwnerTaskKind kind, string dedupeKey, string title, string detail, string? gameId, DateTime nowUtc, IEnumerable<string>? files = null)
        {
            var existing = Studio.OwnerTasks.FirstOrDefault(t => t.Status == OwnerTaskStatus.Open && t.DedupeKey == dedupeKey);
            if (existing != null)
            {
                if (files != null) existing.Files = files.ToList();
                existing.Detail = detail;
                MarkStudio();
                return existing;
            }
            var task = new OwnerTask
            {
                TaskId = "t_" + Guid.NewGuid().ToString("N")[..12],
                Kind = kind,
                DedupeKey = dedupeKey,
                Title = title,
                Detail = detail,
                GameId = gameId,
                CreatedUtc = nowUtc,
                Files = files?.ToList() ?? new List<string>(),
            };
            Studio.OwnerTasks.Add(task);
            MarkStudio();
            AddEvent(EventLevel.Warning, "owner.task", title, gameId, nowUtc);
            return task;
        }

        public void ResolveOwnerTasks(string dedupeKey, DateTime nowUtc)
        {
            foreach (var t in Studio.OwnerTasks.Where(t => t.Status == OwnerTaskStatus.Open && t.DedupeKey == dedupeKey))
            {
                t.Status = OwnerTaskStatus.Done;
                t.ResolvedUtc = nowUtc;
                MarkStudio();
            }
        }
    }

    /// <summary>
    /// In-memory state plus crash-safe JSON persistence under SavedData/OmniBlox. Every write is
    /// tmp file → flush to disk → atomic rename, so a crash leaves the previous version, never a torn
    /// file. A file that will not parse is quarantined (renamed aside) rather than silently replaced.
    /// Writes are coalesced by a background flusher and forced on shutdown.
    /// </summary>
    public sealed class OmniBloxStore
    {
        public const int MaxRecentEvents = 2000;
        private const long MaxEventLogBytes = 8 * 1024 * 1024;

        internal static readonly JsonSerializerSettings JsonSettings = new()
        {
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            NullValueHandling = NullValueHandling.Include,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            Formatting = Formatting.Indented,
        };

        private readonly object gate = new();
        private readonly HashSet<string> dirty = new(StringComparer.Ordinal);
        private readonly List<OmniBloxEvent> pendingEvents = new();
        private readonly SemaphoreSlim flushSignal = new(0, int.MaxValue);
        private readonly SemaphoreSlim writeLock = new(1, 1);
        private readonly Action<string> log;
        private CancellationTokenSource? flusherCts;
        private Task? flusherTask;

        public OmniBloxStore(string rootDirectory, Action<string>? log = null)
        {
            Root = Path.GetFullPath(rootDirectory);
            this.log = log ?? (_ => { });
            State = new OmniBloxState(this);
        }

        public string Root { get; }
        private OmniBloxState State { get; }

        public string GamesDirectory => Path.Combine(Root, "games");
        public string AssetsDirectory => Path.Combine(Root, "assets");
        public string GameDirectory(string gameId) => Path.Combine(GamesDirectory, SafeId(gameId));
        public string WorkspaceDirectory(string gameId) => Path.Combine(GameDirectory(gameId), "workspace");
        public string BuildDirectory(string gameId) => Path.Combine(GameDirectory(gameId), "build");
        public string MediaDirectory(string gameId) => Path.Combine(GameDirectory(gameId), "media");
        private string StudioPath => Path.Combine(Root, "studio.json");
        private string PlaybookPath => Path.Combine(Root, "playbook.json");
        private string EventsPath => Path.Combine(Root, "events.jsonl");
        private string DossierPath(string gameId) => Path.Combine(GameDirectory(gameId), "dossier.json");

        public static string SafeId(string id)
        {
            var chars = id.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray();
            if (chars.Length == 0 || chars.Length != id.Length) throw new ArgumentException("Invalid id: " + id);
            return new string(chars);
        }

        // ─────────────── Access ───────────────

        public T Read<T>(Func<OmniBloxState, T> reader)
        {
            lock (gate) return reader(State);
        }

        public T Mutate<T>(Func<OmniBloxState, T> mutator)
        {
            T result;
            lock (gate) result = mutator(State);
            SignalFlush();
            return result;
        }

        public void Mutate(Action<OmniBloxState> mutator)
        {
            lock (gate) mutator(State);
            SignalFlush();
        }

        /// <summary>A detached deep copy, safe to use outside the lock.</summary>
        public static T Clone<T>(T value) =>
            JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value, JsonSettings), JsonSettings)!;

        internal void MarkDirty(string key) => dirty.Add(key);

        internal void QueueEventAppend(OmniBloxEvent ev) => pendingEvents.Add(ev);

        private void SignalFlush()
        {
            try { flushSignal.Release(); } catch (SemaphoreFullException) { }
        }

        // ─────────────── Load ───────────────

        public void Load()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(GamesDirectory);
            Directory.CreateDirectory(AssetsDirectory);
            lock (gate)
            {
                State.Studio = ReadJson<StudioState>(StudioPath) ?? new StudioState();
                State.Playbook = ReadJson<List<PlaybookLesson>>(PlaybookPath) ?? new List<PlaybookLesson>();
                foreach (string dir in Directory.EnumerateDirectories(GamesDirectory))
                {
                    var game = ReadJson<GameDossier>(Path.Combine(dir, "dossier.json"));
                    if (game == null || string.IsNullOrEmpty(game.GameId)) continue;
                    State.Games[game.GameId] = game;
                }
                foreach (var ev in ReadEventTail()) State.Events.Add(ev);
            }
        }

        private T? ReadJson<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            try
            {
                return JsonConvert.DeserializeObject<T>(File.ReadAllText(path), JsonSettings);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                string aside = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                try { File.Move(path, aside); } catch { }
                log($"[OmniBlox] {Path.GetFileName(path)} could not be read ({ex.Message}); moved aside to {Path.GetFileName(aside)}.");
                return null;
            }
        }

        private IEnumerable<OmniBloxEvent> ReadEventTail()
        {
            if (!File.Exists(EventsPath)) return Array.Empty<OmniBloxEvent>();
            var events = new List<OmniBloxEvent>();
            try
            {
                foreach (string line in File.ReadLines(EventsPath).TakeLast(MaxRecentEvents))
                {
                    try
                    {
                        var ev = JsonConvert.DeserializeObject<OmniBloxEvent>(line, JsonSettings);
                        if (ev != null) events.Add(ev);
                    }
                    catch (JsonException) { }
                }
            }
            catch (IOException) { }
            return events;
        }

        // ─────────────── Flush ───────────────

        public void StartFlusher()
        {
            if (flusherTask != null) return;
            flusherCts = new CancellationTokenSource();
            var token = flusherCts.Token;
            flusherTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await flushSignal.WaitAsync(token);
                        await Task.Delay(300, token); // coalesce bursts of mutations
                        await FlushAsync();
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { log("[OmniBlox] state flush failed: " + ex.Message); }
                }
            });
        }

        public async Task StopAsync()
        {
            flusherCts?.Cancel();
            if (flusherTask != null) { try { await flusherTask; } catch { } }
            flusherTask = null;
            await FlushAsync();
        }

        public async Task FlushAsync()
        {
            await writeLock.WaitAsync();
            try
            {
                List<(string Path, string Json)> writes = new();
                List<OmniBloxEvent> events;
                lock (gate)
                {
                    foreach (string key in dirty)
                    {
                        if (key == "studio") writes.Add((StudioPath, JsonConvert.SerializeObject(State.Studio, JsonSettings)));
                        else if (key == "playbook") writes.Add((PlaybookPath, JsonConvert.SerializeObject(State.Playbook, JsonSettings)));
                        else if (key.StartsWith("game:", StringComparison.Ordinal))
                        {
                            string id = key[5..];
                            if (State.Games.TryGetValue(id, out var game))
                                writes.Add((DossierPath(id), JsonConvert.SerializeObject(game, JsonSettings)));
                        }
                    }
                    dirty.Clear();
                    events = pendingEvents.ToList();
                    pendingEvents.Clear();
                }
                foreach (var (path, json) in writes) WriteAtomic(path, json);
                if (events.Count > 0) AppendEvents(events);
            }
            finally
            {
                writeLock.Release();
            }
        }

        internal static void WriteAtomic(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                sw.Write(content);
                sw.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }

        internal static void WriteAtomic(string path, byte[] content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(content);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }

        private void AppendEvents(List<OmniBloxEvent> events)
        {
            try
            {
                if (File.Exists(EventsPath) && new FileInfo(EventsPath).Length > MaxEventLogBytes)
                {
                    var keep = File.ReadLines(EventsPath).TakeLast(MaxRecentEvents).ToList();
                    WriteAtomic(EventsPath, string.Join("\n", keep) + "\n");
                }
                File.AppendAllLines(EventsPath, events.Select(e => JsonConvert.SerializeObject(e, Formatting.None, JsonSettings)));
            }
            catch (IOException ex)
            {
                log("[OmniBlox] could not append events: " + ex.Message);
            }
        }
    }
}
