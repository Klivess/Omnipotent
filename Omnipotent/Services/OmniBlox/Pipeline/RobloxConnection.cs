using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>
    /// Connecting the owner's Roblox account from the dashboard, which the studio needs for what no API key can do: create
    /// experiences, upload icons and store thumbnails, answer the maturity questionnaire (without one, a public game is
    /// unplayable). The owner used to have to dig the .ROBLOSECURITY cookie out of browser dev tools and paste it into an
    /// OmniSetting. Now they press Connect, type the code shown into Roblox's Quick Log In on a signed-in device and
    /// confirm (<see cref="RobloxQuickLogin"/>); pasting a cookie stays as the fallback. Either way the session is checked
    /// before it is kept: Roblox must accept it, and it must be the account the studio's experiences belong to. Once
    /// connected, the chores that were waiting for it run at once, whether or not the studio is switched on.
    /// </summary>
    public sealed class RobloxConnection
    {
        public const string QuickLogin = "quick-login";
        public const string Pasted = "pasted";

        private readonly StudioContext ctx;
        private readonly RobloxQuickLogin quick;
        private readonly Func<string?, Task> save;
        private readonly Func<long, Task> saveCreator;
        private readonly Func<string, Task> saveApiKey;
        private readonly object sync = new();
        private Pending? pending;
        private string? lastError;

        private sealed class Pending
        {
            public required QuickLoginCode Code { get; init; }
            public required DateTime StartedUtc { get; init; }
            /// <summary>The owner is moving the studio to whichever account confirms the code.</summary>
            public bool SwitchAccount { get; init; }
            public QuickLoginState State { get; set; } = QuickLoginState.Created;
            public string? AccountName { get; set; }
        }

        /// <param name="save">Writes the session to OmniBlox_RobloxSessionCookie (null clears it) and applies it to the studio's settings.</param>
        /// <param name="saveCreator">Writes OmniBlox_CreatorUserId (switching the studio's account).</param>
        /// <param name="saveApiKey">Writes OmniBlox_RobloxApiKey.</param>
        public RobloxConnection(StudioContext ctx, RobloxQuickLogin quick, Func<string?, Task> save, Func<long, Task>? saveCreator = null, Func<string, Task>? saveApiKey = null)
        {
            this.ctx = ctx;
            this.quick = quick;
            this.save = save;
            this.saveCreator = saveCreator ?? (id => { ctx.Settings.CreatorUserId = id; return Task.CompletedTask; });
            this.saveApiKey = saveApiKey ?? (key => { ctx.Settings.ApiKey = key; return Task.CompletedTask; });
        }

        /// <summary>The chores run after the last connection (awaited by tests; the routes do not wait for them).</summary>
        public Task Chores { get; private set; } = Task.CompletedTask;

        /// <summary>Asks Roblox for a new code (withdrawing any earlier one) for the owner to enter.</summary>
        /// <param name="switchAccount">Whoever confirms becomes the studio's account, even another than its creator now (see <see cref="SwitchTo"/>).</param>
        public async Task<object> StartAsync(CancellationToken ct, bool switchAccount = false)
        {
            Pending? old;
            lock (sync) { old = pending; pending = null; lastError = null; }
            if (old != null) await quick.CancelAsync(old.Code, ct);
            try
            {
                var code = await quick.CreateAsync(ct);
                lock (sync) pending = new Pending { Code = code, StartedUtc = ctx.Clock(), SwitchAccount = switchAccount };
            }
            catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or TimeoutException or Newtonsoft.Json.JsonException)
            {
                lock (sync) lastError = "Roblox did not give a Quick Log In code: " + ex.Message;
            }
            return View();
        }

        /// <summary>Checks the code with Roblox; once the owner confirmed it, signs in and keeps the session.</summary>
        public async Task<object> PollAsync(CancellationToken ct)
        {
            Pending? p;
            lock (sync) p = pending;
            if (p == null) return View();
            if (ctx.Clock() >= p.Code.ExpiresUtc)
            {
                Fail(p, "The code expired before it was confirmed (a code lasts about five minutes). Start again for a new one.");
                return View();
            }
            QuickLoginStatus status;
            try { status = await quick.StatusAsync(p.Code, ct); }
            catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or TimeoutException or Newtonsoft.Json.JsonException)
            {
                lock (sync) lastError = "Could not ask Roblox about the code (it keeps trying): " + ex.Message;
                return View();
            }
            lock (sync)
            {
                if (pending != p) return View();
                p.State = status.State;
                p.AccountName = status.AccountName ?? p.AccountName;
                lastError = null;
            }
            switch (status.State)
            {
                case QuickLoginState.Cancelled:
                    Fail(p, "The code was cancelled on Roblox. Start again if that was a slip.");
                    break;
                case QuickLoginState.Expired:
                    Fail(p, "Roblox no longer knows the code: it expired, or was entered from too far away (confirm from the server's network). Start again for a new one.");
                    break;
                case QuickLoginState.Validated:
                    lock (sync) { if (pending == p) pending = null; }
                    try
                    {
                        var session = await quick.LoginAsync(p.Code, ct);
                        await AdoptAsync(session.Cookie, QuickLogin, p.SwitchAccount, ct);
                    }
                    catch (QuickLoginRefusedException ex)
                    {
                        lock (sync) lastError = ex.Message + " The studio does not answer those for you: paste the cookie instead.";
                    }
                    catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or TimeoutException or Newtonsoft.Json.JsonException)
                    {
                        lock (sync) lastError = "The code was confirmed, but signing in with it failed: " + ex.Message;
                    }
                    break;
            }
            return View();
        }

        public async Task<object> CancelAsync(CancellationToken ct)
        {
            Pending? p;
            lock (sync) { p = pending; pending = null; lastError = null; }
            if (p != null) await quick.CancelAsync(p.Code, ct);
            return View();
        }

        /// <summary>Keeps a cookie the owner pasted, once Roblox accepts it for the right account (or for any, when switching).</summary>
        public async Task<object> UseCookieAsync(string text, CancellationToken ct, bool switchAccount = false)
        {
            lock (sync) lastError = null;
            if (RobloxSessionClient.Normalize(text) is not { } cookie) lastError = "That is not a .ROBLOSECURITY cookie.";
            else await AdoptAsync(cookie, Pasted, switchAccount, ct);
            return View();
        }

        /// <summary>
        /// Sets the studio's Open Cloud API key, once Roblox vouches for it (introspection: it exists, is enabled, has not
        /// expired, works from this server) and it belongs to the studio's account. The key itself is never shown again;
        /// what Roblox said about it is.
        /// </summary>
        public async Task<object> SetApiKeyAsync(string key, CancellationToken ct)
        {
            lock (sync) lastError = null;
            key = key.Trim();
            ApiKeyIntrospection? info;
            try { info = key.Length == 0 ? null : await ctx.Public.IntrospectApiKeyAsync(key, ct); }
            catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or TimeoutException or Newtonsoft.Json.JsonException)
            {
                lock (sync) lastError = "Could not check the key with Roblox: " + ex.Message;
                return View();
            }
            string? problem = info switch
            {
                null => "Roblox does not know this API key (check it was copied whole), or it may not be used from this server's IP address.",
                { Enabled: false } => $"The key \"{info.Name}\" is disabled. Enable it in the Creator Dashboard → Open Cloud → API Keys.",
                { Expired: true } => $"The key \"{info.Name}\" has expired. Make a new one, or extend it in the Creator Dashboard.",
                _ when ctx.Settings.CreatorGroupId == null && ctx.Settings.CreatorUserId is long creator && info.OwnerUserId != creator =>
                    $"The key \"{info.Name}\" belongs to user {info.OwnerUserId}, but the studio's account is user {creator}. Switch the studio to that account first (connect it with \"switch the studio to this account\").",
                _ => null,
            };
            if (problem != null)
            {
                lock (sync) lastError = problem;
                return View();
            }
            await saveApiKey(key);
            var now = ctx.Clock();
            ctx.Store.Mutate(s =>
            {
                s.Studio.ApiKey = new ApiKeyInfo { Name = info!.Name, OwnerUserId = info.OwnerUserId, Scopes = info.Scopes, ExpiresUtc = info.ExpiresUtc, CheckedUtc = now };
                s.ResolveOwnerTasks("apikey", now);
                s.ResolveOwnerTasks("apikey-products", now);
                s.MarkStudio();
                s.AddEvent(EventLevel.Success, "studio.apikey", $"The API key \"{info.Name}\" (user {info.OwnerUserId}) is set.", null, now);
            });
            return View();
        }

        /// <summary>Asks Roblox whether the kept session still works.</summary>
        public async Task<object> VerifyAsync(CancellationToken ct)
        {
            lock (sync) lastError = null;
            if (ctx.Session is not { HasSession: true } session) lastError = "No Roblox account is connected.";
            else
            {
                try
                {
                    var (id, name) = await session.GetSignedInUserAsync(ct);
                    ctx.Store.Mutate(s =>
                    {
                        s.Studio.RobloxAccount ??= new RobloxAccount { Method = Pasted, ConnectedUtc = ctx.Clock() };
                        s.Studio.RobloxAccount.UserId = id;
                        s.Studio.RobloxAccount.Name = name;
                        s.Studio.RobloxAccount.VerifiedUtc = ctx.Clock();
                        s.MarkStudio();
                    });
                    StorePageChores.Succeeded(ctx, StorePageChores.SessionKey);
                }
                catch (RobloxSessionException ex)
                {
                    StorePageChores.SessionRefused(ctx, ex);
                    lock (sync) lastError = ex.Message;
                }
                catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or TimeoutException or Newtonsoft.Json.JsonException)
                {
                    lock (sync) lastError = "Could not reach Roblox: " + ex.Message;
                }
            }
            return View();
        }

        /// <summary>Forgets the session. One the studio signed in itself is also signed out; a pasted one is not (that would sign the owner's browser out).</summary>
        public async Task<object> DisconnectAsync(CancellationToken ct)
        {
            var account = ctx.Store.Read(s => s.Studio.RobloxAccount is { } a ? OmniBloxStore.Clone(a) : null);
            string? cookie = RobloxSessionClient.Normalize(ctx.Settings.SessionCookie);
            if (account?.Method == QuickLogin && cookie != null) await quick.LogoutAsync(cookie, ct);
            await save(null);
            ctx.Store.Mutate(s =>
            {
                s.Studio.RobloxAccount = null;
                s.MarkStudio();
                s.AddEvent(EventLevel.Info, "store.session", account != null ? $"Disconnected the Roblox account {account.Name}." : "Forgot the Roblox session.", null, ctx.Clock());
            });
            lock (sync) lastError = null;
            return View();
        }

        /// <summary>
        /// Checks a session and keeps it: Roblox must accept it, and when the studio's creator is a user, it must be that
        /// user (their experiences are the ones the studio changes). Then whatever waited for a session runs now.
        /// </summary>
        private async Task<bool> AdoptAsync(string cookie, string method, bool switchAccount, CancellationToken ct)
        {
            if (ctx.Session == null)
            {
                lock (sync) lastError = "This studio runs without a Roblox session client.";
                return false;
            }
            long id;
            string name;
            try { (id, name) = await ctx.Session.ProbeAsync(cookie, ct); }
            catch (RobloxSessionException ex)
            {
                lock (sync) lastError = ex.Message;
                return false;
            }
            catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or TimeoutException or Newtonsoft.Json.JsonException)
            {
                lock (sync) lastError = "Could not check the session with Roblox: " + ex.Message;
                return false;
            }
            long? previous = ctx.Settings.CreatorUserId;
            bool switching = ctx.Settings.CreatorGroupId == null && previous is long creator && creator != id;
            if (switching && !switchAccount)
            {
                if (method == QuickLogin) await quick.LogoutAsync(cookie, ct);
                lock (sync) lastError = $"That is the account {name} ({id}), but the studio's experiences belong to user {previous} (OmniBlox_CreatorUserId). " +
                                        "Connect that account instead, or choose \"switch the studio to this account\" to move the studio to it.";
                return false;
            }

            await save(cookie);
            if (switching || switchAccount && previous == null) await SwitchTo(id, name, previous);
            var now = ctx.Clock();
            ctx.Store.Mutate(s =>
            {
                s.Studio.RobloxAccount = new RobloxAccount { UserId = id, Name = name, Method = method, ConnectedUtc = now, VerifiedUtc = now };
                // Whatever backed off while there was no session is due now.
                foreach (var (key, chore) in s.Studio.Chores.Where(c => c.Key.StartsWith("icon:") || c.Key.StartsWith("questionnaire:") || c.Key == Slots.CreateKey))
                    chore.NextAttemptUtc = null;
                s.Studio.Chores.Remove(StorePageChores.SessionKey);
                s.ResolveOwnerTasks(StorePageChores.SessionKey, now);
                s.MarkStudio();
                s.AddEvent(EventLevel.Success, "store.session", $"Connected the Roblox account {name} ({(method == QuickLogin ? "Quick Log In" : "pasted cookie")}).", null, now);
            });
            lock (sync) lastError = null;
            Chores = Task.Run(() => RunChoresAsync());
            return true;
        }

        /// <summary>
        /// Makes another Roblox account the studio's: it becomes the creator, and the experiences of the old account are
        /// retired (the studio's own and the owner's listed slots), so no new game lands there. Games already published
        /// there stay up and are flagged; the owner moves each with the game action "move", once the API key is the new
        /// account's too.
        /// </summary>
        private async Task SwitchTo(long userId, string name, long? previous)
        {
            await saveCreator(userId);
            var now = ctx.Clock();
            ctx.Store.Mutate(s =>
            {
                foreach (long universe in s.Studio.CreatedExperiences.Select(e => e.UniverseId).Concat(ctx.Settings.Slots.Select(sl => sl.UniverseId)))
                    if (!s.Studio.RetiredExperiences.Contains(universe)) s.Studio.RetiredExperiences.Add(universe);
                foreach (var g in s.Games.Values.Where(g => g.Slot != null && g.Stage != GameStage.Abandoned))
                {
                    g.Publish.OnOldAccount = true;
                    s.MarkGame(g.GameId);
                }
                s.MarkStudio();
                s.AddEvent(EventLevel.Warning, "studio.account",
                    $"Switched the studio to the Roblox account {name} ({userId})" + (previous is long p ? $", from user {p}" : "") +
                    ". New games go to experiences on this account; move the games already published (each game's Manage → Move) once the API key is this account's too.", null, now);
            });
        }

        /// <summary>The icon, thumbnails and questionnaire that waited for the session, done now rather than at the next housekeeping (which waits for the studio switch).</summary>
        private async Task RunChoresAsync()
        {
            try { await StorePageChores.SweepAsync(ctx, CancellationToken.None); }
            catch (Exception ex) { ctx.Log("[OmniBlox] chores after connecting Roblox: " + ex); }
        }

        private void Fail(Pending p, string why)
        {
            lock (sync)
            {
                if (pending != p) return;
                pending = null;
                lastError = why;
            }
        }

        /// <summary>The dashboard's view: who is connected, the code being confirmed, the last problem. Never the session or the code's key.</summary>
        public object View()
        {
            Pending? p;
            string? error;
            lock (sync) { p = pending; error = lastError; }
            var (account, waiting, apiKey, oldAccount) = ctx.Store.Read(s => (
                s.Studio.RobloxAccount is { } a ? OmniBloxStore.Clone(a) : null,
                s.Studio.OwnerTasks.Where(t => t.Status == OwnerTaskStatus.Open && t.Kind is OwnerTaskKind.UploadIcon or OwnerTaskKind.Questionnaire or OwnerTaskKind.RobloxSession or OwnerTaskKind.ExperienceSlot)
                    .Select(t => t.Title).ToList(),
                s.Studio.ApiKey is { } k ? OmniBloxStore.Clone(k) : null,
                s.Games.Values.Where(g => g.Publish.OnOldAccount && g.Stage != GameStage.Abandoned).OrderBy(g => g.CreatedUtc)
                    .Select(g => new { g.GameId, Title = g.DisplayTitle, Stage = g.Stage.ToString(), g.Slot!.UniverseId }).ToList()));
            bool connected = ctx.Session is { HasSession: true };
            return new
            {
                Connected = connected,
                Account = connected ? account : null,
                CreatorUserId = ctx.Settings.CreatorUserId,
                CreatorGroupId = ctx.Settings.CreatorGroupId,
                Pending = p == null ? null : new
                {
                    p.Code.Code,
                    p.Code.ExpiresUtc,
                    p.StartedUtc,
                    State = p.State.ToString(),
                    p.AccountName,
                    RobloxQuickLogin.ConfirmUrl,
                },
                LastError = error,
                Waiting = waiting,
                // Whether a key is set, and what Roblox said about it when the owner set it here (never the key).
                ApiKey = new { Set = ctx.Cloud.HasKey, Info = apiKey, MatchesAccount = apiKey == null || ctx.Settings.CreatorUserId == null || ctx.Settings.CreatorGroupId != null || apiKey.OwnerUserId == ctx.Settings.CreatorUserId },
                // Games still published on the account the studio switched away from.
                OldAccountGames = oldAccount,
            };
        }
    }
}
