using System.Collections.Concurrent;
using System.Globalization;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects;

/// <summary>
/// Publishes complete, immutable overview responses. The route only reads this table;
/// store walks, historical joins, serialization and disk writes run on one background task.
/// </summary>
public sealed class ProjectOverviewMaterializer
{
    private static readonly string[] Ranges = ["1h", "24h", "7d"];
    private readonly ProjectOverviewService overview;
    private readonly Action<string> log;
    private sealed record Response(string Json, DateTime GeneratedUtc);
    private readonly ConcurrentDictionary<string, Response> responses = new(StringComparer.Ordinal);
    private readonly string directory;

    public ProjectOverviewMaterializer(ProjectOverviewService overview, Action<string>? log = null,
        string? snapshotDirectory = null)
    {
        this.overview = overview;
        this.log = log ?? (_ => { });
        directory = snapshotDirectory ?? Path.Combine(
            OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsDirectory), "RouteSnapshots");
    }

    public string? Get(string range)
    {
        if (!Ranges.Contains(range, StringComparer.Ordinal))
            throw new ArgumentException("Choose range 1h, 24h or 7d.");
        if (!responses.TryGetValue(range, out Response? response)) return null;
        return IsFresh(response.GeneratedUtc) ? response.Json : null;
    }

    public async Task LoadAsync()
    {
        foreach (string range in Ranges)
        {
            try
            {
                string path = PathFor(range);
                if (!File.Exists(path)) continue;
                string json = await File.ReadAllTextAsync(path);
                var parsed = JObject.Parse(json);
                if (parsed["range"]?["key"]?.Value<string>() == range
                    && parsed["projects"] is JArray && parsed["series"] is JArray
                    && parsed["execution"] != null
                    && TryReadLiveAt(parsed, out DateTime liveAt)
                    && IsFresh(liveAt))
                    responses[range] = new Response(json, liveAt);
            }
            catch (Exception ex) { log($"Projects overview snapshot {range} could not load: {ex.Message}"); }
        }
    }

    public void Start(CancellationToken ct)
        => _ = Task.Run(() => RunAsync(ct), ct);

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (string range in Ranges)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    // Existing overview logic can still queue historical builds, but it is never
                    // called by a request. Later passes publish the completed historical joins.
                    string json = ProjectsRoutes.Json(overview.Get(range));
                    var parsed = JObject.Parse(json);
                    if (!TryReadLiveAt(parsed, out DateTime liveAt))
                        throw new InvalidDataException("Overview response has no liveAt timestamp.");
                    Directory.CreateDirectory(directory);
                    string path = PathFor(range);
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
                    responses[range] = new Response(json, liveAt);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { log($"Projects overview snapshot {range} could not refresh: {ex.Message}"); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    private string PathFor(string range) => Path.Combine(directory, $"overview-{range}.json");

    private static bool IsFresh(DateTime liveAt)
    {
        TimeSpan age = DateTime.UtcNow - liveAt;
        return age >= TimeSpan.FromMinutes(-1) && age <= TimeSpan.FromMinutes(2);
    }

    private static bool TryReadLiveAt(JObject body, out DateTime utc)
    {
        utc = default;
        string? text = body["liveAt"]?.Value<string>();
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
            return false;
        utc = parsed.UtcDateTime;
        return true;
    }
}
