using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Omnipotent.Services.OmniDefence
{
    internal enum ReadSnapshotState { Ready, Pending, Busy }
    internal readonly record struct ReadSnapshotReply(ReadSnapshotState State, string? Json);

    /// <summary>
    /// Bounded, query-keyed read materialization. HTTP only looks up an immutable body or
    /// enqueues a key; fixed background workers own every SQLite read and serialization.
    /// </summary>
    internal sealed class OmniDefenceReadSnapshotCache
    {
        private const int MaxEntries = 128;
        private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
        private readonly string directory;
        private readonly Action<Exception>? reportError;
        private readonly object gate = new();
        private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
        private readonly Channel<Entry> requestQueue = NewQueue();
        private readonly Channel<Entry> ipQueue = NewQueue();
        private CancellationToken lifetimeToken;
        private bool started;

        private sealed class Entry
        {
            public required string Key { get; init; }
            public required bool IsIp { get; init; }
            public required Func<CancellationToken, Task<string>> Build { get; set; }
            public bool Pinned { get; set; }
            public bool Queued { get; set; }
            public bool RestoreAttempted { get; set; }
            public string? Json { get; set; }
            public DateTimeOffset AsOfUtc { get; set; }
            public DateTimeOffset LastAccessUtc { get; set; }
            public DateTimeOffset LastFailureUtc { get; set; }
            public long Version { get; set; }
        }

        private sealed class SavedEntry
        {
            public string Key { get; set; } = "";
            public bool IsIp { get; set; }
            public string Json { get; set; } = "";
            public DateTimeOffset AsOfUtc { get; set; }
        }

        public OmniDefenceReadSnapshotCache(string directory, Action<Exception>? reportError = null)
        {
            this.directory = directory;
            this.reportError = reportError;
        }

        public static string KeyFor(string kind, string value)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return kind + ":" + Convert.ToHexString(bytes);
        }

        public void Start(CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (started) return;
                lifetimeToken = cancellationToken;
                started = true;
            }
            _ = Task.Run(() => RunWorkerAsync(requestQueue.Reader, cancellationToken));
            _ = Task.Run(() => RunWorkerAsync(ipQueue.Reader, cancellationToken));
            _ = Task.Run(() => RunRefreshLoopAsync(cancellationToken));
        }

        public ReadSnapshotReply GetOrQueue(string key, bool isIp,
            Func<CancellationToken, Task<string>> build, bool pinned = false)
        {
            lock (gate)
            {
                if (!started || lifetimeToken.IsCancellationRequested) return new(ReadSnapshotState.Busy, null);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (!entries.TryGetValue(key, out Entry? entry))
                {
                    if (entries.Count >= MaxEntries && !EvictOne())
                        return new(ReadSnapshotState.Busy, null);
                    entry = new Entry { Key = key, IsIp = isIp, Build = build, Pinned = pinned, LastAccessUtc = now };
                    entries.Add(key, entry);
                }
                else
                {
                    entry.Build = build;
                    entry.Pinned |= pinned;
                    entry.LastAccessUtc = now;
                }

                if (entry.Json != null && IsFresh(entry.AsOfUtc, now))
                    return new(ReadSnapshotState.Ready, entry.Json);

                QueueIfNeeded(entry, now);
                return new(entry.Queued ? ReadSnapshotState.Pending : ReadSnapshotState.Busy, null);
            }
        }

        public void Invalidate(string key)
        {
            lock (gate)
            {
                if (!entries.TryGetValue(key, out Entry? entry)) return;
                entry.Json = null;
                entry.AsOfUtc = default;
                entry.LastFailureUtc = default;
                entry.Version++;
                QueueIfNeeded(entry, DateTimeOffset.UtcNow);
            }
        }

        private bool EvictOne()
        {
            Entry? oldest = entries.Values.Where(e => !e.Pinned && !e.Queued)
                .OrderBy(e => e.LastAccessUtc).FirstOrDefault();
            if (oldest == null) return false;
            entries.Remove(oldest.Key);
            return true;
        }

        private void QueueIfNeeded(Entry entry, DateTimeOffset now)
        {
            if (entry.Queued || now - entry.LastFailureUtc < RetryDelay) return;
            entry.Queued = (entry.IsIp ? ipQueue.Writer : requestQueue.Writer).TryWrite(entry);
        }

        private static bool IsFresh(DateTimeOffset asOfUtc, DateTimeOffset now)
        {
            TimeSpan age = now - asOfUtc;
            return age >= TimeSpan.FromMinutes(-1) && age <= MaxAge;
        }

        private static Channel<Entry> NewQueue() => Channel.CreateBounded<Entry>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        private async Task RunWorkerAsync(ChannelReader<Entry> queue, CancellationToken cancellationToken)
        {
            try
            {
                await foreach (Entry entry in queue.ReadAllAsync(cancellationToken))
                {
                    long version;
                    lock (gate) version = entry.Version;
                    try
                    {
                        // Disk recovery also happens on the worker. A cold HTTP request never
                        // reads a file, and an expired file never becomes an API response.
                        bool restore;
                        lock (gate)
                        {
                            restore = !entry.RestoreAttempted;
                            entry.RestoreAttempted = true;
                        }
                        SavedEntry? saved = restore && entry.Pinned
                            ? await TryRestoreAsync(entry, cancellationToken) : null;
                        if (saved != null)
                        {
                            Publish(entry, saved.Json, saved.AsOfUtc, version);
                            continue;
                        }

                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        deadline.CancelAfter(TimeSpan.FromSeconds(12));
                        DateTimeOffset observedFromUtc = DateTimeOffset.UtcNow;
                        string json = await entry.Build(deadline.Token);
                        ValidateBody(entry.IsIp, json);
                        deadline.Token.ThrowIfCancellationRequested();
                        if (!Publish(entry, json, observedFromUtc, version)) continue;
                        // Persist only the known default view. Arbitrary filters and IPs
                        // must not create an unbounded number of files under attack.
                        if (entry.Pinned) await SaveAsync(entry, json, observedFromUtc, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        lock (gate) entry.LastFailureUtc = DateTimeOffset.UtcNow;
                        reportError?.Invoke(ex);
                    }
                    finally
                    {
                        lock (gate)
                        {
                            entry.Queued = false;
                            if (entry.Version != version) QueueIfNeeded(entry, DateTimeOffset.UtcNow);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private bool Publish(Entry entry, string json, DateTimeOffset asOfUtc, long version)
        {
            lock (gate)
            {
                if (entry.Version != version) return false;
                // A completed background result remains immutable to request readers.
                entry.Json = json;
                entry.AsOfUtc = asOfUtc;
                entry.LastFailureUtc = default;
                return true;
            }
        }

        private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
                catch (OperationCanceledException) { break; }
                lock (gate)
                {
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    foreach (Entry entry in entries.Values)
                    {
                        if (!entry.Pinned && now - entry.LastAccessUtc > TimeSpan.FromMinutes(2)) continue;
                        TimeSpan age = now - entry.AsOfUtc;
                        if (entry.Json == null || age >= (entry.Pinned ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(25)))
                            QueueIfNeeded(entry, now);
                    }
                }
            }
        }

        private string PathFor(Entry entry) => Path.Combine(directory,
            (entry.IsIp ? "ip-" : "requests-") + entry.Key[(entry.Key.IndexOf(':') + 1)..] + ".json");

        private async Task<SavedEntry?> TryRestoreAsync(Entry entry, CancellationToken cancellationToken)
        {
            try
            {
                string path = PathFor(entry);
                if (!File.Exists(path)) return null;
                string text = await File.ReadAllTextAsync(path, cancellationToken);
                SavedEntry? saved = JsonConvert.DeserializeObject<SavedEntry>(text);
                if (saved == null || saved.Key != entry.Key || saved.IsIp != entry.IsIp
                    || !IsFresh(saved.AsOfUtc, DateTimeOffset.UtcNow)) return null;
                ValidateBody(entry.IsIp, saved.Json);
                return saved;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private async Task SaveAsync(Entry entry, string json, DateTimeOffset asOfUtc,
            CancellationToken cancellationToken)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = PathFor(entry);
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var saved = new SavedEntry { Key = entry.Key, IsIp = entry.IsIp, Json = json, AsOfUtc = asOfUtc };
                    await File.WriteAllTextAsync(temp, JsonConvert.SerializeObject(saved), cancellationToken);
                    File.Move(temp, path, overwrite: true);
                }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) { reportError?.Invoke(ex); }
        }

        private static void ValidateBody(bool isIp, string json)
        {
            JTokenType expected = isIp ? JTokenType.Object : JTokenType.Array;
            if (JToken.Parse(json).Type != expected)
                throw new InvalidDataException($"OmniDefence snapshot must be a JSON {expected}.");
        }
    }
}
