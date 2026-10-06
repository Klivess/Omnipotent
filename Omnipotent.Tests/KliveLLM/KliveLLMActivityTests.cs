using System.Net;
using System.Text;
using Omnipotent.Services.KliveLLM;
using LlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.KliveLLM
{
    /// <summary>
    /// Liveness reporting from inside a model call. A KliveAgent run died at "step 1" with no output
    /// because its stall watchdog saw nothing for five minutes while the call was healthy but silent —
    /// queued behind three shared AIRouter slots, prefilling, or reasoning. These pin the contract
    /// that fixes it: an observing caller hears about every one of those states, and a stream that
    /// genuinely stops producing bytes is bounded instead of being kept alive by the heartbeat.
    /// </summary>
    public class KliveLLMActivityTests
    {
        [Fact]
        public async Task ObserveActivity_FlowsIntoAwaitedCalls_AndEndsWithTheScope()
        {
            var seen = new List<KliveLLMActivity>();
            using (LlmService.ObserveActivity(a => { lock (seen) seen.Add(a); }))
            {
                await Task.Yield();
                await Task.Run(() => LlmService.ReportActivity(KliveLLMActivityKind.Queued, "inside"));
            }
            LlmService.ReportActivity(KliveLLMActivityKind.Queued, "outside");

            Assert.Single(seen);
            Assert.Equal("inside", seen[0].Detail);
            Assert.Null(LlmService.CurrentActivityScope);
        }

        [Fact]
        public async Task Stream_ReasoningDeltas_AreReported_AndAnObjectReasoningFieldNeverDropsContent()
        {
            string body =
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning\":\"Thinking about the signup flow\"}}]}\n\n" +
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"still thinking\"}}]}\n\n" +
                // A provider that sends a structured reasoning value must not cost us the content riding with it.
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning\":{\"type\":\"summary\"},\"content\":\"Hello\"}}]}\n\n" +
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\" there\"},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n";
            var handler = new ScriptedHandler(_ => Ok(new MemoryStream(Encoding.UTF8.GetBytes(body))));
            using var http = new HttpClient(handler);
            var service = new LlmService(http);
            var seen = new List<KliveLLMActivity>();
            var tokens = new StringBuilder();

            HFWrapper.HFLLMInferenceResponse response;
            using (LlmService.ObserveActivity(a => { lock (seen) seen.Add(a); }))
                response = await service.SendInferencePayloadAsync(OpenRouter(), Payload(), CancellationToken.None, t => tokens.Append(t));

            Assert.Equal("Hello there", response.choices[0].message.content);
            Assert.Equal("Hello there", tokens.ToString());
            lock (seen)
            {
                Assert.Contains(seen, a => a.Kind == KliveLLMActivityKind.AwaitingProvider);
                Assert.Contains(seen, a => a.Kind == KliveLLMActivityKind.Reasoning && a.Detail.Contains("chars"));
                Assert.Contains(seen, a => a.Kind == KliveLLMActivityKind.Streaming);
            }
        }

        [Fact]
        public async Task Stream_ThatGoesSilent_IsAbandoned_WhenTheCallerBoundsIt_WithoutABufferedRetry()
        {
            var handler = new ScriptedHandler(_ => Ok(new StallingStream(
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning\":\"starting\"}}]}\n\n")));
            using var http = new HttpClient(handler);
            var service = new LlmService(http);

            KliveLLMStreamStalledException stalled;
            using (LlmService.ObserveActivity(_ => { }, streamIdleTimeout: TimeSpan.FromMilliseconds(300)))
            {
                stalled = await Assert.ThrowsAsync<KliveLLMStreamStalledException>(() =>
                    service.SendInferencePayloadAsync(OpenRouter(), Payload(), CancellationToken.None, _ => { })
                        .WaitAsync(TimeSpan.FromSeconds(20)));
            }

            Assert.False(stalled.ProducedOutput);
            // The buffered fallback exists for streams that fail to START; a silent provider would
            // otherwise hold a second, unstreamed request for the whole HttpClient timeout.
            Assert.Equal(1, handler.Requests);
        }

        [Fact]
        public async Task Stream_StalledAfterVisibleOutput_StillThrows_RatherThanReturningAHalfStreamedTurn()
        {
            var handler = new ScriptedHandler(_ => Ok(new StallingStream(
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"computer_click\",\"arguments\":\"{\\\"x\\\":1\"}}]}}]}\n\n")));
            using var http = new HttpClient(handler);
            var service = new LlmService(http);

            using (LlmService.ObserveActivity(_ => { }, streamIdleTimeout: TimeSpan.FromMilliseconds(300)))
            {
                var stalled = await Assert.ThrowsAsync<KliveLLMStreamStalledException>(() =>
                    service.SendInferencePayloadAsync(OpenRouter(), Payload(), CancellationToken.None, _ => { })
                        .WaitAsync(TimeSpan.FromSeconds(20)));
                Assert.True(stalled.ProducedOutput);
            }
        }

        [Fact]
        public async Task AIRouterRequest_ParkedBehindABusySlot_ReportsThatItIsQueued()
        {
            const string body = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}],"
                + "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":1,\"total_tokens\":11}}";
            var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
            using var http = new HttpClient(handler);
            var service = new LlmService(http) { FairUse = new AIRouterFairUseLimiter(maxParallelRequests: 1) };
            var busy = await service.FairUse.AcquireAsync(10);
            var seen = new List<KliveLLMActivity>();
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Task<HFWrapper.HFLLMInferenceResponse> call;
            using (LlmService.ObserveActivity(a =>
            {
                lock (seen) seen.Add(a);
                if (a.Kind == KliveLLMActivityKind.Queued) queued.TrySetResult();
            }))
            {
                call = service.SendInferencePayloadAsync(AIRouter(), Payload(), CancellationToken.None, null);
            }

            await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(call.IsCompleted);
            lock (seen) Assert.Contains(seen, a => a.Kind == KliveLLMActivityKind.Queued && a.Detail.Contains("AIRouter slot"));

            busy.Dispose();
            var response = await call.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("ok", response.choices[0].message.content);
        }

        [Fact]
        public void StripToolSessionImages_KeepsTheTextAndTheToolProtocol()
        {
            var service = new LlmService(new HttpClient(new ScriptedHandler(_ => throw new InvalidOperationException("no calls"))));
            const string session = "kliveagent-strip-test";
            service.StartToolSession(session, "system");
            service.AppendUserMessageToToolSession(session, "do the task");
            service.AppendUserContentToToolSession(session, "Frames from the computer action",
                new List<(byte[] data, string mimeType)> { (new byte[] { 1, 2, 3 }, "image/jpeg") }, preservePromptPrefix: true);

            Assert.Equal(1, service.StripToolSessionImages(session));
            Assert.Equal(0, service.StripToolSessionImages(session));
            Assert.Equal(0, service.StripToolSessionImages("no-such-session"));
        }

        private static LlmService.RemoteLLMProviderConfiguration OpenRouter() => new(
            LlmService.LLMProvider.OpenRouter, "OpenRouter", "https://provider.test/v1/chat/completions", "test-token", "test/model");

        private static LlmService.RemoteLLMProviderConfiguration AIRouter() => new(
            LlmService.LLMProvider.AIRouter, "AIRouter",
            LlmService.ResolveChatCompletionsEndpoint("https://api.airouter.ch/v1"), "airouter-token", "Qwen3.8");

        private static HFWrapper.HFLLMInferenceRequest Payload() => new()
        {
            model = "test/model",
            stream = false,
            messages = new[] { new HFWrapper.HFMessage { role = "user", content = "hello" } },
        };

        private static HttpResponseMessage Ok(Stream body)
        {
            var content = new StreamContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
            private int requests;
            public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => this.respond = respond;
            public int Requests => Volatile.Read(ref requests);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult(respond(request));
            }
        }

        /// <summary>Serves a prefix, then blocks every read until it is cancelled — a provider that
        /// accepted the request and then stopped sending anything.</summary>
        private sealed class StallingStream : Stream
        {
            private readonly byte[] prefix;
            private int position;

            public StallingStream(string prefix) => this.prefix = Encoding.UTF8.GetBytes(prefix);

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (position < prefix.Length)
                {
                    int n = Math.Min(buffer.Length, prefix.Length - position);
                    prefix.AsMemory(position, n).CopyTo(buffer);
                    position += n;
                    return n;
                }
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async only");
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
