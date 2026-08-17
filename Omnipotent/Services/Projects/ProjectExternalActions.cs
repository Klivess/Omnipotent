using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// The ledger of things a project has actually done in the outside world: accounts created,
    /// emails sent, forms submitted, content published, money moved.
    ///
    /// It exists because a project's own prose is not evidence. A sub-agent once reported twelve
    /// emails as "SENT ✅" thirteen seconds before the send attempt that returned 0/4, and nothing
    /// in the system could contradict it. Entries here are written from confirmed tool outcomes or
    /// declared with evidence, they survive compaction, and they are seeded into later wakes so a
    /// project can answer "have I already done this?" without re-deriving it — or re-doing it.
    /// </summary>
    public static class ProjectExternalActions
    {
        public static readonly IReadOnlyList<string> Kinds = new[]
        {
            "account_created", "email_sent", "form_submitted", "application_submitted",
            "content_published", "message_posted", "purchase_made", "listing_created",
            "api_key_obtained", "other",
        };

        public static string NormalizeKind(string? kind)
        {
            string value = (kind ?? "").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
            return Kinds.Contains(value) ? value : "other";
        }

        /// <summary>Writes one confirmed external action. `evidence` must describe what proves it —
        /// a tool result, a message id, a visible confirmation page.</summary>
        public static void Record(ProjectEventLogStore eventLog, string projectID, string? wakeID,
            string? agentID, string author, string kind, string target, string summary, string evidence)
        {
            if (eventLog == null || string.IsNullOrWhiteSpace(projectID)) return;
            string normalized = NormalizeKind(kind);
            eventLog.Append(new ProjectEvent
            {
                ProjectID = projectID,
                WakeID = wakeID,
                AgentID = agentID,
                Type = ProjectEventTypes.ExternalAction,
                Author = string.IsNullOrWhiteSpace(author) ? "system" : author,
                Text = Compose(normalized, target, summary, evidence),
            });
        }

        internal static string Compose(string kind, string target, string summary, string evidence)
        {
            var builder = new StringBuilder(kind);
            if (!string.IsNullOrWhiteSpace(target)) builder.Append(" @ ").Append(Trim(target, 120));
            if (!string.IsNullOrWhiteSpace(summary)) builder.Append(" — ").Append(Trim(summary, 300));
            if (!string.IsNullOrWhiteSpace(evidence)) builder.Append(" [evidence: ").Append(Trim(evidence, 240)).Append(']');
            return builder.ToString();
        }

        /// <summary>
        /// The ledger as wake context. Deliberately compact and newest-last so it reads as history.
        /// </summary>
        public static string DescribeForPrompt(ProjectEventLogStore eventLog, string projectID, int maxEntries = 20)
        {
            if (eventLog == null || string.IsNullOrWhiteSpace(projectID)) return "";
            List<ProjectEvent> entries;
            try
            {
                entries = eventLog.EnumerateRange(projectID, null, null)
                    .Where(e => e.Type == ProjectEventTypes.ExternalAction)
                    .OrderByDescending(e => e.Sequence)
                    .Take(Math.Clamp(maxEntries, 1, 60))
                    .OrderBy(e => e.Sequence)
                    .ToList();
            }
            catch { return ""; }
            if (entries.Count == 0) return "";

            var builder = new StringBuilder();
            builder.AppendLine("EXTERNAL ACTIONS ALREADY COMPLETED (the only record of what this project has really done "
                + "outside itself — do not repeat these, and never claim an external action that is not here):");
            foreach (var entry in entries)
                builder.AppendLine($"- {entry.Timestamp:yyyy-MM-dd HH:mm}Z [{entry.AgentID ?? "commander"}] {entry.Text}");
            return builder.ToString().TrimEnd();
        }

        /// <summary>True when this exact external action is already on the ledger, so a wake that
        /// lost its context does not sign up twice or send the same email again.</summary>
        public static bool AlreadyRecorded(ProjectEventLogStore eventLog, string projectID, string kind, string target)
        {
            if (eventLog == null || string.IsNullOrWhiteSpace(projectID) || string.IsNullOrWhiteSpace(target))
                return false;
            string normalized = NormalizeKind(kind);
            string needle = Trim(target, 120);
            try
            {
                return eventLog.EnumerateRange(projectID, null, null)
                    .Where(e => e.Type == ProjectEventTypes.ExternalAction)
                    .Any(e => e.Text.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                        && e.Text.Contains(needle, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        private static string Trim(string value, int max)
        {
            string flat = (value ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            return flat.Length <= max ? flat : flat[..max] + "…";
        }
    }
}
