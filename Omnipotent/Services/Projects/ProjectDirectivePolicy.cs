using System.Text.RegularExpressions;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Small deterministic backstop for the few directive classes that map safely to a concrete
    /// tool action. Most natural-language rules are enforced through the always-injected directive
    /// block; this guard makes the common "do not use bot accounts" rule survive a model mistake.
    /// </summary>
    internal static class ProjectDirectivePolicy
    {
        public static string? FindViolation(IEnumerable<ProjectDirective> directives, string agentID, string toolName)
        {
            if (!string.Equals(toolName, "account_register", StringComparison.Ordinal)) return null;
            foreach (var rule in directives.Where(x => x.Kind == ProjectDirectiveKind.Rule &&
                x.Status != ProjectDirectiveStatus.Revoked && x.Status != ProjectDirectiveStatus.Failed &&
                ProjectDirectiveStore.AppliesTo(x, agentID)))
            {
                if (!ForbidsBotAccounts(rule.Text)) continue;
                return $"PROJECT_DIRECTIVE_VIOLATION: rule {rule.DirectiveID} says '{rule.Text}'. " +
                    "Creating a new project account is blocked because it could create/use a bot account. " +
                    "Use an explicitly approved non-bot account or ask Klives to revise the rule.";
            }
            return null;
        }

        private static bool ForbidsBotAccounts(string? text) =>
            !string.IsNullOrWhiteSpace(text) &&
            Regex.IsMatch(text,
                @"\b(?:do\s+not|don't|never|no|avoid|without)\b[^.\n]{0,80}\b(?:bot|automated)\s+accounts?\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
