using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Demiurge;

/// <summary>Prepares the fleet list off the HTTP path and preserves the last good response across restarts.</summary>
public sealed class StewardListMaterializer
{
    private sealed record Response(string Json, DateTime AsOfUtc);
    private sealed class SavedSnapshot
    {
        public DateTime AsOfUtc { get; set; }
        public string Json { get; set; } = "";
    }
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);
    private readonly Func<string> build;
    private readonly Action<string> log;
    private readonly string path;
    private Response? response;

    public StewardListMaterializer(Func<string> build, Action<string>? log = null,
        string? snapshotDirectory = null)
    {
        this.build = build;
        this.log = log ?? (_ => { });
        string directory = snapshotDirectory ?? Path.Combine(
            OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeDirectory), "RouteSnapshots");
        path = Path.Combine(directory, "list.json");
    }

    public string? Get()
    {
        Response? current = Volatile.Read(ref response);
        return current != null && IsFresh(current.AsOfUtc, DateTime.UtcNow) ? current.Json : null;
    }

    private static bool IsFresh(DateTime asOfUtc, DateTime nowUtc)
    {
        TimeSpan age = nowUtc - asOfUtc;
        return age >= TimeSpan.FromMinutes(-1) && age <= MaxAge;
    }

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(path)) return;
            SavedSnapshot? saved = JsonConvert.DeserializeObject<SavedSnapshot>(await File.ReadAllTextAsync(path));
            // A snapshot saved before Projects became Demiurge names its rows' id "projectID", which
            // today's page cannot read; leave it for the first build to replace.
            if (saved != null && IsFresh(saved.AsOfUtc.ToUniversalTime(), DateTime.UtcNow)
                && JToken.Parse(saved.Json) is JArray rows && (rows.Count == 0 || rows[0]?["stewardID"] != null))
                Volatile.Write(ref response, new Response(saved.Json, saved.AsOfUtc.ToUniversalTime()));
        }
        catch (Exception ex) { log($"Demiurge list snapshot could not load: {ex.Message}"); }
    }

    public void Start(CancellationToken ct) => _ = Task.Run(() => RunAsync(ct), ct);

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                DateTime observedFromUtc = DateTime.UtcNow;
                string json = build();
                if (JToken.Parse(json) is not JArray)
                    throw new InvalidDataException("Demiurge list snapshot must be a JSON array.");
                // The in-memory response remains usable even if persistence is temporarily down.
                Volatile.Write(ref response, new Response(json, observedFromUtc));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temporary, JsonConvert.SerializeObject(
                        new SavedSnapshot { AsOfUtc = observedFromUtc, Json = json }), ct);
                    File.Move(temporary, path, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log($"Demiurge list snapshot could not refresh: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
}
