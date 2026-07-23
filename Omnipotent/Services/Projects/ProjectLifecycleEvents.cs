using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Projects;

/// <summary>
/// Canonical payload and backward-compatible reader for durable project lifecycle transitions.
/// The event type remains compatible with the existing timeline; the payload makes the status
/// history unambiguous for analytics and future consumers.
/// </summary>
public static class ProjectLifecycleEvents
{
    public const string PayloadKind = "project-status-transition";
    public const int SchemaVersion = 1;

    public static string Payload(
        ProjectStatus fromStatus,
        ProjectStatus toStatus,
        string reason)
        => JsonConvert.SerializeObject(new
        {
            schemaVersion = SchemaVersion,
            kind = PayloadKind,
            fromStatus = fromStatus.ToString(),
            toStatus = toStatus.ToString(),
            reason,
        });

    internal static bool TryReadToStatus(ProjectEvent evt, out ProjectStatus status)
    {
        bool lifecycleCapableType =
            string.Equals(evt.Type, ProjectEventTypes.Status, StringComparison.OrdinalIgnoreCase)
            || string.Equals(evt.Type, ProjectEventTypes.BudgetPaused, StringComparison.OrdinalIgnoreCase)
            || string.Equals(evt.Type, ProjectEventTypes.ProjectBlocked, StringComparison.OrdinalIgnoreCase)
            || string.Equals(evt.Type, ProjectEventTypes.ProjectUnblocked, StringComparison.OrdinalIgnoreCase)
            || string.Equals(evt.Type, ProjectEventTypes.KlivesMessage, StringComparison.OrdinalIgnoreCase);
        if (!lifecycleCapableType)
        {
            status = default;
            return false;
        }

        if (TryReadStructuredStatus(evt.PayloadJson, out status))
            return true;

        if (string.Equals(evt.Type, ProjectEventTypes.BudgetPaused, StringComparison.OrdinalIgnoreCase))
        {
            status = ProjectStatus.BudgetPaused;
            return true;
        }
        if (string.Equals(evt.Type, ProjectEventTypes.ProjectBlocked, StringComparison.OrdinalIgnoreCase))
        {
            status = ProjectStatus.Blocked;
            return true;
        }
        if (string.Equals(evt.Type, ProjectEventTypes.ProjectUnblocked, StringComparison.OrdinalIgnoreCase)
            && IsTrustedLifecycleAuthor(evt.Author))
        {
            // Old unblocked events did not record whether the plan gate was still pending. Both
            // possible targets (Active/Planning) are runnable, which is what availability needs.
            status = ProjectStatus.Active;
            return true;
        }

        string text = (evt.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            status = default;
            return false;
        }

        string lower = text.ToLowerInvariant();
        bool trustedStatusEvent =
            string.Equals(evt.Type, ProjectEventTypes.Status, StringComparison.OrdinalIgnoreCase)
            && IsTrustedLifecycleAuthor(evt.Author);
        bool trustedBudgetResume =
            string.Equals(evt.Type, ProjectEventTypes.KlivesMessage, StringComparison.OrdinalIgnoreCase)
            && string.Equals(evt.Author, "klives", StringComparison.OrdinalIgnoreCase)
            && lower.Contains("project resumed from budget-pause", StringComparison.Ordinal);
        if (!trustedStatusEvent && !trustedBudgetResume)
        {
            status = default;
            return false;
        }

        if (lower.StartsWith("project paused by klives", StringComparison.Ordinal)
            || lower.StartsWith("project halted by klives", StringComparison.Ordinal))
        {
            status = ProjectStatus.Paused;
            return true;
        }
        if (lower.StartsWith("project unshelved by klives", StringComparison.Ordinal))
        {
            status = ProjectStatus.Paused;
            return true;
        }
        if (lower.StartsWith("project archived", StringComparison.Ordinal))
        {
            status = ProjectStatus.Archived;
            return true;
        }
        if (lower.StartsWith("project completed", StringComparison.Ordinal))
        {
            status = ProjectStatus.Completed;
            return true;
        }
        if (lower.StartsWith("project resumed", StringComparison.Ordinal)
            || trustedBudgetResume)
        {
            status = ProjectStatus.Active;
            return true;
        }
        if (lower.StartsWith("grand plan approved by klives", StringComparison.Ordinal)
            && lower.Contains("now active", StringComparison.Ordinal))
        {
            status = ProjectStatus.Active;
            return true;
        }
        if (lower.StartsWith("project initialised.", StringComparison.Ordinal))
        {
            // Older records predate the Planning state. Active and Planning are both runnable.
            status = ProjectStatus.Active;
            return true;
        }
        if (lower.StartsWith("project unhalted by klives", StringComparison.Ordinal))
        {
            foreach (ProjectStatus candidate in Enum.GetValues<ProjectStatus>())
            {
                string name = candidate.ToString().ToLowerInvariant();
                if (lower.Contains($"resumed to {name}", StringComparison.Ordinal)
                    || lower.Contains($"restored to {name}", StringComparison.Ordinal))
                {
                    status = candidate;
                    return true;
                }
            }
        }

        status = default;
        return false;
    }

    private static bool TryReadStructuredStatus(string? payloadJson, out ProjectStatus status)
    {
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                var payload = JObject.Parse(payloadJson);
                string? kind = payload
                    .GetValue("kind", StringComparison.OrdinalIgnoreCase)?
                    .ToString();
                if (!string.Equals(kind, PayloadKind, StringComparison.Ordinal))
                {
                    status = default;
                    return false;
                }

                JToken? token = payload.GetValue("toStatus", StringComparison.OrdinalIgnoreCase);
                if (token != null && TryParseStatus(token.ToString(), out status))
                    return true;
            }
            catch
            {
                // Historical payloads are not guaranteed to be JSON; fall through to text.
            }
        }

        status = default;
        return false;
    }

    private static bool IsTrustedLifecycleAuthor(string? author)
        => string.Equals(author, "klives", StringComparison.OrdinalIgnoreCase)
            || string.Equals(author, "system", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseStatus(string? raw, out ProjectStatus status)
    {
        if (Enum.TryParse(raw, ignoreCase: true, out status)
            && Enum.IsDefined(status))
            return true;

        string normalized = new((raw ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .ToArray());
        foreach (ProjectStatus candidate in Enum.GetValues<ProjectStatus>())
        {
            string candidateName = new(candidate.ToString()
                .Where(char.IsLetterOrDigit)
                .ToArray());
            if (string.Equals(normalized, candidateName, StringComparison.OrdinalIgnoreCase))
            {
                status = candidate;
                return true;
            }
        }

        status = default;
        return false;
    }
}
