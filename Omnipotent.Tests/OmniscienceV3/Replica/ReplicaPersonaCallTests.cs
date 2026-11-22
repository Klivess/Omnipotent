using System.Text.RegularExpressions;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Replica;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Tests.OmniscienceV3.Replica
{
    /// <summary>
    /// A persona call is sized by what the model can write back, not by what it can read. On the server a slice filled the
    /// model's whole window (77,000 to 187,000 tokens of exchanges) and the sheet that had to come back did not fit under
    /// the ceiling: 92 of 93 calls were cut off or answered without the sheet, twice each, minutes of the model lane a
    /// time, and no replica got a persona (21 Nov 2026). The one call that worked read 13,000 tokens.
    /// </summary>
    [Collection(nameof(EngineKnobsCollection))]
    public class ReplicaPersonaCallTests
    {
        private static string SliceOf(LlmRequest r) =>
            Regex.Match(r.Turns[0].Content, @"exchanges, (\d{4}-\d{2}-\d{2} to \d{4}-\d{2}-\d{2})\)").Groups[1].Value;

        private static async Task<(List<LlmRequest> Calls, long Items, int LargestPrompt)> TrainAsync()
        {
            await using var h = await ReplicaHarness.StartAsync();
            var v = await h.TrainAsync(Personas.Lena);
            Assert.Equal(VersionStates.Ready, v.State);
            Assert.Equal(StageStates.Done, v.Stage(Stages.Persona).State);
            var calls = h.Llm.Requests.Where(r => r.Purpose == "replica.persona").ToList();
            using var c = h.Runtime.Stores.Intel.OpenRead();
            long items = c.Scalar<long>(null, "SELECT COUNT(*) FROM replica_persona_items WHERE version_id=$v", ("$v", v.VersionId));
            return (calls, items, calls.Max(r => h.R.Services.Estimator.Estimate(ScriptedLlm.FullText(r))));
        }

        [Fact]
        public async Task ASliceWithMoreThanOneAnswersWorth_IsReadInParts_AndThePartsMakeOneSheet()
        {
            // As it is on the server, where a slice of this small corpus is one answer's worth: one call a slice.
            var whole = await TrainAsync();
            Assert.NotEmpty(whole.Calls);
            Assert.All(whole.Calls.GroupBy(SliceOf), slice => Assert.Single(slice));
            Assert.All(whole.Calls, r => Assert.True(r.MaxOutputTokens >= PersonaExtractor.OutputTokens, $"room to answer: {r.MaxOutputTokens}"));
            Assert.True(whole.Items > 0);

            int before = PersonaExtractor.EvidenceTokensPerCall;
            PersonaExtractor.EvidenceTokensPerCall = 1_000; // the same slices, now several answers' worth each
            try
            {
                var parts = await TrainAsync();
                var slices = parts.Calls.GroupBy(SliceOf).ToList();
                Assert.Equal(whole.Calls.Count, slices.Count); // the slices are what they were
                Assert.Contains(slices, s => s.Count() > 1);
                Assert.All(slices, s => Assert.InRange(s.Count(), 1, PersonaExtractor.MaxCallsPerSlice));
                // Every part is a question of its own to the model (its own session), and none is longer than was allowed.
                Assert.All(slices, s => Assert.Equal(s.Count(), s.Select(r => r.SessionKey).Distinct(StringComparer.Ordinal).Count()));
                Assert.True(parts.LargestPrompt <= 1_000, $"largest prompt {parts.LargestPrompt}");
                Assert.True(parts.LargestPrompt < whole.LargestPrompt);
                // What each part found is on the sheet: more parts, more of the slice described.
                Assert.True(parts.Items > whole.Items, $"{parts.Items} items in parts, {whole.Items} whole");
            }
            finally
            {
                PersonaExtractor.EvidenceTokensPerCall = before;
            }
        }
    }
}
