using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.MemeScraper.Scraping
{
    /// <summary>One page of inflact's <c>/downloader/api/downloader/reels/</c> response.</summary>
    public sealed class InflactReelsPage
    {
        public bool Success;
        public string Error = "";
        public List<DiscoveredReel> Reels = new();
        public bool HasNextPage;
        public string Cursor = "";

        /// <summary>The signed client headers expired or were rejected; reloading the page mints new ones.</summary>
        public bool TokenRejected =>
            !Success && (Error.Contains("token", StringComparison.OrdinalIgnoreCase)
                         || Error.Contains("reload", StringComparison.OrdinalIgnoreCase)
                         || Error.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses inflact.com downloader API payloads. Shapes observed Oct 2026:
    /// <code>
    /// reels:   {"status":"success","data":{"reels":[{post_id,id,shortCode,createdAt,created_at,url,videoUrl,downloadUrl,
    ///           videoViewCount,playCount,comment_count,likeCount,description,owner:{id,username},...}],
    ///           "hasNextPage":true,"cursor":"..."}}
    /// profile: {"status":"success","data":{"profile":{id,username,full_name,biography,edge_followed_by:{count},
    ///           profile_pic_download_url,...},"avg_likes":..,"avg_comments":..,"hashtags":[{name,count,url}]}}
    /// error:   {"status":"error","message":"Invalid client token. Please reload the page."}
    /// </code>
    /// Every field read has fallbacks because inflact has renamed fields before.
    /// </summary>
    public static class InflactParser
    {
        public static InflactReelsPage ParseReelsPage(string? body, string requestedUsername)
        {
            var page = new InflactReelsPage();
            var root = ScrapeJson.TryParse(body) as JObject;
            if (root == null)
            {
                page.Error = "non-JSON response: " + Snippet(body);
                return page;
            }

            string status = ScrapeJson.Str(root, "status");
            if (!string.IsNullOrEmpty(status) && !status.Equals("success", StringComparison.OrdinalIgnoreCase))
            {
                page.Error = FirstNonEmpty(ScrapeJson.Str(root, "message", "error", "data.message", "alert.type", "data.alert.type"), status);
                return page;
            }

            var data = ScrapeJson.Get(root, "data") as JObject;
            if (data == null)
            {
                page.Error = "response has no data object: " + Snippet(body);
                return page;
            }

            var alert = ScrapeJson.Str(data, "alert.type", "alert");
            var reels = (ScrapeJson.Get(data, "reels") ?? ScrapeJson.Get(data, "items") ?? ScrapeJson.Get(data, "edges")) as JArray;
            if (reels == null)
            {
                page.Error = !string.IsNullOrEmpty(alert) ? "alert: " + alert : "response has no reels array: " + Snippet(body);
                return page;
            }

            page.Success = true;
            page.HasNextPage = ScrapeJson.Bool(data, "hasNextPage", "has_next_page", "pagination.hasNextPage") ?? false;
            page.Cursor = ScrapeJson.Str(data, "cursor", "end_cursor", "nextCursor", "pagination.cursor");
            foreach (var item in reels.OfType<JObject>())
            {
                var node = item["node"] as JObject ?? item;
                var reel = MapReel(node, requestedUsername);
                if (reel != null) page.Reels.Add(reel);
            }
            return page;
        }

        public static DiscoveredReel? MapReel(JObject item, string requestedUsername)
        {
            var reel = new DiscoveredReel { Provider = "inflact" };
            reel.PostID = FirstNonEmpty(
                ScrapeJson.NormalizeMediaId(ScrapeJson.Str(item, "post_id", "postId")),
                ScrapeJson.NormalizeMediaId(ScrapeJson.Str(item, "pk")),
                ScrapeJson.NormalizeMediaId(ScrapeJson.Str(item, "id")));
            reel.ShortCode = ScrapeJson.Str(item, "shortCode", "short_code", "shortcode", "code");
            if (!ScrapeJson.IsShortCode(reel.ShortCode)) reel.ShortCode = "";
            if (!reel.HasIdentity) return null;

            reel.OwnerUsername = FirstNonEmpty(ScrapeJson.Str(item, "owner.username", "user.username", "username"), requestedUsername);
            reel.OwnerID = ScrapeJson.Str(item, "owner.id", "ownerId", "owner_id", "user.pk", "user.id");
            reel.ViewCount = ScrapeJson.Long(item, "videoViewCount", "video_view_count", "playCount", "play_count", "viewCount", "view_count");
            reel.LikeCount = ScrapeJson.Long(item, "likeCount", "like_count");
            reel.CommentCount = ScrapeJson.ClampInt(ScrapeJson.Long(item, "comment_count", "commentCount"));
            reel.CreatedAtUtc = ScrapeJson.EpochToUtc(item, "createdAt", "created_at", "taken_at", "takenAt");
            reel.Description = FirstNonEmpty(ScrapeJson.Str(item, "description", "caption.text", "caption"), "");

            // Raw Instagram CDN first (full quality, no third-party hop), then every rendition
            // Instagram listed, then inflact's own proxy and download mirrors as fallbacks.
            ScrapeJson.AddUrl(reel.VideoUrls, ScrapeJson.Str(item, "url", "video_url"));
            foreach (var versions in ScrapeJson.AllObjects(item, maxNodes: 20_000)
                         .Select(o => o["video_versions"]).Where(v => v is JArray))
            {
                foreach (var url in ScrapeJson.VideoVersionUrls(versions)) ScrapeJson.AddUrl(reel.VideoUrls, url);
            }
            ScrapeJson.AddUrl(reel.VideoUrls, ScrapeJson.Str(item, "videoUrl", "video_download_url"));
            ScrapeJson.AddUrl(reel.VideoUrls, ScrapeJson.Str(item, "downloadUrl", "download_url"));

            long mediaType = ScrapeJson.Long(item, "mediaType", "media_type", "type");
            if (reel.VideoUrls.Count == 0 && mediaType != 0 && mediaType != 2) return null; // a photo slipped into the list
            return reel;
        }

        public static DiscoveredProfile? ParseProfile(string? body, out string error)
        {
            error = "";
            var root = ScrapeJson.TryParse(body) as JObject;
            if (root == null)
            {
                error = "non-JSON response: " + Snippet(body);
                return null;
            }
            string status = ScrapeJson.Str(root, "status");
            if (!string.IsNullOrEmpty(status) && !status.Equals("success", StringComparison.OrdinalIgnoreCase))
            {
                error = FirstNonEmpty(ScrapeJson.Str(root, "message", "error"), status);
                return null;
            }
            var data = ScrapeJson.Get(root, "data") as JObject;
            var profile = ScrapeJson.Get(data, "profile") as JObject;
            if (profile == null)
            {
                var alert = ScrapeJson.Str(data, "alert.type", "alert");
                error = !string.IsNullOrEmpty(alert) ? "alert: " + alert : "response has no profile: " + Snippet(body);
                return null;
            }

            var result = new DiscoveredProfile
            {
                Provider = "inflact",
                Username = ScrapeJson.Str(profile, "username"),
                AccountID = ScrapeJson.Str(profile, "id", "pk"),
                FullName = ScrapeJson.Str(profile, "full_name", "fullName"),
                Followers = ScrapeJson.ClampInt(ScrapeJson.Long(profile, "edge_followed_by.count", "follower_count", "followedByCount", "followers")),
                ProfilePictureUrl = ScrapeJson.Str(profile, "profile_pic_download_url", "profile_pic_url_hd", "profile_pic_url", "profilePicUrl"),
                Bio = ScrapeJson.Str(profile, "biography", "bio"),
                AverageLikes = ScrapeJson.Long(data, "avg_likes", "avgLikes"),
                AverageComments = ScrapeJson.Long(data, "avg_comments", "avgComments"),
            };
            // avg_* are fractional; Long() truncates, so prefer the exact value when it parses.
            if (ScrapeJson.Get(data, "avg_likes") is JValue likes && likes.Type == JTokenType.Float) result.AverageLikes = likes.Value<float>();
            if (ScrapeJson.Get(data, "avg_comments") is JValue comments && comments.Type == JTokenType.Float) result.AverageComments = comments.Value<float>();
            if (ScrapeJson.Get(data, "hashtags") is JArray tags)
            {
                foreach (var tag in tags.OfType<JObject>())
                {
                    result.Hashtags.Add((ScrapeJson.Str(tag, "name"), ScrapeJson.ClampInt(ScrapeJson.Long(tag, "count")), ScrapeJson.Str(tag, "url")));
                }
            }
            if (string.IsNullOrEmpty(result.AccountID) || !result.AccountID.All(char.IsDigit))
            {
                error = "profile has no numeric account id";
                return null;
            }
            return result;
        }

        private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

        internal static string Snippet(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            var flat = text.Replace('\n', ' ').Replace('\r', ' ');
            return flat.Length <= 160 ? flat : flat.Substring(0, 160) + "…";
        }
    }
}
