using System.Security.Cryptography;
using System.Text;
using Omnipotent.Services.OmniBlox.Core;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>
    /// The owner's two gates on the way to players. Neither can be passed by an agent or by the Act-tier task
    /// route; only the owner's games/approve does, and each approval is for exactly what was looked at.
    /// <list type="number">
    /// <item><b>Build sign-off</b> (OmniBlox_RequireBuildSignoff): when a game passes its tests it waits, before any
    /// marketing work, for the owner to play that revision and sign it off — or ask for changes, which go to the
    /// builder as a direction and send the game back to Build. A new revision needs a new sign-off.</item>
    /// <item><b>Launch approval</b> (OmniBlox_RequireLaunchApproval): when the store page, art and products are
    /// ready, the final build has run clean in Roblox's engine and before anything happens on Roblox (products,
    /// store page, thumbnails, publishing), the owner approves this revision with this store page and these
    /// prices. A change to any of them after the approval needs approving again.</item>
    /// </list>
    /// The first studio created a game's products and store page and only then asked; and when one of those
    /// steps failed it retried every five minutes for hours before the owner ever saw the game.
    /// </summary>
    public static class Approvals
    {
        public const string BuildGate = "build";
        public const string LaunchGate = "launch";

        public static string SignoffKey(string gameId) => "signoff:" + gameId;
        public static string LaunchKey(string gameId) => "approve:" + gameId;

        /// <summary>The game is waiting for the owner to sign off its current build.</summary>
        public static bool SignoffNeeded(StudioContext ctx, GameDossier g) =>
            ctx.Settings.RequireBuildSignoff && g.Publish.SignoffRevision != g.Build.Revision;

        /// <summary>The launch is approved for exactly this revision, store page and products.</summary>
        public static bool LaunchApproved(StudioContext ctx, GameDossier g) =>
            !ctx.Settings.RequireLaunchApproval || g.Publish.ApprovedRevision == g.Build.Revision && g.Publish.ApprovedFingerprint == Fingerprint(g);

        /// <summary>What a launch approval covers: the store page and the products, as the players will see them.</summary>
        public static string Fingerprint(GameDossier g)
        {
            var sb = new StringBuilder();
            sb.Append(g.Store.Title).Append('\n').Append(g.Store.Description).Append('\n').Append(g.Store.IconFile).Append('\n').Append(string.Join(",", g.Store.ThumbnailFiles)).Append('\n');
            foreach (var p in g.Products.OrderBy(p => p.Key, StringComparer.Ordinal))
                sb.Append(p.Key).Append('|').Append(p.Kind).Append('|').Append(p.Name).Append('|').Append(p.PriceRobux).Append('|').Append(p.Grant).Append('|').Append(p.Description).Append('\n');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
        }

        /// <summary>Files an owner task to play the current build and sign it off; the game waits.</summary>
        public static void RequestSignoff(StudioContext ctx, string gameId)
        {
            var g = ctx.Game(gameId)!;
            var now = ctx.Clock();
            string detail =
                $"\"{g.DisplayTitle}\" passed its tests on revision r{g.Build.Revision}: map {g.Build.MapScore}/100" +
                (g.Build.LastEngineRun is { } run && run.Revision == g.Build.Revision
                    ? run.Local ? $", {run.Summary.TrimEnd('.')} (Roblox's engine runs it again before launch)" : $", engine: {run.Summary}"
                    : "") +
                (g.LastTest is { } t ? $", review: {t.Summary}" : "") + ".\n" +
                "Play it before it is marketed: ▶ Play on the game's page (Play in Roblox, or Open in Studio). Look at the World tab's renders and the map report.\n" +
                "Then Approve the build, or Request changes with what to fix (it goes to the builder as a direction and the game goes back to Build). Nothing is published or created on Roblox until the launch approval that follows.";
            ctx.Store.Mutate(s =>
            {
                // A sign-off asked for an older revision is replaced by this one.
                s.ResolveOwnerTasks(SignoffKey(gameId), now);
                s.FileOwnerTask(OwnerTaskKind.BuildSignoff, SignoffKey(gameId), $"Play and sign off \"{g.DisplayTitle}\" (r{g.Build.Revision})", detail, gameId, now);
                var game = s.Game(gameId)!;
                game.WaitingReason = $"waiting for you to play r{g.Build.Revision} and sign it off";
                s.Touch(game, now);
            });
        }

        /// <summary>Files an owner task to approve the launch; nothing on Roblox happens until it is approved.</summary>
        public static void RequestLaunch(StudioContext ctx, string gameId, ExperienceSlot slot, IEnumerable<string> files, IEnumerable<string> questionnaire)
        {
            var g = ctx.Game(gameId)!;
            var now = ctx.Clock();
            var products = g.Products.Select(p => $"- {p.Name} ({(p.Kind == ProductKind.GamePass ? "game pass" : "product")}, R$ {p.PriceRobux}): {p.Description}");
            string detail =
                $"Ready to go public as \"{g.Store.Title}\" in experience {slot.UniverseId} (revision r{g.Build.Revision}, run clean in Roblox's engine).\n" +
                $"Store page:\n{g.Store.Description}\n\nProducts:\n{string.Join("\n", products)}\n\n" +
                "Approve to let the studio create the products, set the store page and thumbnails, publish and make the game public. Request changes to send it back (say what to change).\n" +
                "Maturity questionnaire answers prepared:\n- " + string.Join("\n- ", questionnaire);
            ctx.Store.Mutate(s =>
            {
                s.FileOwnerTask(OwnerTaskKind.LaunchApproval, LaunchKey(gameId), $"Approve the launch of \"{g.Store.Title}\"", detail, gameId, now, files);
                var game = s.Game(gameId)!;
                game.WaitingReason = "waiting for your launch approval";
                s.Touch(game, now);
            });
        }

        /// <summary>The owner approves a gate for the game as it is now.</summary>
        public static void Approve(StudioContext ctx, string gameId, string gate, string? note = null)
        {
            var now = ctx.Clock();
            var game = ctx.Game(gameId) ?? throw new KeyNotFoundException("No game " + gameId);
            if (game.Stage != GameStage.Launch) throw new InvalidOperationException("This game is not waiting at a launch gate (it is in " + game.Stage + ").");
            bool build = gate == BuildGate;
            string fingerprint = Fingerprint(game);
            ctx.Store.Mutate(s =>
            {
                var g = s.Game(gameId)!;
                if (build)
                {
                    g.Publish.SignoffRevision = g.Build.Revision;
                    g.Publish.SignoffUtc = now;
                    s.ResolveOwnerTasks(SignoffKey(gameId), now);
                }
                else
                {
                    g.Publish.ApprovedRevision = g.Build.Revision;
                    g.Publish.ApprovedFingerprint = fingerprint;
                    g.Publish.LaunchApprovedUtc = now;
                    s.ResolveOwnerTasks(LaunchKey(gameId), now);
                }
                g.Publish.Approvals.Add(new ApprovalRecord { Gate = gate, Decision = "approved", Note = note ?? "", Revision = g.Build.Revision, Utc = now });
                g.WaitingReason = null;
                g.NextAttemptUtc = now;
                g.Decisions.Add(new Decision { Utc = now, Stage = g.Stage, What = build ? $"The owner signed off r{g.Build.Revision}" : $"The owner approved the launch of r{g.Build.Revision}", Why = note ?? "" });
                s.Touch(g, now);
                s.AddEvent(EventLevel.Success, build ? "owner.signoff" : "owner.approved", build ? $"\"{g.DisplayTitle}\" r{g.Build.Revision} signed off." : $"Launch of \"{g.DisplayTitle}\" approved.", gameId, now);
            });
        }

        /// <summary>
        /// The owner asks for changes at a gate: the note becomes a direction for the agents. At the build gate (or
        /// when the note is about the game itself) the game goes back to Build; at the launch gate otherwise it goes
        /// back to marketing.
        /// </summary>
        public static void RequestChanges(StudioContext ctx, Orchestrator? orchestrator, string gameId, string gate, string note, bool backToBuild)
        {
            var now = ctx.Clock();
            var game = ctx.Game(gameId) ?? throw new KeyNotFoundException("No game " + gameId);
            if (game.Stage != GameStage.Launch) throw new InvalidOperationException("This game is not waiting at a launch gate.");
            if (string.IsNullOrWhiteSpace(note)) throw new ArgumentException("Say what to change.");
            bool toBuild = gate == BuildGate || backToBuild;
            ctx.Store.Mutate(s =>
            {
                var g = s.Game(gameId)!;
                s.ResolveOwnerTasks(gate == BuildGate ? SignoffKey(gameId) : LaunchKey(gameId), now);
                g.Publish.Approvals.Add(new ApprovalRecord { Gate = gate, Decision = "changes", Note = note.Trim(), Revision = g.Build.Revision, Utc = now });
                string id = "d" + (g.Directives.Count + 1);
                while (g.Directives.Any(d => d.DirectiveId == id)) id += "x";
                g.Directives.Add(new OwnerDirective { DirectiveId = id, Utc = now, Text = $"Changes requested at the {(gate == BuildGate ? "build sign-off" : "launch approval")} of r{g.Build.Revision}: {note.Trim()}" });
                g.Publish.LaunchRequestedUtc = null;
                if (toBuild) g.Build.FeatureComplete = false;
                g.WaitingReason = null;
                g.NextAttemptUtc = now;
                s.Touch(g, now);
                s.AddEvent(EventLevel.Warning, "owner.changes", $"Changes requested for \"{g.DisplayTitle}\": {note.Trim()}", gameId, now);
            });
            if (toBuild)
            {
                if (orchestrator != null) orchestrator.Enter(gameId, GameStage.Build, "the owner asked for changes");
                else ctx.Update(gameId, g => { g.Stage = GameStage.Build; g.StageEnteredUtc = now; });
            }
        }
    }
}
