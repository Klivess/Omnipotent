using Newtonsoft.Json;
using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    /// <summary>
    /// The live state. Only ever touched inside <see cref="OmniTumblrStore.Read{T}"/> /
    /// <see cref="OmniTumblrStore.Mutate{T}"/>, which hold the store lock; anything that must outlive the
    /// lock (a post being uploaded, a DTO being serialized) is cloned out first.
    /// </summary>
    internal sealed class OmniTumblrState
    {
        internal OmniTumblrState(OmniTumblrStore store) => this.store = store;
        private readonly OmniTumblrStore store;

        public OmniTumblrAppConfig App { get; internal set; } = new();
        public Dictionary<string, OmniTumblrConnection> Connections { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, OmniTumblrBlog> Blogs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<OmniTumblrPost>> Posts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, BlogInsights> Insights { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PendingAuthFlow> Flows { get; } = new(StringComparer.Ordinal);
        public OmniTumblrEngineState Engine { get; internal set; } = new();
        public List<OmniTumblrEvent> Events { get; } = new();

        public OmniTumblrVault Vault => store.Vault;

        public List<OmniTumblrPost> PostsOf(string blogId)
        {
            if (!Posts.TryGetValue(blogId, out var list))
            {
                list = new List<OmniTumblrPost>();
                Posts[blogId] = list;
            }
            return list;
        }

        public BlogInsights InsightsOf(string blogId)
        {
            if (!Insights.TryGetValue(blogId, out var insights))
            {
                insights = new BlogInsights();
                Insights[blogId] = insights;
            }
            return insights;
        }

        public IEnumerable<OmniTumblrPost> AllPosts() => Posts.Values.SelectMany(p => p);

        public OmniTumblrPost? FindPost(string postId)
        {
            foreach (var list in Posts.Values)
                foreach (var post in list)
                    if (post.PostId == postId) return post;
            return null;
        }

        public OmniTumblrBlog? Blog(string? blogId) => blogId != null && Blogs.TryGetValue(blogId, out var b) ? b : null;
        public OmniTumblrConnection? Connection(string? id) => id != null && Connections.TryGetValue(id, out var c) ? c : null;

        // ── dirty tracking ──
        public void MarkApp() => store.MarkDirty("app");
        public void MarkConnections() => store.MarkDirty("connections");
        public void MarkFlows() => store.MarkDirty("flows");
        public void MarkEngine() => store.MarkDirty("engine");
        public void MarkBlog(string blogId) => store.MarkDirty("blog:" + blogId);
        public void MarkPosts(string blogId) => store.MarkDirty("posts:" + blogId);
        public void MarkInsights(string blogId) => store.MarkDirty("insights:" + blogId);

        public void Touch(OmniTumblrPost post, DateTime nowUtc)
        {
            post.UpdatedUtc = nowUtc;
            MarkPosts(post.BlogId);
        }

        public OmniTumblrEvent AddEvent(EventLevel level, string kind, string message, string? blogId = null, string? postId = null, DateTime? utc = null)
        {
            var ev = new OmniTumblrEvent { Utc = utc ?? DateTime.UtcNow, Level = level, Kind = kind, Message = message, BlogId = blogId, PostId = postId };
            Events.Add(ev);
            if (Events.Count > OmniTumblrStore.MaxRecentEvents) Events.RemoveRange(0, Events.Count - OmniTumblrStore.MaxRecentEvents);
            store.QueueEventAppend(ev);
            return ev;
        }
    }

    /// <summary>
    /// In-memory state + crash-safe JSON persistence under SavedData/OmniTumblr/v2. Every write is
    /// tmp-file → flush-to-disk → atomic rename, so a crash leaves the previous version, never a torn
    /// file. Writes are coalesced by a background flusher (~250 ms) and forced on shutdown.
    /// </summary>
    internal sealed class OmniTumblrStore
    {
        public const int MaxRecentEvents = 2000;
        private const long MaxEventLogBytes = 4 * 1024 * 1024;

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            NullValueHandling = NullValueHandling.Include,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
        };

        private readonly object gate = new();
        private readonly HashSet<string> dirty = new(StringComparer.Ordinal);
        private readonly List<OmniTumblrEvent> pendingEventAppends = new();
        private readonly SemaphoreSlim flushSignal = new(0, int.MaxValue);
        private readonly SemaphoreSlim writeLock = new(1, 1);
        private readonly Action<string> log;
        private CancellationTokenSource? flusherCts;
        private Task? flusherTask;

        public OmniTumblrStore(string rootDirectory, Action<string> log, OmniTumblrVault? vault = null)
        {
            Root = Path.GetFullPath(rootDirectory);
            this.log = log;
            State = new OmniTumblrState(this);
            Vault = vault ?? new OmniTumblrVault(Path.Combine(Root, "secrets.key"), HasCiphertext, log, QuarantineSecrets);
        }

        public string Root { get; }
        public OmniTumblrVault Vault { get; }
        private OmniTumblrState State { get; }

        public string BlogsDirectory => Path.Combine(Root, "blogs");
        public string LibraryDirectory => Path.Combine(Root, "library");
        public string UploadsDirectory => Path.Combine(Root, "uploads");
        public string ThumbnailsDirectory => Path.Combine(Root, "thumbs");
        public string FramesCacheDirectory => Path.Combine(Root, "cache");
        private string AppPath => Path.Combine(Root, "app.json");
        private string ConnectionsPath => Path.Combine(Root, "connections.json");
        private string FlowsPath => Path.Combine(Root, "auth-flows.json");
        private string EnginePath => Path.Combine(Root, "engine.json");
        private string EventsPath => Path.Combine(Root, "events.jsonl");
        private string BlogDir(string blogId) => Path.Combine(BlogsDirectory, blogId);

        // ─────────────────────────────── Access ───────────────────────────────

        public T Read<T>(Func<OmniTumblrState, T> reader)
        {
            lock (gate) return reader(State);
        }

        public T Mutate<T>(Func<OmniTumblrState, T> mutator)
        {
            T result;
            lock (gate) result = mutator(State);
            SignalFlush();
            return result;
        }

        public void Mutate(Action<OmniTumblrState> mutator)
        {
            lock (gate) mutator(State);
            SignalFlush();
        }

        /// <summary>A detached deep copy, safe to use outside the lock.</summary>
        public static T Clone<T>(T value) =>
            JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value, JsonSettings), JsonSettings)!;

        internal void MarkDirty(string key)
        {
            // Called from inside Mutate (lock held).
            dirty.Add(key);
        }

        internal void QueueEventAppend(OmniTumblrEvent ev) => pendingEventAppends.Add(ev);

        private void SignalFlush()
        {
            try { flushSignal.Release(); } catch (SemaphoreFullException) { }
        }

        // ─────────────────────────────── Load ───────────────────────────────

        public void Load()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(BlogsDirectory);
            Directory.CreateDirectory(LibraryDirectory);
            Directory.CreateDirectory(UploadsDirectory);
            Directory.CreateDirectory(ThumbnailsDirectory);
            Directory.CreateDirectory(FramesCacheDirectory);

            lock (gate)
            {
                State.App = ReadJson<OmniTumblrAppConfig>(AppPath) ?? new OmniTumblrAppConfig();
                State.Engine = ReadJson<OmniTumblrEngineState>(EnginePath) ?? new OmniTumblrEngineState();
                foreach (var c in ReadJson<List<OmniTumblrConnection>>(ConnectionsPath) ?? new())
                    if (!string.IsNullOrEmpty(c.ConnectionId)) State.Connections[c.ConnectionId] = c;
                foreach (var f in ReadJson<List<PendingAuthFlow>>(FlowsPath) ?? new())
                    if (!string.IsNullOrEmpty(f.FlowId)) State.Flows[f.FlowId] = f;

                foreach (string dir in Directory.EnumerateDirectories(BlogsDirectory))
                {
                    var blog = ReadJson<OmniTumblrBlog>(Path.Combine(dir, "blog.json"));
                    if (blog == null || string.IsNullOrEmpty(blog.BlogId)) continue;
                    blog.Strategy ??= OmniTumblrStrategy.CreateDefault();
                    blog.Stats ??= new BlogStats();
                    blog.Health ??= new BlogHealth();
                    blog.UsedContentKeys ??= new HashSet<string>(StringComparer.Ordinal);
                    blog.RejectedContentKeys ??= new HashSet<string>(StringComparer.Ordinal);
                    State.Blogs[blog.BlogId] = blog;
                    State.Posts[blog.BlogId] = ReadJson<List<OmniTumblrPost>>(Path.Combine(dir, "posts.json")) ?? new();
                    State.Insights[blog.BlogId] = ReadJson<BlogInsights>(Path.Combine(dir, "insights.json")) ?? new();
                }

                foreach (var ev in ReadEventTail(MaxRecentEvents)) State.Events.Add(ev);
            }
        }

        private T? ReadJson<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            try
            {
                return JsonConvert.DeserializeObject<T>(File.ReadAllText(path), JsonSettings);
            }
            catch (Exception ex)
            {
                // Keep the unreadable file for forensics and continue with defaults rather than
                // refusing to start (a corrupt posts file must not take every other blog down).
                string parked = path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
                try { File.Move(path, parked, overwrite: false); } catch { }
                log($"OmniTumblr: could not read {path} ({ex.Message}); moved it to {Path.GetFileName(parked)} and continued with defaults.");
                return null;
            }
        }

        private IEnumerable<OmniTumblrEvent> ReadEventTail(int max)
        {
            if (!File.Exists(EventsPath)) return Array.Empty<OmniTumblrEvent>();
            try
            {
                var lines = File.ReadAllLines(EventsPath);
                var result = new List<OmniTumblrEvent>();
                foreach (string line in lines.Skip(Math.Max(0, lines.Length - max)))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var ev = JsonConvert.DeserializeObject<OmniTumblrEvent>(line, JsonSettings);
                        if (ev != null) result.Add(ev);
                    }
                    catch { }
                }
                return result;
            }
            catch (Exception ex)
            {
                log("OmniTumblr: could not read the event log: " + ex.Message);
                return Array.Empty<OmniTumblrEvent>();
            }
        }

        // ─────────────────────────────── Flush ───────────────────────────────

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
                        await Task.Delay(250, token); // coalesce bursts of mutations into one write
                        while (flushSignal.CurrentCount > 0) flushSignal.Wait(0);
                        await FlushAsync();
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        log("OmniTumblr: background save failed: " + ex.Message);
                        try { await Task.Delay(2000, token); } catch { break; }
                    }
                }
            });
        }

        public async Task StopAsync()
        {
            try { flusherCts?.Cancel(); } catch { }
            if (flusherTask != null) { try { await flusherTask; } catch { } }
            await FlushAsync();
        }

        /// <summary>Writes every dirty document now.</summary>
        public async Task FlushAsync()
        {
            await writeLock.WaitAsync();
            try
            {
                List<(string Path, string Json)> writes = new();
                List<OmniTumblrEvent> events;
                lock (gate)
                {
                    foreach (string key in dirty)
                    {
                        var write = Serialize(key);
                        if (write != null) writes.Add(write.Value);
                    }
                    dirty.Clear();
                    events = pendingEventAppends.ToList();
                    pendingEventAppends.Clear();
                }

                foreach (var (path, json) in writes)
                {
                    try { WriteAtomic(path, json); }
                    catch (Exception ex)
                    {
                        log($"OmniTumblr: failed to save {path}: {ex.Message}");
                        lock (gate) dirty.Add(KeyForPath(path)); // retry on the next flush
                    }
                }

                if (events.Count > 0) AppendEvents(events);
            }
            finally
            {
                writeLock.Release();
            }
        }

        private (string Path, string Json)? Serialize(string key)
        {
            if (key == "app") return (AppPath, JsonConvert.SerializeObject(State.App, Formatting.Indented, JsonSettings));
            if (key == "connections") return (ConnectionsPath, JsonConvert.SerializeObject(State.Connections.Values.ToList(), Formatting.Indented, JsonSettings));
            if (key == "flows") return (FlowsPath, JsonConvert.SerializeObject(State.Flows.Values.ToList(), Formatting.Indented, JsonSettings));
            if (key == "engine") return (EnginePath, JsonConvert.SerializeObject(State.Engine, Formatting.Indented, JsonSettings));
            int colon = key.IndexOf(':');
            if (colon < 0) return null;
            string kind = key[..colon], blogId = key[(colon + 1)..];
            if (!State.Blogs.TryGetValue(blogId, out var blog))
                return null; // removed since it was marked
            string dir = BlogDir(blogId);
            return kind switch
            {
                "blog" => (Path.Combine(dir, "blog.json"), JsonConvert.SerializeObject(blog, Formatting.Indented, JsonSettings)),
                "posts" => (Path.Combine(dir, "posts.json"), JsonConvert.SerializeObject(State.PostsOf(blogId), Formatting.None, JsonSettings)),
                "insights" => (Path.Combine(dir, "insights.json"), JsonConvert.SerializeObject(State.InsightsOf(blogId), Formatting.None, JsonSettings)),
                _ => null,
            };
        }

        private string KeyForPath(string path)
        {
            if (path == AppPath) return "app";
            if (path == ConnectionsPath) return "connections";
            if (path == FlowsPath) return "flows";
            if (path == EnginePath) return "engine";
            string blogId = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
            return Path.GetFileName(path) switch
            {
                "blog.json" => "blog:" + blogId,
                "posts.json" => "posts:" + blogId,
                _ => "insights:" + blogId,
            };
        }

        internal static void WriteAtomic(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(content);
                    writer.Flush();
                    fs.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private void AppendEvents(List<OmniTumblrEvent> events)
        {
            try
            {
                var info = new FileInfo(EventsPath);
                if (info.Exists && info.Length > MaxEventLogBytes)
                {
                    string rotated = Path.Combine(Root, "events.1.jsonl");
                    File.Move(EventsPath, rotated, overwrite: true);
                }
                File.AppendAllLines(EventsPath, events.Select(e => JsonConvert.SerializeObject(e, Formatting.None, JsonSettings)));
            }
            catch (Exception ex)
            {
                log("OmniTumblr: failed to append events: " + ex.Message);
            }
        }

        /// <summary>
        /// Archives a blog's documents after it was removed from the state. Holds the write lock so an
        /// in-flight background save cannot recreate the folder behind the move.
        /// </summary>
        public async Task ArchiveBlogFilesAsync(string blogId)
        {
            string dir = BlogDir(blogId);
            await writeLock.WaitAsync();
            try
            {
                if (Directory.Exists(dir))
                {
                    string archive = Path.Combine(Root, "removed", $"{blogId}-{DateTime.UtcNow:yyyyMMddHHmmss}");
                    Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                    Directory.Move(dir, archive); // kept, not destroyed: a removed blog's history is cheap to keep
                }
            }
            catch (Exception ex)
            {
                log($"OmniTumblr: could not archive blog folder {dir}: {ex.Message}");
            }
            finally
            {
                writeLock.Release();
            }
        }

        // ─────────────────────────────── Vault hooks ───────────────────────────────

        private bool HasCiphertext()
        {
            lock (gate)
            {
                return OmniTumblrVault.IsProtected(State.App.ConsumerKeyCipher)
                    || OmniTumblrVault.IsProtected(State.App.ConsumerSecretCipher)
                    || State.Connections.Values.Any(c => OmniTumblrVault.IsProtected(c.TokenCipher) || OmniTumblrVault.IsProtected(c.RefreshTokenCipher));
            }
        }

        /// <summary>
        /// The root key was lost or corrupt: every stored secret is unrecoverable. Clear them (keeping the
        /// rest of the configuration) so the app can be re-entered and blogs re-authorized, instead of
        /// refusing to run.
        /// </summary>
        private string QuarantineSecrets()
        {
            int cleared = 0;
            lock (gate)
            {
                if (State.App.ConsumerKeyCipher != null || State.App.ConsumerSecretCipher != null)
                {
                    State.App.ConsumerKeyCipher = null;
                    State.App.ConsumerSecretCipher = null;
                    State.App.LastVerifyError = "The encryption key for stored secrets was lost; re-enter the Tumblr app credentials.";
                    cleared++;
                    dirty.Add("app");
                }
                foreach (var c in State.Connections.Values)
                {
                    if (c.TokenCipher == null && c.RefreshTokenCipher == null) continue;
                    c.TokenCipher = null;
                    c.TokenSecretCipher = null;
                    c.RefreshTokenCipher = null;
                    c.Health = ConnectionHealth.NeedsReauth;
                    c.HealthDetail = "Stored tokens became unreadable (encryption key lost). Reconnect this account.";
                    cleared++;
                    dirty.Add("connections");
                }
            }
            SignalFlush();
            return $"{cleared} credential set(s)";
        }
    }
}
