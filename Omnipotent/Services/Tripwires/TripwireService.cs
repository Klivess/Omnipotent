using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
        private readonly TripwireGeoResolver geoResolver = new();
        private byte[] visitorKey = Array.Empty<byte>();

        public TripwireStore Store { get; private set; } = null!;

        public TripwireService()
        {
            name = "Tripwires";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            try
            {
                Store = new TripwireStore();
                await Store.InitialiseAsync(cancellationToken.Token);
                ServiceQuitRequest += () => Store.Dispose();
                visitorKey = LoadOrCreateVisitorKey();
                await new TripwireRoutes(this).RegisterRoutes();
                _ = Task.Run(() => CleanupLoopAsync(cancellationToken.Token));
                await ServiceLog($"Tripwires ready. DB={Store.DbPath}; public redirect route=/t.");
            }
            catch (Exception ex) { await ServiceLogError(ex, "Tripwire service startup failed."); }
        }

        internal async Task HandlePublicTripAsync(Services.KliveAPI.KliveAPI.UserRequest req)
        {
            CacheDeps.MarkUncacheable("tripwire redirects must record every request");
            string token = (req.userParameters?.Get("key") ?? "").Trim();
            if (token.Length is < 16 or > 128)
            {
                await ReturnUnavailableAsync(req, HttpStatusCode.NotFound, "Tripwire not found");
                return;
            }

            var resolved = await Store.ResolveTokenAsync(token, cancellationToken.Token);
            if (!resolved.HasValue)
            {
                await ReturnUnavailableAsync(req, HttpStatusCode.NotFound, "Tripwire not found");
                return;
            }

            var (tripwire, target) = resolved.Value;
            var settings = tripwire.Settings;
            if (!settings.Enabled || !target.Enabled)
            {
                await ReturnUnavailableAsync(req, HttpStatusCode.Gone, "This tripwire is disabled");
                return;
            }
            if (settings.ExpiresUtc.HasValue && settings.ExpiresUtc <= DateTimeOffset.UtcNow)
            {
                await ReturnUnavailableAsync(req, HttpStatusCode.Gone, "This tripwire has expired");
                return;
            }
            if (settings.MaxTrips.HasValue && tripwire.TotalTrips >= settings.MaxTrips.Value)
            {
                await ReturnUnavailableAsync(req, HttpStatusCode.Gone, "This tripwire has reached its trip limit");
                return;
            }

            string ua = Limit(req.req.UserAgent, 2048) ?? "";
            bool isBot = TripwireUserAgent.IsBot(ua);
            if (settings.IgnoreBots && isBot)
            {
                await ReturnRedirectAsync(req, target.DestinationUrl, settings.RedirectStatusCode);
                return;
            }

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
                TrippedUtc = DateTimeOffset.UtcNow,
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

            TripwireRecordResult recorded;
            try
            {
                recorded = await Store.RecordEventAsync(tripwire, target, item, settings.DeduplicateWindowMinutes, cancellationToken.Token);
            }
            catch (Exception ex)
            {
                _ = ServiceLogError(ex, $"Failed to persist tripwire hit for {tripwire.Name}.", false);
                recorded = new TripwireRecordResult();
            }

            await ReturnRedirectAsync(req, target.DestinationUrl, settings.RedirectStatusCode);

            if (recorded.Accepted)
            {
                _ = Task.Run(() => EnrichAndNotifyAsync(tripwire, target, item,
                    restrictCollection || !settings.CaptureLocation ? null : ip,
                    headerGeo, cancellationToken.Token));
            }
        }

        private async Task EnrichAndNotifyAsync(
            TripwireRecord tripwire,
            TripwireTarget target,
            TripwireEvent item,
            string? ipForLookup,
            TripwireGeoResult headerGeo,
            CancellationToken ct)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(ipForLookup))
                {
                    var resolved = await geoResolver.ResolveAsync(ipForLookup, headerGeo, ct);
                    if (resolved.HasValue)
                    {
                        item.CountryCode = resolved.CountryCode; item.Country = resolved.Country;
                        item.Region = resolved.Region; item.City = resolved.City;
                        item.Latitude = resolved.Latitude; item.Longitude = resolved.Longitude;
                        item.Timezone = resolved.Timezone;
                        await Store.UpdateGeoAsync(item.Id, resolved, ct);
                    }
                }

                if (!tripwire.Settings.DiscordNotifications) return;
                bool claimed = await Store.TryClaimDiscordNotificationAsync(
                    tripwire.Id, tripwire.Settings.NotificationCooldownSeconds, DateTimeOffset.UtcNow, ct);
                if (!claimed) return;

                try
                {
                    var services = await GetServicesByType<KliveBotDiscord>();
                    if (services == null || services.Length == 0) throw new InvalidOperationException("KliveBot Discord is not running.");
                    var bot = (KliveBotDiscord)services[0];
                    var message = await bot.SendMessageToKlives(BuildDiscordMessage(tripwire, target, item))
                        .WaitAsync(TimeSpan.FromSeconds(15), ct);
                    bool sent = message != null;
                    await Store.MarkNotificationAsync(item.Id, sent, sent ? null : "KliveBot returned no Discord message.", ct);
                }
                catch (Exception ex)
                {
                    await Store.MarkNotificationAsync(item.Id, false, Limit(ex.Message, 500), ct);
                    _ = ServiceLogError(ex, $"Tripwire Discord notification failed for {tripwire.Name}.", false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _ = ServiceLogError(ex, $"Tripwire enrichment failed for {tripwire.Name}.", false); }
        }

        private static DiscordMessageBuilder BuildDiscordMessage(TripwireRecord tripwire, TripwireTarget target, TripwireEvent item)
        {
            var description = new StringBuilder();
            description.Append("**Link:** ").AppendLine(EscapeDiscord(target.Label));
            description.Append("**Time:** ").AppendLine($"<t:{item.TrippedUtc.ToUnixTimeSeconds()}:F>");
            string destination = target.DestinationUrl.Length > 1000 ? target.DestinationUrl[..997] + "…" : target.DestinationUrl;
            description.Append("**Destination:** ").AppendLine($"<{destination}>");
            if (item.DoNotTrack && tripwire.Settings.HonourDoNotTrack) description.AppendLine("**Privacy:** Do Not Track honoured; optional visitor details were not stored.");
            if (tripwire.Settings.DiscordIncludeSensitiveDetails)
            {
                if (!string.IsNullOrWhiteSpace(item.IpAddress)) description.Append("**IP:** `").Append(EscapeDiscord(item.IpAddress)).AppendLine("`");
                string location = string.Join(", ", new[] { item.City, item.Region, item.Country ?? item.CountryCode }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!string.IsNullOrWhiteSpace(location)) description.Append("**Location:** ").AppendLine(EscapeDiscord(location));
                if (!string.IsNullOrWhiteSpace(item.DeviceType)) description.Append("**Device:** ").Append(EscapeDiscord(item.DeviceType))
                    .Append(" · ").Append(EscapeDiscord(item.OperatingSystem)).Append(" · ").AppendLine(EscapeDiscord(item.Browser));
                if (!string.IsNullOrWhiteSpace(item.Referrer)) description.Append("**Referrer:** ").AppendLine(EscapeDiscord(Limit(item.Referrer, 1000)));
            }
            description.Append("**Visit:** ").Append(item.IsUnique ? "Unique" : "Repeat");
            string body = description.ToString();
            if (body.Length > 3900) body = body[..3897] + "…";
            return KliveBotDiscord.MakeSimpleEmbed($"⚡ Tripwire tripped: {EscapeDiscord(tripwire.Name)}", body, new DiscordColor(0xF5A623));
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

        private static async Task ReturnUnavailableAsync(Services.KliveAPI.KliveAPI.UserRequest req, HttpStatusCode status, string message)
        {
            var headers = new NameValueCollection { { "Cache-Control", "no-store" }, { "X-Robots-Tag", "noindex, nofollow" } };
            string safe = WebUtility.HtmlEncode(message);
            await req.ReturnResponse($"<!doctype html><meta charset=\"utf-8\"><title>{safe}</title><h1>{safe}</h1>",
                "text/html; charset=utf-8", headers, status);
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
                try { await Store.CleanupExpiredEventsAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _ = ServiceLogError(ex, "Tripwire retention cleanup failed.", false); }
                try { await Task.Delay(TimeSpan.FromHours(6), ct); }
                catch (OperationCanceledException) { break; }
            }
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

        private static string EscapeDiscord(string? value) => (value ?? "").Replace("`", "'", StringComparison.Ordinal).Replace("@", "@\u200b", StringComparison.Ordinal);
        private static string? Limit(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
    }
}
