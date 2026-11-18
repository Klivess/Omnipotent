using Omnipotent.Profiles.Activity;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.KliveAPI.Telemetry;

namespace Omnipotent.Profiles
{
    /// <summary>
    /// The directory's review: what each profile's access looks like in use. Built from the
    /// activity store's rollups and the session store, kept for 30 s and shared by every viewer
    /// (each sees only the parts their permissions allow).
    ///
    /// Why it exists (live, 13 Nov 2026): password-as-bearer was still accepted five weeks after
    /// sessions shipped, with nothing saying whether switching it off would lock anyone out; 15 of
    /// 17 signed-in devices had been unused for 3–24 days; four dormant profiles held 33–58
    /// permissions each; a bot profile used 64 of the 179 it held. None of it was on a page.
    /// </summary>
    public partial class KMProfileManager
    {
        private static readonly TimeSpan InsightsFresh = TimeSpan.FromSeconds(30);
        /// <summary>A signed-in device unused this long counts as idle.</summary>
        public static readonly TimeSpan IdleSessionAge = TimeSpan.FromDays(7);
        private const int UsageWindowDays = 30;
        private const int AuthWindowDays = 7;

        private readonly object insightsGate = new();
        private ProfileInsights? insights;
        private Task<ProfileInsights>? insightsBuild;

        internal sealed class ProfileInsight
        {
            public string ProfileId = "";
            public long[] Hourly24 = Array.Empty<long>();
            public long SessionRequests7d;
            public long PasswordRequests7d;
            public long? LastPasswordMs;
            public long? LastSessionMs;
            public Dictionary<string, long> KeysUsed30d = new(StringComparer.Ordinal);
            public long LoginRefused30d;
            public int SessionsActive;
            public int SessionsIdle;
            public DateTime? OldestIdleSeenUtc;
        }

        internal sealed class ProfileInsights
        {
            public Dictionary<string, ProfileInsight> ByProfile = new(StringComparer.Ordinal);
            public long? TrackingSinceMs;
            public bool RollupsReady;
            public long RollupsSinceMs;
            public long RollupThrough;
            public long RollupUpto;
            public DateTime BuiltUtc;
            public long BuildMs;
        }

        /// <summary>Builds the review from the stores (no caching; see <see cref="GetInsightsAsync"/>).</summary>
        internal ProfileInsights BuildInsights(DateTime nowUtc)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var store = Activity;
            long nowMs = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds();
            long authFrom = new DateTimeOffset(nowUtc.AddDays(-AuthWindowDays)).ToUnixTimeMilliseconds();
            long usageFrom = new DateTimeOffset(nowUtc.AddDays(-UsageWindowDays)).ToUnixTimeMilliseconds();
            var result = new ProfileInsights { BuiltUtc = nowUtc };

            Dictionary<string, Dictionary<string, long>> events = new(StringComparer.Ordinal);
            if (store != null)
            {
                using (RequestTrace.AmbientSpan("insights-events"))
                    events = store.GetEventCountsSince(usageFrom, new[] { "login.refused" });
                result.TrackingSinceMs = store.GetTrackingSinceMs();
                result.RollupsReady = store.RollupsReady;
                result.RollupsSinceMs = store.RollupsSinceMs;
                (result.RollupThrough, result.RollupUpto) = store.RollupProgress;
            }

            DateTime idleSince = nowUtc - IdleSessionAge;
            foreach (var p in profiles)
            {
                var insight = new ProfileInsight { ProfileId = p.UserID };
                if (store != null)
                {
                    insight.Hourly24 = store.GetHourly(p.UserID, 24, nowMs);
                    using (RequestTrace.AmbientSpan("insights-auth"))
                    {
                        var mix = store.GetAuthMix(p.UserID, authFrom);
                        insight.SessionRequests7d = mix.Session;
                        insight.PasswordRequests7d = mix.Password;
                        insight.LastPasswordMs = mix.LastPasswordMs;
                        insight.LastSessionMs = mix.LastSessionMs;
                    }
                    using (RequestTrace.AmbientSpan("insights-usage"))
                        insight.KeysUsed30d = store.GetKeysUsedSince(p.UserID, usageFrom);
                    if (events.TryGetValue(p.UserID, out var byKind)) insight.LoginRefused30d = byKind.GetValueOrDefault("login.refused");
                }
                if (Sessions != null)
                {
                    var (active, idle, oldest) = Sessions.IdleSummary(p.UserID, idleSince);
                    insight.SessionsActive = active;
                    insight.SessionsIdle = idle;
                    insight.OldestIdleSeenUtc = oldest;
                }
                result.ByProfile[p.UserID] = insight;
            }
            result.BuildMs = clock.ElapsedMilliseconds;
            return result;
        }

        /// <summary>The review, at most 30 s old; one build at a time, off the request thread.</summary>
        internal Task<ProfileInsights> GetInsightsAsync()
        {
            lock (insightsGate)
            {
                if (insights != null && DateTime.UtcNow - insights.BuiltUtc < InsightsFresh) return Task.FromResult(insights);
                insightsBuild ??= Task.Run(() =>
                {
                    try
                    {
                        var built = BuildInsights(DateTime.UtcNow);
                        lock (insightsGate) insights = built;
                        return built;
                    }
                    finally
                    {
                        lock (insightsGate) insightsBuild = null;
                    }
                });
                return insightsBuild;
            }
        }

        /// <summary>Test seam: an activity store and the password-as-bearer switch without starting the service.</summary>
        internal void UseActivityForTests(ProfileActivityStore store, bool acceptPasswordAsBearer)
        {
            Activity = store;
            this.acceptPasswordAsBearer = acceptPasswordAsBearer;
            InvalidateInsights();
        }

        /// <summary>Forgets the cached review (after a change the review should show at once).</summary>
        private void InvalidateInsights()
        {
            lock (insightsGate) insights = null;
        }

        /// <summary>
        /// The review as one viewer may see it. Everyone with the directory sees activity shape;
        /// credentials and refusals need activity access, sessions need access control, permission
        /// usage needs both activity access and permission viewing.
        /// </summary>
        internal object InsightsView(ProfileInsights data, KMProfile viewer, DateTime nowUtc)
        {
            bool canActivity = AccessEvaluator.Can(viewer, ProfilesPerms.ActivityRead);
            bool canControl = AccessEvaluator.Can(viewer, ProfilesPerms.AccessControl);
            bool canPermissions = AccessEvaluator.Can(viewer, ProfilesPerms.PermissionsView);
            static DateTime? Utc(long? ms) => ms is long v ? DateTimeOffset.FromUnixTimeMilliseconds(v).UtcDateTime : null;

            var rows = new List<object>();
            var blockers = new List<object>();
            foreach (var p in profiles)
            {
                if (!data.ByProfile.TryGetValue(p.UserID, out var i)) continue;
                var row = new Dictionary<string, object?>
                {
                    ["userId"] = p.UserID,
                    ["hourly24"] = i.Hourly24,
                };
                if (canActivity)
                {
                    row["auth"] = new
                    {
                        session7d = i.SessionRequests7d,
                        password7d = i.PasswordRequests7d,
                        lastPasswordUtc = Utc(i.LastPasswordMs),
                        lastSessionUtc = Utc(i.LastSessionMs),
                        passwordApiAllowed = p.AllowPasswordApiAccess,
                    };
                    row["loginRefused30d"] = i.LoginRefused30d;
                    if (acceptPasswordAsBearer && i.PasswordRequests7d > 0 && !p.AllowPasswordApiAccess)
                        blockers.Add(new { userId = p.UserID, name = p.Name, password7d = i.PasswordRequests7d, lastPasswordUtc = Utc(i.LastPasswordMs) });
                }
                if (canActivity && canPermissions)
                {
                    var held = p.EffectiveKeys(nowUtc);
                    var directKeys = p.Grants.Where(g => g.IsActive(nowUtc)).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
                    row["usage"] = new
                    {
                        windowDays = UsageWindowDays,
                        held = held.Count,
                        used = held.Count(k => i.KeysUsed30d.ContainsKey(k)),
                        // Direct grants only: an implied key goes when the key that implies it goes.
                        unused = p.IsOwner ? new List<string>() : directKeys.Where(k => !i.KeysUsed30d.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList(),
                    };
                }
                if (canControl)
                {
                    row["sessions"] = new
                    {
                        active = i.SessionsActive,
                        idle = i.SessionsIdle,
                        idleDays = (int)IdleSessionAge.TotalDays,
                        oldestIdleSeenUtc = i.OldestIdleSeenUtc,
                    };
                }
                if (canPermissions)
                    row["effective"] = p.IsOwner ? null : p.EffectiveKeys(nowUtc).OrderBy(k => k, StringComparer.Ordinal).ToList();
                rows.Add(row);
            }

            long coveredFromMs = new DateTimeOffset(nowUtc.AddDays(-AuthWindowDays)).ToUnixTimeMilliseconds();
            if (!data.RollupsReady) coveredFromMs = Math.Max(coveredFromMs, data.RollupsSinceMs);
            return new
            {
                builtUtc = data.BuiltUtc,
                buildMs = data.BuildMs,
                trackingSinceUtc = Utc(data.TrackingSinceMs),
                rollups = new { ready = data.RollupsReady, through = data.RollupThrough, upto = data.RollupUpto },
                passwordBearer = canActivity ? new
                {
                    accepted = acceptPasswordAsBearer,
                    windowDays = AuthWindowDays,
                    coveredSinceUtc = Utc(coveredFromMs),
                    // Who would be refused if KMProfiles_AcceptPasswordAsBearer were switched off.
                    blockers,
                    safeToSwitchOff = acceptPasswordAsBearer && blockers.Count == 0,
                } : null,
                profiles = rows,
            };
        }

        private async Task CreateInsightRoutes()
        {
            await Route("/KMProfiles/insights", ProfilesPerms.DirectoryView, HttpMethod.Get, async req =>
            {
                var viewer = Actor(req);
                ProfileInsights data;
                using (req.Trace?.Span("insights")) data = await GetInsightsAsync();
                await Ok(req, InsightsView(data, viewer, DateTime.UtcNow));
            });
        }
    }
}
