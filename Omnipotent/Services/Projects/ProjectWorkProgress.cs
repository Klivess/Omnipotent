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
        if (!IsProductiveResult(result.ResultText, result.ArtifactIDs)) return false;
        string fingerprint = Fingerprint(toolName, normalizedArgumentsJson, result.ResultText, result.ArtifactIDs);
        var checkpoint = runtime.Get(projectID).Checkpoint;
        var previous = actorID == "commander" ? checkpoint.LastSuccessfulAction
            : checkpoint.AgentLastSuccessfulActions.GetValueOrDefault(actorID);
        if (string.Equals(previous?.Fingerprint, fingerprint, StringComparison.Ordinal)) return false;
        var action = new ProjectActionCheckpoint
        {
            ActionID = fingerprint,
            Kind = "tool-result",
            Summary = $"{actorID} produced a novel successful result from {toolName}.",
            ToolName = toolName,
            Fingerprint = fingerprint,
            RecordedBy = actorID,
            Evidence = result.ArtifactIDs.Select(id => new ProjectEvidenceReference
            {
                Kind = ProjectEvidenceKind.Artifact,
                Reference = id,
            }).ToList(),
        };
        if (actorID == "commander") runtime.SetLastSuccessfulAction(projectID, action);
        else runtime.SetAgentLastSuccessfulAction(projectID, actorID, action);
        return true;
    }
}
