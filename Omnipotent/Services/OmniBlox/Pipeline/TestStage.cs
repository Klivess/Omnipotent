using Omnipotent.Services.OmniBlox.Agents;
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
    /// The Test stage: a clean build (syntax + scene + geometry), the kit's and the game's specs in Roblox's
    /// own engine when a key and an experience slot exist, and a playtest review by a vision-capable agent.
    /// A failure sends the game back to Build with the report in its briefing.
    /// </summary>
    public static class TestStage
    {
        public static async Task<TestReport> RunAsync(StudioContext ctx, string gameId, bool withReview, CancellationToken ct)
        {
            var report = new TestReport { Utc = ctx.Clock() };
            var run = BuildTools.RunBuild(ctx, gameId);
            report.BuildRevision = ctx.Game(gameId)!.Build.Revision;
            report.StaticOk = run.Compile.Ok;
            report.GeometryOk = !run.Geometry.Any(g => g.IsError);
            foreach (var e in run.Compile.Errors.Take(30)) report.Issues.Add(new TestIssue { Severity = "error", Area = "build", Message = e });
            foreach (var g in run.Geometry.Take(30)) report.Issues.Add(new TestIssue { Severity = g.Severity, Area = "geometry " + g.ObjectId, Message = g.Message });

            if (run.Ok)
            {
                var slot = ctx.Cloud.HasKey ? Slots.Ensure(ctx, gameId) : null;
                if (slot != null)
                {
                    try
                    {
                        var engine = await EngineTests.RunAsync(ctx, slot, run.Compile.PlaceXml, ct);
                        report.EngineRan = engine.Ran;
                        report.EngineOk = engine.Ok;
                        report.EngineVersion = engine.Version;
                        foreach (var f in engine.Failures.Take(30)) report.Issues.Add(new TestIssue { Severity = "error", Area = "engine", Message = f });
                        if (engine.Ok) ctx.Update(gameId, g => g.Publish.EngineTestedRevision = report.BuildRevision);
                    }
                    catch (Exception ex) when (ex is RobloxApiException or TimeoutException or HttpRequestException)
                    {
                        report.Issues.Add(new TestIssue { Severity = "warning", Area = "engine", Message = "engine tests could not run: " + ex.Message });
                    }
                }
                else
                {
                    report.Issues.Add(new TestIssue { Severity = "warning", Area = "engine", Message = ctx.Cloud.HasKey ? "engine tests wait for a free experience slot" : "engine tests wait for the Roblox API key (they run again before launch)" });
                }

                if (withReview)
                {
                    var verdict = new ReviewTools.Verdict();
                    var game = ctx.Game(gameId)!;
                    var outcome = await ctx.Runner.RunAsync(new CycleRequest
                    {
                        GameId = gameId,
                        Stage = GameStage.Test,
                        Role = "tester",
                        SystemPrompt = AgentPrompts.Tester,
                        Briefing = Briefing.Build(game, ctx, "Automated results this run:\n" + string.Join("\n", report.Issues.Take(20).Select(i => $"- [{i.Severity}] {i.Area}: {i.Message}"))),
                        Tools = ReviewTools.Create(ctx, gameId, verdict),
                        MaxTurns = 16,
                        Options = new LlmCallOptions { MaxTokens = 3000, Thinking = "medium" },
                    }, ct);
                    Orchestrator.Record(ctx, gameId, outcome);
                    foreach (var issue in verdict.Issues) report.Issues.Add(new TestIssue { Severity = issue.Severity, Area = "review " + issue.Area, Message = issue.Message });
                    if (verdict.Pass == false) report.Issues.Add(new TestIssue { Severity = "error", Area = "review", Message = "the playtest review failed the build: " + verdict.Summary });
                    if (verdict.Pass == null && outcome.Error == null)
                        report.Issues.Add(new TestIssue { Severity = "warning", Area = "review", Message = "the reviewer gave no verdict" });
                }
            }

            report.Passed = report.StaticOk && report.GeometryOk && (!report.EngineRan || report.EngineOk) && !report.Issues.Any(i => i.Severity == "error");
            int errors = report.Issues.Count(i => i.Severity == "error"), warnings = report.Issues.Count(i => i.Severity == "warning");
            report.Summary = report.Passed
                ? $"Passed: clean build r{report.BuildRevision}" + (report.EngineRan ? ", engine specs green" : "") + $", {warnings} warning(s)."
                : $"Failed with {errors} error(s) and {warnings} warning(s).";
            ctx.Update(gameId, g => g.LastTest = report);
            return report;
        }
    }
}
