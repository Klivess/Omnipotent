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
    /// task) can interrupt it anywhere: API key → experience slot → compliance → products → final build →
    /// engine tests → store page → homepage thumbnails → owner tasks for the things with no API (icon,
    /// maturity questionnaire) → launch approval if required → publish → public.
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

            // 2. Somewhere to publish.
            Step("experience slot");
            var slot = Slots.Ensure(ctx, gameId);
            if (slot == null)
            {
                ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.ExperienceSlot, "slots", "Create a blank experience for OmniBlox",
                    "Every experience OmniBlox may use is taken. Create a new blank experience in the Creator Dashboard (Roblox has no API for creating one), give the API key access to it, and add it as universeId:placeId to the OmniSetting OmniBlox_ExperienceSlots.", null, now));
                return (LaunchResult.Waiting, "waiting for a free experience slot");
            }

            // 3. Rules (cheap re-check: text may have changed since the marketer ran it).
            Step("compliance");
            var game = ctx.Game(gameId)!;
            var compliance = ComplianceChecker.Check(game.Store, game.Products, game.Brief);
            if (!compliance.Ok)
            {
                ctx.Update(gameId, g => { g.Store.ComplianceOk = false; g.Store.ComplianceNotes = compliance.Errors.Concat(compliance.Warnings).ToList(); g.Publish.LaunchRequestedUtc = null; });
                return (LaunchResult.BackToMarketing, "compliance failed: " + string.Join("; ", compliance.Errors));
            }

            // 4. Products exist on Roblox (ids are baked into the place's config).
            Step("products");
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
                }
                catch (RobloxApiException ex) when (ex.IsAuth)
                {
                    ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.ApiKey, "apikey-products", "Give the API key game-pass and developer-product permissions",
                        $"Creating \"{product.Name}\" was refused: {ex.Message}", gameId, now));
                    return (LaunchResult.Waiting, "the API key cannot create products");
                }
            }

            // 5. The final build (with product ids) and 6. its engine tests.
            Step("final build");
            var run = BuildTools.RunBuild(ctx, gameId);
            if (!run.Ok) return (LaunchResult.BackToBuild, "the final build failed: " + string.Join("; ", run.Compile.Errors.Concat(run.Geometry.Where(g => g.IsError).Select(g => g.ToString())).Take(5)));
            int revision = ctx.Game(gameId)!.Build.Revision;
            Step("engine tests");
            if (ctx.Game(gameId)!.Publish.EngineTestedRevision != revision)
            {
                var engine = await EngineTests.RunAsync(ctx, slot, run.Compile.PlaceXml, ct);
                if (!engine.Ok)
                {
                    ctx.Update(gameId, g => g.LastTest = new TestReport
                    {
                        Utc = ctx.Clock(), BuildRevision = revision, StaticOk = true, GeometryOk = true, EngineRan = true, EngineOk = false, EngineVersion = engine.Version,
                        Summary = "Engine tests failed before launch: " + engine.Message,
                        Issues = engine.Failures.Select(f => new TestIssue { Severity = "error", Area = "engine", Message = f }).ToList(),
                    });
                    return (LaunchResult.BackToBuild, "engine tests failed: " + string.Join("; ", engine.Failures.Take(3)));
                }
                ctx.Update(gameId, g => { g.Publish.EngineTestedRevision = revision; g.Publish.LastSavedVersion = engine.Version; g.Publish.LastSavedRevision = revision; });
            }

            // 7. Store page.
            Step("store page");
            game = ctx.Game(gameId)!;
            if (!game.Store.Applied)
            {
                await ctx.Cloud.UpdatePlaceAsync(slot.UniverseId, slot.PlaceId, Clip(game.Store.Title, ComplianceChecker.TitleMax), Clip(game.Store.Description, ComplianceChecker.DescriptionMax), ct);
                ctx.Update(gameId, g => g.Store.Applied = true);
            }

            // 8. Homepage thumbnails (experimental API; the owner task below covers it if this fails).
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

            // 9. Things with no API: the owner does them once, with everything prepared.
            Step("owner tasks");
            var files = new List<string>();
            if (game.Store.IconFile != null) files.Add(game.Store.IconFile);
            if (!thumbsUploaded) files.AddRange(game.Store.ThumbnailFiles);
            ctx.Store.Mutate(s =>
            {
                s.FileOwnerTask(OwnerTaskKind.UploadIcon, "icon:" + gameId, $"Upload the icon{(thumbsUploaded ? "" : " and thumbnails")} for \"{game.Store.Title}\"",
                    $"Roblox has no API for an experience's icon{(thumbsUploaded ? "" : " or store thumbnails")}. Upload the prepared file(s) in the Creator Dashboard for experience {slot.UniverseId}.", gameId, now, files);
                s.FileOwnerTask(OwnerTaskKind.Questionnaire, "questionnaire:" + gameId, $"Answer the maturity questionnaire for \"{game.Store.Title}\"",
                    "Prepared honest answers:\n- " + string.Join("\n- ", compliance.QuestionnaireNotes), gameId, now);
            });

            // 10. Launch approval.
            Step("approval");
            if (ctx.Settings.RequireLaunchApproval && game.Publish.LaunchApprovedUtc == null)
            {
                ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.LaunchApproval, "approve:" + gameId, $"Approve the launch of \"{game.Store.Title}\"",
                    $"Built, tested and ready. Look it over (thumbnails, store page, products) and approve it on the OmniBlox dashboard. Experience {slot.UniverseId}.", gameId, now,
                    files.Concat(game.Store.ThumbnailFiles).Distinct()));
                return (LaunchResult.Waiting, "waiting for the owner's launch approval");
            }

            // 11. Publish and 12. go public.
            Step("publish");
            game = ctx.Game(gameId)!;
            if (game.Publish.LastPublishedRevision != revision)
            {
                var published = await ctx.Cloud.PublishPlaceAsync(slot.UniverseId, slot.PlaceId, run.Compile.PlaceXml, isXml: true, publish: true, ct);
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

        /// <summary>A small product image: the game's own world behind the product name.</summary>
        private static byte[]? ProductIcon(StudioContext ctx, string gameId, ProductRecord product)
        {
            try
            {
                var result = ctx.Compile(gameId);
                if (result.Parts.Count == 0) return null;
                var render = Build.SceneRenderer.Render(result.Parts, ctx.Workspace(gameId).LoadScene(), "iso", 600, 600, caption: false);
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
