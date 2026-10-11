using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniGram.Api;
using System.Net;
using System.Text;
using Xunit;

namespace Omnipotent.Tests.OmniGram
{
    /// <summary>Answers each request with the next scripted response and remembers what was sent.</summary>
    internal sealed class ScriptedHandler : HttpMessageHandler
    {
        public Queue<Func<HttpRequestMessage, HttpResponseMessage>> Responses { get; } = new();
        public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            if (Responses.Count == 0) return Json(HttpStatusCode.OK, "{}");
            return Responses.Dequeue()(request);
        }

        public static HttpResponseMessage Json(HttpStatusCode code, string json, params (string Name, string Value)[] headers)
        {
            var response = new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
            return response;
        }

        public void Reply(HttpStatusCode code, string json, params (string, string)[] headers) => Responses.Enqueue(_ => Json(code, json, headers));
    }

    public class InstagramProtocolTests
    {
        private static (InstagramGraphClient Client, ScriptedHandler Handler) NewClient(Action<IgUsageHeaders>? onUsage = null)
        {
            var handler = new ScriptedHandler();
            var client = new InstagramGraphClient(new HttpClient(handler), onUsage: onUsage) { RequestTimeout = TimeSpan.FromSeconds(5) };
            return (client, handler);
        }

        [Theory]
        [InlineData(400, 190, 463, IgErrorKind.TokenInvalid)]
        [InlineData(400, 190, null, IgErrorKind.TokenInvalid)]
        [InlineData(400, 4, 2207051, IgErrorKind.SpamRestricted)]
        [InlineData(400, 4, null, IgErrorKind.RateLimited)]
        [InlineData(400, 17, null, IgErrorKind.RateLimited)]
        [InlineData(400, 9, 2207042, IgErrorKind.DailyPublishLimit)]
        [InlineData(400, 25, 2207050, IgErrorKind.AccountRestricted)]
        [InlineData(400, 9004, 2207052, IgErrorKind.MediaFetchFailed)]
        [InlineData(400, -2, 2207003, IgErrorKind.MediaFetchTimeout)]
        [InlineData(400, 24, 2207008, IgErrorKind.ContainerExpired)]
        [InlineData(400, -2, 2207020, IgErrorKind.ContainerExpired)]
        [InlineData(400, 9007, 2207027, IgErrorKind.NotReady)]
        [InlineData(400, 352, 2207026, IgErrorKind.InvalidMedia)]
        [InlineData(400, 36003, 2207009, IgErrorKind.InvalidMedia)]
        [InlineData(400, 36000, 2207004, IgErrorKind.InvalidMedia)]
        [InlineData(400, 100, 2207028, IgErrorKind.InvalidMedia)]
        [InlineData(400, 1, 2207057, IgErrorKind.InvalidCoverFrame)]
        [InlineData(400, 36004, 2207010, IgErrorKind.InvalidCaption)]
        [InlineData(400, 100, 2207040, IgErrorKind.InvalidCaption)]
        [InlineData(400, -1, 2207001, IgErrorKind.ServerError)]
        [InlineData(400, -1, 2207032, IgErrorKind.ServerError)]
        [InlineData(400, 10, null, IgErrorKind.PermissionDenied)]
        [InlineData(403, 200, null, IgErrorKind.PermissionDenied)]
        [InlineData(400, 100, null, IgErrorKind.BadRequest)]
        [InlineData(502, null, null, IgErrorKind.ServerError)]
        [InlineData(429, null, null, IgErrorKind.RateLimited)]
        public void Classify_follows_metas_error_table(int status, int? code, int? subcode, IgErrorKind expected)
        {
            Assert.Equal(expected, InstagramApiException.Classify(status, code, subcode, "message"));
        }

        [Fact]
        public void Classify_recognises_missing_objects_and_expired_tokens_by_text()
        {
            Assert.Equal(IgErrorKind.NotFound, InstagramApiException.Classify(400, 100, 33, "Unsupported get request. Object with ID '1' does not exist"));
            Assert.Equal(IgErrorKind.TokenInvalid, InstagramApiException.Classify(400, null, null, "Error validating access token: Session has expired"));
        }

        [Fact]
        public void A_container_error_is_classified_from_its_status_text()
        {
            var ex = InstagramApiException.FromContainerError("c1", "Error: Video format is not supported (2207026)");
            Assert.Equal(IgErrorKind.InvalidMedia, ex.Kind);
            Assert.Equal(2207026, ex.Subcode);

            Assert.Equal(IgErrorKind.MediaFetchFailed, InstagramApiException.FromContainerError("c2", "Error: Media download has failed. The media URI doesn't meet our requirements.").Kind);
            Assert.Equal(IgErrorKind.ServerError, InstagramApiException.FromContainerError("c3", null).Kind);
        }

        [Fact]
        public async Task Graph_errors_keep_code_and_subcode_and_never_echo_the_token()
        {
            var (client, handler) = NewClient();
            handler.Reply(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Media download has failed.\",\"type\":\"OAuthException\",\"code\":9004,\"error_subcode\":2207052,\"error_user_msg\":\"The media could not be fetched\",\"fbtrace_id\":\"Axyz\"}}");
            var ex = await Assert.ThrowsAsync<InstagramApiException>(() => client.CreateContainerAsync("SECRET-TOKEN-123", "1784", new IgContainerRequest { MediaType = "REELS", VideoUrl = "https://klive.test/v.mp4" }, CancellationToken.None));
            Assert.Equal(IgErrorKind.MediaFetchFailed, ex.Kind);
            Assert.Equal("9004.2207052", ex.Code);
            Assert.Equal("Axyz", ex.FbTraceId);
            Assert.DoesNotContain("SECRET-TOKEN-123", ex.Message);
            Assert.Contains("The media could not be fetched", ex.Message);
        }

        [Fact]
        public async Task Creating_a_reel_container_sends_the_documented_fields()
        {
            var (client, handler) = NewClient();
            handler.Reply(HttpStatusCode.OK, "{\"id\":\"17890000001\"}");
            string id = await client.CreateContainerAsync("tok", "1784", new IgContainerRequest
            {
                MediaType = "REELS",
                VideoUrl = "https://klive.test/omnigram/public/media?t=a.b.c.d&f=media.mp4",
                Caption = "hello #memes",
                ShareToFeed = false,
                ThumbOffsetMs = 1500,
                Collaborators = new List<string> { "friend" },
                TrialGraduationStrategy = "SS_PERFORMANCE",
                IsAiGenerated = true,
            }, CancellationToken.None);

            Assert.Equal("17890000001", id);
            var (method, url, body) = handler.Requests.Single();
            Assert.Equal(HttpMethod.Post, method);
            Assert.Equal("https://graph.instagram.com/v25.0/1784/media", url);
            var form = System.Web.HttpUtility.ParseQueryString(body);
            Assert.Equal("REELS", form["media_type"]);
            Assert.Equal("https://klive.test/omnigram/public/media?t=a.b.c.d&f=media.mp4", form["video_url"]);
            Assert.Equal("hello #memes", form["caption"]);
            Assert.Equal("false", form["share_to_feed"]);
            Assert.Equal("1500", form["thumb_offset"]);
            Assert.Equal("[\"friend\"]", form["collaborators"]);
            Assert.Equal("{\"graduation_strategy\":\"SS_PERFORMANCE\"}", form["trial_params"]);
            Assert.Equal("true", form["is_ai_generated"]);
            Assert.Equal("tok", form["access_token"]);
            Assert.Null(form["image_url"]);
        }

        [Fact]
        public async Task The_code_exchange_reads_both_response_shapes_and_drops_the_hash_suffix()
        {
            var (client, handler) = NewClient();
            handler.Reply(HttpStatusCode.OK, "{\"data\":[{\"access_token\":\"IGQ-short\",\"user_id\":\"17841400000000001\",\"permissions\":\"instagram_business_basic,instagram_business_content_publish\"}]}");
            var token = await client.ExchangeCodeAsync("123", "secret", "https://klive.dev/omnigram/oauth/callback", "AQBcode#_", CancellationToken.None);
            Assert.Equal("IGQ-short", token.AccessToken);
            Assert.Equal("17841400000000001", token.UserId);
            Assert.Equal(2, token.Permissions.Count);
            var form = System.Web.HttpUtility.ParseQueryString(handler.Requests[0].Body);
            Assert.Equal("AQBcode", form["code"]);
            Assert.Equal("authorization_code", form["grant_type"]);

            handler.Reply(HttpStatusCode.OK, "{\"access_token\":\"flat\",\"user_id\":17841400000000002,\"permissions\":[\"instagram_business_basic\"]}");
            var flat = await client.ExchangeCodeAsync("123", "secret", "https://x", "c", CancellationToken.None);
            Assert.Equal("flat", flat.AccessToken);
            Assert.Equal("17841400000000002", flat.UserId);
        }

        [Fact]
        public async Task OAuth_errors_from_api_instagram_com_are_readable()
        {
            var (client, handler) = NewClient();
            handler.Reply(HttpStatusCode.BadRequest, "{\"error_type\":\"OAuthException\",\"code\":400,\"error_message\":\"Matching code was not found or was already used\"}");
            var ex = await Assert.ThrowsAsync<InstagramApiException>(() => client.ExchangeCodeAsync("1", "s", "https://x", "c", CancellationToken.None));
            Assert.Equal(IgErrorKind.BadRequest, ex.Kind);
            Assert.Contains("Matching code was not found", ex.Message);
        }

        [Fact]
        public async Task Long_lived_exchange_and_refresh_use_the_ig_grant_types()
        {
            var (client, handler) = NewClient();
            handler.Reply(HttpStatusCode.OK, "{\"access_token\":\"long\",\"token_type\":\"bearer\",\"expires_in\":5183944}");
            handler.Reply(HttpStatusCode.OK, "{\"access_token\":\"long2\",\"token_type\":\"bearer\",\"expires_in\":5183000}");
            var a = await client.ExchangeForLongLivedAsync("secret", "short", CancellationToken.None);
            var b = await client.RefreshLongLivedAsync("long", CancellationToken.None);
            Assert.Equal(5183944, a.ExpiresInSeconds);
            Assert.Equal("long2", b.AccessToken);
            Assert.StartsWith("https://graph.instagram.com/access_token?grant_type=ig_exchange_token", handler.Requests[0].Url);
            Assert.StartsWith("https://graph.instagram.com/refresh_access_token?grant_type=ig_refresh_token", handler.Requests[1].Url);
        }

        [Fact]
        public void Profiles_parse_with_or_without_a_data_wrapper()
        {
            var wrapped = InstagramGraphClient.ParseProfile(JObject.Parse("{\"data\":[{\"user_id\":\"1784\",\"username\":\"memepage\",\"followers_count\":12}]}"));
            Assert.Equal("1784", wrapped.PublishingId);
            Assert.Equal(12, wrapped.FollowersCount);
            var flat = InstagramGraphClient.ParseProfile(JObject.Parse("{\"id\":\"9\",\"username\":\"x\",\"account_type\":\"BUSINESS\"}"));
            Assert.Equal("9", flat.PublishingId);
            Assert.Equal("BUSINESS", flat.AccountType);
        }

        [Fact]
        public async Task Profile_reading_falls_back_when_bio_fields_are_not_offered()
        {
            var (client, handler) = NewClient();
            handler.Reply(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"(#100) Tried accessing nonexisting field (biography) on node type (User)\",\"code\":100}}");
            handler.Reply(HttpStatusCode.OK, "{\"user_id\":\"1784\",\"username\":\"memepage\"}");
            var profile = await client.GetProfileAsync("tok", CancellationToken.None);
            Assert.Equal("memepage", profile.Username);
            Assert.Contains("biography", handler.Requests[0].Url);
            Assert.DoesNotContain("biography", handler.Requests[1].Url);
        }

        [Fact]
        public void Insights_read_total_values_series_and_breakdowns()
        {
            var json = JObject.Parse(@"{""data"":[
                {""name"":""views"",""period"":""lifetime"",""values"":[{""value"":1234}]},
                {""name"":""reach"",""total_value"":{""value"":800}},
                {""name"":""follows_and_unfollows"",""total_value"":{""value"":5,""breakdowns"":[{""dimension_keys"":[""follow_type""],""results"":[{""dimension_values"":[""FOLLOWER""],""value"":9},{""dimension_values"":[""NON_FOLLOWER""],""value"":4}]}]}},
                {""name"":""ig_reels_avg_watch_time"",""values"":[{""value"":6500.6}]}]}");
            var v = InstagramGraphClient.ParseInsights(json);
            Assert.Equal(1234, v.Long("views"));
            Assert.Equal(800, v.Long("reach"));
            Assert.Equal(6501, v.Long("ig_reels_avg_watch_time"));
            Assert.Equal(9, v.Breakdowns["follows_and_unfollows"]["FOLLOWER"]);
            Assert.Equal(4, v.Breakdowns["follows_and_unfollows"]["NON_FOLLOWER"]);
        }

        [Fact]
        public void Refused_metrics_are_named_from_either_error_wording()
        {
            var requested = new[] { "views", "reach", "reels_skip_rate", "reposts" };
            var fromList = InstagramGraphClient.UnsupportedMetricsIn("(#100) metric[2] must be one of the following values: views, reach, likes", requested);
            Assert.Equal(new[] { "reels_skip_rate", "reposts" }, fromList);
            var fromText = InstagramGraphClient.UnsupportedMetricsIn("The Media Insights API does not support the reposts metric for this media product type.", requested);
            Assert.Equal(new[] { "reposts" }, fromText);
        }

        [Fact]
        public async Task Usage_headers_are_reported_including_time_to_regain_access()
        {
            IgUsageHeaders? seen = null;
            var (client, handler) = NewClient(u => seen = u);
            handler.Reply(HttpStatusCode.OK, "{\"user_id\":\"1\",\"username\":\"a\"}",
                ("x-app-usage", "{\"call_count\":12,\"total_time\":40,\"total_cputime\":7}"),
                ("x-business-use-case-usage", "{\"1784\":[{\"type\":\"instagram\",\"call_count\":81,\"total_cputime\":3,\"total_time\":5,\"estimated_time_to_regain_access\":12}]}"));
            await client.GetProfileAsync("tok", CancellationToken.None);
            Assert.NotNull(seen);
            Assert.Equal(40, seen!.AppUsagePercent);
            Assert.Equal(81, seen.AccountUsagePercent);
            Assert.Equal(12, seen.RegainAccessMinutes);
            Assert.Equal(81, seen.MaxPercent);
        }

        [Fact]
        public async Task A_timed_out_publish_is_marked_ambiguous_but_a_timed_out_read_is_not()
        {
            var handler = new ScriptedHandler();
            handler.Responses.Enqueue(_ => throw new TaskCanceledException("slow"));
            handler.Responses.Enqueue(_ => throw new TaskCanceledException("slow"));
            var client = new InstagramGraphClient(new HttpClient(handler));
            var write = await Assert.ThrowsAsync<InstagramApiException>(() => client.PublishContainerAsync("t", "1", "c", CancellationToken.None));
            Assert.Equal(IgErrorKind.Timeout, write.Kind);
            Assert.True(write.Ambiguous);
            var read = await Assert.ThrowsAsync<InstagramApiException>(() => client.GetContainerStatusAsync("t", "c", CancellationToken.None));
            Assert.False(read.Ambiguous);
        }

        [Fact]
        public void The_authorize_url_asks_for_every_scope_and_carries_the_state()
        {
            var client = new InstagramGraphClient();
            string url = client.BuildAuthorizeUrl("123", "https://klive.dev/omnigram/oauth/callback", "state-1", forceReauth: true);
            Assert.StartsWith("https://www.instagram.com/oauth/authorize?client_id=123", url);
            Assert.Contains("redirect_uri=https%3A%2F%2Fklive.dev%2Fomnigram%2Foauth%2Fcallback", url);
            Assert.Contains("state=state-1", url);
            Assert.Contains("force_reauth=true", url);
            foreach (string scope in InstagramGraphClient.Scopes) Assert.Contains(scope, Uri.UnescapeDataString(url));
        }

        [Fact]
        public void Instagram_timestamps_parse_with_their_compact_offset()
        {
            var t = InstagramGraphClient.ParseTime("2026-10-09T18:04:11+0000");
            Assert.Equal(new DateTime(2026, 10, 9, 18, 4, 11, DateTimeKind.Utc), t);
            Assert.Equal(DateTimeKind.Utc, t!.Value.Kind);
        }

        [Fact]
        public void Graph_versions_and_proxies_are_validated()
        {
            var client = new InstagramGraphClient();
            Assert.True(client.TrySetVersion("v26.0"));
            Assert.Equal("v26.0", client.Version);
            Assert.False(client.TrySetVersion("latest"));
            Assert.Equal("v26.0", client.Version);
            Assert.True(client.TrySetProxy("socks5://user:pw@proxy.local:1080", out _));
            Assert.False(client.TrySetProxy("not a url", out var error));
            Assert.NotNull(error);
        }
    }
}
