using System.Security.Cryptography;
using System.Text;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>
    /// Whose the studio's Roblox credentials are. The API key and the session live in OmniSettings, re-read every minute,
    /// so a key or cookie pasted there directly skips the account checks the dashboard's Roblox account card makes. On
    /// Nov 2 2026 a new account's key and session went in that way: every experience stayed on the old account, Roblox
    /// answered each place upload "User unauthorized to update place", and the studio told the owner the key lacked
    /// permissions it had, while two games retried run_tests 54 times and were paused. Now every key and session is
    /// checked with Roblox when it changes, the owner is told plainly when either belongs to another account than the
    /// games, and Roblox's engine is not retried while it refuses (the local engine stands in for building).
    /// </summary>
    public sealed class CredentialCheck
    {
        /// <summary>Owner-task key for "the key or the session is another account's than the games'".</summary>
        public const string MismatchKey = "account-mismatch";
        /// <summary>Owner-task key for "Roblox refused the engine tests" for any other reason.</summary>
        public const string EngineRefusedKey = "apikey-engine";
        /// <summary>Owner-task key for "Roblox does not know the key in the OmniSetting".</summary>
        public const string UnknownKey = "apikey-unknown";

        private readonly SemaphoreSlim gate = new(1, 1);
        // Hashes of the key and cookie last checked, in memory only: what changed since.
        private string? checkedKey, checkedCookie;

        /// <summary>
        /// Asks Roblox whose the key and the session are when either changed since the last check (the first check after a
        /// start always asks), records the answers, and files or closes the owner's task about them. A key that replaces
        /// another closes the engine-refusal task, so the new key gets a fresh try. Never throws for Roblox being
        /// unreachable: it asks again at the next check.
        /// </summary>
        public async Task CheckAsync(StudioContext ctx, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                string? key = ctx.Settings.ApiKey?.Trim();
                string keyPrint = Print(key);
                if (keyPrint != checkedKey && await CheckKeyAsync(ctx, key, replaced: checkedKey != null, ct)) checkedKey = keyPrint;

                string? cookie = ctx.Session is { HasSession: true } ? ctx.Settings.SessionCookie : null;
                string cookiePrint = Print(cookie);
                if (cookiePrint != checkedCookie && await CheckSessionAsync(ctx, cookie, ct)) checkedCookie = cookiePrint;

                Review(ctx);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>The key was set from the dashboard, which asked Roblox about it already.</summary>
        public void Accepted(string key) => checkedKey = Print(key.Trim());

        private static async Task<bool> CheckKeyAsync(StudioContext ctx, string? key, bool replaced, CancellationToken ct)
        {
            if (key == null)
            {
                ctx.Store.Mutate(s => { s.Studio.ApiKey = null; s.ResolveOwnerTasks(UnknownKey, ctx.Clock()); s.MarkStudio(); });
                return true;
            }
            ApiKeyIntrospection? info;
            try { info = await ctx.Public.IntrospectApiKeyAsync(key, ct); }
            catch (Exception ex) when (Transient(ex, ct))
            {
                ctx.Log("[OmniBlox] could not ask Roblox about the API key: " + ex.Message);
                return false;
            }
            var now = ctx.Clock();
            ctx.Store.Mutate(s =>
            {
                s.Studio.ApiKey = info == null ? null : new ApiKeyInfo { Name = info.Name, OwnerUserId = info.OwnerUserId, Scopes = info.Scopes, ExpiresUtc = info.ExpiresUtc, CheckedUtc = now };
                // What Roblox refused the old key is tried again with the new one.
                if (replaced) s.ResolveOwnerTasks(EngineRefusedKey, now);
                string? problem = info switch
                {
                    null => "Roblox does not know the API key in the OmniSetting OmniBlox_RobloxApiKey: it was not copied whole, was deleted, or may not be used from this server's IP address.",
                    { Enabled: false } => $"The API key \"{info.Name}\" is disabled. Enable it in the Creator Dashboard → Open Cloud → API Keys.",
                    { Expired: true } => $"The API key \"{info.Name}\" has expired. Make a new one, or extend it in the Creator Dashboard.",
                    _ => null,
                };
                if (problem == null) s.ResolveOwnerTasks(UnknownKey, now);
                else s.FileOwnerTask(OwnerTaskKind.ApiKey, UnknownKey, "Roblox refuses the API key", problem + "\nUntil it is fixed nothing reaches Roblox: no engine tests, launches or updates. Set a working key on the studio page's Roblox account card.", null, now);
                if (replaced) s.AddEvent(EventLevel.Info, "studio.apikey", info == null ? "The API key changed; Roblox does not know the new one." : $"The API key changed: \"{info.Name}\" (user {info.OwnerUserId}).", null, now);
                s.MarkStudio();
            });
            return true;
        }

        private static async Task<bool> CheckSessionAsync(StudioContext ctx, string? cookie, CancellationToken ct)
        {
            if (cookie == null || ctx.Session == null) return true;
            long id;
            string name;
            try { (id, name) = await ctx.Session.GetSignedInUserAsync(ct); }
            catch (RobloxSessionException ex)
            {
                if (!ex.Missing) StorePageChores.SessionRefused(ctx, ex);
                return true;
            }
            catch (Exception ex) when (Transient(ex, ct))
            {
                ctx.Log("[OmniBlox] could not ask Roblox whose the session is: " + ex.Message);
                return false;
            }
            var now = ctx.Clock();
            ctx.Store.Mutate(s =>
            {
                var account = s.Studio.RobloxAccount ??= new RobloxAccount { Method = RobloxConnection.Pasted, ConnectedUtc = now };
                if (account.UserId != 0 && account.UserId != id)
                    s.AddEvent(EventLevel.Warning, "store.session", $"The Roblox session changed accounts outside the dashboard: it is now {name} ({id}), not {account.Name} ({account.UserId}).", null, now);
                account.UserId = id;
                account.Name = name;
                account.VerifiedUtc = now;
                s.MarkStudio();
            });
            return true;
        }

        private static bool Transient(Exception ex, CancellationToken ct) =>
            ex is RobloxApiException or HttpRequestException or TimeoutException or Newtonsoft.Json.JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested;

        private static string Print(string? secret) =>
            string.IsNullOrEmpty(secret) ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))[..16];

        // ─────────────── what the answers mean ───────────────

        /// <summary>The account the games' experiences belong to; null for a group (keys there are the group's business) or when unset.</summary>
        private static long? Creator(StudioContext ctx) => ctx.Settings.CreatorGroupId != null ? null : ctx.Settings.CreatorUserId;

        /// <summary>The user the API key belongs to, when Roblox said and it is not the games' account.</summary>
        public static long? KeyIsOthers(StudioContext ctx) =>
            Creator(ctx) is long creator && ctx.Store.Read(s => s.Studio.ApiKey?.OwnerUserId) is long owner && owner > 0 && owner != creator ? owner : null;

        /// <summary>The user the connected session belongs to, when it is not the games' account.</summary>
        public static long? SessionIsOthers(StudioContext ctx) =>
            Creator(ctx) is long creator && ctx.Session is { HasSession: true } && ctx.Store.Read(s => s.Studio.RobloxAccount?.UserId) is long user && user > 0 && user != creator ? user : null;

        /// <summary>
        /// Why Roblox will refuse the studio's engine tests now, or null: the key is another account's, Roblox does not
        /// know it, or Roblox already refused its engine tests (an open owner task, closed when the key changes or the owner
        /// marks it done).
        /// </summary>
        public static string? Refusal(StudioContext ctx)
        {
            if (KeyIsOthers(ctx) is long owner)
                return $"the API key belongs to Roblox user {owner}, not to user {Creator(ctx)}, who owns the games' experiences";
            return ctx.Store.Read(s =>
            {
                if (s.Studio.OwnerTasks.Any(t => t.Status == OwnerTaskStatus.Open && t.DedupeKey == UnknownKey)) return "Roblox refuses the API key";
                if (s.Studio.OwnerTasks.Any(t => t.Status == OwnerTaskStatus.Open && t.DedupeKey == EngineRefusedKey)) return "Roblox refused the API key's engine tests";
                return null;
            });
        }

        /// <summary>Files the owner's task when the key or the session is another account's than the games', and closes it once neither is.</summary>
        public static void Review(StudioContext ctx)
        {
            var now = ctx.Clock();
            string? detail = MismatchDetail(ctx);
            bool keyIsOthers = KeyIsOthers(ctx) != null;
            ctx.Store.Mutate(s =>
            {
                if (detail == null) s.ResolveOwnerTasks(MismatchKey, now);
                else s.FileOwnerTask(OwnerTaskKind.ApiKey, MismatchKey, "The Roblox key or session is another account's than the games'", detail, null, now);
                // That is why Roblox refused the engine tests: one task, with the real reason, not a second about permissions.
                if (keyIsOthers) s.ResolveOwnerTasks(EngineRefusedKey, now);
            });
        }

        private static string? MismatchDetail(StudioContext ctx)
        {
            long? keyOwner = KeyIsOthers(ctx), sessionUser = SessionIsOthers(ctx);
            if (keyOwner == null && sessionUser == null) return null;
            long creator = Creator(ctx)!.Value;
            var (key, account) = ctx.Store.Read(s => (s.Studio.ApiKey?.Name, s.Studio.RobloxAccount?.Name));
            var whose = new List<string>();
            if (keyOwner != null) whose.Add($"the API key \"{key}\" was made on Roblox user {keyOwner}");
            if (sessionUser != null) whose.Add($"the connected session is {account} (user {sessionUser})");
            long other = keyOwner ?? sessionUser!.Value;
            var sb = new StringBuilder();
            sb.Append($"The studio's games and every experience they use belong to Roblox user {creator} (OmniBlox_CreatorUserId), but {string.Join(" and ", whose)}. ");
            if (keyOwner != null)
                sb.Append("A key can change only its own account's experiences, so Roblox refuses every place upload with \"User unauthorized to update place\", whatever permissions the key has: engine tests, launches and updates all fail. ");
            else
                sb.Append("Experiences the studio creates with that session land on that account, where the games are not. ");
            sb.Append("\nFix it one of two ways:\n");
            sb.Append($"- Keep the games on user {creator}: set a key made on that account and connect that account, both on the studio page's Roblox account card.\n");
            sb.Append($"- Move the studio to user {other}: Roblox account card → Connect → \"switch the studio to this account\", then set that account's key on the card. Games still in production get new experiences there; each launched game moves with its Manage → Move.\n");
            sb.Append("A key or session set on the Roblox account card is checked before it is kept; one pasted straight into OmniSettings is not, which is how this happened. Until it is fixed, run_tests runs games in the local engine.");
            return sb.ToString();
        }

        /// <summary>The engine-refusal task's text: what Roblox said, and what to check when the key has the permissions.</summary>
        public static string EngineRefusalDetail(StudioContext ctx, string title, ExperienceSlot slot, RobloxApiException ex)
        {
            var key = ctx.Store.Read(s => s.Studio.ApiKey is { } k ? OmniBloxStore.Clone(k) : null);
            string owner = Creator(ctx) is long creator ? $"user {creator}" : "the experience's owner";
            string whose = key != null
                ? $"Roblox says the key \"{key.Name}\" belongs to user {key.OwnerUserId}, the account the experiences belong to, so check those two permissions include experience {slot.UniverseId}, and that the key is not limited to IP addresses other than this server's."
                : $"If the key has both, check it was made on the account experience {slot.UniverseId} belongs to ({owner}): Roblox answers \"User unauthorized to update place\" to another account's key, whatever its permissions.";
            return $"Roblox refused to run \"{title}\" in its engine: {ex.Message}\n" +
                   $"Saving the test version needs universe-places:write on experience {slot.UniverseId}, and running it universe.place.luau-execution-session:write. {whose}\n" +
                   "Until then run_tests runs games in the local engine. Mark this done once it is fixed and Roblox's engine is tried again (a new API key is tried on its own).";
        }
    }
}
