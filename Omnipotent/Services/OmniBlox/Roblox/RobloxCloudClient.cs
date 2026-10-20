using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.OmniBlox.Roblox
{
    /// <summary>Who uploaded assets belong to.</summary>
    public sealed record RobloxCreator(long? UserId, long? GroupId)
    {
        public bool IsSet => UserId is > 0 || GroupId is > 0;
    }

    /// <summary>
    /// Roblox Open Cloud, authenticated with the owner's API key. Every method throws
    /// <see cref="RobloxKeyMissingException"/> when no key is configured, so callers can tell "not set
    /// up yet" from "Roblox said no".
    /// </summary>
    public sealed class RobloxCloudClient
    {
        private const string Base = "https://apis.roblox.com";
        private readonly RobloxHttp http;
        private readonly Func<string?> apiKey;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;

        public RobloxCloudClient(RobloxHttp http, Func<string?> apiKey, Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            this.http = http;
            this.apiKey = apiKey;
            this.delay = delay ?? Task.Delay;
        }

        public bool HasKey => !string.IsNullOrWhiteSpace(apiKey());

        private string Key(string capability) =>
            apiKey() is { Length: > 0 } k ? k.Trim() : throw new RobloxKeyMissingException(capability);

        private async Task<RobloxResponse> SendAsync(string bucket, string capability, HttpMethod method, string url, Func<HttpContent?>? content, CancellationToken ct)
        {
            string key = Key(capability);
            var response = await http.SendAsync(bucket, () =>
            {
                var req = new HttpRequestMessage(method, url);
                req.Headers.Add("x-api-key", key);
                var c = content?.Invoke();
                if (c != null) req.Content = c;
                return req;
            }, ct);
            if (!response.Ok)
            {
                string? hint = response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? $"check the API key has the permission for {capability} and access to this experience"
                    : null;
                throw new RobloxApiException(new Uri(url).AbsolutePath, response.Status, response.Body, hint);
            }
            return response;
        }

        private static StringContent Json(object body) =>
            new(JsonConvert.SerializeObject(body, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }), Encoding.UTF8, "application/json");

        // ─────────────── Places ───────────────

        /// <summary>
        /// Uploads a place file as a new version. <paramref name="publish"/> false saves it (testable via
        /// Luau execution, invisible to players); true publishes it live.
        /// </summary>
        public async Task<PublishResult> PublishPlaceAsync(long universeId, long placeId, byte[] placeFile, bool isXml, bool publish, CancellationToken ct)
        {
            string url = $"{Base}/universes/v1/{universeId}/places/{placeId}/versions?versionType={(publish ? "Published" : "Saved")}";
            var response = await SendAsync("publish", "place publishing", HttpMethod.Post, url, () =>
            {
                var c = new ByteArrayContent(placeFile);
                c.Headers.ContentType = new MediaTypeHeaderValue(isXml ? "application/xml" : "application/octet-stream");
                return c;
            }, ct);
            var json = JObject.Parse(response.Body);
            return new PublishResult { VersionNumber = (long?)json["versionNumber"] ?? 0 };
        }

        /// <summary>The store-page name and description live on the root place.</summary>
        public async Task UpdatePlaceAsync(long universeId, long placeId, string? displayName, string? description, CancellationToken ct)
        {
            var mask = new List<string>();
            var body = new JObject();
            if (displayName != null) { mask.Add("displayName"); body["displayName"] = displayName; }
            if (description != null) { mask.Add("description"); body["description"] = description; }
            if (mask.Count == 0) return;
            string url = $"{Base}/cloud/v2/universes/{universeId}/places/{placeId}?updateMask={string.Join(",", mask)}";
            await SendAsync("universe", "place configuration (universe.place:write)", HttpMethod.Patch, url,
                () => new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"), ct);
        }

        public async Task<UniverseInfo> GetUniverseAsync(long universeId, CancellationToken ct)
        {
            var response = await SendAsync("universe", "universe reads", HttpMethod.Get, $"{Base}/cloud/v2/universes/{universeId}", null, ct);
            var j = JObject.Parse(response.Body);
            return new UniverseInfo
            {
                UniverseId = universeId,
                DisplayName = (string?)j["displayName"] ?? "",
                Description = (string?)j["description"] ?? "",
                Visibility = (string?)j["visibility"] ?? "",
                RootPlaceId = ParseTrailingId((string?)j["rootPlace"]),
                OwnerUserId = ParseTrailingId((string?)j["user"]),
                OwnerGroupId = ParseTrailingId((string?)j["group"]),
            };
        }

        public async Task UpdateUniverseAsync(long universeId, UniverseUpdate update, CancellationToken ct)
        {
            var mask = new List<string>();
            var body = new JObject();
            // object, not JToken: a null string/bool? converts to a non-null JValue and would slip through.
            void Put(string field, object? value)
            {
                if (value == null) return;
                mask.Add(field);
                body[field] = JToken.FromObject(value);
            }
            Put("visibility", update.Visibility);
            Put("voiceChatEnabled", update.VoiceChatEnabled);
            Put("desktopEnabled", update.DesktopEnabled);
            Put("mobileEnabled", update.MobileEnabled);
            Put("tabletEnabled", update.TabletEnabled);
            Put("consoleEnabled", update.ConsoleEnabled);
            Put("vrEnabled", update.VrEnabled);
            if (update.DisablePrivateServers) { mask.Add("privateServerPriceRobux"); body["privateServerPriceRobux"] = JValue.CreateNull(); }
            else Put("privateServerPriceRobux", update.PrivateServerPriceRobux);
            if (mask.Count == 0) return;
            string url = $"{Base}/cloud/v2/universes/{universeId}?updateMask={string.Join(",", mask)}";
            await SendAsync("universe", "universe configuration (universe:write)", HttpMethod.Patch, url,
                () => new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"), ct);
        }

        // ─────────────── Monetization ───────────────

        public async Task<GamePassInfo> CreateGamePassAsync(long universeId, ProductSpec spec, CancellationToken ct)
        {
            var response = await SendAsync("game-pass", "game passes (game-pass:write)", HttpMethod.Post,
                $"{Base}/game-passes/v1/universes/{universeId}/game-passes", () => ProductForm(spec, includeName: true), ct);
            return ParseGamePass(JObject.Parse(response.Body));
        }

        public async Task UpdateGamePassAsync(long universeId, long gamePassId, ProductSpec spec, CancellationToken ct) =>
            await SendAsync("game-pass", "game passes (game-pass:write)", HttpMethod.Patch,
                $"{Base}/game-passes/v1/universes/{universeId}/game-passes/{gamePassId}", () => ProductForm(spec, includeName: true), ct);

        public async Task<List<GamePassInfo>> ListGamePassesAsync(long universeId, CancellationToken ct)
        {
            var all = new List<GamePassInfo>();
            string? token = null;
            do
            {
                string url = $"{Base}/game-passes/v1/universes/{universeId}/game-passes/creator?pageSize=50" + (token == null ? "" : "&pageToken=" + Uri.EscapeDataString(token));
                var response = await SendAsync("game-pass", "game passes (game-pass:read)", HttpMethod.Get, url, null, ct);
                var j = JObject.Parse(response.Body);
                foreach (var g in j["gamePasses"] as JArray ?? new JArray()) all.Add(ParseGamePass(g));
                token = (string?)j["nextPageToken"];
            } while (!string.IsNullOrEmpty(token));
            return all;
        }

        public async Task<DeveloperProductInfo> CreateDeveloperProductAsync(long universeId, ProductSpec spec, CancellationToken ct)
        {
            var response = await SendAsync("developer-product", "developer products (developer-product:write)", HttpMethod.Post,
                $"{Base}/developer-products/v2/universes/{universeId}/developer-products", () => ProductForm(spec, includeName: true), ct);
            return ParseDeveloperProduct(JObject.Parse(response.Body));
        }

        public async Task UpdateDeveloperProductAsync(long universeId, long productId, ProductSpec spec, CancellationToken ct) =>
            await SendAsync("developer-product", "developer products (developer-product:write)", HttpMethod.Patch,
                $"{Base}/developer-products/v2/universes/{universeId}/developer-products/{productId}", () => ProductForm(spec, includeName: true), ct);

        public async Task<List<DeveloperProductInfo>> ListDeveloperProductsAsync(long universeId, CancellationToken ct)
        {
            var all = new List<DeveloperProductInfo>();
            string? token = null;
            do
            {
                string url = $"{Base}/developer-products/v2/universes/{universeId}/developer-products/creator?pageSize=50" + (token == null ? "" : "&pageToken=" + Uri.EscapeDataString(token));
                var response = await SendAsync("developer-product", "developer products (developer-product:read)", HttpMethod.Get, url, null, ct);
                var j = JObject.Parse(response.Body);
                foreach (var p in j["developerProducts"] as JArray ?? new JArray()) all.Add(ParseDeveloperProduct(p));
                token = (string?)j["nextPageToken"];
            } while (!string.IsNullOrEmpty(token));
            return all;
        }

        private static MultipartFormDataContent ProductForm(ProductSpec spec, bool includeName)
        {
            var form = new MultipartFormDataContent();
            if (includeName) form.Add(new StringContent(spec.Name), "name");
            form.Add(new StringContent(spec.Description ?? ""), "description");
            form.Add(new StringContent(spec.PriceRobux.ToString(CultureInfo.InvariantCulture)), "price");
            form.Add(new StringContent(spec.IsForSale ? "true" : "false"), "isForSale");
            if (spec.ImagePng is { Length: > 0 } png)
            {
                var image = new ByteArrayContent(png);
                image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(image, "imageFile", "icon.png");
            }
            return form;
        }

        private static GamePassInfo ParseGamePass(JToken j) => new()
        {
            GamePassId = (long?)j["gamePassId"] ?? (long?)j["id"] ?? 0,
            Name = (string?)j["name"] ?? "",
            Description = (string?)j["description"] ?? "",
            IsForSale = (bool?)j["isForSale"] ?? false,
            Price = (long?)j["priceInformation"]?["defaultPriceInRobux"] ?? (long?)j["price"],
            IconAssetId = (long?)j["iconAssetId"],
        };

        private static DeveloperProductInfo ParseDeveloperProduct(JToken j) => new()
        {
            ProductId = (long?)j["productId"] ?? (long?)j["id"] ?? 0,
            Name = (string?)j["name"] ?? "",
            Description = (string?)j["description"] ?? "",
            IsForSale = (bool?)j["isForSale"] ?? false,
            Price = (long?)j["priceInformation"]?["defaultPriceInRobux"] ?? (long?)j["price"],
            IconImageAssetId = (long?)j["iconImageAssetId"],
        };

        // ─────────────── Assets ───────────────

        /// <summary>Downloads an asset's content (a model's .rbxm) through Open Cloud asset delivery.</summary>
        public async Task<byte[]> DownloadAssetAsync(long assetId, CancellationToken ct, long maxBytes = 50L * 1024 * 1024)
        {
            var response = await SendAsync("asset-delivery", "model downloads (legacy-asset:manage)", HttpMethod.Get,
                $"{Base}/asset-delivery-api/v1/assetId/{assetId}", null, ct);
            string? location = (string?)JObject.Parse(response.Body)["location"];
            if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new RobloxApiException("asset-delivery", response.Status, response.Body, "no download location in the response");
            var file = await http.SendAsync("cdn", () => new HttpRequestMessage(HttpMethod.Get, uri), ct);
            if (!file.Ok) throw new RobloxApiException("asset-delivery cdn", file.Status, file.Body);
            if (file.Bytes.LongLength > maxBytes) throw new InvalidOperationException($"Asset {assetId} is larger than {maxBytes} bytes.");
            return file.Bytes;
        }

        /// <summary>Creates an Image/Decal/Model asset and waits for Roblox to finish processing it.</summary>
        public async Task<UploadedAsset> UploadAssetAsync(string assetType, string displayName, string description, byte[] content, string contentType,
            string fileName, RobloxCreator creator, CancellationToken ct, TimeSpan? timeout = null)
        {
            if (!creator.IsSet) throw new InvalidOperationException("Set OmniBlox_CreatorUserId or OmniBlox_CreatorGroupId before uploading assets.");
            var request = new JObject
            {
                ["assetType"] = assetType,
                ["displayName"] = Truncate(displayName, 50),
                ["description"] = Truncate(description, 1000),
                ["creationContext"] = new JObject
                {
                    ["creator"] = creator.GroupId is > 0
                        ? new JObject { ["groupId"] = creator.GroupId.Value.ToString(CultureInfo.InvariantCulture) }
                        : new JObject { ["userId"] = creator.UserId!.Value.ToString(CultureInfo.InvariantCulture) },
                },
            };
            var response = await SendAsync("asset-create", "asset uploads (asset:write)", HttpMethod.Post, $"{Base}/assets/v1/assets", () =>
            {
                var form = new MultipartFormDataContent();
                form.Add(new StringContent(request.ToString(Formatting.None), Encoding.UTF8, "application/json"), "request");
                var file = new ByteArrayContent(content);
                file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                form.Add(file, "fileContent", fileName);
                return form;
            }, ct);

            var op = JObject.Parse(response.Body);
            string path = (string?)op["path"] ?? "";
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(2));
            int polls = 0;
            while (!((bool?)op["done"] ?? false))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"Roblox did not finish processing the upload ({path}).");
                await delay(TimeSpan.FromSeconds(Math.Min(10, 1 + polls++)), ct);
                string opId = path.Split('/').Last();
                var poll = await SendAsync("asset-operation", "asset uploads (asset:read)", HttpMethod.Get, $"{Base}/assets/v1/operations/{opId}", null, ct);
                op = JObject.Parse(poll.Body);
            }
            if (op["error"] is JObject err && err.HasValues)
                throw new RobloxApiException("assets/v1/assets", HttpStatusCode.BadRequest, err.ToString(Formatting.None), "the upload was rejected");
            long assetId = (long?)op["response"]?["assetId"] ?? 0;
            if (assetId == 0 && long.TryParse((string?)op["response"]?["assetId"], out var parsed)) assetId = parsed;
            return new UploadedAsset { AssetId = assetId, OperationPath = path };
        }

        // ─────────────── Engine tests ───────────────

        /// <summary>Runs a Luau script against a saved place version in Roblox's engine and waits for the result.</summary>
        public async Task<LuauTaskResult> RunLuauAsync(long universeId, long placeId, long version, string script, TimeSpan timeout, CancellationToken ct)
        {
            string url = $"{Base}/cloud/v2/universes/{universeId}/places/{placeId}/versions/{version}/luau-execution-session-tasks";
            var body = new JObject { ["script"] = script, ["timeout"] = $"{(int)Math.Clamp(timeout.TotalSeconds, 1, 300)}s" };
            var created = await SendAsync("luau-create", "engine tests (universe.place.luau-execution-session:write)", HttpMethod.Post, url,
                () => new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"), ct);
            var task = ParseLuauTask(JObject.Parse(created.Body));

            var deadline = DateTime.UtcNow + timeout + TimeSpan.FromMinutes(2);
            int polls = 0;
            while (!task.Finished)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The Luau execution task did not finish in time.");
                await delay(TimeSpan.FromSeconds(Math.Min(10, 2 + polls++)), ct);
                var poll = await SendAsync("luau-read", "engine tests (read)", HttpMethod.Get, $"{Base}/cloud/v2/{task.Path}", null, ct);
                task = ParseLuauTask(JObject.Parse(poll.Body));
            }

            try
            {
                var logs = await SendAsync("luau-logs", "engine test logs", HttpMethod.Get, $"{Base}/cloud/v2/{task.Path}/logs?maxPageSize=10000", null, ct);
                foreach (var page in JObject.Parse(logs.Body)["luauExecutionSessionTaskLogs"] as JArray ?? new JArray())
                    foreach (var m in page["messages"] as JArray ?? new JArray())
                        task.Logs.Add(m.ToString());
            }
            catch (RobloxApiException) { /* logs are a bonus; the result stands */ }
            return task;
        }

        internal static LuauTaskResult ParseLuauTask(JObject j)
        {
            string state = (string?)j["state"] ?? "";
            return new LuauTaskResult
            {
                Path = (string?)j["path"] ?? "",
                State = state switch
                {
                    "QUEUED" => LuauTaskState.Queued,
                    "PROCESSING" => LuauTaskState.Processing,
                    "CANCELLED" => LuauTaskState.Cancelled,
                    "COMPLETE" => LuauTaskState.Complete,
                    "FAILED" => LuauTaskState.Failed,
                    _ => LuauTaskState.Unspecified,
                },
                OutputJson = j["output"]?["results"]?.ToString(Formatting.None) ?? j["output"]?.ToString(Formatting.None),
                ErrorCode = (string?)j["error"]?["code"],
                ErrorMessage = (string?)j["error"]?["message"],
            };
        }

        // ─────────────── Analytics ───────────────

        public async Task<List<MetricSeries>> QueryMetricAsync(long universeId, MetricQuery q, CancellationToken ct)
        {
            var body = new JObject
            {
                ["metric"] = q.Metric,
                ["granularity"] = q.Granularity,
                ["startTime"] = q.StartUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["endTime"] = q.EndUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            };
            if (q.Breakdown.Count > 0) body["breakdown"] = new JArray(q.Breakdown);
            var response = await SendAsync("analytics", "analytics (universe.analytics:read)", HttpMethod.Post,
                $"{Base}/analytics-query-api/v1/universes/{universeId}/metrics",
                () => new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"), ct);
            var op = JObject.Parse(response.Body);
            int polls = 0;
            while (!((bool?)op["done"] ?? true))
            {
                if (polls++ > 30) throw new TimeoutException("The analytics query did not finish.");
                await delay(TimeSpan.FromSeconds(2), ct);
                var poll = await SendAsync("analytics", "analytics (read)", HttpMethod.Get, $"{Base}/analytics-query-api/{op["path"]}", null, ct);
                op = JObject.Parse(poll.Body);
            }
            if (op["error"] is JObject err && err.HasValues)
                throw new RobloxApiException("analytics-query-api", HttpStatusCode.BadRequest, err.ToString(Formatting.None));

            var series = new List<MetricSeries>();
            foreach (var v in op["response"]?["values"] as JArray ?? new JArray())
            {
                var s = new MetricSeries { Metric = q.Metric };
                foreach (var b in v["breakdowns"] as JArray ?? new JArray())
                    s.Breakdown[(string?)b["dimension"] ?? ""] = (string?)b["value"] ?? "";
                foreach (var p in v["dataPoints"] as JArray ?? new JArray())
                {
                    var time = RobloxPublicClient.Date(p["time"]);
                    if (time == null) continue;
                    double value = p["value"]?.Type is JTokenType.Float or JTokenType.Integer
                        ? (double)p["value"]!
                        : double.TryParse((string?)p["value"], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
                    s.Points.Add(new MetricPoint { Time = time.Value, Value = value });
                }
                series.Add(s);
            }
            return series;
        }

        // ─────────────── Homepage thumbnails (experimental API) ───────────────

        public async Task UploadHomepageThumbnailsAsync(long universeId, IReadOnlyList<byte[]> pngs, CancellationToken ct)
        {
            await SendAsync("thumbnail-personalization", "homepage thumbnails (universe.thumbnail:write)", HttpMethod.Post,
                $"{Base}/thumbnail-personalization-api/v1/universes/{universeId}/thumbnails/uploads", () =>
                {
                    var form = new MultipartFormDataContent();
                    int i = 0;
                    foreach (var png in pngs)
                    {
                        var file = new ByteArrayContent(png);
                        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                        form.Add(file, "files", $"thumbnail{++i}.png");
                    }
                    return form;
                }, ct);
        }

        private static long? ParseTrailingId(string? path) =>
            path != null && long.TryParse(path.Split('/').Last(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
