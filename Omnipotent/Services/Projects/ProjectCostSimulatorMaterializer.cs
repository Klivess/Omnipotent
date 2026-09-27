using System.Collections.Concurrent;
using System.Threading.Channels;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects;

internal sealed record CostSimulatorReadResult(string? Json, bool Pending, bool Busy, long After,
    string? FromUtc = null, string? ToUtc = null);

/// <summary>
/// Holds serialized route responses. The route validates/queues a request and reads this table;
/// every journal scan, serialization and disk write runs on background workers.
/// </summary>
public sealed class ProjectCostSimulatorMaterializer
{
    private sealed record Prepared(string Json, long AsOfTicks);
    private sealed record CustomBuild(string Key, AnalyticsRange Range, bool IncludeArchived);

    private const int MaxQueuedCustom = 8;
    private const int MaxPreparedCustom = 16;
    private static readonly TimeSpan MaxDefaultAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxCustomAge = TimeSpan.FromMinutes(10);
    private readonly Func<IReadOnlyDictionary<string, CostSimulationSnapshot>> buildDefaults;
    private readonly Func<AnalyticsRange, bool, CostSimulationSnapshot> buildCustom;
    private readonly Action<string> log;
    private readonly string directory;
    private readonly ConcurrentDictionary<string, Prepared> responses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> customQueued = new(StringComparer.Ordinal);
    private readonly Channel<CustomBuild> customBuilds = Channel.CreateBounded<CustomBuild>(
        new BoundedChannelOptions(MaxQueuedCustom) { SingleReader = true, SingleWriter = false });
    private readonly SemaphoreSlim defaultWake = new(0, 1);

    public ProjectCostSimulatorMaterializer(ProjectCostSimulatorService service,
        Action<string>? log = null, string? snapshotDirectory = null)
        : this(service.BuildDefaultSnapshots, service.BuildCustom, log, snapshotDirectory) { }

    internal ProjectCostSimulatorMaterializer(
        Func<IReadOnlyDictionary<string, CostSimulationSnapshot>> buildDefaults,
        Func<AnalyticsRange, bool, CostSimulationSnapshot> buildCustom,
        Action<string>? log = null, string? snapshotDirectory = null)
    {
        this.buildDefaults = buildDefaults;
        this.buildCustom = buildCustom;
        this.log = log ?? (_ => { });
        directory = snapshotDirectory ?? Path.Combine(
            OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsDirectory), "RouteSnapshots");
    }

    internal static string Key(string range, bool includeArchived)
        => range + "|" + (includeArchived ? "all" : "active");

    internal CostSimulatorReadResult Read(string? rangeKey, string? fromUtc, string? toUtc,
        bool includeArchived, bool fresh = false, long after = 0)
    {
        DateTime now = DateTime.UtcNow;
        if (after > now.AddMinutes(1).Ticks)
            throw new ArgumentException("Cost simulator after must not be in the future.");
        string range = (rangeKey ?? "30d").Trim().ToLowerInvariant();
        if (range is "1y" or "year") range = "365d";
        bool custom = range == "custom" || fromUtc != null || toUtc != null;
        AnalyticsRange? customRange = custom
            ? ProjectAnalyticsCalculator.ResolveRange("custom", now.Date, now, fromUtc, toUtc)
            : null;
        if (!custom && !ProjectCostSimulatorService.DefaultRanges.Contains(range, StringComparer.Ordinal))
            range = "30d";
        string key = customRange == null
            ? Key(range, includeArchived)
            : "custom|" + customRange.FromUtc.Ticks + "|" + customRange.ToUtc.Ticks
              + "|" + (includeArchived ? "all" : "active");
        string? canonicalFrom = customRange?.FromUtc.ToString("O");
        string? canonicalTo = customRange?.ToUtc.ToString("O");

        // A manual refresh waits for a build that began after this request. For normal reads,
        // returning a prepared snapshot is constant time even while a newer build is running.
        long requiredAfter = fresh ? now.Ticks : Math.Max(0, after);
        if (!fresh && responses.TryGetValue(key, out var prepared)
            && prepared.AsOfTicks > requiredAfter
            && now.Ticks - prepared.AsOfTicks <= (customRange == null ? MaxDefaultAge : MaxCustomAge).Ticks
            && prepared.AsOfTicks <= now.AddMinutes(1).Ticks)
        {
            if (customRange != null && now.Ticks - prepared.AsOfTicks > TimeSpan.FromMinutes(1).Ticks)
                QueueCustom(key, customRange, includeArchived);
            return new CostSimulatorReadResult(prepared.Json, false, false, requiredAfter,
                canonicalFrom, canonicalTo);
        }

        if (customRange != null)
        {
            if (!QueueCustom(key, customRange, includeArchived))
                return new CostSimulatorReadResult(null, false, true, requiredAfter,
                    canonicalFrom, canonicalTo);
        }
        else WakeDefault();
        return new CostSimulatorReadResult(null, true, false, requiredAfter,
            canonicalFrom, canonicalTo);
    }

    public async Task LoadAsync()
    {
        DateTime nowUtc = DateTime.UtcNow;
        foreach (string range in ProjectCostSimulatorService.DefaultRanges)
        foreach (bool includeArchived in new[] { true, false })
        {
            string key = Key(range, includeArchived);
            try
            {
                string path = PathFor(range, includeArchived);
                if (!File.Exists(path)) continue;
                string json = await File.ReadAllTextAsync(path);
                var parsed = JObject.Parse(json);
                if (parsed["scope"]?.Value<string>() != "cost-simulator"
                    || parsed["range"]?["key"]?.Value<string>() != range
                    || parsed["totals"] == null || parsed["projects"] is not JArray
                    || parsed["models"] is not JArray
                    || parsed["generatedAt"]?.Value<DateTime?>() is not { } generatedAt
                    || nowUtc - generatedAt.ToUniversalTime() > MaxDefaultAge
                    || generatedAt.ToUniversalTime() > nowUtc.AddMinutes(1))
                    continue;
                responses[key] = new Prepared(json, generatedAt.ToUniversalTime().Ticks);
            }
            catch (Exception ex) { log($"Cost simulator snapshot {key} could not load: {ex.Message}"); }
        }
    }

    public void Start(CancellationToken ct)
    {
        _ = Task.Run(() => RunDefaultsAsync(ct), ct);
        _ = Task.Run(() => RunCustomAsync(ct), ct);
    }

    private async Task RunDefaultsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snapshots = buildDefaults();
                foreach (var entry in snapshots)
                {
                    if (ct.IsCancellationRequested) return;
                    string[] parts = entry.Key.Split('|');
                    if (parts.Length != 2) continue;
                    string json = ProjectsRoutes.Json(entry.Value);
                    try { await PublishFileAsync(PathFor(parts[0], parts[1] == "all"), json, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception ex) { log($"Cost simulator snapshot {entry.Key} could not persist: {ex.Message}"); }
                    responses[entry.Key] = new Prepared(json, entry.Value.GeneratedAt.ToUniversalTime().Ticks);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log($"Cost simulator default refresh failed: {ex.Message}"); }

            try { await defaultWake.WaitAsync(TimeSpan.FromSeconds(20), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    private async Task RunCustomAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in customBuilds.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var snapshot = buildCustom(job.Range, job.IncludeArchived);
                    responses[job.Key] = new Prepared(
                        ProjectsRoutes.Json(snapshot), snapshot.GeneratedAt.ToUniversalTime().Ticks);
                    TrimCustom();
                }
                catch (Exception ex) { log($"Cost simulator custom refresh failed: {ex.Message}"); }
                finally { customQueued.TryRemove(job.Key, out _); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private bool QueueCustom(string key, AnalyticsRange range, bool includeArchived)
    {
        if (customQueued.ContainsKey(key)) return true;
        if (!customQueued.TryAdd(key, 0)) return true;
        if (customBuilds.Writer.TryWrite(new CustomBuild(key, range, includeArchived))) return true;
        customQueued.TryRemove(key, out _);
        return false;
    }

    private void WakeDefault()
    {
        try { defaultWake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void TrimCustom()
    {
        var custom = responses.Where(entry => entry.Key.StartsWith("custom|", StringComparison.Ordinal))
            .OrderBy(entry => entry.Value.AsOfTicks).ToArray();
        foreach (var entry in custom.Take(Math.Max(0, custom.Length - MaxPreparedCustom)))
            responses.TryRemove(entry.Key, out _);
    }

    private string PathFor(string range, bool includeArchived)
        => Path.Combine(directory, "cost-simulator-" + range + "-"
            + (includeArchived ? "all" : "active") + ".json");

    private static async Task PublishFileAsync(string path, string json, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
