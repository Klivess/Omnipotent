# OmniTumblr

Runs Tumblr blogs on autopilot. For each managed blog it picks content (MemeScraper reels, an upload library or a server folder), writes the caption with a vision model, posts on a weekly schedule in the blog's own time zone, and measures what works. Rebuilt as v2 in October 2026 after v1 had stopped posting.

## Why v1 stopped working

1. **Scheduling ran on TimeManager.** Replaced task timers still fired, so every restart re-registered `OmniTumblrPost_*` tasks: posts double-fired (a race, since the status flipped to "posting" only after awaits), recurring chains multiplied, and an old timer could delete its replacement's `.omnitask` file. Local and UTC times were mixed.
2. **Autopilot never pulled content.** The scheduled task only read content folders. MemeScraper was used only by a manual button.
3. **"AI captions" posted the prompt.** `CaptionMode.AIGenerated` published the prompt text verbatim. There was no model call.
4. **Media was never found or attached.** Reel paths were relative to the base directory but checked against the working directory. The website read `data.FilePath` from an upload response that returns `filePath`.
5. **Website POSTs sent `"[object Object]"`.** Publish-now, schedule, draft and profile edits passed plain objects as the fetch body, so the API answered 500.
6. **Adding blogs was fragile.** NewTumblrSharp sent no User-Agent (Tumblr requires a consistent one), used the legacy post API and hid Tumblr's error subcodes. Every blog meant typing its name and re-entering the app keys. The OAuth flow lived only in memory, and every route used reflection `CreateAPIRoute`, which can fail silently.
7. **Failures were invisible.** Retries ignored the error type, nothing alerted, and the publisher never wrote events, so "recent activity" never showed publishing.
8. **Analytics were mostly empty.** Engagement was hard-coded to 0, likes were never filled, and there were no per-post metrics, best times, tag or source breakdowns.
9. **Cleanup deleted queued media.** Media cleanup removed files by age even when queued posts still referenced them.

## Setup

1. Register an app at [tumblr.com/oauth/apps](https://www.tumblr.com/oauth/apps). Set the **default callback URL** to `https://klive.dev/omnitumblr/oauth/callback`. To connect with OAuth 2.0, add the same URL under **OAuth2 redirect URLs**.
2. On the KM site, open **OmniTumblr → Settings**, paste the consumer key and secret and press **Save and verify**.
3. Press **Connect a Tumblr account**, approve OmniTumblr in the Tumblr window, and pick the blogs to manage and a starting preset (weekly meme video, daily meme video, or manual only).
4. Review each blog's **Strategy** tab, then switch **Autopilot** on. Blogs start with autopilot off unless you tick it in the wizard.

One connection covers every blog the Tumblr account owns. OAuth 1.0a tokens never expire. OAuth 2.0 access tokens last 42 minutes and are refreshed automatically.

## How it works

Three loops run inside the service. Nothing uses TimeManager.

| Loop | Cadence | Job |
|---|---|---|
| plan | every 45 s, each blog at least every 10 min, and on any change | Fills the blog's weekly slots up to `PlanAheadDays` ahead: picks content, makes a thumbnail, writes the caption and tags, then marks the post Ready (or Awaiting approval). Also picks curated reblogs for blogs that have them on |
| publish | sleeps until the next due post, at most 20 s | Claims the post under the store lock, uploads it as an NPF post (multipart, media parts named by identifier), records the Tumblr id and URL |
| sync | every 60 s, within the API budget | Refreshes user info and limits, blog info, posts, notes and the activity feed |

Every action is written to the event log (`events.jsonl`), which feeds the activity lists on the website.

### Scheduling

- Slots are weekly (`day + minute`) in the blog's IANA time zone and stay correct across DST changes.
- Each post lands a deterministic few minutes either side of its slot (`JitterMinutes`, default ±7), never exactly on the hour.
- `MaxPostsPerDay` (8) and `MinGapMinutes` (30) cap bursts on one blog.
- Across all blogs, autopilot posts go out at least `OmniTumblr_FleetSpacingMinutes` (20) apart, plus a random 0–50% on each gap. Posts that come due together, such as a backlog released when Tumblr answers again or two blogs with slots close together, go out one at a time, oldest first. Before this, every blog's backlog went out in the same minute (Oct 27–28 2026). **Publish now** skips the spacing.
- A missed slot (for example after downtime) is published late within `MissedSlotGraceHours` (6), or skipped if the policy is Skip. Older slots are skipped rather than flooding the blog.
- Posts you edit in the queue (time, caption, tags) are marked as manual overrides, so autopilot leaves them alone.

### Content sources

| Source | Behaviour |
|---|---|
| MemeScraper reels | Filters by niche, Instagram account, maximum age, minimum views and maximum length. Picks by Best (views, likes, freshness), Newest, Oldest or Random. Each reel is used once per blog; "reuse across blogs" is opt-in |
| Upload library | Files uploaded on the blog's Strategy tab |
| Server folder | A folder on the server (videos and/or images, optionally recursive) |
| Manual only | Nothing is planned; compose posts yourself |

The blog page shows the **content runway**: how many unused items match the filters, and how many weeks that lasts at the current schedule. A warning appears when it runs low.

### Repeats of the same clip

Instagram meme accounts repost each other's videos, so one clip often arrives as several reels with different keys. On Oct 27–28 2026, three blogs posted the same video within a day. Content keys alone can't catch this, so the planner also compares what each video shows (`OmniTumblrFingerprints`):

- **Fingerprint.** ffmpeg decodes the first two minutes once, at 72×128 greyscale, two frames a second. Each frame gets two 64-bit difference hashes of 9×8 cells. One covers the whole frame. The other covers the *moving region*: the part of the frame that changes over the clip, so a static caption bar, border or padding is cut away. Each hash comes with a mask of the bits that mean something: neighbouring cells at least 4 grey levels apart. Bits from flat areas, such as black bars or white caption space, are not compared. Images get one frame. Fingerprints are stored by content key in `fingerprints.json`.
- **Same clip.** Two videos count as the same clip when their lengths are within 30% (or 3 s) and most frames agree. A pair of frames agrees when at most 16% of the bits meaningful to either differ. Pairs with fewer than 8 meaningful bits are skipped. The frames are compared on the whole frame or on the moving region, as is or mirrored, allowing up to 2 s trimmed from either clip. A still clip (a picture held over music, or a photo) rests on one frame, so it must agree within 5% of at least 16 meaningful bits.
- **Calibration.** The rule was tuned on 733 real MemeScraper reels (Oct–Nov 2026), and the C# hashes were checked bit for bit against the calibration scripts. Copies of 16 sample reels were all recognised when re-encoded at 480p, trimmed by 1 s, made quieter or mirrored. Captioned copies were recognised for all 10 moving reels and none of the 6 still pictures. A repost that re-crops or zooms the clip is not recognised. Across all 261,726 pairs, the only matches were genuine: one clip that one account posted 51 times.
- **Sound is not used.** Matching on loudness was tried and dropped. It flagged 748 pairs of different memes that use the same trending sound. Their cuts land on the beat, so even their motion agreed.
- **When picking.** A candidate is skipped if it is the same clip as anything the blog must not repeat: its own posts, and every other blog's posts unless "reuse across blogs" is on. Published, removed and planned posts all count. Two slots filled in one pass never get the same clip either.
- **Sweep.** Every plan tick, planned autopilot posts that repeat an earlier post are found. This covers posts planned before fingerprints existed. The earlier post keeps the clip: one already out, or the one scheduled first. The later one gets different content and a new caption. If nothing different is left, it is cancelled, and its slot is refilled once content turns up. Posts you edited by hand are left alone. The event log records each case as `post.repeat`.
- **Limits.** Each pass computes at most 12 new fingerprints, so after a deploy it takes a few ticks to fingerprint existing posts. Planning waits until they are done. Media ffmpeg can't read is judged by its key alone, as before. After 5 failures in a row, a `content.fingerprint` warning is logged.

### Captions

- **AI**: KliveLLM writes the caption from the blog's persona, extra instructions and example captions. For videos it sends 4 frames (512 px wide) to a vision model, plus the original caption as context. Output is strict JSON (caption, alt text, suggested tags). If the model fails, the blog's fallback applies: post without a caption, use the fixed caption, or hold the post until it works. Failed attempts retry with backoff.
- **Fixed**, **Rotate** (a pool used in turn) or **None**.
- **Try it on the next video** (Strategy tab) previews a caption with unsaved settings, without creating a post. In the queue, **Rewrite with AI** takes an optional one-line direction.
- Tags are the blog's fixed tags, a sample of `RotatingTagsPerPost` from the rotating pool, and the AI's suggestions, capped at `MaxTags` (Tumblr allows 30 and lists a post under its first 20). With tag research on, the suggestions are checked against Tumblr first (see Reach).
- A reply cut off after the caption still gives a caption, but no tags. The tags that arrived whole are kept, and a reply with none is asked for once more.
- Swapping a post's content takes the old clip's tags off, and a rewritten caption replaces the tags the earlier caption added. Until November 2026 both stayed on the post.

### Reach: tag research

A blog with few followers is seen through its tags and little else. On 10 November 2026 the three blogs had 113 notes on 51 posts and 7 followers after a month. The public tag pages showed why:

- Every post led with `#memes` and `#funny`. Those had 36 and 119 new posts an hour, so a post left the first page within minutes, and the typical post there had no notes.
- Most other tags were phrases the caption model made up ("the shirt is the alibi", "stacked books desk"). The blog's own post was the only one ever filed under them.
- The posts that did get notes carried a tag with a following: `#real madrid` and `#kylian mbappe` (under one post an hour, 6 notes typical), `#joker`, `#assassins creed`, `#politics`.
- The blogs were listed in search. The tags were the problem, not a ban.

So tags are now judged from Tumblr's own listing of them (`GET /v2/tagged`, an API-key call), with this account's own posts left out (`OmniTumblrTagResearch`, `OmniTumblrTagMath`):

| Verdict | Rule | Used on posts |
|---|---|---|
| Dead | Fewer than 3 posts by other blogs in the last 90 days | Never |
| Quiet | Under 1 post a day | Yes, marked down |
| Active | 1 to 480 posts a day | Yes |
| Crowded | 480 a day or more (20 an hour: the first page turns over within the hour) | Yes, but it scores low |

- **Score (0–100).** From the notes on other blogs' posts that are 12 hours to 60 days old: the geometric mean, so one post with thousands of notes does not carry a tag. It is scaled down when fewer than 6 such posts were seen, and for tags slower than a post a day. A busy tag's first page is too young to judge, so a second page from 24 hours back is read.
- **On a post.** The caption model is asked for about twice as many tags as the post will carry, and told to name people, shows, games, teams and communities, not what the frame looks like. Each candidate is looked up. Dead ones are left out, the rest go on best first, up to `MaxSuggestedTags`. A hyphenated tag nobody uses is tried with spaces. The post records what was left out, and the event log has a `tags.dropped` line.
- **Never in the way.** Answers are remembered in `tags.json` (5 days, 14 for dead tags, 30 minutes after a failed lookup). One post costs at most 16 lookups. With no app key, the API budget nearly spent, Tumblr's edge refusing the server, or more than 75 seconds gone, the post gets the model's own first choices, as before.
- **The blog's own tags.** Sync looks up each blog's fixed and rotating tags daily (only stale ones cost a call), and at once after a strategy change or **Refresh from Tumblr**.
- **Listed in search?** Once a day sync looks for one of the blog's recent posts in the listing of one of its own quieter tags, choosing a post that must still be on the first page if it is listed at all. Three different posts missing in a row marks the blog as not listed (`reach.unlisted` in the event log); one found clears it. Nothing is paused or changed on that signal.
- **Switches.** Per blog: **Check suggested tags on Tumblr first** on the Strategy tab (`ResearchTags`, on by default, also for existing blogs). Globally: `OmniTumblr_TagResearchEnabled`.

The blog page's **Reach** tab puts this together: how many recent posts got any notes, whether the blog is listed, findings worst first (flooded or dead tags, too few tags per post, posting once a day or less, all-video posts in tags that are mostly images, an unfinished blog page), every tag with its verdict next to what this blog's posts under it averaged, and a box to look tags up before adding them.

### Reach: curated reblogs

In their first month the blogs reblogged nothing. On Tumblr a reblog is how a blog takes part: the original poster is told, the blog's name sits in the post's notes, and the blog's page is more than one account's uploads. So a blog can now reblog a few well-liked posts a day from the tags it lives in (`OmniTumblrCurator`). It is **off until switched on** per blog (Strategy → Curated reblogs) and needs autopilot on. Liking and following in bulk were left out on purpose: that is the pattern Tumblr treats as spam.

These are other people's posts going onto the blogs, so every pick is screened:

| Step | Rule |
|---|---|
| Hard rules | An original post (not a reblog), not from one of our blogs, no older than `MaxAgeHours` (72), at least `MinNotes` (50) notes, not marked mature, reblogs allowed by its author |
| No repeats | Never a post any of our blogs has reblogged or queued, never one this blog already turned down, and not a blog this one reblogged in the last 7 days |
| The model | Shown the post's picture, text and tags. It turns down anything that does not fit the blog, or is sexual, violent, demeaning, political, personal, an advert, or unreadable. When unsure it says no. A post it turns down is never asked about again. Only a clear "reblog" counts: an unusable reply means no reblog |
| Not seen | A post the model could not look at (its picture would not load, the model cannot see, or it is a video, where only one frame can be shown) always waits for approval |
| Approval | **On by default** (`RequireApproval`): each pick waits in the queue. A pick nobody approves is dropped after two days, and no more than two days' worth wait at once |

- **Finding candidates.** Tumblr lists a tag's posts newest first only, so the curator reads pages from 6, 14 and 26 hours back (then more if those gave too little), where posts have had time to collect notes. Listings are kept for three hours and shared between blogs. Candidates are ranked by notes for their age. Tags come from the blog's reblog tags, or its fixed and rotating tags when none are set; dead tags are skipped.
- **When.** `PerDay` (3) reblog times are spread between `StartHour` and `EndHour` (9–23) of the blog's local day, each nudged by a few stable minutes. A pick is made at most four hours ahead, so it is still fresh. The usual spacing rules apply when it goes out. Reblogs do not use up `MaxPostsPerDay`.
- **In the queue.** A reblog is a post of kind `Reblog`: it can be approved, cancelled, given a comment, or swapped (**Pick another**). Approving one after its time sends it now.
- **Tumblr refuses** (400.8002 or a 403: the original was deleted, or its author does not allow it): the reblog is dropped and that post is never picked again. No alert, and it does not count against the blog's health.
- **Notes.** Tumblr reports the original's notes on every reblog of it. A reblog's notes are therefore never recorded or counted: not in notes per post, top posts, best times, tag averages or the Reach figures. The daily listing check ignores reblogs too, since Tumblr lists only original posts under a tag.
- **Switches.** Per blog as above; globally `OmniTumblr_ReblogsEnabled`.

### Reach: not yet run against Tumblr

Tag research and reblogging have only run against fakes. After the first deploy:

- The Reach tab should show a verdict for each of the blog's tags, and Settings → API usage should list `tagged` calls.
- Switch curated reblogs on for one blog with approval left on, and look at the first picks in the queue before approving any. Approve one and check that it appears on the blog as a reblog of the original.

### Publishing and failures

Each post gets a unique slug before upload. If an attempt fails ambiguously (a timeout or a 5xx after the upload may have landed), the next attempt first looks for that slug on the blog and adopts the existing post instead of posting twice.

| Tumblr response | What happens |
|---|---|
| 401, revoked token | Connection marked *Needs reauth*. Its posts wait. A Discord alert is sent. Not counted as a failure |
| 403.8023 / 403.8004 / 403.8011: daily post, media or video limit | Deferred until the limit resets, plus 2 minutes |
| 429 | Retried after `Retry-After`, or 15 minutes |
| 403.8010: earlier video still transcoding | Retried every 5 minutes, for up to 3 hours |
| 400.8005 (media rejected) or other 400 | Failed. Rejected content is never picked again. Autopilot refills the slot |
| 404, blog not found | Retried hourly. Discord alert (blog renamed or deleted?) |
| 403.8022 (queue full) or other 403 | Failed. Discord alert |
| 403 as a bare nginx HTML page (no JSON) | Tumblr's edge refused the request before the API saw it. See below. Never fails the post |
| 5xx, timeout or unknown | Retried after 2, 5, 15, 30 and 60 minutes. Fails after 5 attempts with a Discord alert |

Discord alerts go through KliveBot, at most one per problem every 6 hours.

### When Tumblr's edge refuses the server

Tumblr's edge proxy sometimes rejects every request from the server's IP for hours, including read-only calls and the OAuth handshake. It answers with nginx's stock "403 Forbidden" HTML page, never with the API's JSON. From Oct 8 to Oct 10 2026, every call from the server got this page in on/off windows, while the same requests with the same app key from another network got normal answers. v2's first policy treated it as a normal failure, so each post gave up after five tries (about 52 minutes) and roughly 30 sync jobs kept retrying into the block.

The edge gate (`TumblrEdgeGate`) now handles it:

- The client reports every response to the gate. An HTML 403 closes the gate, and any JSON answer, even an error, opens it again.
- While the gate is closed, posts wait without using an attempt, and sync jobs are postponed. Every 5, 10, 20, then 30 minutes, one call goes out to test whether Tumblr is answering again. **Publish now** and **Retry** try at once.
- When the gate opens, the waiting autopilot posts go out one at a time, spaced across blogs (see Scheduling), and sync catches up, spread over three minutes. An autopilot post still follows the missed-slot policy, so after the grace period it is skipped rather than posted many hours late.
- The overview shows "Tumblr is refusing this server's requests". The event log records when the block starts and when it lifts. If it lasts over 2 hours, a Discord alert is sent.

If the blocks keep coming back, the cause is the server's IP or network, not OmniTumblr. Send Tumblr traffic another way with `OmniTumblr_Proxy`.

### Analytics

| Data | Source | Refresh |
|---|---|---|
| Followers (history) | `/blog/{blog}/followers` | 12 h |
| Blog info, post count | `/blog/{blog}/info` | 6 h |
| Notes per post | `/blog/{blog}/posts` (`npf=true`) | 1 h for active blogs, 6 h for idle ones |
| Likes / reblogs / replies per post | `/blog/{blog}/notes` | read 1, 3, 7 and 30 days after publishing |
| Activity (follows, likes, reblogs, replies) | `/blog/{blog}/notifications` | 30 min |
| Account limits | `/user/limits` | 1 h |
| Tag listings (see Reach) | `/tagged` | when a post is prepared; the blog's own tags daily |
| Avatar and header (default look?) | `/blog/{blog}/info` | 6 h |

From these the website shows follower growth, notes per post (mean and median), engagement (notes per post ÷ followers), notes by publishing day, best weekday and hour (in the blog's time zone), top posts (including ones made outside OmniTumblr), and average notes by tag, content source and caption style.

**API budget.** Tumblr allows 1,000 calls an hour and 5,000 a day per consumer key. Background sync pauses above 60% of the hourly or 70% of the daily allowance, or when Tumblr's own headers report fewer than 150 calls left this hour or 600 today, so publishing always has headroom. The Settings page shows usage.

## Settings and kill switches

Both are OmniSettings and take effect without a restart:

| Setting | Default | Effect when false |
|---|---|---|
| `OmniTumblr_Enabled` | true | The whole engine stops (no planning, publishing or sync) |
| `OmniTumblrV2_PublishingEnabled` | true | Nothing is posted. Planning, captions and analytics carry on |

The second has a v2 name on purpose: OmniSettings writes a default on first read, so a renamed setting is the only way to ship a new default.

`OmniTumblr_FleetSpacingMinutes` (default 20, 0 turns it off, at most 360) is the minimum gap between autopilot posts on any two blogs. It takes effect within a minute.

`OmniTumblr_TagResearchEnabled` (default true) turns tag research off everywhere when false: suggested tags go on posts unchecked, and the daily tag and listing checks stop. It takes effect within a minute.

`OmniTumblr_ReblogsEnabled` (default true) stops every blog's curated reblogs when false: nothing new is picked. Reblogs are still off for each blog until switched on in its strategy.

`OmniTumblr_Proxy` (default empty, which means direct) sends every Tumblr call through a proxy: `http://host:port`, `https://…` or `socks5://host:port`, optionally with `user:pass@`. It takes effect within a minute. A value that cannot be parsed is ignored and logged once.

## Data

Everything lives under `SavedData/OmniTumblr/v2/`:

```
app.json            Tumblr app (consumer key; the secret is encrypted)
connections.json    connected accounts (tokens encrypted), limits, health
auth-flows.json     pending authorizations (survive restarts; expire after 30 minutes)
engine.json         engine state, migration result
events.jsonl        event log
fingerprints.json   what each clip looks like (repeat detection)
tags.json           what Tumblr's listing showed for each tag looked up
secrets.key         root key for the vault: back it up together with the rest
blogs/{blogId}/     blog.json (settings, strategy, health), posts.json, insights.json
library/  uploads/  thumbs/  cache/
```

Secrets are encrypted with AES-256-GCM, using a per-purpose key derived (HKDF-SHA256) from `secrets.key`. They are decrypted only for the duration of an API call. Writes are atomic, and a background flusher persists changes.

On first start, v2 imports v1 data once: app keys, OAuth 1.0a tokens, each blog with its settings mapped onto a strategy, published history, and content v1 already used, so nothing is reposted. Imported blogs arrive with **autopilot off**. The v1 files are left where they were.

## HTTP API

All routes are under `/omnitumblr/`, registered with typed `CreateRoute`. Live GETs are marked uncacheable for the response cache.

- **Reach:** `reach` (GET, one blog's findings and tag table, from memory), `tags/research` (POST, looks up to 20 tags up on Tumblr now)
- **Read:** `overview`, `dashboard-stats`, `blog`, `analytics`, `post`, `posts`, `events`, `settings`, `library`, `memescraper/options`, `media` (thumbnails and video), `content/thumb`
- **Accounts:** `settings/app`, `connect/begin`, `connect/status`, `oauth/callback`, `connections/refresh`, `connections/remove`
- **Blogs:** `blogs/add`, `blogs/update` (autopilot, pause, approval, strategy, notes), `blogs/plan-now`, `blogs/refresh`, `blogs/remove`
- **Posts:** `posts/create`, `posts/update`, `posts/approve`, `posts/publish-now`, `posts/retry`, `posts/skip`, `posts/swap-content`, `posts/regenerate-caption`, `posts/cancel`, `posts/delete-remote`
- **Captions and media:** `captions/preview`, `media/upload`, `library/delete`

## Website

`pages/schemery/omnitumblr/` in the KM site, on the OmniTrader design system:

- **Overview**: needs-attention items, KPIs, blog cards, the queue, recently published, followers chart, activity log
- **Blog**: tabs for overview, queue, published, analytics, reach, strategy and activity
- **Compose**: video, photo, text or link posts to one or more blogs, now or scheduled
- **Settings**: Tumblr app, connected accounts and their limits, engine status

## Tests

```
DOTNET_ROLL_FORWARD=Major dotnet test Omnipotent.Tests/Omnipotent.Tests.csproj -c Release --filter "FullyQualifiedName~OmniTumblr"
```

These cover the OAuth 1.0a signature vectors, error classification, NPF multipart, schedule maths (including DST), the planner, the publisher (idempotency and every failure policy), the captioner's output parsing, the store and vault, migration, analytics, tag research (verdicts, choosing a post's tags, the listing check, the reach findings), and curated reblogs (what is and is not picked, screening, approval, publishing, and keeping a reblog's notes out of the blog's figures).

Website: `npx playwright test tests/e2e/omnitumblr.spec.ts` runs every page against a stateful API mock. It checks that request bodies are real JSON (v1's `"[object Object]"` bug), and that nothing scrolls sideways at phone width.
