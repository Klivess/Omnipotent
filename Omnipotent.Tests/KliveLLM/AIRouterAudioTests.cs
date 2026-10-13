using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using LlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.KliveLLM
{
    /// <summary>
    /// AIRouter's speech endpoints are undocumented beyond their paths, so the client speaks the OpenAI
    /// shape and learns what the router refuses. These pin the request shapes, the learning, the shared
    /// 429 back-off, and the reasoning-effort vocabulary each AIRouter model accepts.
    /// </summary>
    public class AIRouterAudioTests
    {
        private const string Base = "https://api.airouter.test/v1";

        [Fact]
        public async Task Transcription_SendsTheWhisperFields_AndReadsVerboseDetail()
        {
            var handler = new RecordingHandler(_ => Json(
                "{\"text\":\" Restart the trader. \",\"language\":\"en\",\"duration\":1.4," +
                "\"segments\":[{\"no_speech_prob\":0.02,\"avg_logprob\":-0.2}]}"));
            var client = Client(handler);

            var transcript = await client.TranscribeAsync(new byte[] { 1, 2, 3, 4 }, "speech.wav", "audio/wav",
                language: "en", prompt: "Klives, OmniTrader.");

            Assert.Equal("Restart the trader.", transcript.Text);
            Assert.True(transcript.Detailed);
            Assert.Equal(0.02, transcript.NoSpeechProbability, 3);
            var request = Assert.Single(handler.Requests);
            Assert.EndsWith("/audio/transcriptions", request.Uri);
            Assert.Equal("Bearer test-key", request.Authorization);
            Assert.Contains("name=model", request.Body);
            Assert.Contains("whisper-large-v3-turbo", request.Body);
            Assert.Contains("verbose_json", request.Body);
            Assert.Contains("name=language", request.Body);
            Assert.Contains("Klives, OmniTrader.", request.Body);
        }

        [Fact]
        public async Task Transcription_ThatRefusesOptionalFields_FallsBackOnce_AndRemembers()
        {
            var handler = new RecordingHandler(request => request.Body.Contains("verbose_json")
                ? new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("{\"error\":\"bad response_format\"}") }
                : Json("{\"text\":\"hello\"}"));
            var client = Client(handler);

            var first = await client.TranscribeAsync(new byte[] { 1 }, "a.wav", "audio/wav", "en", "Klives");
            Assert.Equal("hello", first.Text);
            Assert.False(first.Detailed);
            Assert.Equal(2, handler.Requests.Count);
            Assert.DoesNotContain("name=prompt", handler.Requests[1].Body);

            // Learned: the next call goes straight to the shape the router accepts.
            await client.TranscribeAsync(new byte[] { 1 }, "a.wav", "audio/wav", "en", "Klives");
            Assert.Equal(3, handler.Requests.Count);
            Assert.DoesNotContain("verbose_json", handler.Requests[2].Body);
        }

        [Fact]
        public async Task Speech_PostsKokoroJson_AndNeverRewritesTheModel()
        {
            var handler = new RecordingHandler(_ => Audio(new byte[] { 82, 73, 70, 70 }));
            var client = Client(handler);

            var (audio, format) = await client.SynthesizeAsync("Checking the trader.", "bm_george", 1.1, "wav");

            Assert.Equal(new byte[] { 82, 73, 70, 70 }, audio);
            Assert.Equal("wav", format);
            var body = JObject.Parse(Assert.Single(handler.Requests).Body);
            Assert.Equal("kokoro", (string?)body["model"]);
            Assert.Equal("Checking the trader.", (string?)body["input"]);
            Assert.Equal("bm_george", (string?)body["voice"]);
            Assert.Equal("wav", (string?)body["response_format"]);
            Assert.Equal(1.1, (double?)body["speed"]);
        }

        [Fact]
        public async Task Speech_AtNormalSpeed_LeavesSpeedOut()
        {
            var handler = new RecordingHandler(_ => Audio(new byte[] { 1 }));
            await Client(handler).SynthesizeAsync("Hi.", "af_heart", 1.0, "mp3");
            Assert.Null(JObject.Parse(handler.Requests[0].Body)["speed"]);
        }

        [Fact]
        public async Task Speech_WhoseFormatIsRefused_FallsBackToMp3_AndRemembers()
        {
            var handler = new RecordingHandler(request => request.Body.Contains("\"wav\"")
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("unsupported format") }
                : Audio(new byte[] { 7 }));
            var client = Client(handler);

            var (_, format) = await client.SynthesizeAsync("One.", "af_heart", 1.0, "wav");
            Assert.Equal("mp3", format);
            Assert.Equal(2, handler.Requests.Count);

            var (_, again) = await client.SynthesizeAsync("Two.", "af_heart", 1.0, "wav");
            Assert.Equal("mp3", again);
            Assert.Equal(3, handler.Requests.Count); // no second wasted wav attempt
        }

        [Fact]
        public async Task RateLimit_HoldsTheWholeSharedQueue_AndSurfacesAsRateLimited()
        {
            var limiter = new AIRouterFairUseLimiter();
            var handler = new RecordingHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") };
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return response;
            });
            var client = Client(handler, limiter);

            var failure = await Assert.ThrowsAsync<AIRouterAudioException>(() =>
                client.SynthesizeAsync("Hi.", "af_heart", 1.0, "mp3"));
            Assert.True(failure.RateLimited);
            Assert.True(limiter.Describe().PenaltyRemaining > TimeSpan.FromSeconds(20));
        }

        [Fact]
        public async Task AudioCalls_HoldALiveSlotOnlyForTheExchange()
        {
            var limiter = new AIRouterFairUseLimiter();
            int inFlightDuringRequest = -1;
            var handler = new RecordingHandler(_ =>
            {
                inFlightDuringRequest = limiter.Describe().InFlight;
                return Audio(new byte[] { 1 });
            });
            await Client(handler, limiter).SynthesizeAsync("Hi.", "af_heart", 1.0, "mp3");
            Assert.Equal(1, inFlightDuringRequest);
            Assert.Equal(0, limiter.Describe().InFlight);
        }

        [Fact]
        public async Task AudioCalls_SkipTheQueue_WhenConfiguredNotToCount()
        {
            var limiter = new AIRouterFairUseLimiter();
            int inFlightDuringRequest = -1;
            var handler = new RecordingHandler(_ =>
            {
                inFlightDuringRequest = limiter.Describe().InFlight;
                return Audio(new byte[] { 1 });
            });
            var client = new AIRouterAudioClient(Base, _ => Task.FromResult<string?>("test-key"), limiter,
                gateAudio: () => Task.FromResult(false), httpClient: new HttpClient(handler));
            await client.SynthesizeAsync("Hi.", "af_heart", 1.0, "mp3");
            Assert.Equal(0, inFlightDuringRequest);
        }

        [Fact]
        public async Task MissingKey_FailsWithoutSendingAnything()
        {
            var handler = new RecordingHandler(_ => Audio(new byte[] { 1 }));
            var client = new AIRouterAudioClient(Base, _ => Task.FromResult<string?>(null), httpClient: new HttpClient(handler));
            await Assert.ThrowsAsync<AIRouterAudioException>(() => client.SynthesizeAsync("Hi.", "af_heart", 1.0, "mp3"));
            Assert.Empty(handler.Requests);
        }

        [Theory]
        [InlineData("{\"voices\":[{\"id\":\"af_bella\",\"name\":\"Bella\"},{\"id\":\"bm_george\",\"name\":\"George\"}]}")]
        [InlineData("{\"voices\":[\"af_bella\",\"bm_george\"]}")]
        [InlineData("[{\"voice_id\":\"af_bella\"},{\"name\":\"bm_george\"}]")]
        public void VoiceList_AcceptsEveryShapeKokoroServersUse(string body)
        {
            var voices = AIRouterAudioClient.ParseVoices(body);
            Assert.Equal(new[] { "af_bella", "bm_george" }, voices.Select(v => v.Id));
        }

        [Theory]
        [InlineData("Qwen3.8", "off", "none")]
        [InlineData("Qwen3.8", "low", "low")]
        [InlineData("Qwen3.8", "medium", "medium")]
        [InlineData("Qwen3.8", "high", "xhigh")]
        [InlineData("DeepSeek-V4-Flash", "low", "high")]
        [InlineData("DeepSeek-V4-Flash", "high", "xhigh")]
        [InlineData("mystery-model", "low", null)]
        public void ReasoningEffort_UsesEachModelsOwnVocabulary(string model, string level, string? expected)
        {
            Assert.Equal(expected, LlmService.AIRouterReasoningEffort(model, level));
        }

        [Fact]
        public void ReasoningEffort_IsAbsentFromTheWireUnlessSet()
        {
            var payload = new HFWrapper.HFLLMInferenceRequest { model = "Qwen3.8", stream = false };
            Assert.DoesNotContain("reasoning_effort", Newtonsoft.Json.JsonConvert.SerializeObject(payload));
            payload.reasoning_effort = "low";
            Assert.Contains("\"reasoning_effort\":\"low\"", Newtonsoft.Json.JsonConvert.SerializeObject(payload));
        }

        private static AIRouterAudioClient Client(RecordingHandler handler, AIRouterFairUseLimiter? limiter = null) =>
            new(Base, _ => Task.FromResult<string?>("test-key"), limiter ?? new AIRouterFairUseLimiter(),
                gateAudio: () => Task.FromResult(true), httpClient: new HttpClient(handler));

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Audio(byte[] bytes)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        internal sealed record SeenRequest(string Uri, string? Authorization, string Body);

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly Func<SeenRequest, HttpResponseMessage> respond;
            internal List<SeenRequest> Requests { get; } = new();

            internal RecordingHandler(Func<SeenRequest, HttpResponseMessage> respond) => this.respond = respond;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                var seen = new SeenRequest(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body);
                lock (Requests) Requests.Add(seen);
                return respond(seen);
            }
        }
    }
}
