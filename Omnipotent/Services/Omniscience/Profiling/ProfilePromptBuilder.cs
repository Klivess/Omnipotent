using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;

namespace Omnipotent.Services.Omniscience.Profiling
{
    /// <summary>
    /// Builds a deterministic prompt: person header + analytics JSON + a sampled set of
    /// representative messages. The LLM is asked to output a markdown narrative followed
    /// by a sentinel and a structured JSON block.
    /// </summary>
    public static class ProfilePromptBuilder
    {
        public const string TraitsSentinel = "---TRAITS_JSON---";

        public static string SystemPrompt =>
@"You are an analyst constructing a personality dossier from the messaging history of a single person.
You are given (1) computed analytic statistics, (2) a sample of their actual messages, (3) social-graph context.
Produce TWO sections separated by the line '---TRAITS_JSON---':
- Section 1: a concise but rich markdown narrative covering communication style, vocabulary, humour,
  emotional tone, conflict tendencies, recurring topics/interests, social positioning, and noteworthy patterns.
- Section 2: a single valid JSON object with keys: communication_style (string), tone (string),
  humour (string), conflict_tendency (string), top_interests (array of strings), big_five_estimate
  (object with openness, conscientiousness, extraversion, agreeableness, neuroticism each 0-1),
  notable_patterns (array of strings).
Be specific. Do not invent facts that are not supported by the data. Do not include disclaimers.";

        public static string Build(string personDisplayName, string personHandles, JObject statsBundle, IList<string> sampleMessages, IList<string> socialGraphSummary)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Person\n- Display name: {personDisplayName}\n- Known handles: {personHandles}");
            sb.AppendLine();
            sb.AppendLine("# Analytics");
            sb.AppendLine("```json");
            sb.AppendLine(statsBundle.ToString(Newtonsoft.Json.Formatting.Indented));
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("# Social graph");
            foreach (var s in socialGraphSummary) sb.AppendLine("- " + s);
            sb.AppendLine();
            sb.AppendLine("# Representative messages (chronological sample)");
            int i = 1;
            foreach (var m in sampleMessages)
            {
                if (string.IsNullOrWhiteSpace(m)) continue;
                string clean = m.Replace("\r", " ").Replace("\n", " ");
                if (clean.Length > 400) clean = clean[..400] + "\u2026";
                sb.AppendLine($"{i++}. {clean}");
                if (i > 80) break;
            }
            sb.AppendLine();
            sb.AppendLine("Now produce the dossier as instructed.");
            return sb.ToString();
        }
    }
}
