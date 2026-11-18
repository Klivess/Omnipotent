using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// Turns a tool call into a stable identity for "the thing the agent is trying to do", so repetition
    /// can be detected ACROSS wakes.
    ///
    /// The in-wake convergence guard keys on the raw <c>toolName + "|" + argsJson</c> string, which only
    /// catches byte-identical repeats inside a single wake. That misses the two shapes that actually burn
    /// budget: the same intent expressed with cosmetically different arguments (reordered JSON keys,
    /// different casing, a browser element <c>ref</c> that regenerates on every inspect), and the same
    /// intent retried on the NEXT wake after the guard's per-wake counter has been discarded.
    ///
    /// Normalisation here is deliberately conservative — it collapses identity noise, not meaning. Two
    /// calls that differ in what they would actually do keep separate keys, because a false "you already
    /// tried this" is worse than a missed one.
    /// </summary>
    public static class StewardAttemptKey
    {
        /// <summary>
        /// Argument names that carry no intent: handles, one-time secrets and correlation ids that differ
        /// on every call by design. A browser <c>ref</c> is the important one — refs are re-minted by each
        /// inspect, so clicking the same logical button twice never looks identical without dropping it.
        /// </summary>
        private static readonly HashSet<string> VolatileKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "ref", "nonce", "timestamp", "timestamputc", "requestid", "toolcallid", "sequence",
            "code", "otp", "verificationcode", "artifactid", "gateid", "wakeid", "screenshotid",
            "frameid", "correlationid", "idempotencykey",
        };

        /// <summary>Pointer-ish arguments, bucketed so a re-click a few pixels away still counts as the same target.</summary>
        private static readonly HashSet<string> CoordinateKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "x", "y", "left", "top", "fromx", "fromy", "tox", "toy", "startx", "starty", "endx", "endy",
        };

        private const int CoordinateBucket = 25;

        /// <summary>
        /// Tools whose whole purpose is to be called again with the same arguments — polling a mailbox,
        /// re-reading the screen, waiting for a postcondition, listing durable state. These are recorded
        /// (the ledger stays complete) but never warned about; telling an agent to stop polling for the
        /// verification code it is waiting for would be actively harmful.
        /// </summary>
        private static readonly HashSet<string> RepeatableTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "klivemail_wait_for_code", "klivemail_list_messages", "klivemail_get_message",
            "computer_screenshot", "computer_read_screen", "computer_window_state", "computer_wait",
            "computer_browser_inspect", "ensure_desktop_ready",
            "query_events", "cross_steward_search", "get_checkpoint", "get_grand_plan",
            "list_files", "stat_file", "read_file", "resolve_steward_path",
            "list_steward_directives", "list_observables", "list_stimulus_hooks",
            "account_list", "vault_list", "get_shortcuts",
            "recall_memories", "recall_memories_by_tag",
            "read_code_file", "list_code_directory", "get_global_path", "search_code",
            "read_knowledge_doc",
        };

        /// <summary>
        /// A readable, stable identity for one intent: <c>"{tool}:{op}:{hash}"</c>. The tool and op stay in
        /// clear text so the seeded ledger tells the agent something useful without a lookup.
        /// </summary>
        public static string For(string toolName, string? argsJson)
        {
            string tool = (toolName ?? "").Trim();
            string op = "";
            string canonical;
            try
            {
                var parsed = string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson);
                op = ((string?)parsed["op"] ?? "").Trim().ToLowerInvariant();
                canonical = Canonicalize(parsed);
            }
            catch
            {
                // Unparseable arguments still deserve a stable key; fall back to the raw text.
                canonical = Collapse(argsJson ?? "");
            }
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tool + "\0" + canonical)))[..12].ToLowerInvariant();
            return op.Length > 0 ? $"{tool}:{op}:{hash}" : $"{tool}:{hash}";
        }

        /// <summary>True when repeating this call with identical arguments is the intended usage, not a loop.</summary>
        public static bool IsDeliberatelyRepeatable(string toolName)
        {
            string tool = (toolName ?? "").Trim();
            if (RepeatableTools.Contains(tool)) return true;
            // Read-shaped surfaces added later default to repeatable rather than to nagging.
            return tool.StartsWith("list_", StringComparison.OrdinalIgnoreCase)
                || tool.StartsWith("get_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Deterministic textual form of the arguments: keys sorted, volatile keys dropped, coordinates
        /// bucketed, strings case- and whitespace-normalised. Values are otherwise kept in full — writing
        /// different content to the same path is a different attempt, not a repeat of the last one.
        /// </summary>
        private static string Canonicalize(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                {
                    var parts = new List<string>();
                    foreach (var property in ((JObject)token).Properties()
                        .Where(p => !VolatileKeys.Contains(p.Name))
                        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        string value = CoordinateKeys.Contains(property.Name) && IsNumeric(property.Value)
                            ? Bucket(property.Value.Value<double>()).ToString()
                            : Canonicalize(property.Value);
                        parts.Add($"{property.Name.ToLowerInvariant()}={value}");
                    }
                    return "{" + string.Join(",", parts) + "}";
                }
                case JTokenType.Array:
                    return "[" + string.Join(",", ((JArray)token).Select(Canonicalize)) + "]";
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return "";
                default:
                    return Collapse(token.ToString()).ToLowerInvariant();
            }
        }

        private static bool IsNumeric(JToken token) => token.Type is JTokenType.Integer or JTokenType.Float;

        private static int Bucket(double value) => (int)Math.Round(value / CoordinateBucket) * CoordinateBucket;

        private static string Collapse(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();
    }
}
