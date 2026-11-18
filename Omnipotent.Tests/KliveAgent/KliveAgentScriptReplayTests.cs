using Microsoft.CodeAnalysis.Scripting;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAgent;
using Xunit.Abstractions;

namespace Omnipotent.Tests.KliveAgent
{
    /// <summary>
    /// Replays scripts captured from live conversations through today's engine, compile only: nothing a
    /// script would do is executed. It answers "of the scripts that failed to compile live, how many
    /// compile now, and how many of those only because the engine repaired them".
    ///
    /// The capture is real conversation data (scripts carry whatever the task handled), so it never
    /// enters the repository: point KLIVEAGENT_REPLAY_DIR at a folder of saved
    /// GET /kliveagent/conversations/get responses. Without it the test does nothing.
    ///
    /// It is an approximation in one direction: each run is replayed as one chain, and a script is
    /// chained whenever it compiles, whereas live a script that then threw kept only part of its
    /// variables. So a later script can compile here that had no variable to use live.
    /// </summary>
    public class KliveAgentScriptReplayTests
    {
        private readonly ITestOutputHelper output;

        public KliveAgentScriptReplayTests(ITestOutputHelper output) => this.output = output;

        private sealed record Recorded(string Code, bool CompiledLive);

        [Fact]
        public void Replay_CapturedScripts_CompileOnly()
        {
            string? directory = Environment.GetEnvironmentVariable("KLIVEAGENT_REPLAY_DIR");
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                output.WriteLine("KLIVEAGENT_REPLAY_DIR is not set: nothing to replay.");
                return;
            }

            var runs = new List<List<Recorded>>();
            foreach (var file in Directory.EnumerateFiles(directory, "GET_kliveagent_conversations_get_id_*"))
            {
                JObject conversation;
                try { conversation = JObject.Parse(File.ReadAllText(file)); }
                catch { continue; }
                foreach (var message in conversation["messages"] as JArray ?? new JArray())
                {
                    var scripts = (message["scriptResults"] as JArray ?? new JArray())
                        .OfType<JObject>()
                        .Select(s => new Recorded((string?)s["code"] ?? "", !IsCompileFailure((string?)s["errorMessage"], (bool?)s["success"] ?? false)))
                        .Where(s => s.Code.Length > 0)
                        .ToList();
                    if (scripts.Count > 0) runs.Add(scripts);
                }
            }

            var plain = Replay(runs, autoRepair: false, out _);
            var repaired = Replay(runs, autoRepair: true, out var repairKinds);
            int total = runs.Sum(r => r.Count);
            int failedLive = runs.Sum(r => r.Count(s => !s.CompiledLive));

            var summary = new JObject
            {
                ["scripts"] = total,
                ["runs"] = runs.Count,
                ["compileFailuresRecordedLive"] = failedLive,
                ["compileFailuresNow_repairOff"] = plain.Failed,
                ["compileFailuresNow_repairOn"] = repaired.Failed,
                ["liveFailuresThatCompileNow_repairOff"] = plain.FixedLiveFailures,
                ["liveFailuresThatCompileNow_repairOn"] = repaired.FixedLiveFailures,
                ["scriptsRepaired"] = repaired.Repaired,
                ["compiledLiveButFailNow_repairOn"] = repaired.NewFailures,
                ["repairKinds"] = JObject.FromObject(repairKinds.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value)),
                ["remainingErrorIds_repairOn"] = JObject.FromObject(repaired.ErrorIds.OrderByDescending(kv => kv.Value).Take(15).ToDictionary(kv => kv.Key, kv => kv.Value)),
            };
            output.WriteLine(summary.ToString());
            File.WriteAllText(Path.Combine(directory, "replay-summary.json"), summary.ToString());
            File.WriteAllLines(Path.Combine(directory, "replay-remaining.txt"), repaired.Remaining);

            // The engine must never turn a script that compiled into one that does not.
            Assert.True(repaired.Failed <= plain.Failed, "repair made more scripts fail than no repair");
        }

        /// <summary>A script that is not known to have compiled live: a compile error, or a compile that ran
        /// out its clock before saying either way.</summary>
        private static bool IsCompileFailure(string? error, bool success) =>
            !success && !string.IsNullOrEmpty(error)
            && (error.StartsWith("Compilation failed", StringComparison.Ordinal)
                || error.StartsWith("Compilation errors", StringComparison.Ordinal)
                || error.StartsWith("Compiling the script took longer", StringComparison.Ordinal));

        private sealed class Tally
        {
            public int Failed;
            public int FixedLiveFailures;
            public int NewFailures;
            public int Repaired;
            public Dictionary<string, int> ErrorIds = new(StringComparer.Ordinal);
            public List<string> Remaining = new();
        }

        private static Tally Replay(List<List<Recorded>> runs, bool autoRepair, out Dictionary<string, int> repairKinds)
        {
            var tally = new Tally();
            repairKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var run in runs)
            {
                Script? previous = null;
                foreach (var script in run)
                {
                    var (compiled, repairs, errors) = KliveAgentScriptEngine.ScriptExecutionSession.CompileOnly(previous, script.Code, autoRepair);
                    if (compiled == null)
                    {
                        tally.Failed++;
                        if (script.CompiledLive) tally.NewFailures++;
                        string firstError = errors.FirstOrDefault() ?? "?";
                        string first = firstError.Split(' ')[0];
                        tally.ErrorIds[first] = tally.ErrorIds.GetValueOrDefault(first) + 1;
                        // Compiler messages only (type and member names), for reading what is left to fix.
                        tally.Remaining.Add((script.CompiledLive ? "NEW " : "") + string.Join(" || ", errors.Take(3)));
                        continue;
                    }
                    previous = compiled;
                    if (!script.CompiledLive) tally.FixedLiveFailures++;
                    if (repairs.Count == 0) continue;
                    tally.Repaired++;
                    foreach (var kind in repairs.Select(KliveAgentStats.RepairKindOf).Distinct())
                        repairKinds[kind] = repairKinds.GetValueOrDefault(kind) + 1;
                }
            }
            return tally;
        }
    }
}
