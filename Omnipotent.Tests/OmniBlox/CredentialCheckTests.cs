using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline;
using Omnipotent.Services.OmniBlox.Pipeline.Tools;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// Whose the studio's Roblox key and session are (Pipeline/CredentialCheck.cs). On Nov 2 2026 a new account's key and
    /// session were pasted straight into OmniSettings: every place upload to the old account's experiences was refused
    /// ("User unauthorized to update place"), the owner was told the key lacked permissions it had, and two games retried
    /// run_tests 54 times until the watchdog paused them.
    /// </summary>
    public class CredentialCheckTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        private static string GameInBuild(StudioHarness h)
        {
            TestModels.Seed(h.Ctx.Library);
            string id = h.Orchestrator.StartGame();
            h.Ctx.Update(id, g => g.Brief = (GameBrief)LiveMapTests.Briefs().First()[1]);
            h.Orchestrator.Enter(id, GameStage.Build);
            return id;
        }

        private static List<OwnerTask> Open(StudioHarness h, string key) =>
            h.Ctx.Store.Read(s => s.Studio.OwnerTasks.Where(t => t.Status == OwnerTaskStatus.Open && t.DedupeKey == key).Select(OmniBloxStore.Clone).ToList());

        private static int Uploads(StudioHarness h) => h.Roblox.Requests.Count(r => r.Url.Contains("/versions?versionType="));

        [Fact]
        public async Task AKeyFromAnotherAccount_IsNamedAsSuch_NotAsMissingPermissions()
        {
            using var h = new StudioHarness(localEngine: new FakeLocalEngine());
            h.Roblox.KeyOwner = 77; // the games' account is user 42
            h.Roblox.RefusePlaceUploads = true;
            string id = GameInBuild(h);

            var (run, message) = await BuildTools.RunEngineTestsAsync(h.Ctx, id, None);
            Assert.Null(run);
            Assert.Contains("belongs to Roblox user 77", message);
            Assert.DoesNotContain("lacks a permission", message);
            var task = Assert.Single(Open(h, CredentialCheck.MismatchKey));
            Assert.Contains("made on Roblox user 77", task.Detail);
            Assert.Contains("belong to Roblox user 42", task.Detail);
            Assert.Contains("User unauthorized to update place", task.Detail);
            Assert.Contains("switch the studio to this account", task.Detail);
            Assert.Empty(Open(h, CredentialCheck.EngineRefusedKey)); // one task, with the real reason

            // Roblox is not asked again on every run_tests: the local engine stands in for building meanwhile.
            int uploads = Uploads(h);
            Assert.False(StageGates.EngineAvailable(h.Ctx, h.Game(id)));
            Assert.True(StageGates.LocalEngineStandsIn(h.Ctx, h.Game(id)));
            (run, message) = await BuildTools.RunEngineTestsAsync(h.Ctx, id, None);
            Assert.True(run is { Local: true, Ok: true }, message);
            Assert.Equal(uploads, Uploads(h));

            // The owner sets that account's key in OmniSettings: noticed at the next check, the task closes, Roblox's engine is back.
            h.Roblox.KeyOwner = 42;
            h.Roblox.RefusePlaceUploads = false;
            h.Ctx.Settings.ApiKey = "the-right-key";
            await h.Ctx.Credentials.CheckAsync(h.Ctx, None);
            Assert.Empty(Open(h, CredentialCheck.MismatchKey));
            Assert.True(StageGates.EngineAvailable(h.Ctx, h.Game(id)));
            h.Now += TimeSpan.FromMinutes(1); // past the spacing between Roblox engine runs
            (run, message) = await BuildTools.RunEngineTestsAsync(h.Ctx, id, None);
            Assert.True(run is { Local: false, Ok: true }, message);
        }

        [Fact]
        public async Task ARefusalWithTheRightAccountsKey_SaysWhatToCheck_AndClosesOnceRobloxTakesAVersion()
        {
            using var h = new StudioHarness(localEngine: new FakeLocalEngine());
            h.Roblox.RefusePlaceUploads = true;
            string id = GameInBuild(h);

            var (run, message) = await BuildTools.RunEngineTestsAsync(h.Ctx, id, None);
            Assert.Null(run);
            Assert.Contains("Roblox refused the engine tests", message);
            Assert.Contains("local engine", message);
            var task = Assert.Single(Open(h, CredentialCheck.EngineRefusedKey));
            Assert.Null(task.GameId); // the key's problem, not one game's
            Assert.Contains("User unauthorized to update place", task.Detail);
            Assert.Contains("belongs to user 42, the account the experiences belong to", task.Detail);
            Assert.Empty(Open(h, CredentialCheck.MismatchKey));

            // Until it is fixed, building goes on in the local engine without asking Roblox again.
            int uploads = Uploads(h);
            (run, _) = await BuildTools.RunEngineTestsAsync(h.Ctx, id, None);
            Assert.True(run!.Local);
            Assert.Equal(uploads, Uploads(h));

            // A launch still asks Roblox (only its engine clears one) and the refusal stands.
            h.Now += TimeSpan.FromMinutes(1); // past the spacing between Roblox engine runs
            (run, _) = await BuildTools.RunEngineTestsAsync(h.Ctx, id, None, requireRoblox: true);
            Assert.Null(run);
            Assert.Equal(uploads + 1, Uploads(h));

            // The owner fixes the key's access and marks the task done: Roblox's engine is tried again, and a version it takes keeps the task closed.
            h.Roblox.RefusePlaceUploads = false;
            h.Ctx.Store.Mutate(s => s.ResolveOwnerTasks(CredentialCheck.EngineRefusedKey, h.Now));
            h.Now += TimeSpan.FromMinutes(1);
            (run, message) = await BuildTools.RunEngineTestsAsync(h.Ctx, id, None);
            Assert.True(run is { Local: false, Ok: true }, message);
            Assert.Empty(Open(h, CredentialCheck.EngineRefusedKey));
        }

        [Fact]
        public async Task ANewKeyInOmniSettings_GetsAFreshTryAtWhatTheOldOneWasRefused()
        {
            using var h = new StudioHarness();
            await h.Ctx.Credentials.CheckAsync(h.Ctx, None);
            h.Ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.ApiKey, CredentialCheck.EngineRefusedKey, "Roblox refuses the API key's engine tests", "…", null, h.Now));
            Assert.NotNull(CredentialCheck.Refusal(h.Ctx));

            await h.Ctx.Credentials.CheckAsync(h.Ctx, None); // nothing changed: nothing is asked or closed
            Assert.Single(Open(h, CredentialCheck.EngineRefusedKey));
            Assert.Single(h.Roblox.Requests, r => r.Url.Contains("/api-keys/v1/introspect"));

            h.Ctx.Settings.ApiKey = "another-key";
            await h.Ctx.Credentials.CheckAsync(h.Ctx, None);
            Assert.Empty(Open(h, CredentialCheck.EngineRefusedKey));
            Assert.Null(CredentialCheck.Refusal(h.Ctx));
            Assert.Equal("OmniBlox key", h.Ctx.Store.Read(s => s.Studio.ApiKey!.Name));

            h.Ctx.Settings.ApiKey = "unknown-key";
            await h.Ctx.Credentials.CheckAsync(h.Ctx, None);
            Assert.Contains("does not know the API key", Assert.Single(Open(h, CredentialCheck.UnknownKey)).Detail);
            Assert.NotNull(CredentialCheck.Refusal(h.Ctx));
        }

        [Fact]
        public async Task ASessionOfAnotherAccount_IsNamed_AndMakesNoExperienceOnTheWrongAccount()
        {
            using var h = new StudioHarness(session: true);
            h.Roblox.SignedInUserId = 77;
            await h.Ctx.Credentials.CheckAsync(h.Ctx, None);

            Assert.Equal(77, h.Ctx.Store.Read(s => s.Studio.RobloxAccount!.UserId));
            Assert.Contains("the connected session is Owner (user 77)", Assert.Single(Open(h, CredentialCheck.MismatchKey)).Detail);
            Assert.Contains("land on the wrong account", Slots.WhyNotCreating(h.Ctx));
            Assert.Null(CredentialCheck.Refusal(h.Ctx)); // the key itself is the games' account's: engine tests still run

            h.Roblox.SignedInUserId = 42;
            h.Roblox.AcceptedCookie = "cookie-2";
            h.Ctx.Settings.SessionCookie = "cookie-2";
            await h.Ctx.Credentials.CheckAsync(h.Ctx, None);
            Assert.Empty(Open(h, CredentialCheck.MismatchKey));
            Assert.Null(Slots.WhyNotCreating(h.Ctx));
        }

        [Fact]
        public async Task SwitchingAccounts_GivesGamesInProductionAnExperienceOnTheNewAccount()
        {
            using var h = new StudioHarness(session: true);
            string id = GameInBuild(h);
            Assert.Equal(1001, Slots.Ensure(h.Ctx, id)!.UniverseId);
            h.Roblox.SignedInUserId = 77;

            await h.Connection.StartAsync(None, switchAccount: true);
            h.Roblox.QuickLoginStatus = "Validated";
            await h.Connection.PollAsync(None);

            var game = h.Game(id);
            Assert.Null(game.Slot);
            Assert.False(game.Publish.OnOldAccount);
            Assert.Null(Slots.Ensure(h.Ctx, id)); // never the retired old experience
            // The old account's key is now the other one: the owner hears it needs that account's key.
            await h.Ctx.Credentials.CheckAsync(h.Ctx, None);
            Assert.Contains("made on Roblox user 42", Assert.Single(Open(h, CredentialCheck.MismatchKey)).Detail);
        }

        [Fact]
        public async Task SwitchingAccounts_ALaunchThatNeverGotThrough_StartsOverOnTheNewAccount_WithoutAMove()
        {
            using var h = new StudioHarness(session: true);
            // Squash on Nov 2: moved to a new experience, then every upload refused, so nothing of it was ever published there.
            string id = StorePageChoresTests.SeedLiveGame(h);
            h.Ctx.Update(id, g => { g.Stage = GameStage.Launch; g.Publish.LaunchFailures = 17; g.Publish.LastLaunchError = "User unauthorized to update place"; });
            h.Roblox.SignedInUserId = 77;

            await h.Connection.StartAsync(None, switchAccount: true);
            h.Roblox.QuickLoginStatus = "Validated";
            await h.Connection.PollAsync(None);

            var game = h.Game(id);
            Assert.Equal(GameStage.Launch, game.Stage);
            Assert.Null(game.Slot);
            Assert.False(game.Publish.OnOldAccount); // no Move needed
            Assert.Equal(0, game.Publish.LaunchFailures);
            Assert.Null(Assert.Single(game.Products).RobloxId); // made again on its new experience
            Assert.Contains(1001L, h.Ctx.Store.Read(s => s.Studio.RetiredExperiences.ToList()));

            // Until the key is the new account's, no experience is made that it could not reach.
            await h.Ctx.Credentials.CheckAsync(h.Ctx, None);
            Assert.Contains("could not reach a new experience", Slots.WhyNotCreating(h.Ctx));
            Assert.Null(await Slots.EnsureAsync(h.Ctx, id, None));

            h.Roblox.KeyOwner = 77;
            await h.Connection.SetApiKeyAsync("new-key", None);
            Assert.Empty(Open(h, CredentialCheck.MismatchKey));
            var slot = await Slots.EnsureAsync(h.Ctx, id, None);
            Assert.Equal(FakeRoblox.FirstCreatedUniverse, slot?.UniverseId);
        }
    }
}
