using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>
    /// Runs the game in Roblox's real engine: compiles the engine-test build (its server scripts become startable
    /// modules), saves it as a new unpublished version of the game's experience, and executes the kit's
    /// TestRunner there through Open Cloud Luau execution. The runner checks the kit on its own first (a control:
    /// a failure there is the studio's bug, never the game's), loads every module, boots the whole server for a
    /// few seconds catching every runtime error, then runs the world checks and the game's own specs.
    /// </summary>
    public static class EngineTests
    {
        public const string Script =
            "local runner = require(game:GetService('ServerScriptService'):WaitForChild('OmniKitServer'):WaitForChild('TestRunner'))\nreturn runner.RunAll({ bootSeconds = 8 })";

        public static async Task<EngineRun> RunAsync(StudioContext ctx, string gameId, ExperienceSlot slot, CancellationToken ct)
        {
            var compiled = ctx.Compile(gameId, engineTest: true);
            int revision = ctx.Game(gameId)?.Build.Revision ?? 0;
            if (!compiled.Ok)
                return new EngineRun { Utc = ctx.Clock(), Revision = revision, Ran = false, Summary = "the engine-test build did not compile: " + string.Join("; ", compiled.Errors.Take(3)) };
            var saved = await ctx.Cloud.PublishPlaceAsync(slot.UniverseId, slot.PlaceId, compiled.PlaceXml, isXml: true, publish: false, ct);
            var task = await ctx.Cloud.RunLuauAsync(slot.UniverseId, slot.PlaceId, saved.VersionNumber, Script, TimeSpan.FromSeconds(240), ct);
            if (task.State != LuauTaskState.Complete)
            {
                string why = task.ErrorMessage ?? task.State.ToString();
                var run = new EngineRun { Utc = ctx.Clock(), Revision = revision, Ran = true, Ok = false, Version = saved.VersionNumber, Summary = "the test run itself failed: " + why };
                foreach (var log in task.Logs.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(8))
                    run.Errors.Add(Locate(new EngineError { Message = Clip(log, 600) }, compiled));
                run.Results.Add(new EngineCase { Name = "the test run completes", Ok = false, Detail = why, Phase = "run", Owner = "kit" });
                return run;
            }
            var parsed = Parse(task.OutputJson, saved.VersionNumber, compiled);
            parsed.Utc = ctx.Clock();
            parsed.Revision = revision;
            return parsed;
        }

        internal static EngineRun Parse(string? outputJson, long? version, CompileResult? compiled = null)
        {
            var run = new EngineRun { Ran = true, Version = version };
            if (string.IsNullOrWhiteSpace(outputJson))
            {
                run.Summary = "the test runner returned nothing";
                run.Results.Add(new EngineCase { Name = "the test runner answers", Ok = false, Detail = run.Summary, Phase = "run", Owner = "kit" });
                return run;
            }
            JToken root;
            try { root = JToken.Parse(outputJson); }
            catch (Newtonsoft.Json.JsonException) { root = new JValue(outputJson); }
            JObject? summary = root switch
            {
                JArray arr => arr.FirstOrDefault() as JObject,
                JObject obj when obj["results"] is JArray inner && obj["passed"] == null => inner.FirstOrDefault() as JObject,
                JObject obj => obj,
                _ => null,
            };
            if (summary == null)
            {
                run.Summary = "unexpected test output: " + Clip(outputJson, 300);
                run.Results.Add(new EngineCase { Name = "the test runner answers", Ok = false, Detail = run.Summary, Phase = "run", Owner = "kit" });
                return run;
            }
            foreach (var r in (summary["results"] as JArray ?? new JArray()).OfType<JObject>())
            {
                run.Results.Add(new EngineCase
                {
                    Name = (string?)r["name"] ?? "?",
                    Ok = (bool?)r["ok"] ?? false,
                    Detail = r["detail"] is JToken d && d.Type != JTokenType.Null ? Clip(d.ToString(), 900) : null,
                    Phase = (string?)r["phase"] ?? "",
                    Owner = (string?)r["owner"] ?? "game",
                    Seconds = (double?)r["seconds"] ?? 0,
                    Skipped = (bool?)r["skipped"] ?? false,
                    Warnings = (r["warnings"] as JArray)?.Select(w => w.ToString()).Take(5).ToList() ?? new List<string>(),
                });
            }
            foreach (var e in (summary["errors"] as JArray ?? new JArray()).OfType<JObject>())
                run.Errors.Add(Locate(new EngineError
                {
                    Message = Clip((string?)e["message"] ?? "", 700),
                    Source = (string?)e["source"],
                    Trace = (string?)e["trace"] is { } t ? Clip(t, 900) : null,
                    Phase = (string?)e["phase"] ?? "",
                }, compiled));
            foreach (var w in (summary["warnings"] as JArray ?? new JArray()).OfType<JObject>().Take(20))
                run.Warnings.Add(Clip((string?)w["message"] ?? "", 400));
            run.Passed = (int?)summary["passed"] ?? run.Results.Count(r => r.Ok);
            run.Failed = (int?)summary["failed"] ?? run.Results.Count(r => !r.Ok);
            run.KitFailed = run.Results.Any(r => !r.Ok && r.Phase == "kit");
            run.Ok = run.Results.Count > 0 && run.Results.All(r => r.Ok) && run.Errors.Count == 0;
            run.Summary = run.Ok
                ? $"{run.Passed} checks passed" + (run.Results.Any(r => r.Skipped) ? $" ({run.Results.Count(r => r.Skipped)} did not apply)" : "") + "; the server booted with no errors."
                : $"{run.Failed} of {run.Results.Count} checks failed" + (run.Errors.Count > 0 ? $", {run.Errors.Count} runtime error(s)" : "") + (run.KitFailed ? " — including the kit's own control checks (a studio problem, not the game's)" : "") + ".";
            return run;
        }

        private static readonly Regex ScriptLine = new(@"(?<name>[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)+):(?<line>\d+):", RegexOptions.Compiled);

        /// <summary>Finds the game file and line an engine error points at ("ServerScriptService.Forest:150: …").</summary>
        private static EngineError Locate(EngineError error, CompileResult? compiled)
        {
            if (compiled == null) return error;
            foreach (Match m in ScriptLine.Matches(error.Message + "\n" + (error.Trace ?? "")))
            {
                string? file = compiled.FileFor(m.Groups["name"].Value);
                if (file != null)
                {
                    error.File = file;
                    error.Line = int.Parse(m.Groups["line"].Value);
                    return error;
                }
            }
            if (error.Source != null && compiled.FileFor(error.Source) is { } owner) error.File = owner;
            return error;
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

        /// <summary>A run as text for an agent: every failure with its detail, every runtime error with its file and line.</summary>
        public static string Describe(EngineRun run, int maxPassed = 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Engine tests on r{run.Revision}" + (run.Version is long v ? $" (place version {v})" : "") + $": {(run.Ok ? "PASSED" : "FAILED")} — {run.Summary}");
            foreach (var r in run.Results.Where(r => !r.Ok))
                sb.AppendLine($"- FAIL [{r.Phase}/{r.Owner}] {r.Name}: {r.Detail}");
            foreach (var e in run.Errors)
                sb.AppendLine($"- ERROR [{e.Phase}] " + (e.File != null ? $"{e.File}{(e.Line is int l ? ":" + l : "")}: " : e.Source != null ? e.Source + ": " : "") + e.Message);
            foreach (var r in run.Results.Where(r => r.Ok && r.Warnings.Count > 0))
                foreach (var w in r.Warnings) sb.AppendLine($"- note [{r.Name}]: {w}");
            foreach (var w in run.Warnings.Take(8)) sb.AppendLine("- warning: " + w);
            if (maxPassed > 0)
                foreach (var r in run.Results.Where(r => r.Ok).Take(maxPassed)) sb.AppendLine($"- ok {r.Name}" + (r.Skipped ? $" (did not apply: {r.Detail})" : ""));
            if (run.KitFailed)
                sb.AppendLine("The kit's own control checks failed before any game code was loaded: that is a bug in the studio's Game Kit, not in this game. It has been reported; work on everything else.");
            return sb.ToString();
        }
    }
}
