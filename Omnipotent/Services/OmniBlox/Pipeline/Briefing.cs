using System.Text;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Insights;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>
    /// Builds the first message of every cycle from the dossier: the goal and exit-criteria checklist, the
    /// brief, milestones, kept facts, the last hand-overs, decisions, relevant Playbook lessons, and the
    /// stage's current state. Stable sections come first and volatile ones last, so even this message
    /// reuses as much prompt cache as it can.
    /// </summary>
    public static class Briefing
    {
        public static string Build(GameDossier game, StudioContext ctx, string? extra = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Game {game.GameId}: {game.DisplayTitle}");
            sb.AppendLine($"Stage: {game.Stage}. Goal: {StageGates.Goal(game.Stage)}");
            sb.AppendLine();

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

            if (game.KeyFacts.Count > 0)
            {
                sb.AppendLine("## Facts to keep");
                foreach (var f in game.KeyFacts.TakeLast(GameDossier.MaxKeyFacts)) sb.AppendLine("- " + f);
                sb.AppendLine();
            }

            var lessons = ctx.Playbook.ForStage(game.Stage, game.Brief?.Template ?? game.Brief?.Genre, 6);
            if (lessons.Count > 0)
            {
                sb.AppendLine("## Lessons from the studio's Playbook");
                sb.AppendLine(Playbook.Format(lessons));
                sb.AppendLine();
            }

            if (game.Decisions.Count > 0)
            {
                sb.AppendLine("## Decisions so far");
                foreach (var d in game.Decisions.TakeLast(12)) sb.AppendLine($"- [{d.Stage}] {d.What}" + (d.Why.Length > 0 ? $" — because {d.Why}" : ""));
                sb.AppendLine();
            }

            if (game.Milestones.Count > 0)
            {
                sb.AppendLine("## Milestones");
                foreach (var m in game.Milestones) sb.AppendLine($"- [{(m.Done ? "x" : " ")}] {m.MilestoneId}: {m.Title}" + (string.IsNullOrWhiteSpace(m.Notes) ? "" : $" ({m.Notes})"));
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
                    sb.AppendLine($"Build revision {game.Build.Revision}, last build {(game.Build.LastBuildUtc == null ? "never" : (game.Build.LastBuildOk ? "OK" : "FAILED"))}" +
                                  $", {game.Build.InstanceCount} instances, {game.Build.ScriptCount} scripts, feature complete: {game.Build.FeatureComplete}.");
                    if (game.Build.Errors.Count > 0) sb.AppendLine("Errors:\n" + string.Join("\n", game.Build.Errors.Take(25).Select(e => "- " + e)));
                    if (game.Build.Warnings.Count > 0) sb.AppendLine("Warnings:\n" + string.Join("\n", game.Build.Warnings.Take(15).Select(e => "- " + e)));
                    if (game.LastTest is { } t)
                    {
                        sb.AppendLine($"Last test ({t.Utc:yyyy-MM-dd HH:mm}, revision {t.BuildRevision}): {(t.Passed ? "PASSED" : "FAILED")} — {t.Summary}");
                        foreach (var issue in t.Issues.Take(20)) sb.AppendLine($"- [{issue.Severity}] {issue.Area}: {issue.Message}");
                    }
                    break;
                case GameStage.Launch:
                    sb.AppendLine($"Store page: {(game.Store.Title.Length > 0 ? $"\"{game.Store.Title}\"" : "not written")}; icon {(game.Store.IconFile != null ? "made" : "missing")}; thumbnails {game.Store.ThumbnailFiles.Count}; trailer {(game.Store.TrailerFile != null ? "made" : "none")}; compliance {(game.Store.ComplianceOk switch { true => "passed", false => "FAILED", _ => "not run" })}.");
                    if (game.Store.ComplianceNotes.Count > 0) sb.AppendLine("Compliance notes:\n" + string.Join("\n", game.Store.ComplianceNotes.Take(15).Select(n => "- " + n)));
                    if (game.Products.Count > 0) sb.AppendLine("Products: " + string.Join("; ", game.Products.Select(p => $"{p.Key}: {p.Name} {p.PriceRobux} R$ ({p.Kind}, {p.Grant}){(p.RobloxId is long id ? $" id {id}" : "")}")));
                    break;
                case GameStage.Live:
                    sb.AppendLine($"Live since {game.Publish.WentPublicUtc:yyyy-MM-dd}; published version {game.Publish.LastPublishedVersion}; experience {game.Slot}.");
                    if (game.Maintenance.Count > 0) sb.AppendLine("Recent maintenance:\n" + string.Join("\n", game.Maintenance.TakeLast(4).Select(m => $"- {m.Utc:yyyy-MM-dd}: {m.Summary}")));
                    break;
            }

            var tasks = ctx.Store.Read(s => s.Studio.OwnerTasks.Where(t => t.Status == OwnerTaskStatus.Open && (t.GameId == game.GameId || t.GameId == null)).Select(t => t.Title).ToList());
            if (tasks.Count > 0) sb.AppendLine("Waiting on the owner: " + string.Join("; ", tasks));

            sb.AppendLine();
            sb.AppendLine("## Exit criteria for this stage");
            foreach (var c in StageGates.Checklist(game)) sb.AppendLine($"- [{(c.Met ? "x" : " ")}] {c.Text}");
            if (!string.IsNullOrWhiteSpace(extra))
            {
                sb.AppendLine();
                sb.AppendLine(extra);
            }
            sb.AppendLine();
            sb.AppendLine($"Now: {ctx.Clock():yyyy-MM-dd HH:mm} UTC. Cycle {game.StageCycles.GetValueOrDefault(game.Stage) + 1} of at most {StageGates.CycleBudget(game.Stage)} for this stage.");
            return sb.ToString();
        }
    }
}
