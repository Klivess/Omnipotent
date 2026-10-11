using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniGram.Models;

namespace Omnipotent.Services.OmniGram.Engine
{
    /// <summary>
    /// Imports OmniGram v1's data once: each account (as "awaiting connection" — v1 logged in with passwords
    /// through a private-API library, v2 uses Instagram's official login), its content settings mapped onto a
    /// v2 strategy, its follower history, its published posts, and every piece of content it already used, so
    /// nothing is reposted. Passwords are never copied. Imported accounts arrive with autopilot off. The v1
    /// files stay where they were.
    /// </summary>
    internal static class OmniGramMigration
    {
        private sealed class V1Account
        {
            public string? AccountId { get; set; }
            public string? Username { get; set; }
            public string? Password { get; set; }
            public bool IsActive { get; set; } = true;
            public bool IsPaused { get; set; }
            public DateTime AddedDate { get; set; }
            public DateTime? LastPostTime { get; set; }
            public List<string>? Tags { get; set; }
            public string? Notes { get; set; }
            public int FollowerCount { get; set; }
            public int FollowingCount { get; set; }
            public int MediaCount { get; set; }
            public string? ProfilePicUrl { get; set; }
            public string? Biography { get; set; }
            public string? LoginStatus { get; set; }
            public V1Config? ContentConfig { get; set; }
        }

        private sealed class V1Config
        {
            public string? ContentSource { get; set; }
            public List<string>? AllowedContentTypes { get; set; }
            public string? ContentFolderPath { get; set; }
            public string? SelectionMode { get; set; }
            public string? CaptionMode { get; set; }
            public string? StaticCaption { get; set; }
            public List<string>? CandidateCaptions { get; set; }
            public string? AICaptionPrompt { get; set; }
            public List<string>? Hashtags { get; set; }
            public bool RotateHashtags { get; set; }
            public int MaxHashtagsPerPost { get; set; } = 20;
            public int PostsPerDay { get; set; } = 3;
            public int MinIntervalMinutes { get; set; } = 120;
            public List<int>? PreferredPostHoursUTC { get; set; }
            public List<int>? ActiveDaysOfWeek { get; set; }
            public int ScheduleRandomOffsetMinutes { get; set; } = 30;
            public string? MemeScraperNicheFilter { get; set; }
            public List<string>? UsedContentPaths { get; set; }
        }

        private sealed class V1Post
        {
            public string? PostId { get; set; }
            public string? AccountId { get; set; }
            public string? ContentType { get; set; }
            public string? Caption { get; set; }
            public List<string>? Hashtags { get; set; }
            public DateTime ScheduledTime { get; set; }
            public DateTime? PostedTime { get; set; }
            public string? Status { get; set; }
            public string? SourceType { get; set; }
            public string? SourceId { get; set; }
        }

        private sealed class V1Snapshot
        {
            public DateTime Timestamp { get; set; }
            public int FollowerCount { get; set; }
            public int FollowingCount { get; set; }
            public int MediaCount { get; set; }
        }

        /// <summary>Runs once (recorded in engine.json). Returns a summary, or null when nothing was done.</summary>
        public static string? MigrateV1(OmniGramStore store, string v1Root, DateTime nowUtc, Action<string> log)
        {
            if (store.Read(s => s.Engine.MigratedV1Utc) != null) return null;
            string accountsDir = Path.Combine(v1Root, "Accounts");
            string postsDir = Path.Combine(v1Root, "Posts");
            string analyticsDir = Path.Combine(v1Root, "Analytics");

            var v1Accounts = ReadAll<V1Account>(accountsDir, "*.json", log).Where(a => !string.IsNullOrWhiteSpace(a.Username)).ToList();
            var v1Posts = ReadAll<V1Post>(postsDir, "*.json", log).ToList();
            bool passwordsOnDisk = v1Accounts.Any(a => !string.IsNullOrEmpty(a.Password));

            int accounts = 0, published = 0, used = 0, snapshots = 0;
            store.Mutate(s =>
            {
                foreach (var v1 in v1Accounts)
                {
                    string username = v1.Username!.Trim().TrimStart('@');
                    if (s.AccountByUsername(username) != null) continue;
                    var cfg = v1.ContentConfig ?? new V1Config();
                    var account = new OmniGramAccount
                    {
                        Username = username,
                        AddedUtc = v1.AddedDate.Year > 2000 ? DateTime.SpecifyKind(v1.AddedDate, DateTimeKind.Utc) : nowUtc,
                        Connection = ConnectionState.AwaitingConnection,
                        ConnectionDetail = "Imported from OmniGram v1. Connect it with Instagram to resume posting.",
                        Autopilot = false,
                        Paused = v1.IsPaused || !v1.IsActive,
                        Notes = string.IsNullOrWhiteSpace(v1.Notes) ? null : v1.Notes,
                        Tags = v1.Tags ?? new List<string>(),
                        ProfilePictureUrl = string.IsNullOrWhiteSpace(v1.ProfilePicUrl) ? null : v1.ProfilePicUrl,
                        Biography = string.IsNullOrWhiteSpace(v1.Biography) ? null : v1.Biography,
                        Strategy = MapStrategy(cfg),
                        Legacy = new LegacyAccountInfo { V1AccountId = v1.AccountId, ImportedUtc = nowUtc, V1LoginStatus = v1.LoginStatus },
                    };
                    if (v1.FollowerCount > 0) account.Stats.Followers = v1.FollowerCount;
                    if (v1.FollowingCount > 0) account.Stats.Follows = v1.FollowingCount;
                    if (v1.MediaCount > 0) account.Stats.MediaCount = v1.MediaCount;
                    if (v1.LastPostTime is DateTime lastPost) account.Stats.LastPublishedUtc = DateTime.SpecifyKind(lastPost, DateTimeKind.Utc);

                    // Content the account already used must never come back.
                    foreach (string path in cfg.UsedContentPaths ?? new List<string>())
                    {
                        try
                        {
                            var file = new FileInfo(path);
                            if (file.Exists && account.UsedContentKeys.Add(OmniGramContentSources.FileKey(file))) used++;
                        }
                        catch { }
                    }

                    s.Accounts[account.AccountId] = account;
                    var posts = s.PostsOf(account.AccountId);
                    var insights = s.InsightsOf(account.AccountId);

                    foreach (var p in v1Posts.Where(p => p.AccountId == v1.AccountId))
                    {
                        if (p.SourceType == "MemeScraper" && !string.IsNullOrWhiteSpace(p.SourceId) && p.Status is "Posted" or "Queued" or "Posting")
                            if (account.UsedContentKeys.Add(OmniGramContentSources.ReelKey(p.SourceId))) used++;
                        if (p.Status != "Posted" || p.PostedTime == null) continue;
                        posts.Add(new OmniGramPost
                        {
                            AccountId = account.AccountId,
                            Status = PostStatus.Published,
                            Origin = PostOrigin.Imported,
                            Format = p.ContentType switch { "Photo" => PostFormat.Photo, "Story" => PostFormat.Story, "Carousel" => PostFormat.Carousel, _ => PostFormat.Reel },
                            CreatedUtc = DateTime.SpecifyKind(p.ScheduledTime, DateTimeKind.Utc),
                            UpdatedUtc = nowUtc,
                            ScheduledUtc = DateTime.SpecifyKind(p.ScheduledTime, DateTimeKind.Utc),
                            PublishedUtc = DateTime.SpecifyKind(p.PostedTime.Value, DateTimeKind.Utc),
                            Caption = p.Caption,
                            Hashtags = IgCaption.NormalizeHashtags(p.Hashtags, IgCaption.MaxHashtags),
                            Approved = true,
                            Content = string.IsNullOrWhiteSpace(p.SourceId) ? null : new ContentRef
                            {
                                Kind = p.SourceType == "MemeScraper" ? ContentSourceKind.MemeScraper : p.SourceType == "ContentFolder" ? ContentSourceKind.Folder : ContentSourceKind.None,
                                Key = p.SourceType == "MemeScraper" ? OmniGramContentSources.ReelKey(p.SourceId) : "v1:" + p.SourceId,
                            },
                            Publish = new PublishProgress { Stage = PublishStage.Published },
                        });
                        published++;
                    }
                    account.Legacy.V1PostedCount = posts.Count;

                    // Follower history, for the charts' continuity.
                    if (!string.IsNullOrEmpty(v1.AccountId))
                    {
                        foreach (var snap in ReadAll<V1Snapshot>(Path.Combine(analyticsDir, v1.AccountId), "*.json", log).OrderBy(x => x.Timestamp))
                        {
                            if (snap.Timestamp.Year < 2000) continue;
                            insights.Snapshots.Add(new AccountSnapshot
                            {
                                Utc = DateTime.SpecifyKind(snap.Timestamp, DateTimeKind.Utc),
                                Followers = snap.FollowerCount,
                                Follows = snap.FollowingCount,
                                MediaCount = snap.MediaCount,
                            });
                            snapshots++;
                        }
                    }
                    s.MarkAccount(account.AccountId);
                    s.MarkPosts(account.AccountId);
                    s.MarkInsights(account.AccountId);
                    accounts++;
                }

                s.Engine.MigratedV1Utc = nowUtc;
                s.Engine.V1PasswordFilesPresent = passwordsOnDisk;
                s.Engine.MigrationSummary = accounts == 0
                    ? "No OmniGram v1 accounts to import."
                    : $"Imported {accounts} v1 account(s) (awaiting connection), {published} published post(s), {used} used content item(s) and {snapshots} follower reading(s).";
                s.MarkEngine();
                if (accounts > 0)
                    s.AddEvent(EventLevel.Info, "migration.v1", s.Engine.MigrationSummary + " Connect each account with Instagram's official login, review its strategy, then switch autopilot on.", utc: nowUtc);
            });
            return store.Read(s => s.Engine.MigrationSummary);
        }

        /// <summary>
        /// v1's weekly pattern was "these days × these UTC hours, N per day"; v2 slots are local-time weekly
        /// slots, so the mapping keeps UTC and the first N preferred hours on each active day.
        /// </summary>
        private static OmniGramStrategy MapStrategy(V1Config cfg)
        {
            var strategy = OmniGramStrategy.CreateDefault();
            strategy.TimeZone = "UTC";
            var hours = (cfg.PreferredPostHoursUTC ?? new List<int> { 9, 13, 18 }).Where(h => h is >= 0 and <= 23).Distinct().OrderBy(h => h).Take(Math.Clamp(cfg.PostsPerDay, 1, 10)).ToList();
            var days = (cfg.ActiveDaysOfWeek ?? new List<int> { 0, 1, 2, 3, 4, 5, 6 }).Where(d => d is >= 0 and <= 6).Distinct().ToList();
            strategy.Slots = days.SelectMany(d => hours.Select(h => new WeeklySlot((DayOfWeek)d, h * 60))).ToList();
            strategy.JitterMinutes = Math.Clamp(cfg.ScheduleRandomOffsetMinutes, 0, 120);
            strategy.MinGapMinutes = Math.Clamp(cfg.MinIntervalMinutes, 0, 24 * 60);
            strategy.MaxPostsPerDay = Math.Clamp(cfg.PostsPerDay, 1, 50);

            switch (cfg.ContentSource)
            {
                case "ContentFolder":
                    strategy.Source = ContentSourceKind.Folder;
                    var types = cfg.AllowedContentTypes ?? new List<string> { "Reel" };
                    strategy.Folder = new FolderContentSettings
                    {
                        Path = cfg.ContentFolderPath ?? "",
                        Images = types.Contains("Photo") || types.Contains("Carousel"),
                        Videos = types.Contains("Reel") || types.Contains("Story"),
                        Pick = cfg.SelectionMode == "Sequential" ? ContentPick.Oldest : ContentPick.Random,
                    };
                    if (!strategy.Folder.Images && !strategy.Folder.Videos) strategy.Folder.Videos = true;
                    break;
                case "ManualUpload":
                    strategy.Source = ContentSourceKind.None;
                    break;
                default:
                    strategy.Source = ContentSourceKind.MemeScraper;
                    if (!string.IsNullOrWhiteSpace(cfg.MemeScraperNicheFilter)) strategy.MemeScraper.Niches = new List<string> { cfg.MemeScraperNicheFilter.Trim() };
                    break;
            }

            switch (cfg.CaptionMode)
            {
                case "Static":
                    strategy.CaptionMode = string.IsNullOrWhiteSpace(cfg.StaticCaption) ? CaptionMode.None : CaptionMode.Fixed;
                    strategy.FixedCaption = cfg.StaticCaption ?? "";
                    break;
                case "RandomFromList":
                    strategy.CaptionPool = cfg.CandidateCaptions ?? new List<string>();
                    strategy.CaptionMode = strategy.CaptionPool.Count > 0 ? CaptionMode.Rotate : CaptionMode.None;
                    break;
                default:
                    strategy.CaptionMode = CaptionMode.AI;
                    // v1's "prompt" was posted verbatim as the caption; it survives as guidance for the real model.
                    string prompt = (cfg.AICaptionPrompt ?? "").Trim();
                    if (prompt.Length > 0 && !prompt.Equals("Write a short engaging Instagram caption for this content.", StringComparison.OrdinalIgnoreCase))
                        strategy.Ai.Instructions = prompt;
                    break;
            }

            var tags = IgCaption.NormalizeHashtags(cfg.Hashtags, 300);
            if (cfg.RotateHashtags && tags.Count > 0)
            {
                strategy.FixedHashtags = new List<string>();
                strategy.RotatingHashtags = tags;
                strategy.RotatingHashtagsPerPost = Math.Clamp(cfg.MaxHashtagsPerPost, 1, IgCaption.MaxHashtags);
            }
            else if (tags.Count > 0)
            {
                strategy.FixedHashtags = tags.Take(IgCaption.MaxHashtags).ToList();
            }
            strategy.MaxHashtags = Math.Clamp(Math.Max(cfg.MaxHashtagsPerPost, strategy.FixedHashtags.Count), 1, IgCaption.MaxHashtags);
            return OmniGramStrategyRules.Normalize(strategy);
        }

        private static IEnumerable<T> ReadAll<T>(string dir, string pattern, Action<string> log) where T : class
        {
            if (!Directory.Exists(dir)) yield break;
            foreach (string file in Directory.EnumerateFiles(dir, pattern))
            {
                T? value = null;
                try { value = JsonConvert.DeserializeObject<T>(File.ReadAllText(file)); }
                catch (Exception ex) { log($"OmniGram: skipped unreadable v1 file {Path.GetFileName(file)} ({ex.Message})."); }
                if (value != null) yield return value;
            }
        }

        /// <summary>
        /// Removes what v1 left on disk that could log in to Instagram: the plain-text "Password" in each account
        /// file (the rest of the file is kept) and the private-API session cookies. Only on request.
        /// </summary>
        public static (int Files, int Sessions) RemoveV1Credentials(string v1Root)
        {
            int files = 0, sessions = 0;
            string accountsDir = Path.Combine(v1Root, "Accounts");
            if (Directory.Exists(accountsDir))
            {
                foreach (string file in Directory.EnumerateFiles(accountsDir, "*.json"))
                {
                    try
                    {
                        var obj = JObject.Parse(File.ReadAllText(file));
                        if (obj["Password"] is JValue pw && !string.IsNullOrEmpty((string?)pw))
                        {
                            obj.Remove("Password");
                            OmniGramStore.WriteAtomic(file, obj.ToString(Formatting.Indented));
                            files++;
                        }
                    }
                    catch { }
                }
            }
            string sessionsDir = Path.Combine(v1Root, "Sessions");
            if (Directory.Exists(sessionsDir))
            {
                foreach (string file in Directory.EnumerateFiles(sessionsDir))
                {
                    try { File.Delete(file); sessions++; } catch { }
                }
            }
            return (files, sessions);
        }

        /// <summary>Whether any v1 account file still holds a password.</summary>
        public static bool V1PasswordsPresent(string v1Root)
        {
            string accountsDir = Path.Combine(v1Root, "Accounts");
            if (!Directory.Exists(accountsDir)) return false;
            foreach (string file in Directory.EnumerateFiles(accountsDir, "*.json"))
            {
                try
                {
                    if (JObject.Parse(File.ReadAllText(file))["Password"] is JValue pw && !string.IsNullOrEmpty((string?)pw)) return true;
                }
                catch { }
            }
            return false;
        }
    }
}
