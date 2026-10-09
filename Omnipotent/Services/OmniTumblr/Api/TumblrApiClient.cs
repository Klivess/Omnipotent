using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTumblr.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Omnipotent.Services.OmniTumblr.Api
{
    /// <summary>
    /// A small, owned Tumblr API v2 client (replaces NewTumblrSharp, which sent no User-Agent, used the
    /// legacy post API and surfaced no error subcodes). Every request carries the same User-Agent, as
    /// Tumblr requires; OAuth 1.0a requests are signed with <see cref="TumblrOAuth1"/>, OAuth 2.0 ones
    /// carry a bearer token. Failures become <see cref="TumblrApiException"/> with status + subcode.
    /// </summary>
    public sealed class TumblrApiClient : ITumblrApi
    {
        public const string ApiBase = "https://api.tumblr.com";
        public const string WwwBase = "https://www.tumblr.com";
        public const string UserAgent = "OmniTumblr/2.0 (+https://klive.dev)";
        public const string OAuth2Scopes = "basic write offline_access";

        private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        })
        { Timeout = Timeout.InfiniteTimeSpan });

        private readonly HttpClient http;
        private readonly Action<string>? onCall;
        private readonly Action<TumblrRateLimitHeaders>? onRateHeaders;

        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(40);
        public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromMinutes(15);

        /// <param name="onCall">Invoked once per API request with a short endpoint label (budget accounting).</param>
        /// <param name="onRateHeaders">Invoked when a response carries Tumblr's rate-limit headers.</param>
        public TumblrApiClient(HttpClient? http = null, Action<string>? onCall = null, Action<TumblrRateLimitHeaders>? onRateHeaders = null)
        {
            this.http = http ?? SharedClient.Value;
            this.onCall = onCall;
            this.onRateHeaders = onRateHeaders;
        }

        // ─────────────────────────────── Users ───────────────────────────────

        public async Task<TumblrUserInfo> GetUserInfoAsync(TumblrCredentials creds, CancellationToken ct)
        {
            var response = await SendApiAsync(creds, HttpMethod.Get, "/v2/user/info", null, null, "user/info", ct);
            var user = response["user"] ?? response;
            var info = new TumblrUserInfo
            {
                Name = Str(user["name"]) ?? "",
                Following = Long(user["following"]),
                Likes = Long(user["likes"]),
            };
            if (user["blogs"] is JArray blogs)
            {
                foreach (var b in blogs)
                {
                    info.Blogs.Add(new TumblrBlogRef
                    {
                        Name = Str(b["name"]) ?? "",
                        Uuid = Str(b["uuid"]),
                        Title = Str(b["title"]),
                        Url = Str(b["url"]),
                        Followers = Long(b["followers"]),
                        Primary = Bool(b["primary"]) ?? false,
                        Admin = Bool(b["admin"]),
                        Type = Str(b["type"]),
                    });
                }
            }
            return info;
        }

        public async Task<Dictionary<string, TumblrLimit>> GetUserLimitsAsync(TumblrCredentials creds, CancellationToken ct)
        {
            var response = await SendApiAsync(creds, HttpMethod.Get, "/v2/user/limits", null, null, "user/limits", ct);
            var user = response["user"] ?? response;
            var limits = new Dictionary<string, TumblrLimit>(StringComparer.OrdinalIgnoreCase);
            if (user is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    if (prop.Value is not JObject l) continue;
                    long? reset = Long(l["reset_at"]);
                    limits[prop.Name] = new TumblrLimit
                    {
                        Description = Str(l["description"]),
                        Limit = Long(l["limit"]) ?? 0,
                        Remaining = Long(l["remaining"]) ?? 0,
                        ResetUtc = reset is > 0 ? DateTimeOffset.FromUnixTimeSeconds(reset.Value).UtcDateTime : null,
                    };
                }
            }
            return limits;
        }

        // ─────────────────────────────── Blogs ───────────────────────────────

        public async Task<TumblrBlogInfo> GetBlogInfoAsync(TumblrCredentials creds, string blog, CancellationToken ct)
        {
            var response = await SendApiAsync(creds, HttpMethod.Get, $"/v2/blog/{BlogPath(blog)}/info", null, null, "blog/info", ct, appKey: true);
            var b = response["blog"] ?? response;
            long? updated = Long(b["updated"]);
            return new TumblrBlogInfo
            {
                Name = Str(b["name"]) ?? blog,
                Uuid = Str(b["uuid"]),
                Title = Str(b["title"]),
                Description = Str(b["description"]),
                Url = Str(b["url"]),
                Posts = Long(b["posts"]) ?? Long(b["total_posts"]),
                Likes = Long(b["likes"]),
                UpdatedUtc = updated is > 0 ? DateTimeOffset.FromUnixTimeSeconds(updated.Value).UtcDateTime : null,
            };
        }

        public async Task<long?> GetFollowerCountAsync(TumblrCredentials creds, string blog, CancellationToken ct)
        {
            var response = await SendApiAsync(creds, HttpMethod.Get, $"/v2/blog/{BlogPath(blog)}/followers",
                new List<KeyValuePair<string, string>> { new("limit", "1") }, null, "blog/followers", ct);
            return Long(response["total_users"]) ?? Long(response["users"]?["total_users"]);
        }

        public async Task<TumblrPostsPage> GetPostsAsync(TumblrCredentials creds, string blog, int offset, int limit, CancellationToken ct)
        {
            var query = new List<KeyValuePair<string, string>>
            {
                new("npf", "true"),
                new("limit", Math.Clamp(limit, 1, 20).ToString()),
            };
            if (offset > 0) query.Add(new("offset", offset.ToString()));
            var response = await SendApiAsync(creds, HttpMethod.Get, $"/v2/blog/{BlogPath(blog)}/posts", query, null, "blog/posts", ct, appKey: true);
            var page = new TumblrPostsPage { TotalPosts = Long(response["total_posts"]) };
            if (response["posts"] is JArray posts)
                foreach (var p in posts) page.Posts.Add(ParsePost(p));
            return page;
        }

        internal static TumblrPostSummary ParsePost(JToken p)
        {
            long ts = Long(p["timestamp"]) ?? 0;
            var summary = new TumblrPostSummary
            {
                Id = Str(p["id_string"]) ?? Str(p["id"]) ?? "",
                Slug = Str(p["slug"]),
                BlogName = Str(p["blog_name"]) ?? Str(p["blog"]?["name"]),
                PublishedUtc = ts > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime : DateTime.MinValue,
                NoteCount = Long(p["note_count"]) ?? 0,
                PostUrl = Str(p["post_url"]),
                State = Str(p["state"]),
                Summary = Str(p["summary"]),
            };
            if (p["tags"] is JArray tags)
                summary.Tags = tags.Select(t => Str(t) ?? "").Where(t => t.Length > 0).ToList();

            // NPF posts report type "blocks"; derive a meaningful type from the content blocks.
            string? type = Str(p["type"]);
            if (p["content"] is JArray blocks && blocks.Count > 0)
            {
                var kinds = blocks.Select(b => Str(b["type"])).ToList();
                type = kinds.Contains("video") ? "video"
                    : kinds.Contains("image") ? "photo"
                    : kinds.Contains("audio") ? "audio"
                    : kinds.Contains("link") ? "link"
                    : "text";
                if (string.IsNullOrWhiteSpace(summary.Summary))
                    summary.Summary = blocks.Where(b => Str(b["type"]) == "text").Select(b => Str(b["text"])).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            }
            summary.Type = type;
            if (summary.Summary is { Length: > 280 }) summary.Summary = summary.Summary[..280];
            return summary;
        }

        public async Task<TumblrNotesSummary> GetNotesSummaryAsync(TumblrCredentials creds, string blog, string postId, CancellationToken ct)
        {
            var query = new List<KeyValuePair<string, string>> { new("id", postId), new("mode", "conversation") };
            var response = await SendApiAsync(creds, HttpMethod.Get, $"/v2/blog/{BlogPath(blog)}/notes", query, null, "blog/notes", ct, appKey: true);
            return new TumblrNotesSummary
            {
                TotalNotes = Long(response["total_notes"]) ?? 0,
                TotalLikes = Long(response["total_likes"]),
                TotalReblogs = Long(response["total_reblogs"]),
            };
        }

        public async Task<TumblrNotificationsPage> GetNotificationsAsync(TumblrCredentials creds, string blog, long? before, CancellationToken ct)
        {
            var query = new List<KeyValuePair<string, string>> { new("rollups", "false") };
            if (before is > 0) query.Add(new("before", before.Value.ToString()));
            var response = await SendApiAsync(creds, HttpMethod.Get, $"/v2/blog/{BlogPath(blog)}/notifications", query, null, "blog/notifications", ct);
            var page = new TumblrNotificationsPage();
            if (response["notifications"] is JArray items)
            {
                foreach (var n in items)
                {
                    long ts = Long(n["timestamp"]) ?? 0;
                    page.Items.Add(new TumblrNotification
                    {
                        Id = Str(n["id"]) ?? $"{Str(n["type"])}:{ts}:{Str(n["from_tumblelog_name"])}",
                        Type = Str(n["type"]) ?? "",
                        Utc = ts > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime : DateTime.UtcNow,
                        FromBlog = Str(n["from_tumblelog_name"]),
                        TargetPostId = Str(n["target_post_id"]),
                        Text = Str(n["reply_text"]) ?? Str(n["added_text"]),
                    });
                }
            }
            page.NextBefore = Long(response["_links"]?["next"]?["query_params"]?["before"]);
            return page;
        }

        public async Task<TumblrCreatedPost> CreatePostAsync(TumblrCredentials creds, string blog, NpfPostRequest request, CancellationToken ct)
        {
            foreach (var upload in request.Uploads)
                if (!File.Exists(upload.FilePath))
                    throw new TumblrApiException(TumblrErrorKind.InvalidMedia, 0, null, $"Media file is missing: {upload.FilePath}");

            var response = await SendApiAsync(creds, HttpMethod.Post, $"/v2/blog/{BlogPath(blog)}/posts", null,
                () => TumblrNpf.CreateHttpContent(request), "blog/posts:create", ct,
                timeout: request.Uploads.Count > 0 ? UploadTimeout : RequestTimeout);
            string? id = Str(response["id_string"]) ?? Str(response["id"]);
            if (string.IsNullOrWhiteSpace(id))
                throw new TumblrApiException(TumblrErrorKind.Unknown, 201, null, "Tumblr accepted the post but returned no id.") { Ambiguous = true };
            return new TumblrCreatedPost { Id = id };
        }

        public async Task DeletePostAsync(TumblrCredentials creds, string blog, string postId, CancellationToken ct)
        {
            var form = new List<KeyValuePair<string, string>> { new("id", postId) };
            await SendApiAsync(creds, HttpMethod.Post, $"/v2/blog/{BlogPath(blog)}/post/delete", null,
                () => new FormUrlEncodedContent(form), "blog/post:delete", ct, signedForm: form);
        }

        // ─────────────────────────────── OAuth 1.0a ───────────────────────────────

        public async Task<(string Token, string Secret)> GetOAuth1RequestTokenAsync(string consumerKey, string consumerSecret, string callbackUrl, CancellationToken ct)
        {
            var uri = new Uri(WwwBase + "/oauth/request_token");
            string body = await SendOAuthHandshakeAsync(HttpMethod.Post, uri, consumerKey, consumerSecret, null, null,
                new Dictionary<string, string> { ["oauth_callback"] = callbackUrl }, "oauth/request_token", ct);
            var form = TumblrOAuth1.ParseFormResponse(body);
            if (!form.TryGetValue("oauth_token", out var token) || !form.TryGetValue("oauth_token_secret", out var secret)
                || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(secret))
                throw new TumblrApiException(TumblrErrorKind.Unknown, 200, null, "Tumblr's request-token response had no token: " + Clip(body, 200), "oauth/request_token");
            return (token, secret);
        }

        public async Task<(string Token, string Secret)> GetOAuth1AccessTokenAsync(string consumerKey, string consumerSecret,
            string requestToken, string requestTokenSecret, string verifier, CancellationToken ct)
        {
            var uri = new Uri(WwwBase + "/oauth/access_token");
            var extra = new Dictionary<string, string> { ["oauth_verifier"] = verifier };
            string body;
            try
            {
                body = await SendOAuthHandshakeAsync(HttpMethod.Post, uri, consumerKey, consumerSecret, requestToken, requestTokenSecret, extra, "oauth/access_token", ct);
            }
            catch (TumblrApiException ex) when (ex.Status is 404 or 405)
            {
                // Tumblr documents this endpoint as GET; most clients POST. Accept either.
                body = await SendOAuthHandshakeAsync(HttpMethod.Get, uri, consumerKey, consumerSecret, requestToken, requestTokenSecret, extra, "oauth/access_token", ct);
            }
            var form = TumblrOAuth1.ParseFormResponse(body);
            if (!form.TryGetValue("oauth_token", out var token) || !form.TryGetValue("oauth_token_secret", out var secret)
                || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(secret))
                throw new TumblrApiException(TumblrErrorKind.Unknown, 200, null, "Tumblr's access-token response had no token: " + Clip(body, 200), "oauth/access_token");
            return (token, secret);
        }

        public string BuildOAuth1AuthorizeUrl(string requestToken) =>
            $"{WwwBase}/oauth/authorize?oauth_token={TumblrOAuth1.PercentEncode(requestToken)}";

        private async Task<string> SendOAuthHandshakeAsync(HttpMethod method, Uri uri, string consumerKey, string consumerSecret,
            string? token, string? tokenSecret, IReadOnlyDictionary<string, string> extra, string label, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(method, uri);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Authorization",
                TumblrOAuth1.BuildAuthorizationHeader(method.Method, uri, consumerKey, consumerSecret, token, tokenSecret, null, extra));
            if (method == HttpMethod.Post)
                req.Content = new StringContent(string.Empty, Encoding.UTF8, "application/x-www-form-urlencoded");

            var (status, body, _) = await SendRawAsync(req, label, RequestTimeout, ct);
            if (status is >= 200 and < 300) return body;
            throw new TumblrApiException(TumblrApiException.Classify(status, null, body), status, null,
                $"{label} failed ({status}): {Clip(body, 300)}", label);
        }

        // ─────────────────────────────── OAuth 2.0 ───────────────────────────────

        public string BuildOAuth2AuthorizeUrl(string consumerKey, string state, string redirectUri) =>
            $"{WwwBase}/oauth2/authorize?client_id={TumblrOAuth1.PercentEncode(consumerKey)}&response_type=code" +
            $"&scope={TumblrOAuth1.PercentEncode(OAuth2Scopes)}&state={TumblrOAuth1.PercentEncode(state)}" +
            $"&redirect_uri={TumblrOAuth1.PercentEncode(redirectUri)}";

        public Task<TumblrOAuth2Token> ExchangeOAuth2CodeAsync(string consumerKey, string consumerSecret, string code, string redirectUri, CancellationToken ct) =>
            SendOAuth2TokenRequestAsync(new List<KeyValuePair<string, string>>
            {
                new("grant_type", "authorization_code"),
                new("code", code),
                new("client_id", consumerKey),
                new("client_secret", consumerSecret),
                new("redirect_uri", redirectUri),
            }, "oauth2/token:code", ct);

        public Task<TumblrOAuth2Token> RefreshOAuth2TokenAsync(string consumerKey, string consumerSecret, string refreshToken, CancellationToken ct) =>
            SendOAuth2TokenRequestAsync(new List<KeyValuePair<string, string>>
            {
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken),
                new("client_id", consumerKey),
                new("client_secret", consumerSecret),
            }, "oauth2/token:refresh", ct);

        private async Task<TumblrOAuth2Token> SendOAuth2TokenRequestAsync(List<KeyValuePair<string, string>> form, string label, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase + "/v2/oauth2/token")
            {
                Content = new FormUrlEncodedContent(form),
            };
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var (status, body, _) = await SendRawAsync(req, label, RequestTimeout, ct);

            JToken? root = TryParseJson(body);
            if (status is < 200 or >= 300)
            {
                string message = Str(root?["error_description"]) ?? Str(root?["error"]) ?? ErrorMessage(root) ?? Clip(body, 300);
                throw new TumblrApiException(status is 400 or 401 ? TumblrErrorKind.Unauthorized : TumblrApiException.Classify(status, null, message),
                    status, null, $"{label} failed ({status}): {message}", label);
            }
            var token = root?["response"]?["access_token"] != null ? root["response"]! : root;
            string? access = Str(token?["access_token"]);
            if (string.IsNullOrEmpty(access))
                throw new TumblrApiException(TumblrErrorKind.Unknown, status, null, $"{label}: no access_token in response.", label);
            return new TumblrOAuth2Token
            {
                AccessToken = access,
                RefreshToken = Str(token?["refresh_token"]),
                ExpiresIn = (int)(Long(token?["expires_in"]) ?? 2520),
                Scope = Str(token?["scope"]),
            };
        }

        // ─────────────────────────────── Transport ───────────────────────────────

        private async Task<JToken> SendApiAsync(TumblrCredentials creds, HttpMethod method, string path,
            List<KeyValuePair<string, string>>? query, Func<HttpContent>? content, string label, CancellationToken ct,
            bool appKey = false, List<KeyValuePair<string, string>>? signedForm = null, TimeSpan? timeout = null)
        {
            var parameters = new List<KeyValuePair<string, string>>(query ?? new());
            // Endpoints documented as "API key" accept the consumer key as api_key; adding it to every such
            // call keeps them working whatever the auth mode.
            if (appKey && !string.IsNullOrEmpty(creds.ConsumerKey)) parameters.Add(new("api_key", creds.ConsumerKey));
            string url = ApiBase + path;
            if (parameters.Count > 0)
                url += "?" + string.Join("&", parameters.Select(p => TumblrOAuth1.PercentEncode(p.Key) + "=" + TumblrOAuth1.PercentEncode(p.Value)));
            var uri = new Uri(url);

            using var req = new HttpRequestMessage(method, uri);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (creds.HasUserToken)
            {
                if (creds.Mode == TumblrAuthMode.OAuth2)
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.Token);
                else
                    req.Headers.TryAddWithoutValidation("Authorization", TumblrOAuth1.BuildAuthorizationHeader(
                        method.Method, uri, creds.ConsumerKey, creds.ConsumerSecret, creds.Token, creds.TokenSecret, signedForm));
            }
            if (content != null) req.Content = content();

            var (status, body, headers) = await SendRawAsync(req, label, timeout ?? RequestTimeout, ct);
            CaptureRateHeaders(headers);

            JToken? root = TryParseJson(body);
            int metaStatus = (int?)Long(root?["meta"]?["status"]) ?? status;
            if (status is >= 200 and < 300 && metaStatus is >= 200 and < 300)
                return root?["response"] is JToken r && r.Type != JTokenType.Null ? r : (root ?? new JObject());

            int effective = status is >= 200 and < 300 ? metaStatus : status;
            int? subcode = (int?)Long(root?["errors"]?.FirstOrDefault()?["code"]);
            if (subcode == 0) subcode = null;
            string message = ErrorMessage(root) ?? Str(root?["meta"]?["msg"]) ?? DescribeNonApiBody(body);
            // Tumblr's API always answers with a JSON envelope; a 403 without one is its edge proxy (nginx)
            // turning the request away before the API saw it — not a verdict on the account or the post.
            var kind = root == null && effective == 403
                ? TumblrErrorKind.EdgeBlocked
                : TumblrApiException.Classify(effective, subcode, message);
            DateTime? retryAfter = null;
            if (headers?.RetryAfter?.Delta is TimeSpan delta) retryAfter = DateTime.UtcNow + delta;
            else if (headers?.RetryAfter?.Date is DateTimeOffset date) retryAfter = date.UtcDateTime;
            throw new TumblrApiException(kind, effective, subcode,
                $"{label} failed ({effective}{(subcode is int s ? "." + s : "")}): {message}", label)
            { RetryAfterUtc = retryAfter };
        }

        private async Task<(int Status, string Body, HttpResponseHeaders? Headers)> SendRawAsync(HttpRequestMessage req, string label, TimeSpan timeout, CancellationToken ct)
        {
            onCall?.Invoke(label);
            bool mutating = req.Method != HttpMethod.Get;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                using var response = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
                string body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                // Redirects are not followed (auth headers must never leave api.tumblr.com); report them.
                return ((int)response.StatusCode, body, response.Headers);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new TumblrApiException(TumblrErrorKind.Timeout, 0, null, $"{label} timed out after {timeout.TotalSeconds:0}s.", label, ex) { Ambiguous = mutating };
            }
            catch (HttpRequestException ex)
            {
                throw new TumblrApiException(TumblrErrorKind.Network, 0, null, $"{label} failed: {ex.Message}", label, ex) { Ambiguous = mutating };
            }
            catch (IOException ex)
            {
                throw new TumblrApiException(TumblrErrorKind.Network, 0, null, $"{label} failed: {ex.Message}", label, ex) { Ambiguous = mutating };
            }
        }

        private void CaptureRateHeaders(HttpResponseHeaders? headers)
        {
            if (headers == null || onRateHeaders == null) return;
            long? Header(string name) =>
                headers.TryGetValues(name, out var values) && long.TryParse(values.FirstOrDefault(), out var v) ? v : null;
            var observed = new TumblrRateLimitHeaders
            {
                PerHourLimit = Header("X-Ratelimit-Perhour-Limit"),
                PerHourRemaining = Header("X-Ratelimit-Perhour-Remaining"),
                PerHourResetSeconds = Header("X-Ratelimit-Perhour-Reset"),
                PerDayLimit = Header("X-Ratelimit-Perday-Limit"),
                PerDayRemaining = Header("X-Ratelimit-Perday-Remaining"),
                PerDayResetSeconds = Header("X-Ratelimit-Perday-Reset"),
                ObservedUtc = DateTime.UtcNow,
            };
            if (observed.PerHourRemaining != null || observed.PerDayRemaining != null)
            {
                try { onRateHeaders(observed); } catch { }
            }
        }

        // ─────────────────────────────── Helpers ───────────────────────────────

        /// <summary>Blog identifiers go in the path as-is (names, hostnames or "t:" uuids).</summary>
        internal static string BlogPath(string blog)
        {
            string b = (blog ?? string.Empty).Trim();
            if (b.StartsWith("t:", StringComparison.Ordinal)) return b;
            return Uri.EscapeDataString(NormalizeBlogIdentifier(b));
        }

        /// <summary>
        /// Accepts what people paste — "name", "@name", "name.tumblr.com", "https://name.tumblr.com/",
        /// "https://www.tumblr.com/name" — and returns the identifier the API wants.
        /// </summary>
        public static string NormalizeBlogIdentifier(string input)
        {
            string s = (input ?? string.Empty).Trim().TrimStart('@');
            if (s.StartsWith("t:", StringComparison.Ordinal)) return s;
            if (Uri.TryCreate(s.Contains("://") ? s : "https://" + s, UriKind.Absolute, out var uri) && uri.Host.Contains('.'))
            {
                string host = uri.Host.ToLowerInvariant();
                if (host is "www.tumblr.com" or "tumblr.com")
                {
                    var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    // tumblr.com/{name}/… or the older tumblr.com/blog/view/{name}
                    string first = segments.Length == 0 ? "" : segments[0] == "blog" ? segments[^1] : segments[0];
                    return first.TrimStart('@').ToLowerInvariant();
                }
                if (host.EndsWith(".tumblr.com")) return host[..^".tumblr.com".Length];
                return host; // custom domain hostnames are valid identifiers
            }
            return s.ToLowerInvariant();
        }

        public static string BuildPostUrl(string blogName, string postId) => $"https://www.tumblr.com/{blogName}/{postId}";

        public static string BuildAvatarUrl(string blog, int size = 128) => $"{ApiBase}/v2/blog/{BlogPath(blog)}/avatar/{size}";

        private static JToken? TryParseJson(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            string t = body.TrimStart();
            if (!t.StartsWith('{') && !t.StartsWith('[')) return null;
            try { return JToken.Parse(body); } catch { return null; }
        }

        private static string? ErrorMessage(JToken? root)
        {
            if (root?["errors"] is JArray errors && errors.Count > 0)
            {
                var first = errors[0];
                string? title = Str(first["title"]);
                string? detail = Str(first["detail"]);
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(detail) && !detail.Equals(title, StringComparison.OrdinalIgnoreCase))
                    return $"{title} — {detail}";
                return title ?? detail;
            }
            if (root?["response"] is JObject resp && resp["errors"] is JToken inner)
                return inner.Type == JTokenType.Array ? string.Join("; ", inner.Select(e => e.ToString())) : inner.ToString();
            return null;
        }

        internal static string? Str(JToken? t)
        {
            if (t == null || t.Type is JTokenType.Null or JTokenType.Undefined) return null;
            if (t.Type is JTokenType.Object or JTokenType.Array) return null;
            string s = t.ToString();
            return s;
        }

        internal static long? Long(JToken? t)
        {
            if (t == null || t.Type is JTokenType.Null or JTokenType.Undefined) return null;
            if (t.Type == JTokenType.Integer) return t.Value<long>();
            if (t.Type == JTokenType.Float) return (long)t.Value<double>();
            if (t.Type == JTokenType.Boolean) return t.Value<bool>() ? 1 : 0;
            return long.TryParse(t.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        internal static bool? Bool(JToken? t)
        {
            if (t == null || t.Type is JTokenType.Null or JTokenType.Undefined) return null;
            if (t.Type == JTokenType.Boolean) return t.Value<bool>();
            string s = t.ToString().Trim().ToLowerInvariant();
            return s is "true" or "1" or "yes" or "y" ? true : s is "false" or "0" or "no" or "n" ? false : null;
        }

        /// <summary>A non-JSON error body as one readable line: an HTML page becomes its title, not its markup.</summary>
        internal static string DescribeNonApiBody(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "(empty response)";
            if (!body.TrimStart().StartsWith('<')) return Clip(body.Trim(), 300);
            var title = System.Text.RegularExpressions.Regex.Match(body, @"<title[^>]*>\s*(.*?)\s*</title>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            string head = title.Success && title.Groups[1].Value.Length > 0 ? title.Groups[1].Value : "HTML error page";
            bool nginx = body.Contains("nginx", StringComparison.OrdinalIgnoreCase);
            return $"{Clip(head, 120)} (HTML page from Tumblr's {(nginx ? "nginx " : "")}edge, not an API response)";
        }

        private static string Clip(string? s, int max) => string.IsNullOrEmpty(s) ? "(empty response)" : s.Length <= max ? s : s[..max] + "…";
    }
}
