using System.Text;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Insights;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>
    /// Builds the first message of every cycle from the dossier: the owner's open directions and the watchdog's
    /// (when there are any), the goal, the brief, the game's design document, lessons, milestones, kept facts,
    /// decisions, the last hand-overs, the stage's current state (build, types, map, engine run, last test) and
    /// the exit-criteria checklist.
    ///
    /// Sections are ordered by how often they change, rarest first, because the provider's prefix cache can only
    /// reuse this message up to its first byte that differs from the previous cycle's: directions and the brief
    /// (rare), lessons (a post-mortem), the design document and milestones (a few times a build), then what every
    /// cycle grows (facts, decisions, hand-overs) and finally the live state and the clock. It is sent without a
    /// receipt stamp; the closing "Now:" line dates it.
    /// </summary>
    public static class Briefing
    {
        public const int DesignExcerpt = 6000;

        public static string Build(GameDossier game, StudioContext ctx, string? extra = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Game {game.GameId}: {game.DisplayTitle}");
            sb.AppendLine($"Stage: {game.Stage}. Goal: {StageGates.Goal(game.Stage)}");
            sb.AppendLine();

            var open = game.Directives.Where(d => d.Status == DirectiveStatus.Open).ToList();
            if (open.Count > 0)
            {
                sb.AppendLine("## The owner's directions (do these first; report each one in finish_cycle directives_done when it is done)");
                foreach (var d in open) sb.AppendLine($"- {d.DirectiveId} ({d.Utc:yyyy-MM-dd}): {d.Text}");
                sb.AppendLine();
            }
            if (game.Progress.Directive is { Length: > 0 } watchdog)
            {
                sb.AppendLine("## The studio's watchdog");
                sb.AppendLine(watchdog);
                sb.AppendLine();
            }

            if (game.Brief is { } b)
            {
                sb.AppendLine("## The brief");
                sb.AppendLine($"Title: {b.Title} · Genre: {b.Genre} · Template: {b.Template} · Players: {b.TargetPlayers}");
                sb.AppendLine($"Pitch: {b.Pitch}");
                sb.AppendLine($"Core loop: {b.CoreLoop}");
                if (b.MustHave.Count > 0) sb.AppendLine("Must have:\n" + string.Join("\n", b.MustHave.Select(m => "- " + m)));
                if (b.NiceToHave.Count > 0) sb.AppendLine("Nice to have: " + string.Join("; ", b.NiceToHave));
                if (b.ArtStyle.Length > 0) sb.AppendLine("Art style: " + b.ArtStyle);
                if (b.AssetNeeds.Count > 0) sb.AppendLine("Models it needs: " + string.Join(", ", b.AssetNeeds));
                if (b.Monetization.Count > 0) sb.AppendLine("Products: " + string.Join("; ", b.Monetization.Select(p => $"{p.Name} ({p.Kind}, {p.PriceRobux} R$, grant {p.Grant})")));
                if (b.Marketing.Hook.Length > 0 || b.Marketing.Keywords.Count > 0)
                    sb.AppendLine($"Marketing: hook \"{b.Marketing.Hook}\"; title ideas {string.Join(" / ", b.Marketing.TitleIdeas)}; keywords {string.Join(", ", b.Marketing.Keywords)}");
                if (b.DoneWhen.Count > 0) sb.AppendLine("Done when:\n" + string.Join("\n", b.DoneWhen.Select(m => "- " + m)));
                sb.AppendLine();
            }

            if (game.Stage is GameStage.Build or GameStage.Test or GameStage.Launch or GameStage.Live && Design(ctx, game) is { } design)
            {
                sb.AppendLine("## The game's design (design.md)");
                sb.AppendLine(design);
                sb.AppendLine();
            }

            var lessons = ctx.Playbook.ForStage(game.Stage, game.Brief?.Template ?? game.Brief?.Genre, 6);
            if (lessons.Count > 0)
            {
                sb.AppendLine("## Lessons from the studio's Playbook");
                sb.AppendLine(Playbook.Format(lessons));
                sb.AppendLine();
            }

            if (game.Milestones.Count > 0)
            {
                sb.AppendLine("## Milestones");
                foreach (var m in game.Milestones) sb.AppendLine($"- [{(m.Done ? "x" : " ")}] {m.MilestoneId}: {m.Title}" + (string.IsNullOrWhiteSpace(m.Notes) ? "" : $" ({m.Notes})"));
                sb.AppendLine();
            }

            if (game.KeyFacts.Count > 0)
            {
                sb.AppendLine("## Facts to keep (by key; a fact you report with the same key replaces it)");
                foreach (var f in game.KeyFacts.TakeLast(GameDossier.MaxKeyFacts))
                    sb.AppendLine("- " + (f.Source == FactBook.Agent ? "" : $"[{f.Source}] ") + f);
                sb.AppendLine();
            }

            if (game.Decisions.Count > 0)
            {
                sb.AppendLine("## Decisions so far");
                foreach (var d in game.Decisions.TakeLast(12)) sb.AppendLine($"- [{d.Stage}] {d.What}" + (d.Why.Length > 0 ? $" — because {d.Why}" : ""));
                sb.AppendLine();
            }

            var recent = game.Cycles.Where(c => c.Stage == game.Stage || game.Stage == GameStage.Live).TakeLast(3).ToList();
            if (recent.Count > 0)
            {
                sb.AppendLine("## The last hand-overs (newest last)");
                foreach (var c in recent)
                {
                    sb.AppendLine($"### {c.Utc:yyyy-MM-dd HH:mm} UTC · {c.Role} · {c.Outcome}");
                    sb.AppendLine(c.Done);
                    if (c.Problems.Count > 0) sb.AppendLine("Problems: " + string.Join("; ", c.Problems));
                    if (c.NextSteps.Count > 0) sb.AppendLine("Next steps: " + string.Join("; ", c.NextSteps));
                }
                sb.AppendLine();
            }

            var answered = game.Directives.Where(d => d.Status == DirectiveStatus.Done && d.ResolvedUtc is DateTime r && ctx.Clock() - r < TimeSpan.FromDays(3)).TakeLast(3).ToList();
            if (answered.Count > 0)
            {
                sb.AppendLine("## Owner's directions recently completed");
                foreach (var d in answered) sb.AppendLine($"- {d.DirectiveId}: {Clip(d.Text, 200)} → {Clip(d.Response ?? "done", 200)}");
                sb.AppendLine();
            }

            sb.AppendLine("## Current state");
            switch (game.Stage)
            {
                case GameStage.Research:
                    if (game.Research.Findings.Count > 0) sb.AppendLine("Findings so far:\n" + string.Join("\n", game.Research.Findings.TakeLast(20).Select(f => "- " + f)));
                    if (game.Concepts.Count > 0)
                        sb.AppendLine("Concepts:\n" + string.Join("\n", game.Concepts.Select(c => $"- {c.ConceptId}{(c.Chosen ? " (CHOSEN)" : "")}: {c.Pitch} [{c.Genre}/{c.Template}] score {c.Scores.Total:0.##}")));
                    break;
                case GameStage.Build:
                case GameStage.Test:
                case GameStage.Live:
                    BuildState(sb, game, ctx);
                    if (game.Stage == GameStage.Live)
                    {
                        sb.AppendLine($"Live since {game.Publish.WentPublicUtc:yyyy-MM-dd}; published version {game.Publish.LastPublishedVersion} (r{game.Publish.LastPublishedRevision}); experience {game.Slot}.");
                        if (game.Maintenance.Count > 0) sb.AppendLine("Recent maintenance:\n" + string.Join("\n", game.Maintenance.TakeLast(4).Select(m => $"- {m.Utc:yyyy-MM-dd}: {m.Summary}")));
                    }
                    break;
                case GameStage.Launch:
                    sb.AppendLine($"Store page: {(game.Store.Title.Length > 0 ? $"\"{game.Store.Title}\"" : "not written")}; icon {(game.Store.IconFile != null ? "made" : "missing")}; thumbnails {game.Store.ThumbnailFiles.Count}; trailer {(game.Store.TrailerFile != null ? "made" : "none")}; compliance {(game.Store.ComplianceOk switch { true => "passed", false => "FAILED", _ => "not run" })}.");
                    if (game.Store.ComplianceNotes.Count > 0) sb.AppendLine("Compliance notes:\n" + string.Join("\n", game.Store.ComplianceNotes.Take(15).Select(n => "- " + n)));
                    if (game.Products.Count > 0) sb.AppendLine("Products: " + string.Join("; ", game.Products.Where(p => !p.Retired).Select(p => $"{p.Key}: {p.Name} {p.PriceRobux} R$ ({p.Kind}, {p.Grant}){(p.RobloxId is long id ? $" id {id}" : "")}")));
                    if (game.Publish.LastLaunchError is { } launchError) sb.AppendLine("The last launch attempt stopped: " + launchError);
                    if (game.Build.MapScore is int score) sb.AppendLine($"The world: map score {score}/100, revision {game.Build.Revision}.");
                    break;
            }

            if (game.Stage is GameStage.Build or GameStage.Test or GameStage.Launch or GameStage.Live && Playtests.BriefingNote(game, ctx.Clock()) is { } playtest)
                sb.AppendLine(playtest);

            var tasks = ctx.Store.Read(s => s.Studio.OwnerTasks.Where(t => t.Status == OwnerTaskStatus.Open && (t.GameId == game.GameId || t.GameId == null)).Select(t => t.Title).ToList());
            if (tasks.Count > 0) sb.AppendLine("Waiting on the owner: " + string.Join("; ", tasks));

            sb.AppendLine();
            sb.AppendLine("## Exit criteria for this stage");
            foreach (var c in StageGates.Checklist(game, ctx)) sb.AppendLine($"- [{(c.Met ? "x" : " ")}] {c.Text}");
            if (!string.IsNullOrWhiteSpace(extra))
            {
                sb.AppendLine();
                sb.AppendLine(extra);
            }
            sb.AppendLine();
            int cycle = game.StageCycles.GetValueOrDefault(game.Stage) + 1;
            sb.AppendLine($"Now: {ctx.Clock():yyyy-MM-dd HH:mm} UTC. Cycle {cycle} of the {game.Stage} stage. There is no cycle limit: keep working until the exit criteria are met. When your context fills, the studio asks you to hand over — do it cleanly and the next cycle continues from your report.");
            return sb.ToString();
        }

        private static void BuildState(StringBuilder sb, GameDossier game, StudioContext ctx)
        {
            var bs = game.Build;
            sb.AppendLine($"Build revision {bs.Revision}, last build {(bs.LastBuildUtc == null ? "never" : bs.LastBuildOk ? "OK" : "FAILED")}" +
                          $", {bs.InstanceCount} instances, {bs.ScriptCount} scripts, feature complete: {bs.FeatureComplete}.");
            if (bs.LastBuildUtc != null)
                sb.AppendLine(bs.TypeChecked ? "Types: checked (strict)." : "Types: not checked" + (bs.TypeCheckNote is { Length: > 0 } note ? " — " + note : "") + ".");
            if (bs.Errors.Count > 0) sb.AppendLine("Errors:\n" + string.Join("\n", bs.Errors.Take(25).Select(e => "- " + e)));
            if (bs.Warnings.Count > 0) sb.AppendLine("Warnings:\n" + string.Join("\n", bs.Warnings.Take(15).Select(e => "- " + e)));
            // The Test stage brings this run's own engine results and map report with it.
            if (game.Stage == GameStage.Test) return;
            if (bs.MapScore is int score)
            {
                sb.AppendLine($"Map: score {score}/100 (the bar is {StageGates.MinMapScore} with no [error]; aim for 85+).");
                foreach (var f in bs.MapFindings.Take(10)) sb.AppendLine("- " + f);
            }
            if (bs.Economy is { Length: > 0 } economy)
            {
                // The bot's summary line and its problems (the full timeline is in map_report).
                sb.AppendLine("Economy check: " + economy.Split('\n')[0].Trim());
                foreach (var f in bs.EconomyFindings.Take(6)) sb.AppendLine("- " + f);
            }
            var decisions = game.Publish.Approvals.Where(a => a.Decision == "changes").OrderByDescending(a => a.Utc).Take(3).ToList();
            foreach (var d in decisions)
                sb.AppendLine($"The owner asked for changes at the {(d.Gate == Approvals.BuildGate ? "build sign-off" : "launch approval")} of r{d.Revision} ({d.Utc:yyyy-MM-dd}): {d.Note}");
            if (bs.LastEngineRun is { } run)
            {
                bool current = run.Revision == bs.Revision;
                sb.AppendLine(current ? "Engine tests on this build:" : $"Engine tests (on r{run.Revision}, an older build — run_tests again after your changes):");
                sb.AppendLine(Clip(EngineTests.Describe(run), 3500).TrimEnd());
            }
            else if (StageGates.EngineAvailable(ctx, game) && game.Stage == GameStage.Build)
            {
                sb.AppendLine("Engine tests: never run — run_tests runs the game in Roblox's engine.");
            }
            else if (StageGates.LocalEngineStandsIn(ctx, game) && game.Stage == GameStage.Build)
            {
                sb.AppendLine("Engine tests: never run — run_tests boots it and plays it through in the studio's local engine (no Roblox API key or experience slot yet).");
            }
            if (game.LastTest is { } t)
            {
                sb.AppendLine($"Last Test stage ({t.Utc:yyyy-MM-dd HH:mm}, revision {t.BuildRevision}): {(t.Passed ? "PASSED" : "FAILED")} — {t.Summary}");
                foreach (var issue in t.Issues.Where(i => i.Severity != "info").Take(20)) sb.AppendLine($"- [{issue.Severity}] {issue.Area}: {issue.Message}");
            }
        }

        /// <summary>The builder's design document (design.md at the workspace root), clipped. Null when there is none.</summary>
        internal static string? Design(StudioContext ctx, GameDossier game)
        {
            try
            {
                var ws = ctx.Workspace(game.GameId);
                if (!ws.Exists) return null;
                string? text = ws.ReadFile("design.md");
                if (string.IsNullOrWhiteSpace(text)) return null;
                text = text.Replace("\r\n", "\n").Trim();
                return text.Length <= DesignExcerpt ? text : text[..DesignExcerpt] + $"\n… [{text.Length - DesignExcerpt} more characters: read_file design.md]";
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
