using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Marketing;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>
    /// Whether players can actually join a game, as Roblox tells a signed-out visitor (no key or session needed): the
    /// experience's playability and its content maturity rating. Public is not the same as playable: since September
    /// 2025 Roblox lets nobody but the creator play an experience with no maturity rating, and the studio's first
    /// launched game sat like that for four days (Oct 2026) while the dashboard called it live and its maintainer read
    /// the empty numbers as a slow start. Checked by <see cref="LiveWatch"/> whether or not the studio is on, and
    /// before a launch goes public (<see cref="RatingGateAsync"/>).
    /// </summary>
    public static class PlayerReach
    {
        public const string Unrated = "ContextualPlayabilityUnrated";

        /// <summary>What a signed-out check sees on a game anyone with an account can join.</summary>
        private static readonly HashSet<string> Open = new(StringComparer.Ordinal) { "Playable", "GuestProhibited" };

        /// <summary>Statuses that shut everyone but the creator out, and why.</summary>
        private static readonly Dictionary<string, string> Closed = new(StringComparer.Ordinal)
        {
            [Unrated] = "it has no maturity rating, and Roblox lets nobody but the creator play an unrated experience (answer its Maturity & Compliance questionnaire)",
            ["UniverseRootPlaceIsPrivate"] = "the experience is private",
            ["GameUnapproved"] = "Roblox moderation has not approved it",
            ["UnderReview"] = "Roblox is reviewing it",
            ["PlaceHasNoPublishedVersion"] = "its place has no published version",
            ["IncorrectConfiguration"] = "Roblox says its configuration is incomplete",
            ["ComplianceBlocked"] = "Roblox blocks it for compliance",
            ["InsufficientPermissionFriendsOnly"] = "only the owner's friends can join it",
            ["InsufficientPermissionGroupOnly"] = "only the group's members can join it",
        };

        /// <summary>True when anyone with an account can join, false when Roblox shuts them out, null for a status this does not know.</summary>
        public static bool? Interpret(string status, bool isPlayable) =>
            isPlayable || Open.Contains(status) ? true : Closed.ContainsKey(status) ? false : null;

        /// <summary>Why players cannot join, in plain words; null when they can (or it is not known).</summary>
        public static string? Why(ReachRecord? reach) =>
            reach is { Playable: false, Status: { } status } ? Closed.GetValueOrDefault(status, "Roblox reports " + status) : null;

        /// <summary>Roblox shows the game's experience unrated (by its playability or its rating).</summary>
        public static bool ShowsUnrated(GameDossier game) =>
            game.Publish.Reach is { } r && (r.Status == Unrated || string.Equals(r.Maturity, "unrated", StringComparison.OrdinalIgnoreCase));

        /// <summary>A line for the agents: what the numbers mean while players cannot join (null while nothing is wrong).</summary>
        public static string? Describe(GameDossier game)
        {
            var reach = game.Publish.Reach;
            if (game.Stage == GameStage.Live && !game.Publish.IsPublic && game.Publish.LastPublishedVersion != null)
                return "PLAYERS CANNOT JOIN: the owner made the experience private. Until they make it public again it gets no players, so zero traffic says nothing about the game itself.";
            if (reach?.Playable != false || !game.Publish.IsPublic) return null;
            return $"PLAYERS CANNOT JOIN: Roblox reports \"{reach.Status}\" (since {reach.SinceUtc ?? reach.CheckedUtc:yyyy-MM-dd HH:mm} UTC): {Why(reach)}. " +
                   "Until that changes the game gets no players, so zero traffic says nothing about the game itself; the owner has been asked, and no update to the game changes it.";
        }

        /// <summary>
        /// Asks Roblox about one public game and records the answer. When players cannot join, the owner hears it once
        /// per cause (an event, a Discord message and, for a missing rating, the questionnaire task saying what it costs);
        /// when the rating appears without the studio answering, the owner answered it, and the task closes.
        /// </summary>
        public static async Task<ReachRecord?> CheckAsync(StudioContext ctx, string gameId, CancellationToken ct)
        {
            var game = ctx.Game(gameId);
            if (game?.Slot is not { } slot || !game.Publish.IsPublic) return null;
            var now = ctx.Clock();
            var previous = game.Publish.Reach;
            var reach = new ReachRecord { CheckedUtc = now };
            try
            {
                if ((await ctx.Public.GetPlayabilityAsync(new[] { slot.UniverseId }, ct)).FirstOrDefault(p => p.UniverseId == slot.UniverseId) is { } p)
                {
                    reach.Status = p.Status;
                    reach.Playable = Interpret(p.Status, p.IsPlayable);
                }
                var rating = await ctx.Public.GetMaturityAsync(slot.UniverseId, ct);
                reach.Maturity = rating?.Maturity;
                reach.MaturityLabel = rating?.Label;
            }
            catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or TimeoutException or Newtonsoft.Json.JsonException)
            {
                reach.Error = ex.Message;
            }
            // Nothing learned: keep what was known, with the error.
            if (reach.Status == null && previous != null)
            {
                reach.Status = previous.Status;
                reach.Playable = previous.Playable;
            }
            reach.Maturity ??= previous?.Maturity;
            reach.MaturityLabel ??= previous?.MaturityLabel;
            reach.SinceUtc = previous != null && previous.Status == reach.Status ? previous.SinceUtc ?? previous.CheckedUtc
                // First look at a game nobody ever rated: it has been closed since it went public.
                : previous?.Status == null && reach.Status == Unrated && game.Publish.Questionnaire is not { Published: true } ? game.Publish.WentPublicUtc ?? now
                : now;
            reach.AlertedStatus = reach.Playable == true ? null : previous?.AlertedStatus;

            string title = Title(game);
            bool changed = reach.Status != null && reach.Status != previous?.Status;
            bool alert = reach.Playable == false && reach.AlertedStatus != reach.Status;
            if (alert) reach.AlertedStatus = reach.Status;

            ctx.Store.Mutate(s =>
            {
                var g = s.Game(gameId);
                if (g == null) return;
                g.Publish.Reach = reach;
                NoteRating(s, g, previous?.Maturity, now);
                if (changed && reach.Playable == false)
                    s.AddEvent(EventLevel.Warning, "reach.closed", $"Nobody can play \"{title}\": {Why(reach)} (Roblox: {reach.Status}).", gameId, now);
                else if (changed && reach.Playable == true && previous?.Playable == false)
                    s.AddEvent(EventLevel.Success, "reach.open", $"Players can join \"{title}\" again (Roblox: {reach.Status}).", gameId, now);
                s.Touch(g, now);
            });

            if (reach.Status == Unrated || string.Equals(reach.Maturity, "unrated", StringComparison.OrdinalIgnoreCase))
                StorePageChores.FileQuestionnaireTask(ctx, ctx.Game(gameId)!, ctx.Session is { HasSession: true }
                    ? "The studio answers it with your Roblox session; this task stays open until Roblox shows the rating."
                    : "Roblox has no API key for the questionnaire; it needs a signed-in session.\n" + StorePageChores.SessionHint);
            if (alert)
                await ctx.Alert($"Nobody can play \"{title}\": {Why(reach)}. https://www.roblox.com/games/{slot.PlaceId}");
            return reach;
        }

        /// <summary>
        /// The launch's last check before a game goes public: Roblox must show a maturity rating, or the game would go
        /// public with nobody able to play it. Null when it may go public (rated, or Roblox could not be asked: the
        /// watch catches it afterwards); otherwise why the launch waits. The questionnaire task says what is at stake.
        /// </summary>
        public static async Task<string?> RatingGateAsync(StudioContext ctx, string gameId, ExperienceSlot slot, CancellationToken ct)
        {
            MaturityRating? rating;
            try { rating = await ctx.Public.GetMaturityAsync(slot.UniverseId, ct); }
            catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or TimeoutException or Newtonsoft.Json.JsonException)
            {
                ctx.Log($"[OmniBlox] {gameId}: could not read the maturity rating before going public ({ex.Message}); going ahead.");
                return null;
            }
            if (rating == null) return null;
            var now = ctx.Clock();
            ctx.Store.Mutate(s =>
            {
                if (s.Game(gameId) is not { } g) return;
                string? before = g.Publish.Reach?.Maturity;
                g.Publish.Reach ??= new ReachRecord { SinceUtc = now };
                g.Publish.Reach.CheckedUtc = now;
                g.Publish.Reach.Maturity = rating.Maturity;
                g.Publish.Reach.MaturityLabel = rating.Label;
                NoteRating(s, g, before, now);
                s.Touch(g, now);
            });
            if (rating.Rated) return null;
            StorePageChores.FileQuestionnaireTask(ctx, ctx.Game(gameId)!, ctx.Session is { HasSession: true }
                ? "The studio answered it with your Roblox session, but Roblox does not show the rating yet; this task closes when it does."
                : "Roblox has no API key for the questionnaire; it needs a signed-in session.\n" + StorePageChores.SessionHint);
            return "waiting for the maturity rating: Roblox lets nobody play an unrated experience, so the game goes public once its questionnaire is answered";
        }

        private static string Title(GameDossier game) => game.Store.Title.Length > 0 ? game.Store.Title : game.DisplayTitle;

        /// <summary>
        /// Under the store lock, after a new rating reading: a rating that appeared without the studio answering was
        /// the owner's (recorded as theirs, for the game's current facts), and once the experience is rated for the
        /// current facts the questionnaire task closes.
        /// </summary>
        private static void NoteRating(OmniBloxState s, GameDossier g, string? previousMaturity, DateTime now)
        {
            if (g.Publish.Reach is not { Maturity: { Length: > 0 } maturity } reach || maturity == "unrated") return;
            if (string.Equals(previousMaturity, "unrated", StringComparison.OrdinalIgnoreCase) && g.Publish.Questionnaire is not { Published: true })
            {
                g.Publish.Questionnaire = new QuestionnaireRecord
                {
                    Utc = now,
                    FactsHash = StorePageChores.NotesHash(ComplianceChecker.Check(g.Store, g.Products, g.Brief).QuestionnaireNotes),
                    Published = true,
                    ByOwner = true,
                    AgeRating = reach.MaturityLabel,
                };
                s.AddEvent(EventLevel.Success, "reach.rated", $"\"{Title(g)}\" has a maturity rating now ({reach.MaturityLabel}).", g.GameId, now);
            }
            if (!StorePageChores.QuestionnairePending(g)) s.ResolveOwnerTasks(StorePageChores.QuestionnaireKey(g.GameId), now);
        }
    }
}
