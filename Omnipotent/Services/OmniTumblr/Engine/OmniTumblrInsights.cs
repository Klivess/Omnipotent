using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    /// <summary>
    /// Bounded bookkeeping for a blog's analytics: follower/post-count snapshots (hourly for two weeks,
    /// daily after that), an index of the blog's recent posts, and its activity feed with daily counts.
    /// All callers hold the store lock.
    /// </summary>
    internal static class OmniTumblrInsights
    {
        public const int MaxIndexedPosts = 500;
        public const int MaxRecentActivity = 300;
        public const int MaxSeenActivityIds = 3000;
        public const int MaxDailyActivityDays = 400;
        private static readonly TimeSpan DenseWindow = TimeSpan.FromDays(14);

        public static void RecordSnapshot(BlogInsights insights, DateTime nowUtc, long? followers, long? posts)
        {
            if (followers == null && posts == null) return;
            var last = insights.Snapshots.LastOrDefault();
            if (last != null && nowUtc - last.Utc < TimeSpan.FromMinutes(55))
            {
                // Same hour: refresh the latest point rather than stacking near-duplicates.
                if (followers.HasValue) last.Followers = followers;
                if (posts.HasValue) last.Posts = posts;
                last.Utc = nowUtc;
            }
            else
            {
                insights.Snapshots.Add(new BlogSnapshot
                {
                    Utc = nowUtc,
                    Followers = followers ?? last?.Followers,
                    Posts = posts ?? last?.Posts,
                });
            }
            Compact(insights.Snapshots, nowUtc);
        }

        internal static void Compact(List<BlogSnapshot> snapshots, DateTime nowUtc)
        {
            if (snapshots.Count < 400) return;
            DateTime denseFrom = nowUtc - DenseWindow;
            var old = snapshots.Where(s => s.Utc < denseFrom)
                .GroupBy(s => s.Utc.Date)
                .Select(g => g.OrderBy(s => s.Utc).Last());
            var compacted = old.Concat(snapshots.Where(s => s.Utc >= denseFrom)).OrderBy(s => s.Utc).ToList();
            if (compacted.Count > 1500) compacted = compacted.Skip(compacted.Count - 1500).ToList();
            snapshots.Clear();
            snapshots.AddRange(compacted);
        }

        /// <summary>Upserts posts into the index (newest first, bounded). Returns how many were new.</summary>
        public static int MergeIndex(BlogInsights insights, IEnumerable<TumblrPostSummary> posts, Func<TumblrPostSummary, string?> omniPostIdFor)
        {
            var byId = insights.Index.ToDictionary(p => p.Id, StringComparer.Ordinal);
            int added = 0;
            foreach (var p in posts)
            {
                if (string.IsNullOrEmpty(p.Id)) continue;
                if (!byId.TryGetValue(p.Id, out var entry))
                {
                    entry = new IndexedPost { Id = p.Id };
                    byId[p.Id] = entry;
                    added++;
                }
                if (p.PublishedUtc > DateTime.MinValue) entry.PublishedUtc = p.PublishedUtc;
                entry.Type = p.Type ?? entry.Type;
                entry.Notes = p.NoteCount;
                entry.Tags = p.Tags;
                entry.Url = p.PostUrl ?? entry.Url;
                entry.Slug = p.Slug ?? entry.Slug;
                entry.Summary = p.Summary ?? entry.Summary;
                entry.OmniPostId = omniPostIdFor(p) ?? entry.OmniPostId;
            }
            insights.Index = byId.Values.OrderByDescending(p => p.PublishedUtc).Take(MaxIndexedPosts).ToList();
            return added;
        }

        /// <summary>Adds unseen activity items to the feed and daily counts. Returns how many were new.</summary>
        public static int MergeActivity(BlogActivity activity, IEnumerable<TumblrNotification> items)
        {
            var seen = new HashSet<string>(activity.SeenIds, StringComparer.Ordinal);
            var fresh = new List<ActivityItem>();
            foreach (var n in items)
            {
                if (string.IsNullOrEmpty(n.Id) || !seen.Add(n.Id)) continue;
                fresh.Add(new ActivityItem { Id = n.Id, Type = n.Type, Utc = n.Utc, FromBlog = n.FromBlog, TargetPostId = n.TargetPostId, Text = n.Text });
            }
            if (fresh.Count == 0) return 0;

            foreach (var item in fresh)
            {
                string day = item.Utc.ToString("yyyy-MM-dd");
                if (!activity.Daily.TryGetValue(day, out var counts))
                {
                    counts = new Dictionary<string, int>(StringComparer.Ordinal);
                    activity.Daily[day] = counts;
                }
                string type = NormalizeActivityType(item.Type);
                counts[type] = counts.TryGetValue(type, out var c) ? c + 1 : 1;
                if (activity.NewestSeenUtc == null || item.Utc > activity.NewestSeenUtc) activity.NewestSeenUtc = item.Utc;
            }

            activity.Recent = fresh.Concat(activity.Recent).OrderByDescending(a => a.Utc).Take(MaxRecentActivity).ToList();
            activity.SeenIds = activity.SeenIds.Concat(fresh.Select(f => f.Id)).TakeLast(MaxSeenActivityIds).ToList();
            while (activity.Daily.Count > MaxDailyActivityDays) activity.Daily.Remove(activity.Daily.Keys.First());
            return fresh.Count;
        }

        /// <summary>Folds Tumblr's activity types into the buckets the dashboard charts.</summary>
        public static string NormalizeActivityType(string? type) => (type ?? "").ToLowerInvariant() switch
        {
            "like" => "like",
            "reblog_naked" or "reblog_with_content" or "reblog" => "reblog",
            "reply" or "conversational_note" => "reply",
            "follow" => "follow",
            "mention_in_reply" or "mention_in_post" => "mention",
            "ask" or "answered_ask" => "ask",
            _ => "other",
        };
    }
}
