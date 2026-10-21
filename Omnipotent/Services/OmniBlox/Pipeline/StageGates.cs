using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    public sealed record Criterion(string Text, bool Met);

    /// <summary>
    /// Each stage's exit criteria, as checks on the dossier. The orchestrator only advances a game when every
    /// criterion holds — an agent saying "done" is not enough — and every briefing shows the checklist. There is
    /// no cycle budget: a stage takes as long as it takes, and the watchdog steps in only when the game stops
    /// moving (see <see cref="Watchdog"/>).
    /// </summary>
    public static class StageGates
    {
        public const double MinConceptScore = 5.0;
        /// <summary>The map report score a non-obby world must reach (obbies are judged on their course).</summary>
        public const int MinMapScore = 60;

        /// <summary>Whether the game can run in Roblox's engine here: an API key, and an experience slot (its own or a free one).</summary>
        public static bool EngineAvailable(StudioContext ctx, GameDossier game) => ctx.Cloud.HasKey && (game.Slot != null || Slots.AnyFree(ctx));

        /// <summary>The last engine run counts for this build: on the current revision, and every check of the game's own passed.</summary>
        public static bool EnginePassed(GameDossier game) =>
            game.Build.LastEngineRun is { } run && run.Revision == game.Build.Revision && (run.Ok || TestStage.GameChecksPass(run));

        public static List<Criterion> Checklist(GameDossier game, StudioContext ctx)
        {
            var list = new List<Criterion>();
            switch (game.Stage)
            {
                case GameStage.Research:
                    {
                        var chosen = game.Concepts.FirstOrDefault(c => c.Chosen);
                        var b = game.Brief;
                        list.Add(new("At least three concepts proposed and scored", game.Concepts.Count >= 3));
                        list.Add(new($"A concept chosen with a total score of at least {MinConceptScore}", chosen != null && chosen.Scores.Total >= MinConceptScore));
                        list.Add(new("A brief is written with a supported template (obby, tycoon, simulator or sandbox)", b != null && GameKit.Templates.ContainsKey(b.Template ?? "")));
                        list.Add(new("The brief has a core loop, at least 3 must-haves and at least 2 'done when' checks", b != null && b.CoreLoop.Length > 10 && b.MustHave.Count >= 3 && b.DoneWhen.Count >= 2));
                        list.Add(new("The brief plans at least one fair product", b != null && b.Monetization.Count >= 1));
                        break;
                    }
                case GameStage.Build:
                    {
                        bool obby = string.Equals(game.Brief?.Template, "obby", StringComparison.OrdinalIgnoreCase);
                        bool mapBlocked = game.Build.MapFindings.Any(f => f.StartsWith("[error]", StringComparison.Ordinal));
                        list.Add(new("The game has been built at least once", game.Build.Revision >= 1));
                        list.Add(new("The latest build compiles with no errors (syntax, strict types, scene)", game.Build.LastBuildOk));
                        list.Add(new("The geometry verifier reports no errors", game.Build.LastBuildOk && !game.Build.Errors.Any(e => e.StartsWith("[geometry]", StringComparison.Ordinal))));
                        list.Add(obby
                            ? new("The map report has no blocking problems", game.Build.Revision >= 1 && !mapBlocked)
                            : new($"The map scores at least {MinMapScore}/100 with no blocking problems (map_report)", game.Build.Revision >= 1 && !mapBlocked && (game.Build.MapScore ?? 0) >= MinMapScore));
                        if (EngineAvailable(ctx, game))
                            list.Add(new("The game ran clean in Roblox's engine on the current build (run_tests)", EnginePassed(game)));
                        list.Add(new("Every milestone is done", game.Milestones.Count > 0 && game.Milestones.All(m => m.Done)));
                        list.Add(new("The builder has declared the brief's must-haves complete", game.Build.FeatureComplete));
                        break;
                    }
                case GameStage.Test:
                    list.Add(new("The current build passed all tests", game.LastTest is { Passed: true } t && t.BuildRevision == game.Build.Revision));
                    break;
                case GameStage.Launch:
                    list.Add(new("Store page written (title, description)", game.Store.Title.Length > 0 && game.Store.Description.Length > 0));
                    list.Add(new("Icon made", game.Store.IconFile != null));
                    list.Add(new("At least one thumbnail made", game.Store.ThumbnailFiles.Count > 0));
                    list.Add(new("Compliance check passed", game.Store.ComplianceOk == true));
                    list.Add(new("Game is public", game.Publish.IsPublic));
                    break;
            }
            return list;
        }

        public static bool IsMet(GameDossier game, StudioContext ctx) => Checklist(game, ctx).All(c => c.Met);

        /// <summary>The marketing half of Launch is ready (what the launch agent produces before the publish sequence runs).</summary>
        public static bool MarketingReady(GameDossier game) =>
            game.Store.Title.Length > 0 && game.Store.Description.Length > 0 && game.Store.IconFile != null
            && game.Store.ThumbnailFiles.Count > 0 && game.Store.ComplianceOk == true;

        public static string Goal(GameStage stage) => stage switch
        {
            GameStage.Research => "Decide what to build next, with evidence: study what is popular and rising on Roblox right now, find a gap this studio can fill well, and write a buildable brief that will sell.",
            GameStage.Build => "Turn the brief into a complete, playable, good-looking game: plan the map, build a big, detailed, fully dressed world, then the core loop, every must-have and polish. build after every change and fix every error; run_tests to run it in Roblox's engine; look at it with render_scene and map_report.",
            GameStage.Test => "Make sure nothing goes public broken: play the game as a child would — from the spawn, through the loop, the shop and the UI — and pass or fail the build with specific issues.",
            GameStage.Launch => "Get the game in front of players: an honest, catchy store page, a bright icon and thumbnails made from the real game, fair products, a passing compliance check — then request the launch.",
            GameStage.Live => "Keep the game healthy and growing: read its numbers and feedback, fix what is broken, tune what is weak, refresh what is not clicking, add content, and record what was learned.",
            _ => "",
        };
    }
}
