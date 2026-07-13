namespace Omnipotent.Services.Projects;

/// <summary>Builds an evidence-bounded closing status when a model omits one at a context edge.</summary>
public static class ProjectWakeStatus
{
    public static string ForCommander(ProjectDigest digest, ProjectRuntimeState runtime, string? deferredCall = null)
    {
        string state = runtime.Blocker == null ? "CONTINUE" : "BLOCKED";
        var parts = new List<string>
        {
            $"WAKE_STATUS: {state} — the model returned no closing status; this status was synthesized from durable state.",
        };
        AddShared(parts, digest, runtime, runtime.Checkpoint.LastSuccessfulAction,
            deferredCall ?? runtime.Checkpoint.ResumeAction?.Summary);
        return string.Join(" ", parts);
    }

    public static string ForAgent(ProjectDigest digest, ProjectRuntimeState runtime, string agentID,
        string? deferredCall = null)
    {
        var last = runtime.Checkpoint.AgentLastSuccessfulActions.GetValueOrDefault(agentID);
        string? resume = deferredCall
            ?? runtime.Checkpoint.AgentResumeActions.GetValueOrDefault(agentID)?.Summary;
        var parts = new List<string>
        {
            "WORK_STATUS: CONTINUE — the model returned no closing summary; this status was synthesized from durable state.",
        };
        AddShared(parts, digest, runtime, last, resume);
        return string.Join(" ", parts);
    }

    private static void AddShared(List<string> parts, ProjectDigest digest, ProjectRuntimeState runtime,
        ProjectActionCheckpoint? last, string? resume)
    {
        if (!string.IsNullOrWhiteSpace(digest.CurrentFocus)) parts.Add($"Focus: {digest.CurrentFocus}.");
        if (last != null && !string.IsNullOrWhiteSpace(last.Summary)) parts.Add($"Last verified action: {last.Summary}.");
        if (runtime.Blocker != null && !string.IsNullOrWhiteSpace(runtime.Blocker.Summary))
            parts.Add($"Blocker: {runtime.Blocker.Summary}.");
        if (!string.IsNullOrWhiteSpace(resume)) parts.Add($"Exact next action: {resume}.");
        else if (digest.NextSteps.Count > 0) parts.Add($"Next recorded step: {digest.NextSteps[0]}.");
        else parts.Add("Exact next action: rehydrate the latest committed events and select the next evidence-producing action.");
    }
}
