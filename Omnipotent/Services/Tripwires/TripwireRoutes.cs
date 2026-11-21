using System.Net;
using DSharpPlus.Entities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.KliveAPI.Caching;
using static Omnipotent.Profiles.KMProfileManager;
using Omnipotent.Profiles.Permissions;

namespace Omnipotent.Services.Tripwires
{
    internal sealed class TripwireRoutes
    {
        private const long MaxBodyBytes = 256 * 1024;
        private readonly TripwireService service;
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Include,
        };
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(JsonSettings);

        public TripwireRoutes(TripwireService service) => this.service = service;

        public async Task RegisterRoutes()
        {
            await service.CreateAPIRoute("/t", service.HandlePublicTripAsync, HttpMethod.Get, Perms.Public);

            await service.CreateAPIRoute("/tripwires/list", async req =>
            {
                try
                {
                    CacheDeps.MarkUncacheable("live tripwire management data");
                    var items = await service.Store.ListAsync();
                    await req.ReturnResponse(Json(items.Select(Present)));
                }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Get, TripwirePerms.WiresRead);

            // The page's first screen in one answer: every tripwire with its figures, the newest trips across all of
            // them, and how the redirect, the database, geolocation and Discord are doing. Built in the background.
            await service.CreateAPIRoute("/tripwires/overview", async req =>
            {
                try
                {
                    CacheDeps.MarkUncacheable("live tripwire overview");
                    var model = await service.GetOverviewAsync();
                    bool intel = req.Can(OmniDefencePerms.OverviewView);
                    await req.ReturnResponse(Json(PresentOverview(model, intel)));
                }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Get, TripwirePerms.WiresRead);

            await service.CreateAPIRoute("/tripwires/get", async req =>
            {
                try
                {
                    CacheDeps.MarkUncacheable("live tripwire management data");
                    var item = await RequireTripwire(req);
                    if (item != null) await req.ReturnResponse(Json(Present(item)));
                }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Get, TripwirePerms.WiresRead);

            await service.CreateBufferedAPIRoute("/tripwires/create", async req =>
            {
                try
                {
                    var body = ParseBody(req.userMessageContent);
                    string name = RequireName((string?)body["name"]);
                    var targets = ParseTargets(body["targets"], requireAtLeastOne: true);
                    var settings = ParseSettings(body["settings"], new TripwireSettings());
                    string createdBy = req.user?.Name ?? "Klives";
                    var created = await service.Store.CreateAsync(name, createdBy, settings, targets);
                    await service.AfterChangeAsync(created.Id);
                    await req.ReturnResponse(Json(Present(created)), code: HttpStatusCode.Created);
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (JsonException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Post, TripwirePerms.WiresManage, MaxBodyBytes);

            await service.CreateBufferedAPIRoute("/tripwires/update", async req =>
            {
                try
                {
                    var body = ParseBody(req.userMessageContent);
                    string id = RequireId((string?)body["id"]);
                    var existing = await service.Store.GetAsync(id);
                    if (existing == null) { await req.ReturnResponse("Tripwire not found.", code: HttpStatusCode.NotFound); return; }
                    string name = body["name"] == null ? existing.Name : RequireName((string?)body["name"]);
                    var settings = ParseSettings(body["settings"], existing.Settings);
                    List<TripwireTarget>? targets = body["targets"] == null ? null : ParseTargets(body["targets"], requireAtLeastOne: true);
                    var updated = await service.Store.UpdateAsync(id, name, settings, targets);
                    if (updated == null) { await req.ReturnResponse("Tripwire not found.", code: HttpStatusCode.NotFound); return; }
                    await service.AfterChangeAsync(id);
                    await req.ReturnResponse(Json(Present(updated)));
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (JsonException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Post, TripwirePerms.WiresManage, MaxBodyBytes);

            await service.CreateBufferedAPIRoute("/tripwires/delete", async req =>
            {
                try
                {
                    string id = RequireId((string?)ParseBody(req.userMessageContent)["id"]);
                    bool deleted = await service.Store.DeleteAsync(id);
                    if (deleted) await service.AfterChangeAsync(null);
                    await req.ReturnResponse(Json(new { deleted }), code: deleted ? HttpStatusCode.OK : HttpStatusCode.NotFound);
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (JsonException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Post, TripwirePerms.WiresManage, MaxBodyBytes);

            await service.CreateAPIRoute("/tripwires/events", async req =>
            {
                try
                {
                    CacheDeps.MarkUncacheable("live tripwire event data");
                    string id = RequireId(req.userParameters?.Get("id"));
                    if (!await service.Store.ExistsAsync(id)) { await req.ReturnResponse("Tripwire not found.", code: HttpStatusCode.NotFound); return; }
                    string? target = Clean(req.userParameters?.Get("targetId"), 64);
                    string? visitor = Clean(req.userParameters?.Get("visitor"), 64);
                    int limit = Int(req.userParameters?.Get("limit"), 100);
                    int offset = Int(req.userParameters?.Get("offset"), 0);
                    var page = await service.Store.GetEventsAsync(id, target, limit, offset, default, visitor);
                    bool intel = req.Can(OmniDefencePerms.OverviewView);
                    await req.ReturnResponse(Json(new
                    {
                        items = page.Items.Select(item => PresentEvent(item, intel)),
                        page.Total, page.Limit, page.Offset,
                    }));
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Get, TripwirePerms.WiresRead);

            // The people behind a tripwire's trips: one row per visitor id, and the other tripwires they tripped.
            await service.CreateAPIRoute("/tripwires/visitors", async req =>
            {
                try
                {
                    CacheDeps.MarkUncacheable("live tripwire visitor data");
                    string id = RequireId(req.userParameters?.Get("id"));
                    if (!await service.Store.ExistsAsync(id)) { await req.ReturnResponse("Tripwire not found.", code: HttpStatusCode.NotFound); return; }
                    int limit = Int(req.userParameters?.Get("limit"), 200);
                    var visitors = await service.Store.GetVisitorsAsync(id, limit);
                    bool intel = req.Can(OmniDefencePerms.OverviewView);
                    await req.ReturnResponse(Json(new
                    {
                        items = visitors.Select(v => new
                        {
                            v.VisitorHash, v.Trips, v.FirstUtc, v.LastUtc, v.Also,
                            latest = PresentEvent(v.Latest, intel),
                        }),
                        total = visitors.Count,
                    }));
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Get, TripwirePerms.WiresRead);

            await service.CreateAPIRoute("/tripwires/summary", async req =>
            {
                try
                {
                    CacheDeps.MarkUncacheable("live tripwire summary data");
                    string id = RequireId(req.userParameters?.Get("id"));
                    string? summary = await service.Store.GetSummarySnapshotJsonAsync(id);
                    if (summary == null)
                    {
                        bool exists = await service.Store.ExistsAsync(id);
                        await req.ReturnResponse(exists ? "Tripwire summary is preparing." : "Tripwire not found.",
                            code: exists ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound);
                        return;
                    }
                    await req.ReturnResponse(summary);
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Get, TripwirePerms.WiresRead);

            await service.CreateBufferedAPIRoute("/tripwires/events/clear", async req =>
            {
                try
                {
                    string id = RequireId((string?)ParseBody(req.userMessageContent)["id"]);
                    bool cleared = await service.Store.ClearEventsAsync(id);
                    if (cleared)
                    {
                        service.Links.Reset(id);
                        await service.AfterChangeAsync(id);
                    }
                    await req.ReturnResponse(Json(new { cleared }), code: cleared ? HttpStatusCode.OK : HttpStatusCode.NotFound);
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (JsonException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Post, TripwirePerms.WiresManage, MaxBodyBytes);

            await service.CreateBufferedAPIRoute("/tripwires/notification/test", async req =>
            {
                try
                {
                    string id = RequireId((string?)ParseBody(req.userMessageContent)["id"]);
                    var item = await service.Store.GetAsync(id);
                    if (item == null) { await req.ReturnResponse("Tripwire not found.", code: HttpStatusCode.NotFound); return; }
                    var bots = await service.GetServicesByType<KliveBotDiscord>();
                    if (bots == null || bots.Length == 0) { await req.ReturnResponse("KliveBot Discord is not running.", code: HttpStatusCode.ServiceUnavailable); return; }
                    var message = KliveBotDiscord.MakeSimpleEmbed(
                        $"⚡ Tripwire test: {item.Name}",
                        "Discord notifications are connected. The next eligible trip will appear here with the details enabled in this tripwire's settings.",
                        new DiscordColor(0xF5A623));
                    var sent = await ((KliveBotDiscord)bots[0]).SendMessageToKlives(message)
                        .WaitAsync(TimeSpan.FromSeconds(15));
                    if (sent == null)
                    {
                        service.Health.DiscordFailed("KliveBot could not deliver the test notification.");
                        await req.ReturnResponse("KliveBot could not deliver the test notification.", code: HttpStatusCode.BadGateway);
                        return;
                    }
                    service.Health.DiscordSent();
                    await req.ReturnResponse(Json(new { sent = true }));
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (JsonException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Post, TripwirePerms.AlertsTest, MaxBodyBytes);
        }

        private async Task<TripwireRecord?> RequireTripwire(Services.KliveAPI.KliveAPI.UserRequest req)
        {
            string id = RequireId(req.userParameters?.Get("id"));
            var item = await service.Store.GetAsync(id);
            if (item == null) await req.ReturnResponse("Tripwire not found.", code: HttpStatusCode.NotFound);
            return item;
        }

        private object Present(TripwireRecord item)
        {
            // Trips written to the store lag the redirect by the write queue; the index is ahead of it.
            long trips = Math.Max(item.TotalTrips, service.Links.Trips(item.Id));
            return new
            {
                item.Id, item.Name, item.CreatedBy, item.CreatedUtc, item.UpdatedUtc,
                item.LastTrippedUtc, TotalTrips = trips, item.Settings,
                item.ClosedVisits, item.LastClosedUtc, item.PreviewVisits, item.LastPreviewUtc, item.LastPreviewAgent,
                status = Status(item, trips),
                closedReason = TripwireGate.ClosedReason(item, item.Targets.FirstOrDefault(t => t.Enabled) ?? new TripwireTarget { Enabled = false },
                    DateTimeOffset.UtcNow, trips),
                targets = item.Targets.Select(target => new
                {
                    target.Id, target.Label, target.DestinationUrl, target.Enabled,
                    target.SortOrder, target.TripCount, target.CreatedUtc,
                    trackingUrl = $"https://klive.dev/t?key={Uri.EscapeDataString(target.Token)}",
                }),
            };
        }

        internal object PresentOverview(TripwireOverviewModel model, bool intel)
        {
            var now = DateTimeOffset.UtcNow;
            var networks = new Dictionary<string, TripwireNetwork?>(StringComparer.Ordinal);
            TripwireNetwork? Network(string? ip)
            {
                if (!intel || string.IsNullOrWhiteSpace(ip)) return null;
                if (!networks.TryGetValue(ip, out var network)) networks[ip] = network = service.LookupNetwork(ip);
                return network;
            }

            var known = new Dictionary<string, (long Known, long Proxy)>(StringComparer.Ordinal);
            if (intel)
            {
                foreach (var row in model.IpCounts)
                {
                    var network = Network(row.Ip);
                    if (network == null) continue;
                    known.TryGetValue(row.TripwireId, out var counts);
                    if (!string.IsNullOrWhiteSpace(network.Profile)) counts.Known += row.Count;
                    if (network.Proxy == true) counts.Proxy += row.Count;
                    known[row.TripwireId] = counts;
                }
            }

            var tripwires = model.Tripwires.Select(item =>
            {
                var presented = JObject.FromObject(Present(item), Serializer);
                model.Summaries.TryGetValue(item.Id, out var summary);
                presented["summary"] = summary == null ? null : JToken.FromObject(summary, Serializer);
                known.TryGetValue(item.Id, out var counts);
                presented["knownTrips"] = intel ? counts.Known : null;
                presented["proxyTrips"] = intel ? counts.Proxy : null;
                return presented;
            }).ToList();

            long Sum(Func<TripwireSummary, long> pick) => model.Summaries.Values.Sum(pick);
            var last = model.Tripwires.Select(t => t.LastTrippedUtc).Where(t => t.HasValue).DefaultIfEmpty(null).Max();
            return new
            {
                generatedUtc = model.GeneratedUtc,
                tripwires,
                recent = model.Recent.Select(item => PresentEvent(item, intel, Network)),
                totals = new
                {
                    tripwires = model.Tripwires.Count,
                    active = model.Tripwires.Count(t => Status(t, Math.Max(t.TotalTrips, service.Links.Trips(t.Id))) == "Active"),
                    links = model.Tripwires.Sum(t => t.Targets.Count(x => x.Enabled)),
                    trips24h = Sum(s => s.TripsLast24Hours),
                    trips7d = Sum(s => s.TripsLast7Days),
                    retained = Sum(s => s.RetainedTrips),
                    closedVisits = model.Tripwires.Sum(t => t.ClosedVisits),
                    previews = model.Tripwires.Sum(t => t.PreviewVisits),
                    knownTrips = intel ? known.Values.Sum(k => k.Known) : (long?)null,
                    lastTrippedUtc = last,
                },
                health = service.Health.View(service.QueuedWrites, service.Links.Count, service.Links.LoadedUtc,
                    model.DiscordAvailable, intel && service.NetworkAvailable),
                intel,
                asOfUtc = now,
            };
        }

        private JObject PresentEvent(TripwireEvent item, bool intel, Func<string?, TripwireNetwork?>? lookup = null)
        {
            var presented = JObject.FromObject(item, Serializer);
            if (intel)
            {
                var network = lookup != null ? lookup(item.IpAddress) : service.LookupNetwork(item.IpAddress);
                presented["network"] = network == null ? null : JToken.FromObject(network, Serializer);
            }
            return presented;
        }

        internal static string Status(TripwireRecord item, long trips)
        {
            if (!item.Settings.Enabled) return "Disabled";
            if (item.Settings.ExpiresUtc.HasValue && item.Settings.ExpiresUtc <= DateTimeOffset.UtcNow) return "Expired";
            if (item.Settings.MaxTrips.HasValue && trips >= item.Settings.MaxTrips.Value) return "Limit reached";
            if (!item.Targets.Any(target => target.Enabled)) return "No active links";
            return "Active";
        }

        private static JObject ParseBody(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A JSON request body is required.");
            return JObject.Parse(value);
        }

        internal static TripwireSettings ParseSettings(JToken? token, TripwireSettings defaults)
        {
            var settings = JsonConvert.DeserializeObject<TripwireSettings>(JsonConvert.SerializeObject(defaults)) ?? new();
            if (token is JObject obj) JsonConvert.PopulateObject(obj.ToString(), settings);
            settings.Normalize();
            return settings;
        }

        private static List<TripwireTarget> ParseTargets(JToken? token, bool requireAtLeastOne)
        {
            if (token is not JArray array) throw new ArgumentException("targets must be an array.");
            if (array.Count > 100) throw new ArgumentException("A tripwire can contain at most 100 links.");
            var targets = new List<TripwireTarget>();
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is not JObject obj) throw new ArgumentException($"Link {i + 1} is invalid.");
                if (!TripwireService.TryNormalizeDestination((string?)obj["destinationUrl"] ?? (string?)obj["url"], out string url))
                    throw new ArgumentException($"Link {i + 1} must be an absolute http:// or https:// URL without embedded credentials.");
                string label = Clean((string?)obj["label"], 120) ?? new Uri(url).Host;
                targets.Add(new TripwireTarget
                {
                    Id = Clean((string?)obj["id"], 64) ?? "",
                    Label = label,
                    DestinationUrl = url,
                    Enabled = (bool?)obj["enabled"] ?? true,
                });
            }
            if (requireAtLeastOne && targets.Count == 0) throw new ArgumentException("Add at least one destination link.");
            return targets;
        }

        private static string RequireName(string? value)
            => Clean(value, 120) ?? throw new ArgumentException("A tripwire name is required.");
        private static string RequireId(string? value)
        {
            string? id = Clean(value, 64);
            if (id == null || !id.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException("A valid tripwire id is required.");
            return id;
        }
        private static string? Clean(string? value, int max)
        {
            value = value?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value[..Math.Min(max, value.Length)];
        }
        private static int Int(string? value, int fallback) => int.TryParse(value, out int result) ? result : fallback;
        private static string Json(object value) => JsonConvert.SerializeObject(value, JsonSettings);
        private static async Task Error(Services.KliveAPI.KliveAPI.UserRequest req, Exception ex)
            => await req.ReturnResponse("Tripwire request failed: " + ex.Message, code: HttpStatusCode.InternalServerError);
    }
}
