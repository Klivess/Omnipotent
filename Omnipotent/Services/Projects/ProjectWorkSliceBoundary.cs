namespace Omnipotent.Services.Projects;

/// <summary>
/// Describes why a renewable model context should roll over. A boundary is evaluated after a
/// model response, but it never invalidates tool calls already returned by that response: those
/// calls belong to the current, fully observed turn and must be executed and journalled before
/// the session is closed.
/// </summary>
public static class ProjectWorkSliceBoundary
{
    /// <summary>A work-slice resume is consumed by any wake that gets past the boundary. Its
    /// outcome does not matter: retaining it after a deferred/failed/cancelled wake injects stale
    /// rollover instructions into the next seed.</summary>
    public static bool ShouldClearConsumedResume(bool endedAtWorkSlice, ProjectResumeAction? resume) =>
        !endedAtWorkSlice && resume?.Kind == "work-slice";

    /// <summary>Whether a configured tool-call boundary has been reached. A zero limit explicitly
    /// disables this boundary; it must never be interpreted as "zero calls allowed".</summary>
    public static bool IsToolCallLimitReached(int toolCalls, int toolCallLimit) =>
        toolCallLimit > 0 && toolCalls >= toolCallLimit;

    /// <summary>Whether the next model request is the last one permitted by a configured
    /// model-turn boundary. A zero limit explicitly disables this boundary.</summary>
    public static bool IsFinalModelTurn(int modelTurns, int modelTurnLimit) =>
        modelTurnLimit > 0 && modelTurns >= modelTurnLimit - 1;

    /// <summary>Provider prompt usage is the whole current request, so the live context is this
    /// turn's prompt plus completion. It must never be accumulated across turns (that is billing
    /// usage, not resident context).</summary>
    public static long MeasureLiveContext(int promptTokens, int completionTokens) =>
        Math.Max(0L, promptTokens) + Math.Max(0L, completionTokens);

    public static string? Describe(int toolCalls, int toolCallLimit, int modelTurns, int modelTurnLimit,
        long consumedTokens, long tokenLimit)
    {
        var reasons = new List<string>(3);
        if (IsToolCallLimitReached(toolCalls, toolCallLimit))
            reasons.Add($"tool-call boundary reached ({toolCalls}/{toolCallLimit})");
        if (modelTurnLimit > 0 && modelTurns >= modelTurnLimit)
            reasons.Add($"model-turn boundary reached ({modelTurns}/{modelTurnLimit})");
        if (tokenLimit > 0 && consumedTokens >= tokenLimit)
            reasons.Add($"token boundary reached ({consumedTokens}/{tokenLimit})");
        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }

    public static string CompletedBatchMessage(string reason, int completedCalls) =>
        $"WORK_SLICE_ROLLOVER: {reason}. The complete returned tool batch ({completedCalls} " +
        $"call{(completedCalls == 1 ? "" : "s")}) was executed and every result was committed before rollover.";

    public static string ResumeSummary(string reason, IEnumerable<string?> toolNames,
        string? lastToolName = null, string? lastResult = null, string? modelStatus = null)
    {
        string batch = string.Join(", ", toolNames.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        var parts = new List<string>
        {
            "Review the committed results from the completed tool batch" +
            (batch.Length == 0 ? "" : $" ({batch})") +
            $" and continue from current external state. Previous context ended because {reason}."
        };
        if (!string.IsNullOrWhiteSpace(modelStatus))
            parts.Add("Agent's last stated status/plan: " + Clip(modelStatus, 1200));
        if (!string.IsNullOrWhiteSpace(lastResult))
            parts.Add($"Latest committed result{(string.IsNullOrWhiteSpace(lastToolName) ? "" : " from " + lastToolName)}: " +
                Clip(lastResult, 1600));
        return string.Join(" ", parts);
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text.Trim() : text[..max].TrimEnd() + "…";
}
