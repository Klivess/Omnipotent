using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline.Tools;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>Experience slots: blank experiences the owner created, which the studio fills one game each.</summary>
    public static class Slots
    {
        /// <summary>The game's slot, assigning a free one if it has none. Null when every slot is taken.</summary>
        public static ExperienceSlot? Ensure(StudioContext ctx, string gameId)
        {
            var existing = ctx.Game(gameId)?.Slot;
            if (existing != null) return existing;
            ExperienceSlot? chosen = null;
            ctx.Store.Mutate(s =>
            {
                foreach (var slot in ctx.Settings.Slots)
                {
                    string key = slot.ToString();
                    if (s.Studio.SlotAssignments.TryGetValue(key, out var holder) && holder != gameId && s.Game(holder) is { } h && h.Stage != GameStage.Abandoned)
                        continue;
                    s.Studio.SlotAssignments[key] = gameId;
                    var game = s.Game(gameId)!;
                    game.Slot = new ExperienceSlot { UniverseId = slot.UniverseId, PlaceId = slot.PlaceId };
                    s.MarkStudio();
                    s.Touch(game, ctx.Clock());
                    chosen = game.Slot;
                    return;
                }
            });
            return chosen;
        }

        public static void Release(StudioContext ctx, string gameId) => ctx.Store.Mutate(s =>
        {
            foreach (var key in s.Studio.SlotAssignments.Where(kv => kv.Value == gameId).Select(kv => kv.Key).ToList())
                s.Studio.SlotAssignments.Remove(key);
            s.MarkStudio();
        });

        public static bool AnyFree(StudioContext ctx) => ctx.Store.Read(s => ctx.Settings.Slots.Any(slot =>
            !s.Studio.SlotAssignments.TryGetValue(slot.ToString(), out var holder) || s.Game(holder) is not { } h || h.Stage == GameStage.Abandoned));
    }

    /// <summary>
    /// The Test stage: a clean build (syntax, strict types, scene, geometry, the map report), the game in Roblox's
    /// own engine when a key and an experience slot exist, else in the studio's local engine (a passing run on this
    /// very build is reused), and a playtest review by a vision-capable agent playing it as a child would. A failure
    /// sends the game back to Build with the report in its briefing.
    ///
    /// Two kinds of failure are kept apart from the game's record. A run that learned nothing — Roblox or the
    /// model unreachable — is marked <see cref="TestReport.Infrastructure"/> and retried later. A failure of the
    /// kit's own control checks (run before any game code loads) is the studio's bug: the owner is told, and the
    /// game is judged on its own checks only. The first studio blamed every game for a kit bug until it gave up.
    /// </summary>
    public static class TestStage
    {
        public static async Task<TestReport> RunAsync(StudioContext ctx, string gameId, bool withReview, CancellationToken ct)
        {
            var report = new TestReport { Utc = ctx.Clock() };
            var build = await BuildTools.RunBuildAsync(ctx, gameId, ct);
            var game = ctx.Game(gameId)!;
            bool obby = string.Equals(game.Brief?.Template, "obby", StringComparison.OrdinalIgnoreCase);
            report.BuildRevision = game.Build.Revision;
            report.MapScore = build.Map.Score;
            var typeErrors = build.Types?.Errors.ToList() ?? new List<LuauDiagnostic>();
            report.StaticOk = build.Compile.Ok && typeErrors.Count == 0;
            report.GeometryOk = !build.Geometry.Any(g => g.IsError);
            foreach (var e in build.Compile.Errors.Take(30)) report.Issues.Add(new TestIssue { Severity = "error", Area = "build", Message = e });
            foreach (var d in typeErrors.Take(30)) report.Issues.Add(new TestIssue { Severity = "error", Area = "types " + d.File, Message = $"line {d.Line}: {d.Message}" });
            foreach (var g in build.Geometry.Take(30)) report.Issues.Add(new TestIssue { Severity = g.Severity, Area = "geometry " + g.ObjectId, Message = g.Message });
            foreach (var f in build.Map.Findings.Where(f => f.Severity != "info").Take(15))
                report.Issues.Add(new TestIssue { Severity = f.IsError ? "error" : "warning", Area = "map " + f.Area, Message = f.Message });
            if (build.Map.Score < StageGates.MinMapScore)
                report.Issues.Add(new TestIssue { Severity = "error", Area = "map", Message = $"the map scores {build.Map.Score}/100 ({StageGates.MinMapScore} is the bar{(obby ? ", judged on the course" : "")}): map_report lists what to fix" });
            if (build.Types is { Ran: false, Problem: { } why }) report.Issues.Add(new TestIssue { Severity = "warning", Area = "types", Message = "not type-checked: " + why });
            // The economy, played by a bot on the real map: a wall or an unaffordable start is broken, not a nitpick.
            foreach (var f in build.Economy?.Findings ?? new List<MapFinding>())
                report.Issues.Add(new TestIssue { Severity = f.IsError ? "error" : "warning", Area = "economy", Message = f.Message });

            if (build.Ok)
            {
                var engine = await EngineAsync(ctx, gameId, build, report, ct);
                if (report.Infrastructure) return Finish(ctx, gameId, report);
                if (engine != null) AddEngineIssues(report, engine);

                if (withReview)
                {
                    var verdict = new ReviewTools.Verdict();
                    game = ctx.Game(gameId)!;
                    var outcome = await ctx.Runner.RunAsync(new CycleRequest
                    {
                        GameId = gameId,
                        Stage = GameStage.Test,
                        Role = "tester",
                        SystemPrompt = AgentPrompts.Tester,
                        Briefing = Briefing.Build(game, ctx, ReviewBriefing(report, engine, build)),
                        Tools = ReviewTools.Create(ctx, gameId, verdict),
                        WrapUpAtPromptTokens = ctx.Settings.CycleContextTokens,
                        Options = new LlmCallOptions { MaxTokens = ctx.Settings.MaxOutputTokens, Thinking = Orchestrator.Effort(game, "medium") },
                        Observer = new CycleLog(ctx, gameId),
                        OwnerMessages = () => Orchestrator.TakeNewDirectives(ctx, gameId),
                    }, ct);
                    Orchestrator.Record(ctx, gameId, outcome);
                    if (outcome.ProviderError)
                    {
                        // The model could not be reached: the review never happened, which says nothing about the game.
                        report.Infrastructure = true;
                        report.Summary = "The playtest review could not run (" + (outcome.Error ?? "model unavailable") + "); trying again later.";
                        return Finish(ctx, gameId, report, record: false);
                    }
                    foreach (var issue in verdict.Issues) report.Issues.Add(new TestIssue { Severity = issue.Severity, Area = "review " + issue.Area, Message = issue.Message });
                    if (verdict.Pass == false) report.Issues.Add(new TestIssue { Severity = "error", Area = "review", Message = "the playtest review failed the build: " + verdict.Summary });
                    if (verdict.Pass == null)
                        report.Issues.Add(new TestIssue { Severity = "warning", Area = "review", Message = "the reviewer gave no verdict" + (outcome.Error != null ? ": " + outcome.Error : "") });
                }
            }

            bool engineOk = !report.EngineRan || report.EngineOk || report.Engine is { } run && GameChecksPass(run);
            report.Passed = report.StaticOk && report.GeometryOk && engineOk && !report.Issues.Any(i => i.Severity == "error");
            int errors = report.Issues.Count(i => i.Severity == "error"), warnings = report.Issues.Count(i => i.Severity == "warning");
            report.Summary = report.Passed
                ? $"Passed: clean build r{report.BuildRevision}" + (report.EngineRan ? report.Engine is { Local: true } ? ", every local engine check green" : ", every engine check green" : "") + $", map {report.MapScore}/100, {warnings} warning(s)."
                : $"Failed with {errors} error(s) and {warnings} warning(s): " + string.Join(" | ", report.Issues.Where(i => i.Severity == "error").Take(3).Select(i => $"{i.Area}: {Clip(i.Message, 140)}"));
            return Finish(ctx, gameId, report);
        }

        /// <summary>
        /// The engine run for this build: the builder's own passing run on this exact revision is reused (it is the
        /// same code; a local engine run only while the local engine still stands in for Roblox's); otherwise the game
        /// runs now, in Roblox's engine or the local one. Null when neither can run it here.
        /// </summary>
        private static async Task<EngineRun?> EngineAsync(StudioContext ctx, string gameId, BuildTools.BuildRun build, TestReport report, CancellationToken ct)
        {
            var game = ctx.Game(gameId)!;
            bool roblox = StageGates.EngineAvailable(ctx, game);
            if (game.Build.LastEngineRun is { } reuse && reuse.Revision == report.BuildRevision && (reuse.Ok || GameChecksPass(reuse)) && (!reuse.Local || !roblox))
            {
                report.Issues.Add(new TestIssue { Severity = "info", Area = "engine", Message = $"reused the passing {(reuse.Local ? "local engine" : "engine")} run on r{reuse.Revision} ({reuse.Summary})" });
                return reuse;
            }
            string waiting = ctx.Cloud.HasKey ? "engine tests wait for a free experience slot (they run again before launch)" : "engine tests wait for the Roblox API key (they run again before launch)";
            if (!roblox && !StageGates.LocalEngineStandsIn(ctx, game))
            {
                report.Issues.Add(new TestIssue { Severity = "warning", Area = "engine", Message = waiting });
                return null;
            }
            var (run, message) = await BuildTools.RunEngineTestsAsync(ctx, gameId, ct, build);
            if (run == null && !roblox)
            {
                // The local engine could not run (offline, say): the game is not run locally, which is never a failure.
                report.Issues.Add(new TestIssue { Severity = "warning", Area = "engine", Message = waiting + "; the local engine could not run it: " + message });
                return null;
            }
            if (run == null)
            {
                // Roblox could not be reached, or refused: nothing was learned about the game.
                report.Infrastructure = true;
                report.Summary = "Engine tests could not run: " + message;
                return null;
            }
            return run;
        }

        private static void AddEngineIssues(TestReport report, EngineRun run)
        {
            report.Engine = run;
            report.EngineRan = run.Ran;
            report.EngineOk = run.Ok;
            report.EngineVersion = run.Version;
            if (run.Local)
                report.Issues.Add(new TestIssue { Severity = "info", Area = "engine", Message = "ran in the studio's local engine (no Roblox API key or experience slot yet); Roblox's engine runs the game again before launch" });
            foreach (var r in run.Results.Where(r => !r.Ok).Take(30))
                report.Issues.Add(r.Phase == "kit"
                    ? new TestIssue { Severity = "warning", Area = "kit (studio bug, reported to the owner)", Message = $"{r.Name}: {r.Detail}" }
                    : new TestIssue { Severity = "error", Area = $"engine {r.Phase}", Message = $"{r.Name}: {r.Detail}" });
            foreach (var e in run.Errors.Take(20))
                report.Issues.Add(new TestIssue { Severity = "error", Area = $"runtime {e.Phase}", Message = (e.File != null ? $"{e.File}{(e.Line is int l ? ":" + l : "")}: " : "") + e.Message });
            foreach (var w in run.Warnings.Take(8)) report.Issues.Add(new TestIssue { Severity = "warning", Area = "engine", Message = w });
        }

        /// <summary>Every check of the game's own passed and nothing crashed: only the kit's control checks failed (the studio's bug).</summary>
        internal static bool GameChecksPass(EngineRun run) =>
            run.Ran && run.Errors.Count == 0 && run.Results.Any(r => r.Phase != "kit") && run.Results.Where(r => r.Phase != "kit").All(r => r.Ok);

        private static string ReviewBriefing(TestReport report, EngineRun? engine, BuildTools.BuildRun build)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("## What the automated checks found on this build");
            sb.AppendLine(engine != null ? EngineTests.Describe(engine, maxPassed: 25) : "The engine tests could not run here yet (they run again before launch).");
            sb.AppendLine(build.Map.Describe());
            if (build.Economy != null) sb.AppendLine("ECONOMY (a bot playing the game's numbers on its map):\n" + build.Economy.Describe());
            var rest = report.Issues.Where(i => i.Severity != "info" && !i.Area.StartsWith("engine", StringComparison.Ordinal) && !i.Area.StartsWith("runtime", StringComparison.Ordinal) && !i.Area.StartsWith("map", StringComparison.Ordinal)).Take(20).ToList();
            if (rest.Count > 0) sb.AppendLine("Other findings:\n" + string.Join("\n", rest.Select(i => $"- [{i.Severity}] {i.Area}: {i.Message}")));
            sb.AppendLine("Your job is what no check can do: play it in your head as a 9-year-old, from the spawn, using the renders. Report what is broken, confusing, ugly or boring, then give your verdict.");
            return sb.ToString();
        }

        private static TestReport Finish(StudioContext ctx, string gameId, TestReport report, bool record = true)
        {
            if (report.Infrastructure)
            {
                // Keep the last real verdict: this run decided nothing.
                ctx.Event(EventLevel.Warning, "test.deferred", report.Summary, gameId);
                return report;
            }
            if (record) ctx.Update(gameId, g => g.LastTest = report);
            return report;
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
