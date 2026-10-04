using Omnipotent.Services.MemeScraper;
using Omnipotent.Services.MemeScraper.Scraping;
using System.Text;

namespace Omnipotent.Tests.MemeScraper
{
    /// <summary>
    /// Payload shapes below are trimmed copies of live responses captured Oct 2026
    /// (inflact.com downloader API and Instagram's logged-out SSR JSON).
    /// </summary>
    public class MemeScraperParserTests
    {
        private const string InflactReelsPage = """
            {"status":"success","data":{"reels":[
              {"\u0000*\u0000_refererPath":"",
               "\u0000common\\components\\insta\\models\\MobileReel\u0000_data":{"code":"DbjOxmqpAbO","pk":"3955069879184525006",
                  "video_versions":[{"height":1280,"width":720,"type":101,"url":"https://instagram.fmnl13-1.fna.fbcdn.net/o1/v/t2/f2/m86/AQMW.mp4?oh=1"},
                                    {"height":640,"width":360,"type":102,"url":"https://instagram.fmnl13-1.fna.fbcdn.net/o1/v/t2/f2/m86/AQMW-low.mp4?oh=2"}]},
               "commentCount":9,"comment_count":9,"createdAt":1785701124,"created_at":1785701124,
               "description":"V stepped into the crowd","downloadUrl":"https://cdn-v.inflact.com/download/AQMW.mp4?url=x",
               "id":"3955069879184525006","imageUrl":"https://cdn.inflact.com/media/x.jpg","likeCount":605,"like_count":605,"mediaType":2,
               "owner":{"fullName":"memes by bepis","id":"44198560066","username":"bepis.shit"},"ownerId":"44198560066",
               "playCount":13860,"post_id":"3955069879184525006","shortCode":"DbjOxmqpAbO","short_code":"DbjOxmqpAbO","type":2,
               "url":"https://instagram.fmnl13-1.fna.fbcdn.net/o1/v/t2/f2/m86/AQMW.mp4?oh=1",
               "videoUrl":"https://cdn.inflact.com/media/AQMW.mp4?url=y","videoViewCount":13860},
              {"id":"3951443160120924617","shortCode":"DbWWJ5VJ5nJ","createdAt":1785269124,"mediaType":2,
               "owner":{"id":"44198560066","username":"bepis.shit"},"playCount":"42400",
               "videoUrl":"https://cdn.inflact.com/media/AQNI.mp4?url=z"}
            ],"hasNextPage":true,"cursor":"QVFDVm5jTVdK","avgViews":3386.3,"avgLikes":6896,"avgComments":60.3,"topReel":null},
            "isAuthorized":false,"isSubscribed":false,"links":{"subscribe":"https://inflact.com/cabinet/"}}
            """;

        [Fact]
        public void InflactReelsPage_MapsTheCurrentShape()
        {
            var page = InflactParser.ParseReelsPage(InflactReelsPage, "bepis.shit");

            Assert.True(page.Success, page.Error);
            Assert.True(page.HasNextPage);
            Assert.Equal("QVFDVm5jTVdK", page.Cursor);
            Assert.Equal(2, page.Reels.Count);

            var reel = page.Reels[0];
            Assert.Equal("3955069879184525006", reel.PostID);
            Assert.Equal("DbjOxmqpAbO", reel.ShortCode);
            Assert.Equal("bepis.shit", reel.OwnerUsername);
            Assert.Equal("44198560066", reel.OwnerID);
            Assert.Equal(13860, reel.ViewCount);
            Assert.Equal(605, reel.LikeCount);
            Assert.Equal(9, reel.CommentCount);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785701124).UtcDateTime, reel.CreatedAtUtc);
            Assert.Equal("V stepped into the crowd", reel.Description);
            // Raw CDN first, then the other rendition, then inflact's proxy and download mirrors; no duplicates.
            Assert.Equal(new[]
            {
                "https://instagram.fmnl13-1.fna.fbcdn.net/o1/v/t2/f2/m86/AQMW.mp4?oh=1",
                "https://instagram.fmnl13-1.fna.fbcdn.net/o1/v/t2/f2/m86/AQMW-low.mp4?oh=2",
                "https://cdn.inflact.com/media/AQMW.mp4?url=y",
                "https://cdn-v.inflact.com/download/AQMW.mp4?url=x",
            }, reel.VideoUrls);

            // Sparse item: falls back to id/playCount-as-string/videoUrl.
            var sparse = page.Reels[1];
            Assert.Equal("3951443160120924617", sparse.PostID);
            Assert.Equal(42400, sparse.ViewCount);
            Assert.Equal(new[] { "https://cdn.inflact.com/media/AQNI.mp4?url=z" }, sparse.VideoUrls);
        }

        [Fact]
        public void InflactReelsPage_ParsesTheAug2025Shape()
        {
            const string legacy = """
                {"status":"success","data":{"reels":[{"post_id":"3705130604596351051","shortCode":"DNrRKd9ZNBL",
                 "owner":{"username":"bepis.shit","id":"44198560066"},"videoViewCount":13908,"created_at":"1755906061",
                 "url":"https://instagram.fshl2-1.fna.fbcdn.net/o1/v/t16/f2/m86/AQMq.mp4?stp=dst-mp4","comment_count":4,
                 "description":"Follow (@bepis.shit) or else"}]}}
                """;
            var page = InflactParser.ParseReelsPage(legacy, "bepis.shit");
            Assert.True(page.Success);
            Assert.False(page.HasNextPage);
            var reel = Assert.Single(page.Reels);
            Assert.Equal("3705130604596351051", reel.PostID);
            Assert.Equal(13908, reel.ViewCount);
            Assert.Equal(new DateTime(2025, 8, 22, 23, 41, 1, DateTimeKind.Utc), reel.CreatedAtUtc);
        }

        [Fact]
        public void InflactReelsPage_EmptyProfileIsASuccessWithNoReels()
        {
            var page = InflactParser.ParseReelsPage(
                """{"status":"success","data":{"reels":[],"hasNextPage":false,"cursor":null,"avgViews":0,"topReel":null},"isAuthorized":false}""", "tfck");
            Assert.True(page.Success);
            Assert.Empty(page.Reels);
            Assert.False(page.HasNextPage);
            Assert.Equal("", page.Cursor);
        }

        [Fact]
        public void InflactReelsPage_UnsignedRequestIsReportedAsTokenRejection()
        {
            var page = InflactParser.ParseReelsPage(
                """{"status":"error","message":"Invalid client token. Please reload the page.","isAuthorized":false,"isSubscribed":false,"links":{}}""", "x");
            Assert.False(page.Success);
            Assert.True(page.TokenRejected);
            Assert.Contains("Invalid client token", page.Error);
        }

        [Fact]
        public void InflactReelsPage_HtmlErrorPageIsAFailureNotAToken()
        {
            var page = InflactParser.ParseReelsPage("<!DOCTYPE html><title>Just a moment...</title>", "x");
            Assert.False(page.Success);
            Assert.False(page.TokenRejected);
            Assert.StartsWith("non-JSON response", page.Error);
        }

        [Fact]
        public void InflactProfile_MapsTheCurrentShape()
        {
            const string body = """
                {"status":"success","data":{"isAuthorized":false,"hashtags":[{"name":"#Viral","count":1,"url":"https://inflact.com/x"}],
                 "avg_likes":21476.333333333332,"avg_comments":56.666666666666664,"contentType":"profile",
                 "profile":{"biography":"Follow for dumb shitposts","edge_followed_by":{"count":355373},"full_name":"memes by bepis",
                   "id":"44198560066","profile_pic_download_url":"https://cdn-v.inflact.com/download/pic.jpg","username":"bepis.shit"}}}
                """;
            var profile = InflactParser.ParseProfile(body, out var error);
            Assert.NotNull(profile);
            Assert.Equal("", error);
            Assert.Equal("44198560066", profile!.AccountID);
            Assert.Equal("bepis.shit", profile.Username);
            Assert.Equal(355373, profile.Followers);
            Assert.Equal("memes by bepis", profile.FullName);
            Assert.Equal(21476.33f, profile.AverageLikes, 0.01f);
            Assert.Equal(56.67f, profile.AverageComments, 0.01f);
            Assert.Equal("#Viral", Assert.Single(profile.Hashtags).Name);
        }

        /// <summary>Wraps JSON in <paramref name="levels"/> objects: Instagram's SSR nests past Newtonsoft's default MaxDepth of 64.</summary>
        private static string Nest(string json, int levels)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < levels; i++) sb.Append("{\"l\":");
            sb.Append(json);
            sb.Append('}', levels);
            return sb.ToString();
        }

        [Fact]
        public void InstagramProfileReels_ReadsClipsFromDeepSsrJsonAndIgnoresPhotos()
        {
            string clips = """
                {"edges":[
                  {"node":{"media":{"__typename":"XIGPolarisVideoMedia","id":"POLARIS_3955069879184525006","pk":"3955069879184525006","code":"DbjOxmqpAbO",
                    "product_type":"clips","play_count":13860,"like_count":605,"comment_count":9,"user":{"id":"17841444161984820","pk":"44198560066"}}}},
                  {"node":{"media":{"__typename":"XIGPolarisVideoMedia","pk":"3951443160120924617","code":"DbWWJ5VJ5nJ",
                    "product_type":"clips","play_count":38500,"user":{"pk":"44198560066"}}}},
                  {"node":{"media":{"__typename":"XIGPolarisImageMedia","pk":"111","code":"PhotoCode1","product_type":"feed","media_type":1}}}
                ]}
                """;
            var reels = InstagramWebParser.ExtractProfileReels(new[] { Nest(clips, 90), "not json" }, new[] { "/bepis.shit/reel/DXQcQ-qgsoX/" }, "bepis.shit");

            Assert.Equal(3, reels.Count);
            var first = reels.Single(r => r.ShortCode == "DbjOxmqpAbO");
            Assert.Equal("3955069879184525006", first.PostID);
            Assert.Equal(13860, first.ViewCount);
            Assert.Equal("44198560066", first.OwnerID);
            Assert.Equal("bepis.shit", first.OwnerUsername);
            Assert.Contains(reels, r => r.ShortCode == "DbWWJ5VJ5nJ" && r.ViewCount == 38500);
            Assert.Contains(reels, r => r.ShortCode == "DXQcQ-qgsoX"); // from the anchor only
            Assert.DoesNotContain(reels, r => r.ShortCode == "PhotoCode1");
        }

        [Fact]
        public void InstagramReelPage_PicksTheRequestedMediaAndLargestRenditionFirst()
        {
            string media = """
                {"items":[
                  {"__typename":"XDTMediaDict","pk":"3955069879184525006","code":"DbjOxmqpAbO","taken_at":1785701124,"media_type":2,
                   "product_type":"clips","like_count":605,"comment_count":9,
                   "caption":{"text":"V stepped into the crowd"},"user":{"username":"bepis.shit","pk":"44198560066","id":"17841444161984820"},
                   "video_versions":[{"width":360,"height":640,"url":"https://scontent-lhr11-1.cdninstagram.com/low.mp4"},
                                     {"width":720,"height":1280,"url":"https://scontent-lhr11-1.cdninstagram.com/high.mp4"}]},
                  {"pk":"999","code":"OtherReel1","video_versions":[{"width":720,"height":1280,"url":"https://scontent.cdninstagram.com/other.mp4"}]}
                ]}
                """;
            var reel = InstagramWebParser.ExtractReelFromPostPage(new[] { Nest(media, 70) }, "DbjOxmqpAbO");

            Assert.NotNull(reel);
            Assert.Equal("3955069879184525006", reel!.PostID);
            Assert.Equal("bepis.shit", reel.OwnerUsername);
            Assert.Equal("44198560066", reel.OwnerID);
            Assert.Equal("V stepped into the crowd", reel.Description);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785701124).UtcDateTime, reel.CreatedAtUtc);
            Assert.Equal(new[] { "https://scontent-lhr11-1.cdninstagram.com/high.mp4", "https://scontent-lhr11-1.cdninstagram.com/low.mp4" }, reel.VideoUrls);
        }

        [Fact]
        public void InstagramReelPage_WithoutVideoDataIsNull_UnlessOgVideoExists()
        {
            Assert.Null(InstagramWebParser.ExtractReelFromPostPage(new[] { """{"code":"DbjOxmqpAbO"}""" }, "DbjOxmqpAbO"));
            var fromMeta = InstagramWebParser.ExtractReelFromPostPage(Array.Empty<string>(), "DbjOxmqpAbO", "https://scontent.cdninstagram.com/og.mp4");
            Assert.Equal(new[] { "https://scontent.cdninstagram.com/og.mp4" }, fromMeta!.VideoUrls);
        }

        [Fact]
        public void InstagramProfile_PrefersTheRichUserObjectAndFallsBackToOgFollowers()
        {
            string json = """
                {"a":{"user":{"username":"bepis.shit","pk":"44198560066"}},
                 "b":{"user":{"username":"bepis.shit","pk":"44198560066","full_name":"memes by bepis","biography":"Follow for dumb shitposts"}},
                 "c":{"user":{"username":"someone.else","pk":"5","full_name":"x","follower_count":10}}}
                """;
            var profile = InstagramWebParser.ExtractProfile(new[] { json }, "Bepis.Shit", "355K Followers, 121 Following, 1,234 Posts - memes by bepis");
            Assert.NotNull(profile);
            Assert.Equal("44198560066", profile!.AccountID);
            Assert.Equal("memes by bepis", profile.FullName);
            Assert.Equal(355000, profile.Followers);
        }

        [Fact]
        public void InstagramProfile_UsesClipOwnerWhenNoUserObjectIsEmbedded()
        {
            string json = """{"m":{"code":"DbjOxmqpAbO","product_type":"clips","user":{"pk":"44198560066"}}}""";
            var profile = InstagramWebParser.ExtractProfile(new[] { json }, "bepis.shit", null);
            Assert.Equal("44198560066", profile!.AccountID);
            Assert.Equal("bepis.shit", profile.Username);
        }

        [Theory]
        [InlineData("1,234 Followers, 5 Following", 1234)]
        [InlineData("1.5M Followers, 5 Following", 1_500_000)]
        [InlineData("no numbers here", 0)]
        public void OgFollowers(string description, int expected) =>
            Assert.Equal(expected, InstagramWebParser.ParseFollowersFromOgDescription(description));

        [Theory]
        [InlineData("@bepis.shit", "bepis.shit")]
        [InlineData("  tfck ", "tfck")]
        [InlineData("https://www.instagram.com/bepis.shit/reels/?hl=en", "bepis.shit")]
        [InlineData("instagram.com/tfck/", "tfck")]
        [InlineData("", "")]
        public void UsernamesAreNormalized(string input, string expected) =>
            Assert.Equal(expected, MemeScraperSources.NormalizeUsername(input));

        [Theory]
        [InlineData("3955069879184525006_44198560066", "3955069879184525006")]
        [InlineData("POLARIS_3955069879184525006", "3955069879184525006")]
        [InlineData("3955069879184525006", "3955069879184525006")]
        [InlineData("not-an-id", "")]
        public void MediaIdsAreNormalized(string input, string expected) =>
            Assert.Equal(expected, ScrapeJson.NormalizeMediaId(input));
    }
}
