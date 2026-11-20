using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Tests.OmniscienceV3.Knowledge
{
    /// <summary>
    /// What a provider's prefix cache could have served of every request the knowledge layer made, rebuilt from the fake
    /// gateway's journal. A serving stack reuses a request only up to the first byte that differs from one it has seen,
    /// so each request is laid out as the provider reads it (the system prompt, then the tool definitions, then the
    /// turns: the layout of the Hermes-style chat template AIRouter's models use) and compared with every earlier one.
    /// Two things are assumed against us: an answer the model generated is not in the cache (its thinking sits between the
    /// prompt and the answer when it is generated, and is gone when the answer is sent back), and nothing expires.
    /// Tokens are counted as four characters each, the same on both sides of every ratio.
    /// </summary>
    public sealed class PrefixCacheReplay
    {
        /// <param name="ReusableTokens">What the request before it on the same session held, when this one was a
        /// continuation of it: the denominator AIRouter's scheduler measures a continuation against. Zero for a first call.</param>
        public sealed record Sent(string Purpose, string SessionKey, int Turn, string Question, int PromptTokens, int CachedTokens, int ReusableTokens)
        {
            public bool Continuation => ReusableTokens > 0;

            /// <summary>The scheduler's own test (PrefixSurvivalMeter.HitFraction): a continuation that gets less than nine
            /// tenths of its predecessor back from cache is counted as a warm prefix that was lost.</summary>
            public bool CountsAsLostPrefix => Continuation && CachedTokens < 0.9 * ReusableTokens;
        }

        private sealed class Session
        {
            public string System = "";
            public readonly List<(string Role, string Content)> Messages = new();
            public bool Unanswered;
            public int Turns;
            public int LastPromptTokens;
        }

        private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
        private readonly List<string> seen = new();

        public List<Sent> Calls { get; } = new();

        public static PrefixCacheReplay Of(IEnumerable<FakeLlm.JournalEntry> journal)
        {
            var replay = new PrefixCacheReplay();
            foreach (var entry in journal)
            {
                if (entry.Request == null) replay.sessions.Remove(entry.SessionKey);
                else replay.Ask(entry.Request, entry.Answer, entry.Success);
            }
            return replay;
        }

        private void Ask(LlmRequest request, string? answer, bool success)
        {
            // The gateway's rules: a kept session takes the next question on top of what it holds, unless its last call
            // was left unanswered; a request that rewinds goes back to the system prompt of a session that has the same one.
            sessions.TryGetValue(request.SessionKey, out var session);
            bool rewound = request.KeepSession && request.Rewind && session != null && session.System == (request.SystemPrompt ?? "");
            bool fresh = !request.KeepSession || session == null || (request.Rewind ? !rewound : session.Unanswered);
            if (fresh) sessions[request.SessionKey] = session = new Session { System = request.SystemPrompt ?? "" };
            else if (rewound)
            {
                session!.Messages.Clear();
                session.Unanswered = false;
            }
            foreach (var turn in request.Turns) session!.Messages.Add((turn.Role, turn.Content));

            // The tool definitions exactly as the gateway sends them: the call's family, or its own schema alone.
            string image = Render(session!.System, Omnipotent.Services.KliveLLM.KliveLLM.GatewaySchemas(request), session.Messages);
            int cached = 0;
            foreach (string earlier in seen) cached = Math.Max(cached, CommonPrefix(earlier, image));
            seen.Add(image);

            int prompt = image.Length / 4;
            session.Turns++;
            Calls.Add(new Sent(request.Purpose, request.SessionKey, session.Turns, request.Turns[^1].Content, prompt, Math.Min(prompt, cached / 4),
                fresh ? 0 : Math.Min(session.LastPromptTokens, prompt)));
            session.LastPromptTokens = prompt;

            if (success && answer != null)
            {
                // A structured answer is a tool call, and the gateway records a result for it before the next question.
                string name = request.OutputSchema?.Name ?? "";
                session.Messages.Add(("assistant", request.OutputSchema == null ? answer : "<tool_call>\n{\"name\": \"" + name + "\", \"arguments\": " + answer + "}\n</tool_call>"));
                if (request.OutputSchema != null) session.Messages.Add(("tool", "{\"recorded\":true}"));
            }
            else session.Unanswered = true;
            if (!request.KeepSession) sessions.Remove(request.SessionKey);
        }

        private static string Render(string system, IReadOnlyList<LlmOutputSchema> tools, IReadOnlyList<(string Role, string Content)> messages)
        {
            var sb = new StringBuilder("<|im_start|>system\n").Append(system);
            if (tools.Count > 0)
            {
                sb.Append("\n\n# Tools\n\n<tools>");
                foreach (var tool in tools)
                {
                    var definition = new JObject
                    {
                        ["type"] = "function",
                        ["function"] = new JObject { ["name"] = tool.Name, ["description"] = tool.Description, ["parameters"] = tool.Schema.DeepClone() },
                    };
                    sb.Append('\n').Append(definition.ToString(Newtonsoft.Json.Formatting.None));
                }
                sb.Append("\n</tools>");
            }
            sb.Append("<|im_end|>\n");
            foreach (var (role, content) in messages) sb.Append("<|im_start|>").Append(role).Append('\n').Append(content).Append("<|im_end|>\n");
            return sb.Append("<|im_start|>assistant\n").ToString();
        }

        private static int CommonPrefix(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length), i = 0;
            while (i < n && a[i] == b[i]) i++;
            return i;
        }

        public static double Share(IEnumerable<Sent> calls)
        {
            long prompt = 0, cached = 0;
            foreach (var c in calls) { prompt += c.PromptTokens; cached += c.CachedTokens; }
            return prompt == 0 ? 1 : (double)cached / prompt;
        }

        /// <summary>One line per kind of call: how many, how large, how much of it a prefix cache holds.</summary>
        public string Table(Func<Sent, string> kind)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-34}{1,6}{2,12}{3,12}{4,9}{5,12}{6,12}", "kind", "calls", "prompt", "cached", "hit", "re-sent", "lost"));
            void Line(string name, IReadOnlyList<Sent> rows)
            {
                long prompt = rows.Sum(r => (long)r.PromptTokens), cached = rows.Sum(r => (long)r.CachedTokens);
                var continued = rows.Where(r => r.Continuation).ToList();
                long reusable = continued.Sum(r => (long)r.ReusableTokens), served = continued.Sum(r => (long)Math.Min(r.CachedTokens, r.ReusableTokens));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-34}{1,6}{2,12:N0}{3,12:N0}{4,9:P1}{5,12}{6,12}", name, rows.Count, prompt, cached,
                    prompt == 0 ? 0 : (double)cached / prompt,
                    reusable == 0 ? "-" : ((double)served / reusable).ToString("P1", CultureInfo.InvariantCulture),
                    continued.Count == 0 ? "-" : continued.Count(r => r.CountsAsLostPrefix).ToString(CultureInfo.InvariantCulture) + " of " + continued.Count.ToString(CultureInfo.InvariantCulture)));
            }
            foreach (var group in Calls.GroupBy(kind).OrderBy(g => g.Key, StringComparer.Ordinal)) Line(group.Key, group.ToList());
            Line("ALL", Calls);
            return sb.ToString();
        }
    }
}
