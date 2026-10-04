using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.MemeScraper.Scraping
{
    /// <summary>
    /// Reads the server-rendered JSON (<c>&lt;script type="application/json"&gt;</c> blocks) that
    /// Instagram embeds in its logged-out pages. Observed Oct 2026:
    /// <list type="bullet">
    /// <item><c>/{user}/reels/</c> embeds the 12 newest clips as objects with
    ///   <c>{pk, code, product_type:"clips", play_count, like_count, comment_count, user:{pk}}</c> — no video URLs.</item>
    /// <item><c>/reel/{code}/</c> embeds the full media object
    ///   <c>{pk, code, taken_at, caption:{text}, user:{username,pk}, video_versions:[{url,width,height}], ...}</c>.</item>
    /// </list>
    /// The DOM around these differs between loads (anchors are sometimes absent), so the JSON is the
    /// primary signal and anchors are only a fallback for codes.
    /// </summary>
    public static class InstagramWebParser
    {
        private static readonly Regex ReelHref = new(@"/(?:reel|reels|p)/([A-Za-z0-9_-]{5,40})", RegexOptions.Compiled);

        public static List<DiscoveredReel> ExtractProfileReels(IEnumerable<string> jsonScripts, IEnumerable<string>? anchorHrefs, string username)
        {
            var byCode = new Dictionary<string, DiscoveredReel>();
            foreach (var script in jsonScripts)
            {
                var root = ScrapeJson.TryParse(script);
                if (root == null) continue;
                foreach (var obj in ScrapeJson.AllObjects(root))
                {
                    if (!LooksLikeClip(obj)) continue;
                    var reel = MapMedia(obj, username);
                    if (reel == null) continue;
                    if (byCode.TryGetValue(reel.ShortCode, out var existing)) existing.MergeFrom(reel);
                    else byCode[reel.ShortCode] = reel;
                }
            }
            if (anchorHrefs != null)
            {
                foreach (var href in anchorHrefs)
                {
                    if (string.IsNullOrEmpty(href) || !href.Contains("/reel", StringComparison.OrdinalIgnoreCase)) continue;
                    var m = ReelHref.Match(href);
                    if (!m.Success || byCode.ContainsKey(m.Groups[1].Value)) continue;
                    byCode[m.Groups[1].Value] = new DiscoveredReel { ShortCode = m.Groups[1].Value, OwnerUsername = username, Provider = "instagram" };
                }
            }
            return byCode.Values.ToList();
        }

        /// <summary>The media object for <paramref name="shortCode"/> on its own /reel/ page, with video URLs.</summary>
        public static DiscoveredReel? ExtractReelFromPostPage(IEnumerable<string> jsonScripts, string shortCode, string? ogVideoUrl = null)
        {
            DiscoveredReel? best = null;
            foreach (var script in jsonScripts)
            {
                var root = ScrapeJson.TryParse(script);
                if (root == null) continue;
                foreach (var obj in ScrapeJson.AllObjects(root))
                {
                    if (obj["video_versions"] is not JArray versions || versions.Count == 0) continue;
                    var code = ScrapeJson.Str(obj, "code", "shortcode");
                    if (!string.IsNullOrEmpty(code) && !code.Equals(shortCode, StringComparison.Ordinal)) continue;
                    var reel = MapMedia(obj, "");
                    if (reel == null) continue;
                    reel.ShortCode = shortCode;
                    if (best == null) best = reel;
                    else best.MergeFrom(reel);
                }
            }
            if (best == null && ScrapeJson.IsHttpUrl(ogVideoUrl))
            {
                best = new DiscoveredReel { ShortCode = shortCode, Provider = "instagram" };
            }
            if (best != null) ScrapeJson.AddUrl(best.VideoUrls, ogVideoUrl);
            return best != null && best.VideoUrls.Count > 0 ? best : null;
        }

        /// <summary>Account facts from a logged-out profile page (used when inflact can't register a source).</summary>
        public static DiscoveredProfile? ExtractProfile(IEnumerable<string> jsonScripts, string username, string? ogDescription)
        {
            DiscoveredProfile? profile = null;
            string? accountIdFromMedia = null;
            foreach (var script in jsonScripts)
            {
                var root = ScrapeJson.TryParse(script);
                if (root == null) continue;
                foreach (var obj in ScrapeJson.AllObjects(root))
                {
                    var name = ScrapeJson.Str(obj, "username");
                    if (!name.Equals(username, StringComparison.OrdinalIgnoreCase))
                    {
                        // Media objects carry the owner's pk even when the user object itself isn't embedded.
                        if (accountIdFromMedia == null && LooksLikeClip(obj))
                        {
                            var ownerPk = ScrapeJson.Str(obj, "user.pk", "owner.pk", "owner.id");
                            if (ownerPk.All(char.IsDigit) && ownerPk.Length > 0) accountIdFromMedia = ownerPk;
                        }
                        continue;
                    }
                    var pk = ScrapeJson.Str(obj, "pk", "pk_id", "id");
                    if (!pk.All(char.IsDigit) || pk.Length == 0) continue;
                    var candidate = new DiscoveredProfile
                    {
                        Provider = "instagram",
                        Username = name,
                        AccountID = pk,
                        FullName = ScrapeJson.Str(obj, "full_name"),
                        Followers = ScrapeJson.ClampInt(ScrapeJson.Long(obj, "follower_count", "edge_followed_by.count")),
                        Bio = ScrapeJson.Str(obj, "biography"),
                        ProfilePictureUrl = ScrapeJson.Str(obj, "hd_profile_pic_url_info.url", "profile_pic_url_hd", "profile_pic_url"),
                    };
                    // Prefer the richest user object (the profile header has full_name/followers; mentions don't).
                    if (profile == null || Richness(candidate) > Richness(profile)) profile = candidate;
                }
            }
            if (profile == null && accountIdFromMedia != null)
            {
                profile = new DiscoveredProfile { Provider = "instagram", Username = username, AccountID = accountIdFromMedia };
            }
            if (profile != null && profile.Followers == 0) profile.Followers = ParseFollowersFromOgDescription(ogDescription);
            return profile;
        }

        /// <summary>"355K Followers, 121 Following, 1,234 Posts - ..." → 355000.</summary>
        public static int ParseFollowersFromOgDescription(string? description)
        {
            if (string.IsNullOrEmpty(description)) return 0;
            var m = Regex.Match(description, @"([\d.,]+)\s*([KkMmBb]?)\s+Followers");
            if (!m.Success) return 0;
            var number = m.Groups[1].Value.Replace(",", "");
            if (!double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)) return 0;
            value *= m.Groups[2].Value.ToUpperInvariant() switch { "K" => 1_000, "M" => 1_000_000, "B" => 1_000_000_000, _ => 1 };
            return (int)Math.Clamp(value, 0, int.MaxValue);
        }

        private static bool LooksLikeClip(JObject obj)
        {
            var code = ScrapeJson.Str(obj, "code", "shortcode");
            if (!ScrapeJson.IsShortCode(code)) return false;
            var productType = ScrapeJson.Str(obj, "product_type");
            if (productType.Equals("clips", StringComparison.OrdinalIgnoreCase)) return true;
            if (ScrapeJson.Long(obj, "media_type") == 2) return true;
            if (obj["video_versions"] is JArray v && v.Count > 0) return true;
            var typename = ScrapeJson.Str(obj, "__typename");
            return typename.Contains("Video", StringComparison.OrdinalIgnoreCase) || typename.Contains("Clip", StringComparison.OrdinalIgnoreCase);
        }

        private static DiscoveredReel? MapMedia(JObject obj, string fallbackUsername)
        {
            var reel = new DiscoveredReel { Provider = "instagram" };
            reel.ShortCode = ScrapeJson.Str(obj, "code", "shortcode");
            if (!ScrapeJson.IsShortCode(reel.ShortCode)) return null;
            reel.PostID = ScrapeJson.NormalizeMediaId(ScrapeJson.Str(obj, "pk"));
            if (string.IsNullOrEmpty(reel.PostID)) reel.PostID = ScrapeJson.NormalizeMediaId(ScrapeJson.Str(obj, "id"));
            reel.OwnerUsername = ScrapeJson.Str(obj, "user.username", "owner.username");
            if (string.IsNullOrEmpty(reel.OwnerUsername)) reel.OwnerUsername = fallbackUsername;
            reel.OwnerID = ScrapeJson.Str(obj, "user.pk", "owner.pk", "owner.id");
            reel.ViewCount = ScrapeJson.Long(obj, "play_count", "ig_play_count", "view_count", "video_view_count");
            reel.LikeCount = ScrapeJson.Long(obj, "like_count");
            reel.CommentCount = ScrapeJson.ClampInt(ScrapeJson.Long(obj, "comment_count"));
            reel.CreatedAtUtc = ScrapeJson.EpochToUtc(obj, "taken_at", "taken_at_timestamp");
            reel.Description = ScrapeJson.Str(obj, "caption.text", "edge_media_to_caption.edges.0.node.text");
            foreach (var url in ScrapeJson.VideoVersionUrls(obj["video_versions"])) ScrapeJson.AddUrl(reel.VideoUrls, url);
            ScrapeJson.AddUrl(reel.VideoUrls, ScrapeJson.Str(obj, "video_url"));
            return reel;
        }

        private static int Richness(DiscoveredProfile p) =>
            (string.IsNullOrEmpty(p.FullName) ? 0 : 1) + (p.Followers > 0 ? 2 : 0) + (string.IsNullOrEmpty(p.Bio) ? 0 : 1) + (string.IsNullOrEmpty(p.ProfilePictureUrl) ? 0 : 1);
    }
}
