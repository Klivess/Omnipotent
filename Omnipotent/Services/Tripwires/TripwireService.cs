using System.Collections.Specialized;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using DSharpPlus.Entities;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.KliveBot_Discord;
using OmniDefenceService = Omnipotent.Services.OmniDefence.OmniDefence;

namespace Omnipotent.Services.Tripwires
{
    public sealed class TripwireService : OmniService
    {
        private readonly TripwireGeoResolver geoResolver;
        private readonly ConcurrentDictionary<string, byte> dirtySummaries = new(StringComparer.Ordinal);
        // One writer behind the redirect: the visitor is answered from memory, the database catches up here.
        private readonly Channel<PendingWrite> writes = Channel.CreateBounded<PendingWrite>(
            new BoundedChannelOptions(4_096) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        private int queued;
        private int overviewDirty = 1;
        private volatile TripwireOverviewModel? overview;
        private volatile OmniDefenceService? defence;
        private byte[] visitorKey = Array.Empty<byte>();

        public TripwireStore Store { get; private set; } = null!;
        public TripwireLinkIndex Links { get; } = new();
        public TripwireHealth Health { get; } = new();

        public TripwireService()
        {
            name = "Tripwires";
            threadAnteriority = ThreadAnteriority.Standard;
            geoResolver = new TripwireGeoResolver(Health);
        }

        protected override async void ServiceMain()
        {
            try
            {
                Store = new TripwireStore();
                await Store.InitialiseAsync(cancellationToken.Token);
                ServiceQuitRequest += () => { writes.Writer.TryComplete(); Store.Dispose(); };
                visitorKey = LoadOrCreateVisitorKey();
                foreach (string id in await Store.ListTripwireIdsAsync(cancellationToken.Token))
                {
                    try { await Store.RefreshSummarySnapshotAsync(id, cancellationToken.Token); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        MarkSummaryDirty(id);
                        _ = ServiceLogError(ex, $"Initial tripwire summary refresh failed for {id}.", false);
                    }
                }
                Links.Load(await Store.ListAsync(cancellationToken.Token));
                _ = Task.Run(() => WriterLoopAsync(cancellationToken.Token));
                await new TripwireRoutes(this).RegisterRoutes();
                _ = Task.Run(() => CleanupLoopAsync(cancellationToken.Token));
                _ = Task.Run(() => SummaryRefreshLoopAsync(cancellationToken.Token));
                await ResolveDefenceAsync();
                await ServiceLog($"Tripwires ready. DB={Store.DbPath}; {Links.Count} live links; public redirect route=/t.");
            }
            catch (Exception ex) { await ServiceLogError(ex, "Tripwire service startup failed."); }
        }

        private sealed record PendingWrite(
            TripwireCount Kind, TripwireRecord Tripwire, TripwireTarget Target, DateTimeOffset At,
            TripwireEvent? Event = null, string? IpForGeo = null, string? IpForNetwork = null,
            TripwireGeoResult? HeaderGeo = null, string? Agent = null);

        internal async Task HandlePublicTripAsync(Services.KliveAPI.KliveAPI.UserRequest req)
        {
            CacheDeps.MarkUncacheable("tripwire redirects must record every request");
            long started = Stopwatch.GetTimestamp();
            string token = (req.userParameters?.Get("key") ?? "").Trim();
            TripwireLink? link = null;
            if (token.Length is >= 16 and <= 128)
            {
                link = Links.Find(token);
                if (link == null && !Links.Loaded)
                {
                    var resolved = await Store.ResolveTokenAsync(token, cancellationToken.Token);
                    if (resolved.HasValue) link = new TripwireLink(resolved.Value.Tripwire, resolved.Value.Target);
                }
            }

            string ua = Limit(req.req.UserAgent, 2048) ?? "";
            bool isBot = TripwireUserAgent.IsBot(ua);
            var now = DateTimeOffset.UtcNow;
            var verdict = TripwireGate.Decide(link, isBot, now, link == null ? 0 : Links.Trips(link.Tripwire.Id));
            if (!verdict.Found)
            {
                Health.NotFound();
                await ReturnPageAsync(req, HttpStatusCode.NotFound, TripwirePages.NotFound());
                Health.Answered(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return;
            }

            var (tripwire, target) = link!;
            var settings = tripwire.Settings;
            // Two visitors racing for the last trip: the one that loses is a visit to a closed tripwire.
            if (verdict.Count == TripwireCount.Trip && !Links.TryReserve(tripwire))
                verdict = verdict with { ClosedReason = "limit", Count = TripwireCount.Closed };

            switch (verdict.Count)
            {
                case TripwireCount.Preview:
                    Health.Preview();
                    Enqueue(new PendingWrite(TripwireCount.Preview, tripwire, target, now, Agent: TripwireGate.PreviewAgent(ua)));
                    break;
                case TripwireCount.Closed:
                    Health.ClosedVisit();
                    Enqueue(new PendingWrite(TripwireCount.Closed, tripwire, target, now));
                    break;
                case TripwireCount.Trip:
                    var write = BuildTrip(req, tripwire, target, ua, isBot, now);
                    if (!Enqueue(write)) Links.Release(tripwire.Id);
                    break;
            }

            if (verdict.Closed && settings.ClosedBehaviour != TripwireSettings.ClosedRedirect)
                await ReturnPageAsync(req, HttpStatusCode.Gone, TripwirePages.Unavailable());
            else
                await ReturnRedirectAsync(req, target.DestinationUrl, settings.RedirectStatusCode);
            Health.Answered(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        private PendingWrite BuildTrip(Services.KliveAPI.KliveAPI.UserRequest req, TripwireRecord tripwire, TripwireTarget target,
            string ua, bool isBot, DateTimeOffset now)
        {
            var settings = tripwire.Settings;
            bool dnt = IsDoNotTrack(req.req.Headers);
            bool restrictCollection = settings.HonourDoNotTrack && dnt;
            string? ip = OmniDefenceService.ExtractClientIp(req.req);
            var device = TripwireUserAgent.Parse(ua);
            var headerGeo = !restrictCollection && settings.CaptureLocation
                ? geoResolver.FromHeaders(req.req.Headers)
                : new TripwireGeoResult();
            var item = new TripwireEvent
            {
                Id = Guid.NewGuid().ToString("N"),
                TripwireId = tripwire.Id,
                TargetId = target.Id,
                TargetLabel = target.Label,
                DestinationUrl = target.DestinationUrl,
                TrippedUtc = now,
                DoNotTrack = dnt,
                IsBot = isBot,
                VisitorHash = restrictCollection ? null : VisitorHash(ip, ua),
                IpAddress = !restrictCollection && settings.CaptureIpAddress ? Limit(ip, 64) : null,
                CountryCode = headerGeo.CountryCode,
                Country = headerGeo.Country,
                Region = headerGeo.Region,
                City = headerGeo.City,
                Latitude = headerGeo.Latitude,
                Longitude = headerGeo.Longitude,
                Timezone = headerGeo.Timezone,
                UserAgent = !restrictCollection && settings.CaptureUserAgent ? Limit(ua, 2048) : null,
                Browser = !restrictCollection && settings.CaptureDeviceDetails ? device.Browser : null,
                OperatingSystem = !restrictCollection && settings.CaptureDeviceDetails ? device.OperatingSystem : null,
                DeviceType = !restrictCollection && settings.CaptureDeviceDetails ? device.DeviceType : null,
                Referrer = !restrictCollection && settings.CaptureReferrer ? Limit(req.req.UrlReferrer?.ToString(), 2048) : null,
                Language = !restrictCollection && settings.CaptureLanguage ? Limit(req.req.Headers["Accept-Language"], 256) : null,
                QueryParametersJson = !restrictCollection && settings.CaptureQueryParameters ? CaptureQuery(req.userParameters) : null,
            };
            return new PendingWrite(TripwireCount.Trip, tripwire, target, now, item,
                IpForGeo: restrictCollection || !settings.CaptureLocation ? null : ip,
                IpForNetwork: restrictCollection ? null : ip,
                HeaderGeo: headerGeo);
        }

        private bool Enqueue(PendingWrite write)
        {
            if (writes.Writer.TryWrite(write))
            {
                Interlocked.Increment(ref queued);
                return true;
            }
            Health.Dropped();
            _ = ServiceLogError(new InvalidOperationException("Tripwire write queue is full."),
                $"A {write.Kind} for {write.Tripwire.Name} was not recorded: {writes.Reader.Count} writes are waiting.", false);
            return false;
        }

        private async Task WriterLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var write in writes.Reader.ReadAllAsync(ct))
                {
                    Interlocked.Decrement(ref queued);
                    try { await ApplyAsync(write, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        if (write.Kind == TripwireCount.Trip) Links.Release(write.Tripwire.Id);
                        Health.RecordFailed(Limit(ex.Message, 300) ?? ex.GetType().Name);
                        _ = ServiceLogError(ex, $"Failed to persist a tripwire {write.Kind} for {write.Tripwire.Name}.", false);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }

        private async Task ApplyAsync(PendingWrite write, CancellationToken ct)
        {
            string id = write.Tripwire.Id;
            switch (write.Kind)
            {
                case TripwireCount.Trip:
                    var recorded = await Store.RecordEventAsync(write.Tripwire, write.Target, write.Event!,
                        write.Tripwire.Settings.DeduplicateWindowMinutes, ct);
                    if (!recorded.Accepted)
                    {
                        Links.Release(id);
                        if (recorded.LimitReached)
                        {
                            Health.ClosedVisit();
                            await Store.RecordClosedVisitAsync(id, write.At, ct);
                            MarkSummaryDirty(id);
                        }
                        return;
                    }
                    Health.Recorded();
                    MarkSummaryDirty(id);
                    _ = Task.Run(() => EnrichAndNotifyAsync(write.Tripwire, write.Target, write.Event!,
                        write.IpForGeo, write.IpForNetwork, write.HeaderGeo ?? new TripwireGeoResult(), ct));
                    break;
                case TripwireCount.Closed:
                    await Store.RecordClosedVisitAsync(id, write.At, ct);
                    MarkSummaryDirty(id);
                    break;
                case TripwireCount.Preview:
                    await Store.RecordPreviewAsync(id, write.At, write.Agent, ct);
                    MarkSummaryDirty(id);
                    break;
            }
        }

        internal int QueuedWrites => Math.Max(0, Volatile.Read(ref queued));

        private async Task EnrichAndNotifyAsync(
            TripwireRecord tripwire,
            TripwireTarget target,
            TripwireEvent item,
            string? ipForGeo,
            string? ipForNetwork,
            TripwireGeoResult headerGeo,
            CancellationToken ct)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(ipForGeo))
                {
                    var resolved = await geoResolver.ResolveAsync(ipForGeo, headerGeo, ct);
                    if (resolved.HasValue)
                    {
                        item.CountryCode = resolved.CountryCode; item.Country = resolved.Country;
                        item.Region = resolved.Region; item.City = resolved.City;
                        item.Latitude = resolved.Latitude; item.Longitude = resolved.Longitude;
                        item.Timezone = resolved.Timezone;
                        await Store.UpdateGeoAsync(item.Id, resolved, ct);
                        MarkSummaryDirty(tripwire.Id);
                    }
                }

                if (!tripwire.Settings.DiscordNotifications) return;
                var network = await WaitForNetworkAsync(ipForNetwork, ct);
                if (tripwire.Settings.DiscordSkipKnownProfiles && !string.IsNullOrWhiteSpace(network?.Profile))
                {
                    Health.DiscordSkipped();
                    return;
                }
                bool claimed = await Store.TryClaimDiscordNotificationAsync(
                    tripwire.Id, tripwire.Settings.NotificationCooldownSeconds, DateTimeOffset.UtcNow, ct);
                if (!claimed) return;

                try
                {
                    var services = await GetServicesByType<KliveBotDiscord>();
                    if (services == null || services.Length == 0) throw new InvalidOperationException("KliveBot Discord is not running.");
                    var bot = (KliveBotDiscord)services[0];
                    var message = await bot.SendMessageToKlives(BuildDiscordMessage(tripwire, target, item, network))
                        .WaitAsync(TimeSpan.FromSeconds(15), ct);
                    bool sent = message != null;
                    if (sent) Health.DiscordSent(); else Health.DiscordFailed("KliveBot returned no Discord message.");
                    await Store.MarkNotificationAsync(item.Id, sent, sent ? null : "KliveBot returned no Discord message.", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Health.DiscordFailed(Limit(ex.Message, 300) ?? ex.GetType().Name);
                    await Store.MarkNotificationAsync(item.Id, false, Limit(ex.Message, 500), ct);
                    _ = ServiceLogError(ex, $"Tripwire Discord notification failed for {tripwire.Name}.", false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _ = ServiceLogError(ex, $"Tripwire enrichment failed for {tripwire.Name}.", false); }
        }

        /// <summary>
        /// OmniDefence looks a new address up on the same request; give it a moment so the Discord message can say
        /// whether the visitor is on a VPN, a phone network or an address that has signed in to KM.
        /// </summary>
        private async Task<TripwireNetwork?> WaitForNetworkAsync(string? ip, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(ip)) return null;
            TripwireNetwork? network = LookupNetwork(ip);
            for (int i = 0; i < 4 && network != null && network.Hosting == null && network.Isp == null; i++)
            {
                await Task.Delay(500, ct);
                network = LookupNetwork(ip);
            }
            return network;
        }

        /// <summary>What OmniDefence's in-memory address book says about an IP; null when it has no record yet.</summary>
        internal TripwireNetwork? LookupNetwork(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return null;
            var records = defence?.Tracker;
            var rec = records?.Get(ip);
            if (rec == null) return null;
            lock (rec)
            {
                return new TripwireNetwork
                {
                    Isp = rec.Isp, Org = rec.Org, Asn = rec.AsName ?? rec.Asn,
                    Proxy = rec.IsProxy, Hosting = rec.IsHosting, Mobile = rec.IsMobile,
                    Classification = rec.Classification, Tags = rec.ClassTags,
                    Profile = rec.AssociatedProfileName, ProfileRank = rec.AssociatedProfileRank,
                    ProfileSeenUtc = rec.AssociatedProfileLastSeenUtc is long seen ? DateTimeOffset.FromUnixTimeSeconds(seen) : null,
                    DefenceStatus = rec.Status,
                };
            }
        }

        internal bool NetworkAvailable => defence?.Tracker != null;

        private async Task ResolveDefenceAsync()
        {
            if (defence != null) return;
            try { defence = (await GetServicesByType<OmniDefenceService>())?.FirstOrDefault() as OmniDefenceService; }
            catch { defence = null; }
        }

        internal async Task<bool> DiscordAvailableAsync()
        {
            try { return (await GetServicesByType<KliveBotDiscord>())?.Length > 0; }
            catch { return false; }
        }

        internal static DiscordMessageBuilder BuildDiscordMessage(TripwireRecord tripwire, TripwireTarget target, TripwireEvent item, TripwireNetwork? network)
        {
            var description = new StringBuilder();
            description.Append("**Link:** ").AppendLine(EscapeDiscord(target.Label));
            description.Append("**Time:** ").AppendLine($"<t:{item.TrippedUtc.ToUnixTimeSeconds()}:F>");
            string destination = target.DestinationUrl.Length > 1000 ? target.DestinationUrl[..997] + "…" : target.DestinationUrl;
            description.Append("**Destination:** ").AppendLine($"<{destination}>");
            if (!string.IsNullOrWhiteSpace(network?.Profile))
                description.Append("**Known address:** signed in to KM as ").AppendLine(EscapeDiscord(network!.Profile));
            if (item.DoNotTrack && tripwire.Settings.HonourDoNotTrack) description.AppendLine("**Privacy:** Do Not Track honoured; optional visitor details were not stored.");
            if (tripwire.Settings.DiscordIncludeSensitiveDetails)
            {
                if (!string.IsNullOrWhiteSpace(item.IpAddress)) description.Append("**IP:** `").Append(EscapeDiscord(item.IpAddress)).AppendLine("`");
                string location = string.Join(", ", new[] { item.City, item.Region, item.Country ?? item.CountryCode }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!string.IsNullOrWhiteSpace(location))
                {
                    description.Append("**Location:** ").Append(EscapeDiscord(location));
                    if (network?.Proxy == true) description.Append(" (VPN or proxy exit, not the person)");
                    description.AppendLine();
                }
                string? kind = NetworkKind(network);
                string? carrier = network?.Isp ?? network?.Org;
                if (kind != null || !string.IsNullOrWhiteSpace(carrier))
                    description.Append("**Network:** ").AppendLine(EscapeDiscord(string.Join(" · ", new[] { carrier, kind }.Where(x => !string.IsNullOrWhiteSpace(x)))));
                if (!string.IsNullOrWhiteSpace(item.DeviceType)) description.Append("**Device:** ").Append(EscapeDiscord(item.DeviceType))
                    .Append(" · ").Append(EscapeDiscord(item.OperatingSystem)).Append(" · ").AppendLine(EscapeDiscord(item.Browser));
                if (!string.IsNullOrWhiteSpace(item.Referrer)) description.Append("**Referrer:** ").AppendLine(EscapeDiscord(Limit(item.Referrer, 1000)));
            }
            description.Append("**Visit:** ").Append(item.IsUnique ? "Unique" : "Repeat");
            string body = description.ToString();
            if (body.Length > 3900) body = body[..3897] + "…";
            return KliveBotDiscord.MakeSimpleEmbed($"⚡ Tripwire tripped: {EscapeDiscord(tripwire.Name)}", body, new DiscordColor(0xF5A623));
        }

        internal static string? NetworkKind(TripwireNetwork? network)
        {
            if (network == null) return null;
            if (network.Proxy == true) return "VPN or proxy";
            if (network.Hosting == true) return "datacenter";
            if (network.Mobile == true) return "mobile network";
            if (network.Hosting == false) return "home or office";
            return null;
        }

        private string? VisitorHash(string? ip, string? ua)
        {
            if (string.IsNullOrWhiteSpace(ip) && string.IsNullOrWhiteSpace(ua)) return null;
            using var hmac = new HMACSHA256(visitorKey);
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ip}\n{ua}"));
            return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
        }

        private static string? CaptureQuery(NameValueCollection? query)
        {
            if (query == null || query.Count == 0) return null;
            var values = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string? key in query.AllKeys)
            {
                if (string.IsNullOrWhiteSpace(key) || key.Equals("key", StringComparison.OrdinalIgnoreCase)) continue;
                if (values.Count >= 50) break;
                values[Limit(key, 128)!] = (query.GetValues(key) ?? Array.Empty<string>())
                    .Take(20).Select(value => Limit(value, 1024) ?? "").ToArray();
            }
            return values.Count == 0 ? null : JsonConvert.SerializeObject(values);
        }

        private static bool IsDoNotTrack(NameValueCollection headers)
            => headers["DNT"] == "1" || headers["Sec-GPC"] == "1";

        private static async Task ReturnRedirectAsync(Services.KliveAPI.KliveAPI.UserRequest req, string destination, int status)
        {
            var headers = new NameValueCollection
            {
                { "Location", destination }, { "Cache-Control", "no-store, no-cache, max-age=0" },
                { "Pragma", "no-cache" }, { "Referrer-Policy", "no-referrer" }, { "X-Robots-Tag", "noindex, nofollow" },
            };
            string safe = WebUtility.HtmlEncode(destination);
            string html = $"<!doctype html><meta charset=\"utf-8\"><meta name=\"robots\" content=\"noindex,nofollow\"><title>Redirecting…</title><p>Redirecting to <a href=\"{safe}\">{safe}</a>…</p>";
            await req.ReturnResponse(html, "text/html; charset=utf-8", headers, (HttpStatusCode)status);
        }

        private static async Task ReturnPageAsync(Services.KliveAPI.KliveAPI.UserRequest req, HttpStatusCode status, string html)
        {
            var headers = new NameValueCollection
            {
                { "Cache-Control", "no-store" }, { "X-Robots-Tag", "noindex, nofollow" }, { "Referrer-Policy", "no-referrer" },
            };
            await req.ReturnResponse(html, "text/html; charset=utf-8", headers, status);
        }

        private byte[] LoadOrCreateVisitorKey()
        {
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.TripwireVisitorKeyFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                try
                {
                    byte[] existing = Convert.FromBase64String(File.ReadAllText(path).Trim());
                    if (existing.Length >= 32) return existing;
                }
                catch { }
            }
            byte[] key = RandomNumberGenerator.GetBytes(32);
            string temp = path + ".tmp";
            File.WriteAllText(temp, Convert.ToBase64String(key));
            File.Move(temp, path, true);
            return key;
        }

        private async Task CleanupLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Store.CleanupExpiredEventsAsync(ct);
                    foreach (string id in await Store.ListTripwireIdsAsync(ct)) MarkSummaryDirty(id);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _ = ServiceLogError(ex, "Tripwire retention cleanup failed.", false); }
                try { await Task.Delay(TimeSpan.FromHours(6), ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        internal void MarkSummaryDirty(string tripwireId)
        {
            dirtySummaries[tripwireId] = 0;
            Interlocked.Exchange(ref overviewDirty, 1);
        }

        /// <summary>
        /// After a change from the page: the change's summary, the link index and the overview are rebuilt before
        /// the route answers, so the next read already shows it and the redirect already obeys it.
        /// </summary>
        internal async Task AfterChangeAsync(string? tripwireId, CancellationToken ct = default)
        {
            if (!string.IsNullOrWhiteSpace(tripwireId) && await Store.ExistsAsync(tripwireId, ct))
            {
                try { await Store.RefreshSummarySnapshotAsync(tripwireId, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    MarkSummaryDirty(tripwireId);
                    _ = ServiceLogError(ex, $"Tripwire summary refresh failed for {tripwireId}.", false);
                }
            }
            Links.Load(await Store.ListAsync(ct));
            Interlocked.Exchange(ref overviewDirty, 1);
            await RebuildOverviewAsync(ct);
        }

        internal async Task<TripwireOverviewModel> GetOverviewAsync(CancellationToken ct = default)
            => overview ?? await RebuildOverviewAsync(ct);

        private async Task<TripwireOverviewModel> RebuildOverviewAsync(CancellationToken ct)
        {
            Interlocked.Exchange(ref overviewDirty, 0);
            var tripwires = await Store.ListAsync(ct);
            var summaries = new Dictionary<string, TripwireSummary>(StringComparer.Ordinal);
            foreach (var tripwire in tripwires)
            {
                var summary = await Store.GetSummarySnapshotAsync(tripwire.Id, ct) ?? await Store.GetSummaryAsync(tripwire.Id, ct);
                summaries[tripwire.Id] = summary;
            }
            var model = new TripwireOverviewModel
            {
                GeneratedUtc = DateTimeOffset.UtcNow,
                Tripwires = tripwires,
                Summaries = summaries,
                Recent = await Store.GetRecentEventsAsync(TripwireOverviewModel.RecentLimit, ct),
                IpCounts = await Store.GetIpCountsAsync(ct),
                DiscordAvailable = await DiscordAvailableAsync(),
            };
            overview = model;
            return model;
        }

        private async Task SummaryRefreshLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            DateTimeOffset nextFullRefresh = DateTimeOffset.UtcNow.AddMinutes(1);
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    try
                    {
                        bool full = DateTimeOffset.UtcNow >= nextFullRefresh;
                        if (full) nextFullRefresh = DateTimeOffset.UtcNow.AddMinutes(1);
                        IEnumerable<string> ids = full
                            ? await Store.ListTripwireIdsAsync(ct)
                            : dirtySummaries.Keys.ToArray();
                        foreach (string id in ids)
                        {
                            // Remove before computing so a hit during the refresh remains queued.
                            dirtySummaries.TryRemove(id, out _);
                            try { await Store.RefreshSummarySnapshotAsync(id, ct); }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                            catch (Exception ex)
                            {
                                MarkSummaryDirty(id);
                                _ = ServiceLogError(ex, $"Tripwire summary refresh failed for {id}.", false);
                            }
                        }
                        if (full)
                        {
                            // Expiry and limits are read from the index, and settings may have been changed elsewhere.
                            Links.Load(await Store.ListAsync(ct));
                            await ResolveDefenceAsync();
                        }
                        if (full || Volatile.Read(ref overviewDirty) == 1) await RebuildOverviewAsync(ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception ex) { _ = ServiceLogError(ex, "Tripwire summary scheduler failed.", false); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }

        internal static bool TryNormalizeDestination(string? value, out string destination)
        {
            destination = (value ?? "").Trim();
            if (destination.Length is < 8 or > 4096 || !Uri.TryCreate(destination, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            destination = uri.AbsoluteUri;
            return true;
        }

        private static string EscapeDiscord(string? value) => (value ?? "").Replace("`", "'", StringComparison.Ordinal).Replace("@", "@​", StringComparison.Ordinal);
        private static string? Limit(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
    }

    /// <summary>Everything the page's first screen needs, rebuilt in the background and answered from memory.</summary>
    internal sealed class TripwireOverviewModel
    {
        public const int RecentLimit = 60;
        public DateTimeOffset GeneratedUtc { get; init; }
        public List<TripwireRecord> Tripwires { get; init; } = new();
        public Dictionary<string, TripwireSummary> Summaries { get; init; } = new(StringComparer.Ordinal);
        public List<TripwireEvent> Recent { get; init; } = new();
        public List<TripwireIpCount> IpCounts { get; init; } = new();
        public bool DiscordAvailable { get; init; }
    }
}
