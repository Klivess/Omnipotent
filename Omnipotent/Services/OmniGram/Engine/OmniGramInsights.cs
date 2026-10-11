using Omnipotent.Services.OmniGram.Api;
using Omnipotent.Services.OmniGram.Models;

namespace Omnipotent.Services.OmniGram.Engine
{
    /// <summary>
    /// Bounded bookkeeping for an account's analytics: follower snapshots (hourly for two weeks, daily after
    /// that), an index of the account's recent media with its numbers, daily account insights, and recent
    /// comments. All callers hold the store lock.
    /// </summary>
    internal static class OmniGramInsights
    {
        public const int MaxIndexedMedia = 600;
        public const int MaxDays = 800;
        public const int MaxComments = 400;
        public const int MaxSeenCommentIds = 5000;
        private static readonly TimeSpan DenseWindow = TimeSpan.FromDays(14);

        public static void RecordSnapshot(AccountInsights insights, DateTime nowUtc, long? followers, long? follows, long? media)
        {
            if (followers == null && follows == null && media == null) return;
            var last = insights.Snapshots.LastOrDefault();
            if (last != null && nowUtc - last.Utc < TimeSpan.FromMinutes(55))
            {
                // Same hour: refresh the latest point rather than stacking near-duplicates.
                if (followers.HasValue) last.Followers = followers;
                if (follows.HasValue) last.Follows = follows;
                if (media.HasValue) last.MediaCount = media;
                last.Utc = nowUtc;
            }
            else
            {
                insights.Snapshots.Add(new AccountSnapshot
                {
                    Utc = nowUtc,
                    Followers = followers ?? last?.Followers,
                    Follows = follows ?? last?.Follows,
                    MediaCount = media ?? last?.MediaCount,
                });
            }
            Compact(insights.Snapshots, nowUtc);
        }

        internal static void Compact(List<AccountSnapshot> snapshots, DateTime nowUtc)
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

        /// <summary>Upserts media into the index (newest first, bounded). Returns how many were new.</summary>
        public static int MergeIndex(AccountInsights insights, IEnumerable<IgMediaItem> items, Func<IgMediaItem, string?> omniPostIdFor, DateTime nowUtc)
        {
            var byId = insights.Index.ToDictionary(p => p.Id, StringComparer.Ordinal);
            int added = 0;
            foreach (var m in items)
            {
                if (string.IsNullOrEmpty(m.Id)) continue;
                if (!byId.TryGetValue(m.Id, out var entry))
                {
                    entry = new IndexedMedia { Id = m.Id };
                    byId[m.Id] = entry;
                    added++;
                }
                if (m.TimestampUtc > DateTime.MinValue) entry.PublishedUtc = m.TimestampUtc;
                entry.MediaType = m.MediaType ?? entry.MediaType;
                entry.ProductType = m.MediaProductType ?? entry.ProductType;
                entry.Permalink = m.Permalink ?? entry.Permalink;
                entry.Shortcode = m.Shortcode ?? entry.Shortcode;
                entry.Caption = m.Caption is { Length: > 300 } caption ? caption[..300] : m.Caption ?? entry.Caption;
                entry.ThumbnailUrl = m.ThumbnailUrl ?? (m.MediaType == "IMAGE" ? m.MediaUrl : null) ?? entry.ThumbnailUrl;
                entry.Likes = m.LikeCount ?? entry.Likes;
                entry.Comments = m.CommentsCount ?? entry.Comments;
                entry.OmniPostId = omniPostIdFor(m) ?? entry.OmniPostId;
                entry.LastSeenUtc = nowUtc;
            }
            insights.Index = byId.Values.OrderByDescending(p => p.PublishedUtc).Take(MaxIndexedMedia).ToList();
            return added;
        }

        public static void ApplyMediaInsights(IndexedMedia entry, IgInsightValues values, DateTime nowUtc)
        {
            entry.Views = values.Long("views") ?? entry.Views;
            entry.Reach = values.Long("reach") ?? entry.Reach;
            entry.Shares = values.Long("shares") ?? entry.Shares;
            entry.Saves = values.Long("saved") ?? entry.Saves;
            entry.TotalInteractions = values.Long("total_interactions") ?? entry.TotalInteractions;
            // The media list's live counts are newer than insights (which lag): keep the larger.
            entry.Likes = OmniGramAnalyticsSync.Max(entry.Likes, values.Long("likes"));
            entry.Comments = OmniGramAnalyticsSync.Max(entry.Comments, values.Long("comments"));
            entry.InsightsSyncedUtc = nowUtc;
        }

        /// <summary>Upserts an account-insights day.</summary>
        public static void MergeDay(AccountInsights insights, string day, IgInsightValues totals, IgInsightValues? follows, DateTime nowUtc)
        {
            if (!insights.Days.TryGetValue(day, out var row))
            {
                row = new AccountDay { Day = day };
                insights.Days[day] = row;
            }
            row.Reach = totals.Long("reach") ?? row.Reach;
            row.Views = totals.Long("views") ?? row.Views;
            row.AccountsEngaged = totals.Long("accounts_engaged") ?? row.AccountsEngaged;
            row.TotalInteractions = totals.Long("total_interactions") ?? row.TotalInteractions;
            row.Likes = totals.Long("likes") ?? row.Likes;
            row.Comments = totals.Long("comments") ?? row.Comments;
            row.Shares = totals.Long("shares") ?? row.Shares;
            row.Saves = totals.Long("saves") ?? row.Saves;
            row.Replies = totals.Long("replies") ?? row.Replies;
            row.Reposts = totals.Long("reposts") ?? row.Reposts;
            row.ProfileLinksTaps = totals.Long("profile_links_taps") ?? row.ProfileLinksTaps;
            if (follows != null && follows.Breakdowns.TryGetValue("follows_and_unfollows", out var byType))
            {
                long followed = 0, unfollowed = 0;
                foreach (var (key, value) in byType)
                {
                    string k = key.ToUpperInvariant();
                    if (k.Contains("NON_FOLLOWER") || k.Contains("UNFOLLOW")) unfollowed += (long)value;
                    else if (k.Contains("FOLLOW")) followed += (long)value;
                }
                row.Follows = followed;
                row.Unfollows = unfollowed;
            }
            else if (follows?.Long("follows_and_unfollows") is long net && row.Follows == null)
            {
                row.Follows = net;
            }
            row.SyncedUtc = nowUtc;
            while (insights.Days.Count > MaxDays) insights.Days.Remove(insights.Days.Keys.First());
        }

        /// <summary>Upserts the comments on one media. Returns how many were not seen before (excluding our own).</summary>
        public static int MergeComments(AccountInsights insights, string mediaId, string? omniPostId, IEnumerable<IgComment> comments, string ownUsername)
        {
            var seen = new HashSet<string>(insights.SeenCommentIds, StringComparer.Ordinal);
            var byId = insights.Comments.ToDictionary(c => c.Id, StringComparer.Ordinal);
            int fresh = 0;

            void Upsert(IgComment c, string? parentId)
            {
                if (string.IsNullOrEmpty(c.Id)) return;
                bool own = string.Equals(c.Username, ownUsername, StringComparison.OrdinalIgnoreCase);
                if (!byId.TryGetValue(c.Id, out var item))
                {
                    item = new CommentItem { Id = c.Id };
                    byId[c.Id] = item;
                }
                item.MediaId = mediaId;
                item.OmniPostId = omniPostId ?? item.OmniPostId;
                item.ParentId = parentId;
                item.Username = c.Username;
                item.Text = c.Text;
                item.Utc = c.TimestampUtc;
                item.LikeCount = c.LikeCount;
                item.Hidden = c.Hidden;
                item.Own = own;
                if (seen.Add(c.Id) && !own) fresh++;
            }

            foreach (var c in comments)
            {
                Upsert(c, null);
                foreach (var r in c.Replies) Upsert(r, c.Id);
                if (c.Replies.Any(r => string.Equals(r.Username, ownUsername, StringComparison.OrdinalIgnoreCase)) && byId.TryGetValue(c.Id, out var parent))
                    parent.Replied = true;
            }

            insights.Comments = byId.Values.OrderByDescending(c => c.Utc).Take(MaxComments).ToList();
            insights.SeenCommentIds = insights.SeenCommentIds.Concat(seen.Except(insights.SeenCommentIds)).TakeLast(MaxSeenCommentIds).ToList();
            return fresh;
        }
    }
}
