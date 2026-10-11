using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniGram.Api
{
    /// <summary>The Instagram API surface OmniGram uses (an interface so the engine can be tested against a fake).</summary>
    public interface IInstagramApi
    {
        string BuildAuthorizeUrl(string appId, string redirectUri, string state, bool forceReauth);
        Task<IgShortLivedToken> ExchangeCodeAsync(string appId, string appSecret, string redirectUri, string code, CancellationToken ct);
        Task<IgLongLivedToken> ExchangeForLongLivedAsync(string appSecret, string shortLivedToken, CancellationToken ct);
        Task<IgLongLivedToken> RefreshLongLivedAsync(string token, CancellationToken ct);

        Task<IgProfile> GetProfileAsync(string token, CancellationToken ct);

        Task<string> CreateContainerAsync(string token, string igUserId, IgContainerRequest request, CancellationToken ct);
        Task<IgContainerStatus> GetContainerStatusAsync(string token, string containerId, CancellationToken ct);
        Task<string> PublishContainerAsync(string token, string igUserId, string containerId, CancellationToken ct);
        Task<IgPublishingLimit> GetPublishingLimitAsync(string token, string igUserId, CancellationToken ct);

        Task<IgMediaPage> GetMediaPageAsync(string token, string igUserId, string? after, int limit, CancellationToken ct);
        Task<IgMediaItem> GetMediaItemAsync(string token, string mediaId, CancellationToken ct);
        Task<IgInsightValues> GetMediaInsightsAsync(string token, string mediaId, IReadOnlyCollection<string> metrics, CancellationToken ct);
        Task DeleteMediaAsync(string token, string mediaId, CancellationToken ct);

        Task<IgInsightValues> GetAccountTotalsAsync(string token, string igUserId, IReadOnlyCollection<string> metrics, DateTime sinceUtc, DateTime untilUtc, string? breakdown, CancellationToken ct);
        Task<IgInsightValues> GetFollowerDemographicsAsync(string token, string igUserId, string breakdown, CancellationToken ct);
        Task<List<(DateTime EndTimeUtc, Dictionary<int, double> ByHour)>> GetOnlineFollowersAsync(string token, string igUserId, DateTime sinceUtc, DateTime untilUtc, CancellationToken ct);

        Task<List<IgComment>> GetCommentsAsync(string token, string mediaId, int limit, CancellationToken ct);
        Task<string> CreateCommentAsync(string token, string mediaId, string message, CancellationToken ct);
        Task<string> ReplyToCommentAsync(string token, string commentId, string message, CancellationToken ct);
        Task SetCommentHiddenAsync(string token, string commentId, bool hidden, CancellationToken ct);
        Task DeleteCommentAsync(string token, string commentId, CancellationToken ct);
    }

    /// <summary>
    /// A small, owned client for the official Instagram API with Instagram Login (graph.instagram.com).
    /// Replaces InstagramApiSharp, an unmaintained private-API library that impersonated a 2020 Android app
    /// and whose logins Instagram had long since started challenging. Every failure becomes an
    /// <see cref="InstagramApiException"/> with Meta's code and subcode; tokens travel as the documented
    /// access_token parameter and never appear in exception messages.
    /// </summary>
    public sealed class InstagramGraphClient : IInstagramApi
    {
        public const string GraphHost = "https://graph.instagram.com";
        public const string OAuthHost = "https://api.instagram.com";
        public const string AuthorizeEndpoint = "https://www.instagram.com/oauth/authorize";
        public const string DefaultVersion = "v25.0";
        public const string UserAgent = "OmniGram/2.0 (+https://klive.dev)";

        /// <summary>Business Login scopes: profile + media, publishing, insights, comments.</summary>
        public static readonly string[] Scopes =
        {
            "instagram_business_basic",
            "instagram_business_content_publish",
            "instagram_business_manage_insights",
            "instagram_business_manage_comments",
        };

        public const string MediaFields = "id,caption,media_type,media_product_type,media_url,thumbnail_url,permalink,shortcode,timestamp,like_count,comments_count,is_shared_to_feed";
        private const string BasicProfileFields = "id,user_id,username,name,account_type,profile_picture_url,followers_count,follows_count,media_count";

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HttpClient> SharedClients = new(StringComparer.Ordinal);
        private static volatile bool extendedProfileFields = true;

        private readonly HttpClient? injectedHttp;
        private readonly Action<string>? onCall;
        private readonly Action<IgUsageHeaders>? onUsage;
        private volatile string proxy = "";
        private volatile string version = DefaultVersion;

        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(40);
        /// <summary>Creating a container can make Instagram download the media before it answers.</summary>
        public TimeSpan ContainerTimeout { get; set; } = TimeSpan.FromSeconds(120);
        public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

        /// <param name="onCall">Invoked once per API request with a short endpoint label (budget accounting).</param>
        /// <param name="onUsage">Invoked when a response carries Meta's usage headers.</param>
        public InstagramGraphClient(HttpClient? http = null, Action<string>? onCall = null, Action<IgUsageHeaders>? onUsage = null)
        {
            injectedHttp = http;
            this.onCall = onCall;
            this.onUsage = onUsage;
        }

        public string Proxy => proxy;
        public string Version => version;

        private HttpClient Http => injectedHttp ?? SharedClients.GetOrAdd(proxy, CreateHttpClient);

        /// <summary>Pins the Graph API version ("v25.0"). Returns false (keeping the current one) for anything else.</summary>
        public bool TrySetVersion(string? value)
        {
            string v = (value ?? "").Trim();
            if (!Regex.IsMatch(v, @"^v\d{1,3}\.\d{1,2}$")) return false;
            version = v;
            return true;
        }

        /// <summary>Switches the egress route. Returns false (and keeps the current one) when the value is not a usable proxy URL.</summary>
        public bool TrySetProxy(string? value, out string? error)
        {
            string normalized = (value ?? string.Empty).Trim();
            if (normalized.Length > 0 && !TryParseProxy(normalized, out _, out error)) return false;
            error = null;
            proxy = normalized;
            return true;
        }

        internal static bool TryParseProxy(string value, out WebProxy? webProxy, out string? error)
        {
            webProxy = null;
            error = null;
            string text = value.Contains("://") ? value : "http://" + value;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)
                || uri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
            {
                error = "Expected http://host:port, https://host:port or socks5://host:port (optionally user:pass@host).";
                return false;
            }
            webProxy = new WebProxy(new UriBuilder(uri.Scheme, uri.Host, uri.Port).Uri);
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                string[] parts = uri.UserInfo.Split(':', 2);
                webProxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
            }
            return true;
        }

        private static HttpClient CreateHttpClient(string proxy)
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(20),
            };
            if (proxy.Length > 0 && TryParseProxy(proxy, out var webProxy, out _))
            {
                handler.Proxy = webProxy;
                handler.UseProxy = true;
            }
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        private string Graph(string path) => $"{GraphHost}/{version}/{path.TrimStart('/')}";

        // ─────────────────────────────── OAuth ───────────────────────────────

        public string BuildAuthorizeUrl(string appId, string redirectUri, string state, bool forceReauth)
        {
            var query = new List<KeyValuePair<string, string>>
            {
                new("client_id", appId),
                new("redirect_uri", redirectUri),
                new("response_type", "code"),
                new("scope", string.Join(",", Scopes)),
                new("state", state),
            };
            if (forceReauth) query.Add(new("force_reauth", "true"));
            return AuthorizeEndpoint + "?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        }

        public async Task<IgShortLivedToken> ExchangeCodeAsync(string appId, string appSecret, string redirectUri, string code, CancellationToken ct)
        {
            // Instagram appends "#_" to the redirect; it is not part of the code.
            string cleanCode = code.Trim();
            int hash = cleanCode.IndexOf('#');
            if (hash >= 0) cleanCode = cleanCode[..hash];
            var form = new List<KeyValuePair<string, string>>
            {
                new("client_id", appId),
                new("client_secret", appSecret),
                new("grant_type", "authorization_code"),
                new("redirect_uri", redirectUri),
                new("code", cleanCode),
            };
            var json = await SendAsync(HttpMethod.Post, $"{OAuthHost}/oauth/access_token", form, "oauth/access_token", ct);
            var data = json["data"] is JArray arr && arr.Count > 0 ? arr[0] : json;
            var token = new IgShortLivedToken
            {
                AccessToken = Str(data["access_token"]) ?? "",
                UserId = Str(data["user_id"]),
            };
            if (data["permissions"] is JArray perms) token.Permissions = perms.Select(p => p.ToString()).ToList();
            else if (Str(data["permissions"]) is string permText) token.Permissions = permText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (token.AccessToken.Length == 0)
                throw new InstagramApiException(IgErrorKind.BadRequest, 200, null, null, "Instagram's token response had no access_token.", "oauth/access_token");
            return token;
        }

        public async Task<IgLongLivedToken> ExchangeForLongLivedAsync(string appSecret, string shortLivedToken, CancellationToken ct)
        {
            var query = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "ig_exchange_token"),
                new("client_secret", appSecret),
                new("access_token", shortLivedToken),
            };
            var json = await SendAsync(HttpMethod.Get, $"{GraphHost}/access_token?{Encode(query)}", null, "oauth/exchange", ct);
            return ParseLongLived(json, "oauth/exchange");
        }

        public async Task<IgLongLivedToken> RefreshLongLivedAsync(string token, CancellationToken ct)
        {
            var query = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "ig_refresh_token"),
                new("access_token", token),
            };
            var json = await SendAsync(HttpMethod.Get, $"{GraphHost}/refresh_access_token?{Encode(query)}", null, "oauth/refresh", ct);
            return ParseLongLived(json, "oauth/refresh");
        }

        private static IgLongLivedToken ParseLongLived(JObject json, string label)
        {
            var token = new IgLongLivedToken
            {
                AccessToken = Str(json["access_token"]) ?? "",
                TokenType = Str(json["token_type"]),
                ExpiresInSeconds = Long(json["expires_in"]) ?? 0,
            };
            if (token.AccessToken.Length == 0)
                throw new InstagramApiException(IgErrorKind.BadRequest, 200, null, null, "Instagram's token response had no access_token.", label);
            return token;
        }

        // ─────────────────────────────── Profile ───────────────────────────────

        public async Task<IgProfile> GetProfileAsync(string token, CancellationToken ct)
        {
            JObject json;
            if (extendedProfileFields)
            {
                try
                {
                    json = await GetAsync(token, "me", BasicProfileFields + ",biography,website", "me", ct);
                    return ParseProfile(json);
                }
                catch (InstagramApiException ex) when (ex.Kind == IgErrorKind.BadRequest && ex.Message.Contains("field", StringComparison.OrdinalIgnoreCase))
                {
                    extendedProfileFields = false; // this API version does not expose biography/website to Instagram Login
                }
            }
            json = await GetAsync(token, "me", BasicProfileFields, "me", ct);
            return ParseProfile(json);
        }

        internal static IgProfile ParseProfile(JObject json)
        {
            JToken p = json["data"] is JArray arr && arr.Count > 0 ? arr[0] : json;
            return new IgProfile
            {
                Id = Str(p["id"]),
                UserId = Str(p["user_id"]),
                Username = Str(p["username"]) ?? "",
                Name = Str(p["name"]),
                AccountType = Str(p["account_type"]),
                ProfilePictureUrl = Str(p["profile_picture_url"]),
                FollowersCount = Long(p["followers_count"]),
                FollowsCount = Long(p["follows_count"]),
                MediaCount = Long(p["media_count"]),
                Biography = Str(p["biography"]),
                Website = Str(p["website"]),
            };
        }

        // ─────────────────────────────── Publishing ───────────────────────────────

        public async Task<string> CreateContainerAsync(string token, string igUserId, IgContainerRequest r, CancellationToken ct)
        {
            var form = new List<KeyValuePair<string, string>> { new("access_token", token) };
            void Add(string name, string? value) { if (!string.IsNullOrEmpty(value)) form.Add(new(name, value)); }
            Add("media_type", r.MediaType);
            Add("image_url", r.ImageUrl);
            Add("video_url", r.VideoUrl);
            Add("caption", r.Caption);
            if (r.IsCarouselItem == true) Add("is_carousel_item", "true");
            if (r.Children.Count > 0) Add("children", string.Join(",", r.Children));
            if (r.ShareToFeed.HasValue) Add("share_to_feed", r.ShareToFeed.Value ? "true" : "false");
            if (r.ThumbOffsetMs.HasValue) Add("thumb_offset", r.ThumbOffsetMs.Value.ToString(CultureInfo.InvariantCulture));
            Add("cover_url", r.CoverUrl);
            Add("audio_name", r.AudioName);
            Add("alt_text", r.AltText);
            if (r.Collaborators.Count > 0) Add("collaborators", JsonConvert.SerializeObject(r.Collaborators));
            Add("location_id", r.LocationId);
            if (!string.IsNullOrEmpty(r.TrialGraduationStrategy))
                Add("trial_params", JsonConvert.SerializeObject(new { graduation_strategy = r.TrialGraduationStrategy }));
            if (r.IsAiGenerated == true) Add("is_ai_generated", "true");

            var json = await SendAsync(HttpMethod.Post, Graph($"{igUserId}/media"), form, "media/create", ct, ContainerTimeout);
            return Str(json["id"]) ?? throw new InstagramApiException(IgErrorKind.ServerError, 200, null, null, "Instagram created no container id.", "media/create");
        }

        public async Task<IgContainerStatus> GetContainerStatusAsync(string token, string containerId, CancellationToken ct)
        {
            var json = await GetAsync(token, containerId, "id,status_code,status", "container/status", ct);
            return new IgContainerStatus
            {
                Id = Str(json["id"]) ?? containerId,
                StatusCode = (Str(json["status_code"]) ?? "").ToUpperInvariant(),
                Status = Str(json["status"]),
            };
        }

        public async Task<string> PublishContainerAsync(string token, string igUserId, string containerId, CancellationToken ct)
        {
            var form = new List<KeyValuePair<string, string>> { new("access_token", token), new("creation_id", containerId) };
            var json = await SendAsync(HttpMethod.Post, Graph($"{igUserId}/media_publish"), form, "media/publish", ct, ContainerTimeout);
            return Str(json["id"]) ?? throw new InstagramApiException(IgErrorKind.ServerError, 200, null, null, "Instagram published without returning a media id.", "media/publish") { Ambiguous = true };
        }

        public async Task<IgPublishingLimit> GetPublishingLimitAsync(string token, string igUserId, CancellationToken ct)
        {
            var json = await GetAsync(token, $"{igUserId}/content_publishing_limit", "quota_usage,config", "publishing-limit", ct);
            JToken d = json["data"] is JArray arr && arr.Count > 0 ? arr[0] : json;
            return new IgPublishingLimit
            {
                QuotaUsage = (int)(Long(d["quota_usage"]) ?? 0),
                QuotaTotal = (int?)Long(d["config"]?["quota_total"]),
                QuotaDurationSeconds = (int?)Long(d["config"]?["quota_duration"]),
            };
        }

        // ─────────────────────────────── Media ───────────────────────────────

        public async Task<IgMediaPage> GetMediaPageAsync(string token, string igUserId, string? after, int limit, CancellationToken ct)
        {
            var extra = new List<KeyValuePair<string, string>> { new("limit", Math.Clamp(limit, 1, 100).ToString()) };
            if (!string.IsNullOrEmpty(after)) extra.Add(new("after", after));
            var json = await GetAsync(token, $"{igUserId}/media", MediaFields, "media/list", ct, extra);
            var page = new IgMediaPage();
            if (json["data"] is JArray items)
                foreach (var item in items) page.Items.Add(ParseMedia(item));
            page.After = json["paging"]?["next"] != null ? Str(json["paging"]?["cursors"]?["after"]) : null;
            return page;
        }

        public async Task<IgMediaItem> GetMediaItemAsync(string token, string mediaId, CancellationToken ct) =>
            ParseMedia(await GetAsync(token, mediaId, MediaFields, "media/get", ct));

        internal static IgMediaItem ParseMedia(JToken m) => new()
        {
            Id = Str(m["id"]) ?? "",
            Caption = Str(m["caption"]),
            MediaType = Str(m["media_type"]),
            MediaProductType = Str(m["media_product_type"]),
            MediaUrl = Str(m["media_url"]),
            ThumbnailUrl = Str(m["thumbnail_url"]),
            Permalink = Str(m["permalink"]),
            Shortcode = Str(m["shortcode"]),
            TimestampUtc = ParseTime(Str(m["timestamp"])) ?? DateTime.MinValue,
            LikeCount = Long(m["like_count"]),
            CommentsCount = Long(m["comments_count"]),
            IsSharedToFeed = Bool(m["is_shared_to_feed"]),
        };

        public async Task<IgInsightValues> GetMediaInsightsAsync(string token, string mediaId, IReadOnlyCollection<string> metrics, CancellationToken ct)
        {
            var extra = new List<KeyValuePair<string, string>> { new("metric", string.Join(",", metrics)) };
            var json = await GetAsync(token, $"{mediaId}/insights", null, "media/insights", ct, extra);
            return ParseInsights(json);
        }

        public async Task DeleteMediaAsync(string token, string mediaId, CancellationToken ct)
        {
            await SendAsync(HttpMethod.Delete, Graph(mediaId) + "?" + Encode(new[] { new KeyValuePair<string, string>("access_token", token) }), null, "media/delete", ct);
        }

        // ─────────────────────────────── Account insights ───────────────────────────────

        public async Task<IgInsightValues> GetAccountTotalsAsync(string token, string igUserId, IReadOnlyCollection<string> metrics, DateTime sinceUtc, DateTime untilUtc, string? breakdown, CancellationToken ct)
        {
            var extra = new List<KeyValuePair<string, string>>
            {
                new("metric", string.Join(",", metrics)),
                new("period", "day"),
                new("metric_type", "total_value"),
                new("since", ToUnix(sinceUtc).ToString(CultureInfo.InvariantCulture)),
                new("until", ToUnix(untilUtc).ToString(CultureInfo.InvariantCulture)),
            };
            if (!string.IsNullOrEmpty(breakdown)) extra.Add(new("breakdown", breakdown));
            var json = await GetAsync(token, $"{igUserId}/insights", null, "account/insights", ct, extra);
            return ParseInsights(json);
        }

        public async Task<IgInsightValues> GetFollowerDemographicsAsync(string token, string igUserId, string breakdown, CancellationToken ct)
        {
            var extra = new List<KeyValuePair<string, string>>
            {
                new("metric", "follower_demographics"),
                new("period", "lifetime"),
                new("metric_type", "total_value"),
                new("breakdown", breakdown),
            };
            try
            {
                return ParseInsights(await GetAsync(token, $"{igUserId}/insights", null, "account/demographics", ct, extra));
            }
            catch (InstagramApiException ex) when (ex.Kind == IgErrorKind.BadRequest && ex.Message.Contains("timeframe", StringComparison.OrdinalIgnoreCase))
            {
                extra.Add(new("timeframe", "this_month"));
                return ParseInsights(await GetAsync(token, $"{igUserId}/insights", null, "account/demographics", ct, extra));
            }
        }

        public async Task<List<(DateTime EndTimeUtc, Dictionary<int, double> ByHour)>> GetOnlineFollowersAsync(string token, string igUserId, DateTime sinceUtc, DateTime untilUtc, CancellationToken ct)
        {
            var extra = new List<KeyValuePair<string, string>>
            {
                new("metric", "online_followers"),
                new("period", "lifetime"),
                new("since", ToUnix(sinceUtc).ToString(CultureInfo.InvariantCulture)),
                new("until", ToUnix(untilUtc).ToString(CultureInfo.InvariantCulture)),
            };
            var json = await GetAsync(token, $"{igUserId}/insights", null, "account/online-followers", ct, extra);
            var result = new List<(DateTime, Dictionary<int, double>)>();
            if (json["data"] is not JArray data) return result;
            foreach (var metric in data)
            {
                if (metric["values"] is not JArray values) continue;
                foreach (var v in values)
                {
                    if (v["value"] is not JObject byHour) continue;
                    var hours = new Dictionary<int, double>();
                    foreach (var prop in byHour.Properties())
                        if (int.TryParse(prop.Name, out var hour) && hour is >= 0 and < 24 && Double(prop.Value) is double count)
                            hours[hour] = count;
                    if (hours.Count > 0) result.Add((ParseTime(Str(v["end_time"])) ?? DateTime.MinValue, hours));
                }
            }
            return result;
        }

        /// <summary>
        /// Reads an insights response: total_value.value when present (with its breakdowns), otherwise the
        /// last numeric entry of values[]. Metrics Instagram had no data for are simply absent.
        /// </summary>
        internal static IgInsightValues ParseInsights(JObject json)
        {
            var result = new IgInsightValues();
            if (json["data"] is not JArray data) return result;
            foreach (var metric in data)
            {
                string? name = Str(metric["name"]);
                if (string.IsNullOrEmpty(name)) continue;
                var total = metric["total_value"];
                if (total != null)
                {
                    if (Double(total["value"]) is double tv) result.Values[name] = tv;
                    if (total["breakdowns"] is JArray breakdowns)
                    {
                        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        foreach (var b in breakdowns)
                        {
                            if (b["results"] is not JArray results) continue;
                            foreach (var r in results)
                            {
                                string key = r["dimension_values"] is JArray dims ? string.Join("|", dims.Select(d => d.ToString())) : "";
                                if (Double(r["value"]) is double bv) map[key] = map.TryGetValue(key, out var existing) ? existing + bv : bv;
                            }
                        }
                        if (map.Count > 0) result.Breakdowns[name] = map;
                    }
                    continue;
                }
                if (metric["values"] is JArray values)
                {
                    for (int i = values.Count - 1; i >= 0; i--)
                    {
                        if (Double(values[i]["value"]) is double v) { result.Values[name] = v; break; }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// When Instagram refuses a metric for a media type ("…metric[x] must be one of the following values: a, b"
        /// or "does not support the x metric"), names the refused metrics among those requested.
        /// </summary>
        public static List<string> UnsupportedMetricsIn(string message, IEnumerable<string> requested)
        {
            var refused = new List<string>();
            string m = message ?? "";
            var allowed = Regex.Match(m, @"must be one of the following values:\s*([a-z0-9_,\s]+)", RegexOptions.IgnoreCase);
            if (allowed.Success)
            {
                var ok = new HashSet<string>(allowed.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
                refused.AddRange(requested.Where(r => !ok.Contains(r)));
                return refused;
            }
            foreach (string r in requested)
                if (Regex.IsMatch(m, $@"\b{Regex.Escape(r)}\b", RegexOptions.IgnoreCase)
                    && (m.Contains("not support", StringComparison.OrdinalIgnoreCase) || m.Contains("invalid", StringComparison.OrdinalIgnoreCase) || m.Contains("not available", StringComparison.OrdinalIgnoreCase)))
                    refused.Add(r);
            return refused;
        }

        // ─────────────────────────────── Comments ───────────────────────────────

        public async Task<List<IgComment>> GetCommentsAsync(string token, string mediaId, int limit, CancellationToken ct)
        {
            var extra = new List<KeyValuePair<string, string>> { new("limit", Math.Clamp(limit, 1, 100).ToString()) };
            var json = await GetAsync(token, $"{mediaId}/comments",
                "id,text,timestamp,username,like_count,hidden,replies{id,text,timestamp,username,like_count,hidden}", "media/comments", ct, extra);
            var list = new List<IgComment>();
            if (json["data"] is JArray items)
                foreach (var c in items) list.Add(ParseComment(c, null));
            return list;
        }

        private static IgComment ParseComment(JToken c, string? parentId)
        {
            var comment = new IgComment
            {
                Id = Str(c["id"]) ?? "",
                Text = Str(c["text"]),
                Username = Str(c["username"]),
                TimestampUtc = ParseTime(Str(c["timestamp"])) ?? DateTime.MinValue,
                LikeCount = Long(c["like_count"]),
                Hidden = Bool(c["hidden"]) ?? false,
                ParentId = parentId,
            };
            if (c["replies"]?["data"] is JArray replies)
                foreach (var r in replies) comment.Replies.Add(ParseComment(r, comment.Id));
            return comment;
        }

        public async Task<string> CreateCommentAsync(string token, string mediaId, string message, CancellationToken ct)
        {
            var form = new List<KeyValuePair<string, string>> { new("access_token", token), new("message", message) };
            var json = await SendAsync(HttpMethod.Post, Graph($"{mediaId}/comments"), form, "comment/create", ct);
            return Str(json["id"]) ?? "";
        }

        public async Task<string> ReplyToCommentAsync(string token, string commentId, string message, CancellationToken ct)
        {
            var form = new List<KeyValuePair<string, string>> { new("access_token", token), new("message", message) };
            var json = await SendAsync(HttpMethod.Post, Graph($"{commentId}/replies"), form, "comment/reply", ct);
            return Str(json["id"]) ?? "";
        }

        public async Task SetCommentHiddenAsync(string token, string commentId, bool hidden, CancellationToken ct)
        {
            var form = new List<KeyValuePair<string, string>> { new("access_token", token), new("hide", hidden ? "true" : "false") };
            await SendAsync(HttpMethod.Post, Graph(commentId), form, "comment/hide", ct);
        }

        public async Task DeleteCommentAsync(string token, string commentId, CancellationToken ct)
        {
            await SendAsync(HttpMethod.Delete, Graph(commentId) + "?" + Encode(new[] { new KeyValuePair<string, string>("access_token", token) }), null, "comment/delete", ct);
        }

        // ─────────────────────────────── Plumbing ───────────────────────────────

        private Task<JObject> GetAsync(string token, string path, string? fields, string label, CancellationToken ct, IEnumerable<KeyValuePair<string, string>>? extra = null)
        {
            var query = new List<KeyValuePair<string, string>>();
            if (!string.IsNullOrEmpty(fields)) query.Add(new("fields", fields));
            if (extra != null) query.AddRange(extra);
            query.Add(new("access_token", token));
            return SendAsync(HttpMethod.Get, Graph(path) + "?" + Encode(query), null, label, ct);
        }

        private static string Encode(IEnumerable<KeyValuePair<string, string>> pairs) =>
            string.Join("&", pairs.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        private async Task<JObject> SendAsync(HttpMethod method, string url, List<KeyValuePair<string, string>>? form, string label,
            CancellationToken ct, TimeSpan? timeout = null)
        {
            onCall?.Invoke(label);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (form != null) request.Content = new FormUrlEncodedContent(form);

            bool mutating = method != HttpMethod.Get;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? RequestTimeout);
            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InstagramApiException(IgErrorKind.Timeout, 0, null, null, $"{label} timed out.", label) { Ambiguous = mutating };
            }
            catch (HttpRequestException ex)
            {
                throw new InstagramApiException(IgErrorKind.Network, 0, null, null, $"{label}: could not reach Instagram ({ex.Message}).", label, ex) { Ambiguous = mutating };
            }

            using (response)
            {
                var usage = ReadUsage(response.Headers, Clock());
                if (usage != null) onUsage?.Invoke(usage);
                string body = "";
                try { body = await response.Content.ReadAsStringAsync(cts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new InstagramApiException(IgErrorKind.Timeout, (int)response.StatusCode, null, null, $"{label} timed out reading the response.", label) { Ambiguous = mutating };
                }

                JObject? json = null;
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try { json = JToken.Parse(body) as JObject; } catch (JsonException) { json = null; }
                }

                int status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode && json != null && json["error"] == null) return json;
                if (response.IsSuccessStatusCode && json == null)
                {
                    // DELETE/hide answer {"success":true}; anything non-JSON on success is still a success for those.
                    if (mutating) return new JObject();
                    throw new InstagramApiException(IgErrorKind.ServerError, status, null, null, $"{label}: Instagram answered with something that is not JSON.", label);
                }
                throw BuildError(status, json, body, label, mutating, usage);
            }
        }

        private InstagramApiException BuildError(int status, JObject? json, string body, string label, bool mutating, IgUsageHeaders? usage)
        {
            int? code = null, subcode = null;
            string? message = null, traceId = null;
            var err = json?["error"];
            if (err is JObject e)
            {
                code = (int?)Long(e["code"]);
                subcode = (int?)Long(e["error_subcode"]);
                string? userMsg = Str(e["error_user_msg"]);
                message = Str(e["message"]);
                if (!string.IsNullOrWhiteSpace(userMsg) && !string.Equals(userMsg, message, StringComparison.Ordinal)) message = $"{message} — {userMsg}";
                traceId = Str(e["fbtrace_id"]);
            }
            else if (json != null)
            {
                // api.instagram.com/oauth style: {"error_type": "...", "code": 400, "error_message": "..."}
                message = Str(json["error_message"]) ?? Str(json["error_description"]) ?? (err != null ? err.ToString() : null);
                code = (int?)Long(json["code"]);
                if (code == status) code = null;
            }
            message ??= status >= 500 ? "Instagram had a server error." : Clip(body, 200);
            if (string.IsNullOrWhiteSpace(message)) message = $"HTTP {status}";

            var kind = InstagramApiException.Classify(status, code, subcode, message);
            DateTime? retryAfter = null;
            if (usage?.RegainAccessMinutes is > 0) retryAfter = Clock().AddMinutes(usage.RegainAccessMinutes.Value);
            else if (kind == IgErrorKind.RateLimited) retryAfter = Clock().AddMinutes(15);
            return new InstagramApiException(kind, status, code, subcode, $"{label}: {message}", label)
            {
                FbTraceId = traceId,
                RetryAfterUtc = retryAfter,
                Ambiguous = mutating && (status >= 500 || kind is IgErrorKind.ServerError or IgErrorKind.Unknown),
            };
        }

        /// <summary>Reads X-App-Usage / X-Business-Use-Case-Usage (JSON) into percentages.</summary>
        internal static IgUsageHeaders? ReadUsage(HttpResponseHeaders headers, DateTime nowUtc)
        {
            IgUsageHeaders? usage = null;
            if (headers.TryGetValues("x-app-usage", out var app))
            {
                try
                {
                    var o = JObject.Parse(app.First());
                    usage ??= new IgUsageHeaders { ObservedUtc = nowUtc };
                    usage.AppUsagePercent = new[] { Double(o["call_count"]), Double(o["total_time"]), Double(o["total_cputime"]) }.Max() ?? 0;
                }
                catch { }
            }
            if (headers.TryGetValues("x-business-use-case-usage", out var buc))
            {
                try
                {
                    var o = JObject.Parse(buc.First());
                    usage ??= new IgUsageHeaders { ObservedUtc = nowUtc };
                    foreach (var prop in o.Properties())
                    {
                        if (prop.Value is not JArray entries) continue;
                        foreach (var entry in entries)
                        {
                            double pct = new[] { Double(entry["call_count"]), Double(entry["total_time"]), Double(entry["total_cputime"]) }.Max() ?? 0;
                            usage.AccountUsagePercent = Math.Max(usage.AccountUsagePercent ?? 0, pct);
                            if (Long(entry["estimated_time_to_regain_access"]) is long regain && regain > (usage.RegainAccessMinutes ?? 0))
                                usage.RegainAccessMinutes = (int)regain;
                        }
                    }
                }
                catch { }
            }
            return usage;
        }

        // ─────────────────────────────── JSON helpers ───────────────────────────────

        internal static string? Str(JToken? t) => t == null || t.Type == JTokenType.Null ? null
            : t.Type == JTokenType.String ? (string?)t : t.Type is JTokenType.Integer or JTokenType.Float or JTokenType.Boolean ? t.ToString() : null;

        internal static long? Long(JToken? t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer) return t.Value<long>();
            if (t.Type == JTokenType.Float) return (long)Math.Round(t.Value<double>());
            return long.TryParse(t.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        internal static double? Double(JToken? t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type is JTokenType.Integer or JTokenType.Float) return t.Value<double>();
            return double.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        internal static bool? Bool(JToken? t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Boolean) return t.Value<bool>();
            string s = t.ToString();
            return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" ? true
                : s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0" ? false : null;
        }

        internal static DateTime? ParseTime(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            // Instagram uses "2026-10-09T18:04:11+0000", which DateTimeOffset parses once the offset has a colon.
            string v = Regex.Replace(value.Trim(), @"([+-]\d{2})(\d{2})$", "$1:$2");
            return DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;
        }

        internal static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        private static string Clip(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

        /// <summary>The public profile URL for a username.</summary>
        public static string ProfileUrl(string username) => $"https://www.instagram.com/{Uri.EscapeDataString(username.TrimStart('@'))}/";
    }
}
