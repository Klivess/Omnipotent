using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.Projects;
using Omnipotent.Services.Projects.Stimulus;

namespace Omnipotent.Tests.Projects
{
    public class ProjectArtifactStoreTests
    {
        private static string NewPid() => "test_" + Guid.NewGuid().ToString("N");

        [Fact]
        public void SaveAndGet_RoundTrips()
        {
            var store = new ProjectArtifactStore(_ => { });
            string pid = NewPid();
            var art = store.Save(pid, new byte[] { 1, 2, 3 }, "image/jpeg", "a screenshot");
            Assert.Equal(new byte[] { 1, 2, 3 }, store.GetBytes(pid, art.ArtifactID));
            Assert.Equal("a screenshot", store.GetRecord(pid, art.ArtifactID)!.Description);
        }

        [Fact]
        public void RetentionSweep_DegradesOldArtifacts_KeepsDescription()
        {
            var store = new ProjectArtifactStore(_ => { });
            string pid = NewPid();
            var art = store.Save(pid, new byte[] { 9, 9 }, "image/jpeg", "load-bearing description");

            // Age the record past 48h by rewriting its index entry (the layout is the store's contract).
            string indexPath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsDirectory), "Artifacts", pid + ".index.json");
            var records = JsonConvert.DeserializeObject<List<ProjectArtifactStore.ArtifactRecord>>(File.ReadAllText(indexPath))!;
            records[0].CapturedAt = DateTime.UtcNow - TimeSpan.FromHours(49);
            File.WriteAllText(indexPath, JsonConvert.SerializeObject(records));

            var freshStore = new ProjectArtifactStore(_ => { });
            int degraded = freshStore.RunRetentionSweep();
            Assert.True(degraded >= 1);
            Assert.Null(freshStore.GetBytes(pid, art.ArtifactID));                              // raw gone
            var record = freshStore.GetRecord(pid, art.ArtifactID)!;
            Assert.True(record.Degraded);
            Assert.Equal("load-bearing description", record.Description);                       // permanent record survives
        }

        [Fact]
        public void FreshArtifacts_SurviveTheSweep()
        {
            var store = new ProjectArtifactStore(_ => { });
            string pid = NewPid();
            var art = store.Save(pid, new byte[] { 5 }, "image/jpeg", "fresh");
            store.RunRetentionSweep();
            Assert.NotNull(store.GetBytes(pid, art.ArtifactID));
        }

        [Fact]
        public void ArtifactLifecycle_HashesValidatesAndPersistsProvenance()
        {
            string root = Path.Combine(Path.GetTempPath(), "omnipotent-artifact-tests", Guid.NewGuid().ToString("N"));
            try
            {
                var store = new ProjectArtifactStore(_ => { }, root);
                var art = store.Save("p", new byte[] { 1, 2, 3 }, "application/octet-stream", "result",
                    sourceWakeID: "wake-1", agentID: "agent-a", toolCallID: "call-9");

                Assert.Equal(3, art.SizeBytes);
                Assert.Equal(64, art.Sha256.Length);
                Assert.Equal(ProjectArtifactStore.ArtifactLifecycleState.Captured, art.State);
                var validated = store.Validate("p", art.ArtifactID, true, "checked");
                Assert.Equal(ProjectArtifactStore.ArtifactLifecycleState.Validated, validated!.State);
                Assert.Equal("wake-1", new ProjectArtifactStore(_ => { }, root).GetRecord("p", art.ArtifactID)!.WakeID);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }

    [Collection("ProjectsSerial")]
    public class ProjectWorkToolsTests
    {
        private static (ProjectCommanderTools tools, string pid) NewTools()
        {
            var store = new ProjectStore(_ => { });
            var log = new ProjectEventLogStore(_ => { });
            var digests = new ProjectDigestStore(_ => { });
            var subAgents = new ProjectSubAgentManager(store, log);
            var gates = new ProjectGateManager(log, _ => { });
            gates.GateOpened += gate => gates.ResolveGate(
                gate.ProjectID,
                gate.GateID,
                new GateResolution(GateDecision.Approve, "Approved by the work-tool test fixture.", "test"));
            var fetcher = new OpenRouterCostFetcher(() => Task.FromResult<string?>(null), _ => { });
            var budget = new ProjectBudgetLedger(store, log, fetcher, _ => { });
            var vault = new ProjectVault(_ => { });
            var p = store.CreateProject("t", "goal", 100, 100, 10, 5);
            var tools = new ProjectCommanderTools(p, log, digests, subAgents, gates, budget, vault, store, "commander", "wake1");
            return (tools, p.ProjectID);
        }

        [Fact]
        public async Task WriteReadList_RoundTripOnProjectVolume()
        {
            var (tools, _) = NewTools();
            var w = await tools.DispatchAsync("write_file", JsonConvert.SerializeObject(new { path = "notes/plan.txt", content = "step one" }), CancellationToken.None);
            Assert.Contains("Wrote", w.ResultText);
            var r = await tools.DispatchAsync("read_file", JsonConvert.SerializeObject(new { path = "notes/plan.txt" }), CancellationToken.None);
            Assert.Equal("step one", r.ResultText);
            var l = await tools.DispatchAsync("list_files", JsonConvert.SerializeObject(new { path = "notes" }), CancellationToken.None);
            Assert.Contains("plan.txt", l.ResultText);
        }

        [Fact]
        public async Task PathTraversal_IsBlocked()
        {
            var (tools, _) = NewTools();
            var r = await tools.DispatchAsync("read_file", JsonConvert.SerializeObject(new { path = "..\\..\\projects.json" }), CancellationToken.None);
            Assert.Contains("escapes the project volume", r.ResultText);
        }

        [Fact]
        public async Task RunScript_ReturnsOutputAndValue()
        {
            var (tools, _) = NewTools();
            var r = await tools.DispatchAsync("run_script",
                JsonConvert.SerializeObject(new { code = "Output(2 + 2); \"done\"" }), CancellationToken.None);
            Assert.Contains("4", r.ResultText);
            Assert.Contains("done", r.ResultText);
        }

        [Fact]
        public async Task RunScript_CompileError_IsReportedNotThrown()
        {
            var (tools, _) = NewTools();
            var r = await tools.DispatchAsync("run_script",
                JsonConvert.SerializeObject(new { code = "this is not C#" }), CancellationToken.None);
            Assert.Contains("compile error", r.ResultText, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task RunScript_CanUseVolumeFiles()
        {
            var (tools, _) = NewTools();
            var r = await tools.DispatchAsync("run_script",
                JsonConvert.SerializeObject(new { code = "WriteFile(\"from-script.txt\", \"hi\"); ReadFile(\"from-script.txt\")" }), CancellationToken.None);
            Assert.Contains("hi", r.ResultText);
        }

        [Fact]
        public async Task ExecuteCSharpAlias_ChainsLocalsLikeKliveAgent()
        {
            var (tools, _) = NewTools();
            var first = await tools.DispatchAsync("execute_csharp",
                JsonConvert.SerializeObject(new { code = "var persisted = 40; Output(persisted); persisted + 2" }), CancellationToken.None);
            Assert.Contains("40", first.ResultText);
            Assert.Contains("42", first.ResultText);

            var second = await tools.DispatchAsync("run_script",
                JsonConvert.SerializeObject(new { code = "persisted * 2" }), CancellationToken.None);
            Assert.Contains("80", second.ResultText);
        }

        [Fact]
        public void WorkScripts_ExposeTheFullKliveAgentGlobalsSurface()
        {
            var globals = typeof(ProjectCommanderTools.WorkScriptGlobals);
            Assert.True(typeof(Omnipotent.Services.KliveAgent.ScriptGlobals).IsAssignableFrom(globals));
            foreach (var method in new[] { "ListServices", "GetService", "GetTypeSchema", "GetObjectMembers", "CallObjectMethod", "ExecuteServiceMethod", "ListAgentCapabilities", "GetGlobalPath", "SearchCode", "RunPowerShell", "SaveMemory", "ScheduleTask" })
                Assert.NotNull(globals.GetMethod(method));
            Assert.NotNull(globals.GetMethod("ReadProjectFile"));
            Assert.NotNull(globals.GetMethod("ReadCodeFile"));
        }

        [Fact]
        public async Task HookCrudTools_WorkAgainstTheStore()
        {
            var (tools, pid) = NewTools();
            var log = new ProjectEventLogStore(_ => { });
            tools.HookStore = new StimulusHookStore(log);
            bool rearmed = false;
            tools.RearmAdapters = () => rearmed = true;

            var created = await tools.DispatchAsync("create_stimulus_hook",
                JsonConvert.SerializeObject(new { sourceKind = "timer", sourceSpec = new { intervalSeconds = 60 }, criterion = "always" }), CancellationToken.None);
            Assert.Contains("created", created.ResultText);
            Assert.True(rearmed);

            var list = await tools.DispatchAsync("list_stimulus_hooks", "{}", CancellationToken.None);
            Assert.Contains("timer", list.ResultText);

            string hookID = tools.HookStore.List(pid)[0].HookID;
            var deleted = await tools.DispatchAsync("delete_stimulus_hook",
                JsonConvert.SerializeObject(new { hookID }), CancellationToken.None);
            Assert.Contains("deleted", deleted.ResultText, StringComparison.OrdinalIgnoreCase);
        }
    }

    public class ProjectSettingsTests
    {
        private static string NewPid() => "test_" + Guid.NewGuid().ToString("N");

        [Fact]
        public void NewProject_GetsDefaults_NotOmniSettings()
        {
            var store = new ProjectSettingsStore();
            var s = store.Get(NewPid());
            Assert.Equal(ProjectSettings.Defaults.CommanderModel, s.CommanderModel);
            Assert.Equal(ProjectSettings.Defaults.TierTextModel, s.TierTextModel);
            Assert.False(s.ContainersEnabled); // text-only until Klives opts in
            Assert.True(s.VisionEnabled);
        }

        [Fact]
        public void PerProjectOverride_IsIsolated()
        {
            var store = new ProjectSettingsStore();
            string p1 = NewPid(), p2 = NewPid();
            var s1 = store.Get(p1);
            s1.CommanderModel = "custom/smart";
            s1.ContainersEnabled = true;
            store.Save(s1);
            // p2 keeps defaults — each project owns its own settings.
            var s2 = store.Get(p2);
            Assert.Equal(ProjectSettings.Defaults.CommanderModel, s2.CommanderModel);
            Assert.False(s2.ContainersEnabled);
            // p1's override persists across a fresh store instance.
            Assert.Equal("custom/smart", new ProjectSettingsStore().Get(p1).CommanderModel);
        }

        [Fact]
        public void TrySet_AppliesKnownKeys_RejectsUnknown()
        {
            var s = new ProjectSettings { ProjectID = NewPid() };
            Assert.True(s.TrySet("commanderModel", "x/y"));
            Assert.Equal("x/y", s.CommanderModel);
            Assert.True(s.TrySet("containersEnabled", "true"));
            Assert.True(s.ContainersEnabled);
            Assert.True(s.TrySet("visionEnabled", "false"));
            Assert.False(s.VisionEnabled);
            Assert.False(s.TrySet("nonsenseKey", "z"));
        }

        [Fact]
        public void MemoryTools_AreOfferedToEveryTier()
        {
            // Projects is part of KliveAgent — memory transfers, so all tiers can recall/save.
            var router = new ProjectTierRouter(new ProjectSettingsStore());
            foreach (var tier in Enum.GetValues<ProjectAgentTier>())
            {
                Assert.True(router.IsToolAllowed(tier, "recall_memories"));
                Assert.True(router.IsToolAllowed(tier, "save_memory"));
            }
            var names = ProjectCommanderAgent.BuildCoreToolDefinitions().Select(t => t.function.name).ToList();
            Assert.Contains("recall_memories", names);
            Assert.Contains("save_memory", names);
            Assert.Contains("execute_csharp", names);
            Assert.Contains("grep", names);
            Assert.Contains("get_global_path", names);
            Assert.Contains("save_shortcut", names);
        }
    }

    public class SubAgentToolGatingTests
    {
        [Fact]
        public void TextTierAgent_GetsWorkToolsButNoComputerOrCompletion()
        {
            var router = new ProjectTierRouter(new ProjectSettingsStore());
            var offered = ProjectCommanderAgent.BuildCoreToolDefinitions()
                .Where(t => router.IsToolAllowed(ProjectAgentTier.Text, t.function.name))
                .Select(t => t.function.name)
                .ToList();
            Assert.Contains("run_script", offered);
            Assert.Contains("execute_csharp", offered);
            Assert.Contains("grep", offered);
            Assert.Contains("http_request", offered);
            Assert.Contains("send_agent_message", offered);
            Assert.DoesNotContain("complete_project", offered); // strategy stays with the Commander
            Assert.DoesNotContain(ProjectCommanderAgent.BuildComputerToolDefinitions(),
                t => router.IsToolAllowed(ProjectAgentTier.Text, t.function.name));
        }

        [Fact]
        public void VideoTierAgent_GetsComputerTools()
        {
            var router = new ProjectTierRouter(new ProjectSettingsStore());
            var computer = ProjectCommanderAgent.BuildComputerToolDefinitions()
                .Where(t => router.IsToolAllowed(ProjectAgentTier.TextImageVideo, t.function.name))
                .ToList();
            Assert.NotEmpty(computer);
            Assert.Contains(computer, t => t.function.name == "computer_click");
        }
    }

    public class StimulusDestinationRoutingTests
    {
        [Fact]
        public async Task Enqueue_StampsDestinationOnTheEnvelope()
        {
            var q = new StimulusQueue(_ => { });
            var got = new TaskCompletionSource<StimulusEnvelope>();
            q.OnDeliver = e => { got.TrySetResult(e); return Task.CompletedTask; };
            string pid = "test_" + Guid.NewGuid().ToString("N");
            await q.EnqueueAsync(new StimulusEnvelope { ProjectID = pid, HookID = "h", SourceKind = "inter-agent", Payload = "task for you" }, "agent42");
            var env = await got.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("agent42", env.DestinationAgentID);
        }
    }

    public class ScreenDiffGateTests
    {
        [Fact]
        public void IdenticalFrames_ProduceZeroChange()
        {
            var frame = MakeFrame(320, 200, 100);
            var g1 = ScreenDiffAdapter.DownscaleToGrid(frame, 320, 200);
            var g2 = ScreenDiffAdapter.DownscaleToGrid(frame, 320, 200);
            Assert.Equal(0.0, ScreenDiffAdapter.ChangedFraction(g1, g2));
        }

        [Fact]
        public void HalfChangedFrame_TripsThreshold()
        {
            var a = MakeFrame(320, 200, 30);
            var b = MakeFrame(320, 200, 30);
            // Repaint the top half much brighter.
            for (int y = 0; y < 100; y++)
                for (int x = 0; x < 320; x++)
                {
                    int i = (y * 320 + x) * 4;
                    b[i] = b[i + 1] = b[i + 2] = 220;
                }
            var g1 = ScreenDiffAdapter.DownscaleToGrid(a, 320, 200);
            var g2 = ScreenDiffAdapter.DownscaleToGrid(b, 320, 200);
            double changed = ScreenDiffAdapter.ChangedFraction(g1, g2);
            Assert.InRange(changed, 0.4, 0.6); // ~half the grid moved
        }

        [Fact]
        public void TinyNoise_StaysUnderDeadband()
        {
            var a = MakeFrame(320, 200, 100);
            var b = MakeFrame(320, 200, 103); // +3 luma everywhere — under the dead-band
            var g1 = ScreenDiffAdapter.DownscaleToGrid(a, 320, 200);
            var g2 = ScreenDiffAdapter.DownscaleToGrid(b, 320, 200);
            Assert.Equal(0.0, ScreenDiffAdapter.ChangedFraction(g1, g2));
        }

        private static byte[] MakeFrame(int w, int h, byte value)
        {
            var f = new byte[w * h * 4];
            for (int i = 0; i < f.Length; i += 4) { f[i] = f[i + 1] = f[i + 2] = value; f[i + 3] = 255; }
            return f;
        }
    }
}
