using System.Net;
using DSharpPlus.Entities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.KliveAPI.Caching;
using static Omnipotent.Profiles.KMProfileManager;

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

        public TripwireRoutes(TripwireService service) => this.service = service;

        public async Task RegisterRoutes()
        {
            await service.CreateAPIRoute("/t", service.HandlePublicTripAsync, HttpMethod.Get, KMPermissions.Anybody);

            await service.CreateAPIRoute("/tripwires/list", async req =>
            {
                try
                {
                    CacheDeps.MarkUncacheable("live tripwire management data");
                    var items = await service.Store.ListAsync();
                    await req.ReturnResponse(Json(items.Select(Present)));
                }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/tripwires/get", async req =>
            {
                try
                {
                    CacheDeps.MarkUncacheable("live tripwire management data");
                    var item = await RequireTripwire(req);
                    if (item != null) await req.ReturnResponse(Json(Present(item)));
                }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

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
                    await req.ReturnResponse(Json(Present(created)), code: HttpStatusCode.Created);
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (JsonException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives, MaxBodyBytes);

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
                    await req.ReturnResponse(Json(Present(updated!)));
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (JsonException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives, MaxBodyBytes);

            await service.CreateBufferedAPIRoute("/tripwires/delete", async req =>
            {
                try
                {
                    string id = RequireId((string?)ParseBody(req.userMessageContent)["id"]);
                    bool deleted = await service.Store.DeleteAsync(id);
                    await req.ReturnResponse(Json(new { deleted }), code: deleted ? HttpStatusCode.OK : HttpStatusCode.NotFound);
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives, MaxBodyBytes);

            await service.CreateAPIRoute("/tripwires/events", async req =>
            {
                try
                {
                    CacheDeps.MarkUncacheable("live tripwire event data");
                    string id = RequireId(req.userParameters?.Get("id"));
                    if (await service.Store.GetAsync(id) == null) { await req.ReturnResponse("Tripwire not found.", code: HttpStatusCode.NotFound); return; }
                    string? target = Clean(req.userParameters?.Get("targetId"), 64);
                    int limit = Int(req.userParameters?.Get("limit"), 100);
                    int offset = Int(req.userParameters?.Get("offset"), 0);
                    await req.ReturnResponse(Json(await service.Store.GetEventsAsync(id, target, limit, offset)));
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateAPIRoute("/tripwires/summary", async req =>
            {
                try
                {
                    CacheDeps.MarkUncacheable("live tripwire summary data");
                    string id = RequireId(req.userParameters?.Get("id"));
                    if (await service.Store.GetAsync(id) == null) { await req.ReturnResponse("Tripwire not found.", code: HttpStatusCode.NotFound); return; }
                    await req.ReturnResponse(Json(await service.Store.GetSummaryAsync(id)));
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Get, KMPermissions.Klives);

            await service.CreateBufferedAPIRoute("/tripwires/events/clear", async req =>
            {
                try
                {
                    string id = RequireId((string?)ParseBody(req.userMessageContent)["id"]);
                    bool cleared = await service.Store.ClearEventsAsync(id);
                    await req.ReturnResponse(Json(new { cleared }), code: cleared ? HttpStatusCode.OK : HttpStatusCode.NotFound);
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives, MaxBodyBytes);

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
                    if (sent == null) { await req.ReturnResponse("KliveBot could not deliver the test notification.", code: HttpStatusCode.BadGateway); return; }
                    await req.ReturnResponse(Json(new { sent = true }));
                }
                catch (ArgumentException ex) { await req.ReturnResponse(ex.Message, code: HttpStatusCode.BadRequest); }
                catch (Exception ex) { await Error(req, ex); }
            }, HttpMethod.Post, KMPermissions.Klives, MaxBodyBytes);
        }

        private async Task<TripwireRecord?> RequireTripwire(Services.KliveAPI.KliveAPI.UserRequest req)
        {
            string id = RequireId(req.userParameters?.Get("id"));
            var item = await service.Store.GetAsync(id);
            if (item == null) await req.ReturnResponse("Tripwire not found.", code: HttpStatusCode.NotFound);
            return item;
        }

        private static object Present(TripwireRecord item) => new
        {
            item.Id, item.Name, item.CreatedBy, item.CreatedUtc, item.UpdatedUtc,
            item.LastTrippedUtc, item.TotalTrips, item.Settings,
            status = Status(item),
            targets = item.Targets.Select(target => new
            {
                target.Id, target.Label, target.DestinationUrl, target.Enabled,
                target.SortOrder, target.TripCount, target.CreatedUtc,
                trackingUrl = $"https://klive.dev/t?key={Uri.EscapeDataString(target.Token)}",
            }),
        };

        private static string Status(TripwireRecord item)
        {
            if (!item.Settings.Enabled) return "Disabled";
            if (item.Settings.ExpiresUtc.HasValue && item.Settings.ExpiresUtc <= DateTimeOffset.UtcNow) return "Expired";
            if (item.Settings.MaxTrips.HasValue && item.TotalTrips >= item.Settings.MaxTrips.Value) return "Limit reached";
            if (!item.Targets.Any(target => target.Enabled)) return "No active links";
            return "Active";
        }

        private static JObject ParseBody(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A JSON request body is required.");
            return JObject.Parse(value);
        }

        private static TripwireSettings ParseSettings(JToken? token, TripwireSettings defaults)
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
