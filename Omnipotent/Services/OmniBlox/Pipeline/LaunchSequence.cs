using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Marketing;
using Omnipotent.Services.OmniBlox.Pipeline.Tools;
using Omnipotent.Services.OmniBlox.Rbx;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    public enum LaunchResult { Done, Waiting, BackToMarketing, BackToBuild, Failed }

    /// <summary>
    /// Publishes a game whose marketing is ready, one idempotent step at a time so a restart (or an owner
    /// task) can interrupt it anywhere: API key → experience (made now when none is free) → compliance → final build → engine tests →
    /// retire an earlier game's products left in the slot → products → the publish build (with product ids) →
    /// store page → homepage thumbnails → the icon and maturity questionnaire, which no API key can do, through the
    /// owner's Roblox session (<see cref="StorePageChores"/>; what cannot be done now is finished from housekeeping)
    /// → publish → public. Nothing irreversible happens on Roblox (products) until the game has run clean in the
    /// engine and the owner has approved the launch.
    /// </summary>
    public static class LaunchSequence
    {
        public static async Task<(LaunchResult Result, string Message)> RunAsync(StudioContext ctx, string gameId, CancellationToken ct)
        {
            var now = ctx.Clock();
            void Step(string name) => ctx.Update(gameId, g => g.Publish.LastLaunchStep = name);

            // 1. The key.
            Step("api key");
            if (!ctx.Cloud.HasKey)
            {
                ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.ApiKey, "apikey", "Add a Roblox Open Cloud API key",
                    "OmniBlox needs an Open Cloud API key in the OmniSetting OmniBlox_RobloxApiKey to publish games, create products, run engine tests, download models and read analytics. See Docs/omniblox.md for the permissions.", null, now));
                return (LaunchResult.Waiting, "waiting for the Roblox API key");
            }

            // 2. Somewhere to publish: the game's experience, a free one, or one the studio creates now.
            Step("experience slot");
            var slot = await Slots.EnsureAsync(ctx, gameId, ct);
            if (slot == null) return (LaunchResult.Waiting, "waiting for an experience: " + Slots.Unavailable(ctx, ask: true));

            // 3. Rules (cheap re-check: text may have changed since the marketer ran it).
            Step("compliance");
            var game = ctx.Game(gameId)!;
            var compliance = ComplianceChecker.Check(game.Store, game.Products, game.Brief);
            if (!compliance.Ok)
            {
                ctx.Update(gameId, g => { g.Store.ComplianceOk = false; g.Store.ComplianceNotes = compliance.Errors.Concat(compliance.Warnings).ToList(); g.Publish.LaunchRequestedUtc = null; });
                return (LaunchResult.BackToMarketing, "compliance failed: " + string.Join("; ", compliance.Errors));
            }

            // 4. The final build and 5. the game running clean in Roblox's engine — before anything irreversible
            // happens on Roblox (a broken game used to get its products created and then bounce back to Build). A
            // pass in the local engine, which stood in before the key and slot existed, does not count here.
            Step("final build");
            var build = await BuildTools.RunBuildAsync(ctx, gameId, ct);
            if (!build.Ok) return (LaunchResult.BackToBuild, "the final build failed: " + BuildErrors(build));
            Step("engine tests");
            game = ctx.Game(gameId)!;
            if (!StageGates.EnginePassed(game))
            {
                var (engine, message) = await BuildTools.RunEngineTestsAsync(ctx, gameId, ct, build);
                if (engine == null) return (LaunchResult.Failed, "the engine tests could not run: " + message);
                if (!engine.Ok && !TestStage.GameChecksPass(engine))
                {
                    ctx.Update(gameId, g => g.LastTest = new TestReport
                    {
                        Utc = ctx.Clock(), BuildRevision = engine.Revision, StaticOk = true, GeometryOk = true, EngineRan = true, EngineOk = false, EngineVersion = engine.Version, Engine = engine,
                        Summary = "Engine tests failed before launch: " + engine.Summary,
                        Issues = engine.Results.Where(r => !r.Ok && r.Phase != "kit").Select(r => new TestIssue { Severity = "error", Area = "engine " + r.Phase, Message = $"{r.Name}: {r.Detail}" })
                            .Concat(engine.Errors.Select(e => new TestIssue { Severity = "error", Area = "runtime " + e.Phase, Message = (e.File != null ? $"{e.File}{(e.Line is int l ? ":" + l : "")}: " : "") + e.Message })).ToList(),
                    });
                    return (LaunchResult.BackToBuild, "engine tests failed before launch: " + engine.Summary);
                }
            }

            // The launch approval: for this revision, this store page and these products — before anything happens on Roblox.
            Step("approval");
            game = ctx.Game(gameId)!;
            if (!Approvals.LaunchApproved(ctx, game))
            {
                var art = new List<string>();
                if (game.Store.IconFile != null) art.Add(game.Store.IconFile);
                art.AddRange(game.Store.ThumbnailFiles);
                if (!ctx.Store.Read(s => s.Studio.OwnerTasks.Any(t => t.Status == OwnerTaskStatus.Open && t.Kind == OwnerTaskKind.LaunchApproval && t.GameId == gameId)))
                    Approvals.RequestLaunch(ctx, gameId, slot, art.Distinct(), compliance.QuestionnaireNotes);
                return (LaunchResult.Waiting, "waiting for the owner's launch approval");
            }

            // 6. An earlier game's products left in this experience come off sale.
            Step("retire old products");
            await StorePageChores.RetireStaleProductsAsync(ctx, slot, ct);

            // 7. Products exist on Roblox (ids are baked into the place's config).
            Step("products");
            game = ctx.Game(gameId)!;
            bool productsChanged = false;
            foreach (var product in game.Products.Where(p => p.RobloxId == null || p.SyncedUtc == null))
            {
                var spec = new ProductSpec
                {
                    Name = Clip(product.Name, 50),
                    Description = Clip(product.Description, 900),
                    PriceRobux = product.PriceRobux,
                    IsForSale = true,
                    ImagePng = ProductIcon(ctx, gameId, product),
                };
                try
                {
                    long id;
                    if (product.Kind == ProductKind.GamePass)
                    {
                        if (product.RobloxId is long existing) { await ctx.Cloud.UpdateGamePassAsync(slot.UniverseId, existing, spec, ct); id = existing; }
                        else id = (await ctx.Cloud.CreateGamePassAsync(slot.UniverseId, spec, ct)).GamePassId;
                    }
                    else
                    {
                        if (product.RobloxId is long existing) { await ctx.Cloud.UpdateDeveloperProductAsync(slot.UniverseId, existing, spec, ct); id = existing; }
                        else id = (await ctx.Cloud.CreateDeveloperProductAsync(slot.UniverseId, spec, ct)).ProductId;
                    }
                    if (id <= 0) throw new InvalidOperationException($"Roblox returned no id for {product.Name}");
                    ctx.Update(gameId, g =>
                    {
                        var p = g.Products.First(x => x.Key == product.Key);
                        p.RobloxId = id;
                        p.SyncedUtc = ctx.Clock();
                    });
                    productsChanged = true;
                }
                catch (RobloxApiException ex) when (ex.IsAuth)
                {
                    ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.ApiKey, "apikey-products", "Give the API key game-pass and developer-product permissions",
                        $"Creating \"{product.Name}\" was refused: {ex.Message}", gameId, now));
                    return (LaunchResult.Waiting, "the API key cannot create products");
                }
            }

            // 8. The publish build: the same files (so the same revision, which the engine run above covers) with the
            // new product ids in its config.
            int revision = ctx.Game(gameId)!.Build.Revision;
            if (productsChanged)
            {
                Step("publish build");
                build = await BuildTools.RunBuildAsync(ctx, gameId, ct);
                if (!build.Ok) return (LaunchResult.BackToBuild, "the publish build failed: " + BuildErrors(build));
                if (ctx.Game(gameId)!.Build.Revision != revision) return (LaunchResult.Failed, "the game's files changed during the launch; starting the checks again");
            }
            ctx.Update(gameId, g =>
            {
                g.Publish.EngineTestedRevision = revision;
                if (g.Build.LastEngineRun is { Local: false } tested && tested.Revision == revision) { g.Publish.LastSavedVersion = tested.Version; g.Publish.LastSavedRevision = revision; }
            });

            // 9. Store page.
            Step("store page");
            game = ctx.Game(gameId)!;
            if (!game.Store.Applied)
            {
                await ctx.Cloud.UpdatePlaceAsync(slot.UniverseId, slot.PlaceId, Clip(game.Store.Title, ComplianceChecker.TitleMax), Clip(game.Store.Description, ComplianceChecker.DescriptionMax), ct);
                ctx.Update(gameId, g => g.Store.Applied = true);
            }

            // 10. Homepage thumbnails (experimental API; the owner task below covers it if this fails).
            Step("thumbnails");
            bool thumbsUploaded = false;
            try
            {
                var pngs = game.Store.ThumbnailFiles.Select(f => Path.Combine(ctx.Store.MediaDirectory(gameId), f)).Where(File.Exists).Select(File.ReadAllBytes).ToList();
                if (pngs.Count > 0)
                {
                    await ctx.Cloud.UploadHomepageThumbnailsAsync(slot.UniverseId, pngs, ct);
                    thumbsUploaded = true;
                }
            }
            catch (Exception ex) when (ex is RobloxApiException or HttpRequestException)
            {
                ctx.Event(EventLevel.Info, "launch.thumbnails", "Homepage thumbnail upload was not accepted (" + ex.Message + "); left for the owner.", gameId);
            }

            // 11. What no API key can do — the icon (and thumbnails the API refused) and the maturity questionnaire —
            // through the owner's session. Never blocks the launch: what is not done now is finished from housekeeping,
            // and the owner is asked only when it needs them.
            Step("icon and questionnaire");
            if (!thumbsUploaded && game.Store.ThumbnailFiles.Count > 0 && !game.Publish.StoreThumbnailsPending)
                ctx.Update(gameId, g => { g.Publish.StoreThumbnailsPending = true; g.Publish.StoreThumbnailsUploaded = 0; });
            await StorePageChores.IconAsync(ctx, gameId, ct);
            await StorePageChores.QuestionnaireAsync(ctx, gameId, ct);

            // 13. Publish and 14. go public.
            Step("publish");
            game = ctx.Game(gameId)!;
            if (game.Publish.LastPublishedRevision != revision)
            {
                var published = await ctx.Cloud.PublishPlaceAsync(slot.UniverseId, slot.PlaceId, build.Compile.PlaceXml, isXml: true, publish: true, ct);
                ctx.Update(gameId, g =>
                {
                    g.Publish.LastPublishedVersion = published.VersionNumber;
                    g.Publish.LastPublishedRevision = revision;
                    g.Publish.FirstPublishedUtc ??= ctx.Clock();
                    g.Publish.LastPublishedUtc = ctx.Clock();
                });
            }
            Step("public");
            await ctx.Cloud.UpdateUniverseAsync(slot.UniverseId, new UniverseUpdate
            {
                Visibility = "PUBLIC",
                DesktopEnabled = true,
                MobileEnabled = true,
                TabletEnabled = true,
                ConsoleEnabled = true,
            }, ct);
            ctx.Store.Mutate(s =>
            {
                var g = s.Game(gameId)!;
                g.Publish.IsPublic = true;
                g.Publish.WentPublicUtc = ctx.Clock();
                g.Publish.LastLaunchError = null;
                g.Stage = GameStage.Live;
                g.StageEnteredUtc = ctx.Clock();
                g.NextMaintenanceUtc = ctx.Clock().AddDays(2);
                g.WaitingReason = null;
                s.Studio.GamesLaunched++;
                s.ResolveOwnerTasks("approve:" + gameId, ctx.Clock());
                s.MarkStudio();
                s.Touch(g, ctx.Clock());
                s.AddEvent(EventLevel.Success, "game.launched", $"\"{g.Store.Title}\" is live (experience {slot.UniverseId}).", gameId, ctx.Clock());
            });
            return (LaunchResult.Done, $"\"{game.Store.Title}\" is live");
        }

        private static string BuildErrors(BuildTools.BuildRun build) =>
            string.Join("; ", build.Compile.Errors
                .Concat(build.Types?.Errors.Select(d => d.ToString()) ?? Enumerable.Empty<string>())
                .Concat(build.Geometry.Where(g => g.IsError).Select(g => g.ToString())).Take(5));

        /// <summary>A small product image: the game's own world behind the product name, in the bright marketing look.</summary>
        private static byte[]? ProductIcon(StudioContext ctx, string gameId, ProductRecord product)
        {
            try
            {
                var render = ctx.Render(gameId, "iso", 600, 600, labels: false, caption: false, marketing: true);
                return ctx.Marketing.MakeIcon(new ArtSpec { Headline = product.Name, SceneRender = render, Accent = new RbxColor3uint8(255, 190, 60) });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
