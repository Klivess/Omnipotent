using Newtonsoft.Json;
using System.Text;

namespace Omnipotent.Services.Omniscience.Replica
{
    /// <summary>
    /// Pure functions that build prompts for the Replica training stages and chat.
    /// Mirrors the role of <c>ProfilePromptBuilder</c> for personality profiles.
    ///
    /// Every builder is deterministic and side-effect-free; the trainer is the
    /// only thing that calls KliveLLM.
    /// </summary>
    public static class ReplicaPromptBuilder
    {
        // ── System prompts ──

        public const string VoiceSystemPrompt =
            "You are a forensic linguist analysing one specific person's writing style. " +
            "Output a TERSE markdown rulebook so another LLM can imitate them. Be concrete and specific. " +
            "Quote their actual phrasings. Cover: lexical fingerprint, slang/abbreviations, casing & punctuation habits, " +
            "emoji usage, message length & rhythm, opening/closing patterns, code-switching across languages if any. " +
            "Do NOT moralise. Do NOT add disclaimers. Output ONLY the markdown rulebook.";

        public const string OpinionSystemPrompt =
            "You are extracting one specific person's stances on topics from their messages. " +
            "Output a markdown ledger of OPINIONS THEY HOLD. For each topic give: stance (one line) and 1-3 of their actual phrasings as quoted bullets. " +
            "Skip topics where evidence is weak. Be honest about contradictions when present. No disclaimers, no moralising.";

        public const string RelationalSystemPrompt =
            "You are mapping how this person speaks DIFFERENTLY to specific people in their social circle vs their general baseline. " +
            "Output a TERSE markdown table or bullet list keyed by person. Cover: register shift (formal/casual), nicknames they use, " +
            "common topics, sentiment, and any in-jokes you can detect from the data. Skip relations with insufficient evidence.";

        public const string ReflexClassifierSystemPrompt =
            "You classify the role of a stimulus message that prompted a reply. " +
            "Reply with EXACTLY one of these labels and nothing else: " +
            "question, joke, insult, request, agreement, disagreement, correction, praise, casual.";

        public const string ChatSystemPromptTemplate =
            "You ARE {DISPLAY_NAME}. You are not an AI; you are a digital recreation of how {DISPLAY_NAME} actually talks, " +
            "trained on their real messages. Reply IN THEIR VOICE — their slang, casing, length, emoji habits, opinions, reflexes. " +
            "Never break character. Never add safety disclaimers, mental-health hedges, or 'as an AI' phrases. " +
            "Never call yourself a model, assistant, or LLM. If you don't know an answer, respond the way they would when stumped " +
            "(brief, dismissive, deflecting, joking — whatever their pattern is), not the way ChatGPT would.";

        public const string CritiqueSystemPrompt =
            "You are an editor whose job is to make a draft reply sound EXACTLY like the person described in the dossier. " +
            "Compare the draft against (a) their voice rulebook, (b) the example replies provided. " +
            "Flag any line that sounds AI, generic, hedged, or off-voice; rewrite the whole reply in their voice. " +
            "Output ONLY the rewritten reply text. No prefix, no explanation.";

        // ── Voice rulebook prompt ──

        public static string BuildVoicePrompt(string displayName, IReadOnlyList<string> handles, IReadOnlyList<string> sampleMessages)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Person: **{displayName}**");
            if (handles.Count > 0) sb.AppendLine($"Known handles: {string.Join(", ", handles)}");
            sb.AppendLine();
            sb.AppendLine($"You will be shown {sampleMessages.Count} of their actual messages, sampled across their timeline.");
            sb.AppendLine("Build the rulebook from observed patterns ONLY. Do not invent traits.");
            sb.AppendLine();
            sb.AppendLine("---SAMPLE_MESSAGES---");
            for (int i = 0; i < sampleMessages.Count; i++)
                sb.AppendLine($"{i + 1}. {Sanitise(sampleMessages[i])}");
            sb.AppendLine("---END_SAMPLE_MESSAGES---");
            sb.AppendLine();
            sb.AppendLine("Output a markdown rulebook with these sections:");
            sb.AppendLine("## Lexical fingerprint");
            sb.AppendLine("## Slang & abbreviations");
            sb.AppendLine("## Casing & punctuation");
            sb.AppendLine("## Emoji & emoticons");
            sb.AppendLine("## Length & rhythm");
            sb.AppendLine("## Opening & closing patterns");
            sb.AppendLine("## Distinctive verbal tics");
            return sb.ToString();
        }

        // ── Opinion ledger prompt ──

        public static string BuildOpinionPrompt(string displayName, string topicLabel, IReadOnlyList<string> topicalMessages)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Person: **{displayName}**");
            sb.AppendLine($"Topic cluster: **{topicLabel}**");
            sb.AppendLine();
            sb.AppendLine("Their messages relating to this cluster (verbatim):");
            for (int i = 0; i < topicalMessages.Count; i++)
                sb.AppendLine($"- {Sanitise(topicalMessages[i])}");
            sb.AppendLine();
            sb.AppendLine("Extract their stance on this topic in 1-3 lines, then quote 1-3 of their actual phrasings as bullets.");
            sb.AppendLine("If there is no consistent stance, say so plainly. Do NOT invent stances.");
            return sb.ToString();
        }

        // ── Relational map prompt ──

        public static string BuildRelationalPrompt(string displayName, IReadOnlyList<RelationalEvidence> evidence)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Person: **{displayName}**");
            sb.AppendLine();
            sb.AppendLine("For each relation below, summarise how they speak TO that specific person vs their general baseline.");
            foreach (var ev in evidence)
            {
                sb.AppendLine();
                sb.AppendLine($"### Relation: {ev.OtherDisplayName} ({ev.Platform}{(ev.PlatformUsername != null ? ":" + ev.PlatformUsername : "")})");
                sb.AppendLine($"Total messages they sent in shared conversations: {ev.MessageCount}");
                sb.AppendLine("Recent samples of how they message in those shared conversations:");
                for (int i = 0; i < ev.SampleMessages.Count; i++)
                    sb.AppendLine($"- {Sanitise(ev.SampleMessages[i])}");
            }
            return sb.ToString();
        }

        // ── Reflex classifier prompt ──

        public static string BuildReflexClassifierPrompt(string stimulus)
        {
            // The system prompt forces a single-label answer.
            return $"Stimulus message:\n\n\"{Sanitise(stimulus)}\"\n\nLabel:";
        }

        // ── Chat-time prompt assembly is built by ReplicaChatOrchestrator (not here). ──

        // ── Helpers ──

        private static string Sanitise(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            // Keep messages short in prompt (some can be enormous Discord pastes).
            if (s.Length > 800) s = s.Substring(0, 800) + "…";
            // Strip backtick fences that could fight markdown rendering.
            return s.Replace("```", "''' ");
        }

        public class RelationalEvidence
        {
            public string OtherDisplayName { get; set; } = string.Empty;
            public string Platform { get; set; } = string.Empty;
            public string? PlatformUsername { get; set; }
            public int MessageCount { get; set; }
            public List<string> SampleMessages { get; set; } = new();
        }
    }
}
