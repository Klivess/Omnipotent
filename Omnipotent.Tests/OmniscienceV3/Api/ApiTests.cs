using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Api
{
    /// <summary>Phase 1 API layer: strict query parsing, the live stream, ordered events, the engine read side,
    /// settings validation, the single-instance lock, orphan recovery and the generated TypeScript contract.</summary>
    public class ApiTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_api_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2024, 11, 5, 12, 0, 0, DateTimeKind.Utc)));
        private readonly ConcurrentQueue<string> errors = new();
        private V3Runtime rt = null!;
        private V3Host host = null!;
        private EngineQueries queries = null!;

        public async Task InitializeAsync()
        {
            rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(root),
                Clock = clock,
                LlmClient = new NullLlm(),
                Log = _ => { },
                LogError = (m, e) => errors.Enqueue(m + " " + e?.Message),
                Kernel = new KernelOptions { StartWorkers = false, StartLoop = false },
                Modules = new IIntelModule[] { new SettingsModule() },
                LockWait = TimeSpan.FromSeconds(2),
            });
            host = new V3Host();
            host.Begin(clock.NowMs);
            Assert.True(host.SetReady(rt, clock.NowMs));
            queries = new EngineQueries(host, clock, () => new[] { "GET /omniscience/v3/status" });
        }

        public async Task DisposeAsync()
        {
            await host.StopAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }

        private sealed class NullLlm : ILlmClient
        {
            public Task<LlmResult> QueryAsync(LlmRequest request, CancellationToken ct) => throw new InvalidOperationException("No LLM in this test.");
            public Task<ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) =>
                Task.FromResult(new ModelContextInfo("AIRouter", "test", 260_000, 16_384, "operator-stated", null, 0, null));
            public void EndSession(string sessionKey) { }
        }

        private sealed class SettingsModule : IntelModuleBase
        {
            public override ModuleManifest Manifest { get; } = new()
            {
                Id = "test",
                Version = 1,
                Title = "Test module",
                Settings = new[] { new SettingDescriptor("test.threshold", SettingTypes.Int, "5", "A bounded integer.", 1, 10) },
            };

            public override Task<string?> ExecuteAsync(JobContext job) => Task.FromResult<string?>(null);
        }

        // ── query parsing ────────────────────────────────────────────────────────

        private static ApiQuery Q(params (string, string)[] pairs)
        {
            var c = new NameValueCollection();
            foreach (var (k, v) in pairs) c[k] = v;
            return new ApiQuery(c);
        }

        [Fact]
        public void Query_ParsesStrictlyAndNamesTheBadParameter()
        {
            Assert.Equal(25, Q(("limit", "25")).Int("limit", 100, 1, 1000));
            Assert.Equal(100, Q().Int("limit", 100, 1, 1000));
            var ex = Assert.Throws<ApiException>(() => Q(("limit", "lots")).Int("limit", 100, 1, 1000));
            Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
            Assert.Contains("'limit'", ex.Message);
            Assert.Throws<ApiException>(() => Q(("limit", "5000")).Int("limit", 100, 1, 1000));
            Assert.Equal(Lane.LlmBackground, Q(("lane", "llmbackground")).Enum<Lane>("lane"));
            Assert.Throws<ApiException>(() => Q(("lane", "gpu")).Enum<Lane>("lane"));
            Assert.Throws<ApiException>(() => Q(("lane", "7")).Enum<Lane>("lane"));
            Assert.Equal(new[] { "job.", "alert" }, Q(("topics", " job., alert ,")).List("topics"));
            Assert.Null(Q(("module", "   ")).Str("module"));
            Assert.Equal(true, Q(("on", "yes")).Bool("on"));
            Assert.Throws<ApiException>(() => Q(("on", "maybe")).Bool("on"));
        }

        [Fact]
        public void Query_InstantsNeedAnOffsetAndWindowsMustBeOrdered()
        {
            long ms = Instant.FromUtc(new DateTime(2024, 1, 14, 23, 41, 0, DateTimeKind.Utc));
            Assert.Equal(ms, Q(("from", ms.ToString())).Instant("from"));
            Assert.Equal(ms, Q(("from", "2024-01-14T23:41:00Z")).Instant("from"));
            Assert.Equal(ms, Q(("from", "2024-01-15T00:41:00+01:00")).Instant("from"));
            // A local time without an offset would be guessed in the server's zone: rejected (T1).
            Assert.Throws<ApiException>(() => Q(("from", "2024-01-14T23:41:00")).Instant("from"));
            Assert.Throws<ApiException>(() => Q(("from", "2024-02-01T00:00:00Z"), ("to", "2024-01-01T00:00:00Z")).Window());
            Assert.Throws<ApiException>(() => Q(("from", "2024-01-01T00:00:00Z"), ("to", "2024-01-01T00:00:00Z")).Window());
            var (f, t) = Q(("to", "2024-01-01T00:00:00Z")).Window();
            Assert.Null(f);
            Assert.NotNull(t);
        }

        // ── live stream: resume planning ─────────────────────────────────────────

        [Theory]
        [InlineData(0, 10, 50, 50, 0, null)]              // fresh client: replay what is retained
        [InlineData(50, 10, 50, 50, 50, null)]            // up to date
        [InlineData(30, 10, 50, 50, 30, null)]            // inside the retained range
        [InlineData(9, 10, 50, 50, 9, null)]              // exactly contiguous with the oldest retained event
        [InlineData(5, 10, 50, 50, 50, "events_pruned")]  // events 6..9 were pruned
        [InlineData(80, 10, 50, 50, 50, "cursor_ahead")]  // cursor from another database
        [InlineData(40, 0, 0, 50, 50, "events_pruned")]   // the whole log was pruned after the client saw 40 of 50
        [InlineData(50, 0, 0, 50, 50, null)]              // …but a client that saw everything missed nothing
        public void PlanResume_DetectsGaps(long since, long oldest, long newest, long ever, long expectedCursor, string? expectedReset)
        {
            var (cursor, reset) = LiveStreamHub.PlanResume(since, new StreamBounds(oldest, newest, ever));
            Assert.Equal(expectedCursor, cursor);
            Assert.Equal(expectedReset, reset);
        }

        // ── events: ordering, bounds, polling ────────────────────────────────────

        [Fact]
        public async Task Events_ArePublishedInIdOrderUnderConcurrency()
        {
            var seen = new ConcurrentQueue<long>();
            using var sub = rt.Host.Events.Subscribe(ev => { seen.Enqueue(ev.EventId); return ValueTask.CompletedTask; });
            await Task.WhenAll(Enumerable.Range(0, 120).Select(i => Task.Run(() => rt.Host.Events.PublishAsync("job.progress", new { i }))));
            var ids = seen.ToArray();
            Assert.Equal(120, ids.Length);
            for (int i = 1; i < ids.Length; i++) Assert.Equal(ids[i - 1] + 1, ids[i]);
        }

        [Fact]
        public async Task Events_StringPayloadIsAJsonStringAndBoundsSurvivePruning()
        {
            var ev = await rt.Host.Events.PublishAsync("alert", "not json {");
            Assert.Equal("\"not json {\"", ev.PayloadJson);
            await rt.Host.Events.PublishAsync("alert", new { n = 2 });
            clock.Advance(TimeSpan.FromDays(8));
            await rt.Host.Events.PruneAsync(TimeSpan.FromDays(7));
            var b = rt.Host.Events.Bounds();
            Assert.True(b.IsEmpty);
            Assert.Equal(ev.EventId + 1, b.LatestEver);
        }

        [Fact]
        public async Task EventsPage_FiltersByTopicAndPermissionAndPagesWithoutLoss()
        {
            for (int i = 0; i < 6; i++)
            {
                await rt.Host.Events.PublishAsync("job.progress", new { i });
                await rt.Host.Events.PublishAsync("live.message", new { i, text = "secret" });
            }
            Func<string, bool> noMessages = t => !t.StartsWith("live.");
            var p1 = queries.Events(0, Array.Empty<string>(), noMessages, 4);
            Assert.Equal(4, p1.Items.Count);
            Assert.True(p1.More);
            Assert.All(p1.Items, e => Assert.Equal("job.progress", e.Topic));
            var p2 = queries.Events(p1.Cursor, Array.Empty<string>(), noMessages, 4);
            Assert.Equal(2, p2.Items.Count);
            Assert.False(p2.More);
            var all = p1.Items.Concat(p2.Items).Select(e => e.Payload.GetProperty("i").GetInt32()).ToList();
            Assert.Equal(Enumerable.Range(0, 6), all);
            Assert.Null(p1.Reset);

            var onlyLive = queries.Events(0, new[] { "live." }, _ => true, 100);
            Assert.Equal(6, onlyLive.Items.Count);
        }

        [Fact]
        public async Task EventsPage_ReportsAResetWhenTheCursorFellOffTheLog()
        {
            for (int i = 0; i < 5; i++) await rt.Host.Events.PublishAsync("job.progress", new { i });
            clock.Advance(TimeSpan.FromDays(8));
            var kept = await rt.Host.Events.PublishAsync("job.progress", new { i = 99 });
            await rt.Host.Events.PruneAsync(TimeSpan.FromDays(7));
            var page = queries.Events(2, Array.Empty<string>(), _ => true, 100);
            Assert.Equal("events_pruned", page.Reset);
            Assert.Empty(page.Items); // continue from the high-water mark after refetching state
            Assert.Equal(kept.EventId, page.Cursor);
        }

        // ── live stream: the socket ──────────────────────────────────────────────

        /// <summary>An in-memory WebSocket: records frames, lets the test close from the client side, and answers
        /// the server's close frame like a well-behaved browser.</summary>
        private sealed class FakeSocket : WebSocket
        {
            public readonly ConcurrentQueue<string> Sent = new();
            private readonly TaskCompletionSource clientClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private WebSocketState state = WebSocketState.Open;
            public TaskCompletionSource? BlockSends;
            public WebSocketCloseStatus? ServerCloseStatus;

            public override WebSocketCloseStatus? CloseStatus => ServerCloseStatus;
            public override string? CloseStatusDescription => null;
            public override WebSocketState State => state;
            public override string? SubProtocol => null;
            public override void Abort() => state = WebSocketState.Aborted;
            public override void Dispose() { }

            public void ClientCloses() => clientClose.TrySetResult();

            public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            {
                ServerCloseStatus = closeStatus;
                state = WebSocketState.Closed;
                return Task.CompletedTask;
            }

            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            {
                ServerCloseStatus = closeStatus;
                state = state == WebSocketState.CloseReceived ? WebSocketState.Closed : WebSocketState.CloseSent;
                clientClose.TrySetResult(); // the browser answers our close frame
                return Task.CompletedTask;
            }

            public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            {
                await clientClose.Task.WaitAsync(cancellationToken);
                state = state == WebSocketState.CloseSent ? WebSocketState.Closed : WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "bye");
            }

            public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            {
                if (BlockSends != null) await BlockSends.Task.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Sent.Enqueue(Encoding.UTF8.GetString(buffer));
            }

            public IReadOnlyList<(long Id, string Topic)> Frames() => Sent
                .Select(f => JsonDocument.Parse(f).RootElement)
                .Select(e => (e.GetProperty("id").GetInt64(), e.GetProperty("topic").GetString()!))
                .ToList();
        }

        private static async Task Until(Func<bool> condition, int timeoutMs = 10_000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met in time.");
                await Task.Delay(10);
            }
        }

        [Fact]
        public async Task Stream_ReplaysThenGoesLiveWithoutLossOrDuplicatesAndHonoursFilters()
        {
            var events = rt.Host.Events;
            for (int i = 0; i < 4; i++)
            {
                await events.PublishAsync("job.progress", new { i });
                await events.PublishAsync("live.message", new { i });
            }
            var hub = new LiveStreamHub(events, clock, (m, e) => errors.Enqueue(m), TimeSpan.FromMinutes(5));
            var socket = new FakeSocket();
            var query = new NameValueCollection { ["since"] = "2" };
            var handler = hub.HandleAsync(socket, query, t => !t.StartsWith("live."), CancellationToken.None);

            // Live events published from several threads while the replay may still be running.
            await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() => events.PublishAsync(i % 2 == 0 ? "job.progress" : "live.message", new { live = i }))));

            // Expected: every job.progress with id > 2 (3 replayed + 15 live).
            await Until(() => socket.Sent.Count >= 18);
            socket.ClientCloses();
            await handler.WaitAsync(TimeSpan.FromSeconds(10));

            var frames = socket.Frames();
            Assert.Equal(18, frames.Count);
            Assert.All(frames, f => Assert.Equal("job.progress", f.Topic));
            Assert.All(frames, f => Assert.True(f.Id > 2));
            for (int i = 1; i < frames.Count; i++) Assert.True(frames[i].Id > frames[i - 1].Id, "ids must strictly increase");
            Assert.Equal(0, hub.Connections);
            Assert.Equal(0, events.SubscriberCount);
            Assert.Empty(errors);
        }

        [Fact]
        public async Task Stream_ClosesASlowConsumerWith1008()
        {
            var events = rt.Host.Events;
            var hub = new LiveStreamHub(events, clock, (m, e) => errors.Enqueue(m), TimeSpan.FromMinutes(5), queueCapacity: 16);
            var socket = new FakeSocket { BlockSends = new TaskCompletionSource() };
            var handler = hub.HandleAsync(socket, new NameValueCollection(), _ => true, CancellationToken.None);
            await Until(() => events.SubscriberCount == 1);
            for (int i = 0; i < 40; i++) await events.PublishAsync("job.progress", new { i });
            await handler.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.ServerCloseStatus);
            Assert.Equal(0, events.SubscriberCount);
        }

        [Fact]
        public async Task Stream_SendsAResetFrameForACursorFromAnotherDatabase()
        {
            var events = rt.Host.Events;
            await events.PublishAsync("job.progress", new { i = 1 });
            var hub = new LiveStreamHub(events, clock, (m, e) => errors.Enqueue(m), TimeSpan.FromMinutes(5));
            var socket = new FakeSocket();
            var handler = hub.HandleAsync(socket, new NameValueCollection { ["since"] = "999" }, _ => true, CancellationToken.None);
            await Until(() => socket.Sent.Count >= 1);
            socket.ClientCloses();
            await handler.WaitAsync(TimeSpan.FromSeconds(10));
            var first = socket.Frames()[0];
            Assert.Equal(LiveStreamHub.ResetTopic, first.Topic);
            Assert.Equal(1, first.Id);
        }

        [Fact]
        public async Task Stream_EndsCleanlyWhenTheServiceStops()
        {
            var hub = new LiveStreamHub(rt.Host.Events, clock, (m, e) => errors.Enqueue(m), TimeSpan.FromMinutes(5));
            var socket = new FakeSocket();
            using var stopping = new CancellationTokenSource();
            var handler = hub.HandleAsync(socket, new NameValueCollection(), _ => true, stopping.Token);
            await Until(() => rt.Host.Events.SubscriberCount == 1);
            stopping.Cancel();
            await handler.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, socket.ServerCloseStatus);
        }

        // ── engine queries ───────────────────────────────────────────────────────

        [Fact]
        public void Status_ReportsTheRunningEngine()
        {
            var s = queries.Status();
            Assert.Equal(V3Phase.Ready, s.Phase);
            Assert.Equal(4, s.Stores.Count);
            Assert.Equal(Enum.GetValues<Lane>().Length, s.Lanes.Count);
            Assert.Contains(s.Modules, m => m.ModuleId == "test");
            Assert.Equal("Europe/London", s.KlivesZone);
            Assert.NotNull(s.Llm);
            Assert.NotNull(s.Stream);
        }

        [Fact]
        public void Host_AnswersAClassified503InEveryNonReadyPhase()
        {
            var h = new V3Host();
            Assert.Equal(ApiErrors.Starting, Assert.Throws<ApiException>(() => h.Require()).Code);
            h.SetDisabled("off");
            Assert.Equal(ApiErrors.Disabled, Assert.Throws<ApiException>(() => h.Require()).Code);
            h.Begin(clock.NowMs);
            h.SetFailed("boom");
            var failed = Assert.Throws<ApiException>(() => h.Require());
            Assert.Equal(ApiErrors.Failed, failed.Code);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.Status);
            Assert.Contains("boom", failed.Message);
            var status = new EngineQueries(h, clock, () => Array.Empty<string>()).Status();
            Assert.Equal(V3Phase.Failed, status.Phase);
            Assert.Equal("boom", status.Failure);
        }

        [Fact]
        public async Task Host_ReadyAfterStopIsRefusedSoTheCallerDisposes()
        {
            var h = new V3Host();
            h.Begin(clock.NowMs);
            await h.StopAsync();
            Assert.False(h.SetReady(rt, clock.NowMs));
            Assert.Null(h.Runtime);
        }

        [Fact]
        public async Task Jobs_FilterAndKeysetPageWithoutOverlap()
        {
            for (int i = 0; i < 7; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await rt.Host.Jobs.EnqueueAsync(new JobRequest { ModuleId = "test", Kind = i % 2 == 0 ? "even" : "odd", SubjectKind = "person", SubjectId = "p" + i });
            }
            var even = queries.Jobs(new JobQuery { Kind = "even" });
            Assert.Equal(4, even.Items.Count);
            Assert.Null(even.NextCursor);

            var seen = new List<long>();
            long? cursor = null;
            do
            {
                var page = queries.Jobs(new JobQuery { ModuleId = "test", BeforeJobId = cursor, Limit = 3 });
                seen.AddRange(page.Items.Select(j => j.JobId));
                cursor = page.NextCursor;
            } while (cursor != null);
            Assert.Equal(7, seen.Count);
            Assert.Equal(seen.OrderByDescending(x => x), seen);
            Assert.Equal(seen.Distinct().Count(), seen.Count);

            long midMs = clock.NowMs - 3000;
            var recent = queries.Jobs(new JobQuery { CreatedFromMs = midMs });
            Assert.Equal(4, recent.Items.Count);
            Assert.Single(queries.Jobs(new JobQuery { SubjectId = "p3" }).Items);
        }

        [Fact]
        public async Task Jobs_RetryOnlyDeadJobsAndAuditIt()
        {
            long id = await rt.Host.Jobs.EnqueueAsync(new JobRequest { ModuleId = "test", Kind = "k", MaxAttempts = 1 });
            var leased = await rt.Host.Jobs.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1));
            Assert.Equal(id, leased!.JobId);
            await rt.Host.Jobs.FailAsync(id, "w", ErrorClass.Permanent, "bad input");

            var retried = await queries.RetryJobAsync(id, "tester", CancellationToken.None);
            Assert.True(retried.Changed);
            Assert.Equal(JobStates.Queued, retried.State);

            var again = await queries.RetryJobAsync(id, "tester", CancellationToken.None); // already queued: a no-op
            Assert.False(again.Changed);

            var leased2 = await rt.Host.Jobs.LeaseNextAsync(Lane.Cpu, "w", TimeSpan.FromMinutes(1));
            await rt.Host.Jobs.CompleteAsync(leased2!.JobId, "w", null);
            await Assert.ThrowsAsync<ApiException>(() => queries.RetryJobAsync(id, "tester", CancellationToken.None));
            await Assert.ThrowsAsync<ApiException>(() => queries.RetryJobAsync(424242, "tester", CancellationToken.None));

            using var c = rt.Stores.Operator.OpenRead();
            Assert.Equal(1, c.Scalar<long>(null, "SELECT COUNT(*) FROM audit_log WHERE action='job.retry' AND actor='tester'"));
        }

        [Fact]
        public async Task Settings_AreValidatedAgainstTheirDescriptorsAndAudited()
        {
            var list = queries.Settings();
            Assert.Contains(list, s => s.Key == KernelSettings.KlivesTimeZoneKey && s.Scope == "kernel" && s.ValueJson == null);
            Assert.Contains(list, s => s.Key == "test.threshold" && s.Scope == "test");

            JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
            Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<ApiException>(() => queries.SetSettingAsync(new SetSettingRequest("test.typo", J("1")), "t", default))).Status);
            Assert.Equal(HttpStatusCode.BadRequest, (await Assert.ThrowsAsync<ApiException>(() => queries.SetSettingAsync(new SetSettingRequest("test.threshold", J("50")), "t", default))).Status);
            Assert.Equal(HttpStatusCode.BadRequest, (await Assert.ThrowsAsync<ApiException>(() => queries.SetSettingAsync(new SetSettingRequest("test.threshold", J("2.5")), "t", default))).Status);
            Assert.Equal(HttpStatusCode.BadRequest, (await Assert.ThrowsAsync<ApiException>(() => queries.SetSettingAsync(new SetSettingRequest("test.threshold", default), "t", default))).Status);
            Assert.Equal(HttpStatusCode.BadRequest, (await Assert.ThrowsAsync<ApiException>(() => queries.SetSettingAsync(new SetSettingRequest(KernelSettings.KlivesTimeZoneKey, J("\"Mars/Olympus\"")), "t", default))).Status);

            var set = await queries.SetSettingAsync(new SetSettingRequest("test.threshold", J("7")), "tester", default);
            Assert.Equal("7", set.ValueJson);
            Assert.Equal(7, rt.Host.Settings.Get("test.threshold", 5));
            await queries.SetSettingAsync(new SetSettingRequest(KernelSettings.KlivesTimeZoneKey, J("\"America/New_York\"")), "tester", default);
            Assert.Equal("America/New_York", queries.Status().KlivesZone);

            using var c = rt.Stores.Operator.OpenRead();
            Assert.Equal(2, c.Scalar<long>(null, "SELECT COUNT(*) FROM audit_log WHERE action='setting.set' AND actor='tester'"));
        }

        [Fact]
        public void Modules_RejectUnnamespacedOrInvalidSettings()
        {
            var host2 = new ModuleHost(rt.Stores, clock, new KernelOptions { StartWorkers = false, StartLoop = false }, _ => { }, (_, _) => { });
            var bad = new TestManifestModule(new ModuleManifest
            {
                Id = "alpha", Version = 1, Title = "a",
                Settings = new[] { new SettingDescriptor("threshold", SettingTypes.Int, "5", "x") },
            });
            Assert.Throws<InvalidOperationException>(() => host2.Register(bad));
            var badDefault = new TestManifestModule(new ModuleManifest
            {
                Id = "beta", Version = 1, Title = "b",
                Settings = new[] { new SettingDescriptor("beta.n", SettingTypes.Int, "\"five\"", "x") },
            });
            Assert.Throws<InvalidOperationException>(() => host2.Register(badDefault));
        }

        private sealed class TestManifestModule : IntelModuleBase
        {
            public TestManifestModule(ModuleManifest manifest) => Manifest = manifest;
            public override ModuleManifest Manifest { get; }
            public override Task<string?> ExecuteAsync(JobContext job) => Task.FromResult<string?>(null);
        }

        [Fact]
        public async Task LlmSummary_MetersCacheEfficiencyAndLatencyPerClass()
        {
            long t0 = clock.NowMs;
            await rt.Stores.Intel.WriteAsync((c, tx) =>
            {
                void Row(long at, string wc, string outcome, long prompt, long cached, long latency) =>
                    c.Exec(tx, """
                        INSERT INTO llm_calls(at_ms, module_id, purpose, model, provider, work_class, prompt_tokens, cached_tokens, completion_tokens, latency_ms, outcome)
                        VALUES($a,'deduction','facts','Qwen3.8','AIRouter',$w,$p,$c,10,$l,$o)
                        """, ("$a", at), ("$w", wc), ("$p", prompt), ("$c", cached), ("$l", latency), ("$o", outcome));
                Row(t0 + 1, "Background", "ok", 1000, 800, 100);
                Row(t0 + 2, "Background", "ok", 1000, 800, 200);
                Row(t0 + 3, "Background", "ok", 1000, 800, 300);
                Row(t0 + 4, "Background", "Transient", 1000, 800, 400);
                Row(t0 + 5, "Interactive", "ok", 2000, 0, 1000);
                Row(t0 + 6, "Interactive", "ok", 2000, 1000, 3000);
            });
            var s = queries.LlmSummary(new EngineQueries.LlmSummaryFilter(t0, null, LlmGroupBy.WorkClass, null, null));
            var bg = s.Rows.Single(r => r.Key == "Background");
            Assert.Equal(4, bg.Calls);
            Assert.Equal(3, bg.Ok);
            Assert.Equal(1, bg.Failed);
            Assert.Equal(0.8, bg.CacheHitRate, 3);
            Assert.Equal(200, bg.P50LatencyMs);
            Assert.Equal(400, bg.P95LatencyMs);
            Assert.Equal(t0 + 1, bg.FirstAtMs);
            Assert.Equal(t0 + 4, bg.LastAtMs);
            var ia = s.Rows.Single(r => r.Key == "Interactive");
            Assert.Equal(0.25, ia.CacheHitRate, 3);

            var windowed = queries.LlmSummary(new EngineQueries.LlmSummaryFilter(t0 + 5, t0 + 6, LlmGroupBy.Outcome, null, null));
            Assert.Equal(1, windowed.Rows.Single().Calls); // [from, to): only the call at t0+5

            var page = queries.LlmCalls(new LlmCallQuery { WorkClass = "Background", Limit = 3 });
            Assert.Equal(3, page.Items.Count);
            Assert.NotNull(page.NextCursor);
            var rest = queries.LlmCalls(new LlmCallQuery { WorkClass = "Background", Limit = 3, BeforeCallId = page.NextCursor });
            Assert.Single(rest.Items);
            Assert.Null(rest.NextCursor);
        }

        [Theory]
        [InlineData(new long[] { }, 0.5, null)]
        [InlineData(new long[] { 7 }, 0.95, 7L)]
        [InlineData(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 0.5, 5L)]
        [InlineData(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 0.95, 10L)]
        public void Percentile_IsNearestRank(long[] values, double q, long? expected) =>
            Assert.Equal(expected, EngineQueries.Percentile(values, q));

        // ── single instance + orphan recovery ────────────────────────────────────

        [Fact]
        public async Task Stores_RefuseASecondOwnerOfTheSameDirectory()
        {
            var ex = await Assert.ThrowsAsync<InstanceLockedException>(() =>
                OmniscienceStores.OpenAsync(new StorePaths(root), clock, default, TimeSpan.FromMilliseconds(300)));
            Assert.Contains("Another process", ex.Message);
        }

        [Fact]
        public async Task Stores_ReopenAfterTheOwnerLetsGo()
        {
            string other = Path.Combine(Path.GetTempPath(), "omniv3_lock_" + Guid.NewGuid().ToString("N"));
            try
            {
                var first = await OmniscienceStores.OpenAsync(new StorePaths(other), clock, default, TimeSpan.FromMilliseconds(300));
                var waiting = OmniscienceStores.OpenAsync(new StorePaths(other), clock, default, TimeSpan.FromSeconds(10));
                await Task.Delay(300);
                Assert.False(waiting.IsCompleted);
                await first.DisposeAsync();
                var second = await waiting; // the overlapping restart proceeds once the old owner is gone
                await second.DisposeAsync();
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(other, true); }
                catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
            }
        }

        [Fact]
        public async Task Startup_RequeuesOrphanedLeasesAndDeadLettersExhaustedOnes()
        {
            long a = await rt.Host.Jobs.EnqueueAsync(new JobRequest { ModuleId = "test", Kind = "a", MaxAttempts = 3 });
            long b = await rt.Host.Jobs.EnqueueAsync(new JobRequest { ModuleId = "test", Kind = "b", MaxAttempts = 1 });
            await rt.Host.Jobs.LeaseNextAsync(Lane.Cpu, "old-process", TimeSpan.FromHours(1));
            await rt.Host.Jobs.LeaseNextAsync(Lane.Cpu, "old-process", TimeSpan.FromHours(1));
            int n = await rt.Host.Jobs.ReclaimOrphanedAsync();
            Assert.Equal(2, n);
            Assert.Equal(JobStates.Queued, rt.Host.Jobs.Get(a)!.State);
            Assert.Equal(JobStates.Dead, rt.Host.Jobs.Get(b)!.State);
        }

        // ── contract generation ──────────────────────────────────────────────────

        /// <summary>Found from this source file's location, so the test works from any build output folder.</summary>
        private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Omnipotent.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }

        [Fact]
        public void Contracts_TypeScriptFileIsCurrent()
        {
            string ts = TypeScriptContracts.Generate(TypeScriptContracts.Roots(typeof(TypeScriptContracts).Assembly));
            Assert.Contains("export interface V3StatusDto {", ts);
            Assert.Contains("export type Lane = \"Cpu\" | \"Io\" | \"LlmBackground\" | \"LlmInteractive\" | \"Coordinator\";", ts);
            Assert.Contains("  nextCursor?: number | null;", ts);
            string path = Path.Combine(RepoRoot(), "Docs", "omniscience-v3", "api-contracts.ts");
            string existing = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : "";
            if (existing != ts)
            {
                File.WriteAllText(path, ts);
                Assert.Fail("Docs/omniscience-v3/api-contracts.ts was stale and has been regenerated; review and commit it.");
            }
        }

        private sealed record BadContract(DateTime When);
        private sealed record BadDuration(TimeSpan Took);

        [Fact]
        public void Contracts_RejectWallClockTypes()
        {
            Assert.Throws<InvalidOperationException>(() => TypeScriptContracts.Generate(new[] { typeof(BadContract) }));
            Assert.Throws<InvalidOperationException>(() => TypeScriptContracts.Generate(new[] { typeof(BadDuration) }));
        }

        [Fact]
        public async Task Routes_RegisterTheWholeEngineSurface()
        {
            var api = new Omnipotent.Services.KliveAPI.KliveAPI();
            var registrar = new RouteRegistrar(api, () => Array.Empty<object>(), (m, e) => errors.Enqueue(m));
            await new V3Routes(host, registrar, queries).RegisterAsync(CancellationToken.None);
            var routes = registrar.Registered;
            foreach (var expected in new[]
            {
                "GET /omniscience/v3/status", "GET /omniscience/v3/modules", "POST /omniscience/v3/modules/enabled",
                "GET /omniscience/v3/jobs", "GET /omniscience/v3/jobs/get", "POST /omniscience/v3/jobs/cancel", "POST /omniscience/v3/jobs/retry",
                "GET /omniscience/v3/llm/calls", "GET /omniscience/v3/llm/summary", "GET /omniscience/v3/events",
                "GET /omniscience/v3/settings", "POST /omniscience/v3/settings/set", "WS /omniscience/v3/stream",
            })
                Assert.Contains(expected, routes);
            Assert.Empty(errors);
        }
    }
}
