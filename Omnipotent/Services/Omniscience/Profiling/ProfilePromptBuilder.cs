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
- Section 1: a structured markdown dossier. Use exactly these headings, in this order:
    ## Communication Style
    ## Vocabulary & Phrases
    ## Emotional Tone
    ## Humour
    ## Conflict Tendencies
    ## Recurring Interests
    ## Social Positioning
    ## Noteworthy Patterns
    Under each heading, write 2-4 concise bullets. Avoid a single wall of prose.
- Section 2: a single valid JSON object with keys: communication_style (string), tone (string),
  humour (string), conflict_tendency (string), top_interests (array of strings), big_five_estimate
  (object with openness, conscientiousness, extraversion, agreeableness, neuroticism each 0-1),
  notable_patterns (array of strings).
Be specific. Do not invent facts that are not supported by the data. Do not include disclaimers.";

        public static string BiographicalSystemPrompt =>
@"You are an OSINT analyst constructing a *biographical inference dossier* from a single person's chat history.
You are given (1) computed analytics, (2) a sample of their messages, (3) a timezone-inference module's output.
Produce a single markdown document that lists, with explicit confidence (low/medium/high) for each item:
- likely real-world location (country, region, city if hinted)
- likely UTC offset / timezone
- school or university status (current student / graduate / dropout / unknown), and field of study if mentioned
- employment status and inferred role / industry
- specific courses, subjects, or technologies mentioned
- approximate age band
- spoken languages
- hobbies and recurring interests
- any concrete personal facts (pets, family situation, relationship status, health) the person has disclosed
For each bullet cite a brief reason ('messages mention London twice and Premier League fixtures' etc.).
Do NOT fabricate. If a field has zero evidence, write 'no evidence'. Do not include disclaimers about privacy.";

        public static string BuildBiographical(string personDisplayName, string personHandles, JObject statsBundle, IList<string> sampleMessages)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Person\n- Display name: {personDisplayName}\n- Known handles: {personHandles}");
            sb.AppendLine();
            sb.AppendLine("# Analytics (subset)");
            sb.AppendLine("```json");
            // Pass timezone, language, interests, vocabulary phrases, top relationships \u2014 the rest is noise for this prompt.
            var subset = new JObject();
            foreach (var key in new[] { "timezone_inference", "language", "interests", "activity_pattern", "vocabulary", "social_graph", "mention_affinity" })
                if (statsBundle[key] is JToken t) subset[key] = t.DeepClone();
            sb.AppendLine(subset.ToString(Newtonsoft.Json.Formatting.Indented));
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("# Representative messages (chronological sample)");
            int i = 1;
            foreach (var m in sampleMessages)
            {
                if (string.IsNullOrWhiteSpace(m)) continue;
                string clean = m.Replace("\r", " ").Replace("\n", " ");
                if (clean.Length > 400) clean = clean[..400] + "\u2026";
                sb.AppendLine($"{i++}. {clean}");
                if (i > 200) break;
            }
            sb.AppendLine();
            sb.AppendLine("Now produce the biographical dossier as instructed.");
            return sb.ToString();
        }

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
                if (i > 200) break;
            }
            sb.AppendLine();
            sb.AppendLine("Now produce the dossier as instructed.");
            return sb.ToString();
        }
    }
}
