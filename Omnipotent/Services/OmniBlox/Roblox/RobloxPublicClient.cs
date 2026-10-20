using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.OmniBlox.Roblox
{
    /// <summary>
    /// Roblox endpoints that need no key: Creator Store search, charts, game stats and votes, and
    /// thumbnails. These carry the Research loop and the Asset Library's discovery on their own.
    /// </summary>
    public sealed class RobloxPublicClient
    {
        private readonly RobloxHttp http;

        public RobloxPublicClient(RobloxHttp http) => this.http = http;

        // ─────────────── Creator Store ───────────────

        public async Task<CreatorStoreSearchPage> SearchCreatorStoreAsync(CreatorStoreQuery q, CancellationToken ct)
        {
            var args = new List<string>
            {
                "searchCategoryType=" + Uri.EscapeDataString(q.Category),
                "maxPageSize=" + Math.Clamp(q.PageSize, 1, 100).ToString(CultureInfo.InvariantCulture),
                "searchView=Full",
            };
            if (!string.IsNullOrWhiteSpace(q.Query)) args.Add("query=" + Uri.EscapeDataString(q.Query.Trim()));
            if (!string.IsNullOrWhiteSpace(q.PageToken)) args.Add("pageToken=" + Uri.EscapeDataString(q.PageToken));
            if (q.FreeOnly) args.Add("maxPriceCents=0");
            if (q.VerifiedCreatorsOnly) args.Add("includeOnlyVerifiedCreators=true");
            if (!string.IsNullOrWhiteSpace(q.SortBy)) args.Add("sortBy=" + Uri.EscapeDataString(q.SortBy));
            if (!string.IsNullOrWhiteSpace(q.CategoryPath)) args.Add("categoryPath=" + Uri.EscapeDataString(q.CategoryPath));
            if (q.Facets.Count > 0) args.Add("facets=" + Uri.EscapeDataString(string.Join(",", q.Facets)));

            string url = "https://apis.roblox.com/toolbox-service/v2/assets:search?" + string.Join("&", args);
            var json = await GetJsonAsync("toolbox", url, ct);
            var page = new CreatorStoreSearchPage
            {
                NextPageToken = (string?)json["nextPageToken"],
                TotalResults = (long?)json["totalResults"] ?? 0,
                AvailableFacets = (json["queryFacets"]?["availableFacets"] as JArray)?.Select(f => f.ToString()).ToList() ?? new(),
            };
            foreach (var entry in json["creatorStoreAssets"] as JArray ?? new JArray())
            {
                var a = ParseCreatorStoreAsset(entry);
                if (a != null) page.Assets.Add(a);
            }
            return page;
        }

        internal static CreatorStoreAsset? ParseCreatorStoreAsset(JToken entry)
        {
            var asset = entry["asset"];
            if (asset == null) return null;
            long id = (long?)asset["id"] ?? 0;
            if (id <= 0) return null;
            var counts = asset["instanceCounts"];
            var mesh = asset["objectMeshSummary"];
            var voting = entry["voting"];
            var creator = entry["creator"];
            var price = entry["creatorStoreProduct"]?["purchasePrice"]?["quantity"];
            return new CreatorStoreAsset
            {
                AssetId = id,
                Name = ((string?)asset["name"] ?? "").Trim(),
                Description = ((string?)asset["description"] ?? "").Trim(),
                AssetTypeId = (int?)asset["assetTypeId"] ?? 0,
                CategoryPath = (string?)asset["categoryPath"] ?? "",
                HasScripts = (bool?)asset["hasScripts"] ?? false,
                ScriptCount = (int?)asset["scriptCount"] ?? (int?)counts?["script"] ?? 0,
                Triangles = (long?)mesh?["triangles"] ?? 0,
                Vertices = (long?)mesh?["vertices"] ?? 0,
                MeshPartCount = (int?)counts?["meshPart"] ?? 0,
                DecalCount = (int?)counts?["decal"] ?? 0,
                AudioCount = (int?)counts?["audio"] ?? 0,
                ToolCount = (int?)counts?["tool"] ?? 0,
                AnimationCount = (int?)counts?["animation"] ?? 0,
                SubTypes = (asset["subTypes"] as JArray)?.Select(s => s.ToString()).ToList() ?? new(),
                CreatedUtc = Date(asset["createTime"]),
                UpdatedUtc = Date(asset["updateTime"]),
                CreatorUserId = (long?)creator?["userId"] ?? 0,
                CreatorGroupId = (long?)creator?["groupId"] ?? 0,
                CreatorName = (string?)creator?["name"] ?? "",
                CreatorVerified = (bool?)creator?["verified"] ?? false,
                UpVotes = (long?)voting?["upVotes"] ?? 0,
                DownVotes = (long?)voting?["downVotes"] ?? 0,
                UpVotePercent = (int?)voting?["upVotePercent"] ?? 0,
                PriceUsd = Money(price),
                Purchasable = (bool?)entry["creatorStoreProduct"]?["purchasable"] ?? false,
            };
        }

        /// <summary>Roblox money: significand × 10^exponent.</summary>
        internal static decimal Money(JToken? quantity)
        {
            if (quantity == null) return 0m;
            long significand = (long?)quantity["significand"] ?? 0;
            int exponent = (int?)quantity["exponent"] ?? 0;
            if (significand == 0) return 0m;
            decimal value = significand;
            if (exponent < 0) for (int i = 0; i < -exponent; i++) value /= 10m;
            else for (int i = 0; i < exponent; i++) value *= 10m;
            return value;
        }

        // ─────────────── Charts and games ───────────────

        /// <summary>The home/charts sorts (Top Trending, Up-and-Coming, Top Playing Now, …), with their games.</summary>
        public async Task<List<ChartSort>> GetChartsAsync(CancellationToken ct, int maxPages = 3)
        {
            string session = Guid.NewGuid().ToString();
            var sorts = new List<ChartSort>();
            string? token = null;
            for (int page = 0; page < maxPages; page++)
            {
                string url = "https://apis.roblox.com/explore-api/v1/get-sorts?sessionId=" + session
                    + (token == null ? "" : "&sortsPageToken=" + Uri.EscapeDataString(token));
                var json = await GetJsonAsync("public", url, ct);
                foreach (var s in json["sorts"] as JArray ?? new JArray())
                {
                    if ((string?)s["contentType"] != "Games") continue;
                    var sort = new ChartSort
                    {
                        SortId = (string?)s["sortId"] ?? "",
                        DisplayName = (string?)s["sortDisplayName"] ?? "",
                    };
                    int rank = 0;
                    foreach (var g in s["games"] as JArray ?? new JArray())
                    {
                        sort.Games.Add(new ChartGame
                        {
                            UniverseId = (long?)g["universeId"] ?? 0,
                            RootPlaceId = (long?)g["rootPlaceId"] ?? 0,
                            Name = (string?)g["name"] ?? "",
                            PlayerCount = (long?)g["playerCount"] ?? 0,
                            UpVotes = (long?)g["totalUpVotes"] ?? 0,
                            DownVotes = (long?)g["totalDownVotes"] ?? 0,
                            IsSponsored = (bool?)g["isSponsored"] ?? false,
                            Genre = (string?)g["genreL1"] ?? "",
                            ContentMaturity = (string?)g["contentMaturity"] ?? "",
                            Rank = ++rank,
                        });
                    }
                    if (sort.Games.Count > 0 && sorts.All(x => x.SortId != sort.SortId)) sorts.Add(sort);
                }
                token = (string?)json["nextSortsPageToken"];
                if (string.IsNullOrEmpty(token)) break;
            }
            return sorts;
        }

        public async Task<List<GameDetails>> GetGameDetailsAsync(IEnumerable<long> universeIds, CancellationToken ct)
        {
            var ids = universeIds.Where(i => i > 0).Distinct().ToList();
            var results = new List<GameDetails>();
            foreach (var batch in ids.Chunk(50))
            {
                string idList = string.Join(",", batch);
                var details = await GetJsonAsync("public", "https://games.roblox.com/v1/games?universeIds=" + idList, ct);
                var votes = await GetJsonAsync("public", "https://games.roblox.com/v1/games/votes?universeIds=" + idList, ct);
                var voteMap = (votes["data"] as JArray ?? new JArray())
                    .ToDictionary(v => (long?)v["id"] ?? 0, v => ((long?)v["upVotes"] ?? 0, (long?)v["downVotes"] ?? 0));
                foreach (var g in details["data"] as JArray ?? new JArray())
                {
                    long id = (long?)g["id"] ?? 0;
                    voteMap.TryGetValue(id, out var v);
                    results.Add(new GameDetails
                    {
                        UniverseId = id,
                        RootPlaceId = (long?)g["rootPlaceId"] ?? 0,
                        Name = (string?)g["name"] ?? "",
                        Description = (string?)g["description"] ?? "",
                        CreatorName = (string?)g["creator"]?["name"] ?? "",
                        CreatorType = (string?)g["creator"]?["type"] ?? "",
                        Playing = (long?)g["playing"] ?? 0,
                        Visits = (long?)g["visits"] ?? 0,
                        Favorites = (long?)g["favoritedCount"] ?? 0,
                        MaxPlayers = (int?)g["maxPlayers"] ?? 0,
                        Genre = (string?)g["genre"] ?? "",
                        GenreL1 = (string?)g["genre_l1"] ?? "",
                        GenreL2 = (string?)g["genre_l2"] ?? "",
                        CreatedUtc = Date(g["created"]),
                        UpdatedUtc = Date(g["updated"]),
                        UpVotes = v.Item1,
                        DownVotes = v.Item2,
                    });
                }
            }
            return results;
        }

        // ─────────────── Thumbnails ───────────────

        public Task<List<ThumbnailResult>> GetAssetThumbnailsAsync(IEnumerable<long> assetIds, CancellationToken ct, string size = "420x420") =>
            GetThumbnailsAsync("https://thumbnails.roblox.com/v1/assets?assetIds={0}&size=" + size + "&format=Png&isCircular=false", assetIds, ct);

        public Task<List<ThumbnailResult>> GetGameIconsAsync(IEnumerable<long> universeIds, CancellationToken ct, string size = "512x512") =>
            GetThumbnailsAsync("https://thumbnails.roblox.com/v1/games/icons?universeIds={0}&size=" + size + "&format=Png&isCircular=false", universeIds, ct);

        private async Task<List<ThumbnailResult>> GetThumbnailsAsync(string template, IEnumerable<long> ids, CancellationToken ct)
        {
            var results = new List<ThumbnailResult>();
            foreach (var batch in ids.Where(i => i > 0).Distinct().Chunk(100))
            {
                var json = await GetJsonAsync("thumbnails", string.Format(CultureInfo.InvariantCulture, template, string.Join(",", batch)), ct);
                foreach (var t in json["data"] as JArray ?? new JArray())
                    results.Add(new ThumbnailResult
                    {
                        TargetId = (long?)t["targetId"] ?? 0,
                        State = (string?)t["state"] ?? "",
                        ImageUrl = (string?)t["imageUrl"],
                    });
            }
            return results;
        }

        /// <summary>Downloads a CDN image (thumbnail URLs are short-lived and public).</summary>
        public async Task<byte[]> DownloadAsync(string url, CancellationToken ct, long maxBytes = 25 * 1024 * 1024)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !IsRobloxCdn(uri.Host))
                throw new ArgumentException("Only https Roblox CDN URLs can be downloaded: " + url);
            var response = await http.SendAsync("cdn", () => new HttpRequestMessage(HttpMethod.Get, uri), ct);
            if (!response.Ok) throw new RobloxApiException("cdn", response.Status, response.Body);
            if (response.Bytes.LongLength > maxBytes) throw new InvalidOperationException($"Download exceeds {maxBytes} bytes.");
            return response.Bytes;
        }

        internal static bool IsRobloxCdn(string host) =>
            host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase);

        private async Task<JObject> GetJsonAsync(string bucket, string url, CancellationToken ct)
        {
            var response = await http.SendAsync(bucket, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
            if (!response.Ok) throw new RobloxApiException(new Uri(url).AbsolutePath, response.Status, response.Body);
            return JObject.Parse(response.Body);
        }

        internal static DateTime? Date(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Date) return token.Value<DateTime>().ToUniversalTime();
            return DateTime.TryParse(token.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        }
    }
}
