using System.Globalization;
using System.Security.Cryptography;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Marketing;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>The questionnaire could not be answered this time (the model's answers stayed incomplete, or Roblox refused them).</summary>
    public sealed class QuestionnaireException : Exception
    {
        public QuestionnaireException(string message) : base(message) { }
    }

    /// <summary>
    /// The Creator Dashboard chores around a launch, done by the studio: an abandoned game's products in a reused
    /// experience come off sale (API key), and the experience icon, store thumbnails and Roblox's maturity
    /// questionnaire are done with the owner's Roblox session (no API key can do them). Each runs at launch and again
    /// from housekeeping until it is done, waiting longer after each failure. The owner is asked only for what needs
    /// a person: a session to act with (once), a session Roblox signed out, a questionnaire whose rating comes out
    /// above what the game's content allows, or a chore Roblox keeps refusing for a day.
    /// </summary>
    public static class StorePageChores
    {
        public static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan MaxRetry = TimeSpan.FromHours(6);
        /// <summary>How long a chore may keep failing before the owner hears about it.</summary>
        public static readonly TimeSpan EscalateAfter = TimeSpan.FromHours(24);
        public const string SessionKey = "roblox-session";

        public static string IconKey(string gameId) => "icon:" + gameId;
        public static string QuestionnaireKey(string gameId) => "questionnaire:" + gameId;
        public static string RetireKey(long productId) => "retire:" + productId.ToString(CultureInfo.InvariantCulture);

        internal const string SessionHint =
            "To have OmniBlox do this itself, now and for every later game, connect the Roblox account that owns the experiences once: OmniBlox dashboard → Roblox account → Connect, " +
            "then type the code into Roblox's Quick Log In (Account Settings → Quick Log In) on a device signed in to that account, on the same network as the server.";

        // ─────────────── housekeeping ───────────────

        /// <summary>Finishes whatever launches left: products still on sale, icons, thumbnails and questionnaires (and the owner tasks older launches filed for them).</summary>
        public static async Task SweepAsync(StudioContext ctx, CancellationToken ct)
        {
            await ctx.ChoreGate.WaitAsync(ct);
            try { await SweepLockedAsync(ctx, ct); }
            finally { ctx.ChoreGate.Release(); }
        }

        private static async Task SweepLockedAsync(StudioContext ctx, CancellationToken ct)
        {
            await RetirePendingAsync(ctx, ct);

            var games = ctx.Store.Read(s => s.Games.Values.Where(g => g.Stage is GameStage.Live or GameStage.Sunset && g.Slot != null).Select(g => g.GameId).ToList());
            bool? modelUp = null;
            foreach (var gameId in games)
            {
                ct.ThrowIfCancellationRequested();
                if (IconPending(ctx, ctx.Game(gameId)!)) await IconAsync(ctx, gameId, ct);
                if (QuestionnairePending(ctx.Game(gameId)!))
                {
                    modelUp ??= (await ctx.Llm.CheckAvailableAsync(ct)).Ok;
                    if (modelUp == true) await QuestionnaireAsync(ctx, gameId, ct);
                }
            }

            // A session Roblox signed out: the task goes once a session works again.
            if (ctx.Session is { HasSession: true } session && ctx.Store.Read(s => s.Studio.OwnerTasks.Any(t => t.Status == OwnerTaskStatus.Open && t.DedupeKey == SessionKey)) && Due(ctx, SessionKey))
            {
                try
                {
                    await session.GetSignedInUserAsync(ct);
                    Succeeded(ctx, SessionKey);
                }
                catch (RobloxSessionException) { Failed(ctx, SessionKey, "still signed out"); }
                catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or TimeoutException or Newtonsoft.Json.JsonException) { Failed(ctx, SessionKey, ex.Message); }
            }
        }

        // ─────────────── products off sale ───────────────

        /// <summary>
        /// A slot is reused after its game was abandoned, and that game may have created passes and products there
        /// before it stopped (a launch that failed late). Left alone they would sell on the new game's page and grant
        /// nothing, so they come off sale. Never blocks the launch: what Roblox refuses is retried from housekeeping.
        /// </summary>
        public static async Task RetireStaleProductsAsync(StudioContext ctx, ExperienceSlot slot, CancellationToken ct)
        {
            var stale = ctx.Store.Read(s => s.Games.Values
                .Where(g => g.Stage == GameStage.Abandoned && g.Slot?.UniverseId == slot.UniverseId)
                .SelectMany(g => g.Products.Where(p => p.RobloxId != null && NotYetOffSale(s, p)).Select(p => (Game: g.GameId, Product: OmniBloxStore.Clone(p))))
                .ToList());
            foreach (var (oldGame, product) in stale) await RetireAsync(ctx, oldGame, product, slot.UniverseId, ct);
        }

        /// <summary>Still on sale as far as the studio knows: never retired, or marked retired by an older studio that filed an owner task because Roblox refused.</summary>
        private static bool NotYetOffSale(OmniBloxState s, ProductRecord p) =>
            !p.Retired || p.RetiredUtc == null && s.Studio.OwnerTasks.Any(t => t.Status == OwnerTaskStatus.Open && t.DedupeKey == RetireKey(p.RobloxId!.Value));

        /// <summary>Retries the products a launch could not take off sale (its chores and the owner tasks older launches filed).</summary>
        private static async Task RetirePendingAsync(StudioContext ctx, CancellationToken ct)
        {
            if (!ctx.Cloud.HasKey) return;
            var pending = ctx.Store.Read(s =>
            {
                var keys = s.Studio.Chores.Keys.Concat(s.Studio.OwnerTasks.Where(t => t.Status == OwnerTaskStatus.Open).Select(t => t.DedupeKey))
                    .Where(k => k.StartsWith("retire:", StringComparison.Ordinal)).Distinct().ToList();
                var found = new List<(string Key, string? Game, ProductRecord? Product, long? Universe)>();
                foreach (var key in keys)
                {
                    long id = long.TryParse(key["retire:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;
                    var owner = s.Games.Values.FirstOrDefault(g => g.Products.Any(p => p.RobloxId == id));
                    var product = owner?.Products.First(p => p.RobloxId == id);
                    // The experience it sells in: its own game's, else the game that took the experience over (the task's).
                    long? universe = owner?.Slot?.UniverseId ?? s.Game(s.Studio.OwnerTasks.FirstOrDefault(t => t.DedupeKey == key)?.GameId)?.Slot?.UniverseId;
                    found.Add((key, owner?.GameId, product == null ? null : OmniBloxStore.Clone(product), universe));
                }
                return found;
            });
            foreach (var (key, game, product, universe) in pending)
            {
                if (game == null || product == null || universe == null || product.RetiredUtc != null)
                {
                    Succeeded(ctx, key); // nothing left to take off sale
                    continue;
                }
                if (Due(ctx, key)) await RetireAsync(ctx, game, product, universe.Value, ct);
            }
        }

        private static async Task RetireAsync(StudioContext ctx, string oldGame, ProductRecord product, long universeId, CancellationToken ct)
        {
            string key = RetireKey(product.RobloxId!.Value);
            string what = product.Kind == ProductKind.GamePass ? "game pass" : "developer product";
            // Only "not for sale": the old name and description are not sent again.
            var spec = new ProductSpec { Name = product.Name, Description = "", PriceRobux = Math.Max(1, product.PriceRobux), IsForSale = false, OnlySaleState = true };
            try
            {
                if (product.Kind == ProductKind.GamePass) await ctx.Cloud.UpdateGamePassAsync(universeId, product.RobloxId.Value, spec, ct);
                else await ctx.Cloud.UpdateDeveloperProductAsync(universeId, product.RobloxId.Value, spec, ct);
            }
            catch (RobloxApiException ex) when (ex.IsNotFound)
            {
                // Already gone.
            }
            catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or TimeoutException)
            {
                var state = Failed(ctx, key, ex.Message);
                ctx.Event(EventLevel.Warning, "launch.retire", $"Could not take \"{product.Name}\" off sale yet ({ex.Message}); retrying {RetryIn(state, ctx.Clock())}.", oldGame);
                if (Stuck(state, ctx.Clock()))
                    ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.Other, key, $"Take \"{product.Name}\" off sale",
                        $"An abandoned game left this {what} (id {product.RobloxId}) on sale in experience {universeId}, and Roblox has refused to change it since {state.FirstFailureUtc:yyyy-MM-dd HH:mm} UTC ({ex.Message}). " +
                        "The studio keeps trying; if it is urgent, set it to not for sale in the Creator Dashboard.", oldGame, ctx.Clock()));
                return;
            }
            ctx.Update(oldGame, g =>
            {
                if (g.Products.FirstOrDefault(p => p.Key == product.Key) is { } p) { p.Retired = true; p.RetiredUtc = ctx.Clock(); }
            });
            Succeeded(ctx, key);
            ctx.Event(EventLevel.Info, "launch.retired", $"Took \"{product.Name}\" (from an abandoned game in experience {universeId}) off sale.", oldGame);
        }

        // ─────────────── icon and store thumbnails ───────────────

        private static string? IconPath(StudioContext ctx, GameDossier game) =>
            game.Store.IconFile is { } file && File.Exists(Path.Combine(ctx.Store.MediaDirectory(game.GameId), file)) ? Path.Combine(ctx.Store.MediaDirectory(game.GameId), file) : null;

        private static bool IconPending(StudioContext ctx, GameDossier game) =>
            game.Slot != null && (IconPath(ctx, game) is { } path && Hash(File.ReadAllBytes(path)) != game.Publish.IconUploadedHash || ThumbnailsPending(ctx, game).Count > game.Publish.StoreThumbnailsUploaded);

        /// <summary>The store thumbnails to add through the session: the ones the homepage-thumbnail API refused at launch (or an older launch left in its icon task).</summary>
        private static List<string> ThumbnailsPending(StudioContext ctx, GameDossier game)
        {
            bool wanted = game.Publish.StoreThumbnailsPending || ctx.Store.Read(s => s.Studio.OwnerTasks.Any(t =>
                t.Status == OwnerTaskStatus.Open && t.DedupeKey == IconKey(game.GameId) && t.Files.Any(f => game.Store.ThumbnailFiles.Contains(f))));
            return wanted ? game.Store.ThumbnailFiles.Where(f => File.Exists(Path.Combine(ctx.Store.MediaDirectory(game.GameId), f))).ToList() : new List<string>();
        }

        /// <summary>Uploads the game's icon (and any store thumbnails still owed) through the owner's session. True when nothing is left to upload.</summary>
        public static async Task<bool> IconAsync(StudioContext ctx, string gameId, CancellationToken ct)
        {
            string key = IconKey(gameId);
            var game = ctx.Game(gameId)!;
            if (game.Slot is not { } slot) return false;
            string? iconPath = IconPath(ctx, game);
            byte[]? icon = iconPath == null ? null : File.ReadAllBytes(iconPath);
            bool iconOwed = icon != null && Hash(icon) != game.Publish.IconUploadedHash;
            var thumbnails = ThumbnailsPending(ctx, game);
            int thumbnailsUp = Math.Min(game.Publish.StoreThumbnailsUploaded, thumbnails.Count);
            if (!iconOwed && thumbnailsUp >= thumbnails.Count)
            {
                Succeeded(ctx, key);
                return true;
            }

            var files = new List<string>();
            if (iconOwed) files.Add(game.Store.IconFile!);
            files.AddRange(thumbnails.Skip(thumbnailsUp));
            string title = $"Upload the {(iconOwed ? "icon" : "")}{(iconOwed && thumbnails.Count > thumbnailsUp ? " and " : "")}{(thumbnails.Count > thumbnailsUp ? "thumbnails" : "")} for \"{game.Store.Title}\"";
            string manual = $"Upload the prepared file(s) in the Creator Dashboard for experience {slot.UniverseId}.";

            if (ctx.Session is not { HasSession: true } session)
            {
                ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.UploadIcon, key, title,
                    $"Roblox has no API key for an experience's icon or store thumbnails; they need a signed-in session. {manual}\n{SessionHint}", gameId, ctx.Clock(), files));
                return false;
            }
            if (!Due(ctx, key)) return false;
            try
            {
                if (iconOwed)
                {
                    await session.UploadGameIconAsync(slot.UniverseId, icon!, ct);
                    string hash = Hash(icon!);
                    ctx.Update(gameId, g => { g.Publish.IconUploadedHash = hash; g.Publish.IconUploadedUtc = ctx.Clock(); });
                }
                for (int i = thumbnailsUp; i < thumbnails.Count; i++)
                {
                    await session.UploadGameThumbnailAsync(slot.UniverseId, File.ReadAllBytes(Path.Combine(ctx.Store.MediaDirectory(gameId), thumbnails[i])), ct);
                    int done = i + 1;
                    ctx.Update(gameId, g => g.Publish.StoreThumbnailsUploaded = done);
                }
                if (thumbnails.Count > 0) ctx.Update(gameId, g => g.Publish.StoreThumbnailsPending = false);
            }
            catch (RobloxSessionException ex)
            {
                SessionRefused(ctx, ex);
                return false;
            }
            catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or TimeoutException or IOException or Newtonsoft.Json.JsonException)
            {
                var state = Failed(ctx, key, ex.Message);
                ctx.Event(EventLevel.Warning, "store.icon", $"The icon upload was not accepted yet ({ex.Message}); retrying {RetryIn(state, ctx.Clock())}.", gameId);
                if (Stuck(state, ctx.Clock()))
                    ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.UploadIcon, key, title,
                        $"Roblox has refused the upload since {state.FirstFailureUtc:yyyy-MM-dd HH:mm} UTC: {ex.Message}\nThe studio keeps trying. {manual}", gameId, ctx.Clock(), files));
                return false;
            }
            Succeeded(ctx, key);
            Succeeded(ctx, SessionKey);
            ctx.Event(EventLevel.Success, "store.icon", $"Uploaded {(iconOwed ? "the icon" : "")}{(iconOwed && thumbnails.Count > thumbnailsUp ? " and " : "")}{(thumbnails.Count > thumbnailsUp ? $"{thumbnails.Count - thumbnailsUp} store thumbnail(s)" : "")} for \"{game.Store.Title}\" to experience {slot.UniverseId}.", gameId);
            return true;
        }

        // ─────────────── maturity questionnaire ───────────────

        /// <summary>The content notes the rating rests on, by hash (the monetization line's prices change without changing the content).</summary>
        internal static string NotesHash(IEnumerable<string> notes) =>
            MaturityQuestionnaire.FactsHash(string.Join("\n", notes.Where(n => !n.StartsWith("Monetization:", StringComparison.Ordinal))));

        internal static bool QuestionnairePending(GameDossier game)
        {
            if (game.Slot == null) return false;
            string hash = NotesHash(ComplianceChecker.Check(game.Store, game.Products, game.Brief).QuestionnaireNotes);
            // Answered for these facts: published, or stopped for the owner's decision.
            return game.Publish.Questionnaire is not { } q || q.FactsHash != hash;
        }

        /// <summary>
        /// Answers Roblox's Maturity &amp; Compliance questionnaire for the game and publishes the rating, through the
        /// owner's session. Nothing is published when Roblox's preview rates the answers above what the game's
        /// content allows; the owner gets the answers and the preview to decide. True when the rating is published.
        /// </summary>
        public static async Task<bool> QuestionnaireAsync(StudioContext ctx, string gameId, CancellationToken ct)
        {
            string key = QuestionnaireKey(gameId);
            var game = ctx.Game(gameId)!;
            if (game.Slot is not { } slot) return false;
            var compliance = ComplianceChecker.Check(game.Store, game.Products, game.Brief);
            string hash = NotesHash(compliance.QuestionnaireNotes);
            if (game.Publish.Questionnaire is { } prior && prior.FactsHash == hash)
            {
                if (prior.Published) Succeeded(ctx, key);
                return prior.Published;
            }
            string prepared = "Prepared honest answers:\n- " + string.Join("\n- ", compliance.QuestionnaireNotes);
            string title = $"Answer the maturity questionnaire for \"{game.Store.Title}\"";

            if (ctx.Session is not { HasSession: true } session)
            {
                FileQuestionnaireTask(ctx, game, "Roblox has no API key for the questionnaire; it needs a signed-in session.\n" + SessionHint);
                return false;
            }
            if (!Due(ctx, key)) return false;

            QuestionnaireRecord record;
            try
            {
                record = await AnswerAsync(ctx, session, game, slot, compliance, ct);
            }
            catch (RobloxSessionException ex)
            {
                SessionRefused(ctx, ex);
                return false;
            }
            catch (Exception ex) when (ex is QuestionnaireException or RobloxApiException or HttpRequestException or TimeoutException or Newtonsoft.Json.JsonException)
            {
                var state = Failed(ctx, key, ex.Message);
                ctx.Event(EventLevel.Warning, "store.questionnaire", $"The maturity questionnaire is not done yet ({ex.Message}); retrying {RetryIn(state, ctx.Clock())}.", gameId);
                if (Stuck(state, ctx.Clock()))
                    ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.Questionnaire, key, title,
                        $"{prepared}\n\nThe studio has not managed to submit it since {state.FirstFailureUtc:yyyy-MM-dd HH:mm} UTC: {ex.Message}\nIt keeps trying; you can also answer it in the Creator Dashboard for experience {slot.UniverseId}.", gameId, ctx.Clock()));
                return false;
            }

            record.FactsHash = hash;
            ctx.Update(gameId, g => g.Publish.Questionnaire = record);
            Succeeded(ctx, SessionKey); // the session worked, whatever the answers came to
            if (record.Published)
            {
                Succeeded(ctx, key);
                ctx.Event(EventLevel.Success, "store.questionnaire", $"Answered Roblox's maturity questionnaire for \"{game.Store.Title}\": rated {record.AgeRating ?? "(no label)"}" + (record.MinimumAge is int age ? $" (ages {age}+)" : "") + ".", gameId);
                return true;
            }
            // Roblox would rate it above what the content allows: the owner decides, with everything in hand.
            ctx.Store.Mutate(s =>
            {
                if (s.Studio.Chores.Remove(key)) s.MarkStudio();
                s.FileOwnerTask(OwnerTaskKind.Questionnaire, key, title,
                    $"The studio answered the questionnaire but did not publish the rating: {record.StoppedReason}\n" +
                    $"Its answers are saved in the Creator Dashboard for experience {slot.UniverseId}: check them there and submit, or change them.\n\n" +
                    $"{prepared}\n\nAnswers given:\n- " + string.Join("\n- ", record.Answers.Take(60)), gameId, ctx.Clock());
            });
            ctx.Event(EventLevel.Warning, "store.questionnaire", $"Did not publish the questionnaire for \"{game.Store.Title}\": {record.StoppedReason}", gameId);
            return false;
        }

        private static async Task<QuestionnaireRecord> AnswerAsync(StudioContext ctx, RobloxSessionClient session, GameDossier game, ExperienceSlot slot, ComplianceReport compliance, CancellationToken ct)
        {
            long universe = slot.UniverseId;
            string mainId = await session.GetLatestQuestionnaireIdAsync(universe, ct);
            var forms = new List<Questionnaire> { await session.GetQuestionnaireAsync(mainId, ct) };
            if (await session.GetAdditionalQuestionnaireAsync(universe, ct) is { } additional && additional.Id != mainId) forms.Add(additional);

            var answers = await PickAnswersAsync(ctx, session, game, compliance, forms, ct);
            var record = new QuestionnaireRecord
            {
                Utc = ctx.Clock(),
                QuestionnaireIds = forms.Select(f => f.Id).ToList(),
                Answers = forms.SelectMany(f => answers[f.Id].Readable).ToList(),
            };
            int ceiling = MaturityQuestionnaire.AgeCeiling(compliance.QuestionnaireNotes);

            // Roblox's rating for these answers, before anything is saved.
            var preview = await session.PreviewAsync(universe, mainId, answers[mainId].Answers, ct);
            if (Refusal(preview, ceiling) is string stop) return Stopped(record, preview, stop);

            if (forms.Count == 1)
            {
                await session.SubmitSingleAsync(universe, mainId, answers[mainId].Answers, ct);
            }
            else
            {
                foreach (var form in forms)
                {
                    var check = await session.SubmitAsync(universe, form.Id, answers[form.Id].Answers, ct);
                    if (!check.Valid)
                        throw new QuestionnaireException($"Roblox refused the answers to {form.Id}: " + string.Join("; ", check.Failures.Select(f => $"{f.QuestionId}: {f.Reason}").DefaultIfEmpty("no reason given")));
                }
                // Together the questionnaires may rate higher than the first alone.
                preview = await session.PreviewSubmittedAsync(universe, record.QuestionnaireIds, ct);
                if (Refusal(preview, ceiling) is string stopAll) return Stopped(record, preview, stopAll);
                await session.PublishAsync(universe, record.QuestionnaireIds, ct);
            }
            record.Published = true;
            record.AgeRating = preview.AgeRating;
            record.MinimumAge = preview.MinimumAge;
            return record;
        }

        /// <summary>
        /// The owner's task to answer the questionnaire in the Creator Dashboard, with the prepared answers. While Roblox
        /// shows the experience unrated it says what that costs: nobody but the owner can play the game, which the old
        /// task never said (the first launched game sat unplayable for four days behind a routine-looking task).
        /// </summary>
        internal static void FileQuestionnaireTask(StudioContext ctx, GameDossier game, string how)
        {
            var compliance = ComplianceChecker.Check(game.Store, game.Products, game.Brief);
            string prepared = "Prepared honest answers:\n- " + string.Join("\n- ", compliance.QuestionnaireNotes);
            bool unrated = PlayerReach.ShowsUnrated(game);
            string title = unrated
                ? $"Answer the maturity questionnaire: nobody can play \"{game.Store.Title}\" until you do"
                : $"Answer the maturity questionnaire for \"{game.Store.Title}\"";
            string stakes = unrated
                ? $"Roblox shows experience {game.Slot?.UniverseId} as unrated, and it lets nobody but the creator play an unrated experience: the game gets no players at all until this is answered " +
                  "(Creator Dashboard → the experience → Maturity & Compliance questionnaire). The studio sees the rating appear by itself and closes this task.\n\n"
                : "";
            ctx.Store.Mutate(s =>
            {
                var task = s.FileOwnerTask(OwnerTaskKind.Questionnaire, QuestionnaireKey(game.GameId), title, $"{stakes}{prepared}\n\n{how}", game.GameId, ctx.Clock());
                if (task.Title != title)
                {
                    task.Title = title;
                    s.MarkStudio();
                }
            });
        }

        private static QuestionnaireRecord Stopped(QuestionnaireRecord record, QuestionnairePreview preview, string reason)
        {
            record.StoppedReason = reason;
            record.AgeRating = preview.AgeRating;
            record.MinimumAge = preview.MinimumAge;
            return record;
        }

        /// <summary>Why a previewed rating must not be published (null when it fits the game's content).</summary>
        internal static string? Refusal(QuestionnairePreview preview, int ceiling)
        {
            if (preview.Error != null) return "Roblox's preview reported a problem: " + preview.Error;
            if (preview.SubmitDisabled) return "Roblox's preview does not allow these answers to be submitted" + (preview.Warning != null ? ": " + preview.Warning : ".");
            if (preview.MinimumAge is not int age) return "Roblox's preview gave no age rating for these answers, so the studio cannot check it against the game's content.";
            if (age > ceiling)
                return $"Roblox would rate the game {preview.AgeRating ?? "?"} (ages {age}+), above what its content allows ({(ceiling < 9 ? "all ages" : "9+ at most")}), so the answers say more than the game's content does.";
            if (preview.RestrictedCountries.Count > 0)
                return $"The answers would make the game unavailable in {string.Join(", ", preview.RestrictedCountries.Take(8))}{(preview.RestrictedCountries.Count > 8 ? "…" : "")}.";
            return null;
        }

        /// <summary>The model maps the game's facts onto Roblox's questions; every shown question must be answered with its own options, and Roblox's own check must pass, or the model is told what to fix.</summary>
        private static async Task<Dictionary<string, QuestionnaireResolution>> PickAnswersAsync(StudioContext ctx, RobloxSessionClient session, GameDossier game,
            ComplianceReport compliance, List<Questionnaire> forms, CancellationToken ct)
        {
            string llmSession = "omniblox-questionnaire-" + game.GameId + "-" + Guid.NewGuid().ToString("N")[..6];
            ctx.Llm.StartSession(llmSession, AgentPrompts.QuestionnaireAnswerer);
            try
            {
                ctx.Llm.AppendUser(llmSession, "FACTS\n" + MaturityQuestionnaire.Facts(game, compliance) + "\n\n" +
                    string.Join("\n\n", forms.Select(MaturityQuestionnaire.Render)) +
                    $"\n\nAnswer every question shown in {(forms.Count == 1 ? "the questionnaire" : "both questionnaires")}.");
                var picks = new Dictionary<string, ChosenAnswer>(StringComparer.Ordinal);
                List<string> problems = new();
                for (int round = 1; round <= 3; round++)
                {
                    var turn = await ctx.Llm.QueryAsync(llmSession, Array.Empty<ToolDefinition>(), new LlmCallOptions { MaxTokens = 8000, Thinking = "medium", WorkClass = AIRouterWorkClass.OneShot }, ct);
                    ctx.AddUsage(game.GameId, new CycleReport { Turns = 1, PromptTokens = turn.PromptTokens, CachedTokens = turn.CachedTokens, CompletionTokens = turn.CompletionTokens });
                    if (!turn.Success) throw new QuestionnaireException("the model call failed: " + turn.Error);
                    // A correction may resend only the questions that change.
                    foreach (var (id, pick) in MaturityQuestionnaire.ParseReply(turn.Text)) picks[id] = pick;

                    var resolved = forms.ToDictionary(f => f.Id, f => MaturityQuestionnaire.Resolve(f, picks), StringComparer.Ordinal);
                    problems = resolved.Values.SelectMany(r => r.Problems).ToList();
                    if (problems.Count == 0 && resolved.Values.Any(r => r.Answers.Count == 0)) problems.Add("no answers were readable; reply with the JSON object only");
                    if (problems.Count == 0)
                    {
                        foreach (var form in forms)
                        {
                            QuestionnaireCheck check;
                            try { check = await session.ValidateAsync(form.Id, resolved[form.Id].Answers, ct); }
                            catch (RobloxApiException ex) when (form.Id != forms[0].Id && (int)ex.Status is >= 400 and < 500) { continue; } // only the first questionnaire is checked this way; the second is checked when submitted
                            if (!check.Valid)
                                problems.AddRange(check.Failures.Count > 0 ? check.Failures.Select(f => $"Roblox refused the answer to {f.QuestionId}: {f.Reason}") : new[] { $"Roblox refused the answers to {form.Id} without saying why" });
                        }
                        if (problems.Count == 0) return resolved;
                    }
                    ctx.Llm.AppendUser(llmSession, "These answers cannot be submitted yet:\n- " + string.Join("\n- ", problems.Take(40)) +
                        "\nReply with the corrected answers in the same JSON shape (you may send only the questions that change).");
                }
                throw new QuestionnaireException("the answers were still not complete after three tries: " + string.Join("; ", problems.Take(5)));
            }
            finally
            {
                ctx.Llm.EndSession(llmSession);
            }
        }

        // ─────────────── the session ───────────────

        /// <summary>Roblox no longer accepts the owner's session: one task to sign in again (every chore waits for it).</summary>
        internal static void SessionRefused(StudioContext ctx, RobloxSessionException ex)
        {
            ctx.Store.Mutate(s =>
            {
                bool filed = s.Studio.OwnerTasks.Any(t => t.Status == OwnerTaskStatus.Open && t.DedupeKey == SessionKey);
                s.FileOwnerTask(OwnerTaskKind.RobloxSession, SessionKey, "Sign OmniBlox in to Roblox again",
                    $"{ex.Message}\nUntil then the studio cannot create experiences, upload icons and thumbnails or answer the maturity questionnaire; everything waiting is done once the session works.\n{SessionHint}", null, ctx.Clock());
                if (!filed) s.AddEvent(EventLevel.Warning, "store.session", "Roblox refused the owner's session: " + ex.Message, null, ctx.Clock());
            });
        }

        // ─────────────── bookkeeping ───────────────

        internal static bool Due(StudioContext ctx, string key) =>
            ctx.Store.Read(s => !s.Studio.Chores.TryGetValue(key, out var c) || c.NextAttemptUtc == null || c.NextAttemptUtc <= ctx.Clock());

        /// <summary>A chore waiting out a failure is due now (the owner asked for it, or what it waited for arrived).</summary>
        internal static void MakeDue(StudioContext ctx, string key) => ctx.Store.Mutate(s =>
        {
            if (s.Studio.Chores.TryGetValue(key, out var c) && c.NextAttemptUtc != null) { c.NextAttemptUtc = null; s.MarkStudio(); }
        });

        internal static void Succeeded(StudioContext ctx, string key) => ctx.Store.Mutate(s =>
        {
            if (s.Studio.Chores.Remove(key)) s.MarkStudio();
            s.ResolveOwnerTasks(key, ctx.Clock());
        });

        /// <summary>Counts a failure and schedules the retry: 5 minutes, doubling up to 6 hours (or <paramref name="maxWait"/>).</summary>
        internal static ChoreState Failed(StudioContext ctx, string key, string error, TimeSpan? maxWait = null) => ctx.Store.Mutate(s =>
        {
            var now = ctx.Clock();
            if (!s.Studio.Chores.TryGetValue(key, out var c)) s.Studio.Chores[key] = c = new ChoreState();
            c.Failures++;
            c.FirstFailureUtc ??= now;
            c.LastError = error.Length <= 500 ? error : error[..500];
            c.NextAttemptUtc = now + TimeSpan.FromMinutes(Math.Min((maxWait ?? MaxRetry).TotalMinutes, FirstRetry.TotalMinutes * Math.Pow(2, Math.Min(10, c.Failures - 1))));
            s.MarkStudio();
            return new ChoreState { Failures = c.Failures, FirstFailureUtc = c.FirstFailureUtc, NextAttemptUtc = c.NextAttemptUtc, LastError = c.LastError };
        });

        private static bool Stuck(ChoreState state, DateTime now) => state.FirstFailureUtc is DateTime first && now - first >= EscalateAfter;

        internal static string RetryIn(ChoreState state, DateTime now) =>
            state.NextAttemptUtc is DateTime next ? $"in {Math.Max(1, (next - now).TotalMinutes):0} min" : "later";

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16];
    }
}
