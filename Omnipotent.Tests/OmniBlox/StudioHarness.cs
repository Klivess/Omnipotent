using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Insights;
using Omnipotent.Services.OmniBlox.Marketing;
using Omnipotent.Services.OmniBlox.Pipeline;
using Omnipotent.Services.OmniBlox.Pipeline.Tools;
using Omnipotent.Services.OmniBlox.Rbx;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// Plays the language model: each agent cycle of a role consumes the next scripted list of tool calls
    /// (then calls finish_cycle), and the one-shot sessions (labels, compliance, post-mortems) get canned JSON.
    /// Every tool result is logged so tests can assert on what the agents saw.
    /// </summary>
    internal sealed class ScriptedLlm : IOmniBloxLlm
    {
        private sealed class Session
        {
            public string Role = "";
            public List<(string Tool, object Args)> Steps = new();
            public int Next;
            public int Turns;
            public JObject? Finish;
            /// <summary>Every message in order, pictures as content hashes: what the provider would be sent.</summary>
            public List<string> Messages = new();
            public int LastPictureCount;
        }

        private readonly ConcurrentDictionary<string, Session> sessions = new();
        private readonly Dictionary<string, Queue<(List<(string, object)> Steps, JObject? Finish)>> scripts = new();

        public List<string> ToolResults { get; } = new();
        public List<string> Briefings { get; } = new();
        /// <summary>The message list of every label call, at the moment it was sent.</summary>
        public List<List<string>> LabelPrompts { get; } = new();
        public int ImageMessages { get; private set; }
        public bool Available { get; set; } = true;
        public TimeSpan PrefixLifetime { get; set; } = TimeSpan.FromMinutes(5);
        public string LabelJson { get; set; } = JsonConvert.SerializeObject(new
        {
            what = "low-poly pine tree",
            category = "nature",
            subcategory = "tree",
            style = "low-poly",
            themes = new[] { "forest" },
            description = "A tall pine tree with a brown trunk and a round green crown.",
            goodFor = new[] { "forest decoration" },
            pairsWith = new[] { "rocks", "bushes" },
            front = "front",
            walkable = false,
            quality = 7,
            issues = Array.Empty<string>(),
        });
        public string ComplianceJson { get; set; } = "{\"ok\": true, \"problems\": [], \"suggestions\": [\"add an update log\"]}";
        public string PostMortemJson { get; set; } = JsonConvert.SerializeObject(new
        {
            workedWell = "Short stages kept players moving; 40% reached stage 3.",
            didNotWork = "Few players bought the VIP pass.",
            lessons = new[]
            {
                new { topic = "mechanic", text = "Short obby stages with a checkpoint every few jumps keep new players going.", outcome = "worked", confidence = 0.6, tags = new[] { "obby" } },
                new { topic = "pricing", text = "A 149 Robux VIP pass sold poorly in a three-stage obby; offer cheaper boosts first.", outcome = "failed", confidence = 0.5, tags = new[] { "obby" } },
            },
        });

        public void Script(string role, params (string Tool, object Args)[] steps) => Script(role, null, steps);

        public void Script(string role, object? finish, params (string Tool, object Args)[] steps)
        {
            if (!scripts.TryGetValue(role, out var q)) scripts[role] = q = new();
            q.Enqueue((steps.ToList(), finish == null ? null : JObject.FromObject(finish)));
        }

        public int Pending(string role) => scripts.TryGetValue(role, out var q) ? q.Count : 0;

        public Task<(bool Ok, string? Reason)> CheckAvailableAsync(CancellationToken ct) =>
            Task.FromResult(Available ? (true, (string?)null) : (false, "AIRouter is not the active provider (test)"));

        public bool HasSpareCapacity() => true;

        private static string RoleOf(string sessionId)
        {
            var parts = sessionId.Split('-');
            return parts.Length > 1 ? parts[1] : sessionId;
        }

        public void StartSession(string sessionId, string systemPrompt)
        {
            var s = new Session { Role = RoleOf(sessionId) };
            if (scripts.TryGetValue(s.Role, out var q) && q.Count > 0)
            {
                var (steps, finish) = q.Dequeue();
                s.Steps = steps;
                s.Finish = finish;
            }
            s.Messages.Add("system: " + systemPrompt);
            sessions[sessionId] = s;
        }

        public void AppendUser(string sessionId, string text, IReadOnlyList<byte[]>? pngImages = null) => Append(sessionId, "user (stamped)", text, pngImages);

        public void AppendStableUser(string sessionId, string text, IReadOnlyList<byte[]>? pngImages = null) => Append(sessionId, "user", text, pngImages);

        public void AppendAssistant(string sessionId, string text)
        {
            if (sessions.TryGetValue(sessionId, out var s)) s.Messages.Add("assistant: " + text);
        }

        private void Append(string sessionId, string role, string text, IReadOnlyList<byte[]>? pngImages)
        {
            if (pngImages is { Count: > 0 }) ImageMessages++;
            if (!sessions.TryGetValue(sessionId, out var s)) return;
            if (s.Turns == 0 && s.Role is not ("label" or "calibrate" or "compliance" or "postmortem")) Briefings.Add(text);
            var pictures = (pngImages ?? Array.Empty<byte[]>()).Select(p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(p))[..12]);
            s.Messages.Add($"{role}: {text} [{string.Join(",", pictures)}]");
            s.LastPictureCount = pngImages?.Count ?? 0;
        }

        /// <summary>How many calibrations ran.</summary>
        public int Calibrations { get; private set; }

        /// <summary>Scores side-by-side models evenly from 9 (the first picture) down to 2 (the last).</summary>
        private static string CalibrationJson(int models) => JsonConvert.SerializeObject(new
        {
            scores = Enumerable.Range(1, models).Select(i => new
            {
                model = i,
                quality = Math.Round(9 - 7.0 * (i - 1) / Math.Max(1, models - 1), 1),
                issues = i * 2 > models ? new[] { "plain shapes and flat colours" } : Array.Empty<string>(),
            }),
        });

        public void AppendToolResult(string sessionId, string toolCallId, string toolName, string content)
        {
            lock (ToolResults) ToolResults.Add($"{RoleOf(sessionId)}.{toolName}: {content}");
        }

        public Task<LlmTurn> QueryAsync(string sessionId, IReadOnlyList<ToolDefinition> tools, LlmCallOptions options, CancellationToken ct)
        {
            var s = sessions[sessionId];
            s.Turns++;
            switch (s.Role)
            {
                case "label":
                    lock (LabelPrompts) LabelPrompts.Add(s.Messages.ToList());
                    return Text(LabelJson);
                case "calibrate":
                    Calibrations++;
                    return Text(CalibrationJson(s.LastPictureCount));
                case "compliance": return Text(ComplianceJson);
                case "postmortem": return Text(PostMortemJson);
            }
            if (s.Next < s.Steps.Count && tools.Any(t => t.Name == s.Steps[s.Next].Tool))
            {
                var (tool, args) = s.Steps[s.Next++];
                return Call(s, tool, args);
            }
            var finish = s.Finish ?? JObject.FromObject(new { summary = $"Scripted {s.Role} cycle done.", next_steps = new[] { "carry on" }, key_facts = new[] { $"{s.Role} ran" }, stage_complete = true });
            return Call(s, AgentCycleRunner.FinishToolName, finish);
        }

        public void EndSession(string sessionId) => sessions.TryRemove(sessionId, out _);

        private static Task<LlmTurn> Text(string text) => Task.FromResult(new LlmTurn { Success = true, Text = text, PromptTokens = 900, CachedTokens = 600, CompletionTokens = 120, Model = "fake" });

        private int calls;

        /// <summary>
        /// Usage shaped like a warm append-only session: the first turn reuses only the role's cached head,
        /// each later turn re-sends the previous prompt (all of it served from cache) plus ~1.5K new tokens.
        /// </summary>
        private Task<LlmTurn> Call(Session session, string tool, object args)
        {
            long prompt = 6000 + 1500L * (session.Turns - 1);
            return Task.FromResult(new LlmTurn
            {
                Success = true,
                ToolCalls = new List<LlmToolCall> { new() { Id = "call_" + Interlocked.Increment(ref calls), Name = tool, ArgumentsJson = args is string s ? s : JsonConvert.SerializeObject(args) } },
                PromptTokens = prompt,
                CachedTokens = session.Turns == 1 ? 4500 : prompt - 1500,
                CompletionTokens = 200,
                Model = "fake",
            });
        }
    }

    /// <summary>A stand-in for every Roblox endpoint OmniBlox calls, recording what it was asked.</summary>
    internal sealed class FakeRoblox : HttpMessageHandler
    {
        public const long AssetId = 900001;
        public List<(string Method, string Url, string Body)> Requests { get; } = new();
        public byte[] ModelFile { get; set; } = Array.Empty<byte>();
        public byte[] Png { get; set; } = Array.Empty<byte>();
        /// <summary>What the game's engine test run answers: a green run of the kit's phases unless a test says otherwise.</summary>
        public string EngineOutput { get; set; } = "{\"results\":[{\"passed\":3,\"failed\":0,\"results\":[" +
            "{\"name\":\"Kit · profiles start fresh\",\"ok\":true,\"phase\":\"kit\",\"owner\":\"kit\"}," +
            "{\"name\":\"Load · every module loads\",\"ok\":true,\"phase\":\"load\",\"owner\":\"kit\"}," +
            "{\"name\":\"Boot · the server starts cleanly\",\"ok\":true,\"phase\":\"boot\",\"owner\":\"kit\"}],\"errors\":[],\"warnings\":[]}]}";
        /// <summary>Answer restartServers with 403, as a key without universe:write would get.</summary>
        public bool RestartRefused { get; set; }
        private long nextVersion = 1, nextId = 7000;

        public IEnumerable<string> Urls(string method) => Requests.Where(r => r.Method == method).Select(r => r.Url);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();
            string body = request.Content == null ? "" : request.Content is ByteArrayContent b && request.Content.Headers.ContentType?.MediaType == "application/xml" ? "<place>" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request.Method.Method, url, body.Length > 4000 ? body[..4000] : body));
            var uri = request.RequestUri;
            string path = uri.AbsolutePath;

            if (uri.Host == "fake.rbxcdn.com")
                return path.EndsWith(".rbxm") ? Bytes(ModelFile, "application/octet-stream") : Bytes(Png, "image/png");
            if (path.EndsWith("/toolbox-service/v2/assets:search"))
                return Json(new
                {
                    nextPageToken = (string?)null,
                    totalResults = 1,
                    creatorStoreAssets = new[]
                    {
                        new
                        {
                            voting = new { upVotes = 120, downVotes = 4, upVotePercent = 97 },
                            creator = new { userId = 42, name = "TreeMaker", verified = true },
                            creatorStoreProduct = new { purchasePrice = new { currencyCode = "USD", quantity = new { significand = 0, exponent = 0 } }, purchasable = true },
                            asset = new { id = AssetId, name = "Pine Tree", description = "A nice pine", assetTypeId = 10, hasScripts = true, scriptCount = 2, objectMeshSummary = new { triangles = 800, vertices = 900 }, instanceCounts = new { meshPart = 0 }, categoryPath = "3d__nature", createTime = "2024-01-01T00:00:00Z", updateTime = "2024-02-01T00:00:00Z" },
                        },
                    },
                });
            if (uri.Host == "thumbnails.roblox.com")
                return Json(new { data = new[] { new { targetId = AssetId, state = "Completed", imageUrl = "https://fake.rbxcdn.com/thumb.png" } } });
            if (path.StartsWith("/asset-delivery-api/v1/assetId/"))
                return Json(new { location = "https://fake.rbxcdn.com/model.rbxm" });
            if (path.EndsWith("/explore-api/v1/get-sorts"))
                return Json(new
                {
                    sorts = new object[]
                    {
                        new { contentType = "Games", sortId = "top-trending", sortDisplayName = "Top Trending", games = new[] { new { universeId = 111L, rootPlaceId = 222L, name = "Lava Rush Obby", playerCount = 5400, totalUpVotes = 9000, totalDownVotes = 1000, isSponsored = false, genreL1 = "Obby & Platformer", contentMaturity = "minimal" } } },
                    },
                    nextSortsPageToken = "",
                });
            if (uri.Host == "games.roblox.com" && path == "/v1/games")
                return Json(new { data = new[] { new { id = 111L, rootPlaceId = 222L, name = "Lava Rush Obby", description = "Escape the lava!", creator = new { name = "Studio", type = "Group" }, playing = 5400, visits = 12_000_000, favoritedCount = 80_000, maxPlayers = 30, genre_l1 = "Obby & Platformer", genre_l2 = "Tower Obby", created = "2025-09-01T00:00:00Z", updated = "2026-10-01T00:00:00Z" } } });
            if (uri.Host == "games.roblox.com" && path == "/v1/games/votes")
                return Json(new { data = new[] { new { id = 111L, upVotes = 9000, downVotes = 1000 } } });
            if (path.StartsWith("/universes/v1/") && path.EndsWith("/versions"))
                return Json(new { versionNumber = nextVersion++ });
            if (path.Contains("/luau-execution-session-tasks"))
                return Json(JObject.Parse($"{{\"path\":\"universes/1/places/2/versions/1/luau-execution-sessions/s1/tasks/t1\",\"state\":\"COMPLETE\",\"output\":{EngineOutput}}}"));
            if (path.EndsWith("/logs"))
                return Json(new { luauExecutionSessionTaskLogs = new[] { new { messages = new[] { "ok" } } } });
            if (path.Contains("/game-passes/v1/") && request.Method == HttpMethod.Post)
                return Json(new { gamePassId = nextId++, name = "pass" });
            if (path.Contains("/developer-products/v2/") && request.Method == HttpMethod.Post)
                return Json(new { productId = nextId++, name = "product" });
            if (path.Contains("/game-passes/v1/") || path.Contains("/developer-products/v2/"))
                return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent("") };
            if (path.StartsWith("/cloud/v2/universes/") && request.Method == HttpMethod.Patch)
                return Json(new { path = path.TrimStart('/') });
            if (path.StartsWith("/cloud/v2/universes/") && path.EndsWith(":restartServers") && request.Method == HttpMethod.Post)
                return RestartRefused
                    ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"insufficient scope\"}", Encoding.UTF8, "application/json") }
                    : Json(new { });
            if (path.Contains("/thumbnail-personalization-api/"))
                return Json(new { operationIds = new[] { "op1" } });
            if (path.Contains("/analytics-query-api/"))
            {
                string metric = (string?)JObject.Parse(body)["metric"] ?? "";
                double value = metric switch { "DailyActiveUsers" => 120, "DailyRevenue" => 340, "ForwardD1Retention" => 0.22, "Visits" => 300, _ => 5 };
                return Json(new { done = true, response = new { values = new[] { new { breakdowns = Array.Empty<object>(), dataPoints = new[] { new { time = "2026-10-21T00:00:00Z", value } } } } } });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"fake: no route for " + path + "\"}", Encoding.UTF8, "application/json") };
        }

        private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = new StringContent(o is JToken t ? t.ToString(Formatting.None) : JsonConvert.SerializeObject(o), Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Bytes(byte[] b, string type)
        {
            var c = new ByteArrayContent(b);
            c.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = c };
        }
    }

    internal sealed class FakeWeb : IWebResearch
    {
        public Task<string> SearchAsync(string query, int maxResults, string? timeRange, CancellationToken ct) =>
            Task.FromResult($"1. \"Lava obbies are trending on Roblox\" — players love short, colourful stages (result for '{query}').");

        public Task<string> FetchAsync(string url, CancellationToken ct) => Task.FromResult("Page text: tower and lava obbies keep trending in 2026.");
    }

    /// <summary>A whole studio wired to fakes, with a clock tests can move.</summary>
    internal sealed class StudioHarness : IDisposable
    {
        private readonly TempDir dir = new();
        public DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        public ScriptedLlm Llm { get; } = new();
        public FakeRoblox Roblox { get; } = new();
        public StudioContext Ctx { get; }
        public Orchestrator Orchestrator { get; }
        public AssetHarvester Harvester { get; }

        public StudioHarness(bool apiKey = true, bool requireApproval = false)
        {
            ResearchTools.ClearChartCache();
            var analysis = AssetAnalysisTests.SampleTree();
            Roblox.ModelFile = RbxXmlWriter.WriteBytes(analysis);
            Roblox.Png = SchematicRenderer.RenderPng(new List<RenderPrimitive> { new(RbxCFrame.Identity, new RbxVector3(2, 2, 2), "Block", new RbxColor3uint8(30, 140, 60), 0, false) },
                new RenderCamera { Eye = new RbxVector3(6, 6, -6), Target = RbxVector3.Zero }, new RenderOptions { Width = 64, Height = 64, Grid = false });

            Func<DateTime> clock = () => Now;
            Task Advance(TimeSpan t, CancellationToken _) { Now += t; return Task.CompletedTask; }
            var http = new RobloxHttp(new HttpClient(Roblox), clock, Advance);
            var settings = new StudioSettings
            {
                Enabled = true,
                ApiKey = apiKey ? "test-key" : null,
                CreatorUserId = 42,
                Slots = { new ExperienceSlot { UniverseId = 1001, PlaceId = 2002 } },
                RequireLaunchApproval = requireApproval,
                HarvesterEnabled = true,
            };
            var store = new OmniBloxStore(Path.Combine(dir.Path, "studio"));
            store.Load();
            var library = new AssetLibrary(Path.Combine(dir.Path, "studio", "assets"));
            library.Open();
            var telemetry = new TelemetryStore(Path.Combine(dir.Path, "studio", "telemetry.db"));
            telemetry.Open();
            var publicApi = new RobloxPublicClient(http);
            var cloud = new RobloxCloudClient(http, () => settings.ApiKey, Advance);
            Harvester = new AssetHarvester(library, publicApi, cloud, new AssetLabeler(Llm), Llm, store, clock, _ => { });
            Ctx = new StudioContext
            {
                Store = store,
                Library = library,
                Public = publicApi,
                Cloud = cloud,
                Llm = Llm,
                Runner = new AgentCycleRunner(Llm, clock, Advance),
                Playbook = new Playbook(store, clock),
                Telemetry = telemetry,
                Analytics = new AnalyticsSync(cloud, store, clock),
                Marketing = new MarketingStudio(null),
                Harvester = Harvester,
                Web = new FakeWeb(),
                Clock = clock,
                Log = _ => { },
                Settings = settings,
            };
            Orchestrator = new Orchestrator(Ctx);
        }

        public GameDossier Game(string id) => Ctx.Game(id)!;

        public GameDossier OnlyGame() => Ctx.Store.Read(s => OmniBloxStore.Clone(s.Games.Values.Single()));

        /// <summary>Steps the orchestrator until the predicate holds (or fails after maxSteps).</summary>
        public async Task<int> StepUntilAsync(Func<bool> done, int maxSteps = 30)
        {
            for (int i = 0; i < maxSteps; i++)
            {
                if (done()) return i;
                await Orchestrator.StepAsync(CancellationToken.None);
            }
            if (!done()) throw new Xunit.Sdk.XunitException("Gave up waiting. Status: " + Orchestrator.Status + "\nLast results:\n" + string.Join("\n", Llm.ToolResults.TakeLast(12)));
            return maxSteps;
        }

        public void Dispose()
        {
            Ctx.Library.Dispose();
            Ctx.Telemetry.Dispose();
            dir.Dispose();
        }
    }
}
