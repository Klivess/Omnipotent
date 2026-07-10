using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Council store: adversarial deliberation transcripts, one file per project. Councils count
    /// against per-wake and per-day caps, read from CountForWake/CountToday.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class ProjectCouncilStoreTests
    {
        private static ProjectCouncilStore NewStore() => new(_ => { });
        private static string NewProjectId() => "test_" + Guid.NewGuid().ToString("N");

        private static CouncilSession NewSession(string pid, string? wakeID = "w1") => new()
        {
            ProjectID = pid,
            WakeID = wakeID,
            Topic = "Should we pivot?",
            Briefing = "context",
            Roles = new List<string> { "Strategist", "Skeptic" },
            Model = "test/model",
        };

        [Fact]
        public void Create_AssignsId_AndRoundTrips()
        {
            var store = NewStore();
            string pid = NewProjectId();
            var created = store.Create(NewSession(pid));
            Assert.NotEmpty(created.CouncilID);

            var fetched = store.Get(pid, created.CouncilID)!;
            Assert.Equal("Should we pivot?", fetched.Topic);
            Assert.Equal(CouncilStatus.Running, fetched.Status);
            Assert.Equal(2, fetched.Roles.Count);
        }

        [Fact]
        public void Update_OverwritesSameId()
        {
            var store = NewStore();
            string pid = NewProjectId();
            var s = store.Create(NewSession(pid));
            s.Status = CouncilStatus.Completed;
            s.VerdictText = "Do it.";
            s.Statements.Add(new CouncilStatement { Role = "Chair", Round = 3, Text = "Do it." });
            store.Update(s);

            var fetched = store.Get(pid, s.CouncilID)!;
            Assert.Equal(CouncilStatus.Completed, fetched.Status);
            Assert.Equal("Do it.", fetched.VerdictText);
            Assert.Single(fetched.Statements);
            Assert.Single(store.List(pid)); // update, not append
        }

        [Fact]
        public void CountForWake_And_CountToday()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.Create(NewSession(pid, "wakeA"));
            store.Create(NewSession(pid, "wakeA"));
            store.Create(NewSession(pid, "wakeB"));

            Assert.Equal(2, store.CountForWake(pid, "wakeA"));
            Assert.Equal(1, store.CountForWake(pid, "wakeB"));
            Assert.Equal(0, store.CountForWake(pid, null));
            Assert.Equal(3, store.CountToday(pid));
        }

        [Fact]
        public void List_NewestFirst()
        {
            var store = NewStore();
            string pid = NewProjectId();
            var a = store.Create(NewSession(pid));
            Thread.Sleep(5);
            var b = store.Create(NewSession(pid));
            var list = store.List(pid);
            Assert.Equal(b.CouncilID, list[0].CouncilID);
            Assert.Equal(a.CouncilID, list[1].CouncilID);
        }

        [Fact]
        public void SurvivesNewStoreInstance()
        {
            string pid = NewProjectId();
            var created = NewStore().Create(NewSession(pid));
            Assert.NotNull(NewStore().Get(pid, created.CouncilID));
        }

        [Fact]
        public async Task ConcurrentCreates_AllPersisted()
        {
            var store = NewStore();
            string pid = NewProjectId();
            const int n = 20;
            var tasks = Enumerable.Range(0, n).Select(i => Task.Run(() => store.Create(NewSession(pid, $"w{i}")))).ToArray();
            await Task.WhenAll(tasks);
            Assert.Equal(n, store.List(pid).Count);
        }
    }
}
