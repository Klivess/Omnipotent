using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.KliveLLM.Telemetry;

namespace Omnipotent.Services.KliveAgent.Talk;

/// <summary>
/// One spoken conversation with KliveAgent, through OpenAI's GPT-Live.
///
/// Three connections meet here:
/// <list type="bullet">
/// <item>The browser's control socket (<c>/kliveagent/voice</c>): it sends its WebRTC offer and gets
/// OpenAI's answer back, then hears what KliveAgent is doing (turns, tools, state, usage).</item>
/// <item>The browser's own WebRTC call with OpenAI: microphone up, voice down, transcripts on its data
/// channel. This server never touches the audio.</item>
/// <item>The server's sideband to the same GPT-Live session. GPT-Live hears Klives, takes turns, and
/// when an answer needs real work it raises a <i>client delegation</i>. The request is read out of the
/// transcript (<see cref="LiveTranscript"/>) and queued to KliveAgent as a spoken turn. As KliveAgent's
/// reply streams it is cut into speakable pieces (<see cref="SpeechSegmenter"/>) and handed back as
/// <c>session.commentary.append</c>, which GPT-Live says in its own voice; tool activity goes back as
/// silent <c>session.thinking.append</c> context.</item>
/// </list>
///
/// The agent side is unchanged from the AIRouter talk mode this replaced: a spoken run is pinned to one
/// model and thinking level on a warm append-only session (<see cref="ITalkListener"/>, TalkEpoch).
/// </summary>
internal sealed class KliveAgentVoiceSession : ITalkListener
{
    private const int MaxMessageBytes = 256 * 1024;
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ReserveRenewEvery = TimeSpan.FromSeconds(10);
    /// <summary>Speech that paused mid-sentence this long is said as it stands.</summary>
    private static readonly TimeSpan StallAfter = TimeSpan.FromMilliseconds(400);
    /// <summary>Later pieces of a reply are gathered this long, so the voice gets sentences, not words.</summary>
    private static readonly TimeSpan CoalesceFor = TimeSpan.FromMilliseconds(250);
    /// <summary>How long a delegation waits for Klives's last words to be transcribed.</summary>
    internal static readonly TimeSpan SettleQuiet = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan SettleMax = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan ToolNoteEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UsageEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WarmEvery = TimeSpan.FromSeconds(10);
    private static readonly Regex SafeSdp = new(@"^v=0\r?\n", RegexOptions.Compiled);

    private readonly ITalkBackend backend;
    private readonly ITalkVoice voice;
    private readonly WebSocket socket;
    private readonly KliveAgentTalkSettings settings;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<string> outbound = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    // Commands for GPT-Live, sent strictly in order by one loop.
    private readonly Channel<Func<IOpenAILiveSideband, Task>> liveOutbox = Channel.CreateUnbounded<Func<IOpenAILiveSideband, Task>>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly object gate = new();
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private readonly LiveTranscript transcript = new();
    private readonly SpeechSegmenter segmenter;

    // ── The GPT-Live session ──
    private bool liveStarting;
    private string? liveSessionId;
    private IOpenAILiveSideband? sideband;
    private string liveVoice;
    private DateTimeOffset? liveStarted;
    private OpenAILiveUsage? usage;
    private bool usageFinal;
    private string? closedReason;
    private string? liveError;
    private int recorded;
    private readonly TaskCompletionSource closedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long lastUsageSentTicks;

    // ── Delegations ──
    private string? delegationId;
    private long delegationTicks;
    private bool firstWordsRecorded = true;

    // ── The run answering him ──
    private string? activeRequestId;
    private bool runActive;
    private bool suppressRun;
    private bool steeringPending;
    private long runStartedTicks;
    private readonly StringBuilder pendingSpeech = new();
    private long pendingSinceTicks;
    private long lastSpeakableTicks;
    private long lastSaidTicks;
    private long lastProgressTicks;
    private long lastToolNoteTicks;
    private string? currentTool;
    private bool spokeThisRun;
    private readonly StringBuilder spokenThisRun = new();
    private string lastReply = string.Empty;
    private string? turnNote;
    private Task<string?>? preparedContext;
    private string? preparedFor;
    private string preparedPending = string.Empty;
    private readonly HashSet<string> ourMessageIds = new(StringComparer.Ordinal);
    private long lastReserveRenewTicks;
    private long lastWarmTicks;

    internal KliveAgentVoiceSession(ITalkBackend backend, ITalkVoice voice, WebSocket socket, string conversationId,
        KliveAgentTalkSettings settings)
    {
        this.backend = backend;
        this.voice = voice;
        this.socket = socket;
        this.settings = settings;
        ConversationId = conversationId;
        segmenter = new SpeechSegmenter(SpeechSegmenter.ParseLexicon(settings.Pronunciations));
        liveVoice = settings.LiveVoice;
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
        var liveWriter = Task.Run(LiveOutboxLoopAsync);
        try
        {
            Send(new
            {
                t = "ready",
                conversationId = ConversationId,
                settings = new
                {
                    model = settings.LiveModel,
                    voice = settings.LiveVoice,
                    voices = OpenAILiveClient.BuiltInVoices,
                    idleSeconds = settings.IdleSeconds,
                    thinking = settings.Thinking,
                },
                unavailable = voice.Unavailable,
            });
            _ = Task.Run(StartupAsync);
            await ReceiveLoopAsync();
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            backend.LogError(ex, "[KliveAgent] Voice session failed.");
        }
        finally
        {
            backend.Hub.Detach(ConversationId, this);
            if (settings.ReserveSlot) voice.ReleaseReserve(ReserveOwner);
            await CloseLiveAsync("client_closed");
            try { lifetime.Cancel(); } catch { }
            outbound.Writer.TryComplete();
            liveOutbox.Writer.TryComplete();
            try { await writer.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            try { await housekeeping.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            try { await liveWriter.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            }
        }
    }

    /// <summary>What makes the first answer fast, started as the socket opens: TLS to the agent's model
    /// router, the AIRouter slot reserve, and the warm conversation the first spoken turn extends.</summary>
    private async Task StartupAsync()
    {
        var ct = lifetime.Token;
        RenewReserve(force: true);
        try { await voice.WarmAsync(ct); } catch { /* the first call pays for its own TLS */ }
        try { await backend.EnsureWarmAsync(ConversationId, this, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            backend.LogError(ex, "[KliveAgent] Talk warm-up failed (the first turn will start cold).");
        }
    }

    internal async Task CloseAsync(int code, string reason)
    {
        Send(new { t = "closed", reason, takenOver = code == 4001 });
        await CloseLiveAsync("taken_over");
        try { await Task.Delay(100); } catch { }
        try { lifetime.Cancel(); } catch { }
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, CancellationToken.None);
        }
        catch { }
    }

    // ── Browser socket ──

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
            if (result.MessageType == WebSocketMessageType.Text) HandleText(Encoding.UTF8.GetString(bytes));
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var text in outbound.Reader.ReadAllAsync())
            {
                if (socket.State != WebSocketState.Open) break;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, timeout.Token);
            }
        }
        catch
        {
            // A browser that stopped reading must not hold the session open.
            try { lifetime.Cancel(); } catch { }
            try { socket.Abort(); } catch { }
        }
    }

    private void Send(object payload) => outbound.Writer.TryWrite(JsonConvert.SerializeObject(payload));

    private void SendState(string state, string? detail = null) => Send(new { t = "state", state, detail });

    private void HandleText(string text)
    {
        JObject message;
        try { message = JObject.Parse(text); }
        catch { return; }

        switch ((string?)message["t"])
        {
            case "offer":
                _ = StartLiveAsync((string?)message["sdp"], (string?)message["voice"]);
                break;
            case "hush":
                Hush();
                break;
            case "stop_run":
                StopRun(spoken: false);
                break;
            case "hangup":
                _ = CloseLiveAsync("hangup");
                break;
            case "first_audio":
                if ((long?)message["ms"] is long ms && ms > 0) RecordStage("talk.firstAudio", ms);
                break;
            case "ping":
                Send(new { t = "pong" });
                break;
        }
    }

    // ── Starting GPT-Live ──

    private async Task StartLiveAsync(string? sdp, string? requestedVoice)
    {
        lock (gate)
        {
            if (liveStarting || liveSessionId != null)
            {
                Send(new { t = "error", code = "busy", message = "This conversation already has a voice session." });
                return;
            }
            liveStarting = true;
        }
        var ct = lifetime.Token;
        try
        {
            if (voice.Unavailable is { } why) throw new OpenAILiveException(why);
            if (string.IsNullOrWhiteSpace(sdp) || !SafeSdp.IsMatch(sdp) || sdp.Length > 64 * 1024)
                throw new OpenAILiveException("The browser's WebRTC offer was not a valid SDP.");

            string chosen = LiveInstructions.ChooseVoice(requestedVoice, settings.LiveVoice);
            var history = LiveInstructions.History(backend.RecentHistory(ConversationId, settings.HistoryMessages), settings.HistoryMessages);
            var config = LiveInstructions.Session(settings, chosen, history);

            var clock = Stopwatch.StartNew();
            var created = await voice.CreateSessionAsync(config, sdp, ct);
            RecordStage("talk.liveCreate", clock.ElapsedMilliseconds);
            lock (gate)
            {
                liveSessionId = created.SessionId;
                liveVoice = chosen;
                liveStarted = DateTimeOffset.UtcNow;
            }
            Send(new { t = "answer", sdp = created.AnswerSdp, sessionId = created.SessionId, model = settings.LiveModel, voice = chosen, history = history.Count });

            var attached = await voice.AttachAsync(created.SessionId, ct);
            lock (gate) sideband = attached;
            RecordStage("talk.liveAttach", clock.ElapsedMilliseconds);
            _ = Task.Run(() => SidebandLoopAsync(attached));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            bool started;
            lock (gate) started = liveSessionId != null;
            string message = ex is OpenAILiveException ? ex.Message : "Could not start the voice: " + ex.Message;
            Send(new { t = "error", code = started ? "sideband" : "openai", message, fatal = true });
            if (!(ex is OpenAILiveException)) backend.LogError(ex, "[KliveAgent] Could not start a GPT-Live session.");
            lock (gate) liveError = message;
            if (started) await CloseLiveAsync("sideband_failed");
        }
        finally
        {
            lock (gate) liveStarting = false;
        }
    }

    private async Task SidebandLoopAsync(IOpenAILiveSideband live)
    {
        try
        {
            await foreach (var evt in live.ReadEventsAsync(lifetime.Token))
            {
                try { HandleLiveEvent(evt); }
                catch (Exception ex) { backend.LogError(ex, "[KliveAgent] Voice: a GPT-Live event could not be handled."); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { backend.LogError(ex, "[KliveAgent] Voice: the GPT-Live sideband failed."); }

        bool lost;
        lock (gate) lost = closedReason == null && !lifetime.IsCancellationRequested;
        if (lost)
        {
            lock (gate) closedReason = "connection_lost";
            Send(new { t = "closed", reason = "connection_lost" });
            closedSignal.TrySetResult();
            RecordUsageOnce("network", "The sideband closed before GPT-Live said the session ended.");
        }
    }

    internal void HandleLiveEvent(JObject evt)
    {
        switch ((string?)evt["type"])
        {
            case "session.started":
                Send(new { t = "live", state = "started", expiresAt = (long?)evt["session"]?["expires_at"] });
                break;
            case "session.input_transcript.delta":
                transcript.AddUser((string?)evt["delta"], (long?)evt["start_ms"] ?? 0, (long?)evt["end_ms"] ?? 0);
                OnHeard();
                break;
            case "session.output_transcript.delta":
                transcript.AddVoice((string?)evt["delta"], (long?)evt["start_ms"] ?? 0, (long?)evt["end_ms"] ?? 0);
                break;
            case "session.delegation.created":
                {
                    var delegation = evt["delegation"];
                    string? id = (string?)delegation?["id"];
                    string target = (string?)delegation?["target"] ?? "client";
                    if (!string.IsNullOrWhiteSpace(id) && target == "client")
                        _ = HandleDelegationAsync(id!, (long?)evt["offset_ms"] ?? 0);
                    break;
                }
            case "session.usage.updated":
                {
                    var latest = OpenAILiveUsage.From(evt["usage"], evt["context_window"]);
                    if (latest == null) break;
                    bool due;
                    lock (gate)
                    {
                        usage = latest;
                        due = Stopwatch.GetElapsedTime(lastUsageSentTicks) >= UsageEvery || lastUsageSentTicks == 0;
                        if (due) lastUsageSentTicks = Stopwatch.GetTimestamp();
                    }
                    if (due) SendUsage(latest, final: false);
                    break;
                }
            case "session.paused":
                Send(new { t = "standby", paused = true });
                break;
            case "session.resumed":
                Send(new { t = "standby", paused = false });
                RenewReserve();
                break;
            case "session.closed":
                {
                    string reason = (string?)evt["reason"] ?? "closed";
                    var final = OpenAILiveUsage.From(evt["usage"]);
                    lock (gate)
                    {
                        closedReason = reason;
                        if (final != null) { usage = final; usageFinal = true; }
                    }
                    if (final != null) SendUsage(final, final: true);
                    Send(new { t = "closed", reason });
                    closedSignal.TrySetResult();
                    bool normal = reason is "close_requested" or "remote_hangup" or "expired";
                    RecordUsageOnce(normal ? LlmOutcome.Ok : LlmOutcome.Error, normal ? null : "GPT-Live closed the session: " + reason);
                    break;
                }
            case "error":
                {
                    var error = evt["error"];
                    string code = (string?)error?["code"] ?? (string?)error?["type"] ?? "error";
                    string message = (string?)error?["message"] ?? "GPT-Live reported an error.";
                    Send(new { t = "error", code = "live:" + code, message });
                    break;
                }
        }
    }

    private void SendUsage(OpenAILiveUsage u, bool final) => Send(new
    {
        t = "usage",
        seconds = Math.Round(u.Seconds, 1),
        activeSeconds = u.ActiveSeconds,
        pausedSeconds = u.PausedSeconds,
        context = u.ContextRatio,
        costUsd = Math.Round(u.CostUsd(settings.ActivePerMinuteUsd, settings.StandbyPerMinuteUsd), 4),
        final,
    });

    /// <summary>Klives is speaking: open (or confirm) the warm agent conversation while he talks, so its
    /// prefill happens during his sentence rather than after it. A no-op while it is warm.</summary>
    private void OnHeard()
    {
        bool warm;
        lock (gate)
        {
            warm = !runActive && (lastWarmTicks == 0 || Stopwatch.GetElapsedTime(lastWarmTicks) >= WarmEvery);
            if (warm) lastWarmTicks = Stopwatch.GetTimestamp();
        }
        RenewReserve();
        if (warm) _ = backend.EnsureWarmAsync(ConversationId, this, lifetime.Token);
    }

    // ── Delegation: GPT-Live asks KliveAgent ──

    internal async Task HandleDelegationAsync(string id, long offsetMs)
    {
        lock (gate)
        {
            delegationId = id;
            delegationTicks = Stopwatch.GetTimestamp();
            firstWordsRecorded = false;
            // A hushed reply stays hushed while its run goes on; what answers this request is heard
            // (a new run, or the running one's next model call once it takes this as steering).
            if (!runActive) suppressRun = false;
        }
        Send(new { t = "delegation", id });

        // The delegation can arrive a moment before the transcript of his last words: wait for them.
        var clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed < SettleMax
                   && (transcript.SinceLastUserWords < SettleQuiet || transcript.PendingUserText().Length == 0))
                await Task.Delay(40, lifetime.Token);
        }
        catch (OperationCanceledException) { return; }
        RecordStage("talk.delegationSettle", clock.ElapsedMilliseconds);

        var task = transcript.TakeTask(offsetMs);
        if (task == null || string.IsNullOrWhiteSpace(task.Request))
        {
            QueueLive(live => live.AppendCommentaryAsync(id, "Sorry, I didn't catch that. Could you say it again?"));
            return;
        }
        Send(new { t = "heard", text = task.Request });

        switch (TalkText.MatchCommand(task.Request))
        {
            case TalkCommand.Stop:
                StopRun(spoken: true, heard: task.Request);
                return;
            case TalkCommand.Repeat:
                {
                    string reply;
                    lock (gate) reply = lastReply;
                    QueueLive(live => live.AppendCommentaryAsync(id, reply.Length > 0 ? reply : "There's nothing to repeat yet."));
                    Send(new { t = "command", command = "repeat", text = task.Request });
                    return;
                }
        }

        lock (gate) turnNote = task.Context == null ? null : "[On the call, before this]\n" + task.Context;
        await DispatchAsync(task.Request, resubmitOf: null);
    }

    private async Task DispatchAsync(string text, string? resubmitOf)
    {
        string clientMessageId = "spk_" + Guid.NewGuid().ToString("N")[..24];
        lock (gate) ourMessageIds.Add(clientMessageId);
        SendState("thinking");
        TalkTurnReceipt receipt;
        try { receipt = await backend.QueueTurnAsync(ConversationId, text, clientMessageId); }
        catch (Exception ex) { receipt = new TalkTurnReceipt(null, false, false, ex.Message); }

        string? id;
        lock (gate)
        {
            id = delegationId;
            if (!string.IsNullOrWhiteSpace(receipt.RequestId)) activeRequestId = receipt.RequestId;
            if (receipt.Steering) steeringPending = true;
            // A note only reaches a run that starts for this message; steering carries the words alone.
            if (!receipt.Accepted || receipt.Steering) turnNote = null;
        }
        Send(new
        {
            t = "turn",
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
            string why = receipt.Error ?? "KliveAgent did not accept the request.";
            QueueLive(live => live.AppendCommentaryAsync(id, "KliveAgent couldn't take that: " + Clip(why, 200)));
            Send(new { t = "error", code = "agent", message = why });
            SendState("listening");
        }
        else if (receipt.Steering)
        {
            QueueLive(live => live.AppendThinkingAsync(id, "[KliveAgent was already working; it has taken this as a change to what it is doing.]"));
        }
    }

    /// <summary>"Stop" / "never mind", or the stop button: cancel the run, and say so only when it was asked aloud.</summary>
    private void StopRun(bool spoken, string? heard = null)
    {
        string? requestId, id;
        lock (gate)
        {
            requestId = activeRequestId;
            id = delegationId;
            suppressRun = true;
            pendingSpeech.Clear();
            segmenter.Reset();
        }
        bool stopped = !string.IsNullOrWhiteSpace(requestId) && backend.CancelRun(requestId!);
        Send(new { t = "command", command = "stop", text = heard, stoppedRun = stopped });
        if (spoken)
            QueueLive(live => live.AppendCommentaryAsync(id, stopped ? "Stopped." : "There was nothing running to stop."));
        else if (stopped)
            QueueLive(live => live.AppendThinkingAsync(id, "[Klives stopped KliveAgent's work from the screen.]"));
        SendState("listening");
    }

    /// <summary>The stop-speaking button: GPT-Live is told to stop talking, and the rest of the current reply stays unsaid.</summary>
    private void Hush()
    {
        lock (gate)
        {
            suppressRun = true;
            pendingSpeech.Clear();
            segmenter.Reset();
        }
        QueueLive(live => live.AppendInstructionsAsync("Klives pressed stop. Stop speaking now, and do not carry on with the last answer unless he asks."));
    }

    // ── ITalkListener: KliveAgent's reply, handed to the voice ──

    public void OnRunStarted()
    {
        lock (gate)
        {
            runActive = true;
            spokeThisRun = false;
            spokenThisRun.Clear();
            pendingSpeech.Clear();
            segmenter.Reset();
            currentTool = null;
            runStartedTicks = Stopwatch.GetTimestamp();
            lastProgressTicks = 0;
        }
        SendState("thinking");
        RenewReserve();
    }

    public void OnModelTurnStarted()
    {
        lock (gate)
        {
            // The run is about to answer what he said over the last reply: that answer must be heard.
            if (steeringPending && suppressRun)
            {
                suppressRun = false;
                pendingSpeech.Clear();
                segmenter.Reset();
            }
            steeringPending = false;
        }
    }

    public void OnSpeakable(string delta)
    {
        bool flushNow;
        lock (gate)
        {
            if (suppressRun) return;
            if (!runActive) { runActive = true; runStartedTicks = Stopwatch.GetTimestamp(); }
            lastSpeakableTicks = Stopwatch.GetTimestamp();
            QueueSpeechLocked(segmenter.Append(delta));
            // The first words of a reply are the latency that matters: they go at once.
            flushNow = !spokeThisRun && pendingSpeech.Length > 0;
        }
        if (flushNow) FlushSpeech();
    }

    public void OnModelTurnEnded(bool calledTools)
    {
        lock (gate)
        {
            if (suppressRun) return;
            QueueSpeechLocked(segmenter.Flush());
        }
        FlushSpeech();
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
        bool note;
        string? id;
        lock (gate)
        {
            currentTool = label;
            id = delegationId;
            note = lastToolNoteTicks == 0 || Stopwatch.GetElapsedTime(lastToolNoteTicks) >= ToolNoteEvery;
            if (note) lastToolNoteTicks = Stopwatch.GetTimestamp();
        }
        // Silent context, so "what are you doing?" has an answer; spoken progress is Tick's job.
        if (note) QueueLive(live => live.AppendThinkingAsync(id, $"[KliveAgent is working: {Humanise(label)}.]"));
    }

    public void OnToolFinished(string label, bool ok)
    {
        Send(new { t = "tool", phase = "end", name = label, ok });
        lock (gate) if (currentTool == label) currentTool = null;
    }

    public void OnRunEnded(bool success)
    {
        string? requestId, id;
        bool failedSilently;
        lock (gate)
        {
            requestId = activeRequestId;
            id = delegationId;
            steeringPending = false;
            if (!suppressRun) QueueSpeechLocked(segmenter.Flush());
            failedSilently = !success && !spokeThisRun && !suppressRun && pendingSpeech.Length == 0;
            runActive = false;
            currentTool = null;
        }
        FlushSpeech();
        if (failedSilently)
            QueueLive(live => live.AppendCommentaryAsync(id, "That didn't work. The details are on screen."));
        lock (gate)
        {
            if (spokenThisRun.Length > 0 && !suppressRun) lastReply = spokenThisRun.ToString();
        }
        // There is no completion event for a client delegation: say in words that nothing more is coming.
        QueueLive(live => live.AppendThinkingAsync(id, success
            ? "[KliveAgent has finished this request.]"
            : "[KliveAgent stopped on this request without finishing it.]"));
        SendState("listening");
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

    private void QueueSpeechLocked(IReadOnlyList<string> segments)
    {
        foreach (string segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;
            if (pendingSpeech.Length == 0) pendingSinceTicks = Stopwatch.GetTimestamp();
            else pendingSpeech.Append(' ');
            pendingSpeech.Append(segment.Trim());
        }
    }

    /// <summary>Hands whatever is ready to the voice as one commentary append.</summary>
    private void FlushSpeech()
    {
        string text;
        string? id;
        long sinceDelegationMs = -1;
        lock (gate)
        {
            if (pendingSpeech.Length == 0 || suppressRun) { pendingSpeech.Clear(); return; }
            text = pendingSpeech.ToString();
            pendingSpeech.Clear();
            id = delegationId;
            spokeThisRun = true;
            if (spokenThisRun.Length > 0) spokenThisRun.Append(' ');
            spokenThisRun.Append(text);
            lastSaidTicks = Stopwatch.GetTimestamp();
            if (!firstWordsRecorded && delegationTicks != 0)
            {
                firstWordsRecorded = true;
                sinceDelegationMs = (long)Stopwatch.GetElapsedTime(delegationTicks).TotalMilliseconds;
            }
        }
        if (sinceDelegationMs >= 0) RecordStage("talk.firstWords", sinceDelegationMs);
        Send(new { t = "said", text });
        QueueLive(live => live.AppendCommentaryAsync(id, text));
    }

    // ── GPT-Live commands, in order ──

    private void QueueLive(Func<IOpenAILiveSideband, Task> command) => liveOutbox.Writer.TryWrite(command);

    private async Task LiveOutboxLoopAsync()
    {
        try
        {
            await foreach (var command in liveOutbox.Reader.ReadAllAsync())
            {
                IOpenAILiveSideband? live;
                lock (gate) live = sideband;
                if (live == null || !live.IsOpen) continue;
                try { await command(live); }
                catch (Exception ex) when (ex is OpenAILiveException or WebSocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // The session is going away; the close path reports it.
                }
                catch (Exception ex) { backend.LogError(ex, "[KliveAgent] Voice: a command for GPT-Live failed."); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task CloseLiveAsync(string why)
    {
        IOpenAILiveSideband? live;
        bool closed;
        lock (gate)
        {
            live = sideband;
            closed = closedReason != null;
        }
        if (live != null)
        {
            if (!closed && live.IsOpen)
            {
                try
                {
                    await live.CloseSessionAsync();
                    await closedSignal.Task.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch { /* closing anyway */ }
            }
            lock (gate) sideband = null;
            try { await live.DisposeAsync(); } catch { }
        }
        bool started;
        lock (gate) started = liveSessionId != null;
        if (started) RecordUsageOnce(closed || why is "client_closed" or "hangup" or "taken_over" ? LlmOutcome.Ok : LlmOutcome.Error, null);
    }

    private void RecordUsageOnce(string outcome, string? error)
    {
        if (Interlocked.Exchange(ref recorded, 1) != 0) return;
        string? id;
        DateTimeOffset? started;
        OpenAILiveUsage? u;
        bool final;
        lock (gate)
        {
            id = liveSessionId;
            started = liveStarted;
            u = usage;
            final = usageFinal;
            error ??= liveError;
        }
        if (id == null || started == null) return;
        try
        {
            voice.RecordSession(id, settings.LiveModel, started.Value, DateTimeOffset.UtcNow, u, final,
                settings.ActivePerMinuteUsd, settings.StandbyPerMinuteUsd, outcome, error);
        }
        catch { /* the ledger is best-effort */ }
    }

    // ── Housekeeping: stalled speech, coalescing, progress, prepared context, pings ──

    private async Task HousekeepingLoopAsync()
    {
        var ct = lifetime.Token;
        long lastPing = Stopwatch.GetTimestamp();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);
                Tick();
                if (Stopwatch.GetElapsedTime(lastPing) >= PingEvery)
                {
                    lastPing = Stopwatch.GetTimestamp();
                    Send(new { t = "ping" });
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal void Tick()
    {
        bool flush = false;
        string? progress = null;
        string? id = null;
        string? prepareFor = null;
        lock (gate)
        {
            if (runActive && !suppressRun)
            {
                // A reply whose stream paused mid-sentence: say the words so far.
                if (segmenter.PendingWords >= SpeechSegmenter.StallMinWords && Stopwatch.GetElapsedTime(lastSpeakableTicks) >= StallAfter)
                    QueueSpeechLocked(segmenter.TakeStalled());
                if (pendingSpeech.Length > 0 && Stopwatch.GetElapsedTime(pendingSinceTicks) >= CoalesceFor) flush = true;

                // Working a while with nothing said: tell him what it is doing.
                if (settings.ProgressSeconds > 0)
                {
                    var quietFor = TimeSpan.FromSeconds(settings.ProgressSeconds);
                    long since = Math.Max(Math.Max(lastSaidTicks, runStartedTicks), lastProgressTicks);
                    if (since != 0 && Stopwatch.GetElapsedTime(since) >= quietFor)
                    {
                        lastProgressTicks = Stopwatch.GetTimestamp();
                        id = delegationId;
                        progress = currentTool != null
                            ? $"Still working on it: {Humanise(currentTool)}."
                            : "Still working on it.";
                    }
                }
            }

            // He has paused: fetch the memory and knowledge his words will need while the voice decides.
            if (!runActive)
            {
                string pending = transcript.PendingUserText();
                if (pending.Length > 0 && pending != preparedPending && transcript.SinceLastUserWords >= SettleQuiet)
                {
                    preparedPending = pending;
                    prepareFor = pending;
                }
                else if (pending.Length == 0) preparedPending = string.Empty;
            }
        }
        if (flush) FlushSpeech();
        if (progress != null) QueueLive(live => live.AppendCommentaryAsync(id, progress));
        if (prepareFor != null)
        {
            var work = backend.PrepareContextAsync(prepareFor);
            lock (gate)
            {
                preparedFor = prepareFor;
                preparedContext = work;
            }
        }
    }

    /// <summary>
    /// A spoken message that arrived while a run was finishing is accepted as steering; if the run ended
    /// without applying it, it would be silently lost. Send it again as a new turn instead.
    /// </summary>
    private async Task ResubmitRejectedSteeringAsync(string requestId)
    {
        try
        {
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
                await DispatchAsync(text, resubmitOf: id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            backend.LogError(ex, "[KliveAgent] Voice: could not re-send lost steering.");
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
        voice.EngageReserve(ReserveOwner, TimeSpan.FromMinutes(settings.ReserveIdleMinutes));
    }

    private void RecordStage(string stage, long ms)
    {
        try { backend.RecordStage(stage, ms); } catch { }
    }

    /// <summary>A tool label as words ("running read_logs" → "read logs").</summary>
    internal static string Humanise(string? label)
    {
        string t = (label ?? string.Empty).Trim();
        if (t.StartsWith("running ", StringComparison.OrdinalIgnoreCase)) t = t[8..];
        t = SpeechSegmenter.ToSpeech(t.Replace('_', ' '));
        return t.Length > 120 ? t[..120] : t;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
