using System.Collections.Concurrent;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Profiles.Activity;
using Omnipotent.Profiles.Credentials;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Profiles.Sessions;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.KliveBot_Discord;

namespace Omnipotent.Profiles
{
    /// <summary>
    /// Klives Management profiles: who can sign in, what each profile may do (permissions), their
    /// sessions, what they are doing (activity + presence) and the live controls over all of it.
    ///
    /// Profiles are copy-on-write: every change clones the profile, applies the change, persists it
    /// and swaps the new instance in. A request holding a profile therefore always sees one
    /// consistent version, and readers never take a lock.
    /// </summary>
    public partial class KMProfileManager : OmniService
    {
        private const string profileFileExtension = ".kmp";
        public const int CurrentSchemaVersion = 2;

        private volatile IReadOnlyList<KMProfile> profiles = Array.Empty<KMProfile>();
        private readonly object indexLock = new();
        private Dictionary<string, KMProfile> profilesById = new(StringComparer.Ordinal);
        private Dictionary<string, KMProfile> profilesByLookup = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim mutationLock = new(1, 1);

        private ProfileCredentials? credentials;
        private readonly LoginThrottle loginThrottle = new();
        private volatile bool acceptPasswordAsBearer = true;
        private volatile bool loaded;

        /// <summary>Profiles migrated from ranks during this process run, with the rank they had.</summary>
        private readonly ConcurrentDictionary<string, ProfileRank> migratedThisRun = new(StringComparer.Ordinal);

        private string? internalOwnerToken;
        private readonly object internalTokenGate = new();

        public SessionStore Sessions { get; private set; } = null!;
        public ProfileActivityStore? Activity { get; private set; }
        public PresenceTracker Presence { get; } = new();

        /// <summary>Every profile (immutable snapshot).</summary>
        public IReadOnlyList<KMProfile> Profiles => profiles;
        public bool IsLoaded => loaded;

        /// <summary>
        /// Website requests may still send the profile password as the bearer credential until the
        /// new session-based site is live. Controlled by the OmniSetting
        /// <c>KMProfiles_AcceptPasswordAsBearer</c>; when it is off only profiles with
        /// <see cref="KMProfile.AllowPasswordApiAccess"/> may authenticate with a password.
        /// </summary>
        public bool AcceptPasswordAsBearer => acceptPasswordAsBearer;

        public KMProfileManager()
        {
            name = "Klives Management Profile Manager";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        private static string Dir => OmniPaths.GetPath(OmniPaths.GlobalPaths.KlivesManagementInfoDirectory);

        protected override async void ServiceMain()
        {
            try
            {
                PermissionCatalog.EnsureLoaded();
                PermissionCatalog.Registered += OnPermissionRegistered;

                Directory.CreateDirectory(OmniPaths.GetPath(OmniPaths.GlobalPaths.KlivesManagementProfilesDirectory));
                await LoadAllProfiles();

                credentials = new ProfileCredentials(Path.Combine(Dir, "credential-lookup.key"),
                    () => profiles.Any(p => !string.IsNullOrEmpty(p.PasswordLookup)),
                    msg => _ = ServiceLog(msg));
                credentials.VerifyKeyCheck(Path.Combine(Dir, "credential-lookup.check"));

                Sessions = new SessionStore(Path.Combine(Dir, "sessions.json"), msg => _ = ServiceLog(msg));
                Sessions.Load();
                Sessions.Revoked += OnSessionRevoked;

                await MigrateProfilesAsync();
                RebuildIndexes();
                loaded = true;

                Activity = new ProfileActivityStore(Path.Combine(Dir, "profile_activity.db"), msg => _ = ServiceLog(msg));
                Activity.Start();
                Presence.PageLeft += OnPageLeft;

                if (!profiles.Any())
                {
                    _ = RequestProfileFromKlives();
                }

                CreateRoutes();
                _ = MaintenanceLoop();
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Klives Management Profile Manager failed to start.");
            }
        }

        // ───────────────────────────── profile model ─────────────────────────────

        public class KMProfile
        {
            public string UserID = "";
            public string Name = "";
            public DateTime CreationDate;

            /// <summary>Hierarchy label only — see <see cref="ProfileRank"/>. Never grants access.</summary>
            [JsonProperty("KlivesManagementRank")]
            public ProfileRank Rank;

            /// <summary>Plaintext from before schema 2. Cleared by migration; never written again.</summary>
            [JsonProperty("Password", NullValueHandling = NullValueHandling.Ignore)]
            public string? LegacyPassword;

            /// <summary>HMAC of the password under the local lookup key (see <see cref="ProfileCredentials"/>).</summary>
            public string? PasswordLookup;
            /// <summary>Salted PBKDF2 hash of the password.</summary>
            public string? PasswordHash;
            public DateTime? PasswordChangedUtc;

            public string? DiscordID;
            public bool CanLogin { get; set; }

            /// <summary>May the password itself be used as an API credential (scripts, devices)?</summary>
            public bool AllowPasswordApiAccess;

            public string? CreatedById;
            public DateTime? LastLoginUtc;

            // ── Permission model (replaces rank-based access; see Permissions/) ──
            /// <summary>The owner (Klives): holds every current and future permission.</summary>
            public bool IsOwner;
            public List<PermissionGrant> Grants = new();
            public DateTime? SuspendedUntilUtc;
            public string? SuspensionReason;
            public string? SuspendedById;
            /// <summary>Read-only lockdown: every permission of tier Act or above is refused.</summary>
            public bool ReadOnly;
            /// <summary>Bumped on every access change; the website refreshes when it moves.</summary>
            public long AccessVersion;
            public int SchemaVersion;

            [JsonIgnore]
            private ProfileAccessSnapshot? accessSnapshot;

            public bool IsSuspended(DateTime nowUtc) => SuspendedUntilUtc is DateTime until && until > nowUtc;

            /// <summary>
            /// Whether an active grant (or a key implied by one) covers <paramref name="key"/>. The
            /// effective set is rebuilt only when grants change or a temporary grant lapses.
            /// </summary>
            public bool HoldsPermission(string key, DateTime nowUtc)
            {
                if (IsOwner) return true;
                return EffectiveKeys(nowUtc).Contains(key);
            }

            /// <summary>Every key this profile holds right now (owner: every catalog key).</summary>
            public IReadOnlySet<string> EffectiveKeys(DateTime nowUtc)
            {
                if (IsOwner) return PermissionCatalog.All.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
                var snapshot = accessSnapshot;
                if (snapshot == null || (snapshot.RebuildAfterUtc is DateTime rebuild && nowUtc >= rebuild))
                {
                    snapshot = ProfileAccessSnapshot.Build(Grants ?? new(), nowUtc);
                    accessSnapshot = snapshot;
                }
                return snapshot.Effective;
            }

            /// <summary>Drops the cached effective set after <see cref="Grants"/> changes.</summary>
            public void InvalidateAccessSnapshot() => accessSnapshot = null;

            public string CreateProfilePath()
            {
                return Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KlivesManagementProfilesDirectory), $"{UserID}profile{profileFileExtension}");
            }

            internal KMProfile Clone()
            {
                var copy = JsonConvert.DeserializeObject<KMProfile>(JsonConvert.SerializeObject(this))!;
                copy.Grants = (Grants ?? new()).Select(g => g.Clone()).ToList();
                return copy;
            }
        }

        // ───────────────────────────── loading & indexes ─────────────────────────────

        private async Task LoadAllProfiles()
        {
            var loadedProfiles = new List<KMProfile>();
            var files = Directory.GetFiles(OmniPaths.GetPath(OmniPaths.GlobalPaths.KlivesManagementProfilesDirectory))
                .Where(k => Path.GetExtension(k) == profileFileExtension);
            foreach (var file in files)
            {
                try
                {
                    string data = await GetDataHandler().ReadDataFromFile(file);
                    var profile = JsonConvert.DeserializeObject<KMProfile>(data);
                    if (profile != null && !string.IsNullOrWhiteSpace(profile.UserID))
                    {
                        profile.Grants ??= new();
                        loadedProfiles.Add(profile);
                    }
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, $"Could not read profile file {Path.GetFileName(file)}; it was skipped.");
                }
            }
            profiles = loadedProfiles.OrderBy(p => p.UserID, StringComparer.Ordinal).ToList();
            RebuildIndexes();
            ServiceLog($"Loaded {profiles.Count} Klives Management Profiles into memory.");
        }

        private void RebuildIndexes()
        {
            lock (indexLock)
            {
                var byId = new Dictionary<string, KMProfile>(StringComparer.Ordinal);
                var byLookup = new Dictionary<string, KMProfile>(StringComparer.OrdinalIgnoreCase);
                foreach (var profile in profiles.OrderBy(p => p.CreationDate))
                {
                    byId[profile.UserID] = profile;
                    if (!string.IsNullOrWhiteSpace(profile.PasswordLookup) && !byLookup.ContainsKey(profile.PasswordLookup))
                        byLookup[profile.PasswordLookup] = profile;
                }
                profilesById = byId;
                profilesByLookup = byLookup;
            }
        }

        public KMProfile? GetProfileByIDFast(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            CacheDeps.NoteRead("kmprofiles");
            lock (indexLock)
            {
                return profilesById.TryGetValue(id, out var profile) ? profile : null;
            }
        }

        public Task<KMProfile?> GetProfileByID(string id) => Task.FromResult(GetProfileByIDFast(id));

        /// <summary>
        /// Resolves a credential (session token or password) to its profile. Kept under its old name
        /// for callers that still invoke it by reflection.
        /// </summary>
        public Task<KMProfile?> GetProfileByPassword(string credential) => Task.FromResult(Authenticate(credential).Profile);

        public KMProfile? GetOwner() => profiles.FirstOrDefault(p => p.IsOwner);

        // ───────────────────────────── authentication ─────────────────────────────

        public readonly record struct AuthResult(KMProfile? Profile, KMSession? Session, AccessDenyReason Failure, string? Method);

        /// <summary>
        /// Resolves an Authorization value: a session token (<c>kms_…</c>, optionally "Bearer "-prefixed)
        /// or a password. Never throws, never waits.
        /// </summary>
        public AuthResult Authenticate(string? rawCredential, string? ip = null, bool touch = true)
        {
            string? credential = ProfileCredentials.NormalizeCredential(rawCredential);
            if (credential == null) return new AuthResult(null, null, AccessDenyReason.NoCredential, null);
            if (!loaded) return new AuthResult(null, null, AccessDenyReason.InvalidCredential, null);
            DateTime now = DateTime.UtcNow;

            if (ProfileCredentials.IsSessionToken(credential))
            {
                var validation = Sessions.Validate(credential, now);
                if (validation.Session == null) return new AuthResult(null, null, validation.Failure, "session");
                var profile = GetProfileByIDFast(validation.Session.ProfileId);
                if (profile == null)
                {
                    Sessions.Revoke(validation.Session.SessionId, null, "Profile deleted");
                    return new AuthResult(null, null, AccessDenyReason.InvalidCredential, "session");
                }
                if (touch) Sessions.Touch(validation.Session, now, ip);
                return new AuthResult(profile, validation.Session, AccessDenyReason.None, "session");
            }

            var byPassword = FindByPassword(credential);
            if (byPassword == null) return new AuthResult(null, null, AccessDenyReason.InvalidCredential, "password");
            if (!byPassword.AllowPasswordApiAccess && !acceptPasswordAsBearer)
                return new AuthResult(null, null, AccessDenyReason.InvalidCredential, "password");
            return new AuthResult(byPassword, null, AccessDenyReason.None, "password");
        }

        /// <summary>O(1): HMAC lookup. Falls back to plaintext only for a profile whose migration failed.</summary>
        private KMProfile? FindByPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return null;
            if (credentials != null)
            {
                string lookup = credentials.ComputeLookup(password);
                lock (indexLock)
                {
                    if (profilesByLookup.TryGetValue(lookup, out var hit)) return hit;
                }
            }
            foreach (var p in profiles)
            {
                if (p.PasswordHash == null && p.LegacyPassword != null
                    && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(p.LegacyPassword), System.Text.Encoding.UTF8.GetBytes(password)))
                    return p;
            }
            return null;
        }

        /// <summary>
        /// Full login check (lookup + PBKDF2). When the lookup key was regenerated, verifies hashes
        /// directly and repairs the profile's lookup.
        /// </summary>
        private async Task<KMProfile?> VerifyLoginPasswordAsync(string password)
        {
            var candidate = FindByPassword(password);
            if (candidate != null)
            {
                if (candidate.PasswordHash == null) return candidate; // legacy plaintext matched exactly
                if (ProfileCredentials.VerifyPassword(password, candidate.PasswordHash)) return candidate;
            }
            if (credentials?.LookupsStale == true)
            {
                foreach (var p in profiles)
                {
                    if (!ProfileCredentials.VerifyPassword(password, p.PasswordHash)) continue;
                    string lookup = credentials.ComputeLookup(password);
                    await MutateProfileAsync(p.UserID, clone => clone.PasswordLookup = lookup, accessChanged: false);
                    return GetProfileByIDFast(p.UserID);
                }
            }
            return null;
        }

        /// <summary>True when no other profile already uses this password (passwords identify profiles).</summary>
        private bool IsPasswordAvailable(string password, string? exceptProfileId)
        {
            var existing = FindByPassword(password);
            return existing == null || existing.UserID == exceptProfileId;
        }

        private void ApplyPassword(KMProfile clone, string password)
        {
            clone.PasswordHash = ProfileCredentials.HashPassword(password);
            clone.PasswordLookup = credentials?.ComputeLookup(password);
            clone.LegacyPassword = null;
            clone.PasswordChangedUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// An owner credential for Omnipotent's own loopback API calls (KliveAgent's omni_api tool),
        /// so they no longer need the owner's password. In-memory only, valid for this process run.
        /// </summary>
        public string? IssueInternalToken()
        {
            if (internalOwnerToken != null) return internalOwnerToken;
            lock (internalTokenGate)
            {
                if (internalOwnerToken != null) return internalOwnerToken;
                var owner = GetOwner();
                if (owner == null || Sessions == null) return null;
                var (token, _) = Sessions.Issue(owner.UserID, "127.0.0.1", "Omnipotent internal", SessionKind.Internal);
                internalOwnerToken = token;
                return token;
            }
        }

        // ───────────────────────────── mutation ─────────────────────────────

        /// <summary>
        /// Clones the profile, applies <paramref name="mutate"/>, persists it and swaps it in. When
        /// <paramref name="accessChanged"/> is true the access version moves, cached responses that
        /// depended on the profile's access are invalidated and its open tabs are told to refresh.
        /// </summary>
        public async Task<KMProfile?> MutateProfileAsync(string userId, Action<KMProfile> mutate, bool accessChanged = true)
        {
            await mutationLock.WaitAsync();
            KMProfile updated;
            try
            {
                var current = GetProfileByIDFast(userId);
                if (current == null) return null;
                updated = current.Clone();
                mutate(updated);
                updated.InvalidateAccessSnapshot();
                if (accessChanged) updated.AccessVersion++;
                await SaveProfileAsync(updated);
                profiles = profiles.Select(p => p.UserID == userId ? updated : p).ToList();
                RebuildIndexes();
            }
            finally
            {
                mutationLock.Release();
            }
            CacheDeps.Bump("kmprofiles");
            if (accessChanged)
            {
                CacheDeps.Bump(AccessDependencyKey(userId));
                PushToProfile(userId, new { type = "access-changed", version = updated.AccessVersion });
            }
            return updated;
        }

        /// <summary>The cache dependency every in-handler access check notes (see UserRequest.Can).</summary>
        public static string AccessDependencyKey(string userId) => $"kmprofile:{userId}:access";

        public async Task SaveProfileAsync(KMProfile profile)
        {
            string json = JsonConvert.SerializeObject(profile, Formatting.Indented);
            await GetDataHandler().WriteToFile(profile.CreateProfilePath(), json);
        }

        private async Task<KMProfile> CreateProfileCoreAsync(string name, ProfileRank rank, string password, bool isOwner,
            string? createdById, string? discordId, IEnumerable<PermissionGrant>? grants, bool allowPasswordApiAccess)
        {
            await mutationLock.WaitAsync();
            KMProfile profile;
            try
            {
                string id;
                do { id = RandomGeneration.GenerateRandomLengthOfNumbers(8); } while (GetProfileByIDFast(id) != null);
                profile = new KMProfile
                {
                    UserID = id,
                    Name = name,
                    CreationDate = DateTime.Now,
                    Rank = isOwner ? ProfileRank.Klives : rank,
                    IsOwner = isOwner,
                    CanLogin = true,
                    DiscordID = discordId,
                    CreatedById = createdById,
                    AllowPasswordApiAccess = isOwner || allowPasswordApiAccess,
                    Grants = grants?.ToList() ?? new List<PermissionGrant>(),
                    SchemaVersion = CurrentSchemaVersion,
                    AccessVersion = 1,
                };
                ApplyPassword(profile, password);
                await SaveProfileAsync(profile);
                profiles = profiles.Append(profile).ToList();
                RebuildIndexes();
            }
            finally
            {
                mutationLock.Release();
            }
            CacheDeps.Bump("kmprofiles");
            ServiceLog($"Created KM profile '{profile.Name}' ({profile.Rank}, {(profile.IsOwner ? "owner" : profile.Grants.Count + " permissions")}).");
            return profile;
        }

        private async Task<bool> DeleteProfileCoreAsync(string userId)
        {
            await mutationLock.WaitAsync();
            KMProfile? profile;
            try
            {
                profile = GetProfileByIDFast(userId);
                if (profile == null) return false;
                profiles = profiles.Where(p => p.UserID != userId).ToList();
                RebuildIndexes();
                await GetDataHandler().DeleteFile(profile.CreateProfilePath());
            }
            finally
            {
                mutationLock.Release();
            }
            CacheDeps.Bump("kmprofiles");
            CacheDeps.Bump(AccessDependencyKey(userId));
            PushToProfile(userId, new { type = "session-state", state = "ProfileNotFound" });
            Sessions.RevokeAll(userId, null, "Profile deleted");
            return true;
        }

        public async Task RequestProfileFromKlives()
        {
            try
            {
                var password = (string?)await ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>("SendTextPromptToKlivesDiscord",
                    "No profiles detected in Klives Management", "As I am making your profile, please provide me with a password.",
                    TimeSpan.FromDays(3), "Password here! Turn off screenshare!", "Password");
                if (string.IsNullOrWhiteSpace(password)) return;
                await CreateProfileCoreAsync("Klives", ProfileRank.Klives, password, isOwner: true, createdById: null,
                    discordId: null, grants: null, allowPasswordApiAccess: true);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Could not create the owner profile from Discord.");
            }
        }

        // ───────────────────────────── migration ─────────────────────────────

        /// <summary>
        /// Schema 1 → 2: ranks become permissions (exactly the keys the rank could use, via each key's
        /// LegacyRank), Klives becomes the owner, and plaintext passwords become a lookup + hash.
        /// Each file is backed up as <c>*.kmp.v1.bak</c> first.
        /// </summary>
        private async Task MigrateProfilesAsync()
        {
            var notes = new List<string>();
            var all = PermissionCatalog.All;
            foreach (var original in profiles.ToList())
            {
                if (original.SchemaVersion < CurrentSchemaVersion) BackupProfileFile(original);
                var (p, changed, note, legacy) = MigrateProfile(original, all, credentials, DateTime.UtcNow);
                if (note != null) notes.Add(note);
                if (legacy != null) migratedThisRun[p.UserID] = legacy.Value;
                if (!changed) continue;
                try
                {
                    await SaveProfileAsync(p);
                    profiles = profiles.Select(x => x.UserID == p.UserID ? p : x).ToList();
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, $"Could not migrate profile {p.Name}; it keeps its old format for now.");
                }
            }

            if (notes.Count > 0)
            {
                string summary = "Profiles converted from ranks to permissions:\n" + string.Join("\n", notes.Select(n => "• " + n));
                ServiceLog(summary);
                _ = SafeDiscord(summary);
            }
        }

        /// <summary>
        /// The pure part of migration (tested directly): ranks become exactly the keys whose
        /// LegacyRank the rank reached, Klives becomes the owner, plaintext passwords become a
        /// lookup + hash. Returns the migrated copy and, for non-owners, the rank they had.
        /// </summary>
        internal static (KMProfile Profile, bool Changed, string? Note, ProfileRank? LegacyRank) MigrateProfile(
            KMProfile original, IReadOnlyList<PermissionDef> catalog, ProfileCredentials? credentials, DateTime now)
        {
            var p = original.Clone();
            bool changed = false;
            string? note = null;
            ProfileRank? legacyRank = null;

            if (p.SchemaVersion < CurrentSchemaVersion)
            {
                ProfileRank legacy = p.Rank;
                if (legacy >= ProfileRank.Klives)
                {
                    p.IsOwner = true;
                    p.Rank = ProfileRank.Klives;
                    p.AllowPasswordApiAccess = true;
                    p.Grants = new List<PermissionGrant>();
                }
                else
                {
                    if (p.Rank < ProfileRank.Guest) p.Rank = ProfileRank.Guest;
                    p.Grants = catalog.Where(d => d.IsStandard && d.LegacyRank != ProfileRank.Klives && d.LegacyRank <= legacy)
                        .Select(d => new PermissionGrant
                        {
                            Key = d.Key,
                            GrantedUtc = now,
                            GrantedById = "migration",
                            Note = $"Converted from rank {legacy}",
                        }).ToList();
                    legacyRank = legacy;
                }
                p.SchemaVersion = CurrentSchemaVersion;
                p.AccessVersion++;
                note = p.IsOwner ? $"{p.Name}: owner" : $"{p.Name}: {p.Grants.Count} permissions (was {legacy})";
                changed = true;
            }

            if (p.LegacyPassword != null && credentials != null)
            {
                if (p.PasswordHash == null) p.PasswordHash = ProfileCredentials.HashPassword(p.LegacyPassword);
                p.PasswordLookup = credentials.ComputeLookup(p.LegacyPassword);
                p.PasswordChangedUtc ??= now;
                p.LegacyPassword = null;
                changed = true;
            }
            p.InvalidateAccessSnapshot();
            return (p, changed, note, legacyRank);
        }

        /// <summary>
        /// Test seam: loads profiles and wires credentials and sessions under
        /// <paramref name="dataDirectory"/> without starting the service.
        /// </summary>
        internal void InitializeForTests(IEnumerable<KMProfile> initialProfiles, string dataDirectory)
        {
            PermissionCatalog.EnsureLoaded();
            Directory.CreateDirectory(dataDirectory);
            profiles = initialProfiles.ToList();
            credentials = new ProfileCredentials(Path.Combine(dataDirectory, "credential-lookup.key"),
                () => profiles.Any(p => !string.IsNullOrEmpty(p.PasswordLookup)), _ => { });
            Sessions = new SessionStore(Path.Combine(dataDirectory, "sessions.json"), _ => { });
            Sessions.Load();
            Sessions.Revoked += OnSessionRevoked;
            profiles = profiles.Select(p => MigrateProfile(p, PermissionCatalog.All, credentials, DateTime.UtcNow).Profile).ToList();
            RebuildIndexes();
            loaded = true;
        }

        /// <summary>Test seam: the in-process credential helper.</summary>
        internal ProfileCredentials? Credentials => credentials;

        private static void BackupProfileFile(KMProfile profile)
        {
            try
            {
                string path = profile.CreateProfilePath();
                string backup = path + ".v1.bak";
                if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);
            }
            catch { /* the migration itself is still safe; the backup is a courtesy */ }
        }

        /// <summary>
        /// A key registered at runtime (one per KliveTools tool) is granted to profiles migrated during
        /// this run whose old rank could use it — so a tool that loads after migration isn't lost.
        /// Keys registered in later runs are new features and start owner-only.
        /// </summary>
        private void OnPermissionRegistered(PermissionDef def)
        {
            if (!def.IsStandard || def.LegacyRank == ProfileRank.Klives || migratedThisRun.IsEmpty) return;
            foreach (var (userId, legacy) in migratedThisRun)
            {
                if (legacy < def.LegacyRank) continue;
                _ = MutateProfileAsync(userId, p =>
                {
                    if (p.Grants.Any(g => g.Key == def.Key)) return;
                    p.Grants.Add(new PermissionGrant { Key = def.Key, GrantedUtc = DateTime.UtcNow, GrantedById = "migration", Note = $"Converted from rank {legacy}" });
                });
            }
        }

        // ───────────────────────────── upkeep ─────────────────────────────

        private async Task MaintenanceLoop()
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    acceptPasswordAsBearer = await GetBoolOmniSetting("KMProfiles_AcceptPasswordAsBearer", defaultValue: true);
                }
                catch { /* settings not ready yet: keep the last value */ }
                try { Sessions?.FlushIfDirty(); } catch { }
                try { ExpireSuspensions(); } catch { }
                try { await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken.Token); }
                catch (OperationCanceledException) { break; }
            }
            try { Sessions?.FlushIfDirty(); } catch { }
        }

        /// <summary>Lifts suspensions whose time has passed so the profile's tabs refresh.</summary>
        private void ExpireSuspensions()
        {
            DateTime now = DateTime.UtcNow;
            foreach (var p in profiles.Where(p => p.SuspendedUntilUtc != null && p.SuspendedUntilUtc <= now).ToList())
            {
                _ = MutateProfileAsync(p.UserID, clone =>
                {
                    clone.SuspendedUntilUtc = null;
                    clone.SuspensionReason = null;
                    clone.SuspendedById = null;
                });
                RecordEvent(p.UserID, "access.unsuspended", null, new { automatic = true });
            }
        }

        private void OnSessionRevoked(KMSession session)
        {
            Presence.SendToSession(session.SessionId, JsonConvert.SerializeObject(new
            {
                type = "session-state",
                state = "SessionRevoked",
                reason = session.RevokeReason,
            }));
        }

        private void OnPageLeft(PresenceConnection conn, string path, DateTime sinceUtc, long dwellMs)
        {
            Activity?.EnqueuePageView(new ActivityPageView
            {
                ProfileId = conn.ProfileId,
                SessionId = conn.SessionId,
                TsMs = new DateTimeOffset(sinceUtc).ToUnixTimeMilliseconds(),
                Path = path,
                Title = conn.Title,
                DwellMs = dwellMs,
            });
        }

        // ───────────────────────────── activity & notifications ─────────────────────────────

        /// <summary>Called by KliveAPI for every request a profile makes (batch items individually).</summary>
        public void RecordRequest(KMProfile profile, KMSession? session, string method, string route, PermissionDef? permission,
            int status, double durationMs, string? ip, string? page, string? denyReason, bool viaBatch)
        {
            var store = Activity;
            if (store == null || profile == null) return;
            if (session?.Kind == SessionKind.Internal) return;
            if (route.Equals("/KliveAPI/telemetry/rum", StringComparison.OrdinalIgnoreCase)) return;
            store.EnqueueRequest(new ActivityRequest
            {
                ProfileId = profile.UserID,
                TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SessionId = session?.SessionId,
                Method = method,
                Route = route,
                PermKey = permission?.IsStandard == true ? permission.Key : permission?.Key,
                Service = permission?.IsStandard == true ? permission.Service : "Account",
                Status = status,
                DurationMs = durationMs,
                Ip = ip,
                Page = page,
                DenyReason = denyReason,
                ViaBatch = viaBatch,
            });
        }

        public void RecordEvent(string profileId, string kind, KMProfile? actor, object? detail = null, string? ip = null)
        {
            Activity?.EnqueueEvent(new ActivityEvent
            {
                ProfileId = profileId,
                TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Kind = kind,
                ActorId = actor?.UserID,
                ActorName = actor?.Name,
                Ip = ip,
                DetailJson = detail == null ? null : JsonConvert.SerializeObject(detail),
            });
        }

        public int PushToProfile(string profileId, object message)
            => Presence.SendToProfile(profileId, JsonConvert.SerializeObject(message));

        /// <summary>Audit into OmniDefence. Fire-and-forget: OmniDefence's write lock must never delay a request.</summary>
        private void Audit(KMProfile? actor, string category, string action, object? detail = null, string? ip = null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteServiceMethod<Omnipotent.Services.OmniDefence.OmniDefence>(
                        "RecordProfileAction", actor, category, action, detail, ip);
                }
                catch { }
            });
        }

        private void AuthEvent(string type, string? ip, KMProfile? profile, string? userAgent, string? detail)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteServiceMethod<Omnipotent.Services.OmniDefence.OmniDefence>("RecordAuthEventAsync",
                        new Omnipotent.Services.OmniDefence.AuthEventRow
                        {
                            UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            Ip = ip,
                            Type = type,
                            ProfileId = profile?.UserID,
                            ProfileName = profile?.Name,
                            Route = "/KMProfiles/Login",
                            UserAgent = userAgent,
                            Detail = detail,
                        });
                }
                catch { }
            });
        }

        /// <summary>Discord message to Klives that can never delay the caller.</summary>
        private Task SafeDiscord(string message)
        {
            return Task.Run(async () =>
            {
                try { await ExecuteServiceMethod<KliveBotDiscord>("SendMessageToKlives", message); }
                catch { }
            });
        }

        /// <summary>
        /// Per-IP login throttle: the first few failures in ten minutes fail fast, after that each
        /// attempt must wait an escalating delay (capped at five minutes).
        /// </summary>
        internal sealed class LoginThrottle
        {
            private const int FreeFailures = 3;
            private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
            private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);
            private readonly ConcurrentDictionary<string, (int Failures, DateTime WindowStart, DateTime BlockedUntil)> state = new();

            public TimeSpan? RetryAfter(string ip, DateTime nowUtc)
            {
                if (!state.TryGetValue(ip, out var s)) return null;
                if (nowUtc - s.WindowStart > Window) { state.TryRemove(ip, out _); return null; }
                return s.BlockedUntil > nowUtc ? s.BlockedUntil - nowUtc : null;
            }

            public void Failed(string ip, DateTime nowUtc)
            {
                state.AddOrUpdate(ip,
                    _ => (1, nowUtc, nowUtc),
                    (_, s) =>
                    {
                        if (nowUtc - s.WindowStart > Window) return (1, nowUtc, nowUtc);
                        int failures = s.Failures + 1;
                        DateTime blocked = nowUtc;
                        if (failures > FreeFailures)
                        {
                            double seconds = Math.Min(MaxDelay.TotalSeconds, Math.Pow(2, failures - FreeFailures));
                            blocked = nowUtc + TimeSpan.FromSeconds(seconds);
                        }
                        return (failures, s.WindowStart, blocked);
                    });
            }

            public void Succeeded(string ip) => state.TryRemove(ip, out _);
        }
    }
}
