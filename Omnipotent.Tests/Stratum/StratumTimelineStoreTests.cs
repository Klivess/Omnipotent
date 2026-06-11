using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.Stratum;

namespace Omnipotent.Tests.Stratum
{
    /// <summary>
    /// Timeline store tests run against the test bin's SavedData directory (OmniPaths roots
    /// under AppDomain.BaseDirectory). Each test uses a unique project ID so runs are isolated.
    /// </summary>
    public class StratumTimelineStoreTests
    {
        private static StratumTimelineStore NewStore() => new(_ => { });
        private static string NewProjectId() => "test_" + Guid.NewGuid().ToString("N");

        [Fact]
        public void Append_AssignsMonotonicSequences()
        {
            var store = NewStore();
            string pid = NewProjectId();
            var e1 = store.Append(new StratumTimelineEvent { ProjectID = pid, Type = StratumTimelineEventTypes.UserMessage, Author = "user", Text = "one" });
            var e2 = store.Append(new StratumTimelineEvent { ProjectID = pid, Type = StratumTimelineEventTypes.AgentMessage, Author = "agent", Text = "two" });
            var e3 = store.Append(new StratumTimelineEvent { ProjectID = pid, Type = StratumTimelineEventTypes.Thought, Author = "agent", Text = "three" });
            Assert.Equal(1, e1.Sequence);
            Assert.Equal(2, e2.Sequence);
            Assert.Equal(3, e3.Sequence);
            Assert.Equal(3, store.GetLastSequence(pid));
        }

        [Fact]
        public void ReadSince_ReturnsOnlyNewerEvents()
        {
            var store = NewStore();
            string pid = NewProjectId();
            store.Append(new StratumTimelineEvent { ProjectID = pid, Type = "user-message", Text = "a" });
            store.Append(new StratumTimelineEvent { ProjectID = pid, Type = "agent-message", Text = "b" });
            store.Append(new StratumTimelineEvent { ProjectID = pid, Type = "agent-message", Text = "c" });
            var since1 = store.ReadSince(pid, 1);
            Assert.Equal(2, since1.Count);
            Assert.Equal("b", since1[0].Text);
            Assert.Equal("c", since1[1].Text);
            Assert.Empty(store.ReadSince(pid, 3));
        }

        [Fact]
        public void SequenceCounter_SurvivesStoreRestart()
        {
            string pid = NewProjectId();
            NewStore().Append(new StratumTimelineEvent { ProjectID = pid, Type = "user-message", Text = "a" });
            // A fresh store instance must rescan the JSONL and continue the sequence.
            var e2 = NewStore().Append(new StratumTimelineEvent { ProjectID = pid, Type = "agent-message", Text = "b" });
            Assert.Equal(2, e2.Sequence);
        }

        [Fact]
        public void Meta_RoundTrips()
        {
            var store = NewStore();
            string pid = NewProjectId();
            var meta = store.GetMeta(pid);
            meta.RollingSummary = "the user wants a bolted box";
            meta.ActiveTurnID = "turn123";
            meta.LastCompactedSequence = 17;
            store.SaveMeta(meta);
            var loaded = NewStore().GetMeta(pid);
            Assert.Equal("the user wants a bolted box", loaded.RollingSummary);
            Assert.Equal("turn123", loaded.ActiveTurnID);
            Assert.Equal(17, loaded.LastCompactedSequence);
        }

        [Fact]
        public void OversizedPayload_IsTruncatedNotRejected()
        {
            var store = NewStore();
            string pid = NewProjectId();
            var evt = store.Append(new StratumTimelineEvent
            {
                ProjectID = pid,
                Type = "tool-result",
                Text = "big",
                PayloadJson = new string('x', 100_000),
            });
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(evt.PayloadJson!) <= 33 * 1024);
            Assert.EndsWith("…(truncated)", evt.PayloadJson);
        }

        [Fact]
        public void LegacyChat_IsImportedOnFirstAccess()
        {
            string pid = NewProjectId();
            string root = OmniPaths.GetPath(OmniPaths.GlobalPaths.StratumConversationsDirectory);
            Directory.CreateDirectory(root);
            var legacy = new
            {
                Conversation = new StratumConversation { ConversationID = "c1", ProjectID = pid, AgentRole = StratumAgentRoles.MechanicalEngineer, NextSequence = 3 },
                Messages = new[]
                {
                    new StratumChatMessage { MessageID = "m1", ProjectID = pid, Author = "user", Text = "hello engineer", Sequence = 1, CreatedAt = DateTime.UtcNow.AddMinutes(-2) },
                    new StratumChatMessage { MessageID = "m2", ProjectID = pid, Author = "agent", Text = "hello back", Sequence = 2, CreatedAt = DateTime.UtcNow.AddMinutes(-1) },
                },
            };
            File.WriteAllText(Path.Combine(root, $"{pid}_{StratumAgentRoles.MechanicalEngineer}.json"), JsonConvert.SerializeObject(legacy));

            var store = NewStore();
            var events = store.ReadSince(pid, 0);
            Assert.Equal(2, events.Count);
            Assert.Equal(StratumTimelineEventTypes.UserMessage, events[0].Type);
            Assert.Equal("hello engineer", events[0].Text);
            Assert.Equal(StratumTimelineEventTypes.AgentMessage, events[1].Type);

            // New appends continue after the imported history.
            var next = store.Append(new StratumTimelineEvent { ProjectID = pid, Type = "user-message", Text = "new era" });
            Assert.Equal(3, next.Sequence);
        }
    }
}
