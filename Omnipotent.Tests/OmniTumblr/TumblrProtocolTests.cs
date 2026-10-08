using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Models;
using System.Net;
using System.Text;

namespace Omnipotent.Tests.OmniTumblr
{
    public class TumblrOAuth1Tests
    {
        [Fact]
        public void Signature_MatchesTheWellKnownHmacSha1Vector()
        {
            // The canonical OAuth 1.0a example (verified independently with Python's hmac).
            var uri = new Uri("https://api.twitter.com/1.1/statuses/update.json?include_entities=true");
            var parameters = new Dictionary<string, string>
            {
                ["status"] = "Hello Ladies + Gentlemen, a signed OAuth request!",
                ["oauth_consumer_key"] = "xvz1evFS4wEEPTGEFPHBog",
                ["oauth_nonce"] = "kYjzVBB8Y0ZFabxSWbWovY3uYSQ2pTgmZeNu2VS4cg",
                ["oauth_signature_method"] = "HMAC-SHA1",
                ["oauth_timestamp"] = "1318622958",
                ["oauth_token"] = "370773112-GmHxMAgYyLbNEtIKZeRNFsMKPR9EyMZeS9weJAEb",
                ["oauth_version"] = "1.0",
            };
            string baseString = TumblrOAuth1.BuildSignatureBaseString("POST", uri, parameters);
            Assert.StartsWith("POST&https%3A%2F%2Fapi.twitter.com%2F1.1%2Fstatuses%2Fupdate.json&include_entities%3Dtrue%26oauth_consumer_key", baseString);
            Assert.EndsWith("status%3DHello%2520Ladies%2520%252B%2520Gentlemen%252C%2520a%2520signed%2520OAuth%2520request%2521", baseString);

            string signature = TumblrOAuth1.ComputeSignature(baseString, "kAcSOqF21Fu85e7zjz7ZN2U4ZRhfV3WpwPAoE3Z7kBw", "LswwdoUaIvS8ltyTt5jkRh4J50vUPVVHtR2YPi5kE");
            Assert.Equal("hCtSmYh+iHYCEqBWrE7C7hYmtUk=", signature);
        }

        [Theory]
        [InlineData("abcXYZ019-._~", "abcXYZ019-._~")]
        [InlineData("a b+c", "a%20b%2Bc")]
        [InlineData("é", "%C3%A9")]
        [InlineData("https://klive.dev/omnitumblr/oauth/callback", "https%3A%2F%2Fklive.dev%2Fomnitumblr%2Foauth%2Fcallback")]
        public void PercentEncode_FollowsRfc3986(string input, string expected) => Assert.Equal(expected, TumblrOAuth1.PercentEncode(input));

        [Fact]
        public void AuthorizationHeader_CarriesEveryProtocolParameterAndAValidSignature()
        {
            var uri = new Uri("https://www.tumblr.com/oauth/request_token");
            string header = TumblrOAuth1.BuildAuthorizationHeader("POST", uri, "ck", "cs", null, null,
                extraOAuthParameters: new Dictionary<string, string> { ["oauth_callback"] = "https://klive.dev/cb" },
                nonce: "n0nce", timestamp: "1700000000");

            Assert.StartsWith("OAuth ", header);
            foreach (string p in new[] { "oauth_consumer_key=\"ck\"", "oauth_nonce=\"n0nce\"", "oauth_signature_method=\"HMAC-SHA1\"",
                         "oauth_timestamp=\"1700000000\"", "oauth_version=\"1.0\"", "oauth_callback=\"https%3A%2F%2Fklive.dev%2Fcb\"" })
                Assert.Contains(p, header);
            Assert.DoesNotContain("oauth_token=", header);

            string expectedBase = TumblrOAuth1.BuildSignatureBaseString("POST", uri, new Dictionary<string, string>
            {
                ["oauth_callback"] = "https://klive.dev/cb", ["oauth_consumer_key"] = "ck", ["oauth_nonce"] = "n0nce",
                ["oauth_signature_method"] = "HMAC-SHA1", ["oauth_timestamp"] = "1700000000", ["oauth_version"] = "1.0",
            });
            string expectedSignature = TumblrOAuth1.PercentEncode(TumblrOAuth1.ComputeSignature(expectedBase, "cs", null));
            Assert.Contains($"oauth_signature=\"{expectedSignature}\"", header);
        }

        [Fact]
        public void TokenResponse_IsParsedFromFormEncoding()
        {
            var form = TumblrOAuth1.ParseFormResponse("oauth_token=abc%2B1&oauth_token_secret=s3cr3t&oauth_callback_confirmed=true");
            Assert.Equal("abc+1", form["oauth_token"]);
            Assert.Equal("s3cr3t", form["oauth_token_secret"]);
        }
    }

    public class TumblrNpfTests
    {
        private static PostMedia Video(string path = @"C:\media\clip.mp4") => new() { Path = path, MimeType = "video/mp4", Width = 720, Height = 1280, DurationSeconds = 12.4 };

        [Fact]
        public void VideoPost_ReferencesTheUploadByIdentifier_AndPutsTheCaptionAfterIt()
        {
            var request = TumblrNpf.Build(new NpfBuildInput
            {
                Kind = PostKind.Video,
                Media = new[] { Video() },
                Caption = "line one\n\nline two",
                Tags = new[] { "#Memes", "funny, cats", "memes" },
                Slug = "line-one-abc123",
            });

            Assert.Equal("published", request.State);
            Assert.Equal("Memes,funny cats", request.Tags);
            var upload = Assert.Single(request.Uploads);
            Assert.Equal("video0", upload.Identifier);
            Assert.Equal("clip.mp4", upload.FileName);

            var json = JObject.Parse(request.ToJson());
            var blocks = (JArray)json["content"]!;
            Assert.Equal(3, blocks.Count);
            Assert.Equal("video", (string?)blocks[0]["type"]);
            Assert.Equal("tumblr", (string?)blocks[0]["provider"]);
            Assert.Equal("video0", (string?)blocks[0]["media"]!["identifier"]);
            Assert.Equal("video/mp4", (string?)blocks[0]["media"]!["type"]);
            Assert.Equal(720, (int?)blocks[0]["media"]!["width"]);
            Assert.Equal(12400, (long?)blocks[0]["duration"]);
            Assert.Equal("line one", (string?)blocks[1]["text"]);
            Assert.Equal("line two", (string?)blocks[2]["text"]);
            Assert.Equal("line-one-abc123", (string?)json["slug"]);
        }

        [Fact]
        public void PhotoPost_GetsOneImageBlockPerImage_WithAltText()
        {
            var request = TumblrNpf.Build(new NpfBuildInput
            {
                Kind = PostKind.Photo,
                Media = new[]
                {
                    new PostMedia { Path = "a.jpg", MimeType = "image/jpeg" },
                    new PostMedia { Path = "b.png", MimeType = "image/png" },
                },
                AltText = "two cats",
                State = TumblrPostState.Draft,
            });
            var blocks = (JArray)JObject.Parse(request.ToJson())["content"]!;
            Assert.Equal(2, blocks.Count);
            Assert.All(blocks, b => Assert.Equal("image", (string?)b["type"]));
            Assert.Equal("image1", (string?)blocks[1]["media"]![0]!["identifier"]);
            Assert.Equal("two cats", (string?)blocks[0]["alt_text"]);
            Assert.Equal("draft", request.State);
        }

        [Fact]
        public void VideoPost_WithoutAVideo_IsRejected() =>
            Assert.Throws<InvalidOperationException>(() => TumblrNpf.Build(new NpfBuildInput { Kind = PostKind.Video, Media = Array.Empty<PostMedia>() }));

        [Fact]
        public void LongText_IsSplitIntoBlocksWithinTheCodePointLimit()
        {
            string text = new string('x', TumblrNpf.MaxTextBlockCodePoints + 10);
            var blocks = TumblrNpf.SplitText(text);
            Assert.Equal(2, blocks.Count);
            Assert.Equal(TumblrNpf.MaxTextBlockCodePoints, blocks[0].Length);
            Assert.Equal(10, blocks[1].Length);
            Assert.Empty(TumblrNpf.SplitText("  \n \n"));
        }

        [Fact]
        public void Tags_AreNormalizedDedupedAndCapped()
        {
            var tags = TumblrNpf.NormalizeTags(Enumerable.Range(0, 40).Select(i => "#tag" + i).Concat(new[] { "TAG1", "  spaced   out  " }));
            Assert.Equal(TumblrNpf.MaxTags, tags.Count);
            Assert.Equal("tag0", tags[0]);
            Assert.DoesNotContain("TAG1", tags);
        }

        [Fact]
        public void Slug_IsReadableAndUnique()
        {
            Assert.Equal("when-the-build-finally-passes-3f9a2c1d", TumblrNpf.MakeSlug("When the build finally passes! 🎉", "3f9a2c1de5b7"));
            Assert.Equal("post-3f9a2c1d", TumblrNpf.MakeSlug("🎉🎉", "3f9a2c1de5b7"));
        }

        [Fact]
        public async Task Multipart_HasTheJsonPartFirst_ThenOnePartPerUpload()
        {
            using var dir = new TempDir();
            string file = dir.File("clip.mp4", 4096);
            var request = TumblrNpf.Build(new NpfBuildInput { Kind = PostKind.Video, Media = new[] { Video(file) }, Caption = "hi" });
            using var content = TumblrNpf.CreateHttpContent(request);
            var multipart = Assert.IsType<MultipartFormDataContent>(content);
            var parts = multipart.ToList();
            Assert.Equal(2, parts.Count);
            Assert.Equal("form-data; name=\"json\"", parts[0].Headers.ContentDisposition!.ToString());
            Assert.Equal("application/json", parts[0].Headers.ContentType!.MediaType);
            Assert.Contains("\"identifier\":\"video0\"", await parts[0].ReadAsStringAsync());
            // Exactly name + filename: no RFC 5987 filename* parameter that some servers choke on.
            Assert.Equal("form-data; name=\"video0\"; filename=\"clip.mp4\"", parts[1].Headers.ContentDisposition!.ToString());
            Assert.Equal("video/mp4", parts[1].Headers.ContentType!.MediaType);
            Assert.Equal(4096, (await parts[1].ReadAsByteArrayAsync()).Length);
        }

        [Fact]
        public void TextOnlyPost_IsPlainJson()
        {
            var request = TumblrNpf.Build(new NpfBuildInput { Kind = PostKind.Text, Title = "Title", Caption = "Body" });
            using var content = TumblrNpf.CreateHttpContent(request);
            Assert.Equal("application/json", content.Headers.ContentType!.MediaType);
            var blocks = (JArray)JObject.Parse(request.ToJson())["content"]!;
            Assert.Equal("heading1", (string?)blocks[0]["subtype"]);
        }
    }

    public class TumblrApiClientTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            public List<(HttpRequestMessage Request, string Body)> Requests { get; } = new();
            public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } =
                _ => Task.FromResult(Json(HttpStatusCode.OK, "{\"meta\":{\"status\":200,\"msg\":\"OK\"},\"response\":{}}"));
            /// <summary>Simulates a slow server; honours cancellation like a real socket would.</summary>
            public TimeSpan Delay { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                Requests.Add((request, body));
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
                return await Respond(request);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static readonly TumblrCredentials OAuth1 = new() { ConsumerKey = "ck", ConsumerSecret = "cs", Mode = TumblrAuthMode.OAuth1, Token = "tok", TokenSecret = "toksec" };
        private static readonly TumblrCredentials OAuth2 = new() { ConsumerKey = "ck", ConsumerSecret = "cs", Mode = TumblrAuthMode.OAuth2, Token = "bearer-token" };

        [Fact]
        public async Task EveryCall_SendsTheConsistentUserAgent_AndTheRightAuthorization()
        {
            var stub = new StubHandler
            {
                Respond = _ => Task.FromResult(Json(HttpStatusCode.OK,
                    "{\"meta\":{\"status\":200},\"response\":{\"user\":{\"name\":\"klives\",\"blogs\":[{\"name\":\"memeblog\",\"uuid\":\"t:x\",\"followers\":12,\"primary\":true}]}}}")),
            };
            var client = new TumblrApiClient(new HttpClient(stub));

            var user = await client.GetUserInfoAsync(OAuth1, CancellationToken.None);
            await client.GetUserInfoAsync(OAuth2, CancellationToken.None);

            Assert.Equal("klives", user.Name);
            var blog = Assert.Single(user.Blogs);
            Assert.Equal(12, blog.Followers);
            Assert.True(blog.Primary);
            Assert.All(stub.Requests, r => Assert.Equal(TumblrApiClient.UserAgent, string.Join(" ", r.Request.Headers.GetValues("User-Agent"))));
            Assert.StartsWith("OAuth ", stub.Requests[0].Request.Headers.GetValues("Authorization").Single());
            Assert.Contains("oauth_token=\"tok\"", stub.Requests[0].Request.Headers.GetValues("Authorization").Single());
            Assert.Equal("Bearer bearer-token", stub.Requests[1].Request.Headers.Authorization!.ToString());
        }

        [Theory]
        [InlineData(403, 8011, TumblrErrorKind.DailyVideoLimit, "403.8011")]
        [InlineData(403, 8010, TumblrErrorKind.VideoTranscoding, "403.8010")]
        [InlineData(403, 8023, TumblrErrorKind.DailyPostLimit, "403.8023")]
        [InlineData(400, 8005, TumblrErrorKind.InvalidMedia, "400.8005")]
        [InlineData(401, 0, TumblrErrorKind.Unauthorized, "401")]
        [InlineData(429, 0, TumblrErrorKind.RateLimited, "429")]
        [InlineData(503, 0, TumblrErrorKind.ServiceUnavailable, "503")]
        public async Task ErrorEnvelopes_AreClassifiedWithTheirSubcodes(int status, int subcode, TumblrErrorKind kind, string code)
        {
            string errors = subcode == 0 ? "[]" : $"[{{\"title\":\"Nope\",\"code\":{subcode},\"detail\":\"Detail text\"}}]";
            var stub = new StubHandler { Respond = _ => Task.FromResult(Json((HttpStatusCode)status, $"{{\"meta\":{{\"status\":{status},\"msg\":\"Err\"}},\"errors\":{errors},\"response\":[]}}")) };
            var client = new TumblrApiClient(new HttpClient(stub));
            var ex = await Assert.ThrowsAsync<TumblrApiException>(() => client.GetUserInfoAsync(OAuth1, CancellationToken.None));
            Assert.Equal(kind, ex.Kind);
            Assert.Equal(code, ex.Code);
            if (subcode != 0) Assert.Contains("Nope — Detail text", ex.Message);
        }

        [Fact]
        public async Task CreatePost_SendsMultipartWithJsonAndMedia_AndReturnsTheId()
        {
            using var dir = new TempDir();
            string file = dir.File("clip.mp4", 1024);
            var stub = new StubHandler { Respond = _ => Task.FromResult(Json(HttpStatusCode.Created, "{\"meta\":{\"status\":201,\"msg\":\"Created\"},\"response\":{\"id\":\"1234567891234567\"}}")) };
            var client = new TumblrApiClient(new HttpClient(stub));
            var request = TumblrNpf.Build(new NpfBuildInput { Kind = PostKind.Video, Media = new[] { new PostMedia { Path = file, MimeType = "video/mp4" } }, Caption = "cap" });

            var created = await client.CreatePostAsync(OAuth1, "t:abc", request, CancellationToken.None);

            Assert.Equal("1234567891234567", created.Id);
            var (sent, body) = Assert.Single(stub.Requests);
            Assert.Equal(HttpMethod.Post, sent.Method);
            Assert.Equal("https://api.tumblr.com/v2/blog/t:abc/posts", sent.RequestUri!.ToString());
            Assert.Contains("name=\"json\"", body);
            Assert.Contains("name=\"video0\"", body);
        }

        [Fact]
        public async Task CreatePost_ATimeout_IsReportedAsAmbiguous()
        {
            using var dir = new TempDir();
            string file = dir.File("clip.mp4");
            var stub = new StubHandler { Delay = TimeSpan.FromSeconds(30) };
            var client = new TumblrApiClient(new HttpClient(stub)) { UploadTimeout = TimeSpan.FromMilliseconds(150) };
            var request = TumblrNpf.Build(new NpfBuildInput { Kind = PostKind.Video, Media = new[] { new PostMedia { Path = file, MimeType = "video/mp4" } } });
            var ex = await Assert.ThrowsAsync<TumblrApiException>(() => client.CreatePostAsync(OAuth1, "memeblog", request, CancellationToken.None));
            Assert.Equal(TumblrErrorKind.Timeout, ex.Kind);
            Assert.True(ex.Ambiguous);
        }

        [Fact]
        public async Task CreatePost_WithAMissingFile_FailsBeforeSendingAnything()
        {
            var stub = new StubHandler();
            var client = new TumblrApiClient(new HttpClient(stub));
            var request = TumblrNpf.Build(new NpfBuildInput { Kind = PostKind.Video, Media = new[] { new PostMedia { Path = @"Z:\nope\missing.mp4", MimeType = "video/mp4" } } });
            var ex = await Assert.ThrowsAsync<TumblrApiException>(() => client.CreatePostAsync(OAuth1, "memeblog", request, CancellationToken.None));
            Assert.Equal(TumblrErrorKind.InvalidMedia, ex.Kind);
            Assert.Empty(stub.Requests);
        }

        [Fact]
        public async Task Posts_AreParsedFromNpf_WithTypeDerivedFromBlocks()
        {
            const string json = """
            {"meta":{"status":200},"response":{"total_posts":2,"posts":[
              {"id":111,"id_string":"111","slug":"my-slug-abc","blog_name":"memeblog","timestamp":1790000000,"note_count":42,"tags":["a","b"],"post_url":"https://memeblog.tumblr.com/post/111","state":"published","type":"blocks",
               "content":[{"type":"video","media":{"url":"x"}},{"type":"text","text":"caption here"}]},
              {"id_string":"222","timestamp":1790000100,"note_count":"7","type":"blocks","content":[{"type":"image","media":[]}]}
            ]}}
            """;
            var stub = new StubHandler { Respond = _ => Task.FromResult(Json(HttpStatusCode.OK, json)) };
            var client = new TumblrApiClient(new HttpClient(stub));
            var page = await client.GetPostsAsync(OAuth1, "memeblog", 20, 20, CancellationToken.None);

            Assert.Equal(2, page.TotalPosts);
            Assert.Equal("video", page.Posts[0].Type);
            Assert.Equal("my-slug-abc", page.Posts[0].Slug);
            Assert.Equal(42, page.Posts[0].NoteCount);
            Assert.Equal("caption here", page.Posts[0].Summary);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000).UtcDateTime, page.Posts[0].PublishedUtc);
            Assert.Equal("photo", page.Posts[1].Type);
            Assert.Equal(7, page.Posts[1].NoteCount);
            var uri = stub.Requests[0].Request.RequestUri!.ToString();
            Assert.Contains("npf=true", uri);
            Assert.Contains("offset=20", uri);
            Assert.Contains("api_key=ck", uri);
        }

        [Fact]
        public async Task OAuth1RequestToken_IsParsed_AndRejectedCredentialsSurfaceTheBody()
        {
            var stub = new StubHandler
            {
                Respond = r => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("oauth_token=rt&oauth_token_secret=rs&oauth_callback_confirmed=true"),
                }),
            };
            var client = new TumblrApiClient(new HttpClient(stub));
            var (token, secret) = await client.GetOAuth1RequestTokenAsync("ck", "cs", "https://klive.dev/omnitumblr/oauth/callback", CancellationToken.None);
            Assert.Equal(("rt", "rs"), (token, secret));
            Assert.Contains("oauth_callback=\"https%3A%2F%2Fklive.dev%2Fomnitumblr%2Foauth%2Fcallback\"", stub.Requests[0].Request.Headers.GetValues("Authorization").Single());

            stub.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("oauth_signature does not match expected value") });
            var ex = await Assert.ThrowsAsync<TumblrApiException>(() => client.GetOAuth1RequestTokenAsync("ck", "bad", "https://klive.dev/cb", CancellationToken.None));
            Assert.Equal(TumblrErrorKind.Unauthorized, ex.Kind);
            Assert.Contains("oauth_signature does not match", ex.Message);
        }

        [Fact]
        public async Task OAuth1AccessToken_FallsBackToGet_WhenPostIsNotAllowed()
        {
            var stub = new StubHandler
            {
                Respond = r => Task.FromResult(r.Method == HttpMethod.Post
                    ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed) { Content = new StringContent("") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("oauth_token=at&oauth_token_secret=as") }),
            };
            var client = new TumblrApiClient(new HttpClient(stub));
            var (token, secret) = await client.GetOAuth1AccessTokenAsync("ck", "cs", "rt", "rs", "verifier", CancellationToken.None);
            Assert.Equal(("at", "as"), (token, secret));
            Assert.Contains("oauth_verifier=\"verifier\"", stub.Requests[1].Request.Headers.GetValues("Authorization").Single());
        }

        [Fact]
        public async Task OAuth2Refresh_ParsesRotatedTokens()
        {
            var stub = new StubHandler { Respond = _ => Task.FromResult(Json(HttpStatusCode.OK, "{\"access_token\":\"a2\",\"refresh_token\":\"r2\",\"expires_in\":2520,\"token_type\":\"bearer\"}")) };
            var client = new TumblrApiClient(new HttpClient(stub));
            var token = await client.RefreshOAuth2TokenAsync("ck", "cs", "r1", CancellationToken.None);
            Assert.Equal("a2", token.AccessToken);
            Assert.Equal("r2", token.RefreshToken);
            Assert.Contains("grant_type=refresh_token", stub.Requests[0].Body);
            Assert.Contains("refresh_token=r1", stub.Requests[0].Body);
        }

        [Theory]
        [InlineData("memeblog", "memeblog")]
        [InlineData("@MemeBlog", "memeblog")]
        [InlineData("memeblog.tumblr.com", "memeblog")]
        [InlineData("https://memeblog.tumblr.com/", "memeblog")]
        [InlineData("https://www.tumblr.com/memeblog", "memeblog")]
        [InlineData("https://www.tumblr.com/memeblog/123456/some-slug", "memeblog")]
        [InlineData("https://www.tumblr.com/blog/view/memeblog", "memeblog")]
        [InlineData("www.davidslog.com", "www.davidslog.com")]
        [InlineData("t:0aY0xL2Fi1OFJg4YxpmegQ", "t:0aY0xL2Fi1OFJg4YxpmegQ")]
        public void BlogIdentifiers_AreNormalized(string input, string expected) =>
            Assert.Equal(expected, TumblrApiClient.NormalizeBlogIdentifier(input));
    }
}
