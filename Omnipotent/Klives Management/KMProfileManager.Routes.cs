using System.Collections.Specialized;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Omnipotent.Profiles.Activity;
using Omnipotent.Profiles.Credentials;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Profiles.Sessions;
using Omnipotent.Services.KliveAPI.Caching;
using KliveApi = Omnipotent.Services.KliveAPI.KliveAPI;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Profiles
{
    public partial class KMProfileManager
    {
        private static readonly JsonSerializerSettings ApiJson = new()
        {
            Converters = { new StringEnumConverter() },
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            NullValueHandling = NullValueHandling.Include,
            // One casing for every payload (presence and activity DTOs included). Dictionary keys
            // are data — service names, permission keys — and keep their spelling.
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy(processDictionaryKeys: false, overrideSpecifiedNames: false),
            },
        };

        private KliveApi? routeApi;

        private async Task<KliveApi> ResolveApiAsync()
        {
            if (routeApi != null) return routeApi;
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (true)
            {
                var api = GetActiveServices().ToArray().OfType<KliveApi>().FirstOrDefault();
                if (api != null) return routeApi = api;
                if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("KliveAPI did not appear within 60 s; profile routes are unavailable.");
                await Task.Delay(100);
            }
        }

        private async Task Route(string path, PermissionDef permission, HttpMethod method, Func<UserRequest, Task> handler, long maxBody = 256 * 1024)
        {
            var api = await ResolveApiAsync();
            await api.CreateBufferedRoute(path, async req =>
            {
                // Profile and access data is live (presence, sessions, grants): never replay it.
                CacheDeps.MarkUncacheable("live profile data");
                try { await handler(req); }
                catch (ApiError e) { await Fail(req, e.Code, e.Message, e.Extra); }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, $"Error in {path}");
                    await Fail(req, HttpStatusCode.InternalServerError, "Something went wrong: " + ex.Message);
                }
            }, method, permission, maxBody);
        }

        private sealed class ApiError : Exception
        {
            public ApiError(HttpStatusCode code, string message, object? extra = null) : base(message) { Code = code; Extra = extra; }
            public HttpStatusCode Code { get; }
            public object? Extra { get; }
        }

        private static ApiError Bad(string message) => new(HttpStatusCode.BadRequest, message);
        private static ApiError NotFound(string message = "Profile not found.") => new(HttpStatusCode.NotFound, message);
        private static ApiError Forbidden(string message) => new(HttpStatusCode.Forbidden, message);
        private static ApiError Conflict(string message) => new(HttpStatusCode.Conflict, message);

        /// <summary>Profiles aren't loaded yet (just after a restart): "try again", never "wrong password".</summary>
        private static ApiError StartingUp() => new(HttpStatusCode.ServiceUnavailable,
            "Klives Management is starting up. Try again in a moment.", new { reason = "Starting", retryAfterSeconds = 2 });

        private static Task Ok(UserRequest req, object payload, HttpStatusCode code = HttpStatusCode.OK)
            => req.ReturnResponse(JsonConvert.SerializeObject(payload, ApiJson), "application/json", NoStore(), code);

        private static Task Fail(UserRequest req, HttpStatusCode code, string error, object? extra = null)
        {
            var body = extra == null ? JObject.FromObject(new { error }) : JObject.FromObject(extra, JsonSerializer.Create(ApiJson));
            body["error"] = error;
            return req.ReturnResponse(body.ToString(Formatting.None), "application/json", NoStore(), code);
        }

        private static NameValueCollection NoStore() => new() { ["Cache-Control"] = "no-store" };

        private static JObject Body(UserRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.userMessageContent)) return new JObject();
            try
            {
                var token = JToken.Parse(req.userMessageContent);
                return token as JObject ?? new JObject { ["value"] = token };
            }
            catch (JsonException) { throw Bad("The request body must be JSON."); }
        }

        private static string? Str(JObject body, string name) => body.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var v) && v.Type != JTokenType.Null ? v.ToString() : null;
        private static bool? Bool(JObject body, string name) => body.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var v) && v.Type == JTokenType.Boolean ? v.Value<bool>() : null;
        private static int? Int(JObject body, string name) => body.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var v) && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) ? v.Value<int>() : null;

        private static string ClientIp(UserRequest req)
        {
            try { return Omnipotent.Services.OmniDefence.OmniDefence.ExtractClientIp(req.req); }
            catch { return req.req?.RemoteEndPoint?.Address.ToString() ?? ""; }
        }

        private KMProfile Actor(UserRequest req) => req.user ?? throw new ApiError(HttpStatusCode.Unauthorized, "Sign in first.");

        private KMProfile RequireProfile(string? id) => GetProfileByIDFast(id) ?? throw NotFound();

        // ───────────────────────────── hierarchy & delegation ─────────────────────────────

        /// <summary>
        /// "Who is above whom": the owner manages everyone else; any other manager only profiles
        /// ranked strictly below them. Nobody manages themselves through these routes.
        /// </summary>
        public static bool CanManage(KMProfile actor, KMProfile target)
        {
            if (actor.UserID == target.UserID) return false;
            if (target.IsOwner) return false;
            if (actor.IsOwner) return true;
            return target.Rank < actor.Rank;
        }

        private static void EnsureCanManage(KMProfile actor, KMProfile target)
        {
            if (actor.UserID == target.UserID) throw Forbidden("You can't change your own access. Ask someone ranked above you.");
            if (target.IsOwner) throw Forbidden("Only Klives can change the owner profile.");
            if (!CanManage(actor, target)) throw Forbidden($"{target.Name} is not ranked below you.");
        }

        /// <summary>Ranks this actor may give: the owner up to Admin, anyone else strictly below themselves.</summary>
        public static IReadOnlyList<ProfileRank> AssignableRanks(KMProfile actor)
        {
            var all = new[] { ProfileRank.Guest, ProfileRank.Manager, ProfileRank.Associate, ProfileRank.Admin };
            return actor.IsOwner ? all : all.Where(r => r < actor.Rank).ToList();
        }

        private static ProfileRank ParseRank(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw Bad("A rank is required.");
            if (int.TryParse(value, out int n) && Enum.IsDefined(typeof(ProfileRank), n)) return (ProfileRank)n;
            if (Enum.TryParse<ProfileRank>(value, true, out var r)) return r;
            throw Bad($"Unknown rank '{value}'.");
        }

        /// <summary>
        /// May <paramref name="actor"/> add, remove or change <paramref name="key"/> on someone else?
        /// The owner may hand out anything; others only keys they hold themselves, never owner-only keys.
        /// </summary>
        internal static string? CannotDelegate(KMProfile actor, string key, DateTime now)
        {
            if (actor.IsOwner) return null;
            var def = PermissionCatalog.Get(key);
            if (def?.OwnerGrantOnly == true) return $"Only Klives can hand out '{def.Title}'.";
            if (!actor.HoldsPermission(key, now)) return $"You can't hand out '{def?.Title ?? key}' because you don't have it.";
            return null;
        }

        // ───────────────────────────── DTOs ─────────────────────────────

        private object Me(KMProfile p, KMSession? session, string? method)
        {
            DateTime now = DateTime.UtcNow;
            var granted = p.EffectiveKeys(now).OrderBy(k => k, StringComparer.Ordinal).ToList();
            return new
            {
                userId = p.UserID,
                name = p.Name,
                rank = p.Rank.ToString(),
                rankValue = (int)p.Rank,
                isOwner = p.IsOwner,
                canLogin = p.CanLogin,
                discordId = p.DiscordID,
                createdUtc = ToUtc(p.CreationDate),
                // What the gate would allow right now (suspension and read-only applied), so the
                // site can hide and disable exactly what would be refused...
                permissions = granted.Where(k => PermissionCatalog.Get(k) is PermissionDef def && AccessEvaluator.Can(p, def, now)).ToList(),
                // ...and everything held, for "your permissions".
                grantedPermissions = granted,
                accessVersion = p.AccessVersion,
                suspended = p.IsSuspended(now),
                suspendedUntilUtc = p.IsSuspended(now) ? p.SuspendedUntilUtc : null,
                suspensionReason = p.IsSuspended(now) ? p.SuspensionReason : null,
                readOnly = p.ReadOnly,
                sessionId = session?.SessionId,
                authMethod = method,
                assignableRanks = AssignableRanks(p).Select(r => r.ToString()).ToList(),
            };
        }

        private object ProfileRow(KMProfile p, KMProfile viewer, Dictionary<string, long>? lastSeen,
            Dictionary<string, (long Requests, long Denied)>? counts24h, bool detailed)
        {
            DateTime now = DateTime.UtcNow;
            var presence = Presence.Summarize(p.UserID, now);
            DateTime? lastSeenUtc = null;
            if (lastSeen != null && lastSeen.TryGetValue(p.UserID, out long ms)) lastSeenUtc = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            if (presence.LastHeartbeatUtc != null && (lastSeenUtc == null || presence.LastHeartbeatUtc > lastSeenUtc)) lastSeenUtc = presence.LastHeartbeatUtc;
            var activeGrants = p.Grants.Where(g => g.IsActive(now)).ToList();
            var (req24, den24) = counts24h != null && counts24h.TryGetValue(p.UserID, out var c) ? c : (0L, 0L);

            var row = new Dictionary<string, object?>
            {
                ["userId"] = p.UserID,
                ["name"] = p.Name,
                ["rank"] = p.Rank.ToString(),
                ["rankValue"] = (int)p.Rank,
                ["isOwner"] = p.IsOwner,
                ["canLogin"] = p.CanLogin,
                ["readOnly"] = p.ReadOnly,
                ["suspended"] = p.IsSuspended(now),
                ["suspendedUntilUtc"] = p.IsSuspended(now) ? p.SuspendedUntilUtc : null,
                ["suspensionReason"] = p.IsSuspended(now) ? p.SuspensionReason : null,
                ["discordId"] = p.DiscordID,
                ["createdUtc"] = ToUtc(p.CreationDate),
                ["createdById"] = p.CreatedById,
                ["lastLoginUtc"] = p.LastLoginUtc,
                ["lastSeenUtc"] = lastSeenUtc,
                ["allowPasswordApiAccess"] = p.AllowPasswordApiAccess,
                ["accessVersion"] = p.AccessVersion,
                ["grantCount"] = p.IsOwner ? PermissionCatalog.All.Count : activeGrants.Count,
                ["temporaryGrantCount"] = activeGrants.Count(g => g.ExpiresUtc != null),
                ["sessions"] = Sessions.ActiveCount(p.UserID),
                ["requests24h"] = req24,
                ["denied24h"] = den24,
                ["presence"] = new
                {
                    state = presence.State,
                    connections = presence.Connections,
                    currentPath = presence.CurrentPath,
                    currentTitle = presence.CurrentTitle,
                    onPageSinceUtc = presence.OnPageSinceUtc,
                    lastHeartbeatUtc = presence.LastHeartbeatUtc,
                },
                ["manageable"] = CanManage(viewer, p),
                ["isYou"] = viewer.UserID == p.UserID,
            };
            if (detailed)
            {
                row["presence"] = presence;
                row["grants"] = p.Grants.OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
                {
                    var def = PermissionCatalog.Get(g.Key);
                    return new
                    {
                        key = g.Key,
                        title = def?.Title ?? g.Key,
                        service = def?.Service,
                        tier = def?.Tier,
                        known = def != null,
                        active = g.IsActive(now),
                        grantedUtc = g.GrantedUtc,
                        grantedById = g.GrantedById,
                        grantedByName = g.GrantedById == "migration" ? "Rank conversion" : GetProfileByIDFast(g.GrantedById)?.Name,
                        expiresUtc = g.ExpiresUtc,
                        note = g.Note,
                    };
                }).ToList();
                row["effective"] = p.EffectiveKeys(now).OrderBy(k => k, StringComparer.Ordinal).ToList();
            }
            return row;
        }

        private static DateTime ToUtc(DateTime local) => local.Kind == DateTimeKind.Utc ? local : local.ToUniversalTime();

        private object SessionRow(KMSession s, string? currentSessionId)
        {
            var tabs = Presence.All.Where(c => c.SessionId == s.SessionId).ToList();
            return new
            {
                sessionId = s.SessionId,
                kind = s.Kind,
                label = s.Label,
                userAgent = s.UserAgent,
                ip = s.Ip,
                createdUtc = s.CreatedUtc,
                lastSeenUtc = s.LastSeenUtc,
                expiresUtc = s.ExpiresUtc,
                active = s.IsActive(DateTime.UtcNow),
                revokedUtc = s.RevokedUtc,
                revokeReason = s.RevokeReason,
                current = s.SessionId == currentSessionId,
                online = tabs.Count > 0,
                tabs = tabs.Select(t => new { path = t.Path, title = t.Title, visible = t.Visible, lastHeartbeatUtc = t.LastHeartbeatUtc }).ToList(),
            };
        }

        // ───────────────────────────── routes ─────────────────────────────

        private async void CreateRoutes()
        {
            try
            {
                await CreateAuthRoutes();
                await CreateSelfRoutes();
                await CreateDirectoryRoutes();
                await CreatePermissionRoutes();
                await CreateAccessControlRoutes();
                await CreateActivityRoutes();
                await CreateLegacyRoutes();
                await CreateLiveRoutes();
                ServiceLog("Profile routes registered.");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Failed to register profile routes.");
            }
        }

        private async Task CreateAuthRoutes()
        {
            // Sign in with a password; returns a revocable session token for this device.
            await Route("/KMProfiles/Login", Perms.Public, HttpMethod.Post, async req =>
            {
                if (!loaded) throw StartingUp();
                string ip = ClientIp(req);
                DateTime now = DateTime.UtcNow;
                if (loginThrottle.RetryAfter(ip, now) is TimeSpan wait)
                {
                    var headers = NoStore();
                    headers["Retry-After"] = Math.Ceiling(wait.TotalSeconds).ToString();
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { error = "Too many attempts. Try again shortly.", retryAfterSeconds = (int)Math.Ceiling(wait.TotalSeconds) }),
                        "application/json", headers, (HttpStatusCode)429);
                    return;
                }

                var body = Body(req);
                string? password = Str(body, "password") ?? Str(body, "value");
                if (string.IsNullOrEmpty(password)) throw Bad("Enter your password.");

                var profile = await VerifyLoginPasswordAsync(password);
                if (profile == null)
                {
                    loginThrottle.Failed(ip, now);
                    AuthEvent("LoginFailed", ip, null, req.req?.UserAgent, null);
                    throw new ApiError(HttpStatusCode.Unauthorized, "That password doesn't match any profile.", new { reason = "InvalidCredential" });
                }
                if (!profile.CanLogin)
                {
                    loginThrottle.Failed(ip, now);
                    AuthEvent("LoginDisabled", ip, profile, req.req?.UserAgent, null);
                    RecordEvent(profile.UserID, "login.refused", null, new { reason = "ProfileDisabled" }, ip);
                    throw new ApiError(HttpStatusCode.Unauthorized, "This profile can't sign in right now.", new { reason = "ProfileDisabled" });
                }

                loginThrottle.Succeeded(ip);
                var (token, session) = Sessions.Issue(profile.UserID, ip, req.req?.UserAgent);
                var updated = await MutateProfileAsync(profile.UserID, p => p.LastLoginUtc = DateTime.UtcNow, accessChanged: false) ?? profile;
                RecordEvent(profile.UserID, "login", null, new { session = session.SessionId, device = session.Label }, ip);
                AuthEvent("Login", ip, profile, req.req?.UserAgent, session.Label);
                Audit(profile, "Auth", "Login", new { session = session.SessionId }, ip);
                if (!profile.IsOwner) _ = SafeDiscord($"{profile.Name} signed in to Klives Management ({session.Label ?? "unknown device"}).");

                await Ok(req, new
                {
                    token,
                    sessionId = session.SessionId,
                    expiresUtc = session.ExpiresUtc,
                    me = Me(updated, session, "session"),
                });
            });

            // Ends the calling session (this device only).
            await Route("/KMProfiles/Logout", Perms.SignedIn, HttpMethod.Post, async req =>
            {
                if (req.session != null)
                {
                    Sessions.Revoke(req.session.SessionId, req.user?.UserID, "Signed out");
                    RecordEvent(req.user!.UserID, "logout", null, new { session = req.session.SessionId }, ClientIp(req));
                }
                await Ok(req, new { signedOut = true });
            });

            // "May this credential use the site right now?" — answered for any credential.
            await Route("/KMProfiles/LoginStatus", Perms.Public, HttpMethod.Get, async req =>
            {
                // Public, so reachable before profiles load: "not found" then would sign the site out.
                if (!loaded)
                {
                    var retry = NoStore();
                    retry["Retry-After"] = "2";
                    await req.ReturnResponse("Starting", "text/plain", retry, HttpStatusCode.ServiceUnavailable);
                    return;
                }
                var auth = Authenticate(req.req?.Headers["Authorization"], ClientIp(req), touch: false);
                if (auth.Profile == null)
                {
                    string state = auth.Failure switch
                    {
                        AccessDenyReason.SessionRevoked => "SessionRevoked",
                        AccessDenyReason.SessionExpired => "SessionExpired",
                        _ => "ProfileNotFound",
                    };
                    await req.ReturnResponse(state, "text/plain", NoStore(), HttpStatusCode.Unauthorized);
                    return;
                }
                if (!auth.Profile.CanLogin)
                {
                    await req.ReturnResponse("ProfileDisabled", "text/plain", NoStore(), HttpStatusCode.Unauthorized);
                    return;
                }
                await req.ReturnResponse("Allowed", "text/plain", NoStore(), HttpStatusCode.OK);
            });
        }

        private async Task CreateSelfRoutes()
        {
            await Route("/KMProfiles/me", Perms.SignedIn, HttpMethod.Get, async req =>
            {
                var me = Actor(req);
                await Ok(req, Me(me, req.session, req.session != null ? "session" : "password"));
            });

            await Route("/KMProfiles/sessions/mine", Perms.SignedIn, HttpMethod.Get, async req =>
            {
                var me = Actor(req);
                await Ok(req, Sessions.ForProfile(me.UserID).Select(s => SessionRow(s, req.session?.SessionId)).ToList());
            });

            await Route("/KMProfiles/sessions/mine/revoke", Perms.SignedIn, HttpMethod.Post, async req =>
            {
                var me = Actor(req);
                var body = Body(req);
                string? sessionId = Str(body, "sessionId");
                int revoked;
                if (!string.IsNullOrEmpty(sessionId))
                {
                    var s = Sessions.Get(sessionId);
                    if (s == null || s.ProfileId != me.UserID) throw NotFound("Session not found.");
                    revoked = Sessions.Revoke(sessionId, me.UserID, "Signed out from another device") ? 1 : 0;
                }
                else
                {
                    revoked = Sessions.RevokeAll(me.UserID, me.UserID, "Signed out everywhere else", req.session?.SessionId);
                }
                RecordEvent(me.UserID, "session.revoked", me, new { revoked, sessionId }, ClientIp(req));
                await Ok(req, new { revoked });
            });

            await Route("/KMProfiles/password/change", Perms.SignedIn, HttpMethod.Post, async req =>
            {
                var me = Actor(req);
                var body = Body(req);
                string current = Str(body, "currentPassword") ?? "";
                string next = Str(body, "newPassword") ?? "";
                var verified = await VerifyLoginPasswordAsync(current);
                if (verified == null || verified.UserID != me.UserID) throw Forbidden("Your current password is wrong.");
                ValidateNewPassword(next, me.UserID);
                await MutateProfileAsync(me.UserID, p => ApplyPassword(p, next), accessChanged: false);
                int others = Sessions.RevokeAll(me.UserID, me.UserID, "Password changed", req.session?.SessionId);
                RecordEvent(me.UserID, "password.changed", me, new { signedOutSessions = others }, ClientIp(req));
                Audit(me, "Credentials", "ChangeOwnPassword", null, ClientIp(req));
                await Ok(req, new { changed = true, signedOutSessions = others });
            });
        }

        private void ValidateNewPassword(string password, string? exceptProfileId)
        {
            if (password.Length < 8) throw Bad("Passwords need at least 8 characters.");
            if (password.Length > 256) throw Bad("That password is too long.");
            if (ProfileCredentials.IsSessionToken(password)) throw Bad("Passwords can't start with 'kms_'.");
            if (!IsPasswordAvailable(password, exceptProfileId))
                throw Conflict("Another profile already uses that password. Choose a different one.");
        }

        private async Task CreateDirectoryRoutes()
        {
            await Route("/KMProfiles/list", ProfilesPerms.DirectoryView, HttpMethod.Get, async req =>
            {
                var viewer = Actor(req);
                var lastSeen = Activity?.GetLastSeen();
                var counts = Activity?.GetCountsSince(DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds());
                var rows = profiles
                    .OrderByDescending(p => p.IsOwner)
                    .ThenByDescending(p => (int)p.Rank)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => ProfileRow(p, viewer, lastSeen, counts, detailed: false))
                    .ToList();
                await Ok(req, new
                {
                    profiles = rows,
                    assignableRanks = AssignableRanks(viewer).Select(r => r.ToString()).ToList(),
                    acceptPasswordAsBearer,
                });
            });

            await Route("/KMProfiles/get", ProfilesPerms.DirectoryView, HttpMethod.Get, async req =>
            {
                var viewer = Actor(req);
                var p = RequireProfile(req.userParameters.Get("id"));
                var lastSeen = Activity?.GetLastSeen();
                var counts = Activity?.GetCountsSince(DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds());
                // Grants are only shown to someone who may see permissions.
                bool detailed = AccessEvaluator.Can(viewer, ProfilesPerms.PermissionsView) || viewer.UserID == p.UserID;
                await Ok(req, ProfileRow(p, viewer, lastSeen, counts, detailed));
            });

            await Route("/KMProfiles/presence", ProfilesPerms.DirectoryView, HttpMethod.Get, async req =>
            {
                DateTime now = DateTime.UtcNow;
                await Ok(req, profiles.Select(p => new { userId = p.UserID, name = p.Name, presence = Presence.Summarize(p.UserID, now) }).ToList());
            });

            await Route("/KMProfiles/create", ProfilesPerms.Create, HttpMethod.Post, async req =>
            {
                var actor = Actor(req);
                var body = Body(req);
                string name = (Str(body, "name") ?? "").Trim();
                if (name.Length is < 1 or > 48) throw Bad("Give the profile a name (up to 48 characters).");
                if (profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw Conflict($"A profile called '{name}' already exists.");
                ProfileRank rank = ParseRank(Str(body, "rank") ?? "Guest");
                if (rank == ProfileRank.Klives) throw Forbidden("The Klives rank belongs to the owner alone.");
                if (!AssignableRanks(actor).Contains(rank)) throw Forbidden($"You can only create profiles ranked below you.");

                string? password = Str(body, "password");
                bool generated = string.IsNullOrEmpty(password) || Bool(body, "generatePassword") == true;
                if (generated)
                {
                    do { password = ProfileCredentials.GeneratePassword(); } while (!IsPasswordAvailable(password!, null));
                }
                else ValidateNewPassword(password!, null);

                var grants = new List<PermissionGrant>();
                if (body["grants"] is JArray requested && requested.Count > 0)
                {
                    if (!AccessEvaluator.Can(actor, ProfilesPerms.PermissionsGrant))
                        throw Forbidden("You can create profiles but not give them permissions.");
                    grants = ParseGrants(requested, actor, existing: null);
                }

                bool apiAccess = Bool(body, "allowPasswordApiAccess") == true;
                var created = await CreateProfileCoreAsync(name, rank, password!, isOwner: false, createdById: actor.UserID,
                    discordId: Str(body, "discordId"), grants: grants, allowPasswordApiAccess: apiAccess);
                RecordEvent(created.UserID, "profile.created", actor, new { rank = created.Rank.ToString(), permissions = grants.Count }, ClientIp(req));
                Audit(actor, "Profile", "CreateProfile", new { created.UserID, created.Name, rank = created.Rank.ToString(), permissions = grants.Count }, ClientIp(req));
                _ = SafeDiscord($"{actor.Name} created the profile **{created.Name}** ({created.Rank}, {grants.Count} permissions).");
                await Ok(req, new
                {
                    profile = ProfileRow(created, actor, null, null, detailed: true),
                    password = generated ? password : null,
                }, HttpStatusCode.Created);
            });

            await Route("/KMProfiles/update", ProfilesPerms.Edit, HttpMethod.Post, async req =>
            {
                var actor = Actor(req);
                var body = Body(req);
                var target = RequireProfile(Str(body, "id"));
                bool selfOwner = actor.IsOwner && target.UserID == actor.UserID;
                if (!selfOwner) EnsureCanManage(actor, target);

                string? newName = Str(body, "name")?.Trim();
                string? newDiscord = body.ContainsKey("discordId") ? (Str(body, "discordId") ?? "") : null;
                ProfileRank? newRank = Str(body, "rank") is string r ? ParseRank(r) : null;

                if (newName != null)
                {
                    if (newName.Length is < 1 or > 48) throw Bad("Names are 1–48 characters.");
                    if (profiles.Any(p => p.UserID != target.UserID && p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                        throw Conflict($"A profile called '{newName}' already exists.");
                }
                if (newRank != null && newRank != target.Rank)
                {
                    if (target.IsOwner) throw Forbidden("The owner's rank can't change.");
                    if (newRank == ProfileRank.Klives) throw Forbidden("The Klives rank belongs to the owner alone.");
                    if (!AssignableRanks(actor).Contains(newRank.Value)) throw Forbidden("You can only give ranks below your own.");
                }

                var before = target;
                var updated = await MutateProfileAsync(target.UserID, p =>
                {
                    if (newName != null) p.Name = newName;
                    if (newDiscord != null) p.DiscordID = newDiscord.Length == 0 ? null : newDiscord;
                    if (newRank != null && !p.IsOwner) p.Rank = newRank.Value;
                }, accessChanged: newRank != null && newRank != before.Rank);
                var changes = new
                {
                    name = newName != null && newName != before.Name ? new { from = before.Name, to = newName } : null,
                    rank = newRank != null && newRank != before.Rank ? new { from = before.Rank.ToString(), to = newRank.ToString() } : null,
                    discord = newDiscord != null && newDiscord != (before.DiscordID ?? "") ? true : (bool?)null,
                };
                RecordEvent(target.UserID, "profile.updated", actor, changes, ClientIp(req));
                Audit(actor, "Profile", "UpdateProfile", new { target.UserID, changes }, ClientIp(req));
                if (changes.rank != null)
                    _ = SafeDiscord($"{actor.Name} changed {before.Name}'s rank from {before.Rank} to {newRank}.");
                await Ok(req, ProfileRow(updated!, actor, null, null, detailed: AccessEvaluator.Can(actor, ProfilesPerms.PermissionsView)));
            });

            await Route("/KMProfiles/delete", ProfilesPerms.Delete, HttpMethod.Post, async req =>
            {
                var actor = Actor(req);
                var body = Body(req);
                var target = RequireProfile(Str(body, "id"));
                EnsureCanManage(actor, target);
                if (!string.Equals(Str(body, "confirmName")?.Trim(), target.Name, StringComparison.Ordinal))
                    throw Bad($"Type '{target.Name}' to confirm.");
                await DeleteProfileCoreAsync(target.UserID);
                RecordEvent(target.UserID, "profile.deleted", actor, null, ClientIp(req));
                Audit(actor, "Profile", "DeleteProfile", new { target.UserID, target.Name }, ClientIp(req));
                _ = SafeDiscord($"{actor.Name} deleted the profile **{target.Name}**.");
                await Ok(req, new { deleted = true });
            });
        }

        private async Task CreatePermissionRoutes()
        {
            // The catalog is not secret (titles and descriptions); every signed-in profile can load it
            // to label what it may and may not do. Route lists need permissions.view.
            await Route("/KMProfiles/permissions/catalog", Perms.SignedIn, HttpMethod.Get, async req =>
            {
                var viewer = Actor(req);
                bool withRoutes = AccessEvaluator.Can(viewer, ProfilesPerms.PermissionsView);
                Dictionary<string, List<object>>? routes = withRoutes ? await RoutesByPermissionAsync() : null;
                var services = PermissionCatalog.All
                    .GroupBy(d => d.ServiceKey)
                    .Select(g => new
                    {
                        key = g.Key,
                        name = g.First().Service,
                        permissions = g.Select(d => new
                        {
                            key = d.Key,
                            area = d.Area,
                            tier = d.Tier,
                            tierValue = (int)d.Tier,
                            title = d.Title,
                            description = d.Description,
                            sensitive = d.Sensitive,
                            implies = d.Implies,
                            ownerGrantOnly = d.OwnerGrantOnly,
                            legacyRank = d.LegacyRank.ToString(),
                            routes = routes == null ? null : routes.GetValueOrDefault(d.Key) ?? new List<object>(),
                        }).ToList(),
                    })
                    .OrderBy(s => s.name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                await Ok(req, new
                {
                    version = PermissionCatalog.Version,
                    tiers = Enum.GetValues<PermissionTier>().Select(t => new { value = (int)t, name = t.ToString(), description = TierDescription(t) }),
                    services,
                    publicRoutes = routes?.GetValueOrDefault(Perms.Public.Key),
                    signedInRoutes = routes?.GetValueOrDefault(Perms.SignedIn.Key),
                });
            });

            // Self-check: permissions that unlock nothing (and so can be granted to no effect).
            await Route("/KMProfiles/permissions/audit", ProfilesPerms.PermissionsView, HttpMethod.Get, async req =>
            {
                var routes = await RoutesByPermissionAsync();
                var orphans = PermissionCatalog.All.Where(d => !routes.ContainsKey(d.Key) && d.Refines.Count == 0 && !d.Key.StartsWith("klivetools.tool.", StringComparison.Ordinal))
                    .Select(d => new { d.Key, d.Title, d.Service }).ToList();
                var stale = profiles.SelectMany(p => p.Grants.Where(g => !PermissionCatalog.IsKnown(g.Key)).Select(g => new { profile = p.Name, g.Key })).ToList();
                await Ok(req, new
                {
                    permissions = PermissionCatalog.All.Count,
                    routes = routes.Values.Sum(v => v.Count),
                    permissionsWithoutRoutes = orphans,
                    grantsForUnknownPermissions = stale,
                });
            });

            // Replaces a profile's grants with the given set (the console sends the full desired set).
            await Route("/KMProfiles/permissions/set", ProfilesPerms.PermissionsGrant, HttpMethod.Post, async req =>
            {
                var actor = Actor(req);
                var body = Body(req);
                var target = RequireProfile(Str(body, "id"));
                EnsureCanManage(actor, target);
                if (body["grants"] is not JArray requested) throw Bad("Send the full list of grants.");
                long? expectedVersion = body["accessVersion"]?.Type == JTokenType.Integer ? body["accessVersion"]!.Value<long>() : null;
                if (expectedVersion != null && expectedVersion != target.AccessVersion)
                    throw Conflict($"{target.Name}'s access changed while you were editing. Reload and try again.");

                var desired = ParseGrants(requested, actor, target);
                DateTime now = DateTime.UtcNow;
                var current = target.Grants.ToDictionary(g => g.Key, StringComparer.Ordinal);
                var wanted = desired.ToDictionary(g => g.Key, StringComparer.Ordinal);
                var added = wanted.Keys.Except(current.Keys).ToList();
                var removed = current.Keys.Except(wanted.Keys).ToList();
                var changed = wanted.Keys.Intersect(current.Keys)
                    .Where(k => wanted[k].ExpiresUtc != current[k].ExpiresUtc || wanted[k].Note != current[k].Note).ToList();

                foreach (var key in added.Concat(removed).Concat(changed))
                {
                    if (CannotDelegate(actor, key, now) is string why) throw Forbidden(why);
                }
                if (added.Count == 0 && removed.Count == 0 && changed.Count == 0)
                {
                    await Ok(req, new { unchanged = true, profile = ProfileRow(target, actor, null, null, detailed: true) });
                    return;
                }

                var merged = wanted.Values.Select(g => current.TryGetValue(g.Key, out var existing) && !changed.Contains(g.Key)
                    ? existing
                    : new PermissionGrant { Key = g.Key, GrantedUtc = now, GrantedById = actor.UserID, ExpiresUtc = g.ExpiresUtc, Note = g.Note }).ToList();
                var updated = await MutateProfileAsync(target.UserID, p => p.Grants = merged);

                var detail = new { added, removed, changed };
                RecordEvent(target.UserID, "access.permissions", actor, detail, ClientIp(req));
                Audit(actor, "Permission", "SetPermissions", new { target.UserID, target.Name, added, removed, changed }, ClientIp(req));
                _ = SafeDiscord($"{actor.Name} changed {target.Name}'s permissions: +{added.Count} −{removed.Count}" +
                    (changed.Count > 0 ? $" ~{changed.Count}" : "") + ".");
                await Ok(req, new { added, removed, changed, profile = ProfileRow(updated!, actor, null, null, detailed: true) });
            });
        }

        /// <summary>Parses <c>[{key, expiresUtc?, note?}]</c>; unknown keys are refused unless already granted.</summary>
        private static List<PermissionGrant> ParseGrants(JArray requested, KMProfile actor, KMProfile? existing)
        {
            var result = new Dictionary<string, PermissionGrant>(StringComparer.Ordinal);
            DateTime now = DateTime.UtcNow;
            foreach (var item in requested)
            {
                string? key = item.Type == JTokenType.String ? item.ToString() : item["key"]?.ToString();
                if (string.IsNullOrWhiteSpace(key)) continue;
                bool known = PermissionCatalog.IsKnown(key);
                bool alreadyHeld = existing?.Grants.Any(g => g.Key == key) == true;
                if (!known && !alreadyHeld) throw Bad($"Unknown permission '{key}'.");
                DateTime? expires = null;
                if (item is JObject o && o["expiresUtc"] is JToken e && e.Type != JTokenType.Null)
                {
                    if (!DateTime.TryParse(e.ToString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                        throw Bad($"'{e}' is not a valid expiry for {key}.");
                    if (parsed <= now) throw Bad($"The expiry for {key} is in the past.");
                    expires = parsed;
                }
                string? note = item is JObject n ? n["note"]?.ToString() : null;
                if (note != null && note.Length > 200) note = note[..200];
                result[key] = new PermissionGrant { Key = key, ExpiresUtc = expires, Note = string.IsNullOrWhiteSpace(note) ? null : note };
            }
            return result.Values.ToList();
        }

        private static string TierDescription(PermissionTier t) => t switch
        {
            PermissionTier.Glance => "Status, counts and summaries.",
            PermissionTier.Read => "Full records, history and content.",
            PermissionTier.Act => "Routine, mostly reversible operations.",
            PermissionTier.Manage => "Creating, deleting and configuring.",
            PermissionTier.Critical => "Live money, host control, secrets and other profiles.",
            _ => "",
        };

        /// <summary>Every registered route grouped by the permission key that gates it, plus in-handler refinements.</summary>
        private async Task<Dictionary<string, List<object>>> RoutesByPermissionAsync()
        {
            var api = await ResolveApiAsync();
            var map = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            foreach (var r in api.DescribeRoutes())
            {
                if (!map.TryGetValue(r.Permission.Key, out var list)) map[r.Permission.Key] = list = new List<object>();
                list.Add(new { path = r.Path, method = r.Method, kind = r.Kind });
            }
            foreach (var def in PermissionCatalog.All.Where(d => d.Refines.Count > 0))
            {
                if (!map.TryGetValue(def.Key, out var list)) map[def.Key] = list = new List<object>();
                foreach (var path in def.Refines) list.Add(new { path, method = (string?)null, kind = "refines" });
            }
            return map;
        }

        private async Task CreateAccessControlRoutes()
        {
            await Route("/KMProfiles/access/suspend", ProfilesPerms.AccessControl, HttpMethod.Post, async req =>
            {
                var actor = Actor(req);
                var body = Body(req);
                var target = RequireProfile(Str(body, "id"));
                EnsureCanManage(actor, target);
                DateTime now = DateTime.UtcNow;
                DateTime until;
                if (Int(body, "minutes") is int minutes && minutes > 0) until = now.AddMinutes(Math.Min(minutes, 60 * 24 * 365));
                else if (Str(body, "untilUtc") is string u && DateTime.TryParse(u, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) && parsed > now) until = parsed;
                else throw Bad("Say how long to suspend for.");
                string? reason = Str(body, "reason")?.Trim();
                if (reason?.Length > 300) reason = reason[..300];

                var updated = await MutateProfileAsync(target.UserID, p =>
                {
                    p.SuspendedUntilUtc = until;
                    p.SuspensionReason = string.IsNullOrWhiteSpace(reason) ? null : reason;
                    p.SuspendedById = actor.UserID;
                });
                RecordEvent(target.UserID, "access.suspended", actor, new { untilUtc = until, reason }, ClientIp(req));
                Audit(actor, "Access", "Suspend", new { target.UserID, target.Name, until, reason }, ClientIp(req));
                _ = SafeDiscord($"{actor.Name} suspended {target.Name} until {until:yyyy-MM-dd HH:mm} UTC" + (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}"));
                await Ok(req, ProfileRow(updated!, actor, null, null, detailed: false));
            });

            await Route("/KMProfiles/access/unsuspend", ProfilesPerms.AccessControl, HttpMethod.Post, async req =>
            {
                var actor = Actor(req);
                var target = RequireProfile(Str(Body(req), "id"));
                EnsureCanManage(actor, target);
                var updated = await MutateProfileAsync(target.UserID, p =>
                {
                    p.SuspendedUntilUtc = null;
                    p.SuspensionReason = null;
                    p.SuspendedById = null;
                });
                RecordEvent(target.UserID, "access.unsuspended", actor, null, ClientIp(req));
                Audit(actor, "Access", "Unsuspend", new { target.UserID, target.Name }, ClientIp(req));
                await Ok(req, ProfileRow(updated!, actor, null, null, detailed: false));
            });

            await Route("/KMProfiles/access/read-only", ProfilesPerms.AccessControl, HttpMethod.Post, async req =>
            {
                var actor = Actor(req);
                var body = Body(req);
                var target = RequireProfile(Str(body, "id"));
                EnsureCanManage(actor, target);
                bool enabled = Bool(body, "enabled") ?? throw Bad("Say whether read-only should be on or off.");
                var updated = await MutateProfileAsync(target.UserID, p => p.ReadOnly = enabled);
                RecordEvent(target.UserID, enabled ? "access.read-only.on" : "access.read-only.off", actor, null, ClientIp(req));
                Audit(actor, "Access", enabled ? "ReadOnlyOn" : "ReadOnlyOff", new { target.UserID, target.Name }, ClientIp(req));
                await Ok(req, ProfileRow(updated!, actor, null, null, detailed: false));
            });

            await Route("/KMProfiles/access/login", ProfilesPerms.AccessControl, HttpMethod.Post, async req =>
            {
                var actor = Actor(req);
                var body = Body(req);
                var target = RequireProfile(Str(body, "id"));
                EnsureCanManage(actor, target);
                bool enabled = Bool(body, "enabled") ?? throw Bad("Say whether login should be on or off.");
                var updated = await MutateProfileAsync(target.UserID, p => p.CanLogin = enabled);
                if (!enabled)
                {
                    PushToProfile(target.UserID, new { type = "session-state", state = "ProfileDisabled" });
                    Sessions.RevokeAll(target.UserID, actor.UserID, "Login turned off");
                }
                RecordEvent(target.UserID, enabled ? "access.login.on" : "access.login.off", actor, null, ClientIp(req));
                Audit(actor, "Profile", "ChangeCanLogin", new { target.UserID, target.Name, canLogin = enabled }, ClientIp(req));
                _ = SafeDiscord($"{actor.Name} turned {(enabled ? "on" : "off")} sign-in for {target.Name}.");
                await Ok(req, ProfileRow(updated!, actor, null, null, detailed: false));
            });

            await Route("/KMProfiles/access/password-api", ProfilesPerms.AccessControl, HttpMethod.Post, async req =>
            {
                var actor = Actor(req);
                var body = Body(req);
                var target = RequireProfile(Str(body, "id"));
                EnsureCanManage(actor, target);
                bool enabled = Bool(body, "enabled") ?? throw Bad("Say whether password API access should be on or off.");
                var updated = await MutateProfileAsync(target.UserID, p => p.AllowPasswordApiAccess = enabled, accessChanged: false);
                RecordEvent(target.UserID, enabled ? "access.password-api.on" : "access.password-api.off", actor, null, ClientIp(req));
                await Ok(req, ProfileRow(updated!, actor, null, null, detailed: false));
            });

            await Route("/KMProfiles/sessions", ProfilesPerms.AccessControl, HttpMethod.Get, async req =>
            {
                var target = RequireProfile(req.userParameters.Get("id"));
                bool includeEnded = req.userParameters.Get("ended") == "1";
                await Ok(req, Sessions.ForProfile(target.UserID, includeEnded).Select(s => SessionRow(s, null)).ToList());
            });

            await Route("/KMProfiles/sessions/revoke", ProfilesPerms.AccessControl, HttpMethod.Post, async req =>
            {
                var actor = Actor(req);
                var body = Body(req);
                var target = RequireProfile(Str(body, "id"));
                EnsureCanManage(actor, target);
                string? sessionId = Str(body, "sessionId");
                string reason = $"Signed out by {actor.Name}";
                int revoked;
                if (!string.IsNullOrEmpty(sessionId))
                {
                    var s = Sessions.Get(sessionId);
                    if (s == null || s.ProfileId != target.UserID) throw NotFound("Session not found.");
                    revoked = Sessions.Revoke(sessionId, actor.UserID, reason) ? 1 : 0;
                }
                else
                {
                    revoked = Sessions.RevokeAll(target.UserID, actor.UserID, reason);
                }
                RecordEvent(target.UserID, "session.revoked", actor, new { revoked, sessionId }, ClientIp(req));
                Audit(actor, "Access", "RevokeSessions", new { target.UserID, target.Name, revoked }, ClientIp(req));
                await Ok(req, new { revoked });
            });

            await Route("/KMProfiles/credentials/reset", ProfilesPerms.CredentialsReset, HttpMethod.Post, async req =>
            {
                var actor = Actor(req);
                var body = Body(req);
                var target = RequireProfile(Str(body, "id"));
                EnsureCanManage(actor, target);
                string? password = Str(body, "password");
                bool generated = string.IsNullOrEmpty(password);
                if (generated)
                {
                    do { password = ProfileCredentials.GeneratePassword(); } while (!IsPasswordAvailable(password!, target.UserID));
                }
                else ValidateNewPassword(password!, target.UserID);
                await MutateProfileAsync(target.UserID, p => ApplyPassword(p, password!), accessChanged: false);
                PushToProfile(target.UserID, new { type = "session-state", state = "PasswordChanged" });
                int revoked = Sessions.RevokeAll(target.UserID, actor.UserID, "Password reset");
                RecordEvent(target.UserID, "password.reset", actor, new { signedOutSessions = revoked }, ClientIp(req));
                Audit(actor, "Permission", "ChangeProfilePassword", new { target.UserID, target.Name }, ClientIp(req));
                _ = SafeDiscord($"{actor.Name} reset {target.Name}'s password.");
                await Ok(req, new { reset = true, password = generated ? password : null, signedOutSessions = revoked });
            });
        }

        private async Task CreateActivityRoutes()
        {
            await Route("/KMProfiles/activity", ProfilesPerms.ActivityRead, HttpMethod.Get, async req =>
            {
                var target = RequireProfile(req.userParameters.Get("id"));
                var q = req.userParameters;
                long? before = long.TryParse(q.Get("before"), out var b) ? b : null;
                long? since = long.TryParse(q.Get("since"), out var s) ? s : null;
                int limit = int.TryParse(q.Get("limit"), out var l) ? l : 100;
                var types = string.IsNullOrWhiteSpace(q.Get("types")) ? null
                    : new HashSet<string>(q.Get("types")!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);
                string? service = string.IsNullOrWhiteSpace(q.Get("service")) ? null : q.Get("service");
                bool denied = q.Get("denied") == "1";
                var items = Activity?.GetTimeline(target.UserID, before, since, limit, types, service, denied) ?? new();
                await Ok(req, new { items, next = items.Count >= Math.Clamp(limit, 1, 500) ? items.Last().TsMs : (long?)null });
            });

            await Route("/KMProfiles/activity/summary", ProfilesPerms.ActivityRead, HttpMethod.Get, async req =>
            {
                var target = RequireProfile(req.userParameters.Get("id"));
                TimeSpan range = req.userParameters.Get("range") switch
                {
                    "7d" => TimeSpan.FromDays(7),
                    "30d" => TimeSpan.FromDays(30),
                    _ => TimeSpan.FromHours(24),
                };
                var summary = Activity?.GetSummary(target.UserID, range) ?? new ProfileActivityStore.ActivitySummary();
                await Ok(req, summary);
            });

            // Which granted permissions are actually used — the "least privilege" view.
            await Route("/KMProfiles/activity/usage", ProfilesPerms.ActivityRead, HttpMethod.Get, async req =>
            {
                var target = RequireProfile(req.userParameters.Get("id"));
                DateTime now = DateTime.UtcNow;
                var usage = (Activity?.GetPermissionUsage(target.UserID) ?? new()).ToDictionary(u => u.Key, StringComparer.Ordinal);
                var held = target.EffectiveKeys(now);
                var rows = PermissionCatalog.All.Where(d => held.Contains(d.Key) || usage.ContainsKey(d.Key)).Select(d =>
                {
                    usage.TryGetValue(d.Key, out var u);
                    return new
                    {
                        key = d.Key,
                        title = d.Title,
                        service = d.Service,
                        tier = d.Tier,
                        held = held.Contains(d.Key),
                        count = u?.Count ?? 0,
                        denied = u?.Denied ?? 0,
                        lastUsedMs = u?.LastMs,
                    };
                }).OrderBy(r => r.service).ThenBy(r => r.key).ToList();
                long staleCutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
                await Ok(req, new
                {
                    permissions = rows,
                    unusedFor30Days = target.IsOwner ? new List<string>() : rows.Where(r => r.held && (r.lastUsedMs == null || r.lastUsedMs < staleCutoff)).Select(r => r.key).ToList(),
                });
            });
        }

        /// <summary>
        /// Routes the previous website still calls. Kept until the new site is deployed everywhere.
        /// </summary>
        private async Task CreateLegacyRoutes()
        {
            await Route("/KMProfiles/AttemptLogin", Perms.Public, HttpMethod.Post, async req =>
            {
                if (!loaded) throw StartingUp();
                string ip = ClientIp(req);
                DateTime now = DateTime.UtcNow;
                if (loginThrottle.RetryAfter(ip, now) != null)
                {
                    await req.ReturnResponse("TooManyAttempts", "text/plain", NoStore(), (HttpStatusCode)429);
                    return;
                }
                string? password = null;
                try { password = JsonConvert.DeserializeObject<string>(req.userMessageContent ?? ""); } catch { }
                var profile = string.IsNullOrEmpty(password) ? null : await VerifyLoginPasswordAsync(password);
                if (profile == null)
                {
                    loginThrottle.Failed(ip, now);
                    AuthEvent("LoginFailed", ip, null, req.req?.UserAgent, "legacy");
                    await req.ReturnResponse("ProfileNotFound", "text/plain", NoStore(), HttpStatusCode.NotFound);
                    return;
                }
                if (!profile.CanLogin)
                {
                    await req.ReturnResponse("LoginDisabled", "text/plain", NoStore(), HttpStatusCode.Unauthorized);
                    return;
                }
                loginThrottle.Succeeded(ip);
                RecordEvent(profile.UserID, "login", null, new { legacy = true }, ip);
                AuthEvent("Login", ip, profile, req.req?.UserAgent, "legacy");
                if (!profile.IsOwner) _ = SafeDiscord($"{profile.Name} has logged into Klives Management.");
                await req.ReturnResponse("true", "application/json", NoStore());
            });

            await Route("/KMProfiles/GetCurrentProfile", Perms.SignedIn, HttpMethod.Get, async req =>
            {
                var me = Actor(req);
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    me.UserID,
                    me.Name,
                    me.CreationDate,
                    KlivesManagementRank = (int)me.Rank,
                    me.DiscordID,
                    me.CanLogin,
                    me.IsOwner,
                }), "application/json", NoStore());
            });

            await Route("/KMProfiles/GetAllProfiles", ProfilesPerms.DirectoryView, HttpMethod.Get, async req =>
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(profiles.Select(p => new
                {
                    p.UserID,
                    p.Name,
                    p.CreationDate,
                    KlivesManagementRank = (int)p.Rank,
                    Password = "***",
                    p.DiscordID,
                    p.CanLogin,
                })), "application/json", NoStore());
            });
        }
    }
}
