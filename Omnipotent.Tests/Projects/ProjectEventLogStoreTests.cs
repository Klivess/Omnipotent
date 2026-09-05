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
            var writer = NewStore();
            string pid = NewProjectId();
            for (int i = 1; i <= 20; i++)
                writer.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = $"e{i}" });

            var reader = NewStore(); // cold, as after a service restart
            var tail = reader.ReadTail(pid, 5);
            Assert.Equal(5, tail.Count);
            Assert.Equal("e16", tail[0].Text);
            Assert.Equal("e20", tail[^1].Text);
            Assert.Equal(reader.GetLastSequence(pid), tail[^1].Sequence);
            Assert.Equal(0, reader.FullIndexBuilds);

            // Asking for more than exist returns them all, still oldest→newest.
            var allTail = reader.ReadTail(pid, 500);
            Assert.Equal(20, allTail.Count);
            Assert.Equal("e1", allTail[0].Text);
            Assert.Equal("e20", allTail[^1].Text);
            Assert.Equal(0, reader.FullIndexBuilds);
        }

        [Fact]
        public void ReadTail_ReassemblesRecordsAcrossReverseReadChunks()
        {
            string pid = NewProjectId();
            var writer = NewStore();
            writer.Append(new ProjectEvent
            {
                ProjectID = pid,
                Type = ProjectEventTypes.ToolResult,
                Text = "large:" + new string('x', 90_000),
            });
            writer.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = "after" });

            var reader = NewStore();
            var tail = reader.ReadTail(pid, 2);

            Assert.Equal(2, tail.Count);
            Assert.StartsWith("large:", tail[0].Text);
            Assert.Equal(90_006, tail[0].Text.Length);
            Assert.Equal("after", tail[1].Text);
            Assert.Equal(0, reader.FullIndexBuilds);
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

        /// <summary>
        /// /projects/list asks every project for its last sequence on every refresh, and every
        /// Append asks for it too. Answering from the log's tail instead of indexing the whole file
        /// is what keeps both off an O(log size) path — a long-lived project's log is unbounded.
        /// </summary>
        [Fact]
        public void GetLastSequence_DoesNotReadTheWholeLog()
        {
            string pid = NewProjectId();
            var writer = NewStore();
            for (int i = 1; i <= 300; i++)
                writer.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = $"e{i}" });

            var reader = NewStore(); // cold, as after a restart
            Assert.Equal(300, reader.GetLastSequence(pid));
            Assert.Equal(0, reader.FullIndexBuilds);

            // Appending stays on the cheap path too.
            Assert.Equal(301, reader.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = "next" }).Sequence);
            Assert.Equal(0, reader.FullIndexBuilds);
        }

        /// <summary>A current cursor stays on the reverse-tail path. A genuinely old cursor still
        /// builds the sparse index once so forward paging returns the oldest page after `since`.</summary>
        [Fact]
        public void ReadSince_AvoidsColdIndexForCurrentCursor_ButPreservesOldCursorPaging()
        {
            string pid = NewProjectId();
            var writer = NewStore();
            for (int i = 1; i <= 400; i++)
                writer.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = $"e{i}" });

            var reader = NewStore();
            reader.GetLastSequence(pid);                                     // cheap path first
            reader.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = "e401" });

            var page = reader.ReadSince(pid, 395);
            Assert.Equal(0, reader.FullIndexBuilds);
            Assert.Equal(6, page.Count);
            Assert.Equal("e396", page[0].Text);
            Assert.Equal("e401", page[^1].Text);

            // A stale cursor whose gap does not fit in one response needs the forward sparse index.
            var firstOldPage = reader.ReadSince(pid, 0, max: 10);
            Assert.Equal(10, firstOldPage.Count);
            Assert.Equal("e1", firstOldPage[0].Text);
            Assert.Equal("e10", firstOldPage[^1].Text);
            Assert.Equal(1, reader.FullIndexBuilds);

            // Later old-cursor pages reuse that same index.
            Assert.Equal("e11", reader.ReadSince(pid, 10, max: 1)[0].Text);
            Assert.Equal(1, reader.FullIndexBuilds);
        }

        [Fact]
        public void ReadRecentSince_ColdRead_ReturnsNewestWindowWithoutFullIndex()
        {
            string pid = NewProjectId();
            var writer = NewStore();
            for (int i = 1; i <= 30; i++)
                writer.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = $"e{i}" });

            var reader = NewStore();
            var recent = reader.ReadRecentSince(pid, sinceExclusive: 5, count: 5);

            Assert.Equal(new[] { "e26", "e27", "e28", "e29", "e30" }, recent.Select(e => e.Text));
            Assert.Equal(0, reader.FullIndexBuilds);
        }

        [Fact]
        public void AnalyticsRange_RetainsPriorLifecycleButDropsPriorPayloadHistory()
        {
            string pid = NewProjectId();
            var store = NewStore();
            DateTime from = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            store.Append(new ProjectEvent
            {
                ProjectID = pid,
                Timestamp = from.AddDays(-5),
                Type = ProjectEventTypes.ToolResult,
                Text = "old payload",
                PayloadJson = new string('x', 20_000),
            });
            store.Append(new ProjectEvent
            {
                ProjectID = pid,
                Timestamp = from.AddDays(-2),
                Type = ProjectEventTypes.Status,
                Author = "klives",
                Text = "Project paused by Klives",
            });
            store.Append(new ProjectEvent
            {
                ProjectID = pid,
                Timestamp = from.AddHours(1),
                Type = ProjectEventTypes.ToolCall,
                Text = "in range",
            });
            store.Append(new ProjectEvent
            {
                ProjectID = pid,
                Timestamp = from.AddDays(8),
                Type = ProjectEventTypes.ToolCall,
                Text = "after range",
            });

            var result = store.EnumerateForAnalytics(pid, from, from.AddDays(7)).ToList();

            Assert.Equal(new[] { 2L, 3L }, result.Select(item => item.Sequence));
            Assert.Equal(new[] { ProjectEventTypes.Status, ProjectEventTypes.ToolCall },
                result.Select(item => item.Type));
        }

        /// <summary>A hard stop can leave a partial trailing record. The tail read must fall back to
        /// the last intact one rather than restarting the sequence and overwriting history.</summary>
        [Fact]
        public void GetLastSequence_IgnoresAPartialTrailingRecord()
        {
            string pid = NewProjectId();
            var writer = NewStore();
            for (int i = 1; i <= 5; i++)
                writer.Append(new ProjectEvent { ProjectID = pid, Type = ProjectEventTypes.Status, Text = $"e{i}" });

            string path = Path.Combine(
                Omnipotent.Data_Handling.OmniPaths.GetPath(
                    Omnipotent.Data_Handling.OmniPaths.GlobalPaths.ProjectsEventLogDirectory),
                pid + ".log.jsonl");
            File.AppendAllText(path, "{\"ProjectID\":\"" + pid + "\",\"Sequence\":6,\"Te");

            var reader = NewStore();
            Assert.Equal(5, reader.GetLastSequence(pid));
            Assert.Equal(new[] { "e4", "e5" }, reader.ReadTail(pid, 2).Select(e => e.Text));
            Assert.Equal(0, reader.FullIndexBuilds);
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
