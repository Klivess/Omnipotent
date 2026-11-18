using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Demiurge;

/// <summary>
/// Canonical payload and backward-compatible reader for durable Steward lifecycle transitions.
/// The event type remains compatible with the existing timeline; the payload makes the status
/// history unambiguous for analytics and future consumers.
/// </summary>
public static class StewardLifecycleEvents
{
    public const string PayloadKind = "steward-status-transition";
    /// <summary>The kind payloads were written with before Projects became Demiurge.</summary>
    public const string LegacyPayloadKind = "project-status-transition";
    public const int SchemaVersion = 1;

    private static readonly System.Text.RegularExpressions.Regex LegacyNoun =
        new(@"\bproject\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string Payload(
        StewardStatus fromStatus,
        StewardStatus toStatus,
        string reason)
        => JsonConvert.SerializeObject(new
        {
            schemaVersion = SchemaVersion,
            kind = PayloadKind,
            fromStatus = fromStatus.ToString(),
            toStatus = toStatus.ToString(),
            reason,
        });

    internal static bool TryReadToStatus(StewardEvent evt, out StewardStatus status)
    {
        if (!IsLifecycleCapableType(evt.Type))
        {
            status = default;
            return false;
        }

        if (TryReadStructuredStatus(evt.PayloadJson, out status))
            return true;

        if (string.Equals(evt.Type, StewardEventTypes.BudgetPaused, StringComparison.OrdinalIgnoreCase))
        {
            status = StewardStatus.BudgetPaused;
            return true;
        }
        if (string.Equals(evt.Type, StewardEventTypes.StewardBlocked, StringComparison.OrdinalIgnoreCase))
        {
            status = StewardStatus.Blocked;
            return true;
        }
        if (string.Equals(evt.Type, StewardEventTypes.StewardUnblocked, StringComparison.OrdinalIgnoreCase)
            && IsTrustedLifecycleAuthor(evt.Author))
        {
            // Old unblocked events did not record whether the plan gate was still pending. Both
            // possible targets (Active/Planning) are runnable, which is what availability needs.
            status = StewardStatus.Active;
            return true;
        }

        string text = (evt.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            status = default;
            return false;
        }

        // Lower-cased, with the pre-rename noun read as today's: records written before Projects
        // became Demiurge say "Project paused by Klives", newer ones "Steward paused by Klives".
        string lower = LegacyNoun.Replace(text.ToLowerInvariant(), "steward");
        bool trustedStatusEvent =
            string.Equals(evt.Type, StewardEventTypes.Status, StringComparison.OrdinalIgnoreCase)
            && IsTrustedLifecycleAuthor(evt.Author);
        bool trustedBudgetResume =
            string.Equals(evt.Type, StewardEventTypes.KlivesMessage, StringComparison.OrdinalIgnoreCase)
            && string.Equals(evt.Author, "klives", StringComparison.OrdinalIgnoreCase)
            && lower.Contains("steward resumed from budget-pause", StringComparison.Ordinal);
        if (!trustedStatusEvent && !trustedBudgetResume)
        {
            status = default;
            return false;
        }

        if (lower.StartsWith("steward paused by klives", StringComparison.Ordinal)
            || lower.StartsWith("steward halted by klives", StringComparison.Ordinal))
        {
            status = StewardStatus.Paused;
            return true;
        }
        if (lower.StartsWith("steward unshelved by klives", StringComparison.Ordinal))
        {
            status = StewardStatus.Paused;
            return true;
        }
        if (lower.StartsWith("steward archived", StringComparison.Ordinal))
        {
            status = StewardStatus.Archived;
            return true;
        }
        if (lower.StartsWith("steward completed", StringComparison.Ordinal))
        {
            status = StewardStatus.Completed;
            return true;
        }
        if (lower.StartsWith("steward resumed", StringComparison.Ordinal)
            || trustedBudgetResume)
        {
            status = StewardStatus.Active;
            return true;
        }
        if (lower.StartsWith("grand plan approved by klives", StringComparison.Ordinal)
            && lower.Contains("now active", StringComparison.Ordinal))
        {
            status = StewardStatus.Active;
            return true;
        }
        if (lower.StartsWith("steward initialised.", StringComparison.Ordinal))
        {
            // Older records predate the Planning state. Active and Planning are both runnable.
            status = StewardStatus.Active;
            return true;
        }
        if (lower.StartsWith("steward unhalted by klives", StringComparison.Ordinal))
        {
            foreach (StewardStatus candidate in Enum.GetValues<StewardStatus>())
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

    /// <summary>Cheap type-only prefilter used by analytics while scanning old JSONL rows. The
    /// full payload/text reader above remains authoritative for whether a candidate is a transition.</summary>
    internal static bool IsLifecycleCapableType(string? type)
        => string.Equals(type, StewardEventTypes.Status, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, StewardEventTypes.BudgetPaused, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, StewardEventTypes.StewardBlocked, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, StewardEventTypes.StewardUnblocked, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, StewardEventTypes.KlivesMessage, StringComparison.OrdinalIgnoreCase);

    private static bool TryReadStructuredStatus(string? payloadJson, out StewardStatus status)
    {
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                var payload = JObject.Parse(payloadJson);
                string? kind = payload
                    .GetValue("kind", StringComparison.OrdinalIgnoreCase)?
                    .ToString();
                if (!string.Equals(kind, PayloadKind, StringComparison.Ordinal) && !string.Equals(kind, LegacyPayloadKind, StringComparison.Ordinal))
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

    private static bool TryParseStatus(string? raw, out StewardStatus status)
    {
        if (Enum.TryParse(raw, ignoreCase: true, out status)
            && Enum.IsDefined(status))
            return true;

        string normalized = new((raw ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .ToArray());
        foreach (StewardStatus candidate in Enum.GetValues<StewardStatus>())
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
