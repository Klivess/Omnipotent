using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.Projects;

/// <summary>Machine-owned progress classification for renewable work slices. Continuation is
/// earned by a novel successful observation/action or a typed durable progress event, never by
/// merely spending turns or varying failed calls.</summary>
public static class ProjectWorkProgress
{
    private static readonly string[] FailureMarkers =
    {
        "tool_argument_error", "unsupported", "unavailable", "failed:", " failure", " error:",
        "timed out", "permission denied", "not found", "no such", "requires approval", "blocked:",
        "provide '", "provide the ", "can't run", "cannot run", "not available", "is not running",
        "nothing exists", "no file at", "no message with id", "no verification code", "planning phase",
        "no output", "not ready", "not sent", "no approved", "does not exist", "unknown tool",
        "already open", "prohibited", "duplicate council blocked", "limit for this wake reached",
        "daily council limit reached", "was not recorded", "was not sent",
        "desktop_interaction_required", "tool_cancelled", "tool_execution_failed", "work_slice_rollover",
        ProjectToolCallJournal.InterruptedResultPrefix,
    };

    public static bool IsProductiveResult(string resultText, IReadOnlyCollection<string>? artifactIDs = null)
    {
        if (artifactIDs?.Count > 0) return true;
        if (string.IsNullOrWhiteSpace(resultText)) return false;
        string text = resultText.Trim();
        return !FailureMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public static string Fingerprint(string toolName, string normalizedArgumentsJson, string resultText,
        IReadOnlyCollection<string>? artifactIDs = null)
    {
        string material = toolName + "\n" + normalizedArgumentsJson + "\n" + resultText.Trim() + "\n" +
            string.Join(",", artifactIDs ?? Array.Empty<string>());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    public static bool RecordIfNovel(ProjectRuntimeStateStore runtime, string projectID, string actorID,
        string toolName, string normalizedArgumentsJson, CommanderToolResult result)
    {
        if (!result.Succeeded) return false;
        if (!IsProductiveResult(result.ResultText, result.ArtifactIDs)) return false;
        string fingerprint = Fingerprint(toolName, normalizedArgumentsJson, result.ResultText, result.ArtifactIDs);
        var action = new ProjectActionCheckpoint
        {
            ActionID = fingerprint,
            Kind = "tool-result",
            Summary = $"{actorID} completed {toolName}: {Clip(result.ResultText, 800)}",
            ToolName = toolName,
            Fingerprint = fingerprint,
            RecordedBy = actorID,
            Evidence = result.ArtifactIDs.Select(id => new ProjectEvidenceReference
            {
                Kind = ProjectEvidenceKind.Artifact,
                Reference = id,
            }).ToList(),
        };
        return runtime.TryRecordNovelSuccessfulAction(projectID, actorID, action).Applied;
    }

    private static string Clip(string text, int max) =>
        string.IsNullOrWhiteSpace(text) ? "(successful result contained no text)" :
        text.Length <= max ? text.Trim() : text[..max].TrimEnd() + "…";
}
