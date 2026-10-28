using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Marketing;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>The owner's action cannot start (or stopped) for a reason they can act on; its message is shown as is.</summary>
    public sealed class GameActionRefused : Exception
    {
        public GameActionRefused(string message) : base(message) { }
    }

    /// <summary>
    /// What the owner can do to a game's place on Roblox from the dashboard, beyond steering the agents:
    /// <list type="bullet">
    /// <item>sync: put everything on Roblox again (products, the current build, store page, icon, questionnaire);</item>
    /// <item>private / public: the experience's visibility (public only with a maturity rating);</item>
    /// <item>takedown: private, its products off sale, maintenance stopped, and optionally archived (Roblox's delete);</item>
    /// <item>move: relaunch the game in a fresh experience, the studio's new one or one the owner names, e.g. on the
    /// account they switched to; the old one is made private, its products come off sale and it is never reused;</item>
    /// <item>delegate: "do it yourself" on an owner task, done by the studio when it can or handed to the game's agent.</item>
    /// </list>
    /// Each runs in the background, one per game at a time and never under a running agent cycle, and is recorded step by
    /// step on the game (<see cref="GameDossier.OwnerAction"/>) for the game page to follow.
    /// </summary>
    public sealed class GameActions
    {
        public static readonly string[] Kinds = { "sync", "private", "public", "takedown", "move" };
        private const string StudioJob = "(studio)";

        private readonly StudioContext ctx;
        private readonly Func<string, CancellationToken, Task> relaunch;
        private readonly object sync = new();
        private readonly Dictionary<string, Task> running = new(StringComparer.Ordinal);

        /// <param name="relaunch">Runs one launch step for a game (the orchestrator's), so a moved game goes live without waiting for the studio's loop.</param>
        public GameActions(StudioContext ctx, Func<string, CancellationToken, Task> relaunch)
        {
            this.ctx = ctx;
            this.relaunch = relaunch;
        }

        /// <summary>The job running for a game now, if any (tests await it).</summary>
        public Task? Running(string gameId)
        {
            lock (sync) return running.TryGetValue(gameId, out var t) ? t : null;
        }

        /// <summary>The studio-level job running now (an experience made for an owner task), if any.</summary>
        public Task? RunningForStudio() => Running(StudioJob);

        // ─────────────── the game actions ───────────────

        /// <summary>Starts an action on a game; its title. Throws <see cref="GameActionRefused"/> when it cannot start.</summary>
        /// <param name="archive">takedown: also archive the experience (needs the owner's Roblox account).</param>
        /// <param name="target">move: "new" (an experience the studio creates) or the owner's "universeId:placeId".</param>
        public string Start(string gameId, string kind, bool archive = false, string? target = null)
        {
            var game = ctx.Game(gameId) ?? throw new GameActionRefused("No game " + gameId);
            if (!Kinds.Contains(kind)) throw new GameActionRefused("Unknown action " + kind + "; one of " + string.Join(", ", Kinds) + ".");
            if (!ctx.Cloud.HasKey) throw new GameActionRefused("These actions need the Roblox API key (Roblox account card).");
            string name = game.DisplayTitle;
            ExperienceSlot? moveTo = null;
            string title;
            switch (kind)
            {
                case "sync":
                    if (game.Slot == null || game.Publish.LastPublishedVersion == null || game.Stage is not (GameStage.Live or GameStage.Sunset))
                        throw new GameActionRefused("Only a launched game can be updated on Roblox; a game in production gets there through its launch.");
                    title = $"Update \"{name}\" on Roblox";
                    break;
                case "private":
                    if (game.Slot == null) throw new GameActionRefused("It has no experience on Roblox yet.");
                    title = $"Make \"{name}\" private";
                    break;
                case "public":
                    if (game.Slot == null || game.Publish.LastPublishedVersion == null)
                        throw new GameActionRefused("It has never been published; it goes public through its launch.");
                    title = $"Make \"{name}\" public";
                    break;
                case "takedown":
                    if (game.Slot == null) throw new GameActionRefused("It has no experience on Roblox to take down.");
                    if (archive && ctx.Session is not { HasSession: true })
                        throw new GameActionRefused("Archiving needs your Roblox account connected (no API key can archive an experience). Take it down without archiving, or connect the account first.");
                    title = archive ? $"Take \"{name}\" down and archive its experience" : $"Take \"{name}\" down";
                    break;
                default: // move
                    if (game.Stage is not (GameStage.Launch or GameStage.Live or GameStage.Sunset) || !StageGates.MarketingReady(game))
                        throw new GameActionRefused("Only a game with its store page made (Launch or later) can be moved; one in production gets its experience at launch.");
                    if (string.IsNullOrWhiteSpace(target) || target.Trim().Equals("new", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Slots.WhyNotCreating(ctx) is { } why) throw new GameActionRefused("The studio cannot make a new experience now: " + why + ".");
                        title = $"Move \"{name}\" to a new experience";
                    }
                    else
                    {
                        moveTo = ExperienceSlot.Parse(target) ?? throw new GameActionRefused("Give the experience as universeId:placeId (from its Creator Dashboard page).");
                        long universe = moveTo.UniverseId;
                        if (game.Slot?.UniverseId == universe) throw new GameActionRefused("That is the experience it is in now.");
                        if (ctx.Store.Read(s => s.Studio.RetiredExperiences.Contains(universe)))
                            throw new GameActionRefused($"Experience {universe} was retired (taken down, archived or left behind by a move); give another.");
                        string? holder = ctx.Store.Read(s => s.Games.Values.FirstOrDefault(g => g.GameId != gameId && g.Stage != GameStage.Abandoned && g.Slot?.UniverseId == universe)?.DisplayTitle);
                        if (holder != null) throw new GameActionRefused($"Experience {universe} belongs to \"{holder}\".");
                        title = $"Move \"{name}\" to experience {universe}";
                    }
                    break;
            }
            return Begin(gameId, kind, title, kind switch
            {
                "sync" => SyncAsync,
                "private" => (id, step, ct) => VisibilityAsync(id, false, step, ct),
                "public" => (id, step, ct) => VisibilityAsync(id, true, step, ct),
                "takedown" => (id, step, ct) => TakeDownAsync(id, archive, step, ct),
                _ => (id, step, ct) => MoveAsync(id, moveTo, step, ct),
            });
        }

        private delegate Task<string> Work(string gameId, Action<string> step, CancellationToken ct);

        /// <summary>Records the action on the game and runs it in the background.</summary>
        private string Begin(string gameId, string kind, string title, Work work)
        {
            lock (sync)
            {
                if (running.ContainsKey(gameId))
                    throw new GameActionRefused($"\"{ctx.Game(gameId)?.DisplayTitle}\" is already running an action of yours; wait for it to finish.");
                if (ctx.Live.Peek(gameId) is { Running: true } cycle)
                    throw new GameActionRefused($"A {cycle.Role} cycle is working on this game now; try again when it finishes (or pause the game first).");
                var now = ctx.Clock();
                ctx.Update(gameId, g => g.OwnerAction = new OwnerActionRecord { Kind = kind, Title = title, StartedUtc = now, Running = true });
                ctx.Event(EventLevel.Info, "owner.action", title + "…", gameId);
                running[gameId] = Task.Run(() => RunAsync(gameId, work));
            }
            return title;
        }

        private async Task RunAsync(string gameId, Work work)
        {
            bool ok = false;
            string message;
            try
            {
                message = await work(gameId, text => ctx.Update(gameId, g => g.OwnerAction?.Steps.Add(text)), CancellationToken.None);
                ok = true;
            }
            catch (GameActionRefused ex) { message = ex.Message; }
            catch (RobloxSessionException ex)
            {
                if (!ex.Missing) StorePageChores.SessionRefused(ctx, ex);
                message = ex.Message;
            }
            catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or TimeoutException or InvalidOperationException or Newtonsoft.Json.JsonException)
            {
                message = "Roblox refused it: " + ex.Message;
            }
            catch (Exception ex)
            {
                ctx.Log($"[OmniBlox] owner action on {gameId} failed: {ex}");
                message = "Something went wrong: " + ex.Message;
            }
            var now = ctx.Clock();
            try
            {
                ctx.Store.Mutate(s =>
                {
                    if (s.Game(gameId) is not { } g || g.OwnerAction is not { } action) return;
                    action.Running = false;
                    action.Ok = ok;
                    action.FinishedUtc = now;
                    action.Message = message;
                    s.Touch(g, now);
                    s.AddEvent(ok ? EventLevel.Success : EventLevel.Warning, "owner.action", $"{action.Title}: {message}", gameId, now);
                });
            }
            finally
            {
                lock (sync) running.Remove(gameId);
            }
        }

        private async Task<string> SyncAsync(string gameId, Action<string> step, CancellationToken ct)
        {
            var slot = ctx.Game(gameId)!.Slot!;
            var (_, refused) = await LaunchSequence.SyncProductsAsync(ctx, gameId, slot, all: true, ct);
            if (refused != null) throw new GameActionRefused(refused + " (see the owner task).");
            step($"Products: {ctx.Game(gameId)!.Products.Count(p => !p.Retired)} updated on Roblox.");
            var (shipped, message) = await LiveOps.ShipUpdateAsync(ctx, gameId, "The owner asked for the game to be put on Roblox again.", ct);
            if (!shipped) throw new GameActionRefused("Publishing the build failed: " + message);
            step("Published the current build: " + message);
            await ApplyStorePageAsync(gameId, slot, ct);
            step("Store page title and description applied.");
            await StorePageAsync(gameId, reuploadIcon: true, step, ct);
            await ReachAsync(gameId, step, ct);
            return "Everything is on Roblox again.";
        }

        private async Task<string> VisibilityAsync(string gameId, bool makePublic, Action<string> step, CancellationToken ct)
        {
            var game = ctx.Game(gameId)!;
            var slot = game.Slot!;
            var now = ctx.Clock();
            if (!makePublic)
            {
                await ctx.Cloud.UpdateUniverseAsync(slot.UniverseId, new UniverseUpdate { Visibility = "PRIVATE" }, ct);
                ctx.Update(gameId, g => g.Publish.IsPublic = false);
                step($"Experience {slot.UniverseId} is private: nobody but you can join.");
                return "Private.";
            }

            if (await PlayerReach.RatingGateAsync(ctx, gameId, slot, ct) is { } unrated)
                throw new GameActionRefused("Not made public: it has no maturity rating, and Roblox lets nobody play an unrated experience. " +
                                            "Answer its questionnaire (connect your Roblox account and the studio does it), then try again.");
            // A game taken down earlier: its products go back on sale with it.
            bool wasTakenDown = game.Stage == GameStage.Sunset && game.SunsetReason?.StartsWith(TakenDown, StringComparison.Ordinal) == true;
            if (wasTakenDown)
            {
                ctx.Update(gameId, g => { foreach (var p in g.Products.Where(p => p.RobloxId != null)) { p.Retired = false; p.RetiredUtc = null; } });
                var (_, refused) = await LaunchSequence.SyncProductsAsync(ctx, gameId, slot, all: true, ct);
                if (refused != null) throw new GameActionRefused(refused + " (see the owner task).");
                step("Its products are on sale again.");
            }
            await ctx.Cloud.UpdateUniverseAsync(slot.UniverseId, new UniverseUpdate { Visibility = "PUBLIC", DesktopEnabled = true, MobileEnabled = true, TabletEnabled = true, ConsoleEnabled = true }, ct);
            ctx.Update(gameId, g =>
            {
                g.Publish.IsPublic = true;
                g.Publish.WentPublicUtc ??= now;
                if (wasTakenDown)
                {
                    g.Stage = GameStage.Live;
                    g.StageEnteredUtc = now;
                    g.SunsetReason = null;
                    g.NextMaintenanceUtc = now.AddDays(1);
                }
            });
            step($"Experience {slot.UniverseId} is public" + (wasTakenDown ? ", and the game is live and maintained again." : "."));
            await ReachAsync(gameId, step, ct);
            return "Public.";
        }

        private const string TakenDown = "taken down by the owner";

        private async Task<string> TakeDownAsync(string gameId, bool archive, Action<string> step, CancellationToken ct)
        {
            var slot = ctx.Game(gameId)!.Slot!;
            await ctx.Cloud.UpdateUniverseAsync(slot.UniverseId, new UniverseUpdate { Visibility = "PRIVATE" }, ct);
            step($"Experience {slot.UniverseId} is private.");
            step(await ProductsOffSaleAsync(gameId, slot, ct));
            if (archive)
            {
                await ctx.Session!.ArchiveExperienceAsync(slot.UniverseId, ct);
                step($"Experience {slot.UniverseId} is archived (restore it from the Creator Dashboard → Archived); the studio never uses it again.");
            }
            var now = ctx.Clock();
            ctx.Store.Mutate(s =>
            {
                var g = s.Game(gameId)!;
                g.Publish.IsPublic = false;
                if (g.Stage == GameStage.Live)
                {
                    g.Stage = GameStage.Sunset;
                    g.StageEnteredUtc = now;
                    g.SunsetReason = TakenDown + (archive ? " (experience archived)" : "");
                    g.NextMaintenanceUtc = null;
                    g.UrgentMaintenance = false;
                }
                if (archive) Retire(s, g, slot.UniverseId);
                s.Touch(g, now);
            });
            return archive ? "Taken down and archived." : "Taken down: private, nothing on sale, no more maintenance. Make it public again to bring it back.";
        }

        private async Task<string> MoveAsync(string gameId, ExperienceSlot? moveTo, Action<string> step, CancellationToken ct)
        {
            var old = ctx.Game(gameId)!.Slot;
            if (old != null)
            {
                try
                {
                    await ctx.Cloud.UpdateUniverseAsync(old.UniverseId, new UniverseUpdate { Visibility = "PRIVATE" }, ct);
                    step($"The old experience {old.UniverseId} is private.");
                }
                catch (RobloxApiException ex) when (ex.IsAuth || ex.IsNotFound)
                {
                    step($"The API key cannot change the old experience {old.UniverseId} any more ({Clip(ex.Message, 160)}); if it is still public, make it private in the Creator Dashboard.");
                }
                step(await ProductsOffSaleAsync(gameId, old, ct));
            }

            // Everything that belonged to the old experience is forgotten; what the game is (build, store page, products) stays.
            var now = ctx.Clock();
            ctx.Store.Mutate(s =>
            {
                var g = s.Game(gameId)!;
                if (old != null) Retire(s, g, old.UniverseId);
                g.Slot = null;
                foreach (var p in g.Products) { p.RobloxId = null; p.SyncedUtc = null; p.Retired = false; p.RetiredUtc = null; }
                var pub = g.Publish;
                pub.IsPublic = false;
                pub.LastPublishedVersion = null;
                pub.LastPublishedRevision = null;
                pub.LastSavedVersion = null;
                pub.LastSavedRevision = null;
                pub.EngineTestedRevision = null;
                pub.IconUploadedHash = null;
                pub.IconUploadedUtc = null;
                pub.StoreThumbnailsPending = false;
                pub.StoreThumbnailsUploaded = 0;
                pub.Questionnaire = null;
                pub.Reach = null;
                pub.LaunchFailures = 0;
                pub.LastLaunchStep = null;
                pub.LastLaunchError = null;
                pub.OnOldAccount = false;
                g.Store.Applied = false;
                foreach (var key in new[] { StorePageChores.IconKey(gameId), StorePageChores.QuestionnaireKey(gameId) })
                {
                    s.Studio.Chores.Remove(key);
                    s.ResolveOwnerTasks(key, now);
                }
                s.MarkStudio();
                s.Touch(g, now);
            });

            ExperienceSlot slot;
            if (moveTo == null)
            {
                slot = await Slots.CreateForGameAsync(ctx, gameId, ct)
                       ?? throw new GameActionRefused("Roblox did not create the new experience: " + (Slots.WhyNotCreating(ctx) ?? ctx.Store.Read(s => s.Studio.Chores.GetValueOrDefault(Slots.CreateKey)?.LastError) ?? "no reason given") +
                                                      ". The game is off its old experience; move it again once this is fixed.");
                step($"Created experience {slot.UniverseId} (place {slot.PlaceId}) for it.");
            }
            else
            {
                slot = moveTo;
                ctx.Store.Mutate(s =>
                {
                    var g = s.Game(gameId)!;
                    s.Studio.SlotAssignments[slot.ToString()] = gameId;
                    g.Slot = new ExperienceSlot { UniverseId = slot.UniverseId, PlaceId = slot.PlaceId };
                    s.MarkStudio();
                    s.Touch(g, now);
                });
                step($"It goes into your experience {slot.UniverseId} (place {slot.PlaceId}).");
            }

            // Moving a launched game is the owner approving it again as it is: the same build, store page and products.
            ctx.Store.Mutate(s =>
            {
                var g = s.Game(gameId)!;
                string note = $"moved to experience {slot.UniverseId} by the owner";
                if (ctx.Settings.RequireBuildSignoff && g.Publish.SignoffRevision != g.Build.Revision)
                {
                    g.Publish.SignoffRevision = g.Build.Revision;
                    g.Publish.SignoffUtc = now;
                    g.Publish.Approvals.Add(new ApprovalRecord { Gate = Approvals.BuildGate, Decision = "approved", Note = note, Revision = g.Build.Revision, Utc = now });
                }
                g.Publish.ApprovedRevision = g.Build.Revision;
                g.Publish.ApprovedFingerprint = Approvals.Fingerprint(g);
                g.Publish.LaunchApprovedUtc = now;
                g.Publish.Approvals.Add(new ApprovalRecord { Gate = Approvals.LaunchGate, Decision = "approved", Note = note, Revision = g.Build.Revision, Utc = now });
                g.Publish.LaunchRequestedUtc ??= now;
                if (g.Stage != GameStage.Launch) { g.Stage = GameStage.Launch; g.StageEnteredUtc = now; }
                g.WaitingReason = null;
                g.SunsetReason = null;
                g.NextMaintenanceUtc = null;
                g.UrgentMaintenance = false;
                // The studio's loop leaves it to this action for now.
                g.NextAttemptUtc = now + Orchestrator.WaitRecheck;
                s.Touch(g, now);
                s.AddEvent(EventLevel.Info, "game.moved", $"\"{g.DisplayTitle}\" moved from experience {old?.UniverseId.ToString() ?? "none"} to {slot.UniverseId}; relaunching it there.", gameId, now);
            });

            step("Relaunching it in the new experience…");
            await relaunch(gameId, ct);
            var game = ctx.Game(gameId)!;
            if (game.Stage == GameStage.Live && game.Publish.IsPublic) return $"Live again, in experience {slot.UniverseId}.";
            string waiting = game.WaitingReason ?? game.Publish.LastLaunchError ?? "it carries on with the studio";
            return $"Moved to experience {slot.UniverseId}; the launch there is waiting: {waiting}" + (ctx.Settings.Enabled ? "." : " (switch the studio on so it carries on).");
        }

        /// <summary>An experience no game gets again; it stops being the game's.</summary>
        private static void Retire(OmniBloxState s, GameDossier g, long universeId)
        {
            if (!s.Studio.RetiredExperiences.Contains(universeId)) s.Studio.RetiredExperiences.Add(universeId);
            foreach (var key in s.Studio.SlotAssignments.Where(kv => kv.Value == g.GameId).Select(kv => kv.Key).ToList()) s.Studio.SlotAssignments.Remove(key);
            s.MarkStudio();
        }

        /// <summary>Every product the game has on sale there, taken off sale (what Roblox refuses is listed, and the rest carries on).</summary>
        private async Task<string> ProductsOffSaleAsync(string gameId, ExperienceSlot slot, CancellationToken ct)
        {
            var products = ctx.Game(gameId)!.Products.Where(p => p.RobloxId != null && !p.Retired).ToList();
            if (products.Count == 0) return "No products were on sale.";
            var failed = new List<string>();
            int done = 0;
            foreach (var product in products)
            {
                var spec = new ProductSpec { Name = product.Name, Description = "", PriceRobux = Math.Max(1, product.PriceRobux), IsForSale = false, OnlySaleState = true };
                try
                {
                    if (product.Kind == ProductKind.GamePass) await ctx.Cloud.UpdateGamePassAsync(slot.UniverseId, product.RobloxId!.Value, spec, ct);
                    else await ctx.Cloud.UpdateDeveloperProductAsync(slot.UniverseId, product.RobloxId!.Value, spec, ct);
                }
                catch (RobloxApiException ex) when (!ex.IsNotFound)
                {
                    failed.Add($"{product.Name} ({Clip(ex.Message, 120)})");
                    continue;
                }
                done++;
                var now = ctx.Clock();
                ctx.Update(gameId, g => { if (g.Products.FirstOrDefault(p => p.Key == product.Key) is { } p) { p.Retired = true; p.RetiredUtc = now; } });
            }
            return $"{done} product(s) taken off sale." + (failed.Count > 0 ? " Roblox refused " + string.Join("; ", failed) + ": set them to not for sale in the Creator Dashboard." : "");
        }

        private async Task ApplyStorePageAsync(string gameId, ExperienceSlot slot, CancellationToken ct)
        {
            var game = ctx.Game(gameId)!;
            await ctx.Cloud.UpdatePlaceAsync(slot.UniverseId, slot.PlaceId, Clip(game.Store.Title, ComplianceChecker.TitleMax), Clip(game.Store.Description, ComplianceChecker.DescriptionMax), ct);
            ctx.Update(gameId, g => g.Store.Applied = true);
        }

        /// <summary>The icon (uploaded again with <paramref name="reuploadIcon"/>) and the maturity questionnaire, through the owner's session.</summary>
        private async Task StorePageAsync(string gameId, bool reuploadIcon, Action<string> step, CancellationToken ct)
        {
            if (reuploadIcon) ctx.Update(gameId, g => g.Publish.IconUploadedHash = null);
            StorePageChores.MakeDue(ctx, StorePageChores.IconKey(gameId));
            StorePageChores.MakeDue(ctx, StorePageChores.QuestionnaireKey(gameId));
            bool session = ctx.Session is { HasSession: true };
            bool icon = await StorePageChores.IconAsync(ctx, gameId, ct);
            bool rated = await StorePageChores.QuestionnaireAsync(ctx, gameId, ct);
            if (!session) step("The icon and the maturity questionnaire wait for your Roblox account (connect it in the Roblox account card); their tasks say what to do meanwhile.");
            else
            {
                step(icon ? "Icon uploaded." : "The icon did not go up yet; the studio keeps trying.");
                step(rated ? "Maturity questionnaire answered." : "The maturity questionnaire is not done yet (see its task).");
            }
        }

        private async Task ReachAsync(string gameId, Action<string> step, CancellationToken ct)
        {
            if (!ctx.Game(gameId)!.Publish.IsPublic) return;
            var reach = await PlayerReach.CheckAsync(ctx, gameId, ct);
            step(reach?.Playable switch
            {
                true => "Roblox lets players join it.",
                false => "Nobody can play it yet: " + PlayerReach.Why(reach) + ".",
                _ => "Roblox did not say whether players can join it.",
            });
        }

        // ─────────────── "do it yourself" on an owner task ───────────────

        /// <summary>
        /// The owner's "do it yourself" on one of their tasks: the studio does it when it can (the icon and questionnaire
        /// with the owner's Roblox account, products off sale, an experience), or hands it to the game's agent as a
        /// direction. What only the owner can decide or give (approvals, the API key, a sign-in) is refused, saying why.
        /// </summary>
        public string Delegate(string taskId)
        {
            var task = ctx.Store.Read(s => s.Studio.OwnerTasks.FirstOrDefault(t => t.TaskId == taskId && t.Status == OwnerTaskStatus.Open) is { } t ? OmniBloxStore.Clone(t) : null)
                       ?? throw new GameActionRefused("That task is not open any more.");
            bool session = ctx.Session is { HasSession: true };
            switch (task.Kind)
            {
                case OwnerTaskKind.LaunchApproval or OwnerTaskKind.BuildSignoff:
                    throw new GameActionRefused("This one is your decision: approve it, or ask for changes, from the game's page.");
                case OwnerTaskKind.ApiKey or OwnerTaskKind.Creator:
                    throw new GameActionRefused("Only you can give the studio an API key: set it in the Roblox account card.");
                case OwnerTaskKind.RobloxSession:
                    throw new GameActionRefused("Only you can sign the studio in: connect your Roblox account in the Roblox account card.");
                case OwnerTaskKind.UploadIcon or OwnerTaskKind.Questionnaire when task.GameId is { } gameId:
                    if (!session)
                        throw new GameActionRefused("Roblox lets nothing but a signed-in account do this, so the studio needs yours first: connect it in the Roblox account card, and it does this at once.");
                    bool icon = task.Kind == OwnerTaskKind.UploadIcon;
                    return Begin(gameId, "delegate", icon ? "Upload the icon itself" : "Answer the maturity questionnaire itself", async (id, step, ct) =>
                    {
                        if (icon)
                        {
                            StorePageChores.MakeDue(ctx, StorePageChores.IconKey(id));
                            bool done = await StorePageChores.IconAsync(ctx, id, ct);
                            if (!done) throw new GameActionRefused("Roblox did not take the upload yet; the studio keeps trying (see the task).");
                            step("Uploaded.");
                            return "Uploaded the icon.";
                        }
                        StorePageChores.MakeDue(ctx, StorePageChores.QuestionnaireKey(id));
                        bool rated = await StorePageChores.QuestionnaireAsync(ctx, id, ct);
                        if (!rated) throw new GameActionRefused("The questionnaire is not done (see the task for why).");
                        step("Answered and published the rating.");
                        await ReachAsync(id, step, ct);
                        return "Answered the maturity questionnaire.";
                    });
                case OwnerTaskKind.ExperienceSlot:
                    if (Slots.WhyNotCreating(ctx) is { } why) throw new GameActionRefused("The studio cannot make an experience now: " + why + ".");
                    lock (sync)
                    {
                        if (running.ContainsKey(StudioJob)) throw new GameActionRefused("It is already making one.");
                        running[StudioJob] = Task.Run(async () =>
                        {
                            try { await Slots.EnsureSpareAsync(ctx, CancellationToken.None); }
                            catch (Exception ex) { ctx.Log("[OmniBlox] making an experience for the owner: " + ex.Message); }
                            finally { lock (sync) running.Remove(StudioJob); }
                        });
                    }
                    return "Making an experience on your account now.";
                case OwnerTaskKind.Other when task.DedupeKey.StartsWith("retire:", StringComparison.Ordinal):
                    StorePageChores.MakeDue(ctx, task.DedupeKey);
                    _ = Task.Run(async () =>
                    {
                        try { await StorePageChores.SweepAsync(ctx, CancellationToken.None); }
                        catch (Exception ex) { ctx.Log("[OmniBlox] taking a product off sale for the owner: " + ex.Message); }
                    });
                    return "Taking it off sale now.";
                default:
                    if (task.GameId is not { } agentGame || ctx.Game(agentGame) is not { } game)
                        throw new GameActionRefused("There is no game agent to hand this to; it needs you.");
                    var (directive, _) = AddDirective(ctx, agentGame,
                        $"The owner asks you to handle this yourself rather than wait for them: {task.Title}\n{Clip(task.Detail, 1400)}");
                    var now = ctx.Clock();
                    ctx.Store.Mutate(s =>
                    {
                        if (s.Game(agentGame) is { Stage: GameStage.Live } live) { live.UrgentMaintenance = true; s.MarkGame(agentGame); }
                        foreach (var t in s.Studio.OwnerTasks.Where(t => t.TaskId == taskId && t.Status == OwnerTaskStatus.Open)) { t.Status = OwnerTaskStatus.Done; t.ResolvedUtc = now; }
                        s.MarkStudio();
                        s.AddEvent(EventLevel.Info, "owner.delegate", $"Handed to the agent ({directive}): {task.Title}", agentGame, now);
                    });
                    return $"Handed to \"{game.DisplayTitle}\"'s agent as direction {directive}; it takes it up in its next cycle" + (ctx.Settings.Enabled ? "." : ", once the studio is switched on.");
            }
        }

        /// <summary>
        /// The owner's direction for a game: delivered to a cycle running now (at its next turn), leading every briefing
        /// until an agent reports it done. A game the watchdog paused resumes: the direction is the help it asked for.
        /// </summary>
        public static (string DirectiveId, bool Resumed) AddDirective(StudioContext ctx, string gameId, string text)
        {
            var now = ctx.Clock();
            string directiveId = "";
            bool resumed = false;
            ctx.Store.Mutate(s =>
            {
                var g = s.Game(gameId)!;
                int n = 1;
                while (g.Directives.Any(d => d.DirectiveId == "d" + n)) n++;
                directiveId = "d" + n;
                g.Directives.Add(new OwnerDirective { DirectiveId = directiveId, Utc = now, Text = text });
                // Keep the history bounded: the oldest closed directives go first; open ones always stay.
                int excess = g.Directives.Count - 60;
                if (excess > 0)
                    foreach (var old in g.Directives.Where(d => d.Status != DirectiveStatus.Open).Take(excess).ToList()) g.Directives.Remove(old);
                g.NextAttemptUtc = null;
                if (g.Paused && g.Progress.Level >= 3)
                {
                    g.Paused = false;
                    g.WaitingReason = null;
                    Watchdog.Reset(g, now, "the owner gave a direction");
                    s.ResolveOwnerTasks("stuck:" + gameId, now);
                    resumed = true;
                }
                s.Touch(g, now);
                s.AddEvent(EventLevel.Info, "owner.steer", $"Owner's direction ({directiveId}): " + (text.Length > 300 ? text[..300] + "…" : text), gameId, now);
            });
            return (directiveId, resumed);
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
