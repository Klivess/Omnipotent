using Omnipotent.Data_Handling;
using Omnipotent.Services.Projects;
using LlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.DataHandling
{
    /// <summary>
    /// Temporal grounding: every message an agent reads and every event/memory/artifact rendering
    /// carries a readable UTC timestamp. These tests pin the shared format and the key surfaces
    /// (LLM transport stamps, wake-seed clock, event lines) so the guarantee can't silently regress.
    /// </summary>
    public class TemporalFormatTests
    {
        private static readonly DateTime KnownUtc = new(2026, 7, 12, 18, 4, 33, DateTimeKind.Utc);

        [Fact]
        public void Stamp_RendersUtcWithExplicitSuffix()
        {
            Assert.Equal("2026-07-12 18:04:33 UTC", TemporalFormat.Stamp(KnownUtc));
            Assert.Equal("2026-07-12 18:04 UTC", TemporalFormat.StampMinute(KnownUtc));
        }

        [Fact]
        public void Stamp_TreatsUnspecifiedKindAsUtc_NotLocal()
        {
            var unspecified = DateTime.SpecifyKind(KnownUtc, DateTimeKind.Unspecified);
            Assert.Equal(TemporalFormat.Stamp(KnownUtc), TemporalFormat.Stamp(unspecified));
        }

        [Theory]
        [InlineData(0, "just now")]
        [InlineData(59, "just now")]
        [InlineData(5 * 60, "5m ago")]
        [InlineData(3 * 3600 + 20 * 60, "3h 20m ago")]
        [InlineData(12 * 86400 + 4 * 3600, "12d 4h ago")]
        public void Age_ScalesHumanReadably(int secondsAgo, string expected)
        {
            var now = KnownUtc;
            Assert.Equal(expected, TemporalFormat.Age(now.AddSeconds(-secondsAgo), now));
        }

        [Fact]
        public void Age_FutureInstants_RenderAsIn()
        {
            var now = KnownUtc;
            Assert.Equal("in 5m", TemporalFormat.Age(now.AddMinutes(5), now));
        }

        [Fact]
        public void ClockLine_CarriesWeekdayAndUtcMarker()
        {
            var line = TemporalFormat.ClockLine();
            Assert.EndsWith(" UTC", line);
            Assert.Contains(DateTime.UtcNow.DayOfWeek.ToString(), line);
        }

        [Fact]
        public void LlmTransport_StampsIncomingMessagesWithCurrentUtc()
        {
            string stamped = LlmService.StampIncoming("hello");
            Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} UTC\] hello$", stamped);
        }

        [Fact]
        public void CommanderEventLines_CarryFullDateStamps()
        {
            var line = ProjectCommanderPrompts.DescribeEvent(new ProjectEvent
            {
                Sequence = 7,
                Timestamp = KnownUtc,
                Type = ProjectEventTypes.Status,
                Author = "commander",
                Text = "checkpoint reached",
            });
            Assert.Contains("2026-07-12 18:04", line);
        }

        [Fact]
        public void CommanderWakeSeed_AnchorsOnCurrentClockAndProjectAge()
        {
            var project = new Project
            {
                ProjectID = "p1",
                Name = "Test",
                Goal = "Do the thing",
                CreatedAt = DateTime.UtcNow.AddDays(-3),
            };
            string seed = ProjectCommanderPrompts.BuildWakeSeed(
                project,
                new ProjectDigest { ProjectID = "p1" },
                new List<ProjectEvent>(),
                new List<ProjectRetrievalIndex.RetrievalHit>(),
                "keepalive");

            Assert.Contains("Now: ", seed);
            Assert.Contains("UTC", seed);
            Assert.Contains("project created", seed);
            Assert.Contains("ago", seed);
        }
    }
}
