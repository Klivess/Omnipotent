using Newtonsoft.Json;

namespace Omnipotent.Services.Projects;

/// <summary>
/// Produces a compact, user-visible accounting record for every agent wake. This is deliberately
/// separate from prose status messages: it explains whether the model actually returned native
/// tool calls, whether dispatch ran, and which harness boundary ended the wake.
/// </summary>
public static class ProjectWakeDiagnostics
{
    public static ProjectEvent Create(
        string projectID, string wakeID, string agentID, string outcome,
        DateTime startedAtUtc, int modelTurns, int modelToolCalls, int dispatchedToolCalls,
        int productiveActions, int emptyResponses, int loopTrips, bool endedAtWorkSlice,
        string? initialModel, string? finalModel, int toolCallLimit, int modelTurnLimit,
        long liveContextTokens, int tokenLimit, string? lastToolName,
        long promptTokens = 0, long completionTokens = 0, double costUsd = 0,
        string costBasis = "unknown")
    {
        long elapsedMs = Math.Max(0, (long)(DateTime.UtcNow - startedAtUtc).TotalMilliseconds);
        string route = string.IsNullOrWhiteSpace(finalModel) ? (initialModel ?? "unknown") : finalModel;
        string toolBoundary = toolCallLimit <= 0 ? "disabled" : toolCallLimit.ToString();
        string turnBoundary = modelTurnLimit <= 0 ? "disabled" : modelTurnLimit.ToString();
        string signal = modelTurns == 0
            ? "no model response was received"
            : modelToolCalls == 0
                ? "model returned prose/no native tool calls"
                : dispatchedToolCalls == 0
                    ? "model tool calls were rejected before dispatch"
                    : "tool dispatch ran";

        return new ProjectEvent
        {
            ProjectID = projectID,
            WakeID = wakeID,
            AgentID = agentID,
            Type = ProjectEventTypes.WakeDiagnostic,
            Author = "system",
            Text = $"Wake diagnostic: outcome={outcome}; {signal}; model turns={modelTurns}; " +
                $"model tool calls={modelToolCalls}; dispatched={dispatchedToolCalls}; productive={productiveActions}; " +
                $"empty responses={emptyResponses}; loop trips={loopTrips}; elapsed={elapsedMs}ms; route={route}; " +
                $"slice limits: tools={toolBoundary}, turns={turnBoundary}, context={liveContextTokens}/{tokenLimit}; " +
                $"last tool={lastToolName ?? "none"}.",
            PayloadJson = JsonConvert.SerializeObject(new
            {
                schemaVersion = 2,
                outcome,
                elapsedMs,
                modelTurns,
                modelToolCalls,
                dispatchedToolCalls,
                productiveActions,
                emptyResponses,
                loopTrips,
                endedAtWorkSlice,
                initialModel,
                finalModel,
                limits = new { toolCallLimit, modelTurnLimit, tokenLimit },
                liveContextTokens,
                lastToolName,
                promptTokens,
                completionTokens,
                totalTokens = Math.Max(0, promptTokens) + Math.Max(0, completionTokens),
                costUsd = Math.Max(0, costUsd),
                costBasis,
            }),
        };
    }
}
