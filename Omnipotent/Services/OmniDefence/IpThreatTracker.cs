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
        private readonly ConcurrentDictionary<string, ConcurrentQueue<long>> recentAuthFailures = new(StringComparer.OrdinalIgnoreCase);
        // IPs whose in-memory record has changed since the last persist. Only these get
        // written back on flush, so a large mostly-idle cache no longer re-persists every
        // record on every startup/flush (which used to hold the DB lock for many minutes).
        private readonly ConcurrentDictionary<string, byte> dirty = new(StringComparer.OrdinalIgnoreCase);

        // Tunables (overridable from OmniDefence service via Set* methods).
        public int AutoWatchScore { get; set; } = 50;
        public int AutoBlockScore { get; set; } = 200;
        public int FirstAlertThreshold { get; set; } = 1;       // alert on first unauth
        public int Escalation2Threshold { get; set; } = 50;
        public int Escalation3Threshold { get; set; } = 200;

        /// <summary>
        /// Raised (outside the record lock) when score-based auto-escalation changes an IP's status:
        /// (record, previous status, new status). These transitions used to leave no event behind.
        /// </summary>
        public Action<IpRecord, string, string>? AutoEscalated { get; set; }

        /// <summary>
        /// Classes the fingerprint engine only assigns on strong evidence that a person (or Klives)
        /// is behind the IP: a Klives-rank profile, or trusted input events from the browser beacon.
        /// Accumulated score never auto-escalates these; a probe or credential attack reclassifies
        /// the IP first (hard rules), which lifts the protection.
        /// </summary>
        public static bool IsProvenPerson(string? classification) =>
            classification is "Owner" or "Human";

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
            return cache.GetOrAdd(ip, key =>
            {
                // A brand-new IP must be persisted at least once.
                MarkDirty(key);
                return new IpRecord
                {
                    Ip = key,
                    FirstSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Status = nameof(IpStatus.Normal)
                };
            });
        }

        /// <summary>Flags an IP's record as needing to be written back on the next flush.</summary>
        public void MarkDirty(string? ip)
        {
            if (!string.IsNullOrEmpty(ip)) dirty[ip] = 0;
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
        /// Records an auth failure for this IP now and returns how many failures
        /// fall within the rolling window (including this one). In-memory only —
        /// resets on restart, which is acceptable: its purpose is deciding whether
        /// a failing caller gets a fast 403 or an escalating tarpit.
        /// </summary>
        public int RecordAuthFailureAndCount(string ip, TimeSpan window)
        {
            if (string.IsNullOrEmpty(ip)) return 0;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long cutoff = now - (long)window.TotalSeconds;
            var queue = recentAuthFailures.GetOrAdd(ip, _ => new ConcurrentQueue<long>());
            queue.Enqueue(now);
            // Prune expired entries and cap length so a hammering scanner can't grow memory.
            while (queue.TryPeek(out long oldest) && (oldest < cutoff || queue.Count > 100))
            {
                if (!queue.TryDequeue(out _)) break;
            }
            return queue.Count;
        }

        /// <summary>
        /// Records the outcome of a request for threat scoring purposes.
        /// Returns the updated record (post-mutation).
        /// </summary>
        public IpRecord RecordOutcome(string ip, RequestOutcome outcome)
        {
            var rec = GetOrCreate(ip);
            string? before, after;
            lock (rec)
            {
                before = rec.Status;
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
                        // The KM website's own traffic for a visitor with no (or an expired) session:
                        // its logged-out page guard and stale tabs. At +15 it outweighed a wrong
                        // password and auto-blocked share-link guests after 14 calls.
                        rec.UnauthAttempts++;
                        rec.DenyCount++;
                        rec.ThreatScore += 3;
                        break;
                    case RequestOutcome.IncorrectMethod:
                        rec.DenyCount++;
                        rec.ThreatScore += 1;
                        break;
                    case RequestOutcome.NotFound:
                        rec.ThreatScore += 0.5;
                        break;
                    case RequestOutcome.Probe:
                        // /.env, /.git/config, wp-login.php...: KliveAPI serves none of these, so each
                        // one is an exploit probe, not a typo. With the one-off VulnScanner class delta,
                        // about 14 reach the auto-block score (at +0.5 it took ~320).
                        rec.ThreatScore += 12;
                        break;
                    case RequestOutcome.HoneypotHit:
                        rec.DenyCount++;
                        rec.ThreatScore += 50;
                        break;
                    case RequestOutcome.ServerError:
                    case RequestOutcome.ClientError:
                        // not the IP's fault
                        break;
                    case RequestOutcome.PreBlocked:
                        rec.DenyCount++;
                        rec.ThreatScore += 1;
                        break;
                }
                rec.ThreatScore = Math.Clamp(rec.ThreatScore, 0, 1000);
                ApplyAutoEscalation(rec);
                after = rec.Status;
            }
            MarkDirty(ip);
            ReportEscalation(rec, before, after);
            return rec;
        }

        private void ReportEscalation(IpRecord rec, string? before, string? after)
        {
            if (string.Equals(before, after, StringComparison.Ordinal) || after == null) return;
            try { AutoEscalated?.Invoke(rec, before ?? nameof(IpStatus.Normal), after); } catch { /* observers never break scoring */ }
        }

        /// <summary>Auto-escalation of status: only escalates, never downgrades away from manual states. Caller holds lock(rec).</summary>
        private void ApplyAutoEscalation(IpRecord rec)
        {
            if (!string.IsNullOrWhiteSpace(rec.AssociatedProfileId) || rec.Status == nameof(IpStatus.Blocked) || rec.Status == nameof(IpStatus.Honeypot)) return;
            if (IsProvenPerson(rec.Classification)) return;
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

        /// <summary>
        /// One-off threat-score increase when fingerprinting moves an IP into a malicious
        /// class. Goes through the same auto-escalation as per-request scoring.
        /// </summary>
        public IpRecord? ApplyClassDelta(string ip, double delta)
        {
            if (delta == 0) return null;
            var rec = Get(ip);
            if (rec == null) return null;
            string? before, after;
            lock (rec)
            {
                before = rec.Status;
                rec.ThreatScore = Math.Clamp(rec.ThreatScore + delta, 0, 1000);
                ApplyAutoEscalation(rec);
                after = rec.Status;
            }
            MarkDirty(ip);
            ReportEscalation(rec, before, after);
            return rec;
        }

        private static int Severity(string? status) => status switch
        {
            nameof(IpStatus.Watch) => 1,
            nameof(IpStatus.Tarpit) => 2,
            nameof(IpStatus.Honeypot) => 3,
            nameof(IpStatus.Blocked) => 4,
            _ => 0
        };

        /// <summary>Raises an IP's status to <paramref name="status"/> unless it is already at least as severe. Returns true if it changed.</summary>
        public bool EscalateStatus(string ip, IpStatus status, string? reason)
        {
            var rec = Get(ip);
            if (rec == null) return false;
            lock (rec)
            {
                if (Severity(rec.Status) >= Severity(status.ToString())) return false;
                rec.Status = status.ToString();
                if (status == IpStatus.Blocked) rec.LastBlockReason = reason;
            }
            MarkDirty(ip);
            return true;
        }

        public Task PersistAsync(IpRecord rec)
        {
            // This record is being written now, so it no longer needs a flush.
            if (rec != null) dirty.TryRemove(rec.Ip, out _);
            return store.UpsertIpRecordAsync(rec);
        }

        public async Task PersistAllDirtyAsync()
        {
            // Snapshot and clear the dirty set up front so records mutated during the
            // write get re-flagged for the next flush rather than lost.
            var dirtyIps = dirty.Keys.ToArray();
            if (dirtyIps.Length == 0) return;
            foreach (var ip in dirtyIps) dirty.TryRemove(ip, out _);

            var toPersist = new List<IpRecord>(dirtyIps.Length);
            foreach (var ip in dirtyIps)
            {
                if (cache.TryGetValue(ip, out var rec)) toPersist.Add(rec);
            }
            if (toPersist.Count == 0) return;

            try
            {
                // One transaction for the whole batch instead of one per record.
                await store.UpsertIpRecordsAsync(toPersist);
            }
            catch
            {
                // Re-flag so a failed batch is retried on the next flush rather than dropped.
                foreach (var rec in toPersist) MarkDirty(rec.Ip);
                throw;
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
            MarkDirty(ip);
        }

        public void SetNotes(string ip, string? notes)
        {
            var rec = GetOrCreate(ip);
            lock (rec) { rec.Notes = notes; }
            MarkDirty(ip);
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
            MarkDirty(rec.Ip);
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
        PreBlocked,
        /// <summary>A normal 4xx client error (validation, conflict, payload limit); no threat score.</summary>
        ClientError,
        /// <summary>A non-existent route matching <see cref="Fingerprint.ProbePatterns"/> (secrets, VCS, CMS, shells).</summary>
        Probe,
        /// <summary>A request for a registered honeypot route (including the robots.txt trap).</summary>
        HoneypotHit
    }
}
