using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Insights;
using Omnipotent.Services.OmniBlox.Marketing;
using Omnipotent.Services.OmniBlox.Pipeline;
using Omnipotent.Services.OmniBlox.Rbx;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Tests.OmniBlox
{
    public class ComplianceTests
    {
        private static StorePage Page(string title = "Lava Forest Obby", string? description = null) => new()
        {
            Title = title,
            Description = description ?? "Jump across glowing lava platforms in a pine forest and race to the golden finish!",
            Keywords = { "obby" },
        };

        private static ProductRecord Product(string name, int price = 50, string grant = "currency:100", ProductKind kind = ProductKind.DeveloperProduct, string description = "100 coins") =>
            new() { Key = "p", Name = name, PriceRobux = price, Grant = grant, Kind = kind, Description = description };

        [Fact]
        public void AnHonestPage_Passes_AndPreparesTheQuestionnaire()
        {
            var r = ComplianceChecker.Check(Page(), new[] { Product("100 Coins"), Product("Rollercoaster Pass", 99, "flag:coaster", ProductKind.GamePass, "Ride the coaster") }, null);
            Assert.True(r.Ok, r.ToString());
            Assert.Contains(r.QuestionnaireNotes, n => n.StartsWith("Violence: none"));
        }

        [Theory]
        [InlineData("Pokemon Obby", "franchise")]
        [InlineData("FREE ROBUX Obby", "scam bait")]
        [InlineData("Obby discord.gg/abc", "link")]
        public void ForbiddenTitles_Fail(string title, string expected)
        {
            var r = ComplianceChecker.Check(Page(title), new[] { Product("Coins") }, null);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.Contains(expected));
        }

        [Fact]
        public void WholeWordsOnly_SoMarionetteIsNotMario()
        {
            var r = ComplianceChecker.Check(Page("Marionette Theatre Obby"), new[] { Product("Coins") }, null);
            Assert.DoesNotContain(r.Errors, e => e.Contains("mario"));
        }

        [Fact]
        public void PaidRandomItems_AndSillyPrices_Fail()
        {
            var r = ComplianceChecker.Check(Page(), new[] { Product("Mystery Crate", 50, "currency:1") , Product("Coins", 20_000) }, null);
            Assert.Contains(r.Errors, e => e.Contains("paid random item"));
            Assert.Contains(r.Errors, e => e.Contains("between 1 and 10,000"));
        }

        [Fact]
        public void TooLongText_Fails()
        {
            var r = ComplianceChecker.Check(Page(new string('A', 3) + new string('b', 60), new string('x', 1200)), Array.Empty<ProductRecord>(), null);
            Assert.Contains(r.Errors, e => e.Contains("Roblox allows 50"));
            Assert.Contains(r.Errors, e => e.Contains("Roblox allows 1000"));
            Assert.Contains(r.Warnings, w => w.Contains("sells nothing"));
        }
    }

    public class RobloxClientTests
    {
        private sealed class Recorder : HttpMessageHandler
        {
            public HttpRequestMessage? Last;
            public string Body = "";
            public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Last = request;
                Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
                return Respond(request);
            }
        }

        private static (RobloxCloudClient Cloud, Recorder Rec) Cloud(string? key = "k")
        {
            var rec = new Recorder();
            var http = new RobloxHttp(new HttpClient(rec), () => DateTime.UtcNow, (_, _) => Task.CompletedTask);
            return (new RobloxCloudClient(http, () => key, (_, _) => Task.CompletedTask), rec);
        }

        [Fact]
        public void CreatorStoreAsset_ParsesTheRealResponseShape()
        {
            // Captured from apis.roblox.com/toolbox-service/v2/assets:search on 2026-10-19.
            var json = JObject.Parse(@"{""voting"":{""showVotes"":true,""upVotes"":97,""downVotes"":3,""voteCount"":100,""upVotePercent"":97},
""creator"":{""creator"":""user/143539349"",""userId"":143539349,""name"":""GabeQZ"",""verified"":true},
""creatorStoreProduct"":{""purchasePrice"":{""currencyCode"":""USD"",""quantity"":{""significand"":4990000000,""exponent"":-9}},""purchasable"":true},
""asset"":{""subTypes"":[],""hasScripts"":false,""scriptCount"":0,""objectMeshSummary"":{""triangles"":10048,""vertices"":10514},
""instanceCounts"":{""script"":0,""meshPart"":3,""animation"":0,""decal"":0,""audio"":0,""tool"":0},""id"":5392132809,""name"":""Realistic Pine Tree "",
""description"":""props to whoever did."",""assetTypeId"":10,""createTime"":""2020-07-20T23:33:26.837Z"",""updateTime"":""2021-07-24T09:10:05.19Z"",""categoryPath"":""3d__nature""}}");
            var a = RobloxPublicClient.ParseCreatorStoreAsset(json)!;
            Assert.Equal(5392132809, a.AssetId);
            Assert.Equal("Realistic Pine Tree", a.Name);
            Assert.Equal(10048, a.Triangles);
            Assert.Equal(3, a.MeshPartCount);
            Assert.Equal(97, a.UpVotePercent);
            Assert.True(a.CreatorVerified);
            Assert.Equal(4.99m, a.PriceUsd);
            Assert.False(a.IsFree);
            Assert.Equal(2020, a.CreatedUtc!.Value.Year);
        }

        [Fact]
        public async Task PublishPlace_SendsXmlWithTheVersionType()
        {
            var (cloud, rec) = Cloud();
            rec.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"versionNumber\": 7}", Encoding.UTF8, "application/json") };
            var result = await cloud.PublishPlaceAsync(1, 2, Encoding.UTF8.GetBytes("<roblox/>"), isXml: true, publish: false, CancellationToken.None);
            Assert.Equal(7, result.VersionNumber);
            Assert.Equal("https://apis.roblox.com/universes/v1/1/places/2/versions?versionType=Saved", rec.Last!.RequestUri!.ToString());
            Assert.Equal("application/xml", rec.Last.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("k", rec.Last.Headers.GetValues("x-api-key").Single());
        }

        [Fact]
        public async Task UpdateUniverse_SendsOnlyTheFieldsGiven()
        {
            var (cloud, rec) = Cloud();
            await cloud.UpdateUniverseAsync(9, new UniverseUpdate { Visibility = "PUBLIC", MobileEnabled = true }, CancellationToken.None);
            Assert.Equal("https://apis.roblox.com/cloud/v2/universes/9?updateMask=visibility,mobileEnabled", rec.Last!.RequestUri!.ToString());
            var body = JObject.Parse(rec.Body);
            Assert.Equal(2, body.Count);
            Assert.Equal("PUBLIC", (string?)body["visibility"]);
        }

        [Fact]
        public async Task WithoutAKey_CloudCallsSayWhy()
        {
            var (cloud, _) = Cloud(key: null);
            Assert.False(cloud.HasKey);
            var ex = await Assert.ThrowsAsync<RobloxKeyMissingException>(() => cloud.GetUniverseAsync(1, CancellationToken.None));
            Assert.Contains("OmniBlox_RobloxApiKey", ex.Message);
        }

        [Fact]
        public async Task Throttled_CallsRetryWithBackoff()
        {
            int calls = 0;
            var (cloud, rec) = Cloud();
            rec.Respond = _ => ++calls < 3
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"displayName\":\"x\"}", Encoding.UTF8, "application/json") };
            var info = await cloud.GetUniverseAsync(5, CancellationToken.None);
            Assert.Equal("x", info.DisplayName);
            Assert.Equal(3, calls);
        }

        [Fact]
        public async Task AuthFailures_SurfaceWithAHint()
        {
            var (cloud, rec) = Cloud();
            rec.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"no\"}", Encoding.UTF8, "application/json") };
            var ex = await Assert.ThrowsAsync<RobloxApiException>(() => cloud.CreateGamePassAsync(1, new ProductSpec { Name = "VIP", PriceRobux = 99 }, CancellationToken.None));
            Assert.True(ex.IsAuth);
            Assert.Contains("game-pass:write", ex.Message);
        }

        [Theory]
        [InlineData("[{\"passed\":4,\"failed\":0,\"results\":[]}]", true, 0)]
        [InlineData("[{\"passed\":3,\"failed\":1,\"results\":[{\"name\":\"Core · coins\",\"ok\":false,\"detail\":\"expected 10, got 0\"}]}]", false, 1)]
        [InlineData("{\"results\":[{\"passed\":2,\"failed\":0,\"results\":[]}]}", true, 0)]
        public void EngineOutput_Parses(string output, bool ok, int failures)
        {
            var r = EngineTests.Parse(output, 3);
            Assert.Equal(ok, r.Ok);
            Assert.Equal(failures, r.Failures.Count);
            if (failures > 0) Assert.Contains("expected 10", r.Failures[0]);
        }
    }

    public class InsightsTests
    {
        [Fact]
        public void Telemetry_RejectsJunk_RateLimits_AndSummarizesFunnels()
        {
            using var dir = new TempDir();
            using var store = new TelemetryStore(Path.Combine(dir.Path, "t.db"));
            store.Open();
            var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
            var payload = JObject.Parse(@"{""events"":[
                {""n"":""session_start"",""s"":""a""},{""n"":""funnel"",""s"":""a"",""f"":{""step"":1}},{""n"":""funnel"",""s"":""a"",""f"":{""step"":2}},
                {""n"":""session_start"",""s"":""b""},{""n"":""funnel"",""s"":""b"",""f"":{""step"":1}},
                {""n"":""purchase"",""s"":""a"",""f"":{""key"":""vip"",""robux"":149}},
                {""n"":""error"",""f"":{""message"":""attempt to index nil"",""count"":1}},
                {""n"":""bad name!""},{""n"":""""}]}");
            Assert.Equal(7, store.Ingest("g1", payload, now));
            var s = store.Summarize("g1", now.AddDays(-1));
            Assert.Equal(2, s.Sessions);
            Assert.Equal(2, s.Funnel[1]);
            Assert.Equal(1, s.Funnel[2]);
            Assert.Equal(149, s.PurchaseRobux);
            Assert.Equal("attempt to index nil", s.TopErrors.Single().Message);
            Assert.Contains("Funnel", TelemetryStore.Describe(s));
            Assert.Equal(0, store.Summarize("other", now.AddDays(-1)).Events);

            // A flood is cut off at the per-minute limit.
            var flood = new JObject { ["events"] = new JArray(Enumerable.Range(0, 600).Select(_ => new JObject { ["n"] = "heartbeat" })) };
            int total = 0;
            for (int i = 0; i < 10; i++) total += store.Ingest("g1", flood, now.AddSeconds(i));
            Assert.True(total <= TelemetryStore.MaxEventsPerMinute);
        }

        [Fact]
        public void Playbook_MergesRepeats_AndFindsByTopic()
        {
            using var dir = new TempDir();
            var store = new OmniBloxStore(dir.Path);
            store.Load();
            var pb = new Playbook(store, () => DateTime.UtcNow);
            string a = pb.Add("pricing", "Cheap developer products sell better than expensive game passes in obbies.", "game 1", "worked", 0.5, new[] { "obby" }, "g1");
            string b = pb.Add("pricing", "Cheap developer products sell better than expensive game passes in obbies!", "game 2", "worked", 0.5, new[] { "obby" }, "g2");
            Assert.Equal(a, b);
            var lesson = store.Read(s => s.Playbook.Single());
            Assert.Equal(0.6, lesson.Confidence, 3);
            Assert.Contains("game 2", lesson.Evidence);
            pb.Add("marketing", "Thumbnails with one big character get more clicks than wide landscapes.", "game 1", "worked", 0.5, null, "g1");
            Assert.Single(pb.Search("thumbnail clicks"));
            Assert.Single(pb.ForStage(GameStage.Research, "obby", 1));
        }

        [Fact]
        public void Store_QuarantinesACorruptDossier_InsteadOfCrashing()
        {
            using var dir = new TempDir();
            var store = new OmniBloxStore(dir.Path);
            store.Load();
            store.Mutate(s => { s.Games["g1"] = new GameDossier { GameId = "g1", WorkingTitle = "A" }; s.MarkGame("g1"); });
            store.FlushAsync().GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(dir.Path, "games", "g1", "dossier.json"), "{ not json");
            var reloaded = new OmniBloxStore(dir.Path);
            reloaded.Load();
            Assert.Equal(0, reloaded.Read(s => s.Games.Count));
            Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "games", "g1"), "dossier.json.corrupt-*"));
        }

        [Fact]
        public void Harvester_PrefersWhatAGameNeeds_ThenRotatesTheVocabulary()
        {
            var h = new HarvesterState();
            var now = DateTime.UtcNow;
            h.PriorityQueries.Add("pirate ship");
            Assert.Equal(("pirate ship", 5), (AssetHarvester.PickQuery(h, 0, now).Query, AssetHarvester.PickQuery(h, 0, now).Priority));
            h.PriorityQueries.Clear();
            Assert.Null(AssetHarvester.PickQuery(h, AssetHarvester.MaxBacklog, now).Query);
            var first = AssetHarvester.PickQuery(h, 0, now).Query!;
            h.QueryLastRunUtc[first] = now;
            Assert.NotEqual(first, AssetHarvester.PickQuery(h, 0, now).Query);
        }
    }

    public class CycleRunnerTests
    {
        private sealed class OneShotLlm : IOmniBloxLlm
        {
            public Queue<LlmTurn> Turns { get; } = new();
            public List<string> Users { get; } = new();
            public List<string> StableUsers { get; } = new();
            public List<string> Results { get; } = new();
            /// <summary>The tool names each query offered, in order.</summary>
            public List<string> ToolLists { get; } = new();
            public TimeSpan PrefixLifetime => TimeSpan.FromMinutes(5);
            public Task<(bool Ok, string? Reason)> CheckAvailableAsync(CancellationToken ct) => Task.FromResult((true, (string?)null));
            public bool HasSpareCapacity() => true;
            public void StartSession(string sessionId, string systemPrompt) { }
            public void AppendUser(string sessionId, string text, IReadOnlyList<byte[]>? pngImages = null) => Users.Add(text);
            public void AppendStableUser(string sessionId, string text, IReadOnlyList<byte[]>? pngImages = null) => StableUsers.Add(text);
            public void AppendAssistant(string sessionId, string text) { }
            public void AppendToolResult(string sessionId, string toolCallId, string toolName, string content) => Results.Add(content);
            public Task<LlmTurn> QueryAsync(string sessionId, IReadOnlyList<ToolDefinition> tools, LlmCallOptions options, CancellationToken ct)
            {
                ToolLists.Add(string.Join(",", tools.Select(t => t.Name)));
                return Task.FromResult(Turns.Count > 0 ? Turns.Dequeue() : new LlmTurn { Success = true, Text = "done talking" });
            }
            public void EndSession(string sessionId) { }
        }

        private static LlmTurn Call(string name, string args, long prompt = 1000, long cached = 0) => new()
        {
            Success = true,
            PromptTokens = prompt,
            CachedTokens = cached,
            ToolCalls = new List<LlmToolCall> { new() { Id = Guid.NewGuid().ToString("N"), Name = name, ArgumentsJson = args } },
        };

        private static CycleRequest Request(AgentToolbox tools, int maxTurns = 10, long wrapUp = 70_000) => new()
        {
            GameId = "g1",
            Stage = GameStage.Build,
            Role = "builder",
            SystemPrompt = "sys",
            Briefing = "brief",
            Tools = tools,
            MaxTurns = maxTurns,
            WrapUpAtPromptTokens = wrapUp,
        };

        private static AgentToolbox Tools() => new AgentToolbox()
            .Add("echo", "echo", Schema.Object(Schema.Req("text", Schema.Str("t"))), a => ToolResult.Ok("echo " + Args.Str(a, "text")))
            .Add("broken", "always fails", Schema.Object(), _ => ToolResult.Error("nope"));

        [Fact]
        public async Task ACycleEndsWithAStructuredHandOver()
        {
            var llm = new OneShotLlm();
            llm.Turns.Enqueue(Call("echo", "{\"text\":\"hi\"}"));
            llm.Turns.Enqueue(Call("finish_cycle", "{\"summary\":\"built the lobby\",\"decisions\":[{\"what\":\"use red lava\",\"why\":\"visible\"}],\"key_facts\":[\"lobby is p12\"],\"stage_complete\":true}"));
            var outcome = await new AgentCycleRunner(llm).RunAsync(Request(Tools()), CancellationToken.None);
            Assert.True(outcome.Finished);
            Assert.True(outcome.StageComplete);
            Assert.Equal("built the lobby", outcome.Report.Done);
            Assert.Equal("use red lava", outcome.NewDecisions.Single().What);
            Assert.Equal("lobby is p12", outcome.KeyFacts.Single());
            Assert.Equal("echo hi", llm.Results[0].Split('\n')[0]);
        }

        [Fact]
        public async Task MissingArguments_AndRepeatedFailures_AreExplainedToTheModel()
        {
            var llm = new OneShotLlm();
            llm.Turns.Enqueue(Call("echo", "{}"));
            for (int i = 0; i < 3; i++) llm.Turns.Enqueue(Call("broken", "{}"));
            llm.Turns.Enqueue(Call("nope_tool", "{}"));
            llm.Turns.Enqueue(Call("finish_cycle", "{\"summary\":\"gave up\"}"));
            var outcome = await new AgentCycleRunner(llm).RunAsync(Request(Tools()), CancellationToken.None);
            Assert.Contains(llm.Results, r => r.Contains("'text' is required"));
            Assert.Contains(llm.Results, r => r.Contains("failed 3 times"));
            Assert.Contains(llm.Results, r => r.Contains("no tool called 'nope_tool'"));
            Assert.Equal(5, outcome.ToolErrors);
        }

        [Fact]
        public async Task TalkingInsteadOfActing_IsNudged_ThenTheHandOverIsRequested()
        {
            var llm = new OneShotLlm(); // only ever answers with text
            var outcome = await new AgentCycleRunner(llm).RunAsync(Request(Tools()), CancellationToken.None);
            Assert.Equal(2, llm.Users.Count(u => u.StartsWith("Keep going")));
            Assert.Contains(llm.Users, u => u.StartsWith("This cycle is over"));
            Assert.False(outcome.Finished);
            Assert.Equal("cut short", outcome.Report.Outcome);
        }

        [Fact]
        public async Task AFullContext_TriggersAWrapUp()
        {
            var llm = new OneShotLlm();
            llm.Turns.Enqueue(Call("echo", "{\"text\":\"a\"}", prompt: 80_000));
            llm.Turns.Enqueue(Call("finish_cycle", "{\"summary\":\"wrapped\"}"));
            var outcome = await new AgentCycleRunner(llm).RunAsync(Request(Tools(), wrapUp: 70_000), CancellationToken.None);
            Assert.Contains(llm.Users, u => u.Contains("context is nearly full"));
            Assert.True(outcome.Finished);
        }

        [Fact]
        public async Task EveryCall_OffersTheSameTools_TheWrapUpIncluded()
        {
            // The tool schemas sit at the head of the prompt: a narrower list for the hand-over would make
            // the provider prefill the whole cycle again. A wrong call then is answered, not run.
            var llm = new OneShotLlm();
            llm.Turns.Enqueue(Call("echo", "{\"text\":\"a\"}"));
            llm.Turns.Enqueue(Call("echo", "{\"text\":\"b\"}"));
            llm.Turns.Enqueue(Call("echo", "{\"text\":\"c\"}"));
            llm.Turns.Enqueue(Call("finish_cycle", "{\"summary\":\"handed over\"}"));
            var outcome = await new AgentCycleRunner(llm).RunAsync(Request(Tools(), maxTurns: 2), CancellationToken.None);

            Assert.True(outcome.Finished);
            Assert.Equal("handed over", outcome.Report.Done);
            Assert.Equal(4, llm.ToolLists.Count);
            Assert.Equal("echo,broken,finish_cycle", llm.ToolLists.Distinct().Single());
            Assert.Contains(llm.Results, r => r.StartsWith("Not run: this cycle is over"));
            Assert.DoesNotContain(llm.Results, r => r.StartsWith("echo c"));
            Assert.Equal(4, outcome.Report.Turns); // every model call is counted, the hand-over's too
        }

        [Fact]
        public async Task ReSentTokens_AreMeasuredAgainstWhatTheCacheServed()
        {
            var llm = new OneShotLlm();
            llm.Turns.Enqueue(Call("echo", "{\"text\":\"a\"}", prompt: 1000, cached: 300));
            llm.Turns.Enqueue(Call("echo", "{\"text\":\"b\"}", prompt: 1500, cached: 1000));
            llm.Turns.Enqueue(Call("finish_cycle", "{\"summary\":\"done\"}", prompt: 2000, cached: 600));
            var outcome = await new AgentCycleRunner(llm).RunAsync(Request(Tools()), CancellationToken.None);

            // Turn 2 re-sent turn 1's 1000 tokens (all cached); turn 3 re-sent 1500 and got only 600 back.
            Assert.Equal(2500, outcome.Report.ResentTokens);
            Assert.Equal(1600, outcome.Report.ResentCachedTokens);
            Assert.Equal(1900, outcome.Report.CachedTokens);
        }

        [Fact]
        public async Task TheBriefing_GoesOutWithoutAReceiptStamp()
        {
            var llm = new OneShotLlm();
            llm.Turns.Enqueue(Call("finish_cycle", "{\"summary\":\"done\"}"));
            await new AgentCycleRunner(llm).RunAsync(Request(Tools()), CancellationToken.None);
            Assert.Equal("brief", llm.StableUsers.Single());
            Assert.DoesNotContain("brief", llm.Users);
        }
    }

    public class MarketingTests
    {
        [Fact]
        public void IconsAndThumbnails_AreTheRightSize_AndRenderTheRealWorld()
        {
            using var dir = new TempDir();
            var ws = BuildTests.ObbyWorkspace(dir.Path);
            var (load, info) = BuildTests.TreeAsset();
            var result = PlaceCompiler.Compile(new CompileInput { GameId = "g", Workspace = ws, LoadAsset = load, AssetInfo = info });
            var scene = ws.LoadScene();
            var studio = new MarketingStudio(null);
            byte[] render = SceneRenderer.Render(result.Parts, scene, "iso", 1024, 1024, caption: false);
            // Stands in for a Roblox thumbnail of a placed model: a clean picture, no caption, grid or arrow.
            var tree = SchematicRenderer.PrimitivesOf(AssetAnalyzer.Analyze(AssetAnalysisTests.SampleTree(), 1, "t").Model);
            var (tmin, tmax) = SchematicRenderer.Bounds(tree);
            var tc = (tmin + tmax) * 0.5f;
            float tr = MathF.Max(2, (tmax - tmin).Magnitude * 0.5f);
            byte[] sticker = SchematicRenderer.RenderPng(tree, new RenderCamera { Eye = tc + new RbxVector3(tr * 1.9f, tr * 1.5f, -tr * 2.3f), Target = tc, FieldOfView = 38 }, new RenderOptions { Width = 256, Height = 256, Grid = false });
            byte[] icon = studio.MakeIcon(new ArtSpec { Headline = "Lava Obby", SceneRender = render, Featured = { sticker }, Badge = "NEW" });
            byte[] thumb = studio.MakeThumbnail(new ArtSpec { Headline = "Lava Forest Obby", Subtitle = "Can you reach the finish?", SceneRender = SceneRenderer.Render(result.Parts, scene, "iso", 1920, 1080, caption: false), Featured = { sticker } });
            using (var bmp = SkiaSharp.SKBitmap.Decode(icon)) Assert.Equal((512, 512), (bmp.Width, bmp.Height));
            using (var bmp = SkiaSharp.SKBitmap.Decode(thumb)) Assert.Equal((1920, 1080), (bmp.Width, bmp.Height));
            Assert.False(studio.CanMakeVideo);

            string? outDir = Environment.GetEnvironmentVariable("OMNIBLOX_RENDER_OUT");
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllBytes(Path.Combine(outDir, "icon.png"), icon);
                File.WriteAllBytes(Path.Combine(outDir, "thumb.png"), thumb);
                File.WriteAllBytes(Path.Combine(outDir, "scene_overview.png"), SceneRenderer.Render(result.Parts, scene, "overview"));
                File.WriteAllBytes(Path.Combine(outDir, "scene_spawn.png"), SceneRenderer.Render(result.Parts, scene, "spawn"));
            }
        }
    }
}
