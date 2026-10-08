using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    /// <summary>
    /// One-time import of the v1 data (SavedData/OmniTumblr/Accounts + Posts): the app credentials and
    /// OAuth 1.0a tokens, each blog with its configuration mapped onto a v2 strategy, published-post
    /// history, and the content v1 already used. Blogs come over with autopilot off, so nothing posts
    /// until their strategy has been reviewed. The v1 files are left in place.
    /// </summary>
    internal static class OmniTumblrMigration
    {
        public static string? MigrateV1(OmniTumblrStore store, string v1Root, string? legacyCallbackUrl, DateTime now, Action<string> log)
        {
            if (store.Read(s => s.Engine.MigratedV1Utc) != null) return null;
            string accountsDir = Path.Combine(v1Root, "Accounts");
            string postsDir = Path.Combine(v1Root, "Posts");

            var accounts = ReadAll(accountsDir, log);
            var posts = ReadAll(postsDir, log);
            if (accounts.Count == 0)
            {
                store.Mutate(s =>
                {
                    s.Engine.MigratedV1Utc = now;
                    s.Engine.MigrationSummary = "No v1 data to import.";
                    if (!string.IsNullOrWhiteSpace(legacyCallbackUrl) && Uri.TryCreate(legacyCallbackUrl, UriKind.Absolute, out _) && !s.App.IsConfigured)
                        s.App.CallbackUrl = legacyCallbackUrl.Trim();
                    s.MarkApp();
                    s.MarkEngine();
                });
                return null;
            }

            string summary = store.Mutate(s =>
            {
                int blogsImported = 0, connectionsCreated = 0, postsImported = 0, postsDropped = 0;
                var notes = new List<string>();

                if (!string.IsNullOrWhiteSpace(legacyCallbackUrl) && Uri.TryCreate(legacyCallbackUrl, UriKind.Absolute, out _))
                    s.App.CallbackUrl = legacyCallbackUrl.Trim();

                var connectionByToken = new Dictionary<string, string>(StringComparer.Ordinal);
                var blogIdByV1Account = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var a in accounts)
                {
                    string? blogName = Str(a["BlogName"]);
                    if (string.IsNullOrWhiteSpace(blogName)) continue;
                    string name = TumblrApiClient.NormalizeBlogIdentifier(blogName);
                    if (s.Blogs.Values.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))) continue;

                    string? key = Str(a["ConsumerKey"]);
                    string? secret = Str(a["ConsumerSecret"]);
                    if (!s.App.IsConfigured && !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(secret))
                    {
                        s.App.ConsumerKeyCipher = s.Vault.Protect(key.Trim(), "app:consumer-key");
                        s.App.ConsumerSecretCipher = s.Vault.Protect(secret.Trim(), "app:consumer-secret");
                        s.App.ConsumerKeyHint = key.Length > 4 ? key[^4..] : key;
                        s.App.PreferredAuthMode = TumblrAuthMode.OAuth1;
                        s.App.UpdatedUtc = now;
                        s.MarkApp();
                    }

                    string connectionId = "";
                    string? token = Str(a["OAuthToken"]);
                    string? tokenSecret = Str(a["OAuthTokenSecret"]);
                    if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(tokenSecret))
                    {
                        if (!connectionByToken.TryGetValue(token, out var existing))
                        {
                            var conn = new OmniTumblrConnection
                            {
                                AuthMode = TumblrAuthMode.OAuth1,
                                CreatedUtc = now,
                                Health = ConnectionHealth.Unknown,
                                HealthDetail = "Imported from OmniTumblr v1; verifying on the next sync.",
                            };
                            conn.TokenCipher = s.Vault.Protect(token.Trim(), $"conn:{conn.ConnectionId}:token");
                            conn.TokenSecretCipher = s.Vault.Protect(tokenSecret.Trim(), $"conn:{conn.ConnectionId}:token-secret");
                            s.Connections[conn.ConnectionId] = conn;
                            connectionByToken[token] = conn.ConnectionId;
                            existing = conn.ConnectionId;
                            connectionsCreated++;
                            s.MarkConnections();
                        }
                        connectionId = existing;
                    }
                    else
                    {
                        notes.Add($"@{name} had no OAuth token in v1 — connect its account again.");
                    }

                    var blog = new OmniTumblrBlog
                    {
                        Name = name,
                        ConnectionId = connectionId,
                        Autopilot = false,
                        Paused = Bool(a["IsPaused"]) ?? false,
                        Notes = Str(a["Notes"]),
                        Title = Str(a["Title"]),
                        Description = Str(a["Description"]),
                        Url = Str(a["Url"]),
                        AddedUtc = Date(a["AddedDate"]) ?? now,
                        Strategy = MapStrategy(a["ContentConfig"] as JObject, notes, name),
                    };
                    long followers = Long(a["FollowerCount"]) ?? 0;
                    long postCount = Long(a["PostCount"]) ?? 0;
                    if (followers > 0) blog.Stats.Followers = followers;
                    if (postCount > 0) blog.Stats.Posts = postCount;
                    blog.Stats.LastPublishedUtc = Date(a["LastPostTime"]);

                    // Content v1 already posted (or reserved) must not be posted again.
                    if (a["ContentConfig"]?["UsedContentPaths"] is JArray used)
                    {
                        foreach (string? path in used.Select(Str))
                        {
                            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                            blog.UsedContentKeys.Add(OmniTumblrContentSources.FileKey(new FileInfo(path)));
                        }
                    }

                    s.Blogs[blog.BlogId] = blog;
                    s.PostsOf(blog.BlogId);
                    s.InsightsOf(blog.BlogId);
                    s.MarkBlog(blog.BlogId);
                    string? v1Id = Str(a["AccountId"]);
                    if (v1Id != null) blogIdByV1Account[v1Id] = blog.BlogId;
                    blogsImported++;
                }

                foreach (var p in posts)
                {
                    string? v1Account = Str(p["AccountId"]);
                    if (v1Account == null || !blogIdByV1Account.TryGetValue(v1Account, out var blogId)) { postsDropped++; continue; }
                    var blog = s.Blogs[blogId];
                    string? sourceType = Str(p["SourceType"]);
                    string? sourceId = Str(p["SourceId"]);
                    if (sourceType == "MemeScraper" && !string.IsNullOrWhiteSpace(sourceId))
                        blog.UsedContentKeys.Add(OmniTumblrContentSources.ReelKey(sourceId));

                    if (Str(p["Status"]) != "Posted") { postsDropped++; continue; }
                    string? tumblrId = Str(p["TumblrPostId"]);
                    var published = Date(p["PostedTime"]) ?? Date(p["ScheduledTime"]) ?? now;
                    var post = new OmniTumblrPost
                    {
                        BlogId = blogId,
                        Status = PostStatus.Published,
                        Origin = PostOrigin.Imported,
                        Kind = Str(p["PostType"]) switch
                        {
                            "Video" => PostKind.Video,
                            "Photo" or "PhotoSet" => PostKind.Photo,
                            "Link" => PostKind.Link,
                            _ => PostKind.Text,
                        },
                        CreatedUtc = published,
                        UpdatedUtc = now,
                        ScheduledUtc = Date(p["ScheduledTime"]) ?? published,
                        PublishedUtc = published,
                        Caption = Str(p["Caption"]),
                        Title = Str(p["Title"]),
                        Tags = (p["Tags"] as JArray)?.Select(t => t.ToString()).ToList() ?? new(),
                        TumblrPostId = string.IsNullOrWhiteSpace(tumblrId) || tumblrId == "0" ? null : tumblrId,
                        Approved = true,
                    };
                    if (post.TumblrPostId != null) post.TumblrUrl = TumblrApiClient.BuildPostUrl(blog.Name, post.TumblrPostId);
                    if (sourceType == "MemeScraper" && sourceId != null)
                        post.Content = new ContentRef { Kind = ContentSourceKind.MemeScraper, Key = OmniTumblrContentSources.ReelKey(sourceId) };
                    s.PostsOf(blogId).Add(post);
                    s.MarkPosts(blogId);
                    postsImported++;
                }

                s.Engine.MigratedV1Utc = now;
                string text = $"Imported {blogsImported} blog(s), {connectionsCreated} account connection(s) and {postsImported} published post(s) from OmniTumblr v1"
                    + (postsDropped > 0 ? $"; {postsDropped} unpublished v1 post(s) were not carried over" : "")
                    + ". Imported blogs start with autopilot off: review each blog's strategy, then switch autopilot on.";
                if (notes.Count > 0) text += " Notes: " + string.Join(" ", notes.Distinct());
                s.Engine.MigrationSummary = text;
                s.MarkEngine();
                s.AddEvent(EventLevel.Info, "migration", text, utc: now);
                return text;
            });
            return summary;
        }

        internal static OmniTumblrStrategy MapStrategy(JObject? c, List<string> notes, string blogName)
        {
            var strategy = OmniTumblrStrategy.CreateDefault();
            if (c == null) return strategy;

            strategy.Source = Str(c["ContentSource"]) switch
            {
                "MemeScraper" => ContentSourceKind.MemeScraper,
                "ContentFolder" => ContentSourceKind.Folder,
                _ => ContentSourceKind.None,
            };
            strategy.Folder.Path = Str(c["ContentFolderPath"]) ?? "";
            var allowed = (c["AllowedPostTypes"] as JArray)?.Select(t => t.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();
            if (allowed.Count > 0)
            {
                strategy.Folder.Images = allowed.Contains("Photo") || allowed.Contains("PhotoSet");
                strategy.Folder.Videos = allowed.Contains("Video");
            }
            var pick = Str(c["SelectionMode"]) == "Sequential" ? ContentPick.Oldest : ContentPick.Random;
            strategy.Folder.Pick = pick;

            switch (Str(c["CaptionMode"]))
            {
                case "Static":
                    strategy.CaptionMode = CaptionMode.Fixed;
                    strategy.FixedCaption = Str(c["StaticCaption"]) ?? "";
                    break;
                case "RandomFromList":
                    strategy.CaptionMode = CaptionMode.Rotate;
                    strategy.CaptionPool = (c["CandidateCaptions"] as JArray)?.Select(t => t.ToString()).Where(t => t.Length > 0).ToList() ?? new();
                    break;
                case "AIGenerated":
                    strategy.CaptionMode = CaptionMode.AI;
                    string? prompt = Str(c["AICaptionPrompt"]);
                    if (!string.IsNullOrWhiteSpace(prompt) && !prompt.StartsWith("Write a short engaging Tumblr caption", StringComparison.OrdinalIgnoreCase))
                        strategy.Ai.Instructions = prompt;
                    break;
            }

            var tags = (c["Tags"] as JArray)?.Select(t => t.ToString()).Where(t => t.Length > 0).ToList() ?? new();
            int maxTags = (int)(Long(c["MaxTagsPerPost"]) ?? 12);
            strategy.MaxTags = Math.Clamp(maxTags, 1, TumblrNpf.MaxTags);
            if (Bool(c["RotateTags"]) == true)
            {
                strategy.FixedTags = new();
                strategy.RotatingTags = tags;
                strategy.RotatingTagsPerPost = Math.Min(strategy.MaxTags, Math.Max(1, tags.Count));
            }
            else if (tags.Count > 0)
            {
                strategy.FixedTags = tags;
            }

            // v1 slots were whole UTC hours on chosen weekdays.
            var hours = (c["PreferredPostHoursUTC"] as JArray)?.Select(t => (int)(Long(t) ?? -1)).Where(h => h is >= 0 and < 24).Distinct().ToList() ?? new();
            var days = (c["ActiveDaysOfWeek"] as JArray)?.Select(t => (int)(Long(t) ?? -1)).Where(d => d is >= 0 and < 7).Distinct().ToList() ?? new();
            if (hours.Count > 0 && days.Count > 0)
            {
                strategy.TimeZone = "UTC";
                strategy.Slots = days.SelectMany(d => hours.Select(h => new WeeklySlot((DayOfWeek)d, h * 60))).ToList();
            }
            strategy.MaxPostsPerDay = Math.Clamp((int)(Long(c["PostsPerDay"]) ?? 3), 1, 50);
            strategy.MinGapMinutes = Math.Clamp((int)(Long(c["MinIntervalMinutes"]) ?? 30), 0, 24 * 60);
            strategy.JitterMinutes = Math.Clamp((int)(Long(c["ScheduleRandomOffsetMinutes"]) ?? 7), 0, 60);

            string? niche = Str(c["MemeScraperNicheFilter"]);
            if (!string.IsNullOrWhiteSpace(niche))
            {
                strategy.MemeScraper.Niches = new List<string> { niche.Trim() };
                notes.Add($"@{blogName}'s v1 MemeScraper filter \"{niche.Trim()}\" became a niche filter — check it matches a MemeScraper niche.");
            }
            return strategy;
        }

        private static List<JObject> ReadAll(string directory, Action<string> log)
        {
            var result = new List<JObject>();
            if (!Directory.Exists(directory)) return result;
            foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    if (JToken.Parse(File.ReadAllText(file)) is JObject obj) result.Add(obj);
                }
                catch (Exception ex)
                {
                    log($"OmniTumblr migration: skipped unreadable v1 file {Path.GetFileName(file)}: {ex.Message}");
                }
            }
            return result;
        }

        private static string? Str(JToken? t) => TumblrApiClient.Str(t) is string s && s.Length > 0 ? s : null;
        private static long? Long(JToken? t) => TumblrApiClient.Long(t);
        private static bool? Bool(JToken? t) => TumblrApiClient.Bool(t);

        private static DateTime? Date(JToken? t)
        {
            if (t == null || t.Type is JTokenType.Null or JTokenType.Undefined) return null;
            if (t.Type == JTokenType.Date) return OmniTumblrScheduleMath.AsUtc(t.Value<DateTime>());
            return DateTime.TryParse(t.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        }
    }
}
