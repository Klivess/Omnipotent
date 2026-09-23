using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.OmniDefence.Fingerprint;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// Fingerprinting wiring: settings persistence, the engine + intel lifecycle, class-change
    /// consequences (events, score deltas, opt-in auto-actions) and browser-beacon ingestion.
    /// </summary>
    public partial class OmniDefence
    {
        /// <summary>How much audit history the first-run backfill folds.</summary>
        private const int BackfillMaxRows = 500_000;
        private static readonly TimeSpan BackfillWindow = TimeSpan.FromDays(30);

        // ── settings ──

        private async Task LoadSettingsAsync()
        {
            try
            {
                var map = await store.LoadSettingsAsync();
                settings = OmniDefenceSettings.Parse(map.TryGetValue(OmniDefenceSettings.StorageKey, out var json) ? json : null);
            }
            catch (Exception ex)
            {
                settings = new OmniDefenceSettings();
                await ServiceLogError(ex, "OmniDefence settings failed to load; using defaults.");
            }
            ApplySettingsToTracker(settings);
        }

        private void ApplySettingsToTracker(OmniDefenceSettings s)
        {
            tracker.AutoWatchScore = s.AutoWatchScore;
            tracker.AutoBlockScore = s.AutoBlockScore;
            tracker.Escalation2Threshold = s.Escalation2;
            tracker.Escalation3Threshold = s.Escalation3;
        }

        /// <summary>Validates, applies and persists new settings.</summary>
        public async Task SaveSettingsAsync(OmniDefenceSettings next)
        {
            next.AutoActions = next.AutoActions
                .Where(kv => OmniDefenceSettings.ActionableClasses.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => OmniDefenceSettings.AllowedActions.FirstOrDefault(a => a.Equals(kv.Value, StringComparison.OrdinalIgnoreCase)) ?? "None", StringComparer.OrdinalIgnoreCase);
            next.AutoActionMinConfidence = Math.Clamp(next.AutoActionMinConfidence, 0.5, 1.0);
            next.AbuseIpDbDailyBudget = Math.Clamp(next.AbuseIpDbDailyBudget, 0, 1000);
            next.GreyNoiseDailyBudget = Math.Clamp(next.GreyNoiseDailyBudget, 0, 1000);
            next.RobotsTrapPath ??= settings.RobotsTrapPath;

            bool trapToggled = next.RobotsTrapEnabled != settings.RobotsTrapEnabled;
            settings = next;
            ApplySettingsToTracker(next);
            await store.SetSettingAsync(OmniDefenceSettings.StorageKey, JsonConvert.SerializeObject(next));
            if (fingerprints != null) fingerprints.RobotsTrapRoute = RobotsTrapRoute;
            if (trapToggled) await EnsureRobotsTrapAsync();
        }

        // ── robots trap ──

        /// <summary>
        /// robots.txt disallows one path nothing links to. Fetching robots.txt is a bot tell;
        /// then fetching the disallowed path means a bot that reads robots and ignores it.
        /// The path is a honeypot route, so it also answers with junk.
        /// </summary>
        private async Task EnsureRobotsTrapAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(settings.RobotsTrapPath))
                {
                    settings.RobotsTrapPath = "/archive/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + "/export";
                    await store.SetSettingAsync(OmniDefenceSettings.StorageKey, JsonConvert.SerializeObject(settings));
                }
                string trap = settings.RobotsTrapPath!;
                if (settings.RobotsTrapEnabled && !honeypotRoutes.ContainsKey(trap))
                    await RegisterHoneypotRouteAsync(trap, "Robots trap (auto): disallowed in robots.txt");
                else if (!settings.RobotsTrapEnabled && honeypotRoutes.ContainsKey(trap))
                    await RemoveHoneypotRouteAsync(trap);
            }
            catch (Exception ex) { await ServiceLogError(ex, "OmniDefence robots trap setup failed."); }
        }

        public string BuildRobotsTxt()
        {
            var sb = new StringBuilder();
            sb.Append("User-agent: *\n");
            if (settings.RobotsTrapEnabled && !string.IsNullOrEmpty(settings.RobotsTrapPath))
                sb.Append("Disallow: ").Append(settings.RobotsTrapPath).Append('\n');
            sb.Append("Disallow: /omnidefence/\n");
            return sb.ToString();
        }

        // ── lifecycle ──

        private async Task StartFingerprintingAsync()
        {
            try
            {
                string feedDir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDefenceDirectory), "feeds");
                intel = new IpIntelService(store, feedDir)
                {
                    Settings = () => settings,
                    GetApiKey = GetIntelApiKeyAsync,
                    LogError = (ex, msg) => _ = ServiceLogError(ex, msg),
                    Log = msg => _ = ServiceLog(msg),
                };
                await intel.LoadAsync();

                fingerprints = new FingerprintEngine(store)
                {
                    IsHoneypotRoute = route => honeypotRoutes.ContainsKey(route),
                    RobotsTrapRoute = RobotsTrapRoute,
                    GetRecord = ip => tracker.Get(ip),
                    MarkRecordDirty = ip => tracker.MarkDirty(ip),
                    GetIntel = (ip, rec, fp) => intel.Compose(ip, rec, fp),
                    // Backfilled history gets free rDNS but never spends keyed-API budget.
                    AfterClassify = (fp, rec) => { if (settings.FingerprintEnabled) intel.Consider(fp, rec, allowKeyed: !fingerprints!.BackfillRunning); },
                    ClassChanged = OnClassChanged,
                    LogError = (ex, msg) => _ = ServiceLogError(ex, msg),
                };
                intel.IntelUpdated = ip => fingerprints.Reclassify(ip);
                intel.FeedsUpdated = () => fingerprints.ReclassifyAll();

                await fingerprints.LoadAsync();
                ServiceQuitRequest += () =>
                {
                    try { intelCts.Cancel(); } catch { }
                    try { fingerprints?.Dispose(); } catch { }
                };

                bool needsBackfill = fingerprints.Count == 0;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (needsBackfill)
                        {
                            long minUtc = DateTimeOffset.UtcNow.Add(-BackfillWindow).ToUnixTimeSeconds();
                            await fingerprints.BackfillAsync(BackfillMaxRows, minUtc, msg => _ = ServiceLog(msg));
                        }
                    }
                    catch (Exception ex) { await ServiceLogError(ex, "OmniDefence fingerprint backfill failed; continuing with live traffic only."); }
                    finally
                    {
                        // Live signals have been buffering in the channel; start draining them.
                        fingerprints.Start();
                        await ServiceLog($"OmniDefence fingerprinting active: {fingerprints.Count} fingerprints, " +
                            $"{intel.DatacenterRanges} datacenter / {intel.CrawlerRanges} crawler ranges, {intel.TorExits} Tor exits.");
                    }
                });
                _ = intel.RunFeedLoopAsync(intelCts.Token);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "OmniDefence fingerprinting failed to start; request recording is unaffected.");
            }
        }

        private readonly ConcurrentDictionary<string, (string? Value, DateTime At)> apiKeyCache = new();

        private async Task<string?> GetIntelApiKeyAsync(string name)
        {
            if (apiKeyCache.TryGetValue(name, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(5)) return cached.Value;
            string? value = null;
            try { value = await GetOmniSetting(name, OmniSettingType.String, sensitive: true, askKlivesForFulfillment: false); }
            catch { }
            value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            apiKeyCache[name] = (value, DateTime.UtcNow);
            return value;
        }

        /// <summary>Whether an intel API key is configured (the value itself is never exposed).</summary>
        public async Task<bool> HasIntelApiKeyAsync(string name) => await GetIntelApiKeyAsync(name) != null;

        // ── request-path hooks (never block) ──

        public void OfferFingerprintSignals(RequestSignals signals)
        {
            if (!settings.FingerprintEnabled) return;
            fingerprints?.Offer(signals);
        }

        /// <summary>A /batch sub-request: counts toward route coverage without re-counting the request.</summary>
        public void NoteBatchRoute(string ip, string route)
        {
            if (!settings.FingerprintEnabled || fingerprints == null || string.IsNullOrEmpty(ip)) return;
            fingerprints.Post(ip, fp =>
            {
                fp.ViaBatch++;
                if (fp.Routes.Count < IpFingerprint.MaxRoutes) fp.Routes.Add(route);
            }, urgent: false);
        }

        // ── class-change consequences ──

        private void OnClassChanged(ClassChange change)
        {
            // Backfill relabels history; it must not punish or alert on old traffic.
            if (change.FromBackfill) return;
            var rec = tracker.Get(change.Ip);
            string reasons = string.Join("; ", change.Result.Evidence.Take(3).Select(e => e.Reason));

            _ = RecordIpEventAsync(new IpEventRow
            {
                UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ip = change.Ip,
                Kind = "Classified:" + change.Current,
                Detail = JsonConvert.SerializeObject(new
                {
                    from = change.Previous.ToString(),
                    to = change.Current.ToString(),
                    confidence = change.Result.Confidence,
                    tags = change.Result.Tags,
                    reasons = change.Result.Evidence.Take(5).Select(e => e.Reason)
                })
            });

            if (rec == null) return;
            bool protectedIp;
            lock (rec) protectedIp = !string.IsNullOrWhiteSpace(rec.AssociatedProfileId);
            if (protectedIp || tracker.IsLinkedToKlives(change.Ip)) return;

            var s = settings;
            if (s.ClassScoreDeltas)
            {
                double delta = ClassifierWeights.ScoreDelta(change.Current);
                if (delta > 0) tracker.ApplyClassDelta(change.Ip, delta);
            }

            string action = s.ActionFor(change.Current);
            if (action == "None" || change.Result.Confidence < s.AutoActionMinConfidence) return;
            if (!Enum.TryParse<IpThreatTracker.IpStatus>(action, true, out var status)) return;

            string reason = $"Auto: classified {change.Current} ({change.Result.Confidence:P0}) — {reasons}";
            if (reason.Length > 400) reason = reason.Substring(0, 400);
            if (!tracker.EscalateStatus(change.Ip, status, reason)) return;

            _ = RecordIpEventAsync(new IpEventRow
            {
                UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ip = change.Ip,
                Kind = "AutoAction:" + status,
                Detail = reason
            });
            if (status == IpThreatTracker.IpStatus.Blocked) _ = SendBlockNotificationAsync(rec, "OmniDefence fingerprinting");
        }

        // ── browser beacon ──

        private readonly byte[] beaconSecret = RandomNumberGenerator.GetBytes(32);
        private readonly ConcurrentDictionary<string, (long Minute, int Count)> beaconRate = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> deviceIps = new(StringComparer.Ordinal);
        public const int BeaconMaxBytes = 16 * 1024;
        private const int BeaconsPerMinute = 20;
        private const long NonceBucketSeconds = 600;

        public string IssueBeaconNonce(string ip) => BeaconNonce(ip, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / NonceBucketSeconds);

        private string BeaconNonce(string ip, long bucket)
        {
            byte[] mac = HMACSHA256.HashData(beaconSecret, Encoding.UTF8.GetBytes(ip + "|" + bucket));
            return Convert.ToBase64String(mac, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>Nonces bind a beacon to the IP that fetched it within ~10–20 minutes.</summary>
        public bool ValidateBeaconNonce(string ip, string? nonce)
        {
            if (string.IsNullOrEmpty(nonce)) return false;
            long bucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / NonceBucketSeconds;
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(nonce), Encoding.UTF8.GetBytes(BeaconNonce(ip, bucket)))
                || CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(nonce), Encoding.UTF8.GetBytes(BeaconNonce(ip, bucket - 1)));
        }

        public bool TryTakeBeaconSlot(string ip)
        {
            long minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            var next = beaconRate.AddOrUpdate(ip, (minute, 1), (_, cur) => cur.Minute == minute ? (minute, cur.Count + 1) : (minute, 1));
            if (beaconRate.Count > 50_000) beaconRate.Clear();
            return next.Count <= BeaconsPerMinute;
        }

        /// <summary>
        /// Folds a validated beacon into the IP's fingerprint. Returns false when the payload
        /// is unusable. Evidence, not proof: a forged "perfect human" beacon still loses to
        /// header and TLS contradictions.
        /// </summary>
        public bool IngestBeacon(string ip, string? headerUserAgent, JObject payload)
        {
            if (fingerprints == null || !settings.BeaconEnabled) return false;
            var parsed = BeaconAnalyzer.Analyze(payload, headerUserAgent, tracker.Get(ip)?.Timezone);
            if (parsed == null) return false;

            int ipsForDevice = 0;
            if (!string.IsNullOrEmpty(parsed.DeviceId))
            {
                var ips = deviceIps.GetOrAdd(parsed.DeviceId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
                if (ips.Count < 64) ips[ip] = 0;
                ipsForDevice = ips.Count;
                if (deviceIps.Count > 100_000) deviceIps.Clear();
                _ = store.UpsertDeviceAsync(parsed.DeviceId, ip, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), parsed.CompactJson);
            }

            fingerprints.Post(ip, fp => BeaconAnalyzer.Merge(fp, parsed, ipsForDevice), urgent: true);
            return true;
        }
    }
}
