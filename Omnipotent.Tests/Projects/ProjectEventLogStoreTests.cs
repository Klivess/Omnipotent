using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Event log tests run against the test bin's SavedData directory (OmniPaths roots under
    /// AppDomain.BaseDirectory). Each test uses a unique project ID so runs are isolated.
    /// The concurrency test is the one Stratum never needed: Projects' log is multi-writer.
    /// </summary>
    public class ProjectEventLogStoreTests
    {
        private static ProjectEventLogStore NewStore() => new(_ => { });
        private static string NewProjectId() => "test_" + Guid.NewGuid().ToString("N");

        [Fact]
        public void Append_AssignsMonotonicSequences()
        {
            var store = NewStore();
            string pid = NewProjectId();
            var e1 = store.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.KlivesMessage, Author = "klives", Text = "one" });
            var e2 = store.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.CommanderMessage, Author = "commander", Text = "two" });
            var e3 = store.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.CommanderThought, Author = "commander", Text = "three" });
            Assert.Equal(1, e1.Sequence);
            Assert.Equal(2, e2.Sequence);
            Assert.Equal(3, e3.Sequence);
            Assert.Equal(3, store.GetLastSequence(pid));
        }

        [Fact]
        public void ReadSince_ReturnsOnlyNewerEvents_InOrder()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = "a" });
            store.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = "b" });
            store.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = "c" });
            var since1 = store.ReadSince(pid, 1);
            Assert.Equal(2, since1.Count);
            Assert.Equal("b", since1[0].Text);
            Assert.Equal("c", since1[1].Text);
            Assert.Empty(store.ReadSince(pid, 3));
        }

        [Fact]
        public void ReadTail_ReturnsMostRecentEvents_InAscendingOrder()
        {
            // Regression guard for the "workspace opened on days-old history" bug: the initial backlog
            // load asks for the tail, which must be the NEWEST `count` events (not the oldest), in
            // ascending order, and its last sequence must equal GetLastSequence so the client cursor
            // and the displayed events agree (otherwise everything between is silently skipped).
            var store = NewStore();
            string pid = NewProjectId();
            for (int i = 1; i <= 20; i++)
                store.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = $"e{i}" });

            var tail = store.ReadTail(pid, 5);
            Assert.Equal(5, tail.Count);
            Assert.Equal("e16", tail[0].Text);
            Assert.Equal("e20", tail[^1].Text);
            Assert.Equal(store.GetLastSequence(pid), tail[^1].Sequence);

            // Asking for more than exist returns them all, still oldest→newest.
            var allTail = store.ReadTail(pid, 500);
            Assert.Equal(20, allTail.Count);
            Assert.Equal("e1", allTail[0].Text);
            Assert.Equal("e20", allTail[^1].Text);
        }

        [Fact]
        public void SequenceCounter_SurvivesStoreRestart()
        {
            string pid = NewProjectId();
            NewStore().Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = "a" });
            // A fresh store instance must rescan the JSONL and continue the sequence.
            var e2 = NewStore().Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = "b" });
            Assert.Equal(2, e2.Sequence);
        }

        [Fact]
        public void Append_IsolatesCrashPartialTail_AndKeepsFirstPostRestartEventReadable()
        {
            string pid = NewProjectId();
            string dir = Omnipotent.Data_Handling.OmniPaths.GetPath(
                Omnipotent.Data_Handling.OmniPaths.GlobalPaths.ProjectsEventLogDirectory);
            string path = Path.Combine(dir, pid + ".log.jsonl");
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{\"ProjectID\":\"interrupted");
            try
            {
                var store = NewStore();
                var appended = store.Append(new ProjectEvent
                {
                    ProjectID = pid, Type = ProjectEventTypes.Status, Text = "survived restart",
                });

                Assert.Equal(1, appended.Sequence);
                var read = Assert.Single(store.ReadSince(pid, 0));
                Assert.Equal("survived restart", read.Text);
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public async Task ConcurrentMultiWriterAppends_ProduceUniqueContiguousSequences()
        {
            // The Projects log is multi-writer: Commander + sub-agents + stimulus bus all
            // append concurrently. Sequences must come out unique and contiguous, and every
            // line must be intact JSON (no interleaved writes).
            var store = NewStore();
            string pid = NewProjectId();
            const int writers = 8;
            const int perWriter = 50;

            var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
            {
                for (int i = 0; i < perWriter; i++)
                {
                    store.Append(new ProjectEvent
                    {
                        ProjectID = pid,
                        Type = ProjectEventTypes.ToolResult,
                        Author = "agent",
                        AgentID = $"agent{w}",
                        Text = $"writer {w} event {i}",
                    });
                }
            })).ToArray();
            await Task.WhenAll(tasks);

            var all = store.ReadSince(pid, 0, max: writers * perWriter + 10);
            Assert.Equal(writers * perWriter, all.Count);
            var sequences = all.Select(e => e.Sequence).ToList();
            Assert.Equal(sequences.OrderBy(s => s).ToList(), sequences); // ascending read order
            Assert.Equal(writers * perWriter, sequences.Distinct().Count()); // unique
            Assert.Equal(1, sequences.Min());
            Assert.Equal(writers * perWriter, sequences.Max()); // contiguous
        }

        [Fact]
        public void OversizedPayload_IsTruncatedNotRejected()
        {
            var store = NewStore();
            string pid = NewProjectId();
            var evt = store.Append(new ProjectEvent
            {
                ProjectID = pid,
                Type = ProjectEventTypes.ToolResult,
                Text = "big",
                PayloadJson = new string('x', 100_000),
            });
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(evt.PayloadJson!) <= 33 * 1024);
            Assert.EndsWith("…(truncated)", evt.PayloadJson);
        }

        [Fact]
        public void EventAppended_FiresForSubscribers()
        {
            var store = NewStore();
            string pid = NewProjectId();
            ProjectEvent? seen = null;
            store.EventAppended += e => seen = e;
            store.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = "ping" });
            Assert.NotNull(seen);
            Assert.Equal("ping", seen!.Text);
            Assert.Equal(1, seen.Sequence);
        }
    }
}
