using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.KliveAgent.Talk;

/// <summary>
/// One live spoken conversation over one WebSocket.
///
/// Inbound: the browser does voice-activity detection and streams 16 kHz PCM while Klives speaks, with
/// <c>speech_start</c> / <c>pause</c> / <c>resume</c> / <c>commit</c> markers. Outbound: transcripts,
/// state, and the reply as a sequence of synthesised segments.
///
/// Where the latency goes, and what this does about each part:
/// <list type="bullet">
/// <item>Upload: audio arrives while he is still speaking, so nothing is uploaded after he stops.</item>
/// <item>Transcription: started at the first short pause. If he does not speak again before the turn is
/// committed, that transcript is used as-is and transcription costs nothing on the critical path.</item>
/// <item>Turn-taking: the provisional transcript tells the browser whether the sentence sounded finished,
/// so it can commit sooner (or wait longer for "and, um...").</item>
/// <item>Context: memory and knowledge for the turn are fetched during that same pause.</item>
/// <item>Speech: the reply is cut into segments while it streams; the first is synthesised after a
/// few words, later ones while earlier ones play.</item>
/// </list>
/// </summary>
internal sealed class KliveAgentTalkSession : ITalkListener
{
    private const int MaxMessageBytes = 64 * 1024;
    private const int MaxUtteranceBytes = TalkWire.CaptureBytesPerSecond * 45;
    private const int MinSpeculativeBytes = TalkWire.CaptureBytesPerSecond * 3 / 10;
    private const int MaxTtsInFlight = 2;
    private const int FarEnoughAheadMs = 6000;
    private static readonly TimeSpan StallAfter = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ReserveRenewEvery = TimeSpan.FromSeconds(10);
    private static readonly string[] FillerTexts = { "One sec.", "On it.", "Checking now." };

    private readonly ITalkBackend backend;
    private readonly ITalkAudio audio;
    private readonly WebSocket socket;
    private readonly KliveAgentTalkSettings settings;
    private readonly IReadOnlyList<(Regex Pattern, string Replacement)> lexicon;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<Outbound> outbound = Channel.CreateBounded<Outbound>(
        new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly object gate = new();
    private readonly string sessionId = Guid.NewGuid().ToString("N");

    // Voice preferences (the browser may override the server defaults).
    private string voice;
    private double speed;
    private string format;

    // ── The utterance being heard ──
    private int utterance = -1;
    private MemoryStream pcm = new();
    private long speechStartedTicks;
    private Task<AIRouterTranscript?>? speculative;
    private int speculativeUtterance = -1;
    private bool resumedSinceSpeculative;
    private Task<string?>? preparedContext;
    private string? preparedFor;

    // ── The reply being spoken ──
    private int turn;
    private bool turnActive;
    private bool suppressTurn;
    private bool runEnded;
    private int seq;
    private int nextSeqToSend = 1;
    private int ttsInFlight;
    private int clientAheadMs;
    private readonly Queue<string> pendingSegments = new();
    private readonly SortedDictionary<int, SynthResult> ready = new();
    private CancellationTokenSource turnCts = new();
    private readonly SpeechSegmenter segmenter;
    private long lastSpeakableTicks;
    private bool spokeThisTurn;
    private readonly StringBuilder spokenThisTurn = new();
    private string lastSpokenReply = string.Empty;
    private string? turnNote;
    private long commitTicks;
    private bool firstSegmentRecorded;
    // Klives spoke while a run was still going: his words became steering for that run, so whatever the
    // run says from its next model call on is the answer to them and must be heard, even if the reply
    // he talked over is being kept silent.
    private bool steeringPending;

    // ── Bookkeeping ──
    private string? activeRequestId;
    private readonly HashSet<string> ourMessageIds = new(StringComparer.Ordinal);
    private long lastReserveRenewTicks;

    private sealed record Outbound(string? Text, byte[]? Binary);
    private sealed record SynthResult(string Text, byte[]? Audio, string Format, int? DurationMs, string? Error);

    internal KliveAgentTalkSession(ITalkBackend backend, ITalkAudio audio, WebSocket socket, string conversationId,
        KliveAgentTalkSettings settings, string? voice, double? speed, string? format)
    {
        this.backend = backend;
        this.audio = audio;
        this.socket = socket;
        this.settings = settings;
        ConversationId = conversationId;
        lexicon = SpeechSegmenter.ParseLexicon(settings.Pronunciations);
        segmenter = new SpeechSegmenter(lexicon);
        this.voice = string.IsNullOrWhiteSpace(voice) ? settings.Voice : voice.Trim();
        this.speed = speed is > 0 ? Math.Clamp(speed.Value, 0.5, 2.0) : settings.Speed;
        this.format = KliveAgentTalkSettings.AudioFormats.Contains(format ?? "") ? format! : settings.AudioFormat;
    }

    internal string ConversationId { get; }

    private string ReserveOwner => "talk:" + sessionId;

    // ── ITalkListener: the profile spoken runs use ──

    public string ThinkingLevel => settings.Thinking;
    public string? ModelOverride => string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model;
    public int SessionMaxTokens => settings.SessionMaxTokens;

    // ── Lifecycle ──

    internal async Task RunAsync()
    {
        var previous = backend.Hub.Attach(ConversationId, this);
        if (previous != null) await previous.CloseAsync(4001, "Talk mode moved to another tab.");

        var writer = Task.Run(WriteLoopAsync);
        var housekeeping = Task.Run(HousekeepingLoopAsync);
        try
        {
            Send(new
            {
                t = "ready",
                conversationId = ConversationId,
                settings = new { voice, speed, format, language = settings.Language, thinking = settings.Thinking, fillers = settings.Fillers },
            });
            _ = Task.Run(StartupAsync);
            await ReceiveLoopAsync();
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            backend.LogError(ex, "[KliveAgent] Talk session failed.");
        }
        finally
        {
            backend.Hub.Detach(ConversationId, this);
            if (settings.ReserveSlot) audio.ReleaseReserve(ReserveOwner);
            try { lifetime.Cancel(); } catch { }
            lock (gate) { try { turnCts.Cancel(); } catch { } }
            outbound.Writer.TryComplete();
            try { await writer.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            try { await housekeeping.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            }
        }
    }

    /// <summary>Everything that makes the first turn fast, started the moment the socket opens: TLS to
    /// AIRouter for both chat and audio, the voice list, the filler clips, and the warm conversation.</summary>
    private async Task StartupAsync()
    {
        var ct = lifetime.Token;
        try
        {
            RenewReserve(force: true);
            await audio.WarmAsync(ct);
            var voices = await audio.ListVoicesAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            Send(new { t = "voices", voices = voices.Select(v => new { id = v.Id, name = v.Name }) });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            Send(new { t = "error", code = "voices", message = "Could not reach AIRouter's voice list: " + ex.Message });
        }

        try { await backend.EnsureWarmAsync(ConversationId, this, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            backend.LogError(ex, "[KliveAgent] Talk warm-up failed (the first turn will start cold).");
        }

        if (settings.Fillers) await SendFillersAsync();
    }

    private async Task SendFillersAsync()
    {
        string v; double s; string f;
        lock (gate) { v = voice; s = speed; f = format; }
        for (int i = 0; i < FillerTexts.Length; i++)
        {
            try
            {
                var (clip, actual) = await TalkSpeechCache.GetOrSynthesizeAsync(audio, FillerTexts[i], v, s, f, lifetime.Token);
                Send(new { t = "filler", index = i, text = FillerTexts[i], format = actual, voice = v });
                SendBinary(TalkWire.FillerFrame(i, clip));
            }
            catch (OperationCanceledException) { return; }
            catch { /* fillers are a nicety */ }
        }
    }

    internal async Task CloseAsync(int code, string reason)
    {
        Send(new { t = "closed", reason });
        try { await Task.Delay(100); } catch { }
        try { lifetime.Cancel(); } catch { }
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, CancellationToken.None);
        }
        catch { }
    }

    // ── Socket I/O ──

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        var ct = lifetime.Token;
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                await CloseAsync(4009, "Message too large.");
                break;
            }
            if (!result.EndOfMessage) continue;

            var bytes = message.ToArray();
            message.SetLength(0);
            if (result.MessageType == WebSocketMessageType.Binary) HandleBinary(bytes);
            else HandleText(Encoding.UTF8.GetString(bytes));
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var message in outbound.Reader.ReadAllAsync())
            {
                if (socket.State != WebSocketState.Open) break;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                if (message.Binary != null)
                    await socket.SendAsync(new ArraySegment<byte>(message.Binary), WebSocketMessageType.Binary, true, timeout.Token);
                else
                    await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message.Text ?? "{}")),
                        WebSocketMessageType.Text, true, timeout.Token);
            }
        }
        catch
        {
            // A browser that stopped reading must not hold the session open.
            try { lifetime.Cancel(); } catch { }
            try { socket.Abort(); } catch { }
        }
    }

    private void Send(object payload) =>
        outbound.Writer.TryWrite(new Outbound(JsonConvert.SerializeObject(payload), null));

    private void SendBinary(byte[] frame) => outbound.Writer.TryWrite(new Outbound(null, frame));

    private void SendState(string state, string? detail = null) => Send(new { t = "state", state, detail });

    private async Task HousekeepingLoopAsync()
    {
        var ct = lifetime.Token;
        long lastPing = Stopwatch.GetTimestamp();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);
                // A reply whose stream paused mid-sentence: say the words so far.
                bool pump = false;
                lock (gate)
                {
                    if (turnActive && !suppressTurn && segmenter.PendingWords >= SpeechSegmenter.StallMinWords
                        && Stopwatch.GetElapsedTime(lastSpeakableTicks) >= StallAfter)
                    {
                        EnqueueLocked(segmenter.TakeStalled());
                        pump = true;
                    }
                }
                if (pump) Pump();
                if (Stopwatch.GetElapsedTime(lastPing) >= PingEvery)
                {
                    lastPing = Stopwatch.GetTimestamp();
                    Send(new { t = "ping" });
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Inbound messages ──

    private void HandleBinary(byte[] frame)
    {
        if (!TalkWire.TryReadClientAudio(frame, out int utt, out var samples)) return;
        lock (gate)
        {
            if (utt != utterance) return;
            if (pcm.Length + samples.Length > MaxUtteranceBytes) return;
            pcm.Write(samples);
        }
    }

    private void HandleText(string text)
    {
        JObject message;
        try { message = JObject.Parse(text); }
        catch { return; }

        switch ((string?)message["t"])
        {
            case "speech_start":
                OnSpeechStart((int?)message["utt"] ?? 0);
                break;
            case "pause":
                OnPause((int?)message["utt"] ?? -1);
                break;
            case "resume":
                lock (gate) { if (((int?)message["utt"] ?? -1) == utterance) resumedSinceSpeculative = true; }
                break;
            case "commit":
                _ = CommitAsync((int?)message["utt"] ?? -1, (long?)message["endpointMs"] ?? 0);
                break;
            case "cancel_utt":
                lock (gate)
                {
                    if (((int?)message["utt"] ?? -1) == utterance) ResetUtteranceLocked();
                }
                SendState("listening");
                break;
            case "interrupt":
                Interrupt((int?)message["turn"] ?? -1, (string?)message["heardText"]);
                break;
            case "buffer":
                lock (gate) clientAheadMs = Math.Max(0, (int?)message["aheadMs"] ?? 0);
                Pump();
                break;
            case "first_audio":
                RecordFirstAudio((long?)message["ms"]);
                break;
            case "config":
                ApplyConfig(message);
                break;
            case "ping":
                Send(new { t = "pong" });
                break;
        }
    }

    private void OnSpeechStart(int utt)
    {
        lock (gate)
        {
            ResetUtteranceLocked();
            utterance = utt;
            speechStartedTicks = Stopwatch.GetTimestamp();
        }
        RenewReserve();
        SendState("hearing");
        // Speaking again after a long silence: rebuild/warm the conversation while he talks, so the
        // prefill happens during his sentence instead of after it. A no-op while it is still warm.
        _ = backend.EnsureWarmAsync(ConversationId, this, lifetime.Token);
    }

    private void ResetUtteranceLocked()
    {
        utterance = -1;
        pcm = new MemoryStream();
        speculative = null;
        speculativeUtterance = -1;
        resumedSinceSpeculative = false;
        preparedContext = null;
        preparedFor = null;
    }

    /// <summary>The first short silence: transcribe what we have now, and fetch the context the turn will
    /// need. If he does not speak again before committing, both are already done.</summary>
    private void OnPause(int utt)
    {
        Task<AIRouterTranscript?> work;
        lock (gate)
        {
            if (utt != utterance || pcm.Length < MinSpeculativeBytes) return;
            byte[] snapshot = pcm.ToArray();
            resumedSinceSpeculative = false;
            speculativeUtterance = utt;
            work = speculative = TranscribeAsync(snapshot);
        }
        _ = PublishPartialAsync(utt, work);
    }

    private async Task PublishPartialAsync(int utt, Task<AIRouterTranscript?> work)
    {
        var transcript = await work;
        if (transcript == null || string.IsNullOrWhiteSpace(transcript.Text)) return;
        lock (gate)
        {
            if (utt != utterance || !ReferenceEquals(work, speculative)) return;
            preparedFor = transcript.Text;
            preparedContext = backend.PrepareContextAsync(transcript.Text);
        }
        Send(new { t = "partial", utt, text = transcript.Text, hint = TalkText.EndpointHint(transcript.Text) });
    }

    private async Task<AIRouterTranscript?> TranscribeAsync(byte[] samples)
    {
        try
        {
            string lastSpoken;
            lock (gate) lastSpoken = TalkText.LastSentence(lastSpokenReply);
            return await audio.TranscribeAsync(TalkWire.Wav(samples),
                string.IsNullOrWhiteSpace(settings.Language) ? null : settings.Language,
                TalkText.WhisperPrompt(settings.Vocabulary, lastSpoken), lifetime.Token);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Send(new { t = "error", code = "stt", message = DescribeAudioFailure(ex) });
            return null;
        }
    }

    // ── Commit: the turn is over; transcribe (or reuse), filter, and hand it to the agent ──

    private async Task CommitAsync(int utt, long endpointMs)
    {
        try { await CommitCoreAsync(utt, endpointMs); }
        catch (Exception ex)
        {
            backend.LogError(ex, "[KliveAgent] Talk: a spoken turn failed.");
            Send(new { t = "error", code = "turn", message = ex.Message });
            SendState("listening");
        }
    }

    private async Task CommitCoreAsync(int utt, long endpointMs)
    {
        byte[] samples;
        Task<AIRouterTranscript?>? reuse;
        long speechTicks;
        lock (gate)
        {
            if (utt != utterance) return;
            samples = pcm.ToArray();
            reuse = speculativeUtterance == utt && !resumedSinceSpeculative ? speculative : null;
            speechTicks = speechStartedTicks;
            utterance = -1;
            commitTicks = Stopwatch.GetTimestamp();
            firstSegmentRecorded = false;
        }
        SendState("transcribing");
        RenewReserve();

        var sttClock = Stopwatch.StartNew();
        AIRouterTranscript? transcript = reuse != null ? await reuse : null;
        bool reused = transcript != null;
        if (transcript == null) transcript = await TranscribeAsync(samples);
        long sttMs = sttClock.ElapsedMilliseconds;

        RecordStage("talk.endpoint", endpointMs);
        RecordStage(reused ? "talk.sttReused" : "talk.stt", sttMs);
        Send(new { t = "timing", utt, endpointMs, sttMs, sttReused = reused });

        if (transcript == null)
        {
            SendState("listening");
            return;
        }

        int audioMs = samples.Length * 1000 / TalkWire.CaptureBytesPerSecond;
        if (TalkText.LooksLikePhantom(transcript, audioMs, TalkWire.Rms(samples)))
        {
            Send(new { t = "discarded", utt, text = transcript.Text });
            SendState("listening");
            return;
        }

        string text = transcript.Text.Trim();
        switch (TalkText.MatchCommand(text))
        {
            case TalkCommand.Stop:
                StopEverything(utt, text);
                return;
            case TalkCommand.Repeat:
                Repeat(utt, text);
                return;
        }

        await DispatchAsync(utt, text, resubmitOf: null);
    }

    private async Task DispatchAsync(int utt, string text, string? resubmitOf)
    {
        string clientMessageId = "spk_" + Guid.NewGuid().ToString("N")[..24];
        lock (gate) ourMessageIds.Add(clientMessageId);
        SendState("thinking");
        TalkTurnReceipt receipt;
        try
        {
            receipt = await backend.QueueTurnAsync(ConversationId, text, clientMessageId);
        }
        catch (Exception ex)
        {
            receipt = new TalkTurnReceipt(null, false, false, ex.Message);
        }

        lock (gate)
        {
            if (!string.IsNullOrWhiteSpace(receipt.RequestId)) activeRequestId = receipt.RequestId;
            if (receipt.Steering) steeringPending = true;
        }
        Send(new
        {
            t = "turn",
            utt,
            text,
            clientMessageId,
            requestId = receipt.RequestId,
            steering = receipt.Steering,
            accepted = receipt.Accepted,
            error = receipt.Error,
            resubmitOf,
        });
        if (!receipt.Accepted)
        {
            Send(new { t = "error", code = "agent", message = receipt.Error ?? "KliveAgent did not accept the turn." });
            SendState("listening");
        }
    }

    private void StopEverything(int utt, string heard)
    {
        string? requestId;
        int current;
        lock (gate)
        {
            requestId = activeRequestId;
            current = turn;
        }
        Interrupt(current, null, note: false);
        bool stopped = !string.IsNullOrWhiteSpace(requestId) && backend.CancelRun(requestId!);
        Send(new { t = "command", utt, command = "stop", text = heard, stoppedRun = stopped });
        Send(new { t = "stop_audio" });
        SendState("listening");
    }

    /// <summary>"Say that again": the same text again, as a new spoken turn. The segments are in the
    /// speech cache, so it starts instantly and costs nothing.</summary>
    private void Repeat(int utt, string heard)
    {
        string reply;
        lock (gate) reply = lastSpokenReply;
        Send(new { t = "command", utt, command = "repeat", text = heard });
        if (string.IsNullOrWhiteSpace(reply))
        {
            SendState("listening");
            return;
        }
        lock (gate)
        {
            StartTurnLocked();
            EnqueueLocked(segmenter.Append(reply));
            EnqueueLocked(segmenter.Flush());
            runEnded = true;
        }
        Send(new { t = "turn_start", turn });
        Pump();
    }

    /// <summary>
    /// Barge-in. The browser has already faded the audio out; here the rest of the reply is dropped and
    /// kept silent for as long as the run keeps producing it. The run itself is left to finish its model
    /// call: cancelling it would throw away the warm session the next turn depends on.
    /// </summary>
    private void Interrupt(int interruptedTurn, string? heardText, bool note = true)
    {
        lock (gate)
        {
            if (interruptedTurn != turn || !turnActive) return;
            suppressTurn = true;
            pendingSegments.Clear();
            ready.Clear();
            try { turnCts.Cancel(); } catch { }
            turnCts = new CancellationTokenSource();
            segmenter.Reset();
            if (note)
            {
                string heard = (heardText ?? string.Empty).Trim();
                if (heard.Length > 300) heard = heard[..300] + "…";
                turnNote = heard.Length > 0
                    ? $"[Interrupted] Klives cut your last reply off; he only heard: \"{heard}\". The rest was not said."
                    : "[Interrupted] Klives cut your last reply off before hearing any of it.";
                lastSpokenReply = heard;
            }
        }
        MaybeFinishTurn();
    }

    private void ApplyConfig(JObject message)
    {
        bool fillersChanged = false;
        lock (gate)
        {
            string? v = (string?)message["voice"];
            if (!string.IsNullOrWhiteSpace(v) && v != voice) { voice = v.Trim(); fillersChanged = true; }
            double? s = (double?)message["speed"];
            if (s is > 0 && Math.Abs(s.Value - speed) > 0.001) { speed = Math.Clamp(s.Value, 0.5, 2.0); fillersChanged = true; }
            string? f = (string?)message["format"];
            if (!string.IsNullOrWhiteSpace(f) && KliveAgentTalkSettings.AudioFormats.Contains(f) && f != format) { format = f; fillersChanged = true; }
        }
        Send(new { t = "config", voice, speed, format });
        if (fillersChanged && settings.Fillers) _ = Task.Run(SendFillersAsync);
    }

    // ── ITalkListener: the agent's reply, turned into speech ──

    public void OnRunStarted()
    {
        int started;
        lock (gate)
        {
            StartTurnLocked();
            started = turn;
        }
        Send(new { t = "turn_start", turn = started });
        SendState("thinking");
        RenewReserve();
    }

    public void OnModelTurnStarted()
    {
        int started = 0;
        lock (gate)
        {
            // The run is about to answer what he said over the last reply: that answer is a new turn.
            if (turnActive && suppressTurn && steeringPending)
            {
                StartTurnLocked();
                started = turn;
            }
            steeringPending = false;
        }
        if (started > 0) Send(new { t = "turn_start", turn = started });
    }

    private void StartTurnLocked()
    {
        turn = turn % 65535 + 1;
        turnActive = true;
        suppressTurn = false;
        runEnded = false;
        seq = 0;
        nextSeqToSend = 1;
        pendingSegments.Clear();
        ready.Clear();
        try { turnCts.Cancel(); } catch { }
        turnCts = new CancellationTokenSource();
        segmenter.Reset();
        spokeThisTurn = false;
        spokenThisTurn.Clear();
        lastSpeakableTicks = Stopwatch.GetTimestamp();
    }

    public void OnSpeakable(string delta)
    {
        lock (gate)
        {
            if (!turnActive) StartTurnLocked();
            if (suppressTurn) return;
            lastSpeakableTicks = Stopwatch.GetTimestamp();
            EnqueueLocked(segmenter.Append(delta));
        }
        Pump();
    }

    public void OnModelTurnEnded(bool calledTools)
    {
        lock (gate)
        {
            if (!turnActive || suppressTurn) return;
            EnqueueLocked(segmenter.Flush());
        }
        Pump();
    }

    public void OnModelTurnAborted()
    {
        lock (gate) segmenter.Reset();
    }

    public void OnToolStarted(string label)
    {
        Send(new { t = "tool", phase = "start", name = label });
        SendState("tool", label);
        RenewReserve();
    }

    public void OnToolFinished(string label, bool ok) => Send(new { t = "tool", phase = "end", name = label, ok });

    public void OnRunEnded(bool success)
    {
        string? requestId;
        lock (gate)
        {
            requestId = activeRequestId;
            steeringPending = false;
            if (turnActive)
            {
                if (!suppressTurn)
                {
                    EnqueueLocked(segmenter.Flush());
                    if (!success && !spokeThisTurn && pendingSegments.Count == 0 && ttsInFlight == 0)
                        EnqueueLocked(new[] { "Sorry, that didn't work. The details are on screen." });
                }
                runEnded = true;
            }
        }
        Pump();
        MaybeFinishTurn();
        if (requestId != null) _ = ResubmitRejectedSteeringAsync(requestId);
    }

    public string? TakeTurnNote()
    {
        lock (gate)
        {
            string? note = turnNote;
            turnNote = null;
            return note;
        }
    }

    public async Task<string?> TakePreparedContextAsync(string userMessage)
    {
        Task<string?>? work;
        lock (gate)
        {
            work = preparedContext != null && preparedFor != null
                && (TalkText.SameWords(preparedFor, userMessage)
                    || TalkText.NormalizeForMatch(userMessage).StartsWith(TalkText.NormalizeForMatch(preparedFor), StringComparison.Ordinal))
                ? preparedContext
                : null;
            preparedContext = null;
            preparedFor = null;
        }
        if (work == null) return null;
        try { return await work.WaitAsync(TimeSpan.FromMilliseconds(600)); }
        catch { return null; }
    }

    private void EnqueueLocked(IReadOnlyList<string> segments)
    {
        foreach (string segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;
            pendingSegments.Enqueue(segment);
            if (spokenThisTurn.Length > 0) spokenThisTurn.Append(' ');
            spokenThisTurn.Append(segment);
        }
    }

    /// <summary>
    /// Starts synthesis for queued segments: the first of a turn immediately and on its own (it is the
    /// latency-critical one), later ones merged up to one request's worth, at most two in flight, and not
    /// far ahead of what the browser is about to play — so a barge-in wastes little.
    /// </summary>
    private void Pump()
    {
        lock (gate)
        {
            while (ttsInFlight < MaxTtsInFlight && pendingSegments.Count > 0 && turnActive && !suppressTurn)
            {
                bool first = seq == 0;
                if (!first && ttsInFlight > 0 && clientAheadMs > FarEnoughAheadMs) break;
                string text = pendingSegments.Dequeue();
                if (!first)
                    while (pendingSegments.Count > 0
                           && text.Length + 1 + pendingSegments.Peek().Length <= SpeechSegmenter.MaxSegmentChars)
                        text += " " + pendingSegments.Dequeue();
                int mySeq = ++seq;
                int myTurn = turn;
                var token = turnCts.Token;
                string v = voice, f = format;
                double s = speed;
                ttsInFlight++;
                _ = SynthesizeAsync(myTurn, mySeq, text, v, s, f, token);
            }
        }
    }

    private async Task SynthesizeAsync(int forTurn, int forSeq, string text, string v, double s, string f, CancellationToken token)
    {
        byte[]? clip = null;
        string actualFormat = f;
        string? error = null;
        var clock = Stopwatch.StartNew();
        try
        {
            (clip, actualFormat) = await TalkSpeechCache.GetOrSynthesizeAsync(audio, text, v, s, f, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = DescribeAudioFailure(ex); }
        RecordStage("talk.tts", clock.ElapsedMilliseconds);

        lock (gate)
        {
            ttsInFlight = Math.Max(0, ttsInFlight - 1);
            if (forTurn == turn && !suppressTurn && !token.IsCancellationRequested)
            {
                int? duration = clip != null && actualFormat == "wav" ? TalkWire.WavDurationMs(clip) : null;
                ready[forSeq] = new SynthResult(text, clip, actualFormat, duration, error);
                FlushReadyLocked();
            }
        }
        Pump();
        MaybeFinishTurn();
    }

    /// <summary>Sends finished segments strictly in order; a later one waits for an earlier one.</summary>
    private void FlushReadyLocked()
    {
        while (ready.Remove(nextSeqToSend, out var result))
        {
            int sentSeq = nextSeqToSend++;
            if (result.Audio == null)
            {
                Send(new { t = "segment", turn, seq = sentSeq, text = result.Text, skipped = true, error = result.Error });
                continue;
            }
            Send(new { t = "segment", turn, seq = sentSeq, text = result.Text, format = result.Format, ms = result.DurationMs });
            SendBinary(TalkWire.SpeechFrame(turn, sentSeq, result.Audio));
            if (!spokeThisTurn)
            {
                spokeThisTurn = true;
                SendState("speaking");
            }
            if (!firstSegmentRecorded && commitTicks != 0)
            {
                firstSegmentRecorded = true;
                RecordStage("talk.firstSegment", (long)Stopwatch.GetElapsedTime(commitTicks).TotalMilliseconds);
            }
        }
    }

    /// <summary>The run is over and every segment has been sent: tell the browser the turn is complete.</summary>
    private void MaybeFinishTurn()
    {
        int finished;
        string spoken;
        lock (gate)
        {
            if (!turnActive || !runEnded || pendingSegments.Count > 0 || ttsInFlight > 0 || ready.Count > 0) return;
            if (!suppressTurn && segmenter.PendingWords > 0) return;
            turnActive = false;
            finished = turn;
            spoken = spokenThisTurn.ToString();
            if (!suppressTurn && spoken.Length > 0) lastSpokenReply = spoken;
            commitTicks = 0;
        }
        Send(new { t = "turn_end", turn = finished, spoken });
    }

    /// <summary>
    /// A spoken message that arrived while a run was finishing is accepted as steering; if the run ended
    /// without applying it (a failure or stop path), it would be silently lost. Send it again as a new
    /// turn instead.
    /// </summary>
    private async Task ResubmitRejectedSteeringAsync(string requestId)
    {
        try
        {
            // The run marks unapplied steering as rejected just after the loop returns.
            await Task.Delay(750, lifetime.Token);
            var steering = backend.GetSteering(requestId);
            if (steering == null) return;
            List<(string Text, string Id)> lost;
            lock (gate)
            {
                lost = steering
                    .Where(m => m.Status == "rejected" && m.ClientMessageId != null && ourMessageIds.Remove(m.ClientMessageId))
                    .Select(m => (m.Message, m.ClientMessageId!))
                    .ToList();
            }
            foreach (var (text, id) in lost)
                await DispatchAsync(-1, text, resubmitOf: id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            backend.LogError(ex, "[KliveAgent] Talk: could not re-send lost steering.");
        }
    }

    // ── Helpers ──

    private void RenewReserve(bool force = false)
    {
        if (!settings.ReserveSlot) return;
        long now = Stopwatch.GetTimestamp();
        lock (gate)
        {
            if (!force && Stopwatch.GetElapsedTime(lastReserveRenewTicks, now) < ReserveRenewEvery) return;
            lastReserveRenewTicks = now;
        }
        audio.EngageReserve(ReserveOwner, TimeSpan.FromMinutes(settings.ReserveIdleMinutes));
    }

    private void RecordFirstAudio(long? clientMs)
    {
        if (clientMs is not > 0) return;
        RecordStage("talk.firstAudio", clientMs.Value);
    }

    private void RecordStage(string stage, long ms)
    {
        try { backend.RecordStage(stage, ms); } catch { }
    }

    private static string DescribeAudioFailure(Exception ex) => ex switch
    {
        AIRouterAudioException { RateLimited: true } => "AIRouter is rate-limiting speech right now.",
        AIRouterAudioException audio => audio.Message,
        _ => ex.Message,
    };
}
