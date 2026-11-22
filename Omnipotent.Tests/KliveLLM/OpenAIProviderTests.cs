using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAgent.Talk;
using Omnipotent.Services.KliveLLM;
using LlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.KliveLLM;

/// <summary>
/// OpenAI as a KliveLLM provider (its router, its wire rules) and the GPT-Live client KliveAgent's voice
/// is built on: the create request, the attach address, the sideband's event filtering and splitting,
/// and what a session costs.
/// </summary>
public sealed class OpenAIProviderTests
{
    // ── The router ──

    [Fact]
    public void OpenAI_IsABuiltInRouter_WithItsOwnKeyAndModelSettings()
    {
        var routers = LlmRouterRegistry.Merge(null);
        var openai = routers[LlmRouterIds.OpenAI];
        Assert.True(openai.BuiltIn);
        Assert.Equal(LlmBilling.PerToken, openai.Billing);
        Assert.Equal("OpenAILLMToken", openai.KeySetting);
        Assert.Equal("OpenAIModelID", openai.ModelSetting);
        Assert.Equal(LlmRouterIds.OpenAI, LlmRouterRegistry.FromProvider(LlmService.LLMProvider.OpenAI));
        Assert.Equal(LlmService.LLMProvider.OpenAI, LlmService.ParseProvider("OpenAI"));
        Assert.Equal(LlmService.LLMProvider.OpenAI, LlmService.ParseProvider("openai"));
        // An added router named "OpenAI" can no longer take the built-in's id.
        Assert.NotEqual(LlmRouterIds.OpenAI, LlmRouterRegistry.NewId("OpenAI", new List<string>()));
    }

    [Fact]
    public void SettingNames_AreHiddenOnlyWhereTheyHoldASecret()
    {
        Assert.True(OmniGlobalSettingsManager.IsCredentialName("OpenAILLMToken"));
        foreach (string name in new[]
                 {
                     "OpenAIBaseURL", "OpenAIModelID", "KliveAgent_LiveModel", "KliveAgent_LiveVoice", "KliveAgent_LiveIdleSeconds",
                     "KliveAgent_LiveAckDelayMs", "KliveAgent_LiveExtraInstructions", "KliveAgent_LiveHistoryMessages",
                     "KliveAgent_LiveProgressSeconds", "KliveAgent_LiveActivePerMinuteUsd", "KliveAgent_LiveStandbyPerMinuteUsd",
                 })
            Assert.False(OmniGlobalSettingsManager.IsCredentialName(name), name);
    }

    [Theory]
    [InlineData("gpt-6-luna", "low", false, "low")]
    [InlineData("gpt-6-luna", "off", false, "none")]
    [InlineData("gpt-6.2-astra", "high", false, "high")]
    [InlineData("gpt-5-mini", "medium", false, "medium")]
    [InlineData("o4-mini", "low", false, "low")]
    [InlineData("openai/gpt-6-luna", "low", false, "low")]
    [InlineData("gpt-6-luna", "high", true, "none")]   // Chat Completions only calls functions at "none"
    [InlineData("gpt-4.1-mini", "high", false, null)] // not a reasoning model: nothing is sent
    [InlineData("gpt-4o", "low", true, null)]
    [InlineData("gpt-6-luna", null, false, null)]
    public void ReasoningEffort_FollowsTheModelFamily_AndToolCalls(string model, string? level, bool tools, string? expected)
    {
        Assert.Equal(expected, LlmService.OpenAIReasoningEffort(model, level, tools));
    }

    [Fact]
    public void MaxTokens_BecomesMaxCompletionTokens_ForOpenAIOnly()
    {
        var openai = new LlmService.RemoteLLMProviderConfiguration(LlmService.LLMProvider.OpenAI, "OpenAI", "https://api.openai.com/v1/chat/completions", "k", "gpt-6-luna");
        var payload = new HFWrapper.HFLLMInferenceRequest { model = "gpt-6-luna", max_tokens = 512 };
        LlmService.ApplyOpenAIWireRules(ref payload, openai);
        Assert.Null(payload.max_tokens);
        Assert.Equal(512, payload.max_completion_tokens);
        string json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
        Assert.Contains("\"max_completion_tokens\":512", json);
        Assert.DoesNotContain("\"max_tokens\"", json);

        var airouter = new LlmService.RemoteLLMProviderConfiguration(LlmService.LLMProvider.AIRouter, "AIRouter", "https://api.airouter.ch/v1/chat/completions", "k", "Qwen3.8");
        var other = new HFWrapper.HFLLMInferenceRequest { model = "Qwen3.8", max_tokens = 512 };
        LlmService.ApplyOpenAIWireRules(ref other, airouter);
        Assert.Equal(512, other.max_tokens);
        Assert.DoesNotContain("max_completion_tokens", Newtonsoft.Json.JsonConvert.SerializeObject(other));
    }

    [Fact]
    public void OpenAIPrices_FallBackToOpenRoutersListing()
    {
        var book = LlmPriceBook.InMemory();
        book.Save(new[] { new LlmPriceEntry { Router = LlmRouterIds.OpenRouter, Model = "openai/gpt-6-luna", InputPerMTok = 0.10, OutputPerMTok = 0.50 } });
        Assert.Equal(0.10, book.Find(LlmRouterIds.OpenAI, "gpt-6-luna")?.InputPerMTok);
        Assert.Null(book.Find(LlmRouterIds.AIRouter, "gpt-6-luna"));
    }

    // ── GPT-Live: creating a session ──

    [Fact]
    public async Task CreateSession_PostsTheOfferAndSession_ToLiveSessions_WithTheKey()
    {
        var handler = new Recorder(HttpStatusCode.Created, """{"session":{"id":"live_123"},"transport":{"type":"webrtc","sdp":"v=0\r\nanswer"}}""");
        var client = new OpenAILiveClient(_ => Task.FromResult(("sk-test", "https://api.openai.com/v1/")), new HttpClient(handler));

        var session = new JObject { ["model"] = "gpt-live-1.1" };
        var created = await client.CreateWebRtcSessionAsync(session, "v=0\r\noffer", CancellationToken.None);

        Assert.Equal("live_123", created.SessionId);
        Assert.Equal("v=0\r\nanswer", created.AnswerSdp);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://api.openai.com/v1/live/sessions", handler.Url);
        Assert.Equal("Bearer sk-test", handler.Authorization);
        var body = JObject.Parse(handler.Body!);
        Assert.Equal("gpt-live-1.1", (string?)body["session"]?["model"]);
        Assert.Equal("webrtc", (string?)body["transport"]?["type"]);
        Assert.Equal("v=0\r\noffer", (string?)body["transport"]?["sdp"]);
    }

    [Fact]
    public async Task CreateSession_RefusedKey_SaysWhichSettingToFix()
    {
        var handler = new Recorder(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided.","code":"invalid_api_key"}}""");
        var client = new OpenAILiveClient(_ => Task.FromResult(("bad", "https://api.openai.com/v1")), new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<OpenAILiveException>(() => client.CreateWebRtcSessionAsync(new JObject(), "v=0\r\n", CancellationToken.None));
        Assert.True(ex.IsAuth);
        Assert.Equal("invalid_api_key", ex.Code);
        Assert.Contains("OpenAILLMToken", ex.Message);
        Assert.Contains("Incorrect API key", ex.Message);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "wss://api.openai.com/v1/live/sessions/live_1/attach")]
    [InlineData("https://api.openai.com/v1/chat/completions", "wss://api.openai.com/v1/live/sessions/live_1/attach")]
    [InlineData("http://localhost:8080/v1/", "ws://localhost:8080/v1/live/sessions/live_1/attach")]
    [InlineData("", "wss://api.openai.com/v1/live/sessions/live_1/attach")]
    public void AttachUrl_IsTheSidebandAddressOfTheSession(string baseUrl, string expected)
    {
        Assert.Equal(expected, OpenAILiveClient.AttachUrl(baseUrl, "live_1").ToString());
    }

    [Fact]
    public void Voices_BuiltInByName_CustomById()
    {
        Assert.Equal("marin", OpenAILiveClient.VoiceToken(null).ToString());
        Assert.Equal("cedar", OpenAILiveClient.VoiceToken("Cedar").ToString());
        Assert.Equal("voice_abc123", (string?)OpenAILiveClient.VoiceToken("voice_abc123")["id"]);
    }

    // ── GPT-Live: the sideband ──

    [Fact]
    public async Task Sideband_DropsReflectedAudioAndReplays_AndSerialisesCommands()
    {
        var (server, client) = await SocketPairAsync();
        var sideband = new OpenAILiveSideband(client, "live_1");
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

        async Task ServerSend(string json) =>
            await server.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

        await ServerSend("""{"type":"session.output_audio.delta","delta":"AAAA","start_ms":1,"end_ms":2}""");
        await ServerSend("""{"type":"session.input_transcript.delta","event_id":"e1","delta":"Hi","start_ms":0,"end_ms":200}""");
        await ServerSend("""{"type":"session.input_transcript.delta","event_id":"e1","delta":"Hi","start_ms":0,"end_ms":200}""");
        await ServerSend("""{"type":"session.input_audio.append","audio":"AAAA"}""");
        await ServerSend("""{"type":"session.delegation.created","event_id":"e2","offset_ms":300,"delegation":{"id":"item_1","type":"delegation","target":"client"}}""");
        await server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct);

        var seen = new List<string>();
        await foreach (var evt in sideband.ReadEventsAsync(ct)) seen.Add((string)evt["type"]!);
        Assert.Equal(new[] { "session.input_transcript.delta", "session.delegation.created" }, seen);

        var (server2, client2) = await SocketPairAsync();
        var commands = new OpenAILiveSideband(client2, "live_2");
        await commands.AppendCommentaryAsync("item_1", "The trader is running.", ct);
        await commands.AppendThinkingAsync(null, "", ct);
        var first = await ReceiveJsonAsync(server2, ct);
        Assert.Equal("session.commentary.append", (string?)first["type"]);
        Assert.Equal("item_1", (string?)first["delegation_id"]);
        Assert.Equal("The trader is running.", (string?)first["content"]);
        Assert.StartsWith("kla_", (string?)first["event_id"]);
        var second = await ReceiveJsonAsync(server2, ct);
        Assert.Equal(JTokenType.Null, second["delegation_id"]!.Type);
        Assert.Equal("", (string?)second["content"]);
    }

    [Fact]
    public void LongContent_IsSplitAtSentences_UnderTheAppendLimit()
    {
        string sentence = "The fleet ran all night without a single failure. ";
        string text = string.Concat(Enumerable.Repeat(sentence, 80));
        var parts = OpenAILiveSideband.SplitForAppend(text);
        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.True(p.Length <= OpenAILiveSideband.MaxAppendChars));
        Assert.All(parts, p => Assert.EndsWith(".", p));
        Assert.Equal(new[] { "" }, OpenAILiveSideband.SplitForAppend(""));
    }

    [Fact]
    public void Usage_CostsActiveAndStandbyMinutes_AtTheirOwnPrices()
    {
        Assert.Equal(0.07, new OpenAILiveUsage(120, 60, 60, null).CostUsd(0.05, 0.02), 6);
        Assert.Equal(0.05, new OpenAILiveUsage(60, null, null, null).CostUsd(0.05, 0.02), 6); // never paused: all active
        var parsed = OpenAILiveUsage.From(JObject.Parse("""{"seconds":90,"paused_seconds":30}"""), JObject.Parse("""{"usage_ratio":0.25}"""))!;
        Assert.Equal(0.25, parsed.ContextRatio);
        Assert.Equal((60 * 0.05 + 30 * 0.02) / 60, parsed.CostUsd(0.05, 0.02), 6);
    }

    // ── helpers ──

    private sealed class Recorder(HttpStatusCode status, string response) : HttpMessageHandler
    {
        internal HttpMethod? Method;
        internal string? Url;
        internal string? Authorization;
        internal string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Url = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<(WebSocket Server, WebSocket Client)> SocketPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var clientTcp = new TcpClient();
        var connect = clientTcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var serverTcp = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        return (WebSocket.CreateFromStream(serverTcp.GetStream(), new WebSocketCreationOptions { IsServer = true }),
            WebSocket.CreateFromStream(clientTcp.GetStream(), new WebSocketCreationOptions { IsServer = false }));
    }

    private static async Task<JObject> ReceiveJsonAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return JObject.Parse(Encoding.UTF8.GetString(message.ToArray()));
        }
    }
}

/// <summary>What GPT-Live is told, and what a delegation asks for.</summary>
public sealed class LiveVoiceTextTests
{
    [Fact]
    public void Instructions_UseTheDelegateWording_ExceptOnTheFirstModel()
    {
        string latest = LiveInstructions.Build("gpt-live-1.1", "Klives, OmniTrader", "Call me Klives.");
        Assert.Contains("Delegate to the backend when:", latest);
        Assert.Contains("Do not delegate when:", latest);
        Assert.Contains("Klives, OmniTrader", latest);
        Assert.Contains("Call me Klives.", latest);
        Assert.DoesNotContain("SpawnThinking", latest);

        string first = LiveInstructions.Build("gpt-live-1", null, null);
        Assert.Contains("Generate <|SpawnThinking|> when:", first);
        Assert.Contains("Do not generate <|SpawnThinking|> when:", first);
        Assert.DoesNotContain("Delegate to the backend", first);
        Assert.Contains("Delegate to the backend", LiveInstructions.Build("gpt-live-2", null, null));
    }

    [Fact]
    public void History_KeepsTheNewestThatFit_OldestFirst_WithoutCode()
    {
        var messages = Enumerable.Range(1, 30).Select(i => (i % 2 == 1, $"message {i} ```csharp\nvar x = {i};\n``` see https://klive.dev/x")).ToList();
        var items = LiveInstructions.History(messages, 10);
        Assert.Equal(10, items.Count);
        Assert.StartsWith("message 21", (string?)items[0]["content"]![0]!["text"]);
        Assert.StartsWith("message 30", (string?)items[9]["content"]![0]!["text"]);
        Assert.Equal("user", (string?)items[0]["role"]);
        Assert.Equal("input_text", (string?)items[0]["content"]![0]!["type"]);
        Assert.Equal("assistant", (string?)items[9]["role"]);
        Assert.Equal("output_text", (string?)items[9]["content"]![0]!["type"]);
        string all = string.Join(" ", items.Select(i => (string?)i["content"]![0]!["text"]));
        Assert.DoesNotContain("var x", all);
        Assert.DoesNotContain("https://", all);
    }

    [Fact]
    public void Session_LeavesStandbyOut_WhenItIsOff_AndChoosesOnlyUsableVoices()
    {
        var settings = KliveAgent.KliveAgentVoiceSessionTests.Settings() with { IdleSeconds = 0 };
        var session = LiveInstructions.Session(settings, "marin", new JArray());
        Assert.Null(session["idle_timeout_s"]);
        Assert.Null(session["input"]);
        Assert.False((bool)session["store"]!);

        Assert.Equal("cedar", LiveInstructions.ChooseVoice("Cedar", "marin"));
        Assert.Equal("voice_123", LiveInstructions.ChooseVoice("voice_123", "marin"));
        Assert.Equal("marin", LiveInstructions.ChooseVoice("not a voice!", "marin"));
        Assert.Equal("marin", LiveInstructions.ChooseVoice(null, "marin"));
    }

    [Fact]
    public void Transcript_JoinsDeltasAsSent_AndLeavesLaterWordsForTheNextRequest()
    {
        var t = new LiveTranscript();
        t.AddUser("Check", 0, 200);
        t.AddUser(" the trad", 200, 500);
        t.AddUser("er.", 500, 700);
        t.AddVoice("On it.", 900, 1300);
        t.AddUser("And the logs.", 2600, 3200); // after the delegation below

        var task = t.TakeTask(1000)!;
        Assert.Equal("Check the trader.", task.Request);
        Assert.Null(task.Context); // the voice spoke after he finished: not context
        Assert.Equal("And the logs.", t.PendingUserText());
        Assert.Equal("And the logs.", t.TakeTask(3400)!.Request);
        Assert.Null(t.TakeTask(5000));
    }

    [Fact]
    public void Transcript_WithMisalignedTimestamps_StillHandsOverHisWords()
    {
        var t = new LiveTranscript();
        t.AddUser("Restart the trader.", 9000, 9800);
        Assert.Equal("Restart the trader.", t.TakeTask(500)!.Request);
    }

    [Fact]
    public void Transcript_APauseMidThought_IsOneRequest()
    {
        var t = new LiveTranscript();
        t.AddUser("Check the trader", 0, 900);
        t.AddUser(" and the logs.", 2000, 2600);
        var task = t.TakeTask(2800)!;
        Assert.Equal("Check the trader and the logs.", task.Request);
        Assert.Null(task.Context);
    }
}
