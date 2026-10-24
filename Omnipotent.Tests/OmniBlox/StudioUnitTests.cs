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
using SkiaSharp;

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
        public void AFixedBundle_NamedLikeAContainer_IsFine_ButAChanceWordAlwaysFails()
        {
            var bundle = ComplianceChecker.Check(Page(), new[] { Product("Starter Crate", 49, "currency:500", description: "500 coins, always") }, null);
            Assert.DoesNotContain(bundle.Errors, e => e.Contains("paid random item"));
            var lucky = ComplianceChecker.Check(Page(), new[] { Product("Lucky Crate", 49, "currency:500", description: "500 coins") }, null);
            Assert.Contains(lucky.Errors, e => e.Contains("paid random item"));
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
        [InlineData("[{\"passed\":1,\"failed\":0,\"results\":[{\"name\":\"Core · coins\",\"ok\":true,\"phase\":\"game\"}]}]", true, 0)]
        [InlineData("[{\"passed\":3,\"failed\":1,\"results\":[{\"name\":\"Core · coins\",\"ok\":false,\"detail\":\"expected 10, got 0\",\"phase\":\"game\"}]}]", false, 1)]
        [InlineData("{\"results\":[{\"passed\":1,\"failed\":0,\"results\":[{\"name\":\"Kit · data\",\"ok\":true,\"phase\":\"kit\",\"owner\":\"kit\"}]}]}", true, 0)]
        public void EngineOutput_Parses(string output, bool ok, int failures)
        {
            var r = EngineTests.Parse(output, 3);
            Assert.Equal(ok, r.Ok);
            Assert.Equal(failures, r.Results.Count(x => !x.Ok));
            if (failures > 0) Assert.Contains("expected 10", r.Results.First(x => !x.Ok).Detail);
        }

        [Fact]
        public void EngineOutput_WithNoResultsAtAll_IsAFailureNotAPass()
        {
            // An empty run proves nothing: the first studio could "pass" a run that never started a single check.
            var r = EngineTests.Parse("[{\"passed\":0,\"failed\":0,\"results\":[]}]", 3);
            Assert.False(r.Ok);
            Assert.False(EngineTests.Parse("", 3).Ok);
            Assert.False(EngineTests.Parse("not json at all", 3).Ok);
        }

        [Fact]
        public void EngineOutput_KitControlFailures_AreMarkedAsTheStudiosAndRuntimeErrorsAreLocated()
        {
            const string output = "[{\"passed\":1,\"failed\":1,\"results\":[" +
                "{\"name\":\"Kit · new profiles start with the configured currency\",\"ok\":false,\"detail\":\"expected 200, got 335\",\"phase\":\"kit\",\"owner\":\"kit\"}," +
                "{\"name\":\"Forest · chopping pays\",\"ok\":true,\"phase\":\"game\",\"owner\":\"game\"}]," +
                "\"errors\":[{\"message\":\"ServerScriptService.Systems.Forest:12: attempt to index nil with 'Health'\",\"phase\":\"boot\"}]}]";
            var r = EngineTests.Parse(output, 7);
            Assert.False(r.Ok);
            Assert.True(r.KitFailed);
            Assert.Single(r.Errors);
            Assert.Contains("studio", r.Summary);
            Assert.False(TestStage.GameChecksPass(r)); // a runtime error is the game's
            r.Errors.Clear();
            Assert.True(TestStage.GameChecksPass(r)); // only the kit's control failed: not the game's fault
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
            /// <summary>The pictures of each picture message, as sent.</summary>
            public List<IReadOnlyList<byte[]>> Pictures { get; } = new();
            /// <summary>keepNewest of each DropPictures call.</summary>
            public List<int> Drops { get; } = new();
            /// <summary>Bytes of pictures each query carried.</summary>
            public List<long> RequestPictureBytes { get; } = new();
            private readonly List<long> carried = new();
            /// <summary>The tool names each query offered, in order.</summary>
            public List<string> ToolLists { get; } = new();
            public TimeSpan PrefixLifetime => TimeSpan.FromMinutes(5);
            public Task<(bool Ok, string? Reason)> CheckAvailableAsync(CancellationToken ct) => Task.FromResult((true, (string?)null));
            public bool HasSpareCapacity() => true;
            public void StartSession(string sessionId, string systemPrompt) { }
            public void AppendUser(string sessionId, string text, IReadOnlyList<byte[]>? images = null)
            {
                Users.Add(text);
                if (images is not { Count: > 0 }) return;
                Pictures.Add(images);
                carried.Add(images.Sum(p => (long)p.Length));
            }
            public int DropPictures(string sessionId, int keepNewest)
            {
                Drops.Add(keepNewest);
                int dropped = Math.Max(0, carried.Count - keepNewest);
                carried.RemoveRange(0, dropped);
                return dropped;
            }
            public void AppendStableUser(string sessionId, string text, IReadOnlyList<byte[]>? pngImages = null) => StableUsers.Add(text);
            public void AppendAssistant(string sessionId, string text) { }
            public void AppendToolResult(string sessionId, string toolCallId, string toolName, string content) => Results.Add(content);
            public Task<LlmTurn> QueryAsync(string sessionId, IReadOnlyList<ToolDefinition> tools, LlmCallOptions options, CancellationToken ct)
            {
                ToolLists.Add(string.Join(",", tools.Select(t => t.Name)));
                RequestPictureBytes.Add(carried.Sum());
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
            Assert.Equal("lobby is p12", outcome.KeyFacts.Single().Text);
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
            Assert.Equal(3, llm.Users.Count(u => u.StartsWith("Keep going")));
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

        [Fact]
        public async Task AToolCallCutOffByTheOutputLimit_IsNotRun_AndTheModelIsToldToSplitIt()
        {
            // The first studio ran cut-off write_file calls, truncating the ends of files.
            var llm = new OneShotLlm();
            var cut = Call("echo", "{\"text\":\"half a fi");
            llm.Turns.Enqueue(new LlmTurn { Success = true, FinishReason = "length", ToolCalls = cut.ToolCalls, PromptTokens = 1000 });
            llm.Turns.Enqueue(Call("echo", "{\"text\":\"whole\"}"));
            llm.Turns.Enqueue(Call("finish_cycle", "{\"summary\":\"done\"}"));
            var outcome = await new AgentCycleRunner(llm).RunAsync(Request(Tools()), CancellationToken.None);
            Assert.True(outcome.Finished);
            Assert.Contains(llm.Results, r => r.Contains("NOT RUN") && r.Contains("output limit"));
            Assert.DoesNotContain(llm.Results, r => r.StartsWith("echo half"));
            Assert.Contains(llm.Results, r => r.StartsWith("echo whole"));
            Assert.Equal(1, outcome.Report.Truncations);
        }

        [Fact]
        public async Task TheOwnersMessages_ReachARunningCycle_AndComeBackAsDone()
        {
            var llm = new OneShotLlm();
            llm.Turns.Enqueue(Call("echo", "{\"text\":\"a\"}"));
            llm.Turns.Enqueue(Call("echo", "{\"text\":\"b\"}"));
            llm.Turns.Enqueue(Call("finish_cycle", "{\"summary\":\"made it bigger\",\"directives_done\":[{\"id\":\"d1\",\"response\":\"doubled the map\"}]," +
                "\"key_facts\":[{\"key\":\"map\",\"fact\":\"800 studs a side\"}],\"drop_facts\":[\"old map\"]}"));
            int asked = 0;
            var request = new CycleRequest
            {
                GameId = "g1", Stage = GameStage.Build, Role = "builder", SystemPrompt = "sys", Briefing = "brief", Tools = Tools(),
                OwnerMessages = () => ++asked == 2 ? new[] { ("d1", "Make the map twice as big") } : Array.Empty<(string, string)>(),
            };
            var outcome = await new AgentCycleRunner(llm).RunAsync(request, CancellationToken.None);
            Assert.Single(llm.Users, u => u.Contains("Message from the owner (directive d1)") && u.Contains("twice as big"));
            Assert.Equal(("d1", "doubled the map"), outcome.DirectivesDone.Single());
            Assert.Equal("map", outcome.KeyFacts.Single().Key);
            Assert.Equal("800 studs a side", outcome.KeyFacts.Single().Text);
            Assert.Contains("old map", outcome.DropFacts);
        }

        /// <summary>A PNG shaped like a world render: a gradient with grain, so it compresses as badly as a real one.</summary>
        private static byte[] Render(int seed, bool transparent = false)
        {
            const int w = 640, h = 400;
            var rng = new Random(seed);
            using var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    bitmap.SetPixel(x, y, new SKColor((byte)(x * 255 / w), (byte)(y * 255 / h), (byte)(120 + rng.Next(40)), transparent && x < w / 2 ? (byte)0 : (byte)255));
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        private static AgentToolbox PictureTools()
        {
            int seed = 0;
            return Tools().Add("look", "render", Schema.Object(), _ => ToolResult.WithImages("looked", new[] { Render(++seed) }));
        }

        private static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

        [Fact]
        public void OpaquePictures_BecomeJpeg_WhileTransparentOnesStayAsTheyAre()
        {
            var render = Render(1);
            var sent = ModelPictures.ForModel(render);
            Assert.True(IsJpeg(sent));
            Assert.True(sent.Length * 3 < render.Length, $"{sent.Length} bytes as JPEG against {render.Length} as PNG");
            Assert.Equal("image/jpeg", ModelPictures.MimeType(sent));

            // A library model's thumbnail has a transparent background that a JPEG would turn black.
            var thumbnail = Render(1, transparent: true);
            Assert.Same(thumbnail, ModelPictures.ForModel(thumbnail));
            Assert.Equal("image/png", ModelPictures.MimeType(thumbnail));
            Assert.Same(sent, ModelPictures.ForModel(sent));
        }

        [Fact]
        public async Task EveryPictureIsShownAsJpeg_AndTheOldestGoBeforeARequestOutgrowsTheLimit()
        {
            // Every picture stays in the request until it is swapped out; as PNGs, four renders were 6.7 MB and
            // AIRouter refused the request (413), losing the cycle. The agent must still see every picture it asks for.
            var llm = new OneShotLlm();
            for (int i = 0; i < 6; i++) llm.Turns.Enqueue(Call("look", "{}"));
            llm.Turns.Enqueue(Call("finish_cycle", "{\"summary\":\"looked around\"}"));
            long one = ModelPictures.ForModel(Render(1)).Length;
            var request = new CycleRequest
            {
                GameId = "g1", Stage = GameStage.Build, Role = "builder", SystemPrompt = "sys", Briefing = "brief", Tools = PictureTools(),
                PictureBytesPerRequest = one * 5 / 2,
            };
            var outcome = await new AgentCycleRunner(llm).RunAsync(request, CancellationToken.None);

            Assert.True(outcome.Finished);
            Assert.Equal(6, llm.Pictures.Count);
            Assert.All(llm.Pictures.SelectMany(p => p), p => Assert.True(IsJpeg(p)));
            Assert.All(llm.RequestPictureBytes, bytes => Assert.True(bytes <= request.PictureBytesPerRequest));
            // Pruned in batches (the third and fifth pictures pushed it over), not one picture per turn.
            Assert.Equal(new[] { 1, 1 }, llm.Drops);
            Assert.DoesNotContain(llm.Users, u => u.Contains("budget") || u.Contains("hand over"));
        }

        [Fact]
        public async Task ARequestRefusedAsTooLarge_DropsThePictures_AndTheCycleCarriesOn()
        {
            var llm = new OneShotLlm();
            llm.Turns.Enqueue(Call("look", "{}"));
            llm.Turns.Enqueue(new LlmTurn { Success = false, TooLarge = true, Error = "AIRouter request failed with status 413 (Request Entity Too Large)." });
            llm.Turns.Enqueue(Call("look", "{}"));
            llm.Turns.Enqueue(Call("finish_cycle", "{\"summary\":\"carried on\"}"));
            var request = new CycleRequest { GameId = "g1", Stage = GameStage.Build, Role = "builder", SystemPrompt = "sys", Briefing = "brief", Tools = PictureTools() };
            var outcome = await new AgentCycleRunner(llm, delay: (_, _) => Task.CompletedTask).RunAsync(request, CancellationToken.None);

            Assert.True(outcome.Finished);
            Assert.Null(outcome.Error);
            Assert.False(outcome.ProviderError);
            Assert.Equal(new[] { 0 }, llm.Drops);
            Assert.Equal(4, llm.ToolLists.Count); // the refused request is not sent again unchanged
            Assert.Contains(llm.Users, u => u.Contains("too large for the model provider"));
            Assert.Equal(2, llm.Pictures.Count); // pictures are still shown after the drop
        }

        [Fact]
        public async Task ARequestTooLargeWithNoPicturesToDrop_IsTheProvidersFailure_AndIsNotRetried()
        {
            var llm = new OneShotLlm();
            llm.Turns.Enqueue(new LlmTurn { Success = false, TooLarge = true, Error = "status 413" });
            var outcome = await new AgentCycleRunner(llm, delay: (_, _) => Task.CompletedTask).RunAsync(Request(Tools()), CancellationToken.None);

            Assert.False(outcome.Finished);
            Assert.True(outcome.ProviderError);
            Assert.Equal("status 413", outcome.Error);
            Assert.Single(llm.ToolLists);
            Assert.Equal(new[] { 0 }, llm.Drops);
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
