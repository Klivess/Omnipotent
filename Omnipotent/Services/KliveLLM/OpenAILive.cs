using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.KliveLLM;

/// <summary>A GPT-Live call that failed: the HTTP status (or null for a socket failure) and OpenAI's error code.</summary>
public sealed class OpenAILiveException : Exception
{
    public OpenAILiveException(string message, HttpStatusCode? status = null, string? code = null, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        Code = code;
    }

    public HttpStatusCode? Status { get; }
    public string? Code { get; }

    /// <summary>The key is missing, wrong, or not allowed to use the model.</summary>
    public bool IsAuth => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    public bool RateLimited => Status == HttpStatusCode.TooManyRequests;
}

/// <summary>A GPT-Live session OpenAI accepted over WebRTC: its id, and the SDP answer for the browser.</summary>
public sealed record OpenAILiveCreated(string SessionId, string AnswerSdp);

/// <summary>Cumulative GPT-Live audio usage, as <c>session.usage.updated</c> and <c>session.closed</c> report it.</summary>
public sealed record OpenAILiveUsage(double Seconds, double? ActiveSeconds, double? PausedSeconds, double? ContextRatio)
{
    public static OpenAILiveUsage? From(JToken? usage, JToken? contextWindow = null)
    {
        if (usage is not JObject u) return null;
        double seconds = (double?)u["seconds"] ?? 0;
        return new OpenAILiveUsage(seconds, (double?)u["active_seconds"], (double?)u["paused_seconds"],
            (double?)contextWindow?["usage_ratio"]);
    }

    /// <summary>What these seconds cost at the given per-minute prices. Time not reported as standby is
    /// billed as active, which is what OpenAI does when a session never pauses.</summary>
    public double CostUsd(double activePerMinute, double standbyPerMinute)
    {
        double paused = Math.Max(0, PausedSeconds ?? 0);
        double active = ActiveSeconds ?? Math.Max(0, Seconds - paused);
        return (Math.Max(0, active) * activePerMinute + paused * standbyPerMinute) / 60.0;
    }
}

/// <summary>
/// OpenAI's GPT-Live API (<c>/v1/live/sessions</c>, Nov 2026): a full-duplex voice model that listens and
/// speaks at once and hands reasoning and tool use to a backend agent ("delegation").
///
/// KliveAgent uses it the way the docs recommend for a browser: the page sends its WebRTC offer to our
/// server, the server creates the session here (the API key and the session configuration never reach
/// the browser) and returns OpenAI's answer, and the server then attaches a <b>sideband</b> WebSocket to
/// the same session to run client delegation with KliveAgent. Audio goes straight between the browser
/// and OpenAI; nothing here touches it.
///
/// The model id is whatever the caller configures (KliveAgent_LiveModel, default gpt-live-1.1). Nothing
/// in this client depends on a particular model.
/// </summary>
public sealed class OpenAILiveClient
{
    /// <summary>GPT-Live's built-in voices (Nov 2026 reference enum). Anything else is treated as a custom voice id.</summary>
    public static readonly IReadOnlyList<string> BuiltInVoices = new[]
    {
        "alloy", "ash", "ballad", "beacon", "bossa", "brise", "calandria", "cedar", "cinder", "compas", "coral",
        "delta", "destello", "echo", "flitz", "gleam", "halo", "harema", "hornero", "juni", "luciernaga", "marin",
        "meridian", "nira", "noeul", "nuri", "pearl", "quartz", "ripple", "sage", "shida", "shimmer", "sillage",
        "stone", "tempo", "verse", "vesper", "willow",
    };

    public const string DefaultVoice = "marin";

    private static readonly HttpClient sharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly Func<CancellationToken, Task<(string Key, string BaseUrl)>> credentials;
    private readonly HttpClient http;

    public OpenAILiveClient(Func<CancellationToken, Task<(string Key, string BaseUrl)>> credentials, HttpClient? http = null)
    {
        this.credentials = credentials;
        this.http = http ?? sharedHttp;
    }

    /// <summary>Test seam: opens the sideband socket (the live one is a ClientWebSocket with the Bearer header).</summary>
    internal Func<Uri, string, CancellationToken, Task<WebSocket>>? ConnectOverride { get; set; }

    /// <summary>The voice as GPT-Live's <c>audio.output.voice</c> takes it: a built-in name, or <c>{ id }</c> for a custom voice.</summary>
    public static JToken VoiceToken(string? voice)
    {
        string v = string.IsNullOrWhiteSpace(voice) ? DefaultVoice : voice.Trim();
        return BuiltInVoices.Contains(v, StringComparer.OrdinalIgnoreCase)
            ? new JValue(v.ToLowerInvariant())
            : new JObject { ["id"] = v };
    }

    /// <summary>
    /// <c>POST /v1/live/sessions</c> with the browser's SDP offer. OpenAI answers 201 with the session id
    /// and its SDP answer; the session runs until it is closed or expires.
    /// </summary>
    public async Task<OpenAILiveCreated> CreateWebRtcSessionAsync(JObject session, string offerSdp, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(offerSdp)) throw new OpenAILiveException("The browser sent no SDP offer.");
        var (key, baseUrl) = await credentials(cancellationToken);
        var body = new JObject
        {
            ["session"] = session,
            ["transport"] = new JObject { ["type"] = "webrtc", ["sdp"] = offerSdp },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, SessionsUrl(baseUrl))
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        HttpResponseMessage response;
        try { response = await http.SendAsync(request, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new OpenAILiveException("Could not reach OpenAI: " + ex.Message, inner: ex); }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode, text);
            JObject json;
            try { json = JObject.Parse(text); }
            catch (Exception ex) { throw new OpenAILiveException("OpenAI answered with something that is not JSON.", response.StatusCode, inner: ex); }
            string? id = (string?)json["session"]?["id"];
            string? sdp = (string?)json["transport"]?["sdp"];
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(sdp))
                throw new OpenAILiveException("OpenAI's answer had no session id or SDP.", response.StatusCode);
            return new OpenAILiveCreated(id!, sdp!);
        }
    }

    /// <summary>Attaches the server's sideband to a running session (<c>wss://…/live/sessions/{id}/attach</c>).
    /// Nothing is sent first: attach does not create a session.</summary>
    public async Task<OpenAILiveSideband> AttachAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new OpenAILiveException("No session to attach to.");
        var (key, baseUrl) = await credentials(cancellationToken);
        var uri = AttachUrl(baseUrl, sessionId);
        try
        {
            WebSocket socket;
            if (ConnectOverride != null) socket = await ConnectOverride(uri, key, cancellationToken);
            else
            {
                var client = new ClientWebSocket();
                client.Options.SetRequestHeader("Authorization", "Bearer " + key);
                client.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await client.ConnectAsync(uri, cancellationToken);
                socket = client;
            }
            return new OpenAILiveSideband(socket, sessionId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (WebSocketException ex)
        {
            throw new OpenAILiveException("Could not attach to the GPT-Live session: " + ex.Message, inner: ex);
        }
    }

    internal static string SessionsUrl(string? baseUrl) => NormaliseBase(baseUrl) + "/live/sessions";

    internal static Uri AttachUrl(string? baseUrl, string sessionId)
    {
        string http = NormaliseBase(baseUrl);
        string ws = http.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" + http[8..]
            : http.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws://" + http[7..]
            : http;
        return new Uri($"{ws}/live/sessions/{Uri.EscapeDataString(sessionId)}/attach");
    }

    /// <summary>"https://api.openai.com/v1/" and "https://api.openai.com/v1/chat/completions" both mean the v1 base.</summary>
    internal static string NormaliseBase(string? baseUrl)
    {
        string url = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (url.Length == 0) url = KliveLLM.DefaultOpenAIBaseUrl;
        foreach (string suffix in new[] { "/chat/completions", "/responses", "/live/sessions" })
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) url = url[..^suffix.Length];
        return url.TrimEnd('/');
    }

    private static OpenAILiveException Failure(HttpStatusCode status, string body)
    {
        string? message = null, code = null;
        try
        {
            var json = JObject.Parse(body);
            message = (string?)json["error"]?["message"] ?? (string?)json["message"];
            code = (string?)json["error"]?["code"] ?? (string?)json["error"]?["type"];
        }
        catch { /* not JSON */ }
        message ??= string.IsNullOrWhiteSpace(body) ? status.ToString() : (body.Length > 300 ? body[..300] : body);
        string prefix = status switch
        {
            HttpStatusCode.Unauthorized => "OpenAI refused the key (OpenAILLMToken): ",
            HttpStatusCode.Forbidden => "OpenAI would not let this key start a GPT-Live session: ",
            HttpStatusCode.TooManyRequests => "OpenAI is rate-limiting GPT-Live sessions: ",
            _ => $"OpenAI refused the GPT-Live session ({(int)status}): ",
        };
        return new OpenAILiveException(prefix + message, status, code);
    }
}

/// <summary>What a voice session needs from a GPT-Live sideband (so it can be exercised without OpenAI).</summary>
public interface IOpenAILiveSideband : IAsyncDisposable
{
    string SessionId { get; }
    bool IsOpen { get; }
    IAsyncEnumerable<JObject> ReadEventsAsync(CancellationToken cancellationToken);
    Task AppendThinkingAsync(string? delegationId, string content, CancellationToken cancellationToken = default);
    Task AppendCommentaryAsync(string? delegationId, string content, CancellationToken cancellationToken = default);
    Task AppendInstructionsAsync(string content, CancellationToken cancellationToken = default);
    Task CloseSessionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The server's sideband to one GPT-Live session: every conversation and delegation event, and the
/// commands that steer it (thinking, commentary, instructions, mute, close). Events are read on one
/// loop and handed out in order; sends are serialised.
/// </summary>
public sealed class OpenAILiveSideband : IOpenAILiveSideband
{
    /// <summary>GPT-Live's limit is 500 tokens per append; a character budget comfortably under it.</summary>
    public const int MaxAppendChars = 1600;

    private readonly WebSocket socket;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Queue<string> seenOrder = new();
    private int nextEventId;

    internal OpenAILiveSideband(WebSocket socket, string sessionId)
    {
        this.socket = socket;
        SessionId = sessionId;
    }

    public string SessionId { get; }

    public bool IsOpen => socket.State == WebSocketState.Open;

    /// <summary>
    /// The session's events, in order, until the socket closes. Reflected audio (base64 PCM in both
    /// directions) is dropped unparsed; events the attach replay repeats are dropped by event_id.
    /// </summary>
    public async IAsyncEnumerable<JObject> ReadEventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try { result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken); }
            catch (OperationCanceledException) { yield break; }
            catch (WebSocketException) { yield break; }
            if (result.MessageType == WebSocketMessageType.Close) yield break;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var bytes = message.GetBuffer();
            int length = (int)message.Length;
            message.SetLength(0);
            if (result.MessageType != WebSocketMessageType.Text) continue;
            if (IsReflectedAudio(bytes, length)) continue;

            JObject evt;
            try { evt = JObject.Parse(Encoding.UTF8.GetString(bytes, 0, length)); }
            catch { continue; }
            if (IsReplay((string?)evt["event_id"])) continue;
            yield return evt;
        }
    }

    /// <summary>Cheap check on the first bytes: the two audio reflections are by far the largest and most
    /// frequent messages, and the sideband has no use for them.</summary>
    internal static bool IsReflectedAudio(byte[] bytes, int length)
    {
        int window = Math.Min(length, 160);
        string head = Encoding.ASCII.GetString(bytes, 0, window);
        return head.Contains("\"session.output_audio.delta\"", StringComparison.Ordinal)
            || head.Contains("\"session.input_audio.append\"", StringComparison.Ordinal);
    }

    private bool IsReplay(string? eventId)
    {
        if (string.IsNullOrEmpty(eventId)) return false;
        lock (seen)
        {
            if (!seen.Add(eventId)) return true;
            seenOrder.Enqueue(eventId);
            while (seenOrder.Count > 1024) seen.Remove(seenOrder.Dequeue());
            return false;
        }
    }

    public string NewEventId() => "kla_" + Interlocked.Increment(ref nextEventId).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public async Task SendAsync(JObject command, CancellationToken cancellationToken = default)
    {
        if (command["event_id"] == null) command["event_id"] = NewEventId();
        byte[] bytes = Encoding.UTF8.GetBytes(command.ToString(Formatting.None));
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State != WebSocketState.Open) throw new OpenAILiveException("The GPT-Live sideband is closed.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token);
        }
        finally { sendLock.Release(); }
    }

    /// <summary>Silent context for the voice: it may use it later, it does not say it now. An empty
    /// <paramref name="content"/> cancels the automatic acknowledgement still pending.</summary>
    public Task AppendThinkingAsync(string? delegationId, string content, CancellationToken cancellationToken = default)
        => AppendAsync("session.thinking.append", delegationId, content, cancellationToken);

    /// <summary>Something for the voice to say now (in its own words).</summary>
    public Task AppendCommentaryAsync(string? delegationId, string content, CancellationToken cancellationToken = default)
        => AppendAsync("session.commentary.append", delegationId, content, cancellationToken);

    /// <summary>A trusted, session-wide instruction. Can interrupt speech in progress.</summary>
    public Task AppendInstructionsAsync(string content, CancellationToken cancellationToken = default)
        => AppendAsync("session.instructions.append", null, content, cancellationToken);

    public Task CloseSessionAsync(CancellationToken cancellationToken = default)
        => SendAsync(new JObject { ["type"] = "session.close" }, cancellationToken);

    private async Task AppendAsync(string type, string? delegationId, string content, CancellationToken cancellationToken)
    {
        foreach (string part in SplitForAppend(content))
        {
            await SendAsync(new JObject
            {
                ["type"] = type,
                ["delegation_id"] = delegationId == null ? JValue.CreateNull() : new JValue(delegationId),
                ["content"] = part,
            }, cancellationToken);
        }
    }

    /// <summary>Content split at sentence (else word) boundaries into appends GPT-Live accepts. Empty
    /// content stays one empty append (that is a meaningful command).</summary>
    internal static IReadOnlyList<string> SplitForAppend(string? content)
    {
        string text = content ?? string.Empty;
        if (text.Length <= MaxAppendChars) return new[] { text };
        var parts = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            int remaining = text.Length - start;
            if (remaining <= MaxAppendChars) { parts.Add(text[start..].Trim()); break; }
            int window = start + MaxAppendChars;
            int cut = -1;
            for (int i = window - 1; i > start + MaxAppendChars / 2; i--)
                if (text[i] is '.' or '?' or '!' or '\n' && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]))) { cut = i + 1; break; }
            if (cut < 0) cut = text.LastIndexOf(' ', window - 1, MaxAppendChars / 2) is int sp and > 0 ? sp + 1 : window;
            parts.Add(text[start..cut].Trim());
            start = cut;
        }
        return parts.Where(p => p.Length > 0).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
            }
        }
        catch { }
        socket.Dispose();
        sendLock.Dispose();
    }
}
