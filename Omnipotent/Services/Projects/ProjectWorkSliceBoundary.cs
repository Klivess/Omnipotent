namespace Omnipotent.Services.Projects;

/// <summary>
/// Describes why a renewable model context should roll over. A boundary is evaluated after a
/// model response, but it never invalidates tool calls already returned by that response: those
/// calls belong to the current, fully observed turn and must be executed and journalled before
/// the session is closed.
/// </summary>
public static class ProjectWorkSliceBoundary
{
    public static string? Describe(int toolCalls, int toolCallLimit, int modelTurns, int modelTurnLimit,
        long consumedTokens, long tokenLimit)
    {
        var reasons = new List<string>(3);
        if (toolCalls >= toolCallLimit)
            reasons.Add($"tool-call boundary reached ({toolCalls}/{toolCallLimit})");
        if (modelTurns >= modelTurnLimit)
            reasons.Add($"model-turn boundary reached ({modelTurns}/{modelTurnLimit})");
        if (consumedTokens >= tokenLimit)
            reasons.Add($"token boundary reached ({consumedTokens}/{tokenLimit})");
        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }

    public static string CompletedBatchMessage(string reason, int completedCalls) =>
        $"WORK_SLICE_ROLLOVER: {reason}. The complete returned tool batch ({completedCalls} " +
        $"call{(completedCalls == 1 ? "" : "s")}) was executed and every result was committed before rollover.";

    public static string ResumeSummary(string reason, IEnumerable<string?> toolNames)
    {
        string batch = string.Join(", ", toolNames.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        return $"Review the committed results from the completed tool batch" +
               (batch.Length == 0 ? "" : $" ({batch})") +
               $" and continue from current external state. Previous context ended because {reason}.";
    }
}
