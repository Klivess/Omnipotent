using Omnipotent.Services.Omniscience.Analytics;
using Omnipotent.Services.Omniscience.Deduction;
using Omnipotent.Services.Omniscience.Profiling;
using Omnipotent.Services.Omniscience.Replica;

namespace Omnipotent.Tests.Omniscience
{
    public class TemporalWeightingTests
    {
        [Fact]
        public void Weight_RecentMessage_NearOne()
        {
            TemporalWeighting.Configure(180, 0.05);
            var now = DateTime.UtcNow;
            Assert.True(TemporalWeighting.Weight(now.AddDays(-1), now) > 0.99);
        }

        [Fact]
        public void Weight_HalfLife_IsHalf()
        {
            TemporalWeighting.Configure(180, 0.05);
            var now = DateTime.UtcNow;
            Assert.Equal(0.5, TemporalWeighting.Weight(now.AddDays(-180), now), 2);
        }

        [Fact]
        public void Weight_SixYearOldMessage_HitsFloorNotZero()
        {
            // The floor is the whole point: 6+ year corpora must stay audible.
            TemporalWeighting.Configure(180, 0.05);
            var now = DateTime.UtcNow;
            Assert.Equal(0.05, TemporalWeighting.Weight(now.AddYears(-6), now));
        }

        [Fact]
        public void FacetKey_MapsConversationKinds()
        {
            Assert.Equal("dm", AnalyticSplits.FacetKey(new AnalyticMessage { ConversationKind = "dm" }));
            Assert.Equal("group_dm", AnalyticSplits.FacetKey(new AnalyticMessage { ConversationKind = "group_dm" }));
            Assert.Equal("server:My Server", AnalyticSplits.FacetKey(new AnalyticMessage { ConversationKind = "guild_channel", GuildName = "My Server" }));
            Assert.Equal("server:123", AnalyticSplits.FacetKey(new AnalyticMessage { ConversationKind = "guild_channel", GuildId = "123" }));
        }

        [Fact]
        public void StratifiedSample_PrefersRecentMessages()
        {
            var now = DateTime.UtcNow;
            var msgs = new List<(string Content, DateTime SentAt)>();
            for (int i = 0; i < 1000; i++) msgs.Add(($"old{i}", now.AddDays(-400 - i)));
            for (int i = 0; i < 1000; i++) msgs.Add(($"mid{i}", now.AddDays(-100 - i % 200)));
            for (int i = 0; i < 1000; i++) msgs.Add(($"new{i}", now.AddDays(-(i % 80))));

            var sample = PersonalityProfiler.StratifiedSample(msgs, 100);
            Assert.Equal(100, sample.Count);
            int recent = sample.Count(s => s.StartsWith("new"));
            int old = sample.Count(s => s.StartsWith("old"));
            Assert.True(recent >= 45, $"expected ≥45 recent, got {recent}");
            Assert.True(old <= 30, $"expected ≤30 old, got {old}");
        }

        [Fact]
        public void StratifiedSample_RedistributesWhenStrataEmpty()
        {
            // Person inactive for a year: budget must still fill from older eras.
            var now = DateTime.UtcNow;
            var msgs = Enumerable.Range(0, 500).Select(i => ($"old{i}", now.AddDays(-400 - i))).ToList();
            var sample = PersonalityProfiler.StratifiedSample(msgs, 100);
            Assert.Equal(100, sample.Count);
        }

        [Fact]
        public void DensityScore_InfoRichWindowBeatsMemeWindow()
        {
            static List<WindowMessage> Window(params string[] contents) =>
                contents.Select((c, i) => new WindowMessage { Number = i + 1, Content = c, SentAt = DateTime.UtcNow }).ToList();

            var dense = Window(
                "my brother Jake is coming home from uni",
                "i'm 17 btw, born in 2008",
                "I live in Leeds, moved there last year",
                "my mum works at the school");
            var memes = Window("lmaooo", "💀💀💀", "real", "no way", "fr fr");

            Assert.True(ExtractionJob.DensityScore(dense) >= 6);
            Assert.True(ExtractionJob.DensityScore(memes) < 6);
            Assert.True(ExtractionJob.DensityScore(dense) > ExtractionJob.DensityScore(memes));
        }

        [Fact]
        public void StyleMatchScore_IdenticalStyleScoresHigh_MismatchScoresLow()
        {
            double same = ReplicaFidelity.StyleMatchScore("yeah lol that was mad", "yeah lol so mad innit");
            double mismatch = ReplicaFidelity.StyleMatchScore(
                "yeah lol",
                "Indeed, that situation was rather amusing. I found it quite entertaining overall, and I would certainly be happy to discuss it further.");
            Assert.True(same > mismatch);
            Assert.True(same > 0.7, $"same-style score too low: {same}");
            Assert.True(mismatch < 0.5, $"mismatch score too high: {mismatch}");
        }
    }
}
