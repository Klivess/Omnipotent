namespace Omnipotent.Services.Projects;

/// <summary>
/// Repairs the only ambiguous state an append-only tool transcript can contain after a hard
/// process stop: a committed tool-call without a committed tool-result. Recovery never guesses
/// whether an external side effect happened; it records that uncertainty and requires inspection
/// before retrying.
/// </summary>
public static class ProjectToolCallJournal
{
    public const string InterruptedResultPrefix = "TOOL_INTERRUPTED_BY_RESTART";

    public static int ReconcileInterruptedWake(ProjectEventLogStore eventLog, string projectID,
        string? wakeID, string? agentID)
    {
        if (string.IsNullOrWhiteSpace(wakeID)) return 0;
        var events = eventLog.EnumerateRange(projectID, null, null)
            .Where(e => string.Equals(e.WakeID, wakeID, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(agentID)
                    || string.Equals(e.AgentID, agentID, StringComparison.Ordinal)))
            .ToList();
        var completed = events
            .Where(e => e.Type == ProjectEventTypes.ToolResult && !string.IsNullOrWhiteSpace(e.ToolCallId))
            .Select(e => e.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);
        int repaired = 0;
        foreach (var call in events.Where(e => e.Type == ProjectEventTypes.ToolCall
                     && !string.IsNullOrWhiteSpace(e.ToolCallId))
                 .GroupBy(e => e.ToolCallId!, StringComparer.Ordinal)
                 .Select(g => g.OrderBy(e => e.Sequence).Last()))
        {
            if (completed.Contains(call.ToolCallId!)) continue;
            eventLog.Append(new ProjectEvent
            {
                ProjectID = projectID,
                WakeID = wakeID,
                AgentID = call.AgentID ?? agentID,
                Type = ProjectEventTypes.ToolResult,
                Author = "system",
                ToolName = call.ToolName,
                ToolCallId = call.ToolCallId,
                PayloadJson = "{\"succeeded\":false}",
                Text = $"{InterruptedResultPrefix}: no result was durably committed for {call.ToolName ?? "the tool"}; " +
                       "its external outcome is unknown. Inspect the website, desktop, files, or account registry before retrying the action.",
            });
            repaired++;
        }
        return repaired;
    }
}
