using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.KliveAgent.Models;
using Omnipotent.Services.KliveAgent.Talk;
using Omnipotent.Services.KliveLLM;
using LlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.KliveAgent;

/// <summary>
/// Talk mode on GPT-Live, end to end over a real browser WebSocket, with OpenAI and the agent faked:
/// the session GPT-Live is started with, a delegation turned into a KliveAgent turn read from the
/// transcript, the agent's reply handed back as commentary (code never), local commands, usage and
/// cost, and the session closed when the page goes.
/// </summary>
public sealed class KliveAgentVoiceSessionTests
{
    [Fact]
    public async Task Offer_StartsGptLive_WithTheConfiguredModelVoiceAndHistory_AndReturnsItsAnswer()
    {
        await using var h = await Harness.StartAsync();
        h.Backend.History = new List<(bool, string)> { (true, "Is the trader up?"), (false, "Yes, it is running.\n```screen\nlogs\n```") };

        var ready = await h.WaitForAsync("ready");
        Assert.Equal("gpt-live-test-9", (string?)ready["settings"]?["model"]);
        Assert.Contains("marin", ready["settings"]!["voices"]!.Values<string>());

        await h.OfferAsync(voice: "cedar");
        var answer = await h.WaitForAsync("answer");
        Assert.Equal("v=0\r\nanswer", (string?)answer["sdp"]);
        Assert.Equal("live_test", (string?)answer["sessionId"]);

        var session = h.Voice.CreatedWith!;
        Assert.Equal("gpt-live-test-9", (string?)session["model"]);
        Assert.Equal("cedar", (string?)session["audio"]?["output"]?["voice"]);
        Assert.Null(session["audio"]?["format"]); // WebRTC negotiates the format in SDP
        Assert.Equal("client", (string?)session["delegation"]?["type"]);
        Assert.Equal(450, (int?)session["delegation"]?["acknowledgement_delay_ms"]);
        Assert.Equal(20, (int?)session["idle_timeout_s"]);
        Assert.Equal(new[] { "session.input_audio.mute", "session.input_audio.unmute", "session.close" },
            session["client"]!["data_channel"]!["allowed_client_events"]!.Values<string>());
        var input = (JArray)session["input"]!;
        Assert.Equal(2, input.Count);
        Assert.Equal("assistant", (string?)input[1]["role"]);
        Assert.DoesNotContain("logs", (string?)input[1]["content"]![0]!["text"]);
        Assert.Contains("Delegate to the backend", (string?)session["instructions"]);
        await h.UntilAsync(() => h.Voice.Sideband.Attached);
    }

    [Fact]
    public async Task Delegation_QueuesWhatKlivesSaid_AsASpokenTurn()
    {
        await using var h = await Harness.StartLiveAsync();
        h.Live.User("Check", 0, 300);
        h.Live.User(" the trader", 300, 900);
        h.Live.User(" please.", 900, 1200);
        h.Live.Delegate("del_1", 1400);

        var turn = await h.WaitForAsync("turn");
        Assert.Equal("Check the trader please.", (string?)turn["text"]);
        Assert.Equal(new[] { "Check the trader please." }, h.Backend.Turns);
        Assert.StartsWith("spk_", (string?)turn["clientMessageId"]);
        Assert.Null(h.Session.TakeTurnNote()); // nothing else was said
    }

    [Fact]
    public async Task WhatWasSaidInBetween_TravelsAsContext_NotAsTheRequest()
    {
        await using var h = await Harness.StartLiveAsync();
        h.Live.User("Restart something for me.", 0, 1500);
        h.Live.Voice("Which service?", 1800, 2600);
        h.Live.User("The trader.", 3200, 3900);
        h.Live.Delegate("del_1", 4100);

        await h.WaitForAsync("turn");
        Assert.Equal(new[] { "The trader." }, h.Backend.Turns);
        string note = h.Session.TakeTurnNote()!;
        Assert.Contains("Klives: Restart something for me.", note);
        Assert.Contains("Voice: Which service?", note);
        Assert.DoesNotContain("The trader.", note);
    }

    [Fact]
    public async Task AgentReply_IsHandedToTheVoice_FirstWordsAtOnce_AndCodeNever()
    {
        await using var h = await Harness.StartLiveAsync();
        h.Live.User("How's the trader?", 0, 900);
        h.Live.Delegate("del_7", 1100);
        await h.WaitForAsync("turn");

        h.Session.OnRunStarted();
        h.Session.OnModelTurnStarted();
        h.Session.OnSpeakable("I checked the trader just now, and ");
        h.Session.OnSpeakable("it is running fine. ```screen\nsecret-token-123\n``` The last fill was ten minutes ago.");
        h.Session.OnModelTurnEnded(calledTools: false);
        h.Session.OnRunEnded(success: true);

        await h.UntilAsync(() => h.Live.Commands.Any(c => c.Type == "session.thinking.append" && c.Content.Contains("finished")));
        var said = h.Live.Commands.Where(c => c.Type == "session.commentary.append").ToList();
        Assert.NotEmpty(said);
        Assert.All(said, c => Assert.Equal("del_7", c.DelegationId));
        string all = string.Join(" ", said.Select(c => c.Content));
        Assert.StartsWith("I checked the trader just now", said[0].Content);
        Assert.Contains("ten minutes ago", all);
        Assert.DoesNotContain("secret-token-123", all);
        Assert.DoesNotContain("```", all);
    }

    [Fact]
    public async Task ToolActivity_IsSilentContext_AndAFailureWithNothingSaid_IsSpoken()
    {
        await using var h = await Harness.StartLiveAsync();
        h.Live.User("Read the logs.", 0, 900);
        h.Live.Delegate("del_2", 1100);
        await h.WaitForAsync("turn");

        h.Session.OnRunStarted();
        h.Session.OnToolStarted("running read_logs");
        h.Session.OnToolFinished("running read_logs", false);
        h.Session.OnRunEnded(success: false);

        await h.UntilAsync(() => h.Live.Commands.Any(c => c.Type == "session.commentary.append"));
        Assert.Contains(h.Live.Commands, c => c.Type == "session.thinking.append" && c.Content.Contains("read logs"));
        Assert.Contains(h.Live.Commands, c => c.Type == "session.commentary.append" && c.Content.Contains("didn't work"));
        Assert.Contains(h.Messages("tool"), m => (string?)m["phase"] == "start");
    }

    [Fact]
    public async Task Stop_CancelsTheRun_WithoutAskingTheAgent()
    {
        await using var h = await Harness.StartLiveAsync();
        h.Live.User("Check everything.", 0, 900);
        h.Live.Delegate("del_1", 1100);
        await h.WaitForAsync("turn");
        h.Session.OnRunStarted();

        h.Live.User("Stop.", 3000, 3400);
        h.Live.Delegate("del_2", 3600);

        var command = await h.WaitForAsync("command");
        Assert.Equal("stop", (string?)command["command"]);
        Assert.True((bool?)command["stoppedRun"]);
        Assert.Equal(new[] { "req-1" }, h.Backend.Cancelled);
        Assert.Single(h.Backend.Turns);
        await h.UntilAsync(() => h.Live.Commands.Any(c => c.Type == "session.commentary.append" && c.Content == "Stopped."));

        // The rest of the cancelled run stays unsaid.
        h.Session.OnSpeakable("Here is everything I found so far. ");
        h.Session.OnModelTurnEnded(false);
        await Task.Delay(400);
        Assert.DoesNotContain(h.Live.Commands, c => c.Content.Contains("everything I found"));
    }

    [Fact]
    public async Task Hush_TellsTheVoiceToStop_AndKeepsTheRestOfTheReplyUnsaid()
    {
        await using var h = await Harness.StartLiveAsync();
        h.Live.User("Tell me about the fleet.", 0, 900);
        h.Live.Delegate("del_1", 1100);
        await h.WaitForAsync("turn");
        h.Session.OnRunStarted();
        h.Session.OnSpeakable("The fleet has twelve Stewards running. ");
        await h.UntilAsync(() => h.Live.Commands.Any(c => c.Type == "session.commentary.append"));

        await h.SendAsync(new { t = "hush" });
        await h.UntilAsync(() => h.Live.Commands.Any(c => c.Type == "session.instructions.append"));
        int before = h.Live.Commands.Count(c => c.Type == "session.commentary.append");
        h.Session.OnSpeakable("Three of them are paused, and one is waiting for approval. ");
        h.Session.OnModelTurnEnded(false);
        await Task.Delay(400);
        Assert.Equal(before, h.Live.Commands.Count(c => c.Type == "session.commentary.append"));
    }

    [Fact]
    public async Task Closing_RecordsTheMinutes_AtTheConfiguredPrices()
    {
        await using var h = await Harness.StartLiveAsync();
        h.Live.Push(new JObject
        {
            ["type"] = "session.closed",
            ["event_id"] = "evt_closed",
            ["reason"] = "expired",
            ["usage"] = new JObject { ["seconds"] = 120, ["active_seconds"] = 60, ["paused_seconds"] = 60 },
        });

        var closed = await h.WaitForAsync("closed");
        Assert.Equal("expired", (string?)closed["reason"]);
        var usage = await h.WaitForAsync("usage", m => (bool?)m["final"] == true);
        Assert.Equal(0.07, (double)usage["costUsd"]!, 4);
        await h.UntilAsync(() => h.Voice.Recorded.Count == 1);
        var record = h.Voice.Recorded[0];
        Assert.True(record.Final);
        Assert.Equal("gpt-live-test-9", record.Model);
        Assert.Equal(0.07, record.Usage!.CostUsd(0.05, 0.02), 4);
    }

    [Fact]
    public async Task LeavingThePage_ClosesTheGptLiveSession_AndRecordsItOnce()
    {
        var h = await Harness.StartLiveAsync();
        await h.DisposeAsync();
        Assert.Contains(h.Live.Commands, c => c.Type == "session.close");
        Assert.Single(h.Voice.Recorded);
        Assert.True(h.Voice.Released > 0);
        Assert.Null(h.Backend.Hub.GetListener("conv1"));
    }

    [Fact]
    public async Task ASecondTab_TakesTheConversationOver()
    {
        await using var first = await Harness.StartAsync();
        await using var second = await Harness.StartAsync(first.Backend);
        await first.WaitForAsync("closed");
        Assert.Same(second.Session, first.Backend.Hub.GetListener("conv1"));
    }

    [Fact]
    public async Task ASwitchedOffOpenAIRouter_RefusesTheOffer_WithTheReason()
    {
        await using var h = await Harness.StartAsync(unavailable: "The OpenAI router is switched off on the KliveLLM page.");
        await h.OfferAsync();
        var error = await h.WaitForAsync("error");
        Assert.Contains("switched off", (string?)error["message"]);
        Assert.Null(h.Voice.CreatedWith);
    }

    [Fact]
    public async Task AnOfferThatIsNotSdp_NeverReachesOpenAI()
    {
        await using var h = await Harness.StartAsync();
        await h.SendAsync(new { t = "offer", sdp = "hello" });
        await h.WaitForAsync("error");
        Assert.Null(h.Voice.CreatedWith);
    }

    // ── Warm continuation (the agent side, unchanged by the move to GPT-Live) ──

    [Fact]
    public void Continuation_RequiresTheSameBuild_AFreshPrefix_AndAFinishedLog()
    {
        var llm = new LlmService(new HttpClient(new NeverCalled()));
        string sid = "kliveagent-talk-test-" + Guid.NewGuid().ToString("N");
        string key = KliveAgentBrain.TalkCompatibilityKey(KliveAgentComputerTarget.None, true, null);
        var epoch = new TalkEpoch(key, new List<HFWrapper.HFTool>());

        Assert.False(KliveAgentBrain.CanContinueTalk(llm, sid, epoch, key, 48_000)); // no session yet

        llm.StartToolSession(sid, "system");
        llm.AppendUserMessageToToolSession(sid, "[Talk mode] seed");
        Assert.True(KliveAgentBrain.CanContinueTalk(llm, sid, epoch, key, 48_000)); // a seed awaiting its question
        Assert.False(KliveAgentBrain.CanContinueTalk(llm, sid, null, key, 48_000));
        Assert.False(KliveAgentBrain.CanContinueTalk(llm, sid, epoch,
            KliveAgentBrain.TalkCompatibilityKey(KliveAgentComputerTarget.Container, true, null), 48_000));

        llm.AppendToolResult(sid, "call-without-a-request", "execute_csharp", "orphan");
        Assert.False(KliveAgentBrain.CanContinueTalk(llm, sid, epoch, key, 48_000)); // broken tool exchange
    }

    [Fact]
    public void TurnContext_OnlyCarriesLinesTheConversationHasNotSeen()
    {
        var epoch = KliveAgentBrain.NewTalkEpoch("k", new List<HFWrapper.HFTool>(),
            "[Rules]\n- Klives prefers British English\n");
        string raw = "[Memories & Shortcuts]\n- Klives prefers British English\n- OmniTrader trades on Trading212\n" +
                     "[Relevant Knowledge]\n- Nothing new here\n";
        epoch.InjectedContext.Add("- Nothing new here");

        string first = KliveAgentBrain.DedupeTalkContext(raw, epoch.InjectedContext).Replace("\r\n", "\n");
        Assert.Equal("[Memories & Shortcuts]\n- OmniTrader trades on Trading212", first);
        Assert.Equal(string.Empty, KliveAgentBrain.DedupeTalkContext(raw, epoch.InjectedContext));
    }

    // ── Harness ──

    internal static KliveAgentTalkSettings Settings(int progressSeconds = 0) => new(
        Enabled: true, LiveModel: "gpt-live-test-9", LiveVoice: "marin", IdleSeconds: 20, AckDelayMs: 450,
        ExtraInstructions: "", HistoryMessages: 20, ProgressSeconds: progressSeconds, ActivePerMinuteUsd: 0.05,
        StandbyPerMinuteUsd: 0.02, Thinking: "low", Model: "", Vocabulary: TalkText.DefaultVocabulary, Pronunciations: "",
        ReserveSlot: true, ReserveIdleMinutes: 3, SessionMaxTokens: 48_000);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ConcurrentQueue<JObject> messages = new();
        private readonly CancellationTokenSource stop = new();
        private TcpClient? clientTcp;
        private TcpClient? serverTcp;
        private Task? run;
        private Task? reader;
        private bool disposed;

        internal FakeBackend Backend { get; private set; } = new();
        internal FakeVoice Voice { get; private set; } = new();
        internal FakeSideband Live => Voice.Sideband;
        internal KliveAgentVoiceSession Session { get; private set; } = null!;
        internal WebSocket Client { get; private set; } = null!;

        internal static async Task<Harness> StartAsync(FakeBackend? sharedBackend = null, string? unavailable = null)
        {
            var h = new Harness();
            if (sharedBackend != null) h.Backend = sharedBackend;
            h.Voice.UnavailableReason = unavailable;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            h.clientTcp = new TcpClient();
            var connect = h.clientTcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            h.serverTcp = await listener.AcceptTcpClientAsync();
            await connect;
            listener.Stop();

            var server = WebSocket.CreateFromStream(h.serverTcp.GetStream(), new WebSocketCreationOptions { IsServer = true });
            h.Client = WebSocket.CreateFromStream(h.clientTcp.GetStream(), new WebSocketCreationOptions { IsServer = false });

            h.Session = new KliveAgentVoiceSession(h.Backend, h.Voice, server, "conv1", Settings());
            h.run = Task.Run(h.Session.RunAsync);
            h.reader = Task.Run(h.ReadLoopAsync);
            await h.WaitForAsync("ready");
            return h;
        }

        /// <summary>A session with GPT-Live started and its sideband attached.</summary>
        internal static async Task<Harness> StartLiveAsync()
        {
            var h = await StartAsync();
            await h.OfferAsync();
            await h.WaitForAsync("answer");
            await h.UntilAsync(() => h.Voice.Sideband.Attached);
            h.Live.Push(new JObject { ["type"] = "session.started", ["event_id"] = "evt_start", ["session"] = new JObject { ["id"] = "live_test" } });
            await h.WaitForAsync("live");
            return h;
        }

        internal Task OfferAsync(string? voice = null) => SendAsync(new { t = "offer", sdp = "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\n", voice });

        private async Task ReadLoopAsync()
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            try
            {
                while (Client.State == WebSocketState.Open)
                {
                    var result = await Client.ReceiveAsync(buffer, stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;
                    var bytes = message.ToArray();
                    message.SetLength(0);
                    messages.Enqueue(JObject.Parse(Encoding.UTF8.GetString(bytes)));
                }
            }
            catch { }
        }

        internal Task SendAsync(object payload) =>
            Client.SendAsync(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload)), WebSocketMessageType.Text, true, CancellationToken.None);

        internal List<JObject> Messages(string type) => messages.Where(m => (string?)m["t"] == type).ToList();

        internal async Task<JObject> WaitForAsync(string type, Func<JObject, bool>? where = null, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var found = messages.FirstOrDefault(m => (string?)m["t"] == type && (where == null || where(m)));
                if (found != null) return found;
                await Task.Delay(10);
            }
            throw new TimeoutException($"No '{type}' message. Saw: {string.Join(", ", messages.Select(m => (string?)m["t"]))}");
        }

        internal async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition never became true.");
                await Task.Delay(10);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (Client.State == WebSocketState.Open)
                    await Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
            try { await run!.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            stop.Cancel();
            try { await reader!.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            clientTcp?.Dispose();
            serverTcp?.Dispose();
        }
    }

    internal sealed class FakeBackend : ITalkBackend
    {
        private readonly List<string> turns = new();
        private readonly List<string> cancelled = new();

        public KliveAgentTalkHub Hub { get; } = new();
        internal Func<string, TalkTurnReceipt> Receipt { get; set; } = _ => new TalkTurnReceipt("req-1", false, true, null);
        internal IReadOnlyList<(bool User, string Text)> History { get; set; } = Array.Empty<(bool, string)>();
        internal IReadOnlyList<string> Turns { get { lock (turns) return turns.ToList(); } }
        internal IReadOnlyList<string> Cancelled { get { lock (cancelled) return cancelled.ToList(); } }

        public Task<TalkTurnReceipt> QueueTurnAsync(string conversationId, string text, string clientMessageId)
        {
            lock (turns) turns.Add(text);
            return Task.FromResult(Receipt(text));
        }

        public bool CancelRun(string requestId)
        {
            lock (cancelled) cancelled.Add(requestId);
            return true;
        }

        public IReadOnlyList<AgentSteeringMessage>? GetSteering(string requestId) => null;
        public Task EnsureWarmAsync(string conversationId, ITalkListener listener, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> PrepareContextAsync(string text) => Task.FromResult<string?>("context for " + text);
        public IReadOnlyList<(bool User, string Text)> RecentHistory(string conversationId, int max) => History;
        public void RecordStage(string stage, long ms) { }
        public void LogError(Exception ex, string message) { }
    }

    internal sealed record RecordedSession(string Model, OpenAILiveUsage? Usage, bool Final, string Outcome);

    internal sealed class FakeVoice : ITalkVoice
    {
        private int released;
        private readonly List<RecordedSession> recorded = new();

        internal string? UnavailableReason { get; set; }
        internal JObject? CreatedWith { get; private set; }
        internal FakeSideband Sideband { get; } = new();
        internal int Released => Volatile.Read(ref released);
        internal IReadOnlyList<RecordedSession> Recorded { get { lock (recorded) return recorded.ToList(); } }

        public string? Unavailable => UnavailableReason;

        public Task<OpenAILiveCreated> CreateSessionAsync(JObject session, string offerSdp, CancellationToken cancellationToken)
        {
            CreatedWith = session;
            return Task.FromResult(new OpenAILiveCreated("live_test", "v=0\r\nanswer"));
        }

        public Task<IOpenAILiveSideband> AttachAsync(string sessionId, CancellationToken cancellationToken)
        {
            Sideband.Attached = true;
            return Task.FromResult<IOpenAILiveSideband>(Sideband);
        }

        public Task WarmAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void EngageReserve(string ownerId, TimeSpan ttl) { }
        public void ReleaseReserve(string ownerId) => Interlocked.Increment(ref released);

        public void RecordSession(string sessionId, string model, DateTimeOffset started, DateTimeOffset ended,
            OpenAILiveUsage? usage, bool usageFinal, double activePerMinute, double standbyPerMinute, string outcome, string? error)
        {
            lock (recorded) recorded.Add(new RecordedSession(model, usage, usageFinal, outcome));
        }
    }

    internal sealed record LiveCommand(string Type, string? DelegationId, string Content);

    /// <summary>GPT-Live's side of the sideband: the test pushes its events and reads the commands sent to it.</summary>
    internal sealed class FakeSideband : IOpenAILiveSideband
    {
        private readonly Channel<JObject> events = Channel.CreateUnbounded<JObject>();
        private readonly List<LiveCommand> commands = new();
        private int next;

        internal volatile bool Attached;
        internal IReadOnlyList<LiveCommand> Commands { get { lock (commands) return commands.ToList(); } }

        public string SessionId => "live_test";
        public bool IsOpen { get; private set; } = true;

        internal void Push(JObject evt) => events.Writer.TryWrite(evt);

        internal void User(string delta, long start, long end) => Push(new JObject
        {
            ["type"] = "session.input_transcript.delta", ["event_id"] = "evt_" + Interlocked.Increment(ref next),
            ["delta"] = delta, ["start_ms"] = start, ["end_ms"] = end,
        });

        internal void Voice(string delta, long start, long end) => Push(new JObject
        {
            ["type"] = "session.output_transcript.delta", ["event_id"] = "evt_" + Interlocked.Increment(ref next),
            ["delta"] = delta, ["start_ms"] = start, ["end_ms"] = end,
        });

        internal void Delegate(string id, long offsetMs) => Push(new JObject
        {
            ["type"] = "session.delegation.created", ["event_id"] = "evt_" + Interlocked.Increment(ref next), ["offset_ms"] = offsetMs,
            ["delegation"] = new JObject { ["id"] = id, ["type"] = "delegation", ["target"] = "client" },
        });

        public async IAsyncEnumerable<JObject> ReadEventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (true)
            {
                JObject evt;
                try { evt = await events.Reader.ReadAsync(cancellationToken); }
                catch (OperationCanceledException) { yield break; }
                catch (ChannelClosedException) { yield break; }
                yield return evt;
            }
        }

        private Task Record(string type, string? delegationId, string content)
        {
            lock (commands) commands.Add(new LiveCommand(type, delegationId, content));
            return Task.CompletedTask;
        }

        public Task AppendThinkingAsync(string? delegationId, string content, CancellationToken cancellationToken = default) =>
            Record("session.thinking.append", delegationId, content);

        public Task AppendCommentaryAsync(string? delegationId, string content, CancellationToken cancellationToken = default) =>
            Record("session.commentary.append", delegationId, content);

        public Task AppendInstructionsAsync(string content, CancellationToken cancellationToken = default) =>
            Record("session.instructions.append", null, content);

        public Task CloseSessionAsync(CancellationToken cancellationToken = default)
        {
            Record("session.close", null, "");
            // GPT-Live answers a close with session.closed and its final usage.
            Push(new JObject
            {
                ["type"] = "session.closed", ["event_id"] = "evt_closed_" + Interlocked.Increment(ref next), ["reason"] = "close_requested",
                ["usage"] = new JObject { ["seconds"] = 30 },
            });
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NeverCalled : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No HTTP expected.");
    }
}
