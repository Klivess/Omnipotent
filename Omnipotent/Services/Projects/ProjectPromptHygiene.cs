namespace Omnipotent.Services.Projects;

/// <summary>
/// Keeps machine-owned wake/context bookkeeping out of model-authored project state. Harness
/// diagnostics remain in the lossless event log for Klives, but they are not project facts and
/// must never be replayed as reasons for an agent to wait.
/// </summary>
public static class ProjectPromptHygiene
{
    private static readonly string[] ContextBookkeepingMarkers =
    {
        "slice limits",
        "slice limit reset",
        "tools=disabled",
        "turns=disabled",
        "work_slice_rollover",
        "context work slice complete",
        "automatic context rollover",
        "previous context ended because",
        "context rollover will not",
    };

    /// <summary>Klives' own words always survive. Machine diagnostics and model-authored state
    /// contaminated by context-bookkeeping terminology do not return to an agent prompt.</summary>
    public static bool IsAgentVisibleEvent(ProjectEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.Equals(evt.Author, "klives", StringComparison.OrdinalIgnoreCase)
            || evt.Type == ProjectEventTypes.KlivesMessage)
            return true;
        if (evt.Type is ProjectEventTypes.WakeDiagnostic or ProjectEventTypes.DigestRebuilt)
            return false;
        return !ContainsContextBookkeeping(evt.Text);
    }

    public static bool IsAgentVisibleRetrievalHit(ProjectRetrievalIndex.RetrievalHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (hit.Type == ProjectEventTypes.KlivesMessage) return true;
        if (hit.Type is ProjectEventTypes.WakeDiagnostic or ProjectEventTypes.DigestRebuilt)
            return false;
        return !ContainsContextBookkeeping(hit.Snippet);
    }

    public static bool ContainsContextBookkeeping(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && ContextBookkeepingMarkers.Any(marker =>
            text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes contaminated lines from old digests/runtime summaries. A single-line field
    /// that contains a false wait condition is intentionally omitted and reconstructed from the
    /// Grand Plan, typed state and verified events.</summary>
    public static string ScrubState(string? text, string fallback = "(none)")
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        string clean = string.Join("\n", text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line => !ContainsContextBookkeeping(line)))
            .Trim();
        return clean.Length == 0 ? fallback : clean;
    }

    /// <summary>Old persisted rollover triggers can outlive a deployment. Convert them into the
    /// only instruction the model needs instead of reintroducing harness terminology.</summary>
    public static string ScrubTrigger(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no trigger text)";
        return ContainsContextBookkeeping(text)
            ? "Continue the active assignment now from the durable execution state and latest verified external result."
            : text;
    }

    public const string CapabilityTruth =
        "Every tool offered in this request is available now. Internal context renewal is automatic " +
        "and is never a project dependency or a condition to wait for. Ignore any historical plan or " +
        "status claiming that a runtime reset is required; take the next executable step toward the goal.";
}
