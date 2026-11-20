using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Temporal;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniscienceV3.Knowledge
{
    /// <summary>
    /// How much of what the extraction pipeline sends a prefix cache can serve. On 20 Nov 2026, two days after v3 began
    /// reading the archive, the fleet's cache-hit rate fell from 80.9% to 48.5% with v3's background lane the largest
    /// sender (7.8M of 13.6M tokens). Invented people and text only.
    /// </summary>
    public class PrefixCacheShapeTests
    {
        private static readonly long Now = Instant.FromUtc(new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc));
        private const long Minute = 60_000L;

        private readonly ITestOutputHelper output;

        public PrefixCacheShapeTests(ITestOutputHelper output) => this.output = output;

        private static bool IsCritic(string question) => question.Contains("sceptical reviewer", StringComparison.Ordinal);

        /// <summary>A model that finds "I have taken up X" in the subject's own messages, and a critic that agrees with every fact.
        /// A third of the subject's messages state something, so an answer is about as long as the evidence it was read from,
        /// as on the server (0.4 to 2.6 output tokens per evidence token).</summary>
        private static void Wire(KnowledgeHarness h)
        {
            h.Llm.On("deduction.extract", call =>
            {
                if (IsCritic(call.Question))
                    return JsonSerializer.Serialize(new { verdicts = Regex.Matches(call.Question, @"^(f\d+):", RegexOptions.Multiline).Select(m => new { id = m.Groups[1].Value, verdict = "supported", reason = "stated" }) });
                var roster = FakeLlm.Roster(call.Prompt);
                string subject = roster["Dee Marsh"];
                var claims = new List<object>();
                foreach (var (reference, (handle, text)) in FakeLlm.EvidenceLines(call.Prompt))
                {
                    var m = Regex.Match(text, @"I have taken up (Pastime\d+)");
                    if (handle != subject || !m.Success) continue;
                    claims.Add(new
                    {
                        id = "c" + (claims.Count + 1).ToString(CultureInfo.InvariantCulture), subject, attribute = "hobby", value = m.Groups[1].Value, polarity = "affirmed",
                        modality = "literal", source_kind = "self_disclosure", speech_act = "statement", sincerity = "sincere",
                        evidence = new[] { new { @ref = reference, quote = m.Value } }, probability = 0.9,
                    });
                }
                return JsonSerializer.Serialize(new { claims });
            });
            h.Llm.On("deduction.values", call => JsonSerializer.Serialize(new
            {
                groups = Regex.Matches(call.Prompt, @"^- (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).Select(v => new { canonical = v, members = new[] { v } }),
            }));
            h.Llm.On("deduction.detective", _ => """{"inferences":[],"hypotheses":[]}""");
            h.Llm.On("deduction.hypothesis", _ => """{"outcome":"no_change","cites":[]}""");
        }

        private static async Task SeedAsync(KnowledgeHarness h, int messages)
        {
            var corpus = new CorpusBuilder(h.Stores);
            await corpus.PersonAsync("me", "Klives", self: true);
            await corpus.PersonAsync("dee", "Dee Marsh", tier: "tracked");
            await corpus.ConversationAsync("discord:c-dee", "dm", "me", "dee");
            const string filler = "the weather has been all over the place again this week and nobody on the street can agree what to wear in the morning";
            for (int i = 0; i < messages; i++)
            {
                long at = Now - 400L * 86_400_000 + i * 20 * Minute;
                string text = i % 3 == 1 ? $"by the way I have taken up Pastime{i} now, started last month" : $"{filler} ({i})";
                await corpus.MessageAsync(i % 3 == 0 ? "me" : "dee", "discord:c-dee", at, i % 3 == 0 ? $"right, {filler}" : text);
            }
        }

        private static string Kind(PrefixCacheReplay.Sent call) => call.Purpose != "deduction.extract" ? call.Purpose
            : IsCritic(call.Question) ? "deduction.extract (critic)"
            : call.Question.Contains("could not be used", StringComparison.Ordinal) ? "deduction.extract (asked again)"
            : "deduction.extract (reading)";

        [Fact]
        public async Task WhatAnExtractionRunSends_AndHowMuchOfItACacheHolds()
        {
            await using var h = await KnowledgeHarness.StartAsync(Now);
            Wire(h);
            await SeedAsync(h, 900);

            // Job after job until everything is read (a job reads four slices, then yields).
            for (int round = 0; round < 12; round++)
            {
                int before = h.Llm.Requests.Count(r => r.Purpose == "deduction.extract");
                while (await h.Runtime.Host.PumpChangesOnceAsync() > 0) { }
                await h.Runtime.Host.PlanOnceAsync();
                var failures = await InquiryTests.DriveAsync(h);
                Assert.True(failures.Count == 0, string.Join("\n", failures));
                if (h.Llm.Requests.Count(r => r.Purpose == "deduction.extract") == before) break;
                h.Clock.Advance(TimeSpan.FromMinutes(20));
            }

            var replay = PrefixCacheReplay.Of(h.Llm.Journal);
            output.WriteLine(replay.Table(Kind));

            // Before 20 Nov 2026 this run read: critic calls 33.8% cached, 37.9% of what was sent a second time served from
            // cache, and 20 of 20 later questions counted by the scheduler as a warm prefix lost (45.4% over all calls).
            var later = replay.Calls.Where(c => c.Continuation).ToList();
            Assert.NotEmpty(later);
            // Nothing the provider had already been sent is prefilled again: every later question begins with the bytes of
            // the one before it, tool definitions included, and none of them is scored as a lost prefix (which is what
            // shortens the cache lifetime AIRouter's scheduler plans every service around).
            Assert.DoesNotContain(later, c => c.CountsAsLostPrefix);
            long resent = later.Sum(c => (long)c.ReusableTokens), served = later.Sum(c => (long)Math.Min(c.CachedTokens, c.ReusableTokens));
            Assert.True(served >= 0.97 * resent, $"{served:N0} of {resent:N0} re-sent tokens were served from cache");

            // The critic costs its own question and nothing else: the evidence comes out of the cache, and the reading's
            // answer is not sent back at all.
            var critics = replay.Calls.Where(c => Kind(c) == "deduction.extract (critic)").ToList();
            var readings = replay.Calls.Where(c => Kind(c) == "deduction.extract (reading)").ToList();
            Assert.Equal(readings.Count, critics.Count);
            Assert.True(PrefixCacheReplay.Share(critics) >= 0.85, $"critic calls were {PrefixCacheReplay.Share(critics):P1} cached");
            foreach (var critic in critics)
            {
                var reading = readings.Single(r => r.SessionKey == critic.SessionKey);
                // Both are the slice's prefix and a question: what the critic adds is the difference between the two questions.
                int ownQuestion = critic.Question.Length - (reading.Question.Length - ExtractionPrompt.ExtractQuestion.Length);
                Assert.True(critic.PromptTokens - critic.CachedTokens <= ownQuestion / 4 + 40,
                    $"a critic call prefilled {critic.PromptTokens - critic.CachedTokens:N0} tokens for a question of {ownQuestion / 4:N0}");
            }

            // A reading has to prefill its slice once: what it gets from the cache is the head it shares with every other
            // reading (the rules and the tool definitions) and, within a job, what is known about the subject. This is why
            // the plain hit rate of extraction cannot be 100% and is not what the lane is judged on.
            Assert.All(readings.Skip(1), r => Assert.True(r.CachedTokens >= 1_500, "a reading shared less than the rules and tools with the one before it"));
            Assert.True(PrefixCacheReplay.Share(replay.Calls) >= 0.60, $"all calls were {PrefixCacheReplay.Share(replay.Calls):P1} cached");
        }
    }
}
