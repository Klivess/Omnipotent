using Newtonsoft.Json;
using Omnipotent.Services.OmniGram.Models;

namespace Omnipotent.Services.OmniGram.Engine
{
    /// <summary>
    /// The live state. Only ever touched inside <see cref="OmniGramStore.Read{T}"/> /
    /// <see cref="OmniGramStore.Mutate{T}"/>, which hold the store lock; anything that must outlive the lock
    /// (a post being uploaded, a DTO being serialized) is cloned out first.
    /// </summary>
    internal sealed class OmniGramState
    {
        internal OmniGramState(OmniGramStore store) => this.store = store;
        private readonly OmniGramStore store;

        public OmniGramAppConfig App { get; internal set; } = new();
        public Dictionary<string, OmniGramAccount> Accounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<OmniGramPost>> Posts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, AccountInsights> Insights { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PendingAuthFlow> Flows { get; } = new(StringComparer.Ordinal);
        public OmniGramEngineState Engine { get; internal set; } = new();
        public List<OmniGramEvent> Events { get; } = new();

        public OmniGramVault Vault => store.Vault;

        public List<OmniGramPost> PostsOf(string accountId)
        {
            if (!Posts.TryGetValue(accountId, out var list))
            {
                list = new List<OmniGramPost>();
                Posts[accountId] = list;
            }
            return list;
        }

        public AccountInsights InsightsOf(string accountId)
        {
            if (!Insights.TryGetValue(accountId, out var insights))
            {
                insights = new AccountInsights();
                Insights[accountId] = insights;
            }
            return insights;
        }

        public IEnumerable<OmniGramPost> AllPosts() => Posts.Values.SelectMany(p => p);

        public OmniGramPost? FindPost(string postId)
        {
            foreach (var list in Posts.Values)
                foreach (var post in list)
                    if (post.PostId == postId) return post;
            return null;
        }

        public OmniGramAccount? Account(string? accountId) => accountId != null && Accounts.TryGetValue(accountId, out var a) ? a : null;

        public OmniGramAccount? AccountByUsername(string? username) => string.IsNullOrWhiteSpace(username) ? null
            : Accounts.Values.FirstOrDefault(a => string.Equals(a.Username, username.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase));

        // ── dirty tracking ──
        public void MarkApp() => store.MarkDirty("app");
        public void MarkFlows() => store.MarkDirty("flows");
        public void MarkEngine() => store.MarkDirty("engine");
        public void MarkAccount(string accountId) => store.MarkDirty("account:" + accountId);
        public void MarkPosts(string accountId) => store.MarkDirty("posts:" + accountId);
        public void MarkInsights(string accountId) => store.MarkDirty("insights:" + accountId);

        public void Touch(OmniGramPost post, DateTime nowUtc)
        {
            post.UpdatedUtc = nowUtc;
            MarkPosts(post.AccountId);
        }

        public OmniGramEvent AddEvent(EventLevel level, string kind, string message, string? accountId = null, string? postId = null, DateTime? utc = null)
        {
            var ev = new OmniGramEvent { Utc = utc ?? DateTime.UtcNow, Level = level, Kind = kind, Message = message, AccountId = accountId, PostId = postId };
            Events.Add(ev);
            if (Events.Count > OmniGramStore.MaxRecentEvents) Events.RemoveRange(0, Events.Count - OmniGramStore.MaxRecentEvents);
            store.QueueEventAppend(ev);
            return ev;
        }
    }

    /// <summary>
    /// In-memory state + crash-safe JSON persistence under SavedData/OmniGram/v2. Every write is
    /// tmp-file → flush-to-disk → atomic rename, so a crash leaves the previous version, never a torn file.
    /// Writes are coalesced by a background flusher (~250 ms) and forced on shutdown.
    /// </summary>
    internal sealed class OmniGramStore
    {
        public const int MaxRecentEvents = 2000;
        private const long MaxEventLogBytes = 4 * 1024 * 1024;

        internal static readonly JsonSerializerSettings JsonSettings = new()
        {
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            NullValueHandling = NullValueHandling.Include,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
        };

        private readonly object gate = new();
        private readonly HashSet<string> dirty = new(StringComparer.Ordinal);
        private readonly List<OmniGramEvent> pendingEventAppends = new();
        private readonly SemaphoreSlim flushSignal = new(0, int.MaxValue);
        private readonly SemaphoreSlim writeLock = new(1, 1);
        private readonly Action<string> log;
        private CancellationTokenSource? flusherCts;
        private Task? flusherTask;

        public OmniGramStore(string rootDirectory, Action<string> log, OmniGramVault? vault = null)
        {
            Root = Path.GetFullPath(rootDirectory);
            this.log = log;
            State = new OmniGramState(this);
            Vault = vault ?? new OmniGramVault(Path.Combine(Root, "secrets.key"), HasCiphertext, log, QuarantineSecrets);
        }

        public string Root { get; }
        public OmniGramVault Vault { get; }
        private OmniGramState State { get; }

        public string AccountsDirectory => Path.Combine(Root, "accounts");
        public string LibraryDirectory => Path.Combine(Root, "library");
        public string UploadsDirectory => Path.Combine(Root, "uploads");
        /// <summary>Instagram-ready copies of post media. The public media route serves files from here only.</summary>
        public string PreparedDirectory => Path.Combine(Root, "prepared");
        public string ThumbnailsDirectory => Path.Combine(Root, "thumbs");
        public string CacheDirectory => Path.Combine(Root, "cache");
        private string AppPath => Path.Combine(Root, "app.json");
        private string FlowsPath => Path.Combine(Root, "auth-flows.json");
        private string EnginePath => Path.Combine(Root, "engine.json");
        private string EventsPath => Path.Combine(Root, "events.jsonl");
        private string AccountDir(string accountId) => Path.Combine(AccountsDirectory, accountId);

        // ─────────────────────────────── Access ───────────────────────────────

        public T Read<T>(Func<OmniGramState, T> reader)
        {
            lock (gate) return reader(State);
        }

        public T Mutate<T>(Func<OmniGramState, T> mutator)
        {
            T result;
            lock (gate) result = mutator(State);
            SignalFlush();
            return result;
        }

        public void Mutate(Action<OmniGramState> mutator)
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

        internal void QueueEventAppend(OmniGramEvent ev) => pendingEventAppends.Add(ev);

        private void SignalFlush()
        {
            try { flushSignal.Release(); } catch (SemaphoreFullException) { }
        }

        // ─────────────────────────────── Load ───────────────────────────────

        public void Load()
        {
            foreach (string dir in new[] { Root, AccountsDirectory, LibraryDirectory, UploadsDirectory, PreparedDirectory, ThumbnailsDirectory, CacheDirectory })
                Directory.CreateDirectory(dir);

            lock (gate)
            {
                State.App = ReadJson<OmniGramAppConfig>(AppPath) ?? new OmniGramAppConfig();
                State.Engine = ReadJson<OmniGramEngineState>(EnginePath) ?? new OmniGramEngineState();
                foreach (var f in ReadJson<List<PendingAuthFlow>>(FlowsPath) ?? new())
                    if (!string.IsNullOrEmpty(f.FlowId)) State.Flows[f.FlowId] = f;

                foreach (string dir in Directory.EnumerateDirectories(AccountsDirectory))
                {
                    var account = ReadJson<OmniGramAccount>(Path.Combine(dir, "account.json"));
                    if (account == null || string.IsNullOrEmpty(account.AccountId)) continue;
                    account.Strategy ??= OmniGramStrategy.CreateDefault();
                    account.Strategy.Reels ??= new ReelSettings();
                    account.Strategy.Ai ??= new AiCaptionSettings();
                    account.Strategy.MemeScraper ??= new MemeScraperContentFilter();
                    account.Strategy.Folder ??= new FolderContentSettings();
                    account.Stats ??= new AccountStats();
                    account.Health ??= new AccountHealth();
                    account.Quota ??= new PublishingQuota();
                    account.Scopes ??= new List<string>();
                    account.Tags ??= new List<string>();
                    account.UsedContentKeys ??= new HashSet<string>(StringComparer.Ordinal);
                    account.RejectedContentKeys ??= new HashSet<string>(StringComparer.Ordinal);
                    State.Accounts[account.AccountId] = account;
                    State.Posts[account.AccountId] = ReadJson<List<OmniGramPost>>(Path.Combine(dir, "posts.json")) ?? new();
                    State.Insights[account.AccountId] = ReadJson<AccountInsights>(Path.Combine(dir, "insights.json")) ?? new();
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
                // Keep the unreadable file for forensics and continue with defaults rather than refusing to
                // start (a corrupt posts file must not take every other account down).
                string parked = path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
                try { File.Move(path, parked, overwrite: false); } catch { }
                log($"OmniGram: could not read {path} ({ex.Message}); moved it to {Path.GetFileName(parked)} and continued with defaults.");
                return null;
            }
        }

        private IEnumerable<OmniGramEvent> ReadEventTail(int max)
        {
            if (!File.Exists(EventsPath)) return Array.Empty<OmniGramEvent>();
            try
            {
                var lines = File.ReadAllLines(EventsPath);
                var result = new List<OmniGramEvent>();
                foreach (string line in lines.Skip(Math.Max(0, lines.Length - max)))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var ev = JsonConvert.DeserializeObject<OmniGramEvent>(line, JsonSettings);
                        if (ev != null) result.Add(ev);
                    }
                    catch { }
                }
                return result;
            }
            catch (Exception ex)
            {
                log("OmniGram: could not read the event log: " + ex.Message);
                return Array.Empty<OmniGramEvent>();
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
                        log("OmniGram: background save failed: " + ex.Message);
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
                List<OmniGramEvent> events;
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
                        log($"OmniGram: failed to save {path}: {ex.Message}");
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
            if (key == "flows") return (FlowsPath, JsonConvert.SerializeObject(State.Flows.Values.ToList(), Formatting.Indented, JsonSettings));
            if (key == "engine") return (EnginePath, JsonConvert.SerializeObject(State.Engine, Formatting.Indented, JsonSettings));
            int colon = key.IndexOf(':');
            if (colon < 0) return null;
            string kind = key[..colon], accountId = key[(colon + 1)..];
            if (!State.Accounts.TryGetValue(accountId, out var account))
                return null; // removed since it was marked
            string dir = AccountDir(accountId);
            return kind switch
            {
                "account" => (Path.Combine(dir, "account.json"), JsonConvert.SerializeObject(account, Formatting.Indented, JsonSettings)),
                "posts" => (Path.Combine(dir, "posts.json"), JsonConvert.SerializeObject(State.PostsOf(accountId), Formatting.None, JsonSettings)),
                "insights" => (Path.Combine(dir, "insights.json"), JsonConvert.SerializeObject(State.InsightsOf(accountId), Formatting.None, JsonSettings)),
                _ => null,
            };
        }

        private string KeyForPath(string path)
        {
            if (path == AppPath) return "app";
            if (path == FlowsPath) return "flows";
            if (path == EnginePath) return "engine";
            string accountId = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
            return Path.GetFileName(path) switch
            {
                "account.json" => "account:" + accountId,
                "posts.json" => "posts:" + accountId,
                _ => "insights:" + accountId,
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

        private void AppendEvents(List<OmniGramEvent> events)
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
                log("OmniGram: failed to append events: " + ex.Message);
            }
        }

        /// <summary>
        /// Archives an account's documents after it was removed from the state. Holds the write lock so an
        /// in-flight background save cannot recreate the folder behind the move.
        /// </summary>
        public async Task ArchiveAccountFilesAsync(string accountId)
        {
            string dir = AccountDir(accountId);
            await writeLock.WaitAsync();
            try
            {
                if (Directory.Exists(dir))
                {
                    string archive = Path.Combine(Root, "removed", $"{accountId}-{DateTime.UtcNow:yyyyMMddHHmmss}");
                    Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                    Directory.Move(dir, archive); // kept, not destroyed: a removed account's history is cheap to keep
                }
            }
            catch (Exception ex)
            {
                log($"OmniGram: could not archive account folder {dir}: {ex.Message}");
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
                return OmniGramVault.IsProtected(State.App.AppSecretCipher)
                    || State.Accounts.Values.Any(a => OmniGramVault.IsProtected(a.TokenCipher));
            }
        }

        /// <summary>
        /// The root key was lost or corrupt: every stored secret is unrecoverable. Clear them (keeping the
        /// rest of the configuration) so the app secret can be re-entered and accounts reconnected, instead
        /// of refusing to run.
        /// </summary>
        private string QuarantineSecrets()
        {
            int cleared = 0;
            lock (gate)
            {
                if (State.App.AppSecretCipher != null)
                {
                    State.App.AppSecretCipher = null;
                    State.App.LastVerifyError = "The encryption key for stored secrets was lost; re-enter the Instagram app secret.";
                    cleared++;
                    dirty.Add("app");
                }
                foreach (var a in State.Accounts.Values)
                {
                    if (a.TokenCipher == null) continue;
                    a.TokenCipher = null;
                    a.Connection = ConnectionState.NeedsReauth;
                    a.ConnectionDetail = "The stored token became unreadable (encryption key lost). Reconnect this account.";
                    cleared++;
                    dirty.Add("account:" + a.AccountId);
                }
            }
            SignalFlush();
            return $"{cleared} credential(s)";
        }
    }
}
