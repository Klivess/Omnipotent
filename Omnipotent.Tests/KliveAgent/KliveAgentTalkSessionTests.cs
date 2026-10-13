using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.KliveAgent.Models;
using Omnipotent.Services.KliveAgent.Talk;
using Omnipotent.Services.KliveLLM;
using LlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.KliveAgent;

/// <summary>
/// The talk session end to end over a real WebSocket, with the speech service and the agent faked:
/// a transcript taken during the pause is reused when nothing followed it, phantoms and local commands
/// never reach the agent, the reply is spoken in order with its first clause first, and a barge-in
/// silences the rest while what Klives actually heard is handed to the next turn.
/// </summary>
public sealed class KliveAgentTalkSessionTests
{
    private static readonly byte[] HalfSecondOfSpeech = KliveAgentTalkTextTests.Tone(0.3, TalkWire.CaptureBytesPerSecond / 2);

    [Fact]
    public async Task SpokenTurn_ReusesThePauseTranscript_WhenNothingFollowedIt()
    {
        await using var h = await Harness.StartAsync();

        await h.SpeakAsync(1, HalfSecondOfSpeech);
        await h.SendAsync(new { t = "pause", utt = 1 });
        var partial = await h.WaitForAsync("partial");
        Assert.Equal("Check the trader.", (string?)partial["text"]);
        Assert.Equal("complete", (string?)partial["hint"]);

        await h.SendAsync(new { t = "commit", utt = 1, endpointMs = 450 });
        var turn = await h.WaitForAsync("turn");

        Assert.Equal("Check the trader.", (string?)turn["text"]);
        Assert.Equal("req-1", (string?)turn["requestId"]);
        Assert.StartsWith("spk_", (string?)turn["clientMessageId"]);
        Assert.Equal(new[] { "Check the trader." }, h.Backend.Turns);
        Assert.Equal(1, h.Audio.Transcriptions); // the pause transcript was the final one
        var timing = await h.WaitForAsync("timing");
        Assert.True((bool?)timing["sttReused"]);
    }

    [Fact]
    public async Task SpeakingAgainAfterThePause_TranscribesTheWholeUtteranceAgain()
    {
        await using var h = await Harness.StartAsync();
        h.Audio.Transcript = n => n == 1 ? "Check the" : "Check the trader please.";

        await h.SpeakAsync(1, HalfSecondOfSpeech);
        await h.SendAsync(new { t = "pause", utt = 1 });
        var partial = await h.WaitForAsync("partial");
        Assert.Equal("incomplete", (string?)partial["hint"]);

        await h.SendAsync(new { t = "resume", utt = 1 });
        await h.SendAudioAsync(1, HalfSecondOfSpeech);
        await h.SendAsync(new { t = "commit", utt = 1, endpointMs = 1100 });
        var turn = await h.WaitForAsync("turn");

        Assert.Equal("Check the trader please.", (string?)turn["text"]);
        Assert.Equal(2, h.Audio.Transcriptions);
    }

    [Fact]
    public async Task Phantom_OnAQuietShortClip_IsDiscarded_AndNeverReachesTheAgent()
    {
        await using var h = await Harness.StartAsync();
        h.Audio.Transcript = _ => "Thank you.";

        await h.SpeakAsync(1, new byte[TalkWire.CaptureBytesPerSecond * 4 / 10]);
        await h.SendAsync(new { t = "commit", utt = 1, endpointMs = 700 });
        await h.WaitForAsync("discarded");

        Assert.Empty(h.Backend.Turns);
    }

    [Fact]
    public async Task Stop_CancelsTheRunWithoutAModelCall()
    {
        await using var h = await Harness.StartAsync();
        await h.TurnAsync(1, "Restart the trader.");

        h.Audio.Transcript = _ => "Stop.";
        await h.SpeakAsync(2, HalfSecondOfSpeech);
        await h.SendAsync(new { t = "commit", utt = 2, endpointMs = 450 });
        var command = await h.WaitForAsync("command");

        Assert.Equal("stop", (string?)command["command"]);
        Assert.Equal(new[] { "req-1" }, h.Backend.Cancelled);
        Assert.Single(h.Backend.Turns);
    }

    [Fact]
    public async Task Reply_IsSpokenInOrder_FirstClauseFirst_EvenWhenALaterSegmentIsReadySooner()
    {
        await using var h = await Harness.StartAsync();
        h.Audio.BeforeSynth = text => text.StartsWith("I checked", StringComparison.Ordinal) ? Task.Delay(300) : Task.CompletedTask;

        h.Session.OnRunStarted();
        h.Session.OnSpeakable("I checked the server logs, and ");
        h.Session.OnSpeakable("everything looks healthy. ");
        h.Session.OnModelTurnEnded(calledTools: false);
        h.Session.OnRunEnded(success: true);
        var end = await h.WaitForAsync("turn_end");

        var segments = h.Messages("segment");
        Assert.Equal(new[] { "I checked the server logs,", "and everything looks healthy." },
            segments.Select(s => (string?)s["text"]));
        Assert.Equal(new[] { 1, 2 }, segments.Select(s => (int)s["seq"]!));
        Assert.Equal(new[] { 1, 2 }, h.SpeechFrames().Select(f => BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(3))).Select(x => (int)x));
        Assert.Equal("I checked the server logs, and everything looks healthy.", (string?)end["spoken"]);
    }

    [Fact]
    public async Task BargeIn_SilencesTheRest_AndTheNextTurnLearnsWhatWasHeard()
    {
        await using var h = await Harness.StartAsync();

        h.Session.OnRunStarted();
        int turn = (int)(await h.WaitForAsync("turn_start"))["turn"]!;
        h.Session.OnSpeakable("First sentence here. ");
        await h.WaitForAsync("segment");

        await h.SendAsync(new { t = "interrupt", turn, heardText = "First sentence" });
        await h.RoundTripAsync();
        h.Session.OnSpeakable("Second sentence that must stay silent. ");
        h.Session.OnModelTurnEnded(calledTools: false);
        h.Session.OnRunEnded(success: true);
        await h.WaitForAsync("turn_end");

        Assert.DoesNotContain(h.Messages("segment"), s => ((string?)s["text"])!.Contains("Second"));
        Assert.Contains("First sentence", h.Session.TakeTurnNote());
        Assert.Null(h.Session.TakeTurnNote()); // consumed once
    }

    [Fact]
    public async Task AfterABargeIn_TheAnswerToWhatKlivesSaid_IsSpoken()
    {
        await using var h = await Harness.StartAsync();
        h.Backend.Receipt = _ => new TalkTurnReceipt("req-1", Steering: true, Accepted: true, Error: null);

        h.Session.OnRunStarted();
        int first = (int)(await h.WaitForAsync("turn_start"))["turn"]!;
        h.Session.OnSpeakable("A long answer begins. ");
        await h.WaitForAsync("segment");
        await h.SendAsync(new { t = "interrupt", turn = first, heardText = "A long answer" });

        h.Audio.Transcript = _ => "Actually check the logs instead.";
        await h.TurnAsync(1, null);

        // The run applies the steering at its next model call: that call is a new spoken turn.
        h.Session.OnModelTurnStarted();
        var next = await h.WaitForAsync("turn_start", m => (int)m["turn"]! != first);
        h.Session.OnSpeakable("Checking the logs now. ");
        h.Session.OnModelTurnEnded(calledTools: false);
        h.Session.OnRunEnded(success: true);
        await h.WaitForAsync("turn_end", m => (int)m["turn"]! == (int)next["turn"]!);

        Assert.Contains(h.Messages("segment"), s => (string?)s["text"] == "Checking the logs now.");
    }

    [Fact]
    public async Task SayThatAgain_ReplaysTheLastReplyFromTheCache_WithoutSynthesisingAgain()
    {
        await using var h = await Harness.StartAsync();
        h.Session.OnRunStarted();
        h.Session.OnSpeakable("The trader is up. ");
        h.Session.OnModelTurnEnded(calledTools: false);
        h.Session.OnRunEnded(success: true);
        await h.WaitForAsync("turn_end");
        int synthesised = h.Audio.SynthesisCalls;

        h.Audio.Transcript = _ => "Say that again?";
        await h.SpeakAsync(1, HalfSecondOfSpeech);
        await h.SendAsync(new { t = "commit", utt = 1, endpointMs = 450 });
        await h.WaitForAsync("command", m => (string?)m["command"] == "repeat");
        await h.WaitForAsync("segment", m => (string?)m["text"] == "The trader is up." && (int)m["turn"]! > 1);

        Assert.Equal(synthesised, h.Audio.SynthesisCalls);
        Assert.Empty(h.Backend.Turns);
    }

    [Fact]
    public async Task Reserve_IsTakenWhileTalking_AndGivenBackWhenTheSocketCloses()
    {
        var h = await Harness.StartAsync();
        await h.UntilAsync(() => h.Audio.Engaged > 0);
        await h.DisposeAsync();
        Assert.Equal(1, h.Audio.Released);
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

    // ── Warm continuation ──

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

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ConcurrentQueue<JObject> messages = new();
        private readonly ConcurrentQueue<byte[]> frames = new();
        private readonly CancellationTokenSource stop = new();
        private TcpClient? clientTcp;
        private TcpClient? serverTcp;
        private Task? run;
        private Task? reader;
        private bool disposed;

        internal FakeBackend Backend { get; private set; } = new();
        internal FakeAudio Audio { get; } = new();
        internal KliveAgentTalkSession Session { get; private set; } = null!;
        internal WebSocket Client { get; private set; } = null!;

        internal static async Task<Harness> StartAsync(FakeBackend? sharedBackend = null)
        {
            var h = new Harness();
            if (sharedBackend != null) h.Backend = sharedBackend;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            h.clientTcp = new TcpClient();
            var connect = h.clientTcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            h.serverTcp = await listener.AcceptTcpClientAsync();
            await connect;
            listener.Stop();

            var server = WebSocket.CreateFromStream(h.serverTcp.GetStream(), new WebSocketCreationOptions { IsServer = true });
            h.Client = WebSocket.CreateFromStream(h.clientTcp.GetStream(), new WebSocketCreationOptions { IsServer = false });

            // A voice nobody else uses: the speech cache is shared process-wide.
            string voice = "test_" + Guid.NewGuid().ToString("N");
            var settings = new KliveAgentTalkSettings(true, voice, 1.0, "low", "", "en", "", "", "wav",
                ReserveSlot: true, ReserveIdleMinutes: 3, SessionMaxTokens: 48_000, Fillers: false);
            h.Session = new KliveAgentTalkSession(h.Backend, h.Audio, server, "conv1", settings, voice, null, null);
            h.run = Task.Run(h.Session.RunAsync);
            h.reader = Task.Run(h.ReadLoopAsync);
            await h.WaitForAsync("ready");
            return h;
        }

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
                    if (result.MessageType == WebSocketMessageType.Binary) frames.Enqueue(bytes);
                    else messages.Enqueue(JObject.Parse(Encoding.UTF8.GetString(bytes)));
                }
            }
            catch { }
        }

        internal Task SendAsync(object payload) =>
            Client.SendAsync(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload)), WebSocketMessageType.Text, true, CancellationToken.None);

        internal async Task SendAudioAsync(int utt, byte[] pcm)
        {
            var frame = new byte[3 + pcm.Length];
            frame[0] = TalkWire.ClientAudio;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)utt);
            pcm.CopyTo(frame, 3);
            await Client.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        internal async Task SpeakAsync(int utt, byte[] pcm)
        {
            await SendAsync(new { t = "speech_start", utt });
            await SendAudioAsync(utt, pcm);
        }

        /// <summary>One complete spoken turn that reaches the agent.</summary>
        internal async Task TurnAsync(int utt, string? transcript)
        {
            if (transcript != null) Audio.Transcript = _ => transcript;
            int before = Messages("turn").Count;
            await SpeakAsync(utt, HalfSecondOfSpeech);
            await SendAsync(new { t = "commit", utt, endpointMs = 450 });
            await UntilAsync(() => Messages("turn").Count > before);
        }

        /// <summary>Everything sent before this has been handled by the session.</summary>
        internal async Task RoundTripAsync()
        {
            int before = Messages("pong").Count;
            await SendAsync(new { t = "ping" });
            await UntilAsync(() => Messages("pong").Count > before);
        }

        internal List<JObject> Messages(string type) => messages.Where(m => (string?)m["t"] == type).ToList();

        internal List<byte[]> SpeechFrames() => frames.Where(f => f.Length > 5 && f[0] == TalkWire.ServerSpeech).ToList();

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
        public void RecordStage(string stage, long ms) { }
        public void LogError(Exception ex, string message) { }
    }

    internal sealed class FakeAudio : ITalkAudio
    {
        private int transcriptions;
        private int synthesis;
        private int engaged;
        private int released;

        internal Func<int, string> Transcript { get; set; } = _ => "Check the trader.";
        internal Func<string, Task>? BeforeSynth { get; set; }
        internal int Transcriptions => Volatile.Read(ref transcriptions);
        internal int SynthesisCalls => Volatile.Read(ref synthesis);
        internal int Engaged => Volatile.Read(ref engaged);
        internal int Released => Volatile.Read(ref released);

        public Task<AIRouterTranscript> TranscribeAsync(byte[] wav, string? language, string? prompt, CancellationToken cancellationToken)
        {
            int n = Interlocked.Increment(ref transcriptions);
            return Task.FromResult(new AIRouterTranscript(Transcript(n), "en", 1, 0.01, -0.1, true));
        }

        public async Task<(byte[] Audio, string Format)> SynthesizeAsync(string text, string voice, double speed, string format,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref synthesis);
            if (BeforeSynth != null) await BeforeSynth(text);
            return (TalkWire.Wav(new byte[TalkWire.CaptureBytesPerSecond / 10]), "wav");
        }

        public Task<IReadOnlyList<AIRouterVoice>> ListVoicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AIRouterVoice>>(new[] { new AIRouterVoice("bm_george", "George") });

        public Task WarmAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void EngageReserve(string ownerId, TimeSpan ttl) => Interlocked.Increment(ref engaged);
        public void ReleaseReserve(string ownerId) => Interlocked.Increment(ref released);
    }

    private sealed class NeverCalled : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No HTTP expected.");
    }
}
