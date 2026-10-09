using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Omnipotent.Profiles.Credentials;
using Omnipotent.Profiles.Permissions;

namespace Omnipotent.Profiles.Sessions
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum SessionKind
    {
        /// <summary>A website sign-in on one device.</summary>
        Browser = 0,
        /// <summary>An in-process credential for Omnipotent's own API calls. Never persisted.</summary>
        Internal = 1,
    }

    /// <summary>
    /// One signed-in device. The token itself is never stored, only its SHA-256, so the sessions
    /// file cannot be replayed if it leaks.
    /// </summary>
    public sealed class KMSession
    {
        public string SessionId { get; set; } = "";
        public string ProfileId { get; set; } = "";
        public string TokenHash { get; set; } = "";
        public SessionKind Kind { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public string? Ip { get; set; }
        public string? UserAgent { get; set; }
        /// <summary>"Chrome on Windows" — derived from the user agent when the session starts.</summary>
        public string? Label { get; set; }
        public DateTime? RevokedUtc { get; set; }
        public string? RevokedById { get; set; }
        public string? RevokeReason { get; set; }

        public bool IsRevoked => RevokedUtc != null;
        public bool IsActive(DateTime nowUtc) => RevokedUtc == null && ExpiresUtc > nowUtc;
    }

    public readonly record struct SessionValidation(KMSession? Session, AccessDenyReason Failure);

    /// <summary>
    /// Every signed-in device, with a sliding 30-day lifetime. Validation is an in-memory hash
    /// lookup; writes are persisted atomically (temp file, flush, rename). "Last seen" updates are
    /// batched so a busy session does not rewrite the file on every request.
    /// </summary>
    public sealed class SessionStore
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
        private static readonly TimeSpan TouchGranularity = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan KeepEndedSessionsFor = TimeSpan.FromDays(30);

        private readonly string path;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, KMSession> byTokenHash = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, KMSession> byId = new(StringComparer.Ordinal);
        private readonly object saveGate = new();
        private int dirty;

        /// <summary>Raised after a session is revoked (by anyone, for any reason).</summary>
        public event Action<KMSession>? Revoked;

        public SessionStore(string path, Action<string> log)
        {
            this.path = path;
            this.log = log;
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(path)) return;
                var sessions = JsonConvert.DeserializeObject<List<KMSession>>(File.ReadAllText(path)) ?? new List<KMSession>();
                DateTime now = DateTime.UtcNow;
                foreach (var s in sessions)
                {
                    if (string.IsNullOrWhiteSpace(s.SessionId) || string.IsNullOrWhiteSpace(s.TokenHash)) continue;
                    if (!s.IsActive(now) && (s.RevokedUtc ?? s.ExpiresUtc) < now - KeepEndedSessionsFor) continue;
                    byId[s.SessionId] = s;
                    byTokenHash[s.TokenHash] = s;
                }
            }
            catch (Exception ex)
            {
                // A corrupt sessions file only signs everyone out; it must never block startup.
                log("KMProfiles: could not read sessions (everyone will need to sign in again): " + ex.Message);
                try { File.Move(path, path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false); } catch { }
            }
        }

        public (string Token, KMSession Session) Issue(string profileId, string? ip, string? userAgent,
            SessionKind kind = SessionKind.Browser, DateTime? nowUtc = null)
        {
            DateTime now = nowUtc ?? DateTime.UtcNow;
            string token = ProfileCredentials.NewSessionToken();
            var session = new KMSession
            {
                SessionId = ProfileCredentials.NewSessionId(),
                ProfileId = profileId,
                TokenHash = ProfileCredentials.HashToken(token),
                Kind = kind,
                CreatedUtc = now,
                LastSeenUtc = now,
                ExpiresUtc = kind == SessionKind.Internal ? DateTime.MaxValue : now + Lifetime,
                Ip = ip,
                UserAgent = userAgent,
                Label = DescribeUserAgent(userAgent),
            };
            byId[session.SessionId] = session;
            byTokenHash[session.TokenHash] = session;
            if (kind != SessionKind.Internal) SaveNow();
            return (token, session);
        }

        public SessionValidation Validate(string token, DateTime nowUtc)
        {
            if (!byTokenHash.TryGetValue(ProfileCredentials.HashToken(token), out var session))
                return new SessionValidation(null, AccessDenyReason.InvalidCredential);
            if (session.RevokedUtc != null) return new SessionValidation(null, AccessDenyReason.SessionRevoked);
            if (session.ExpiresUtc <= nowUtc) return new SessionValidation(null, AccessDenyReason.SessionExpired);
            return new SessionValidation(session, AccessDenyReason.None);
        }

        /// <summary>Slides the expiry and records where the session was last seen.</summary>
        public void Touch(KMSession session, DateTime nowUtc, string? ip)
        {
            if (session.Kind == SessionKind.Internal) return;
            bool moved = !string.IsNullOrEmpty(ip) && !string.Equals(ip, session.Ip, StringComparison.Ordinal);
            if (!moved && nowUtc - session.LastSeenUtc < TouchGranularity) return;
            session.LastSeenUtc = nowUtc;
            session.ExpiresUtc = nowUtc + Lifetime;
            if (moved) session.Ip = ip;
            Interlocked.Exchange(ref dirty, 1);
        }

        public KMSession? Get(string sessionId) => byId.TryGetValue(sessionId ?? "", out var s) ? s : null;

        public IReadOnlyList<KMSession> ForProfile(string profileId, bool includeEnded = false)
        {
            DateTime now = DateTime.UtcNow;
            return byId.Values
                .Where(s => s.ProfileId == profileId && s.Kind == SessionKind.Browser && (includeEnded || s.IsActive(now)))
                .OrderByDescending(s => s.LastSeenUtc)
                .ToList();
        }

        public int ActiveCount(string profileId)
        {
            DateTime now = DateTime.UtcNow;
            return byId.Values.Count(s => s.ProfileId == profileId && s.Kind == SessionKind.Browser && s.IsActive(now));
        }

        public bool Revoke(string sessionId, string? revokedById, string reason)
        {
            var session = Get(sessionId);
            if (session == null || session.RevokedUtc != null) return false;
            session.RevokedUtc = DateTime.UtcNow;
            session.RevokedById = revokedById;
            session.RevokeReason = reason;
            SaveNow();
            try { Revoked?.Invoke(session); } catch { }
            return true;
        }

        /// <summary>Revokes every live session of a profile, optionally keeping one (the caller's own).</summary>
        public int RevokeAll(string profileId, string? revokedById, string reason, string? exceptSessionId = null)
        {
            DateTime now = DateTime.UtcNow;
            var targets = byId.Values
                .Where(s => s.ProfileId == profileId && s.RevokedUtc == null && s.SessionId != exceptSessionId)
                .ToList();
            foreach (var s in targets)
            {
                s.RevokedUtc = now;
                s.RevokedById = revokedById;
                s.RevokeReason = reason;
            }
            if (targets.Count > 0) SaveNow();
            foreach (var s in targets)
            {
                try { Revoked?.Invoke(s); } catch { }
            }
            return targets.Count;
        }

        /// <summary>Writes pending "last seen" updates. Called periodically by the profile manager.</summary>
        public void FlushIfDirty()
        {
            if (Interlocked.Exchange(ref dirty, 0) == 1) SaveNow();
        }

        public void SaveNow()
        {
            lock (saveGate)
            {
                try
                {
                    DateTime now = DateTime.UtcNow;
                    var keep = byId.Values
                        .Where(s => s.Kind != SessionKind.Internal)
                        .Where(s => s.IsActive(now) || (s.RevokedUtc ?? s.ExpiresUtc) >= now - KeepEndedSessionsFor)
                        .OrderBy(s => s.CreatedUtc)
                        .ToList();
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    using (var writer = new StreamWriter(fs))
                    {
                        writer.Write(JsonConvert.SerializeObject(keep, Formatting.Indented));
                        writer.Flush();
                        fs.Flush(flushToDisk: true);
                    }
                    File.Move(temp, path, overwrite: true);
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref dirty, 1);
                    log("KMProfiles: could not save sessions: " + ex.Message);
                }
            }
        }

        /// <summary>A short human label for a user agent: "Chrome on Windows", "Safari on iPhone".</summary>
        public static string? DescribeUserAgent(string? ua)
        {
            if (string.IsNullOrWhiteSpace(ua)) return null;
            string browser =
                ua.Contains("Edg/", StringComparison.Ordinal) ? "Edge" :
                ua.Contains("OPR/", StringComparison.Ordinal) ? "Opera" :
                ua.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox" :
                ua.Contains("Chrome/", StringComparison.Ordinal) ? "Chrome" :
                ua.Contains("Safari/", StringComparison.Ordinal) ? "Safari" :
                ua.Contains("curl/", StringComparison.OrdinalIgnoreCase) ? "curl" :
                ua.Contains("python", StringComparison.OrdinalIgnoreCase) ? "Python" :
                "Browser";
            string os =
                ua.Contains("iPhone", StringComparison.Ordinal) ? "iPhone" :
                ua.Contains("iPad", StringComparison.Ordinal) ? "iPad" :
                ua.Contains("Android", StringComparison.Ordinal) ? "Android" :
                ua.Contains("Windows", StringComparison.Ordinal) ? "Windows" :
                ua.Contains("Mac OS X", StringComparison.Ordinal) ? "macOS" :
                ua.Contains("Linux", StringComparison.Ordinal) ? "Linux" :
                "";
            return os.Length == 0 ? browser : $"{browser} on {os}";
        }
    }
}
