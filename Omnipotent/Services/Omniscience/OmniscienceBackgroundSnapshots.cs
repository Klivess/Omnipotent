using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Omniscience;

/// <summary>
/// Builds expensive, fixed-key route payloads outside HTTP handlers and keeps the last
/// good result on disk. A request only reads an immutable in-memory snapshot.
/// </summary>
internal sealed class OmniscienceBackgroundSnapshots : IDisposable
{
    private sealed record Snapshot(DateTimeOffset GeneratedUtc, string Payload);
    private sealed record SnapshotFile(DateTimeOffset GeneratedUtc, string Payload);

    private sealed class Slot
    {
        public required string Path;
        public required TimeSpan RefreshInterval;
        public required TimeSpan MaxAge;
        public required Func<string> Build;
        public readonly SemaphoreSlim BuildGate = new(1, 1);
        public readonly SemaphoreSlim Wake = new(0, 1);
        public Snapshot? Current;
        public DateTimeOffset LastAttemptUtc;
    }

    private readonly ConcurrentDictionary<string, Slot> slots = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stop = new();
    private readonly string directory;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Action<string, Exception>? onError;

    internal OmniscienceBackgroundSnapshots(
        string directory,
        Action<string, Exception>? onError = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.directory = directory;
        this.onError = onError;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal void Register(
        string key,
        string fileName,
        TimeSpan refreshInterval,
        TimeSpan maxAge,
        Func<string> build,
        bool startWorker = true)
    {
        if (Path.GetFileName(fileName) != fileName || string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Snapshot file name must not contain a directory.", nameof(fileName));
        if (refreshInterval <= TimeSpan.Zero || maxAge < refreshInterval)
            throw new ArgumentOutOfRangeException(nameof(refreshInterval));

        var slot = new Slot
        {
            Path = Path.Combine(directory, fileName),
            RefreshInterval = refreshInterval,
            MaxAge = maxAge,
            Build = build,
        };
        Load(slot, key);
        if (!slots.TryAdd(key, slot)) throw new InvalidOperationException($"Snapshot target {key} is already registered.");
        if (startWorker) _ = Task.Run(() => RefreshLoopAsync(key, slot, stop.Token));
    }

    internal string? TryGet(string key)
    {
        if (!slots.TryGetValue(key, out var slot)) return null;
        var snapshot = Volatile.Read(ref slot.Current);
        if (snapshot == null) return null;
        TimeSpan age = utcNow() - snapshot.GeneratedUtc;
        return age >= TimeSpan.FromMinutes(-1) && age <= slot.MaxAge ? snapshot.Payload : null;
    }

    internal void MarkDirty(string key)
    {
        if (!slots.TryGetValue(key, out var slot)) return;
        try { slot.Wake.Release(); }
        catch (SemaphoreFullException) { /* many writes collapse into one refresh */ }
    }

    internal async Task RefreshNowAsync(string key, CancellationToken ct = default)
    {
        if (!slots.TryGetValue(key, out var slot)) throw new ArgumentException("Unknown snapshot target.", nameof(key));
        await slot.BuildGate.WaitAsync(ct);
        try
        {
            DateTimeOffset observedFromUtc = utcNow();
            slot.LastAttemptUtc = observedFromUtc;
            string payload = await Task.Run(slot.Build, ct);
            JToken.Parse(payload); // never publish a malformed response
            var snapshot = new Snapshot(observedFromUtc, payload);
            Volatile.Write(ref slot.Current, snapshot);
            try { await SaveAsync(slot.Path, snapshot, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { onError?.Invoke(key, ex); }
        }
        finally { slot.BuildGate.Release(); }
    }

    private async Task RefreshLoopAsync(string key, Slot slot, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool succeeded = true;
            try { await RefreshNowAsync(key, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                succeeded = false;
                onError?.Invoke(key, ex);
            }

            try
            {
                await slot.Wake.WaitAsync(succeeded ? slot.RefreshInterval : TimeSpan.FromSeconds(10), ct);
                // A write can wake the worker immediately, but repeated writes cannot
                // launch an unbounded series of full-database scans.
                TimeSpan sinceAttempt = utcNow() - slot.LastAttemptUtc;
                if (sinceAttempt < TimeSpan.FromSeconds(10))
                    await Task.Delay(TimeSpan.FromSeconds(10) - sinceAttempt, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private void Load(Slot slot, string key)
    {
        try
        {
            if (!File.Exists(slot.Path)) return;
            var saved = JsonConvert.DeserializeObject<SnapshotFile>(File.ReadAllText(slot.Path));
            if (saved == null || string.IsNullOrEmpty(saved.Payload)) return;
            JToken.Parse(saved.Payload);
            Volatile.Write(ref slot.Current, new Snapshot(saved.GeneratedUtc, saved.Payload));
        }
        catch (Exception ex) { onError?.Invoke(key, ex); }
    }

    private static async Task SaveAsync(string path, Snapshot snapshot, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp,
                JsonConvert.SerializeObject(new SnapshotFile(snapshot.GeneratedUtc, snapshot.Payload)), ct);
            File.Move(temp, path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public void Dispose() => stop.Cancel();
}
