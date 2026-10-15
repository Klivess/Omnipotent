using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Klives' hard limits for one project — the "never" and "ask first" columns of what an agent may do
    /// on its own. The escalation bar in the prompt covers judgement calls; these are switches enforced at
    /// tool dispatch, so they hold even when a model talks itself past the prompt:
    ///   • blocked sites: navigation, fetches and HTTP calls to them are refused outright;
    ///   • ask-first sites: the first use opens an approval card, and an approval lasts a day;
    ///   • host shell/scripts, outbound email and new account registration can each be switched off.
    /// Defaults change nothing, so existing projects behave exactly as before until Klives sets a limit.
    /// </summary>
    public static class ProjectAutonomyPolicy
    {
        /// <summary>Tools that run code on Omnipotent's own host rather than in the agent's container.</summary>
        public static readonly IReadOnlySet<string> HostShellTools =
            new HashSet<string>(StringComparer.Ordinal) { "run_powershell", "run_bash", "execute_csharp", "run_script" };

        /// <summary>Tools whose `url` argument takes an agent to a website.</summary>
        private static readonly IReadOnlySet<string> UrlTools =
            new HashSet<string>(StringComparer.Ordinal) { "computer_navigate", "computer_open_browser", "web_fetch", "http_request" };

        public static readonly TimeSpan AskFirstApprovalLifetime = TimeSpan.FromHours(24);

        // project|domain → approval expiry. In memory on purpose: a restart asks again, which is the safe
        // direction for a limit Klives set.
        private static readonly ConcurrentDictionary<string, DateTime> Approved = new(StringComparer.OrdinalIgnoreCase);

        internal static void ResetForTests() => Approved.Clear();

        /// <summary>
        /// The refusal to return instead of running the tool, or null when the call is within limits.
        /// <paramref name="askFirstDomain"/> is set when the call targets an ask-first site that Klives
        /// has not approved recently; the caller opens the approval card.
        /// </summary>
        public static string? FindViolation(string projectID, ProjectSettings settings, string toolName, string? argsJson,
            out string? askFirstDomain)
        {
            askFirstDomain = null;
            if (!settings.AllowHostShell && HostShellTools.Contains(toolName))
                return "AUTONOMY_POLICY: Klives has switched off host shell and script tools for this project. " +
                       "Do the work inside your own desktop (computer_terminal runs any shell there) or with the project file tools.";
            if (!settings.AllowOutboundEmail && toolName == "klivemail_send")
                return "AUTONOMY_POLICY: Klives has switched off outbound email for this project. Draft the message into /project " +
                       "and tell him it is ready (reply_to_klives); do not try another way to send it.";
            if (!settings.AllowAccountCreation && toolName == "account_register")
                return "AUTONOMY_POLICY: Klives has switched off creating new accounts for this project. Use an account already in " +
                       "the registry (account op:list), or ask him through request_user_approval.";

            string? url = UrlTools.Contains(toolName) ? UrlOf(argsJson)
                : toolName == "computer_cdp" ? CdpNavigationUrlOf(argsJson)
                : null;
            string? host = ProjectSitePlaybookStore.NormalizeDomain(url);
            if (host == null) return null;
            string? blocked = Match(host, settings.BlockedDomains);
            if (blocked != null)
                return $"AUTONOMY_POLICY: {blocked} is on this project's blocked-sites list, so {toolName} to {host} was refused. " +
                       "Choose another route to the goal; do not reach the site another way.";
            string? askFirst = Match(host, settings.AskFirstDomains);
            if (askFirst != null && !IsApproved(projectID, askFirst)) askFirstDomain = askFirst;
            return null;
        }

        public static void RecordApproval(string projectID, string domain) =>
            Approved[projectID + "|" + domain] = DateTime.UtcNow + AskFirstApprovalLifetime;

        public static bool IsApproved(string projectID, string domain) =>
            Approved.TryGetValue(projectID + "|" + domain, out var until) && DateTime.UtcNow < until;

        /// <summary>The policy as the agent should read it at the top of a wake, or "" when nothing is limited.</summary>
        public static string Describe(ProjectSettings settings)
        {
            var lines = new List<string>();
            if (settings.BlockedDomains.Count > 0)
                lines.Add("Never use these sites (refused at dispatch): " + string.Join(", ", settings.BlockedDomains) + ".");
            if (settings.AskFirstDomains.Count > 0)
                lines.Add("Ask first (your first use opens an approval card for Klives; approval lasts 24h): " + string.Join(", ", settings.AskFirstDomains) + ".");
            if (!settings.AllowHostShell) lines.Add("Host shell/script tools are OFF — work inside your own desktop instead.");
            if (!settings.AllowOutboundEmail) lines.Add("Outbound email is OFF — draft messages for Klives instead of sending them.");
            if (!settings.AllowAccountCreation) lines.Add("Creating new accounts is OFF — reuse registered accounts or ask Klives.");
            return string.Join("\n", lines);
        }

        /// <summary>Normalised, de-duplicated domain list from an array or a comma/newline separated string.</summary>
        public static List<string> ParseDomains(JToken? value)
        {
            IEnumerable<string?> raw = value switch
            {
                JArray array => array.Values<string?>(),
                null => Array.Empty<string?>(),
                _ => (value.Type == JTokenType.String ? value.Value<string>() ?? "" : value.ToString())
                    .Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries),
            };
            return raw.Select(ProjectSitePlaybookStore.NormalizeDomain).Where(d => d != null).Select(d => d!)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
        }

        private static string? Match(string host, IEnumerable<string> domains) =>
            domains.FirstOrDefault(d => string.Equals(host, d, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));

        private static string? UrlOf(string? argsJson)
        {
            if (string.IsNullOrWhiteSpace(argsJson)) return null;
            try { return (string?)JObject.Parse(argsJson)["url"]; }
            catch { return null; }
        }

        /// <summary>
        /// The page a raw CDP call would open — Page.navigate or Target.createTarget through
        /// computer_cdp's "send" — so the escape hatch cannot step around a blocked site. Params may
        /// arrive as an object or as a JSON string (the adapter accepts both).
        /// </summary>
        private static string? CdpNavigationUrlOf(string? argsJson)
        {
            if (string.IsNullOrWhiteSpace(argsJson)) return null;
            try
            {
                var args = JObject.Parse(argsJson);
                string method = ((string?)args["method"] ?? "").Trim();
                if (!string.Equals(method, "Page.navigate", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(method, "Target.createTarget", StringComparison.OrdinalIgnoreCase))
                    return null;
                JToken? parameters = args["params"];
                if (parameters?.Type == JTokenType.String) parameters = JToken.Parse((string?)parameters ?? "{}");
                return (string?)parameters?["url"];
            }
            catch { return null; }
        }
    }
}
