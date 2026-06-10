using System;
using System.Collections.Generic;
using System.Text;

namespace Omnipotent.Services.Omniscience.Deduction
{
    /// <summary>One message inside an extraction window, with its participant letter.</summary>
    public class WindowMessage
    {
        public int Number;                  // #n within the window — used for evidence refs
        public string MessageId = "";       // composite id
        public string AuthorIdentityId = "";
        public string ParticipantLetter = ""; // 'A', 'B', … assigned per window
        public string DisplayName = "";
        public DateTime SentAt;
        public string Content = "";
        public string? ReplyToMessageId;    // composite id of the replied-to message
        public int? ReplyToNumber;          // #n of the replied-to message when in-window
    }

    /// <summary>
    /// Builds the strict-JSON extraction prompt for one conversation window. Participants
    /// are letter-coded (A/B/C…) so the model can't confuse similar usernames, and every
    /// claim must cite message numbers so evidence maps back to message ids.
    /// </summary>
    public static class ExtractionPromptBuilder
    {
        public const string SystemPrompt =
@"You are an information-extraction engine reading a window of chat messages. Extract ONLY what the text supports — never guess, never embellish. Output a SINGLE valid JSON object, nothing else (no markdown fences, no commentary), with these keys (use [] when empty):

""facts"": [{""subject"":""A"",""category"":""location|education|employment|relationships|family|pets|health|possessions|schedule|preferences|beliefs|skills|finances|plans|age|name|misc"",""fact"":""concise factual claim"",""confidence"":""low|medium|high"",""evidence"":[3,5]}]
  - subject is the participant letter the fact is ABOUT (facts about non-authors count: what B says about A is evidence about A).
  - Make facts concrete and self-contained (""A is in year 11 at school"" not ""A mentioned school"").

""name_usages"": [{""speaker"":""A"",""name"":""james"",""type"":""vocative|self_identification|third_person|greeting"",""target"":""B or null"",""evidence"":[2]}]
  - vocative: addressing someone by name (""thanks james"" in a reply to B → target B, name james).
  - self_identification: speaker states their own name (""it's tom btw"").
  - third_person: naming someone not present or unresolved (""tell sarah i said hi"" → target null, name sarah).

""qa_pairs"": [{""asker"":""A"",""answerer"":""B"",""question"":""how old r u"",""answer"":""17"",""category"":""age"",""question_msg"":4,""answer_msg"":5}]
  - ONLY direct personal questions with a real answer in this window. These are the highest-value extractions.

""entity_mentions"": [{""by"":""A"",""kind"":""person|place|org|school|pet|event|object"",""name"":""Jake"",""descriptor"":""A's brother"",""evidence"":[7]}]

""relationship_signals"": [{""a"":""A"",""b"":""B"",""signal"":""petname|banter|family_ref|romance_hint|hostility|support|inside_joke"",""note"":""short justification"",""evidence"":[6]}]

""temporal_refs"": [{""subject"":""A"",""statement"":""I turn 18 next month"",""resolved"":""birthday ≈ 2025-04"",""evidence"":[9]}]
  - Resolve relative time expressions against the message dates shown in the transcript.

Evidence values are the #numbers of supporting messages. Participants are letters (A, B, C…) defined in the header — always use letters, never display names, in subject/speaker/target/by/a/b fields.";

        public static string BuildUserPrompt(string contextLabel, IReadOnlyList<(string Key, string DisplayName)> participants, IReadOnlyList<WindowMessage> messages)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Context: {contextLabel}");
            sb.AppendLine("Participants:");
            foreach (var (key, name) in participants)
                sb.AppendLine($"  {key} = {name}");
            sb.AppendLine();
            sb.AppendLine("Messages:");
            foreach (var m in messages)
            {
                string content = (m.Content ?? "").Replace("\r", " ").Replace("\n", " ");
                if (content.Length > 500) content = content[..500] + "…";
                string reply = m.ReplyToNumber.HasValue ? $" (replying to #{m.ReplyToNumber})" : "";
                sb.AppendLine($"#{m.Number} [{m.SentAt:yyyy-MM-dd HH:mm}] {m.ParticipantLetter}{reply}: {content}");
            }
            sb.AppendLine();
            sb.AppendLine("Extract now. Output the JSON object only.");
            return sb.ToString();
        }
    }
}
