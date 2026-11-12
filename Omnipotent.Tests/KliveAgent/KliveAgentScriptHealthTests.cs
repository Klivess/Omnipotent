using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.KliveAgent.Models;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Projects.Stimulus;

namespace Omnipotent.Tests.KliveAgent
{
    /// <summary>The measurements and guidance around scripts: what is recorded, what the route serves,
    /// what the model is told when scripts keep failing, and the shared script reference set.</summary>
    public class KliveAgentScriptHealthTests
    {
        private static KliveAgentStats NewStats() =>
            new(Path.Combine(Path.GetTempPath(), "kliveagent-stats-" + Guid.NewGuid().ToString("N") + ".json"));

        private static AgentScriptResult Script(bool ok, string? kind = null, long compileMs = 20, bool cold = false,
            long? runMs = 5, string? error = null, params string[] repairs) => new()
            {
                Code = "x", Success = ok, FailureKind = ok ? null : kind, CompileMs = compileMs, ColdStart = cold, RunMs = runMs,
                ErrorMessage = error, Repairs = repairs.Length > 0 ? repairs.ToList() : null,
            };

        [Fact]
        public void ScriptHealth_CountsOutcomesRepairsAndCompileTimes()
        {
            var stats = NewStats();
            stats.RecordScriptOutcome(Script(true, compileMs: 4600, cold: true));
            stats.RecordScriptOutcome(Script(true, compileMs: 120));
            stats.RecordScriptOutcome(Script(true, compileMs: 90, repairs: "line 1: a top-level `using var` is not valid in a script, so `ms` was declared without `using`"));
            stats.RecordScriptOutcome(Script(false, "compile", compileMs: 70, runMs: null,
                error: "Compilation failed — 1 error(s); nothing ran.\n[CS1061] line 4:19 — 'Foo' does not contain a definition for 'Bar'\n     4 | var secretToken = foo.Bar;"), 1);
            stats.RecordScriptOutcome(Script(false, "runtime", error: "Runtime error: NullReferenceException: Object reference not set."), 2);
            stats.RecordScriptOutcome(Script(false, "timeout", runMs: 30000, error: "Script execution timed out after 30s."), 3);
            stats.RecordScriptOutcome(Script(false, "stopped", error: "Script was stopped before it finished."), 0);

            var view = JObject.FromObject(stats.BuildScriptHealth(7));
            var totals = (JObject)view["totals"]!;
            Assert.Equal(7, (int)totals["total"]!);
            Assert.Equal(3, (int)totals["failed"]!);
            Assert.Equal(1, (int)totals["compile"]!);
            Assert.Equal(1, (int)totals["runtime"]!);
            Assert.Equal(1, (int)totals["timeout"]!);
            Assert.Equal(1, (int)totals["stopped"]!);
            Assert.Equal(1, (int)totals["repaired"]!);
            // Three of six counted scripts failed: the stopped one is nobody's failure.
            Assert.Equal(50.0, (double)totals["failurePct"]!);
            Assert.Equal(1, (int)totals["coldCompiles"]!);
            Assert.Equal(5000, (int)totals["coldCompileP50Ms"]!);
            Assert.Equal(4600, (int)totals["coldCompileMaxMs"]!);
            // Six warm compiles (20, 20, 20, 70, 90, 120 ms): the median falls in the "under 50 ms" bucket.
            Assert.Equal(50, (int)totals["warmCompileP50Ms"]!);
            Assert.Equal(250, (int)totals["warmCompileP95Ms"]!);
            Assert.Equal(3, (int)totals["longestFailureStreak"]!);

            Assert.Equal("top-level using declaration", (string)view["repairKinds"]![0]!["kind"]!);
            Assert.Single((JArray)view["days"]!);
            Assert.Single((JArray)view["weeks"]!);

            var recent = (JArray)view["recentFailures"]!;
            Assert.Equal(3, recent.Count);
            // The compiler's sentence is kept; the script's own lines are not.
            var compileExample = recent.First(f => (string)f["kind"]! == "compile");
            Assert.Equal("CS1061", (string)compileExample["code"]!);
            Assert.Contains("does not contain a definition for 'Bar'", (string)compileExample["summary"]!);
            Assert.DoesNotContain("secretToken", view.ToString());
        }

        [Fact]
        public async Task ScriptHealth_SurvivesARestart()
        {
            string path = Path.Combine(Path.GetTempPath(), "kliveagent-stats-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var first = new KliveAgentStats(path);
                first.RecordScriptOutcome(Script(false, "compile", runMs: null, error: "[CS0103] line 1:1 — The name 'x' does not exist"), 4);
                first.RecordScriptOutcome(Script(true, compileMs: 300, cold: true, repairs: "added `using System.Xml.Linq;` for `XElement`"));
                for (int i = 0; i < 40 && !File.Exists(path); i++) await Task.Delay(100);
                await Task.Delay(700);

                var second = new KliveAgentStats(path);
                await second.InitializeAsync();
                var totals = (JObject)JObject.FromObject(second.BuildScriptHealth(7))["totals"]!;
                Assert.Equal(2, (int)totals["total"]!);
                Assert.Equal(1, (int)totals["compile"]!);
                Assert.Equal(1, (int)totals["repaired"]!);
                Assert.Equal(4, (int)totals["longestFailureStreak"]!);
                Assert.Equal(500, (int)totals["coldCompileP50Ms"]!);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Theory]
        [InlineData("line 2: a top-level `using var` is not valid in a script, so `ms` was declared without `using`", "top-level using declaration")]
        [InlineData("line 2: `await (…)` is read as a call to a method named await in a script, so it ran as `await Aw(…)`", "await of a cast, parenthesis or object")]
        [InlineData("added `using System.Web;` for `HttpUtility`", "missing namespace")]
        [InlineData("line 2: `stash` did not exist, so it was declared here", "assignment to an undeclared name")]
        [InlineData("line 2: `GetAsync()` returns a Task, so `await` was added", "missing await")]
        [InlineData("line 3: `Store` is internal to Omnipotent and cannot be named in a script, so the cast became `Dyn(…)`", "internal type or non-public member")]
        [InlineData("line 2: `o` is typed `object`, so its member was read as `Dyn(o).Length`", "member of an object-typed value")]
        public void RepairNotes_AreCountedByRule(string note, string expected)
        {
            Assert.Equal(expected, KliveAgentStats.RepairKindOf(note));
        }

        [Fact]
        public void FailureStreak_IsAnsweredAtThreeAndAtSix_ThenEveryFourMore()
        {
            Assert.Null(KliveAgentBrain.FailureStreakGuidance(0, 0));
            Assert.Null(KliveAgentBrain.FailureStreakGuidance(2, 0));
            Assert.Contains("Stop guessing names", KliveAgentBrain.FailureStreakGuidance(3, 0));
            Assert.Null(KliveAgentBrain.FailureStreakGuidance(4, 3));
            Assert.Null(KliveAgentBrain.FailureStreakGuidance(5, 3));
            Assert.Contains("say in one sentence what you will do differently", KliveAgentBrain.FailureStreakGuidance(6, 3));
            Assert.Null(KliveAgentBrain.FailureStreakGuidance(8, 6));
            Assert.Contains("[10 scripts in a row failed]", KliveAgentBrain.FailureStreakGuidance(10, 6));
            // Several scripts can fail in one step: the first notice may already be the second kind.
            Assert.Contains("This approach is not working", KliveAgentBrain.FailureStreakGuidance(7, 0));
        }

        [Theory]
        [InlineData("{\"code\":\"Log(1);\",\"timeoutSeconds\":120}", 120)]
        [InlineData("{\"code\":\"Log(1);\",\"timeoutSeconds\":\"90\"}", 90)]
        [InlineData("{\"code\":\"Log(1);\"}", null)]
        [InlineData("{\"code\":\"Log(1);\",\"timeoutSeconds\":0}", null)]
        [InlineData("Log(1);", null)]
        public void ScriptTimeout_IsReadFromTheToolCall(string arguments, int? expected)
        {
            var call = new HFWrapper.HFToolCall { function = new HFWrapper.HFFunctionCall { name = "execute_csharp", arguments = arguments } };
            Assert.Equal(expected, KliveAgentBrain.ExtractScriptTimeout(call));
        }

        [Fact]
        public void ExecuteCSharp_OffersATimeout_AndThePromptStatesTheScriptRules()
        {
            var tool = KliveAgentBrain.BuildToolDefinitions().First(t => t.function.name == "execute_csharp");
            string schema = Newtonsoft.Json.JsonConvert.SerializeObject(tool.function.parameters);
            Assert.Contains("timeoutSeconds", schema);

            string prompt = KliveAgentPromptPolicy.Build(nativeTools: true, computerUse: false);
            Assert.Contains("Dyn(x).Member", prompt);
            Assert.Contains("await Aw((Task<T>)x)", prompt);
            Assert.Contains("`var x = ...;` not `using var x = ...;`", prompt);
        }

        [Fact]
        public void ModelStateClock_SplitsACallByState_AndByWhatElseWasInFlight()
        {
            var performance = new KliveAgentPerformance();
            var inFlight = new ModelStateClock.InFlightCounter();
            using (var first = new ModelStateClock(performance, inFlight))
            {
                first.Observe(KliveLLMActivityKind.AwaitingProvider);
                Thread.Sleep(30);
                first.Observe(KliveLLMActivityKind.AwaitingProvider);   // a repeat pulse is the same state
                first.Observe(KliveLLMActivityKind.Reasoning);
                using (var second = new ModelStateClock(performance, inFlight))
                    second.Observe(KliveLLMActivityKind.Streaming);
                first.Observe(KliveLLMActivityKind.Streaming);
            }
            Assert.Equal(0, inFlight.Current);

            var stages = performance.Snapshot(1, 0, 0).Stages;
            Assert.Equal(1, stages["model.awaiting"].Count);
            Assert.True(stages["model.awaiting"].TotalMs >= 20);
            Assert.Equal(1, stages["model.reasoning"].Count);
            Assert.Equal(2, stages["model.streaming"].Count);
            Assert.Equal(1, stages["model.alone"].Count);
            Assert.Equal(1, stages["model.with1other"].Count);
        }

        // ── the shared reference set ──

        [Fact]
        public void References_AreTheWholeAppNotWhateverIsLoaded_AndLeaveOutTooling()
        {
            var paths = OmniScriptReferences.CandidatePaths().Select(Path.GetFileNameWithoutExtension).ToList();
            Assert.Contains("Omnipotent", paths);
            Assert.Contains("System.Linq", paths);
            Assert.Contains("System.Net.Http", paths);
            Assert.Contains("Newtonsoft.Json", paths);
            Assert.DoesNotContain(paths, name => name!.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.True(OmniScriptReferences.Count > 100, $"only {OmniScriptReferences.Count} references");
            // What holding the set costs: metadata only, never whole images (Omnipotent.dll alone is 126 MB).
            Assert.InRange(OmniScriptReferences.MetadataBytes, 1_000_000, 200_000_000);
        }

        [Fact]
        public void DefaultImports_DeclareNoTypeNameTwice()
        {
            var imports = KliveAgentScriptEngine.DefaultImports.ToHashSet(StringComparer.Ordinal);
            var index = KliveAgentTypeIndex.Shared;
            var clashes = new List<string>();
            foreach (var name in new[] { "Timer", "Path", "File", "Task", "Regex", "Group", "Match", "Process", "HttpMethod", "Formatting",
                         "JsonSerializer", "JsonException", "Aes", "Stopwatch", "Lock", "Thread", "Assembly", "Module", "Capture", "Cookie",
                         "Activity", "Debug", "Trace", "Calendar", "Missing", "Binder", "Pointer", "Authorization", "Switch", "JsonConverter" })
            {
                var hits = index.NamespacesOf(name).Where(imports.Contains).ToList();
                if (hits.Count > 1) clashes.Add($"{name}: {string.Join(", ", hits)}");
            }
            Assert.True(clashes.Count == 0, "ambiguous in a script: " + string.Join(" | ", clashes));
        }

        [Fact]
        public void TypeIndex_NamesTheNamespaceABareTypeMeans()
        {
            var index = KliveAgentTypeIndex.Shared;
            Assert.Equal("System.Reflection", index.PreferredNamespaceOf("BindingFlags"));
            Assert.Equal("System.Xml.Linq", index.PreferredNamespaceOf("XElement"));
            Assert.Equal("System.Collections.Concurrent", index.PreferredNamespaceOf("ConcurrentDictionary<string,int>"));
            Assert.Null(index.PreferredNamespaceOf("NoSuchTypeAnywhere"));
            // Two framework namespaces declare Timer: not something to guess.
            Assert.Null(index.PreferredNamespaceOf("Timer"));
            Assert.Contains(index.Similar("StringBuildr"), s => s.Name == "StringBuilder" && s.Namespace == "System.Text");
        }

        [Fact]
        public void StimulusScripts_CompileAgainstTheSharedReferences()
        {
            // The live failure: "error CS0234: The type or namespace name 'Linq' does not exist in the namespace 'System'".
            var errors = StimulusScriptAdapter.CompileErrors(
                "var doubled = new[] { 1, 2 }.Select(x => x * 2).ToList();\n"
                + "var json = System.Text.Json.JsonSerializer.Serialize(doubled);\n"
                + "await Emit(json);");
            Assert.Empty(errors);
            Assert.NotEmpty(StimulusScriptAdapter.CompileErrors("this is not C#"));
        }
    }
}
