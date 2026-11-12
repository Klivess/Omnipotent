using Omnipotent.Services.OmniTrader.Research.Alpha;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// A paper that was being read when the process stopped. Three Alpha Lab batches were cut by a
    /// restart between 24 Oct and 6 Nov 2026; each used up one of the two attempts their papers get.
    /// </summary>
    public class AlphaLabRestartTests
    {
        private static ResearchPaper Reading(int attempts, int refunds = 0) =>
            new() { Id = "p1", Status = PaperStatus.Analysing, Attempts = attempts, RestartRefunds = refunds };

        [Fact]
        public void RestartDuringARead_GivesTheAttemptBack()
        {
            var paper = Reading(attempts: 1);
            AlphaLab.RequeueAfterRestart(paper);
            Assert.Equal(PaperStatus.Queued, paper.Status);
            Assert.Equal(0, paper.Attempts);
            Assert.Equal(1, paper.RestartRefunds);
            Assert.Null(paper.Verdict);
        }

        [Fact]
        public void TwoRestarts_NoLongerCloseAPaperNobodyRead()
        {
            // First batch: sent (attempt 1), restart. Second batch: sent (attempt 1 again), restart.
            var paper = Reading(attempts: 1);
            AlphaLab.RequeueAfterRestart(paper);
            paper.Status = PaperStatus.Analysing;
            paper.Attempts++;
            AlphaLab.RequeueAfterRestart(paper);

            Assert.Equal(PaperStatus.Queued, paper.Status);
            Assert.Equal(0, paper.Attempts);
            Assert.Equal(2, paper.RestartRefunds);
        }

        [Fact]
        public void RefundsAreBounded_SoAPaperThatKeepsDyingWithTheProcessStillEnds()
        {
            var paper = Reading(attempts: 1);
            for (int restart = 0; restart < 10 && paper.Status != PaperStatus.Analysed; restart++)
            {
                AlphaLab.RequeueAfterRestart(paper);
                if (paper.Status != PaperStatus.Queued) break;
                paper.Status = PaperStatus.Analysing;
                paper.Attempts++;
            }

            // Three refunds, then the two ordinary attempts: closed on the fifth interrupted read.
            Assert.Equal(PaperStatus.Analysed, paper.Status);
            Assert.Equal(AlphaLab.MaxRestartRefunds, paper.RestartRefunds);
            Assert.Equal("no verdict recorded", paper.Verdict);
        }

        [Fact]
        public void AnAttemptTheAgentReallyUsed_StillCounts()
        {
            // One full attempt ended without a verdict (not a restart); the second read is interrupted.
            var paper = Reading(attempts: 2);
            AlphaLab.RequeueAfterRestart(paper);
            Assert.Equal(PaperStatus.Queued, paper.Status);
            Assert.Equal(1, paper.Attempts);
        }
    }
}
