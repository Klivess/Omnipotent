using System.Text.Json;
using System.Text.RegularExpressions;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Llm;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>
    /// A fake LLM for WS-C tests. It answers by purpose with a hidden oracle over synthetic text (the synthetic corpus
    /// vocabulary), so tests can check that the local ensemble learns what the "teacher" knows. It records every request.
    /// </summary>
    internal sealed class FakeLlm : ILlmClient
    {
        public static readonly string[] PositiveWords = { "saffron", "nectar" };
        public static readonly string[] NegativeWords = { "tundra", "glacier" };

        private static readonly Regex Target = new(@"<m ref=""(?<ref>[^""]+)""[^>]*target=""yes""[^>]*>(?<text>.*?)</m>", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex Any = new(@"<m ref=""(?<ref>[^""]+)""[^>]*>(?<text>.*?)</m>", RegexOptions.Singleline | RegexOptions.Compiled);

        public List<LlmRequest> Requests { get; } = new();
        public Func<LlmRequest, LlmResult?>? Override { get; set; }
        /// <summary>Concept names the fake policy check treats as special-category.</summary>
        public HashSet<string> SensitiveConcepts { get; } = new(StringComparer.OrdinalIgnoreCase) { "religion talk" };
        /// <summary>Words that make the fake policy check treat a watch rule as aimed at a special category.</summary>
        public HashSet<string> SensitiveRules { get; } = new(StringComparer.OrdinalIgnoreCase) { "religion" };
        /// <summary>The word that makes a message match a watch rule, unless it is negated ("not selling").</summary>
        public string WatchWord { get; set; } = "selling";
        /// <summary>The word that makes a message match a taught concept.</summary>
        public string ConceptWord { get; set; } = "saffron";

        public static string OracleSentiment(string text)
        {
            int score = PositiveWords.Sum(w => Count(text, w)) - NegativeWords.Sum(w => Count(text, w));
            return score > 0 ? "positive" : score < 0 ? "negative" : "neutral";
        }

        private static int Count(string text, string word) => Regex.Matches(text, @"\b" + Regex.Escape(word) + @"\b").Count;

        public static string OracleLanguage(string text) =>
            text.Contains("日本") ? "ja" : text.Contains("привет") ? "ru" : text.Contains("مرحبا") ? "ar" : "en";

        public Task<LlmResult> QueryAsync(LlmRequest request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            if (Override?.Invoke(request) is { } forced) return Task.FromResult(forced);
            string user = request.Turns[^1].Content;
            if (request.Purpose.StartsWith("briefs.", StringComparison.Ordinal)) return Task.FromResult(Ok(Brief(request), user));
            string json = request.Purpose switch
            {
                "classify.teacher" => Teacher(user),
                "classify.concept.policy" => JsonSerializer.Serialize(new { targets_special_category = SensitiveConcepts.Any(s => user.Contains(s, StringComparison.OrdinalIgnoreCase)) }),
                "classify.concept.label" => Concept(user),
                "signals.watch.policy" => JsonSerializer.Serialize(new { targets_special_category = SensitiveRules.Any(w => user.Contains(w, StringComparison.OrdinalIgnoreCase)) }),
                "signals.watch.confirm" => Watch(user),
                "translate.batch" => Translate(user),
                // Links: every link is filed under "other" unless a test says otherwise (Override).
                "links.categorise" => JsonSerializer.Serialize(new { items = Regex.Matches(user, "<l ref=\"(?<ref>[0-9]+)\"").Select(m => new { @ref = m.Groups["ref"].Value, topic = "other", summary = "A link." }) }),
                _ => throw new InvalidOperationException($"FakeLlm has no answer for purpose '{request.Purpose}'."),
            };
            return Task.FromResult(Ok(json, user));
        }

        public static LlmResult Ok(string json, string prompt) =>
            new(true, null, json, LlmErrorKind.None, null, "stop", "fake-model", "fake", Math.Max(1, prompt.Length / 4), 0, json.Length / 4, prompt.Length, 5);

        private static string Decode(string s) => s.Replace("‹", "<").Replace("›", ">");

        /// <summary>What the fake translator knows. Anything else comes back unchanged.</summary>
        public static readonly Dictionary<string, string> Phrases = new()
        {
            ["これは新しいテストです"] = "this is a new test", ["これはテストです"] = "this is a test", ["привет как дела"] = "hi how are you", ["مرحبا بالعالم"] = "hello world", ["日本"] = "Japan",
        };

        /// <summary>The translator: known phrases are replaced, markers are kept (unless the text says DROPMARK), a message
        /// that says SKIPME is not answered for, and the language is read off the script.</summary>
        private static string Translate(string user)
        {
            var items = new List<object>();
            foreach (Match m in Any.Matches(user))
            {
                string text = Decode(m.Groups["text"].Value);
                if (text.Contains("SKIPME")) continue;
                string translated = text;
                foreach (var (from, to) in Phrases.OrderByDescending(x => x.Key.Length)) translated = translated.Replace(from, to);
                if (text.Contains("DROPMARK")) translated = Regex.Replace(translated, "⟦[0-9]+⟧", "");
                items.Add(new { @ref = m.Groups["ref"].Value, lang = OracleLanguage(text), text = translated });
            }
            return JsonSerializer.Serialize(new { items });
        }

        private static string Teacher(string user)
        {
            var items = Target.Matches(user).Select(m =>
            {
                string text = Decode(m.Groups["text"].Value);
                var labels = new Dictionary<string, string> { ["lang"] = OracleLanguage(text) };
                string sentiment = OracleSentiment(text);
                if (sentiment == "positive") { labels["sen"] = "pos"; labels["emo"] = "joy"; }
                if (sentiment == "negative") { labels["sen"] = "neg"; labels["emo"] = "sad"; }
                return new { @ref = m.Groups["ref"].Value, labels };
            }).ToList();
            return JsonSerializer.Serialize(new { items });
        }

        private string Concept(string user)
        {
            var items = Any.Matches(user).Select(m => new { @ref = m.Groups["ref"].Value, match = Decode(m.Groups["text"].Value).Contains(ConceptWord), conf = 0.9 }).ToList();
            return JsonSerializer.Serialize(new { items });
        }

        private static readonly Regex BriefGroup = new(@"<group g=""(?<g>[^""]+)"" title=""(?<title>[^""]*)"">(?<body>.*?)</group>", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex BriefItem = new(@"<(?<tag>m|p) ref=""(?<ref>[^""]+)""(?<attrs>[^>]*)>(?<text>.*?)</\k<tag>>", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex BriefBy = new(@"by=""(?<by>[^""]*)""", RegexOptions.Compiled);

        /// <summary>
        /// The summariser's oracle: per group one point for every distinct first word of its items, citing those items
        /// ("3 about pizza"). An item containing FABRICATE makes it add a point that cites something never shown and one that
        /// cites nothing, which the engine must drop. Items containing "!!!" are notable. For action items it reads promises
        /// ("I will"), plans ("let's") and questions ("?").
        /// </summary>
        private static string Brief(LlmRequest request)
        {
            string user = request.Turns[^1].Content;
            bool overall = !(request.SystemPrompt ?? "").Contains("Leave \"overall\" empty", StringComparison.Ordinal);
            bool actions = request.Purpose.StartsWith("briefs.actions", StringComparison.Ordinal);
            var groups = new List<object>();
            var overallPoints = new List<object>();
            var notable = new List<string>();
            foreach (Match g in BriefGroup.Matches(user))
            {
                var items = BriefItem.Matches(g.Groups["body"].Value).Select(m => (Ref: m.Groups["ref"].Value, Text: Decode(m.Groups["text"].Value).Trim(),
                    By: BriefBy.Match(m.Groups["attrs"].Value) is { Success: true } by ? by.Groups["by"].Value : "")).ToList();
                var points = new List<object>();
                if (actions)
                {
                    foreach (var i in items)
                    {
                        if (i.Text.Contains("I will", StringComparison.OrdinalIgnoreCase)) points.Add(new { text = $"Promise: {i.By} said they will do it", refs = new[] { i.Ref } });
                        else if (i.Text.Contains("let's", StringComparison.OrdinalIgnoreCase)) points.Add(new { text = $"Plan: {i.Text}", refs = new[] { i.Ref } });
                        else if (i.Text.EndsWith('?')) points.Add(new { text = $"Question: {i.Text}", refs = new[] { i.Ref } });
                    }
                }
                else
                {
                    foreach (var cluster in items.GroupBy(i => Regex.Match(i.Text, @"[\p{L}\p{N}\[\]#]+").Value.ToLowerInvariant()))
                        points.Add(new { text = $"{cluster.Count()} about {cluster.Key}", refs = cluster.Select(c => c.Ref).ToArray() });
                }
                if (items.Any(i => i.Text.Contains("FABRICATE", StringComparison.Ordinal)))
                {
                    points.Add(new { text = "fabricated from a source that was never shown", refs = new[] { "99999" } });
                    points.Add(new { text = "fabricated without a source", refs = Array.Empty<string>() });
                }
                notable.AddRange(items.Where(i => i.Text.Contains("!!!", StringComparison.Ordinal)).Select(i => i.Ref));
                groups.Add(new { g = g.Groups["g"].Value, points });
                if (overall && items.Count > 0) overallPoints.Add(new { text = "overall: " + g.Groups["title"].Value, refs = new[] { items[0].Ref } });
            }
            return JsonSerializer.Serialize(new { groups, overall = overallPoints, notable });
        }

        private string Watch(string user)
        {
            var items = Any.Matches(user).Select(m =>
            {
                string text = Decode(m.Groups["text"].Value);
                bool match = text.Contains(WatchWord, StringComparison.OrdinalIgnoreCase) && !text.Contains("not " + WatchWord, StringComparison.OrdinalIgnoreCase);
                return new { @ref = m.Groups["ref"].Value, match, conf = 0.9 };
            }).ToList();
            return JsonSerializer.Serialize(new { items });
        }

        /// <summary>The context window the fake reports (tests shrink it to force a summary across several calls).</summary>
        public int ContextWindow { get; set; } = 260_000;

        public Task<ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) =>
            Task.FromResult(new ModelContextInfo("AIRouter", "fake-model", ContextWindow, Math.Min(16_384, ContextWindow / 4), "test", null, 0, null));

        public void EndSession(string sessionKey) { }
    }
}
