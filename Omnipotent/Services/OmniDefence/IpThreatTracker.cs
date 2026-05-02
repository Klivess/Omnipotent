using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// In-memory cache of IP records keyed by IP. Hot-path lookups for the
    /// request pipeline; persists writes back to <see cref="OmniDefenceStore"/>.
    /// </summary>
    public class IpThreatTracker
    {
        public enum IpStatus
        {
            Normal,
            Watch,
            Blocked,
            Tarpit,
            Honeypot
        }

        public enum GateDecision
        {
            Allow,
            Block,
            Tarpit,
            Honeypot
        }

        private readonly OmniDefenceStore store;
        private readonly ConcurrentDictionary<string, IpRecord> cache = new(StringComparer.OrdinalIgnoreCase);

        // Tunables (overridable from OmniDefence service via Set* methods).
        public int AutoWatchScore { get; set; } = 50;
        public int AutoBlockScore { get; set; } = 200;
        public int FirstAlertThreshold { get; set; } = 1;       // alert on first unauth
        public int Escalation2Threshold { get; set; } = 50;
        public int Escalation3Threshold { get; set; } = 200;

        public IpThreatTracker(OmniDefenceStore store)
        {
            this.store = store;
        }

        public async Task LoadAsync()
        {
            var all = await store.LoadAllIpRecordsAsync();
            foreach (var r in all)
            {
                cache[r.Ip] = r;
            }
        }

        public IpRecord GetOrCreate(string ip)
        {
            return cache.GetOrAdd(ip, key => new IpRecord
            {
                Ip = key,
                FirstSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Status = nameof(IpStatus.Normal)
            });
        }

        public IpRecord? Get(string ip) => cache.TryGetValue(ip, out var r) ? r : null;

        public IEnumerable<IpRecord> All() => cache.Values;

        /// <summary>
        /// Pre-dispatch decision based on current IP record status.
        /// </summary>
        public GateDecision Evaluate(string ip)
        {
            if (!cache.TryGetValue(ip, out var rec)) return GateDecision.Allow;
            return rec.Status switch
            {
                nameof(IpStatus.Blocked) => GateDecision.Block,
                nameof(IpStatus.Tarpit) => GateDecision.Tarpit,
                nameof(IpStatus.Honeypot) => GateDecision.Honeypot,
                _ => GateDecision.Allow
            };
        }

        /// <summary>
        /// Records the outcome of a request for threat scoring purposes.
        /// Returns the updated record (post-mutation).
        /// </summary>
        public IpRecord RecordOutcome(string ip, RequestOutcome outcome)
        {
            var rec = GetOrCreate(ip);
            lock (rec)
            {
                rec.LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                rec.TotalRequests++;
                switch (outcome)
                {
                    case RequestOutcome.Success:
                        rec.SuccessfulRequests++;
                        rec.ThreatScore = Math.Max(0, rec.ThreatScore - 0.1);
                        break;
                    case RequestOutcome.UnauthRoute:
                        rec.UnauthAttempts++;
                        rec.DenyCount++;
                        rec.ThreatScore += 5;
                        break;
                    case RequestOutcome.InsufficientClearance:
                        rec.UnauthAttempts++;
                        rec.DenyCount++;
                        rec.ThreatScore += 8;
                        break;
                    case RequestOutcome.InvalidPassword:
                        rec.UnauthAttempts++;
                        rec.DenyCount++;
                        rec.ThreatScore += 10;
                        break;
                    case RequestOutcome.WebsiteNoProfile:
                        rec.UnauthAttempts++;
                        rec.DenyCount++;
                        rec.ThreatScore += 15;
                        break;
                    case RequestOutcome.IncorrectMethod:
                        rec.DenyCount++;
                        rec.ThreatScore += 1;
                        break;
                    case RequestOutcome.NotFound:
                        rec.ThreatScore += 0.5;
                        break;
                    case RequestOutcome.ServerError:
                        // not the IP's fault
                        break;
                    case RequestOutcome.PreBlocked:
                        rec.DenyCount++;
                        rec.ThreatScore += 1;
                        break;
                }
                rec.ThreatScore = Math.Clamp(rec.ThreatScore, 0, 1000);

                // Auto-escalation of status (only escalate, never auto-downgrade away from manual states)
                if (string.IsNullOrWhiteSpace(rec.AssociatedProfileId) && rec.Status != nameof(IpStatus.Blocked) && rec.Status != nameof(IpStatus.Honeypot))
                {
                    if (rec.ThreatScore >= AutoBlockScore)
                    {
                        rec.Status = nameof(IpStatus.Blocked);
                        rec.LastBlockReason = $"Auto: threat score reached {rec.ThreatScore:F0}";
                    }
                    else if (rec.ThreatScore >= AutoWatchScore && rec.Status == nameof(IpStatus.Normal))
                    {
                        rec.Status = nameof(IpStatus.Watch);
                    }
                }
            }
            return rec;
        }

        public Task PersistAsync(IpRecord rec) => store.UpsertIpRecordAsync(rec);

        public async Task PersistAllDirtyAsync()
        {
            // Naive: persist all cached records. Could add dirty tracking later.
            foreach (var r in cache.Values)
            {
                await store.UpsertIpRecordAsync(r);
            }
        }

        public void SetStatus(string ip, IpStatus status, string? reason)
        {
            var rec = GetOrCreate(ip);
            lock (rec)
            {
                rec.Status = status.ToString();
                if (status == IpStatus.Blocked) rec.LastBlockReason = reason;
            }
        }

        public void SetNotes(string ip, string? notes)
        {
            var rec = GetOrCreate(ip);
            lock (rec) { rec.Notes = notes; }
        }

        public void LinkProfile(IpRecord rec, string? profileId, string? profileName, int? profileRank, long seenUtc)
        {
            if (rec == null || string.IsNullOrWhiteSpace(profileId)) return;

            lock (rec)
            {
                if (rec.AssociatedProfileLastSeenUtc.HasValue && rec.AssociatedProfileLastSeenUtc.Value > seenUtc) return;

                rec.AssociatedProfileId = profileId;
                rec.AssociatedProfileName = profileName;
                rec.AssociatedProfileRank = profileRank;
                rec.AssociatedProfileLastSeenUtc = seenUtc;
            }
        }

        public bool IsLinkedToKlives(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            var rec = Get(ip);
            return rec?.AssociatedProfileRank >= 5;
        }
    }

    public enum RequestOutcome
    {
        Success,
        UnauthRoute,
        InsufficientClearance,
        InvalidPassword,
        WebsiteNoProfile,
        IncorrectMethod,
        NotFound,
        ServerError,
        PreBlocked
    }
}
