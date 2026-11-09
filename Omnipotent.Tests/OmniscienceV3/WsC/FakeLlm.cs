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
            string json = request.Purpose switch
            {
                "classify.teacher" => Teacher(user),
                "classify.concept.policy" => JsonSerializer.Serialize(new { targets_special_category = SensitiveConcepts.Any(s => user.Contains(s, StringComparison.OrdinalIgnoreCase)) }),
                "classify.concept.label" => Concept(user),
                _ => throw new InvalidOperationException($"FakeLlm has no answer for purpose '{request.Purpose}'."),
            };
            return Task.FromResult(Ok(json, user));
        }

        public static LlmResult Ok(string json, string prompt) =>
            new(true, null, json, LlmErrorKind.None, null, "stop", "fake-model", "fake", Math.Max(1, prompt.Length / 4), 0, json.Length / 4, prompt.Length, 5);

        private static string Decode(string s) => s.Replace("‹", "<").Replace("›", ">");

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

        public Task<ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) =>
            Task.FromResult(new ModelContextInfo("AIRouter", "fake-model", 260_000, 16_384, "test", null, 0, null));

        public void EndSession(string sessionKey) { }
    }
}
