using DSharpPlus.Entities;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Profiles;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.OmniDefence.Fingerprint;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// Cyberdefence service for Omnipotent. Tracks every API request, every
    /// authentication event, every KM profile action, and every IP that talks
    /// to KliveAPI. Provides Klives-only routes to query/filter the data and
    /// to ban / watch / honeypot / scan attacker IPs.
    /// </summary>
    public partial class OmniDefence : OmniService
    {
        private OmniDefenceStore store = null!;
        // Null until the store has opened. The service counts as active from the moment it starts,
        // so request-path hooks must treat a null tracker as "not ready yet", never dereference it.
        private volatile IpThreatTracker tracker = null!;
        private OmniDefenceOverviewSnapshot? overviewSnapshot;
        private OmniDefenceReadSnapshotCache? readSnapshots;
        // Start-up step by step, and the profile-link pass that follows it (both in OverviewStatus).
        private readonly OmniDefenceStartupTrace startup = new();
        private volatile object? profileLinkStatus;
        private OmniDefenceScanner scanner = new();
        private static readonly HttpClient GeoIpClient = new() { Timeout = TimeSpan.FromSeconds(3) };

        // Discord notification dedupe state (separate from DB so it survives intra-request scenarios fast)
        private readonly ConcurrentDictionary<string, byte> recentlyScannedIps = new();
        private readonly ConcurrentDictionary<string, DateTime> lastScanByIp = new();
        private readonly ConcurrentDictionary<string, byte> geoLookupsInFlight = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> geoLookupCooldown = new(StringComparer.OrdinalIgnoreCase);

        // Cached set of routes registered as honeypots (route -> response kind)
        private readonly ConcurrentDictionary<string, string> honeypotRoutes = new(StringComparer.OrdinalIgnoreCase);

        // Cached set of blocked geographic regions (id -> row).
        private readonly ConcurrentDictionary<long, BlockedRegionRow> blockedRegions = new();
        private volatile BlockedRegionRow[] blockedRegionSnapshot = Array.Empty<BlockedRegionRow>();

        // Fingerprinting: per-IP signal aggregation + classification, and external intel.
        private FingerprintEngine? fingerprints;
        private IpIntelService? intel;
        private volatile OmniDefenceSettings settings = new();
        private readonly CancellationTokenSource intelCts = new();

        // ip-api's free tier allows 45 lookups/minute; stay under it so a burst of new IPs
        // doesn't earn a ban (a 429 would otherwise read as a failure and cool down 6h).
        private readonly object geoRateLock = new();
        private readonly Queue<DateTime> geoRecentLookups = new();
        private const int GeoLookupsPerMinute = 40;

        public OmniDefence()
        {
            name = "OmniDefence";
            threadAnteriority = ThreadAnteriority.High;
        }

        public OmniDefenceStore Store => store;
        public IpThreatTracker Tracker => tracker;
        internal string? OverviewJson => overviewSnapshot?.CurrentJson;
        internal long OverviewBuildMs => overviewSnapshot?.LastBuildMs ?? 0;
        /// <summary>The overview builder names each step as it starts it, so a stalled build can say where it is.</summary>
        internal void OverviewStep(string step) => overviewSnapshot?.ReportStep(step);
        /// <summary>How the overview build is going. Answers whether or not an overview exists.</summary>
        internal object OverviewStatus() => new
        {
            overview = overviewSnapshot?.Status(),
            auditQueue = store?.PendingAuditRows ?? 0,
            auditRowsDropped = store?.DroppedAuditRows ?? 0,
            readPoolTimeouts = store?.ReadPoolTimeouts ?? 0,
            // How long the last start took, step by step: this route only exists once it is over.
            startup = startup.Status(),
            // The pass that follows start-up: which audit rows it read to link IPs to profiles.
            profileLinks = profileLinkStatus,
        };
        internal OmniDefenceReadSnapshotCache ReadSnapshots => readSnapshots
            ?? throw new InvalidOperationException("OmniDefence read snapshots are not running.");
        public OmniDefenceScanner Scanner => scanner;
        public FingerprintEngine? Fingerprints => fingerprints;
        public IpIntelService? Intel => intel;
        public OmniDefenceSettings Settings => settings;
        public string? RobotsTrapRoute => settings.RobotsTrapEnabled ? settings.RobotsTrapPath : null;

        protected override async void ServiceMain()
        {
            // Every step is named and timed (see OmniDefenceStartupTrace): whatever runs before
            // the routes register is how long the whole service is missing.
            startup.Begin();
            _ = ReportSlowStartupAsync();
            try
            {
                Directory.CreateDirectory(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDefenceDirectory));
                store = new OmniDefenceStore(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDefenceDatabaseFile));
                // Three steps of its own: opening the database, checking the schema, scrubbing
                // settings audits.
                await store.InitializeAsync(startup.Step);

                // Audit rows are batched, so a shutdown between flushes would drop the
                // tail of the log. Commit what is queued on the way out.
                ServiceQuitRequest += () => { try { store.ShutdownAsync().GetAwaiter().GetResult(); } catch { } };

                tracker = new IpThreatTracker(store) { AutoEscalated = OnAutoEscalated };
                await startup.Step("settings", LoadSettingsAsync);
                await startup.Step("IP records", () => tracker.LoadAsync());

                await startup.Step("honeypot routes", async () =>
                {
                    foreach (var hp in await store.ListHoneypotRoutesAsync())
                    {
                        honeypotRoutes[hp.Route] = hp.ResponseKind;
                    }
                    await EnsureRobotsTrapAsync();
                });

                await startup.Step("blocked regions", async () =>
                {
                    foreach (var region in await store.ListBlockedRegionsAsync())
                    {
                        blockedRegions[region.Id] = region;
                    }
                    RefreshBlockedRegionSnapshot();
                });

                await ServiceLog($"OmniDefence active. Loaded {tracker.All().Count()} IP records, {honeypotRoutes.Count} honeypot routes, {blockedRegions.Count} blocked regions.");

                await startup.Step("snapshots", () =>
                {
                    // The overview is expensive to aggregate. Load a recent durable body
                    // immediately, then keep rebuilding it on a single background worker.
                    string overviewPath = Path.Combine(
                        OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDefenceDirectory),
                        "overview.snapshot.json");
                    var snapshot = new OmniDefenceOverviewSnapshot(overviewPath,
                        () => OmniDefenceRoutes.BuildOverviewJsonAsync(this),
                        ex => _ = ServiceLogError(ex, "OmniDefence overview refresh failed."));
                    overviewSnapshot = snapshot;
                    snapshot.LoadFromDisk();
                    var refreshCts = new CancellationTokenSource();
                    ServiceQuitRequest += () => refreshCts.Cancel();
                    _ = Task.Run(() => snapshot.RunAsync(refreshCts.Token));

                    var reads = new OmniDefenceReadSnapshotCache(Path.Combine(
                        OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDefenceDirectory), "read-snapshots"),
                        ex => _ = ServiceLogError(ex, "OmniDefence read snapshot refresh failed."));
                    reads.RemoveLegacyAuditSnapshots();
                    readSnapshots = reads;
                    var readCts = new CancellationTokenSource();
                    ServiceQuitRequest += () => readCts.Cancel();
                    reads.Start(readCts.Token);
                    OmniDefenceRoutes.WarmDefaultRequests(this);
                    return Task.CompletedTask;
                });

                // Periodic flush so threat scores survive restarts.
                _ = PeriodicFlushLoop();

                await startup.Step("routes", () => OmniDefenceRoutes.RegisterAsync(this));

                startup.Ready();
                await ServiceLog($"OmniDefence start-up took {startup.Summary()}.");

                // After the routes, never before them: nothing on the start path waits for these.
                _ = Task.Run(FinishStartupAsync);
            }
            catch (Exception ex)
            {
                startup.Failed(ex);
                await ServiceLogError(ex, "OmniDefence failed to start.");
            }
        }

        /// <summary>
        /// What start-up leaves until the routes exist. Loading the fingerprint engine and its intel
        /// took 193.8 s of a 318.5 s start on 11 Nov 2026, and every /omnidefence route waited for
        /// it. The engine queues signals and commands until it has loaded, and each route answers
        /// without it, so it now loads once the service is up. The profile links follow: they read
        /// only the audit rows added since the last start. One after the other, because each holds
        /// one of the two read slots while it reads.
        /// </summary>
        private async Task FinishStartupAsync()
        {
            await startup.Step("fingerprinting", StartFingerprintingAsync);
            await CatchUpProfileLinksAsync();
        }

        /// <summary>
        /// A start that overruns says so, once, naming its step. A slow one used to leave only
        /// "Started service." in the log while every /omnidefence route answered 404.
        /// </summary>
        private async Task ReportSlowStartupAsync()
        {
            try { await Task.Delay(OmniDefenceStartupTrace.SlowAfter, cancellationToken?.Token ?? CancellationToken.None); }
            catch (OperationCanceledException) { return; }
            if (!startup.Running) return;
            await ServiceLogError(new TimeoutException(
                $"OmniDefence start-up has run for over {OmniDefenceStartupTrace.SlowAfter.TotalMinutes:0} minutes and is still in step '{startup.CurrentStep}'. " +
                "The steps after it, and every /omnidefence route, wait for it."));
        }

        /// <summary>Score-based Watch/Block transitions get an IP event like every manual one.</summary>
        private void OnAutoEscalated(IpRecord rec, string previous, string current)
        {
            string? reason;
            double score;
            lock (rec) { reason = rec.LastBlockReason; score = rec.ThreatScore; }
            _ = RecordIpEventAsync(new IpEventRow
            {
                UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ip = rec.Ip,
                Kind = "Auto" + current,
                Detail = current == nameof(IpThreatTracker.IpStatus.Blocked) && reason != null
                    ? reason
                    : $"Threat score {score:F0} (was {previous})"
            });
            readSnapshots?.Invalidate(OmniDefenceReadSnapshotCache.KeyFor("ip", rec.Ip));
        }

        private async Task PeriodicFlushLoop()
        {
            while (cancellationToken == null || !cancellationToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken?.Token ?? CancellationToken.None); }
                catch (OperationCanceledException) { return; }
                try { await tracker.PersistAllDirtyAsync(); }
                catch (Exception ex) { await ServiceLogError(ex, "OmniDefence periodic flush failed."); }
            }
        }

        // ------------------------------------------------------------------
        //  Pre-dispatch hook from KliveAPI
        // ------------------------------------------------------------------

        /// <summary>
        /// Gate decision for one request. Until the IP records have loaded there is nothing to judge
        /// with, so requests pass: a null tracker used to throw here, and KliveAPI answered every
        /// request on the server with a 500 for as long as the store took to open.
        /// </summary>
        public IpThreatTracker.GateDecision EvaluateRequestGate(string ip, string route)
        {
            var records = tracker;
            if (string.IsNullOrEmpty(ip) || records is null) return IpThreatTracker.GateDecision.Allow;
            if (records.IsLinkedToKlives(ip)) return IpThreatTracker.GateDecision.Allow;
            // Honeypot routes always honeypot regardless of IP status.
            if (honeypotRoutes.ContainsKey(route)) return IpThreatTracker.GateDecision.Honeypot;
            var decision = records.Evaluate(ip);
            if (decision != IpThreatTracker.GateDecision.Allow) return decision;

            var rec = records.Get(ip);
            if (TryApplyBlockedRegion(rec, out string regionReason))
            {
                _ = PersistRegionBlockAsync(rec!, regionReason);
                return IpThreatTracker.GateDecision.Block;
            }

            return IpThreatTracker.GateDecision.Allow;
        }

        public bool IsLinkedToKlives(string ip) => tracker?.IsLinkedToKlives(ip) == true;

        /// <summary>
        /// Registers an auth failure for the IP and returns the count of failures
        /// within the last 10 minutes (including this one). KliveAPI uses this to
        /// fast-fail the first few failures (stale cookies) and tarpit only repeat
        /// offenders.
        /// </summary>
        public int RegisterAuthFailure(string ip)
            => tracker?.RecordAuthFailureAndCount(ip, TimeSpan.FromMinutes(10)) ?? 0;

        public bool IsHoneypotRoute(string route) => honeypotRoutes.ContainsKey(route);

        // ------------------------------------------------------------------
        //  Recording API (called from KliveAPI ProcessRequestAsync / KMProfileManager etc.)
        // ------------------------------------------------------------------

        public async Task RecordRequestAsync(RequestRow row, RequestOutcome outcome)
        {
            try
            {
                // Requests that land while the store is still opening are not scored (there is no
                // tracker yet); they used to throw here and log one error per request.
                var records = tracker;
                if (!string.IsNullOrEmpty(row.Ip) && records is not null)
                {
                    records.LinkProfile(records.GetOrCreate(row.Ip!), row.ProfileId, row.ProfileName, row.ProfileRank, row.UtcTimestamp);
                    outcome = ScoringOutcome(row, outcome);
                    var rec = records.RecordOutcome(row.Ip!, outcome);
                    _ = EnrichIpRecordAsync(rec);
                    if (string.IsNullOrWhiteSpace(rec.AssociatedProfileId)) _ = MaybeAlertAsync(rec, row, outcome);
                }
                // Queued, not inserted: this runs in the finally of every API request, so
                // a per-row transaction here sets the write lock's arrival rate to the
                // site's total traffic and every reader queues behind it.
                store?.EnqueueRequest(row);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "OmniDefence RecordRequestAsync failed.");
            }
        }

        /// <summary>
        /// KliveAPI reports every unknown route as NotFound. For scoring, a path that only exploit
        /// tooling asks for (<see cref="ProbePatterns"/>) is a probe: it used to score the same +0.5
        /// as a typo, so scanners fired hundreds of /.env and /.git requests before any block.
        /// </summary>
        internal static RequestOutcome ScoringOutcome(RequestRow row, RequestOutcome outcome)
        {
            if (outcome == RequestOutcome.NotFound && !row.MatchedRoute && ProbePatterns.IsProbe(row.Route, row.Query))
                return RequestOutcome.Probe;
            return outcome;
        }

        public async Task RecordAuthEventAsync(AuthEventRow row)
        {
            try { await store.InsertAuthEventAsync(row); }
            catch (Exception ex) { await ServiceLogError(ex, "OmniDefence RecordAuthEventAsync failed."); }
        }

        public async Task RecordProfileAction(KMProfileManager.KMProfile? profile, string category, string action, object? detail = null, string? ip = null)
        {
            try
            {
                var row = new ProfileActionRow
                {
                    UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ProfileId = profile?.UserID,
                    ProfileName = profile?.Name,
                    Ip = ip,
                    Category = category ?? "",
                    Action = action ?? "",
                    DetailJson = detail == null ? null : JsonConvert.SerializeObject(detail)
                };
                await store.InsertProfileActionAsync(row);
            }
            catch (Exception ex) { await ServiceLogError(ex, "OmniDefence RecordProfileAction failed."); }
        }

        public async Task RecordIpEventAsync(IpEventRow row)
        {
            try { await store.InsertIpEventAsync(row); }
            catch (Exception ex) { await ServiceLogError(ex, "OmniDefence RecordIpEventAsync failed."); }
        }

        /// <summary>
        /// Links IPs to the profile that last used them, from audit rows added since the last pass
        /// (<see cref="IpThreatTracker.CatchUpProfileLinksAsync"/>). Runs once start-up is over, in
        /// the background. It used to run inside start-up as one query over the whole request log,
        /// and everything after it waited: on 10 Nov 2026 the routes were still missing 82 and 92
        /// minutes after two starts.
        /// </summary>
        private async Task CatchUpProfileLinksAsync()
        {
            static object Status(bool running, IpThreatTracker.ProfileLinkPass? pass, long? ms, string? error) => new
            {
                running,
                fromId = pass?.FromId,
                throughId = pass?.ThroughId,
                rowsRead = pass?.RowsRead,
                ms,
                error,
            };

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            long Elapsed() => (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            profileLinkStatus = Status(true, null, null, null);
            try
            {
                var pass = await tracker.CatchUpProfileLinksAsync(
                    cancellationToken: cancellationToken?.Token ?? CancellationToken.None);
                profileLinkStatus = Status(false, pass, Elapsed(), null);
            }
            catch (OperationCanceledException)
            {
                profileLinkStatus = Status(false, null, Elapsed(), "cancelled");
            }
            catch (Exception ex)
            {
                profileLinkStatus = Status(false, null, Elapsed(), ex.Message);
                await ServiceLogError(ex, "OmniDefence profile link catch-up failed.");
            }
        }

        private async Task EnrichIpRecordAsync(IpRecord rec)
        {
            if (rec == null || string.IsNullOrWhiteSpace(rec.Ip)) return;
            lock (rec)
            {
                bool hasDetailedGeo = (!string.IsNullOrWhiteSpace(rec.City) || !string.IsNullOrWhiteSpace(rec.Region) || !string.IsNullOrWhiteSpace(rec.Isp) || !string.IsNullOrWhiteSpace(rec.Org)) && rec.Latitude.HasValue && rec.Longitude.HasValue;
                bool isKnownPrivate = string.Equals(rec.Country, "Private", StringComparison.OrdinalIgnoreCase) && string.Equals(rec.Asn, "Local", StringComparison.OrdinalIgnoreCase);
                // Records geolocated before the hosting/proxy/mobile flags existed get one more lookup.
                bool hasNetworkFlags = rec.IsHosting.HasValue;
                if (isKnownPrivate || (hasDetailedGeo && hasNetworkFlags))
                {
                    if (hasDetailedGeo) _ = EnforceBlockedRegionsAsync(rec);
                    return;
                }
            }

            if (!geoLookupsInFlight.TryAdd(rec.Ip, 0)) return;
            try
            {
                if (geoLookupCooldown.TryGetValue(rec.Ip, out var lastFail) && DateTime.UtcNow - lastFail < TimeSpan.FromHours(6)) return;

                if (!IPAddress.TryParse(rec.Ip, out var address) || IsPrivateOrLocalAddress(address))
                {
                    lock (rec)
                    {
                        rec.Country = "Private";
                        rec.Asn = "Local";
                        rec.City = null;
                        rec.Region = null;
                        rec.Isp = null;
                        rec.Org = null;
                        rec.Latitude = null;
                        rec.Longitude = null;
                    }
                    await tracker.PersistAsync(rec);
                    return;
                }

                if (!TryTakeGeoLookupSlot()) return; // over the rate limit: the next request retries
                string url = $"http://ip-api.com/json/{rec.Ip}?fields=status,country,countryCode,regionName,city,zip,isp,org,as,asname,lat,lon,timezone,mobile,proxy,hosting,query,message";
                string json = await GeoIpClient.GetStringAsync(url);
                var result = JsonConvert.DeserializeObject<GeoIpResponse>(json);
                if (result?.Status == "success")
                {
                    lock (rec)
                    {
                        rec.Country = string.IsNullOrWhiteSpace(result.CountryCode) ? result.Country : result.CountryCode;
                        rec.Asn = result.Asn;
                        rec.City = result.City;
                        rec.Region = result.RegionName;
                        rec.Isp = result.Isp;
                        rec.Org = result.Org;
                        rec.Latitude = result.Latitude;
                        rec.Longitude = result.Longitude;
                        rec.IsHosting = result.Hosting;
                        rec.IsProxy = result.Proxy;
                        rec.IsMobile = result.Mobile;
                        rec.Timezone = result.Timezone;
                        rec.AsName = result.AsName;
                    }
                    await tracker.PersistAsync(rec);
                    fingerprints?.Reclassify(rec.Ip);
                    await RecordIpEventAsync(new IpEventRow
                    {
                        UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Ip = rec.Ip,
                        Kind = "GeoIP",
                        Detail = JsonConvert.SerializeObject(new { result.Country, result.CountryCode, result.RegionName, result.City, result.Zip, result.Isp, result.Org, result.Asn, result.AsName, result.Latitude, result.Longitude, result.Timezone, result.Hosting, result.Proxy, result.Mobile })
                    });
                    await EnforceBlockedRegionsAsync(rec);
                }
                else
                {
                    geoLookupCooldown[rec.Ip] = DateTime.UtcNow;
                }
            }
            catch
            {
                geoLookupCooldown[rec.Ip] = DateTime.UtcNow;
            }
            finally
            {
                geoLookupsInFlight.TryRemove(rec.Ip, out _);
            }
        }

        private static bool IsPrivateOrLocalAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) return true;
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = address.GetAddressBytes();
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254);
            }
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.ToString().StartsWith("fc", StringComparison.OrdinalIgnoreCase) || address.ToString().StartsWith("fd", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private sealed class GeoIpResponse
        {
            [JsonProperty("status")]
            public string? Status { get; set; }

            [JsonProperty("country")]
            public string? Country { get; set; }

            [JsonProperty("countryCode")]
            public string? CountryCode { get; set; }

            [JsonProperty("regionName")]
            public string? RegionName { get; set; }

            [JsonProperty("city")]
            public string? City { get; set; }

            [JsonProperty("zip")]
            public string? Zip { get; set; }

            [JsonProperty("isp")]
            public string? Isp { get; set; }

            [JsonProperty("org")]
            public string? Org { get; set; }

            [JsonProperty("timezone")]
            public string? Timezone { get; set; }

            [JsonProperty("as")]
            public string? Asn { get; set; }

            [JsonProperty("lat")]
            public double? Latitude { get; set; }

            [JsonProperty("lon")]
            public double? Longitude { get; set; }

            [JsonProperty("asname")]
            public string? AsName { get; set; }

            [JsonProperty("hosting")]
            public bool? Hosting { get; set; }

            [JsonProperty("proxy")]
            public bool? Proxy { get; set; }

            [JsonProperty("mobile")]
            public bool? Mobile { get; set; }
        }

        private bool TryTakeGeoLookupSlot()
        {
            lock (geoRateLock)
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-1);
                while (geoRecentLookups.Count > 0 && geoRecentLookups.Peek() < cutoff) geoRecentLookups.Dequeue();
                if (geoRecentLookups.Count >= GeoLookupsPerMinute) return false;
                geoRecentLookups.Enqueue(DateTime.UtcNow);
                return true;
            }
        }

        // ------------------------------------------------------------------
        //  Discord notifier (replaces the per-request spam)
        // ------------------------------------------------------------------

        /// <summary>
        /// Whether this request may raise "new attacker" / escalation alerts. Over 60 days, 26 of the 33
        /// IPs that alerted were people on the KM website (17 with real-input beacon proof): its
        /// logged-out page guard and expired sessions arrive as WebsiteNoProfile, which counted as
        /// an unauth attempt. Scanners, meanwhile, only produce 404s and never alerted at all.
        /// </summary>
        internal static bool IsAlertable(IpRecord rec, RequestRow row, RequestOutcome outcome)
        {
            if (outcome == RequestOutcome.HoneypotHit) return true;
            if (row.DenyReason == null && row.StatusCode < 400) return false; // only alert on suspicious activity
            if (outcome == RequestOutcome.WebsiteNoProfile) return false;
            string? cls;
            lock (rec) cls = rec.Classification;
            return !(IpThreatTracker.IsProvenPerson(cls) || cls == nameof(IpClass.LikelyHuman));
        }

        private async Task MaybeAlertAsync(IpRecord rec, RequestRow row, RequestOutcome outcome)
        {
            try
            {
                if (rec == null) return;
                if (!IsAlertable(rec, row, outcome)) return;

                bool first;
                int? escalation = null;
                lock (rec)
                {
                    first = rec.FirstAlertedUtc == null && (rec.UnauthAttempts >= tracker.FirstAlertThreshold || outcome == RequestOutcome.HoneypotHit);
                    if (first)
                    {
                        rec.FirstAlertedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        rec.LastAlertedUtc = rec.FirstAlertedUtc;
                        rec.EscalationLevel = 1;
                    }
                    else if (rec.EscalationLevel < 2 && rec.UnauthAttempts >= tracker.Escalation2Threshold)
                    {
                        rec.EscalationLevel = 2;
                        escalation = 2;
                        rec.LastAlertedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    }
                    else if (rec.EscalationLevel < 3 && rec.UnauthAttempts >= tracker.Escalation3Threshold)
                    {
                        rec.EscalationLevel = 3;
                        escalation = 3;
                        rec.LastAlertedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    }
                }

                // Alert bookkeeping mutated the record; ensure it survives the next flush.
                if (first || escalation.HasValue) tracker.MarkDirty(rec.Ip);

                if (first)
                {
                    var builder = KliveBotDiscord.MakeSimpleEmbed(
                        $"OmniDefence: New attacker detected ({rec.Ip})",
                        BuildAlertDescription(rec, row, outcome == RequestOutcome.HoneypotHit ? "Requested a honeypot route" : "First detection"),
                        DiscordColor.Yellow);
                    await SendDiscordBuilderSafeAsync(builder);
                }
                if (escalation.HasValue)
                {
                    var color = escalation == 3 ? DiscordColor.Red : DiscordColor.Orange;
                    var builder = KliveBotDiscord.MakeSimpleEmbed(
                        $"OmniDefence: Escalation L{escalation} ({rec.Ip})",
                        BuildAlertDescription(rec, row, $"Escalated to level {escalation}"),
                        color);
                    await SendDiscordBuilderSafeAsync(builder);
                }
            }
            catch (Exception ex) { await ServiceLogError(ex, "OmniDefence MaybeAlertAsync failed."); }
        }

        private static string BuildAlertDescription(IpRecord rec, RequestRow row, string reason)
        {
            return $"**Reason:** {reason}\n" +
                   $"**IP:** `{rec.Ip}`\n" +
                   $"**Threat score:** {rec.ThreatScore:F0}\n" +
                   $"**Unauth attempts:** {rec.UnauthAttempts}\n" +
                   $"**Total requests:** {rec.TotalRequests}\n" +
                   $"**Status:** {rec.Status}\n" +
                   $"**Last route:** `{row.Method} {row.Route}`\n" +
                   $"**User-agent:** {row.UserAgent ?? "(none)"}";
        }

        public async Task SendBlockNotificationAsync(IpRecord rec, string actor)
        {
            try
            {
                var builder = KliveBotDiscord.MakeSimpleEmbed(
                    $"OmniDefence: IP blocked ({rec.Ip})",
                    $"**Actor:** {actor}\n" +
                    $"**Reason:** {rec.LastBlockReason ?? "(none)"}\n" +
                    $"**Threat score:** {rec.ThreatScore:F0}\n" +
                    $"**Unauth attempts:** {rec.UnauthAttempts}",
                    DiscordColor.DarkRed);
                await SendDiscordBuilderSafeAsync(builder);
            }
            catch (Exception ex) { await ServiceLogError(ex, "OmniDefence SendBlockNotificationAsync failed."); }
        }

        private async Task SendDiscordBuilderSafeAsync(DiscordMessageBuilder builder)
        {
            try
            {
                await ExecuteServiceMethod<KliveBotDiscord>("SendMessageToKlives", builder);
            }
            catch { }
        }

        // ------------------------------------------------------------------
        //  Honeypot helpers (used by KliveAPI integration to pick a response)
        // ------------------------------------------------------------------

        public string GenerateHoneypotResponse(string ip)
        {
            return JunkPayloadGenerator.GenerateJunkJson(ip ?? "unknown");
        }

        public Task RegisterHoneypotRouteAsync(string route, string? note)
        {
            honeypotRoutes[route] = "JunkJson";
            return store.UpsertHoneypotRouteAsync(new HoneypotRouteRow
            {
                Route = route,
                CreatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ResponseKind = "JunkJson",
                Note = note
            });
        }

        public Task RemoveHoneypotRouteAsync(string route)
        {
            honeypotRoutes.TryRemove(route, out _);
            return store.DeleteHoneypotRouteAsync(route);
        }

        public IReadOnlyDictionary<string, string> HoneypotRouteSnapshot() =>
            new Dictionary<string, string>(honeypotRoutes, StringComparer.OrdinalIgnoreCase);

        // ------------------------------------------------------------------
        //  Blocked region helpers
        // ------------------------------------------------------------------

        public IReadOnlyCollection<BlockedRegionRow> BlockedRegionsSnapshot() => blockedRegionSnapshot.ToList();

        public void RegisterBlockedRegion(BlockedRegionRow row)
        {
            if (row == null || row.Id <= 0) return;
            blockedRegions[row.Id] = row;
            RefreshBlockedRegionSnapshot();
        }

        public void UnregisterBlockedRegion(long id)
        {
            blockedRegions.TryRemove(id, out _);
            RefreshBlockedRegionSnapshot();
        }

        private void RefreshBlockedRegionSnapshot()
        {
            blockedRegionSnapshot = blockedRegions.Values.ToArray();
        }

        public bool IsLatLonInBlockedRegion(double latitude, double longitude, out BlockedRegionRow? region)
        {
            foreach (var r in blockedRegionSnapshot)
            {
                if (latitude >= r.LatMin && latitude <= r.LatMax && longitude >= r.LonMin && longitude <= r.LonMax)
                {
                    region = r;
                    return true;
                }
            }
            region = null;
            return false;
        }

        private static bool IsLatLonInRegion(double latitude, double longitude, BlockedRegionRow region)
        {
            return latitude >= region.LatMin && latitude <= region.LatMax && longitude >= region.LonMin && longitude <= region.LonMax;
        }

        private static string BuildRegionBlockReason(BlockedRegionRow region)
        {
            return $"Region block #{region.Id}: {(string.IsNullOrWhiteSpace(region.Reason) ? "Region block" : region.Reason)}";
        }

        private bool TryApplyBlockedRegion(IpRecord? rec, out string reason)
        {
            reason = string.Empty;
            if (rec == null || !rec.Latitude.HasValue || !rec.Longitude.HasValue) return false;
            if (IsLinkedToKlives(rec.Ip)) return false;
            if (!IsLatLonInBlockedRegion(rec.Latitude.Value, rec.Longitude.Value, out var region) || region == null) return false;
            return TryApplyBlockedRegion(rec, region, out reason);
        }

        private bool TryApplyBlockedRegion(IpRecord rec, BlockedRegionRow region, out string reason)
        {
            reason = string.Empty;
            if (rec == null || !rec.Latitude.HasValue || !rec.Longitude.HasValue) return false;
            if (IsLinkedToKlives(rec.Ip)) return false;
            if (!IsLatLonInRegion(rec.Latitude.Value, rec.Longitude.Value, region)) return false;
            reason = BuildRegionBlockReason(region);
            lock (rec)
            {
                if (string.Equals(rec.Status, nameof(IpThreatTracker.IpStatus.Blocked), StringComparison.OrdinalIgnoreCase)) return false;
                rec.Status = nameof(IpThreatTracker.IpStatus.Blocked);
                rec.LastBlockReason = reason;
            }
            return true;
        }

        private async Task PersistRegionBlockAsync(IpRecord rec, string reason, string? actorProfileId = null, string? actorProfileName = null)
        {
            try
            {
                await tracker.PersistAsync(rec);
                await RecordIpEventAsync(new IpEventRow
                {
                    UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Ip = rec.Ip,
                    Kind = "RegionBlock",
                    ActorProfileId = actorProfileId,
                    ActorProfileName = actorProfileName,
                    Detail = reason
                });
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "OmniDefence region block persistence failed.");
            }
        }

        public async Task<int> ApplyBlockedRegionToKnownIpsAsync(BlockedRegionRow region, string? actorProfileId, string? actorProfileName)
        {
            int blocked = 0;
            foreach (var rec in tracker.All())
            {
                if (TryApplyBlockedRegion(rec, region, out string reason))
                {
                    blocked++;
                    await PersistRegionBlockAsync(rec, reason, actorProfileId, actorProfileName);
                }
            }
            return blocked;
        }

        public async Task<int> UnblockIpsInRegionAsync(BlockedRegionRow region, string? actorProfileId, string? actorProfileName)
        {
            int unblocked = 0;
            foreach (var rec in tracker.All())
            {
                if (rec == null || !rec.Latitude.HasValue || !rec.Longitude.HasValue) continue;
                if (!IsLatLonInRegion(rec.Latitude.Value, rec.Longitude.Value, region)) continue;

                bool reset = false;
                lock (rec)
                {
                    if (string.Equals(rec.Status, nameof(IpThreatTracker.IpStatus.Blocked), StringComparison.OrdinalIgnoreCase)
                        && !IsLatLonInBlockedRegion(rec.Latitude.Value, rec.Longitude.Value, out _))
                    {
                        rec.Status = nameof(IpThreatTracker.IpStatus.Normal);
                        rec.LastBlockReason = null;
                        reset = true;
                    }
                }

                if (!reset) continue;
                unblocked++;
                await tracker.PersistAsync(rec);
                await RecordIpEventAsync(new IpEventRow
                {
                    UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Ip = rec.Ip,
                    Kind = "RegionUnblock",
                    ActorProfileId = actorProfileId,
                    ActorProfileName = actorProfileName,
                    Detail = $"Removed region block #{region.Id}"
                });
            }
            return unblocked;
        }

        private async Task EnforceBlockedRegionsAsync(IpRecord rec)
        {
            if (TryApplyBlockedRegion(rec, out string reason))
            {
                await PersistRegionBlockAsync(rec, reason);
            }
        }

        // ------------------------------------------------------------------
        //  Convenience helpers
        // ------------------------------------------------------------------

        public static string ExtractClientIp(HttpListenerRequest req)
        {
            if (req == null) return "";
            try
            {
                string? remote = req.RemoteEndPoint?.Address?.ToString();
                // Honour X-Forwarded-For only when the request hits us locally
                // (i.e. a known reverse proxy on this box). Anything else and we
                // trust the socket peer.
                if (remote == "127.0.0.1" || remote == "::1")
                {
                    string? xff = req.Headers["X-Forwarded-For"];
                    if (!string.IsNullOrWhiteSpace(xff))
                    {
                        var first = xff.Split(',')[0].Trim();
                        if (!string.IsNullOrEmpty(first)) return first;
                    }
                }
                return remote ?? "";
            }
            catch { return ""; }
        }

        public static string HashBody(byte[] bodyBytes)
        {
            if (bodyBytes == null || bodyBytes.Length == 0) return "";
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bodyBytes));
        }

        /// <summary>
        /// Throttles port scans to one per IP per hour.
        /// </summary>
        public bool TryAcquireScanSlot(string ip)
        {
            var now = DateTime.UtcNow;
            if (lastScanByIp.TryGetValue(ip, out var last) && (now - last) < TimeSpan.FromHours(1))
            {
                return false;
            }
            lastScanByIp[ip] = now;
            return true;
        }
    }
}
