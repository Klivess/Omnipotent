using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    public sealed class ReachFindingDto
    {
        /// <summary>problem | warning | info | good</summary>
        public string Level { get; set; } = "info";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        /// <summary>A hint the UI turns into a button: strategy, schedule, profile.</summary>
        public string? Action { get; set; }
    }

    public sealed class TagHealthDto
    {
        public string Tag { get; set; } = "";
        /// <summary>fixed | rotating | ai | checked</summary>
        public string Source { get; set; } = "ai";
        public string Verdict { get; set; } = "Unknown";
        public double Score { get; set; }
        public double? PostsPerDay { get; set; }
        public double? TypicalNotes { get; set; }
        public double? ShareWithNotes { get; set; }
        public int Settled { get; set; }
        public DateTime? CheckedUtc { get; set; }
        /// <summary>This blog's posts carrying the tag in the last 90 days, and what they averaged.</summary>
        public int OurPosts { get; set; }
        public double? OurAvgNotes { get; set; }
        public string Summary { get; set; } = "";
        public List<string> Related { get; set; } = new();
    }

    public sealed class ReachReportDto
    {
        public string BlogId { get; set; } = "";
        /// <summary>Posts in the last 30 days that are at least a day old, so their notes mean something.</summary>
        public int Posts30d { get; set; }
        public int WithNotes30d { get; set; }
        public long Notes30d { get; set; }
        public double? AvgNotes30d { get; set; }
        public long? Followers { get; set; }
        public long? FollowersDelta30d { get; set; }
        public double PostsPerDay { get; set; }
        public bool TagResearchOn { get; set; }
        public SearchVisibility Search { get; set; } = new();
        public List<ReachFindingDto> Findings { get; set; } = new();
        public List<TagHealthDto> Tags { get; set; } = new();
        /// <summary>Tags other people put next to this blog's tags: candidates for its rotating pool.</summary>
        public List<string> RelatedTags { get; set; } = new();
    }

    /// <summary>
    /// Why a blog is or is not being seen, from what OmniTumblr already knows: its posts' notes, what Tumblr's
    /// listings say about its tags, how often it posts, what it posts, and how the blog page looks. Built from
    /// memory (callers hold the store lock); the lookups behind it are sync's and the planner's.
    /// </summary>
    internal static class OmniTumblrReach
    {
        public static TagHealthDto TagHealth(TagResearch? r, string tag, string source)
        {
            var dto = new TagHealthDto { Tag = OmniTumblrTagMath.Key(tag), Source = source };
            if (r == null)
            {
                dto.Summary = "Not checked yet.";
                return dto;
            }
            dto.Verdict = r.Verdict.ToString();
            dto.Score = r.Score;
            dto.PostsPerDay = r.PostsPerDay;
            dto.TypicalNotes = r.TypicalNotes;
            dto.ShareWithNotes = r.ShareWithNotes;
            dto.Settled = r.Settled;
            dto.CheckedUtc = r.CheckedUtc;
            dto.Summary = OmniTumblrTagMath.Describe(r);
            dto.Related = r.Related.ToList();
            return dto;
        }

        public static ReachReportDto Build(OmniTumblrState s, OmniTumblrBlog blog, DateTime now, bool tagResearchEnabled)
        {
            var strategy = blog.Strategy;
            var posts = s.PostsOf(blog.BlogId);
            var insights = s.InsightsOf(blog.BlogId);
            var published90 = posts.Where(p => p.Status == PostStatus.Published && !p.IsReblog && p.PublishedUtc > now.AddDays(-90)).ToList();
            var settled = published90.Where(p => p.PublishedUtc > now.AddDays(-30) && p.PublishedUtc <= now.AddDays(-1)).ToList();
            var report = new ReachReportDto
            {
                BlogId = blog.BlogId,
                Posts30d = settled.Count,
                WithNotes30d = settled.Count(p => p.Metrics.Notes > 0),
                Notes30d = settled.Sum(p => p.Metrics.Notes),
                AvgNotes30d = settled.Count > 0 ? Math.Round(settled.Average(p => (double)p.Metrics.Notes), 1) : null,
                Followers = blog.Stats.Followers,
                FollowersDelta30d = OmniTumblrViews.FollowerDelta(insights, now, TimeSpan.FromDays(30), blog.Stats.Followers),
                PostsPerDay = Math.Round(OmniTumblrScheduleMath.SlotsPerWeek(strategy) / 7.0, 1),
                TagResearchOn = tagResearchEnabled && strategy.ResearchTags,
                Search = OmniTumblrStore.Clone(blog.Health.Search ?? new SearchVisibility()),
            };

            TagResearch? Known(string tag) => s.Tags.TryGetValue(OmniTumblrTagMath.Key(tag), out var r) && r.Verdict != TagVerdict.Unknown ? r : null;

            // ── tag table: the blog's own tags first, then the suggested tags its recent posts carried ──
            var fixedTags = (strategy.FixedTags ?? new()).Select(OmniTumblrTagMath.Key).Where(t => t.Length > 0).Distinct().ToList();
            var rotating = (strategy.RotatingTags ?? new()).Select(OmniTumblrTagMath.Key).Where(t => t.Length > 0 && !fixedTags.Contains(t)).Distinct().ToList();
            var usage = published90.SelectMany(p => p.Tags.Select(OmniTumblrTagMath.Key).Distinct().Select(t => (Tag: t, p.Metrics.Notes)))
                .GroupBy(x => x.Tag)
                .ToDictionary(g => g.Key, g => (Posts: g.Count(), Avg: Math.Round(g.Average(x => (double)x.Notes), 1)), StringComparer.Ordinal);
            var suggested = usage.Keys.Where(t => !fixedTags.Contains(t) && !rotating.Contains(t))
                .OrderByDescending(t => usage[t].Avg).ThenByDescending(t => usage[t].Posts).ThenBy(t => t, StringComparer.Ordinal)
                .Take(30);
            foreach (var (tag, source) in fixedTags.Select(t => (t, "fixed")).Concat(rotating.Select(t => (t, "rotating"))).Concat(suggested.Select(t => (t, "ai"))))
            {
                var dto = TagHealth(Known(tag), tag, source);
                if (usage.TryGetValue(tag, out var u))
                {
                    dto.OurPosts = u.Posts;
                    dto.OurAvgNotes = u.Avg;
                }
                report.Tags.Add(dto);
            }
            var ownTags = fixedTags.Concat(rotating).ToHashSet(StringComparer.Ordinal);
            report.RelatedTags = fixedTags.Concat(rotating).Select(Known).Where(r => r != null)
                .SelectMany(r => r!.Related.Select((t, i) => (Tag: t, Rank: i)))
                .Where(x => !ownTags.Contains(x.Tag))
                .GroupBy(x => x.Tag)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Min(x => x.Rank))
                .Take(12)
                .Select(g => g.Key)
                .ToList();

            var findings = report.Findings;

            // ── is Tumblr listing the blog at all? ──
            var search = report.Search;
            if (search.Listed == false)
                findings.Add(new ReachFindingDto
                {
                    Level = "problem",
                    Title = "Tumblr is not listing this blog's posts under their tags",
                    Detail = $"The last {search.Misses} posts checked were missing from the listings of their own tags (latest check: #{search.Tag}, {search.CheckedUtc:d MMM HH:mm} UTC). " +
                        "Tumblr leaves a blog out of search when the account's email is unverified, the account is very new, or it has been flagged. Until that lifts, tags bring nobody. " +
                        "Open one of the blog's tags on tumblr.com while logged out to confirm, then check the account's email verification and contact Tumblr support.",
                });
            else if (search.Listed == true)
                findings.Add(new ReachFindingDto
                {
                    Level = "good",
                    Title = "Tumblr lists this blog's posts under their tags",
                    Detail = $"A recent post was found in the listing for #{search.Tag} on {search.LastListedUtc:d MMM}. The blog is not hidden from search.",
                });

            // ── are posts being seen? ──
            if (settled.Count >= 5)
            {
                int silent = settled.Count - report.WithNotes30d;
                double share = silent / (double)settled.Count;
                if (share >= 0.3)
                    findings.Add(new ReachFindingDto
                    {
                        Level = share >= 0.6 ? "problem" : "warning",
                        Title = $"{silent} of the last {settled.Count} posts got no notes at all",
                        Detail = "A blog with few followers is seen through its tags and little else, so a post nobody reacted to is usually a post nobody was shown. " +
                            "That makes this a tag problem before it is a content problem: see the tags below.",
                        Action = "strategy",
                    });
            }

            // ── the blog's own tags ──
            var fixedKnown = fixedTags.Select(t => (Tag: t, Research: Known(t))).ToList();
            var crowded = fixedKnown.Where(x => x.Research?.Verdict == TagVerdict.Crowded).ToList();
            if (crowded.Count > 0)
                findings.Add(new ReachFindingDto
                {
                    Level = crowded.Count == fixedTags.Count ? "problem" : "warning",
                    Title = crowded.Count == 1 ? $"#{crowded[0].Tag} is flooded" : $"{string.Join(", ", crowded.Select(c => "#" + c.Tag))} are flooded",
                    Detail = string.Join(" ", crowded.Select(c => $"#{c.Tag}: {Lower(OmniTumblrTagMath.Describe(c.Research!))}")) +
                        (crowded.Count == fixedTags.Count ? " Every post leads with these, so every post is buried almost at once. Keep one at most and give the other slots to tags with a following." : " Keep one flooded tag at most."),
                    Action = "strategy",
                });
            var dead = fixedTags.Concat(rotating).Select(t => (Tag: t, Research: Known(t))).Where(x => x.Research?.Verdict == TagVerdict.Dead).ToList();
            if (dead.Count > 0)
                findings.Add(new ReachFindingDto
                {
                    Level = "warning",
                    Title = $"{dead.Count} of the blog's own tags {(dead.Count == 1 ? "has" : "have")} nobody in {(dead.Count == 1 ? "it" : "them")}",
                    Detail = $"{string.Join(", ", dead.Take(8).Select(d => "#" + d.Tag))}: nobody else posts under {(dead.Count == 1 ? "this tag" : "these")}, so nobody browses {(dead.Count == 1 ? "it" : "them")}. Replace {(dead.Count == 1 ? "it" : "them")}.",
                    Action = "strategy",
                });
            if (fixedTags.Count + rotating.Count > 0 && fixedTags.Concat(rotating).All(t => Known(t) == null))
                findings.Add(new ReachFindingDto
                {
                    Level = "info",
                    Title = "The blog's tags have not been checked yet",
                    Detail = tagResearchEnabled ? "They are looked up on Tumblr within a few minutes of the engine starting, and again whenever they go stale. Refresh the blog to check them now." : "Tag research is switched off (OmniSetting OmniTumblr_TagResearchEnabled).",
                });

            // ── the caption model's tags ──
            if (strategy.CaptionMode == CaptionMode.AI && strategy.Ai.SuggestTags)
            {
                if (!report.TagResearchOn)
                    findings.Add(new ReachFindingDto
                    {
                        Level = "warning",
                        Title = "Suggested tags are not checked against Tumblr",
                        Detail = (tagResearchEnabled ? "Tag research is off for this blog. " : "Tag research is switched off (OmniSetting OmniTumblr_TagResearchEnabled). ") +
                            "Without it posts carry whatever tags the caption model comes up with, including ones nobody else has ever used.",
                        Action = "strategy",
                    });
                else
                {
                    var researched = published90.Where(p => p.CaptionInfo.TagsResearched).ToList();
                    int dropped = researched.Sum(p => p.CaptionInfo.DroppedTags.Count);
                    findings.Add(new ReachFindingDto
                    {
                        Level = "good",
                        Title = "Suggested tags are checked against Tumblr",
                        Detail = researched.Count == 0
                            ? "From the next post on, a suggested tag is used only if other people post under it, and the tags whose posts get the most notes go first."
                            : $"{researched.Count} post(s) so far went out with checked tags; {dropped} suggested tag(s) nobody else uses were left out.",
                    });
                }
                int room = Math.Min(strategy.Ai.MaxSuggestedTags, Math.Max(0, strategy.MaxTags - fixedTags.Count - Math.Min(strategy.RotatingTagsPerPost, rotating.Count)));
                if (room < 8)
                    findings.Add(new ReachFindingDto
                    {
                        Level = "warning",
                        Title = $"Only {room} suggested tag(s) fit on a post",
                        Detail = $"Tumblr lists a post under each of its first 20 tags, and each tag with a following is another place the post is seen. This blog allows {strategy.Ai.MaxSuggestedTags} suggested tags and {strategy.MaxTags} tags in all. " +
                            "Raise suggested tags to 10 or more and the tag limit to 20.",
                        Action = "strategy",
                    });
            }

            // ── how often ──
            int perWeek = OmniTumblrScheduleMath.SlotsPerWeek(strategy);
            if (blog.Autopilot && perWeek > 0 && perWeek < 14)
                findings.Add(new ReachFindingDto
                {
                    Level = perWeek < 7 ? "problem" : "warning",
                    Title = perWeek < 7 ? $"Posts {perWeek} time(s) a week" : perWeek == 7 ? "Posts once a day" : $"Posts {perWeek} times a week",
                    Detail = "Each post is one entry near the top of each of its tags, for as long as that tag takes to move on. At this rate the blog is in front of people for a small part of the day and absent for the rest. " +
                        "Three or four posts a day, spread across the hours when the tags are busiest, gives every tag several entries a day. Check the content runway first: more posts use more clips.",
                    Action = "schedule",
                });

            // ── does it take part? ──
            int reblogs30 = posts.Count(p => p.IsReblog && p.Status == PostStatus.Published && p.PublishedUtc > now.AddDays(-30));
            if (!strategy.Reblogs.Enabled && reblogs30 == 0)
                findings.Add(new ReachFindingDto
                {
                    Level = "warning",
                    Title = "The blog only posts; it never reblogs",
                    Detail = "Reblogging is how a blog takes part on Tumblr: the original poster is told, the blog appears in the post's notes, and its page stops looking like one account's upload folder. " +
                        "Curated reblogs pick a few well-liked posts a day from the blog's tags, screen each one, and by default hold them for your approval.",
                    Action = "strategy",
                });
            else if (strategy.Reblogs.Enabled)
                findings.Add(new ReachFindingDto
                {
                    Level = "good",
                    Title = "Curated reblogs are on",
                    Detail = reblogs30 == 0
                        ? $"Up to {strategy.Reblogs.PerDay} a day{(strategy.Reblogs.RequireApproval ? ", each waiting in the queue for approval" : "")}. None has gone out yet."
                        : $"{reblogs30} reblog(s) in the last 30 days, up to {strategy.Reblogs.PerDay} a day.",
                });

            // ── what ──
            var recentKinds = settled.Count >= 5 ? settled : published90;
            var listed = fixedKnown.Where(x => x.Research != null).Select(x => x.Research!).ToList();
            int images = listed.Sum(r => r.Images), videos = listed.Sum(r => r.Videos), texts = listed.Sum(r => r.Texts);
            if (recentKinds.Count >= 5 && recentKinds.All(p => p.Kind == PostKind.Video) && images + videos + texts >= 15 && images >= 0.6 * (images + videos + texts))
                findings.Add(new ReachFindingDto
                {
                    Level = "warning",
                    Title = "Every post is a video, in tags that are mostly images",
                    Detail = $"Under {string.Join(" and ", fixedKnown.Where(x => x.Research != null).Take(3).Select(x => "#" + x.Tag))}, {100 * images / (images + videos + texts)}% of recent posts are images and {100 * videos / (images + videos + texts)}% are videos. " +
                        "Tumblr shows a video as a muted preview, so a clip whose joke is in the sound reads as nothing while scrolling; an image or GIF is the whole joke at a glance and is what gets reblogged. " +
                        "Mixing in image posts (a folder or the blog's library as a content source) is worth a try.",
                    Action = "strategy",
                });

            // ── the blog page ──
            var unfinished = new List<string>();
            if (string.IsNullOrWhiteSpace(OmniTumblrCaptioner.StripHtml(blog.Description ?? ""))) unfinished.Add("no description");
            if (blog.Stats.AvatarUrl?.Contains("default_avatar", StringComparison.OrdinalIgnoreCase) == true) unfinished.Add("the default avatar");
            if (blog.Stats.HeaderImageUrl?.Contains("default_header", StringComparison.OrdinalIgnoreCase) == true) unfinished.Add("the default header image");
            if (unfinished.Count > 0 && blog.Stats.InfoSyncedUtc != null)
                findings.Add(new ReachFindingDto
                {
                    Level = "warning",
                    Title = "The blog page looks unfinished: " + string.Join(", ", unfinished),
                    Detail = "Someone who likes a post opens the blog before following it. A page with " + string.Join(" and ", unfinished) +
                        " looks like a throwaway or a bot, and Tumblr users block those on sight. Set them in the blog's settings on tumblr.com (the API cannot).",
                    Action = "profile",
                });

            int Order(string level) => level switch { "problem" => 0, "warning" => 1, "info" => 2, _ => 3 };
            report.Findings = findings.OrderBy(f => Order(f.Level)).ToList();
            return report;
        }

        private static string Lower(string sentence) => sentence.Length == 0 ? sentence : char.ToLowerInvariant(sentence[0]) + sentence[1..];
    }
}
