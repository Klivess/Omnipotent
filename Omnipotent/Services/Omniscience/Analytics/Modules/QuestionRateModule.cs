using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Curiosity profile: how often the person asks questions, what kind (factual /
    /// personal / opinion-seeking / requests), and where (facets). Question-asking is a
    /// strong engagement + social-interest signal.
    /// </summary>
    public class QuestionRateModule : IPersonAnalyticModule
    {
        public string Name => "question_rate";
        public int Version => 1;

        private static readonly (string Kind, Regex Re)[] Kinds =
        {
            ("personal",        new Regex(@"\b(?:how (?:are|r) (?:you|u)|you ok|u ok|wyd|hbu|wbu|how was your|are you|do you|did you|have you|will you)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("opinion_seeking", new Regex(@"\b(?:what do you think|thoughts|should i|would you|do you reckon|opinions?|rate this)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("factual",         new Regex(@"\b(?:what is|whats|what's|who is|whos|who's|when is|when does|where is|how does|how do|how many|how much|why is|why does)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("request",         new Regex(@"\b(?:can you|could you|can someone|anyone (?:know|got|have)|pls|please)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        };

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            return Task.FromResult(AnalyticSplits.Apply(msgs, ComputeFromMessages));
        }

        internal static JObject ComputeFromMessages(List<AnalyticMessage> msgs)
        {
            int analysed = 0, questions = 0;
            long questionChars = 0;
            var byKind = Kinds.ToDictionary(k => k.Kind, _ => 0);
            foreach (var m in msgs)
            {
                if (string.IsNullOrWhiteSpace(m.Content)) continue;
                analysed++;
                bool isQuestion = m.Content.Contains('?');
                string? kind = null;
                foreach (var k in Kinds)
                {
                    if (!k.Re.IsMatch(m.Content)) continue;
                    kind = k.Kind;
                    // Interrogative phrasing without '?' still counts (very common in chat).
                    isQuestion = true;
                    break;
                }
                if (!isQuestion) continue;
                questions++;
                questionChars += m.Content.Length;
                if (kind != null) byKind[kind]++;
            }
            return new JObject(
                new JProperty("messages_analysed", analysed),
                new JProperty("question_messages", questions),
                new JProperty("question_rate", analysed == 0 ? 0 : (double)questions / analysed),
                new JProperty("avg_question_chars", questions == 0 ? 0 : (double)questionChars / questions),
                new JProperty("personal_questions", byKind["personal"]),
                new JProperty("opinion_seeking_questions", byKind["opinion_seeking"]),
                new JProperty("factual_questions", byKind["factual"]),
                new JProperty("request_questions", byKind["request"])
            );
        }
    }
}
