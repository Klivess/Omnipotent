using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline;
using Omnipotent.Services.OmniBlox.Roblox;
using Xunit;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// Connecting the owner's Roblox account from the dashboard. It used to mean digging the .ROBLOSECURITY cookie out of
    /// browser dev tools into an OmniSetting, so it was never done, and the first launched game stayed unrated (unplayable)
    /// with its icon never uploaded. Now: Quick Log In with a code the owner confirms on Roblox, or a pasted cookie, each
    /// checked before it is kept.
    /// </summary>
    public class RobloxConnectionTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        private static JObject Json(object view) => JObject.Parse(OmniBloxViews.Serialize(view));

        private static List<OwnerTask> Open(StudioHarness h) => h.Ctx.Store.Read(s => s.Studio.OwnerTasks.Where(t => t.Status == OwnerTaskStatus.Open).Select(OmniBloxStore.Clone).ToList());

        private static async Task<JObject> ConfirmQuickLogInAsync(StudioHarness h)
        {
            await h.Connection.StartAsync(None);
            h.Roblox.QuickLoginStatus = "Validated";
            return Json(await h.Connection.PollAsync(None));
        }

        [Fact]
        public async Task QuickLogIn_TheOwnerConfirmsTheCode_TheStudioSignsInItself_AndDoesWhatWaited()
        {
            using var h = new StudioHarness();
            h.Ctx.Settings.Enabled = false; // the chores run on connecting, studio on or off
            string id = StorePageChoresTests.SeedLiveGame(h);
            await StorePageChores.SweepAsync(h.Ctx, None);
            Assert.Contains(Open(h), t => t.Kind == OwnerTaskKind.UploadIcon);
            Assert.Contains(Open(h), t => t.Kind == OwnerTaskKind.Questionnaire && t.Detail.Contains("Quick Log In"));

            var view = Json(await h.Connection.StartAsync(None));
            Assert.False((bool)view["Connected"]!);
            Assert.Equal("QK7Z2P", (string)view["Pending"]!["Code"]!);
            Assert.Equal(RobloxQuickLogin.ConfirmUrl, (string)view["Pending"]!["ConfirmUrl"]!);
            Assert.DoesNotContain("pk-1", view.ToString()); // the code's private key never leaves the server
            Assert.Equal(2, ((JArray)view["Waiting"]!).Count);

            // Not entered yet; then entered on a signed-in device, waiting for Confirm.
            view = Json(await h.Connection.PollAsync(None));
            Assert.Equal("Created", (string)view["Pending"]!["State"]!);
            h.Roblox.QuickLoginStatus = "UserLinked";
            view = Json(await h.Connection.PollAsync(None));
            Assert.Equal("UserLinked", (string)view["Pending"]!["State"]!);
            Assert.Equal("Ow***", (string)view["Pending"]!["AccountName"]!);

            // Confirmed: the studio signs in with the code (after Roblox's CSRF handshake) and keeps its own session.
            h.Roblox.QuickLoginStatus = "Validated";
            view = Json(await h.Connection.PollAsync(None));
            Assert.True((bool)view["Connected"]!, (string?)view["LastError"]);
            Assert.Equal(JTokenType.Null, view["Pending"]!.Type);
            Assert.Equal(RobloxConnection.QuickLogin, (string)view["Account"]!["Method"]!);
            Assert.Equal(42, (long)view["Account"]!["UserId"]!);
            Assert.Equal("cookie-quick", h.Ctx.Settings.SessionCookie);
            Assert.DoesNotContain("cookie-quick", view.ToString());
            var logins = h.Roblox.Requests.Where(r => r.Url == "https://auth.roblox.com/v2/login").ToList();
            Assert.Equal(2, logins.Count);
            Assert.Contains("\"ctype\":\"AuthToken\"", logins[^1].Body);
            Assert.Contains("\"password\":\"pk-1\"", logins[^1].Body);

            // What waited for a session is done at once: the icon, and the questionnaire that keeps a public game playable.
            await h.Connection.Chores;
            var game = h.Game(id);
            Assert.NotNull(game.Publish.IconUploadedHash);
            Assert.True(game.Publish.Questionnaire!.Published);
            Assert.Empty(Open(h));
            Assert.Contains(h.Ctx.Store.Read(s => s.Events.ToList()), e => e.Kind == "store.session" && e.Message.Contains("Quick Log In"));
        }

        [Fact]
        public async Task AnAccountThatDoesNotOwnTheExperiences_IsRefused_AndTheSessionItMadeIsSignedOut()
        {
            using var h = new StudioHarness();
            h.Roblox.SignedInUserId = 77;
            var view = await ConfirmQuickLogInAsync(h);
            Assert.False((bool)view["Connected"]!);
            Assert.Contains("belong to user 42", (string)view["LastError"]!);
            Assert.Null(h.Ctx.Settings.SessionCookie);
            Assert.Contains("cookie-quick", h.Roblox.LoggedOut);
        }

        [Fact]
        public async Task ACodeThatIsCancelledForgottenChallengedOrExpired_EndsWithAClearReason_AndKeepsNothing()
        {
            using var h = new StudioHarness();

            await h.Connection.StartAsync(None);
            h.Roblox.QuickLoginStatus = "Cancelled";
            var view = Json(await h.Connection.PollAsync(None));
            Assert.Equal(JTokenType.Null, view["Pending"]!.Type);
            Assert.Contains("cancelled", (string)view["LastError"]!);

            await h.Connection.StartAsync(None);
            h.Roblox.QuickLoginStatus = "Invalid";
            view = Json(await h.Connection.PollAsync(None));
            Assert.Contains("no longer knows the code", (string)view["LastError"]!);

            // Roblox wants a captcha before handing over the session: never answered for the owner.
            h.Roblox.QuickLoginChallenge = "captcha";
            view = await ConfirmQuickLogInAsync(h);
            Assert.False((bool)view["Connected"]!);
            Assert.Contains("captcha", (string)view["LastError"]!);
            Assert.Contains("paste the cookie", (string)view["LastError"]!);

            // A code past its five minutes is not even asked about.
            h.Roblox.QuickLoginChallenge = null;
            await h.Connection.StartAsync(None);
            h.Now = DateTime.Parse(FakeRoblox.QuickLoginExpiry).ToUniversalTime().AddSeconds(1);
            int requests = h.Roblox.Requests.Count;
            view = Json(await h.Connection.PollAsync(None));
            Assert.Contains("expired", (string)view["LastError"]!);
            Assert.Equal(requests, h.Roblox.Requests.Count);

            Assert.Null(h.Ctx.Settings.SessionCookie);
            Assert.Equal(JTokenType.Null, view["Account"]!.Type);
        }

        [Fact]
        public async Task StartingAgain_WithdrawsTheEarlierCode()
        {
            using var h = new StudioHarness();
            await h.Connection.StartAsync(None);
            await h.Connection.StartAsync(None);
            Assert.Equal(2, h.Roblox.QuickLoginCodes);
            Assert.Single(h.Roblox.Requests, r => r.Url.EndsWith("/auth-token-service/v1/login/cancel"));
        }

        [Fact]
        public async Task APastedCookie_IsCheckedBeforeItIsKept_AndDisconnectingLeavesTheBrowserSignedIn()
        {
            using var h = new StudioHarness();
            var view = Json(await h.Connection.UseCookieAsync("not-a-session", None));
            Assert.False((bool)view["Connected"]!);
            Assert.Contains("does not accept", (string)view["LastError"]!);
            Assert.Null(h.Ctx.Settings.SessionCookie);

            view = Json(await h.Connection.UseCookieAsync("RBXEventTrackerV2=1; .ROBLOSECURITY=cookie-1; path=/", None));
            Assert.True((bool)view["Connected"]!);
            Assert.Equal(RobloxConnection.Pasted, (string)view["Account"]!["Method"]!);
            Assert.Equal("cookie-1", h.Ctx.Settings.SessionCookie);

            view = Json(await h.Connection.VerifyAsync(None));
            Assert.True((bool)view["Connected"]!);
            Assert.Equal(JTokenType.Null, view["LastError"]!.Type);

            view = Json(await h.Connection.DisconnectAsync(None));
            Assert.False((bool)view["Connected"]!);
            Assert.Null(h.Ctx.Settings.SessionCookie);
            Assert.Empty(h.Roblox.LoggedOut); // the owner's browser session stays signed in
        }

        [Fact]
        public async Task DisconnectingASessionTheStudioMade_SignsItOut()
        {
            using var h = new StudioHarness();
            Assert.True((bool)(await ConfirmQuickLogInAsync(h))["Connected"]!);
            await h.Connection.DisconnectAsync(None);
            Assert.Equal(new[] { "cookie-quick" }, h.Roblox.LoggedOut);
            Assert.Null(h.Ctx.Settings.SessionCookie);
        }

        [Fact]
        public async Task ASignedOutSession_IsReportedByVerify_AndBecomesTheSignInTask()
        {
            using var h = new StudioHarness(session: true);
            h.Roblox.AcceptedCookie = "some-other-session";
            var view = Json(await h.Connection.VerifyAsync(None));
            Assert.Contains("signed the session out", (string)view["LastError"]!);
            Assert.Contains(Open(h), t => t.Kind == OwnerTaskKind.RobloxSession);
        }

        [Fact]
        public void ThePastedCookieRoute_IsKeptOutOfStoredRequestBodies()
        {
            OmniBloxRoutes.MarkSecretRoutes();
            Assert.True(Omnipotent.Services.KliveAPI.KliveAPI.IsSensitiveSettingsRoute("/omniblox/roblox/cookie"));
            Assert.False(Omnipotent.Services.KliveAPI.KliveAPI.IsSensitiveSettingsRoute("/omniblox/roblox/verify"));
        }
    }
}
