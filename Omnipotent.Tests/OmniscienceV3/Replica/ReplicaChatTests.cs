using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Replica;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Tests.OmniscienceV3.Replica
{
    /// <summary>Replica chat (F285–F309, F325/F326): durable turns, failure marking, superseding, cancellation, regeneration,
    /// idempotent re-runs, bounded memory, MinorGuard and migrated v2 chats.</summary>
    public class ReplicaChatTests
    {
        private static async Task<(ReplicaHarness H, string ChatId)> ChatAsync(Action<ReplicaHarness>? configure = null)
        {
            var h = await ReplicaHarness.StartAsync(configure: configure);
            await h.TrainAsync(Personas.Lena);
            var chat = await h.R.Chat.CreateAsync(new ReplicaChatCreateRequest(Personas.Lena, Title: "test"), "tester", CancellationToken.None);
            return (h, chat.ChatId);
        }

        [Fact]
        public async Task Send_StoresTheReplyBurstAtomicallyAndLabelsItSimulated()
        {
            var (h, chatId) = await ChatAsync();
            await using var _ = h;
            var (turnId, seq) = await h.R.Chat.SendAsync(chatId, "hey how are you", "tester", CancellationToken.None);
            var pending = h.R.Chat.Chats.Lines(chatId).Single(l => l.Seq == seq);
            Assert.Equal(TurnStatuses.Pending, pending.Meta.Status); // stored before any generation (F306)
            await h.DrainAsync();
            var lines = h.R.Chat.Chats.Lines(chatId);
            var user = lines.Single(l => l.Seq == seq);
            Assert.Equal(TurnStatuses.Answered, user.Meta.Status);
            var replies = lines.Where(l => l.Role == "assistant").ToList();
            Assert.NotEmpty(replies);
            Assert.All(replies, l => { Assert.Equal(turnId, l.Meta.TurnId); Assert.True(l.Meta.Simulated); Assert.NotNull(l.Meta.VersionId); });
            Assert.Equal(replies.Count, replies[0].Meta.BurstSize);
            Assert.True(ChatStore.Dto(chatId, replies[0]).Simulated);
            using var c = h.Runtime.Stores.Intel.OpenRead();
            Assert.Equal(1L, c.Scalar<long>(null, "SELECT COUNT(*) FROM replica_generations WHERE turn_id=$t AND outcome='ok'", ("$t", turnId)));
            // The chat used the interactive work class and a cached prefix.
            Assert.Contains(h.Llm.Requests, r => r.Purpose == "replica.compose" && r.WorkClass == Omnipotent.Services.KliveLLM.AIRouterWorkClass.Interactive);
        }

        [Fact]
        public async Task Failure_MarksTheUserTurnFailedInsteadOfOrphaningIt()
        {
            var (h, chatId) = await ChatAsync();
            await using var _ = h;
            h.Llm.FailWhen = r => r.Purpose == "replica.compose";
            var (turnId, seq) = await h.R.Chat.SendAsync(chatId, "you there?", "tester", CancellationToken.None);
            await h.DrainAsync();
            var user = h.R.Chat.Chats.Lines(chatId).Single(l => l.Seq == seq);
            Assert.Equal(TurnStatuses.Failed, user.Meta.Status);
            Assert.False(string.IsNullOrEmpty(user.Meta.Error));
            Assert.DoesNotContain(h.R.Chat.Chats.Lines(chatId), l => l.Role == "assistant");
            Assert.Equal(TurnStatuses.Failed, h.R.Chat.Turn(chatId, turnId).Status);
            Assert.Equal(JobStates.Dead, h.Runtime.Host.Jobs.Query(new JobQuery { RunId = turnId }).Items.Single().State);
        }

        [Fact]
        public async Task SendingAgain_FoldsIntoOneReply()
        {
            var (h, chatId) = await ChatAsync();
            await using var _ = h;
            var (first, _) = await h.R.Chat.SendAsync(chatId, "wait", "tester", CancellationToken.None);
            var (second, _) = await h.R.Chat.SendAsync(chatId, "did you see the match?", "tester", CancellationToken.None);
            Assert.All(h.R.Chat.Chats.Lines(chatId).Where(l => l.Role == "user"), l => Assert.Equal(second, l.Meta.TurnId));
            int before = h.Llm.Requests.Count;
            await h.DrainAsync();
            var compose = h.Llm.Requests.Skip(before).First(r => r.Purpose == "replica.compose");
            string last = compose.Turns[^1].Content;
            Assert.Contains("wait", last);
            Assert.Contains("did you see the match?", last);
            var replies = h.R.Chat.Chats.Lines(chatId).Where(l => l.Role == "assistant").ToList();
            Assert.All(replies, l => Assert.Equal(second, l.Meta.TurnId));
            Assert.Equal(TurnStatuses.Superseded, h.R.Chat.Turn(chatId, first).Status);
        }

        [Fact]
        public async Task Cancel_StopsTheTurnBeforeItIsAnswered()
        {
            var (h, chatId) = await ChatAsync();
            await using var _ = h;
            var (turnId, seq) = await h.R.Chat.SendAsync(chatId, "hello", "tester", CancellationToken.None);
            Assert.True(await h.R.Chat.CancelAsync(chatId, "client left", CancellationToken.None));
            await h.DrainAsync();
            Assert.Equal(TurnStatuses.Cancelled, h.R.Chat.Chats.Lines(chatId).Single(l => l.Seq == seq).Meta.Status);
            Assert.DoesNotContain(h.R.Chat.Chats.Lines(chatId), l => l.Role == "assistant");
            Assert.Equal(JobStates.Cancelled, h.Runtime.Host.Jobs.Query(new JobQuery { RunId = turnId }).Items.Single().State);
        }

        [Fact]
        public async Task Regenerate_ReplacesTheReplyButKeepsTheOldOneInTheTranscript()
        {
            var (h, chatId) = await ChatAsync();
            await using var _ = h;
            var (turnId, _) = await h.R.Chat.SendAsync(chatId, "pizza or sushi tonight?", "tester", CancellationToken.None);
            await h.DrainAsync();
            string newTurn = await h.R.Chat.RegenerateAsync(chatId, "tester", CancellationToken.None);
            await h.DrainAsync();
            var lines = h.R.Chat.Chats.Lines(chatId);
            Assert.All(lines.Where(l => l.Role == "assistant" && l.Meta.TurnId == turnId), l => Assert.Equal(TurnStatuses.Replaced, l.Meta.Status));
            Assert.Contains(lines, l => l.Role == "assistant" && l.Meta.TurnId == newTurn && l.Meta.Status == TurnStatuses.Answered);
            // The next turn's history leaves the replaced reply out.
            int before = h.Llm.Requests.Count;
            await h.R.Chat.SendAsync(chatId, "ok", "tester", CancellationToken.None);
            await h.DrainAsync();
            var compose = h.Llm.Requests.Skip(before).First(r => r.Purpose == "replica.compose");
            var replaced = lines.First(l => l.Role == "assistant" && l.Meta.TurnId == turnId);
            Assert.DoesNotContain(compose.Turns.Skip(1), t => t.Role == "assistant" && t.Content.Contains(replaced.Content) && !lines.Any(n => n.Meta.TurnId == newTurn && n.Content == replaced.Content));
        }

        [Fact]
        public async Task ReRunningAnAnsweredTurn_NeverAnswersTwice()
        {
            var (h, chatId) = await ChatAsync();
            await using var _ = h;
            var (turnId, _) = await h.R.Chat.SendAsync(chatId, "hey", "tester", CancellationToken.None);
            await h.DrainAsync();
            int replies = h.R.Chat.Chats.Lines(chatId).Count(l => l.Role == "assistant");
            // A crash after the reply was stored but before the job completed: the job runs again.
            await h.Runtime.Host.Jobs.EnqueueAsync(new JobRequest
            {
                ModuleId = ReplicaSchema.ModuleId, Kind = ReplicaChatOrchestrator.KindTurn, SubjectKind = "replica_chat", SubjectId = chatId, Lane = Lane.LlmInteractive,
                PayloadJson = RJson.Write(new { turnId }), RunId = turnId,
            });
            // The user line is pending again in the crash scenario.
            await h.R.Chat.Chats.PatchAsync(chatId, l => l.Role == "user" && l.Meta.TurnId == turnId, new { status = TurnStatuses.Pending }, CancellationToken.None);
            await h.DrainAsync();
            Assert.Equal(replies, h.R.Chat.Chats.Lines(chatId).Count(l => l.Role == "assistant"));
            Assert.Equal(TurnStatuses.Answered, h.R.Chat.Chats.Lines(chatId).First(l => l.Role == "user").Meta.Status);
        }

        [Fact]
        public async Task LongChats_SummariseOnlyThroughLinesTheyIncluded()
        {
            var (h, chatId) = await ChatAsync();
            await using var _ = h;
            long at = h.Clock.NowMs;
            var history = new List<(string, string, object)>();
            for (int i = 0; i < 400; i++)
                history.Add((i % 2 == 0 ? "user" : "assistant", $"line {i:D3} " + new string('x', 120), new { turnId = "old" + (i / 2), status = TurnStatuses.Answered, simulated = i % 2 == 1 }));
            await h.R.Chat.Chats.AppendAsync(chatId, history, at, CancellationToken.None);
            int before = h.Llm.Requests.Count;
            await h.R.Chat.SendAsync(chatId, "so what now", "tester", CancellationToken.None);
            await h.DrainAsync();
            var (summary, through) = new ChatMemory(h.R.Services).Load(chatId);
            Assert.NotNull(summary);
            Assert.True(through > 0 && through < 400, $"through {through}");
            var compose = h.Llm.Requests.Skip(before).First(r => r.Purpose == "replica.compose");
            string text = ScriptedLlm.FullText(compose);
            Assert.Contains("[Earlier in this chat, summarised:", text);
            // No gap: the first line after the summary's watermark is shown verbatim, the newest too.
            var lines = h.R.Chat.Chats.Lines(chatId);
            string firstUncovered = lines.First(l => l.Seq == through + 1).Content;
            Assert.Contains(firstUncovered, text);
            Assert.Contains("line 399", text);
            Assert.DoesNotContain(lines.First(l => l.Seq == 1).Content, text);
        }

        [Fact]
        public async Task Minors_GetNoChat()
        {
            await using var h = await ReplicaHarness.StartAsync();
            await Assert.ThrowsAsync<ReplicaRefusedException>(() => h.R.Chat.CreateAsync(new ReplicaChatCreateRequest(Personas.Max), "tester", CancellationToken.None));
            await h.TrainAsync(Personas.Sofia);
            var chat = await h.R.Chat.CreateAsync(new ReplicaChatCreateRequest(Personas.Sofia), "tester", CancellationToken.None);
            await h.Runtime.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO person_settings(person_id, minor_marked, updated_at_ms) VALUES($p,1,0)", ("$p", Personas.Sofia)));
            var refused = await Assert.ThrowsAsync<ReplicaRefusedException>(() => h.R.Chat.SendAsync(chat.ChatId, "hola", "tester", CancellationToken.None));
            Assert.Contains("under 18", refused.Message);
        }

        [Fact]
        public void AdultFrom_FindsWhenSomeoneStoppedBeingAMinor()
        {
            long eighteen = Personas.Start + 200L * 86_400_000L;
            var oracle = new TimeOracle(at => at < eighteen ? Omnipotent.Services.OmniscienceV3.Knowledge.Policy.AgeBand.Minor : Omnipotent.Services.OmniscienceV3.Knowledge.Policy.AgeBand.Adult);
            var (restricted, adultFrom) = People.AdultFrom(oracle, "p", Personas.Start, Personas.End);
            Assert.False(restricted);
            Assert.InRange(adultFrom!.Value, eighteen, eighteen + 86_400_000L);
            var always = new TimeOracle(_ => Omnipotent.Services.OmniscienceV3.Knowledge.Policy.AgeBand.Minor);
            Assert.True(People.AdultFrom(always, "p", Personas.Start, Personas.End).RestrictedAtEnd);
            var never = new TimeOracle(_ => Omnipotent.Services.OmniscienceV3.Knowledge.Policy.AgeBand.Unknown);
            Assert.Null(People.AdultFrom(never, "p", Personas.Start, Personas.End).AdultFromMs);
        }

        [Fact]
        public async Task MigratedV2Chat_ContinuesWithItsSummaryAndKeepsV2Metadata()
        {
            var (h, _) = await ChatAsync();
            await using var _ = h;
            await h.Runtime.Stores.Operator.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO replica_chats(chat_id, person_id, title, context_json, archived, created_at_ms, updated_at_ms) VALUES('v2-chat:7',$p,'old','{\"contextKind\":\"dm\",\"summary\":\"they planned a trip to the coast\"}',0,1,1)",
                    ("$p", Personas.Lena));
                c.Exec(tx, "INSERT INTO replica_chat_messages(chat_id, seq, role, content, meta_json, at_ms) VALUES('v2-chat:7',1,'user','hi there','{\"v2MessageId\":11}',1)");
                c.Exec(tx, "INSERT INTO replica_chat_messages(chat_id, seq, role, content, meta_json, at_ms) VALUES('v2-chat:7',2,'assistant','heyy','{\"v2MessageId\":12,\"model\":\"old\"}',2)");
            });
            int before = h.Llm.Requests.Count;
            await h.R.Chat.SendAsync("v2-chat:7", "so are we going?", "tester", CancellationToken.None);
            await h.DrainAsync();
            Assert.Contains(h.R.Chat.Chats.Lines("v2-chat:7"), l => l.Role == "assistant" && l.Seq > 2);
            Assert.Contains("they planned a trip to the coast", ScriptedLlm.FullText(h.Llm.Requests.Skip(before).First(r => r.Purpose == "replica.compose")));
            await h.R.Chat.RateAsync(new ReplicaChatRateRequest("v2-chat:7", 2, 1, "good"), "tester", CancellationToken.None);
            var rated = h.R.Chat.Chats.Lines("v2-chat:7").Single(l => l.Seq == 2);
            Assert.Equal(1, rated.Meta.Rating);
            Assert.Contains("\"v2MessageId\":12", rated.MetaJson);
            using var c = h.Runtime.Stores.Operator.OpenRead();
            Assert.Equal(1L, c.Scalar<long>(null, "SELECT COUNT(*) FROM gold_labels WHERE kind='replica.reply_rating' AND superseded_at_ms IS NULL"));
        }

        private sealed class TimeOracle : Omnipotent.Services.OmniscienceV3.Knowledge.Policy.IPolicyOracle
        {
            private readonly Func<long, Omnipotent.Services.OmniscienceV3.Knowledge.Policy.AgeBand> band;
            public TimeOracle(Func<long, Omnipotent.Services.OmniscienceV3.Knowledge.Policy.AgeBand> band) => this.band = band;
            public Omnipotent.Services.OmniscienceV3.Knowledge.Policy.AgeBand BandOf(string personId, long atMs) => band(atMs);
            public string Canonical(string personId) => personId;
        }
    }
}
