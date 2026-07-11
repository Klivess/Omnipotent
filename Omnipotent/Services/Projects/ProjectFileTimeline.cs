using Newtonsoft.Json;

namespace Omnipotent.Services.Projects;

/// <summary>Bridges the detailed project-file database into the existing bounded project timeline.</summary>
public static class ProjectFileTimeline
{
    public static ProjectEvent Append(
        ProjectEventLogStore eventLog,
        string projectID,
        ProjectFileActor actor,
        ProjectFileOperation operation,
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
            ProjectFileOperation.Upload => "uploaded",
            ProjectFileOperation.Write => "wrote",
            ProjectFileOperation.CreateDirectory => "created a directory",
            ProjectFileOperation.Move => "moved",
            ProjectFileOperation.Copy => "copied",
            ProjectFileOperation.Delete => "deleted",
            ProjectFileOperation.Metadata => "updated metadata for",
            ProjectFileOperation.ReconcileCreate => "discovered",
            ProjectFileOperation.ReconcileModify => "noticed an external change to",
            ProjectFileOperation.ReconcileDelete => "noticed external deletion of",
            _ => "changed",
        };
        string subject = materialized.Count switch
        {
            0 => "shared project files",
            1 => $"'{Truncate(materialized[0], 240)}'",
            _ => $"{materialized.Count} shared project files",
        };
        string size = totalBytes > 0 ? $" ({FormatBytes(totalBytes)})" : "";
        string sample = materialized.Count > 1
            ? ": " + string.Join(", ", materialized.Take(5).Select(p => Truncate(p, 120))) + (materialized.Count > 5 ? ", …" : "")
            : "";

        var evt = new ProjectEvent
        {
            ProjectID = projectID,
            WakeID = wakeID,
            AgentID = actor.Type is ProjectFileActorType.Agent or ProjectFileActorType.Commander ? actor.ID : null,
            Type = ProjectEventTypes.ProjectFileChanged,
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

    private static string Author(ProjectFileActorType type) => type switch
    {
        ProjectFileActorType.User => "klives",
        ProjectFileActorType.Commander => "commander",
        ProjectFileActorType.Agent => "agent",
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
