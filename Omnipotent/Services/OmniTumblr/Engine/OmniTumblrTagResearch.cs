using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Models;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    /// <summary>
    /// Judges a tag from Tumblr's own listing of it. A blog with few followers is seen through its tags and
    /// little else, so a tag is worth a slot only if other people's posts under it get notes.
    /// In November 2026 the three blogs had 113 notes on 51 posts: two of every post's tags were #memes and
    /// #funny (36 and 119 new posts an hour, so a post left the first page within minutes, and the typical
    /// post there had no notes), and most of the rest were phrases the caption model made up, where the blog's
    /// own post was the only one ever filed. The few posts that did get notes carried a tag with a following
    /// (#real madrid: under one post an hour, 6 notes typical).
    /// </summary>
    internal static class OmniTumblrTagMath
    {
        public const int PageSize = 20;
        /// <summary>A post this old has collected most of the notes its tags will bring it.</summary>
        public static readonly TimeSpan SettledAfter = TimeSpan.FromHours(12);
        public static readonly TimeSpan SettledWithin = TimeSpan.FromDays(60);
        /// <summary>Fewer than <see cref="AliveMinPosts"/> posts by other blogs in this long: nobody is there.</summary>
        public static readonly TimeSpan AliveWindow = TimeSpan.FromDays(90);
        public const int AliveMinPosts = 3;
        /// <summary>20 an hour: the listing's first page turns over within the hour.</summary>
        public const double CrowdedPostsPerDay = 480;

        /// <summary>The form a tag is cached and compared under.</summary>
        public static string Key(string? tag) =>
            Regex.Replace((tag ?? string.Empty).Trim().TrimStart('#').Trim(), "\\s+", " ").ToLowerInvariant();

        /// <summary>
        /// The first page is from a busy tag and too young to show what posts there end up with, so a second
        /// page from a day back is worth a call.
        /// </summary>
        public static bool NeedsOlderPage(IReadOnlyList<TumblrPostSummary> newest, DateTime now)
        {
            var dated = newest.Where(p => p.PublishedUtc > DateTime.MinValue).ToList();
            if (dated.Count < PageSize - 5) return false; // a short page is the whole tag
            return now - dated.Min(p => p.PublishedUtc) < TimeSpan.FromHours(24);
        }

        /// <param name="newest">The listing's first page.</param>
        /// <param name="older">A page from about a day back, when one was fetched (busy tags).</param>
        /// <param name="ownBlogs">Our blogs' names: their posts say nothing about who else is there.</param>
        public static TagResearch Evaluate(string tag, IReadOnlyList<TumblrPostSummary> newest, IReadOnlyList<TumblrPostSummary>? older,
            IReadOnlySet<string> ownBlogs, DateTime now)
        {
            bool Ours(TumblrPostSummary p) => p.BlogName != null && ownBlogs.Contains(p.BlogName);
            var dated = newest.Where(p => p.PublishedUtc > DateTime.MinValue && p.PublishedUtc <= now.AddMinutes(10)).ToList();
            var others = dated.Where(p => !Ours(p)).ToList();
            var result = new TagResearch
            {
                Tag = Key(tag),
                CheckedUtc = now,
                Sampled = others.Count,
            };

            if (others.Count > 0)
            {
                result.NewestPostUtc = others.Max(p => p.PublishedUtc);
                // Measured to now, not to the newest post: twenty posts in one week a year ago is a dead tag.
                double days = Math.Max((now - others.Min(p => p.PublishedUtc)).TotalDays, 1.0 / 1440);
                result.PostsPerDay = Math.Round(others.Count / days, others.Count / days < 10 ? 2 : 0);
            }
            foreach (var p in others)
            {
                if (p.Type == "video") result.Videos++;
                else if (p.Type == "photo") result.Images++;
                else result.Texts++;
            }

            var settled = others.Concat((older ?? Array.Empty<TumblrPostSummary>()).Where(p => p.PublishedUtc > DateTime.MinValue && !Ours(p)))
                .GroupBy(p => p.Id.Length > 0 ? p.Id : p.BlogName + "|" + p.PublishedUtc.Ticks)
                .Select(g => g.First())
                .Where(p => now - p.PublishedUtc >= SettledAfter && now - p.PublishedUtc <= SettledWithin)
                .ToList();
            result.Settled = settled.Count;
            double typical = 0;
            if (settled.Count > 0)
            {
                var notes = settled.Select(p => (double)Math.Max(0, p.NoteCount)).OrderBy(n => n).ToList();
                result.TypicalNotes = notes.Count % 2 == 1 ? notes[notes.Count / 2] : (notes[notes.Count / 2 - 1] + notes[notes.Count / 2]) / 2;
                result.ShareWithNotes = Math.Round(notes.Count(n => n > 0) / (double)notes.Count, 2);
                // Geometric mean: one post with 4,000 notes does not make a tag where the rest get none look good.
                typical = Math.Exp(notes.Average(n => Math.Log(1 + n))) - 1;
            }

            result.Related = others.SelectMany(p => p.Tags.Select(Key).Distinct())
                .Where(t => t.Length > 0 && t != result.Tag)
                .GroupBy(t => t)
                .Where(g => g.Count() >= 2)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(8)
                .Select(g => g.Key)
                .ToList();

            int alive = others.Count(p => now - p.PublishedUtc <= AliveWindow);
            double perDay = result.PostsPerDay ?? 0;
            result.Verdict = alive < AliveMinPosts ? TagVerdict.Dead
                : perDay >= CrowdedPostsPerDay ? TagVerdict.Crowded
                : perDay < 1 ? TagVerdict.Quiet
                : TagVerdict.Active;

            if (result.Verdict != TagVerdict.Dead)
            {
                double confidence = Math.Min(1, settled.Count / 6.0);
                // Under a post a day there are few people watching the tag, whatever its regulars' posts earn.
                double audience = perDay >= 1 ? 1 : 0.4 + 0.6 * perDay;
                result.Score = Math.Round(100 * (1 - Math.Exp(-typical / 4)) * confidence * audience, 1);
            }
            return result;
        }

        /// <summary>One line a person can act on.</summary>
        public static string Describe(TagResearch r)
        {
            string typical = r.Settled == 0 || r.TypicalNotes == null ? "" : $" A typical post there ends up with {r.TypicalNotes:0.#} note{(r.TypicalNotes == 1 ? "" : "s")}.";
            return r.Verdict switch
            {
                TagVerdict.Dead => r.Sampled == 0
                    ? "Nobody else has ever posted under this tag, so nobody is browsing it."
                    : "Almost nobody else posts under this tag (fewer than 3 posts in the last 3 months), so nobody is browsing it.",
                TagVerdict.Crowded => $"Flooded: about {Rate(r.PostsPerDay ?? 0)}, so a new post leaves the first page within {FirstPage(r.PostsPerDay ?? 0)}.{typical}",
                TagVerdict.Quiet => $"Alive but slow: about {Rate(r.PostsPerDay ?? 0)}.{typical}",
                TagVerdict.Active => $"Active: about {Rate(r.PostsPerDay ?? 0)}, so a post stays on the first page for {FirstPage(r.PostsPerDay ?? 0)}.{typical}",
                _ => r.Error != null ? "Could not be checked: " + r.Error : "Not checked yet.",
            };
        }

        private static string Rate(double perDay) =>
            perDay >= 48 ? $"{perDay / 24:0} posts an hour"
            : perDay >= 2 ? $"{perDay:0} posts a day"
            : perDay >= 2.0 / 7 ? $"{perDay * 7:0} posts a week"
            : $"{Math.Max(1, perDay * 30):0} post(s) a month";

        /// <summary>How long a post stays among the newest <see cref="PageSize"/>.</summary>
        private static string FirstPage(double perDay)
        {
            double hours = PageSize / Math.Max(perDay, 0.001) * 24;
            return hours < 1.5 ? $"{Math.Max(1, hours * 60):0} minutes"
                : hours < 48 ? $"{hours:0} hours"
                : hours < 24 * 60 ? $"{hours / 24:0} days"
                : "months";
        }
    }

    /// <summary>What <see cref="OmniTumblrTagResearch.VetAsync"/> decided about a post's suggested tags.</summary>
    public sealed class TagVetting
    {
        /// <summary>The tags to use, best first.</summary>
        public List<string> Kept { get; set; } = new();
        /// <summary>Left out because nobody else uses them.</summary>
        public List<string> Dropped { get; set; } = new();
        /// <summary>At least one tag's listing was read (now or earlier); false when Tumblr could not be asked at all.</summary>
        public bool Researched { get; set; }
    }

    /// <summary>
    /// Looks tags up on Tumblr and remembers the answers (tags.json). The planner asks it which of the caption
    /// model's tags have anyone browsing them; sync keeps each blog's own fixed and rotating tags checked and
    /// uses the listings to see whether Tumblr still lists the blog's posts at all. Lookups never hold up a
    /// post: without an answer (no app key, the API budget is spent, Tumblr's edge is refusing the server) a
    /// tag is used as the model gave it.
    /// </summary>
    internal sealed class OmniTumblrTagResearch
    {
        private static readonly TimeSpan FreshFor = TimeSpan.FromDays(5);
        private static readonly TimeSpan DeadFreshFor = TimeSpan.FromDays(14);
        private static readonly TimeSpan RetryFailedAfter = TimeSpan.FromMinutes(30);
        private const int MaxCached = 4000;

        private readonly OmniTumblrStore store;
        private readonly ITumblrApi api;
        private readonly OmniTumblrAuth auth;
        private readonly OmniTumblrApiBudget? budget;
        private readonly TumblrEdgeGate? edgeGate;
        private readonly Func<DateTime> clock;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, Task<TagResearch?>> inFlight = new(StringComparer.Ordinal);

        public OmniTumblrTagResearch(OmniTumblrStore store, ITumblrApi api, OmniTumblrAuth auth, Func<DateTime> clock,
            OmniTumblrApiBudget? budget = null, TumblrEdgeGate? edgeGate = null, Action<string>? log = null)
        {
            this.store = store;
            this.api = api;
            this.auth = auth;
            this.clock = clock;
            this.budget = budget;
            this.edgeGate = edgeGate;
            this.log = log ?? (_ => { });
        }

        /// <summary>The OmniSetting OmniTumblr_TagResearchEnabled.</summary>
        public Func<bool> Enabled { get; set; } = () => true;

        /// <summary>Tumblr can be asked right now.</summary>
        public bool CanLookUp => Enabled() && auth.AppOnlyCredentials() != null && edgeGate?.IsBlocked != true && budget?.AllowBackground() != false;

        /// <summary>What is known about a tag, however old; null when it was never looked up.</summary>
        public TagResearch? Known(string tag)
        {
            string key = OmniTumblrTagMath.Key(tag);
            return store.Read(s => s.Tags.TryGetValue(key, out var r) ? OmniTumblrStore.Clone(r) : null);
        }

        private bool IsFresh(TagResearch r, DateTime now)
        {
            var age = now - r.CheckedUtc;
            if (r.Verdict == TagVerdict.Unknown) return age < RetryFailedAfter;
            return age < (r.Verdict == TagVerdict.Dead ? DeadFreshFor : FreshFor);
        }

        /// <summary>
        /// The tag's research: the remembered answer while fresh, otherwise a new look at Tumblr. When Tumblr
        /// cannot be asked, the remembered answer however old, or null.
        /// </summary>
        public async Task<TagResearch?> GetAsync(string tag, CancellationToken ct, bool force = false)
        {
            string key = OmniTumblrTagMath.Key(tag);
            if (key.Length == 0) return null;
            var known = Known(key);
            DateTime now = clock();
            if (known != null && !force && IsFresh(known, now)) return known;
            if (!CanLookUp) return known?.Verdict == TagVerdict.Unknown ? null : known;

            // Two posts being prepared at once often want the same tag: one lookup serves both.
            var task = inFlight.GetOrAdd(key, k => LookUpAsync(k, known, ct));
            try { return await task; }
            finally { inFlight.TryRemove(new KeyValuePair<string, Task<TagResearch?>>(key, task)); }
        }

        private async Task<TagResearch?> LookUpAsync(string key, TagResearch? known, CancellationToken ct)
        {
            var creds = auth.AppOnlyCredentials();
            if (creds == null) return known;
            DateTime now = clock();
            TagResearch result;
            try
            {
                var newest = await api.GetTaggedAsync(creds, key, null, ct);
                List<TumblrPostSummary>? older = null;
                if (OmniTumblrTagMath.NeedsOlderPage(newest, now))
                {
                    try { older = await api.GetTaggedAsync(creds, key, new DateTimeOffset(now.AddHours(-24)).ToUnixTimeSeconds(), ct); }
                    catch (TumblrApiException) { /* the first page still gives the rate */ }
                }
                var own = store.Read(s => s.Blogs.Values.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
                result = OmniTumblrTagMath.Evaluate(key, newest, older, own, now);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Keep a good earlier answer; otherwise remember the failure so the tag is not retried at once.
                if (known != null && known.Verdict != TagVerdict.Unknown) return known;
                result = new TagResearch { Tag = key, CheckedUtc = now, Verdict = TagVerdict.Unknown, Error = Clip(ex.Message, 200), Failures = (known?.Failures ?? 0) + 1 };
                if (result.Failures == 1) log($"OmniTumblr tag research: #{key} could not be checked: {result.Error}");
            }
            store.Mutate(s =>
            {
                s.Tags[key] = result;
                if (s.Tags.Count > MaxCached)
                    foreach (string stale in s.Tags.OrderBy(kv => kv.Value.CheckedUtc).Take(s.Tags.Count - MaxCached + 200).Select(kv => kv.Key).ToList())
                        s.Tags.Remove(stale);
                s.MarkTags();
            });
            return result.Verdict == TagVerdict.Unknown ? null : OmniTumblrStore.Clone(result);
        }

        /// <summary>Looks up several tags (the Strategy tab's check, and sync's upkeep of a blog's own tags).</summary>
        public async Task<List<TagResearch>> ResearchAsync(IEnumerable<string> tags, CancellationToken ct, bool force = false, int max = 30)
        {
            var results = new List<TagResearch>();
            foreach (string tag in tags.Select(OmniTumblrTagMath.Key).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).Take(max))
            {
                ct.ThrowIfCancellationRequested();
                var r = await GetAsync(tag, ct, force) ?? Known(tag) ?? new TagResearch { Tag = tag, Verdict = TagVerdict.Unknown };
                results.Add(r);
            }
            return results;
        }

        /// <summary>
        /// Chooses which of the caption model's candidate tags a post should carry: the ones with the best
        /// record first, none that nobody else uses, at most <paramref name="keep"/>. Candidates Tumblr could
        /// not be asked about follow the known ones in the model's order. A hyphenated tag nobody uses is tried
        /// with spaces, which is how Tumblr tags are mostly written.
        /// </summary>
        public async Task<TagVetting> VetAsync(IEnumerable<string> candidates, int keep, CancellationToken ct, int maxLookups = 16)
        {
            var vetting = new TagVetting();
            var ordered = TumblrNpf.NormalizeTags(candidates.Select(OmniTumblrTagMath.Key), TumblrNpf.MaxTags);
            if (ordered.Count == 0 || keep <= 0) return vetting;
            DateTime now = clock();
            int lookups = 0;

            async Task<TagResearch?> Look(string tag)
            {
                var known = Known(tag);
                if (known != null && IsFresh(known, now)) return known.Verdict == TagVerdict.Unknown ? null : known;
                if (lookups >= maxLookups) return known?.Verdict == TagVerdict.Unknown ? null : known;
                lookups++;
                var looked = await GetAsync(tag, ct);
                return looked?.Verdict == TagVerdict.Unknown ? null : looked;
            }

            var judged = new List<(string Tag, TagResearch? Research, int Order)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < ordered.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string tag = ordered[i];
                var research = await Look(tag);
                if (research?.Verdict == TagVerdict.Dead && tag.Contains('-'))
                {
                    string spaced = OmniTumblrTagMath.Key(tag.Replace('-', ' '));
                    var alternative = spaced == tag ? null : await Look(spaced);
                    if (alternative != null && alternative.Verdict != TagVerdict.Dead)
                    {
                        tag = spaced;
                        research = alternative;
                    }
                }
                if (!seen.Add(tag)) continue;
                judged.Add((tag, research, i));
            }

            vetting.Researched = judged.Any(j => j.Research != null);
            vetting.Dropped = judged.Where(j => j.Research?.Verdict == TagVerdict.Dead).Select(j => j.Tag).ToList();
            vetting.Kept = judged.Where(j => j.Research?.Verdict != TagVerdict.Dead)
                .OrderBy(j => j.Research == null ? 1 : 0)
                .ThenByDescending(j => j.Research?.Score ?? 0)
                .ThenBy(j => j.Order)
                .Select(j => j.Tag)
                .Take(keep)
                .ToList();
            return vetting;
        }

        /// <summary>
        /// Looks for one of the blog's recent posts in the listing of one of its own tags, to see whether Tumblr
        /// still lists the blog there. Picks the post and tag where the post must still be on the first page if
        /// it is listed at all. Returns false when no post lends itself to a check yet.
        /// </summary>
        public async Task<bool> CheckListedAsync(string blogId, CancellationToken ct)
        {
            DateTime now = clock();
            var candidates = store.Read(s =>
            {
                var blog = s.Blog(blogId);
                if (blog == null) return null;
                string? lastChecked = blog.Health.Search?.PostId;
                return s.PostsOf(blogId)
                    // Never a reblog: Tumblr lists only original posts under a tag, so a reblog is always "missing".
                    .Where(p => p.Status == PostStatus.Published && !p.IsReblog && p.TumblrPostId != null && !p.TumblrIdProvisional && p.TumblrState == TumblrPostState.Published
                        && p.PublishedUtc is DateTime at && now - at >= TimeSpan.FromHours(1) && now - at <= TimeSpan.FromDays(5))
                    .OrderByDescending(p => p.PublishedUtc)
                    .Take(6)
                    .SelectMany(p => p.Tags.Select(t => (Post: p.TumblrPostId!, Published: p.PublishedUtc!.Value, Tag: OmniTumblrTagMath.Key(t),
                        Known: s.Tags.TryGetValue(OmniTumblrTagMath.Key(t), out var r) && r.Verdict != TagVerdict.Unknown ? r.PostsPerDay ?? 0 : (double?)null,
                        Fresh: p.TumblrPostId != lastChecked)))
                    .ToList();
            });
            if (candidates == null || candidates.Count == 0 || !CanLookUp) return false;

            // Posts by other people filed since ours: with fewer than half a page of them, ours is still on page one.
            var pick = candidates.Where(c => c.Known.HasValue)
                .Select(c => (c.Post, c.Published, c.Tag, c.Fresh, Ahead: c.Known!.Value * (now - c.Published).TotalDays))
                .Where(c => c.Ahead <= OmniTumblrTagMath.PageSize / 2.0)
                .OrderByDescending(c => c.Fresh).ThenBy(c => c.Ahead)
                .FirstOrDefault();
            if (pick.Post == null) return false;

            var creds = auth.AppOnlyCredentials();
            if (creds == null) return false;
            var listing = await api.GetTaggedAsync(creds, pick.Tag, null, ct);
            bool listed = listing.Any(p => p.Id == pick.Post);
            store.Mutate(s =>
            {
                var blog = s.Blog(blogId);
                if (blog == null) return;
                var search = blog.Health.Search ??= new SearchVisibility();
                bool samePostAsLast = search.PostId == pick.Post;
                search.CheckedUtc = now;
                search.Tag = pick.Tag;
                search.PostId = pick.Post;
                if (listed)
                {
                    bool wasMissing = search.Listed == false && search.Misses >= 3;
                    search.Listed = true;
                    search.LastListedUtc = now;
                    search.Misses = 0;
                    if (wasMissing) s.AddEvent(EventLevel.Success, "reach.listed", $"@{blog.Name}: Tumblr is listing the blog's posts under their tags again.", blogId, utc: now);
                }
                else
                {
                    // Only a different post counts as another miss: one post missing is one post's problem.
                    if (!samePostAsLast || search.Misses == 0) search.Misses++;
                    if (search.Misses >= 3 && search.Listed != false)
                    {
                        search.Listed = false;
                        s.AddEvent(EventLevel.Warning, "reach.unlisted",
                            $"@{blog.Name}: the last {search.Misses} posts checked were missing from Tumblr's listings of their own tags (latest: #{pick.Tag}). While that lasts, nobody can find the blog's posts by tag.", blogId, utc: now);
                    }
                }
                s.MarkBlog(blogId);
            });
            return true;
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
