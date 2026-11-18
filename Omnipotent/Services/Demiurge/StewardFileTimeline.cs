using Newtonsoft.Json;

namespace Omnipotent.Services.Demiurge;

/// <summary>Bridges the detailed steward-file database into the existing bounded Steward timeline.</summary>
public static class StewardFileTimeline
{
    public static StewardEvent Append(
        StewardEventLogStore eventLog,
        string stewardID,
        StewardFileActor actor,
        StewardFileOperation operation,
        IEnumerable<string> paths,
        long totalBytes = 0,
        string? batchID = null,
        string? previousPath = null,
        string? wakeID = null)
    {
        ArgumentNullException.ThrowIfNull(eventLog);
        var materialized = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string actorName = Truncate(string.Join(" ", (actor.DisplayName ?? "Unknown")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), 120);
        string verb = operation switch
        {
            StewardFileOperation.Upload => "uploaded",
            StewardFileOperation.Write => "wrote",
            StewardFileOperation.CreateDirectory => "created a directory",
            StewardFileOperation.Move => "moved",
            StewardFileOperation.Copy => "copied",
            StewardFileOperation.Delete => "deleted",
            StewardFileOperation.Metadata => "updated metadata for",
            StewardFileOperation.ReconcileCreate => "discovered",
            StewardFileOperation.ReconcileModify => "noticed an external change to",
            StewardFileOperation.ReconcileDelete => "noticed external deletion of",
            _ => "changed",
        };
        string subject = materialized.Count switch
        {
            0 => "shared Steward files",
            1 => $"'{Truncate(materialized[0], 240)}'",
            _ => $"{materialized.Count} shared Steward files",
        };
        string size = totalBytes > 0 ? $" ({FormatBytes(totalBytes)})" : "";
        string sample = materialized.Count > 1
            ? ": " + string.Join(", ", materialized.Take(5).Select(p => Truncate(p, 120))) + (materialized.Count > 5 ? ", …" : "")
            : "";

        var evt = new StewardEvent
        {
            StewardID = stewardID,
            WakeID = wakeID,
            AgentID = actor.Type is StewardFileActorType.Agent or StewardFileActorType.Commander ? actor.ID : null,
            Type = StewardEventTypes.StewardFileChanged,
            Author = Author(actor.Type),
            Text = $"{actorName} {verb} {subject}{size}{sample}.",
            PayloadJson = JsonConvert.SerializeObject(new
            {
                operation = operation.ToString(),
                paths = materialized.Take(25).Select(p => Truncate(p, 240)).ToList(),
                pathCount = materialized.Count,
                previousPath,
                totalBytes,
                batchID,
                actor = new { type = actor.Type.ToString(), actor.ID, displayName = actorName },
            }),
        };
        return eventLog.Append(evt);
    }

    private static string Author(StewardFileActorType type) => type switch
    {
        StewardFileActorType.User => "klives",
        StewardFileActorType.Commander => "commander",
        StewardFileActorType.Agent => "agent",
        _ => "system",
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}
