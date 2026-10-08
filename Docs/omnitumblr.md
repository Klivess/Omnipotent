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
| plan | every 45 s, each blog at least every 10 min, and on any change | Fills the blog's weekly slots up to `PlanAheadDays` ahead: picks content, makes a thumbnail, writes the caption and tags, then marks the post Ready (or Awaiting approval) |
| publish | sleeps until the next due post, at most 20 s | Claims the post under the store lock, uploads it as an NPF post (multipart, media parts named by identifier), records the Tumblr id and URL |
| sync | every 60 s, within the API budget | Refreshes user info and limits, blog info, posts, notes and the activity feed |

Every action is written to the event log (`events.jsonl`), which feeds the activity lists on the website.

### Scheduling

- Slots are weekly (`day + minute`) in the blog's IANA time zone and stay correct across DST changes.
- Each post lands a deterministic few minutes either side of its slot (`JitterMinutes`, default ±7), never exactly on the hour.
- `MaxPostsPerDay` (8) and `MinGapMinutes` (30) cap bursts.
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

### Captions

- **AI**: KliveLLM writes the caption from the blog's persona, extra instructions and example captions. For videos it sends 4 frames (512 px wide) to a vision model, plus the original caption as context. Output is strict JSON (caption, alt text, suggested tags). If the model fails, the blog's fallback applies: post without a caption, use the fixed caption, or hold the post until it works. Failed attempts retry with backoff.
- **Fixed**, **Rotate** (a pool used in turn) or **None**.
- **Try it on the next video** (Strategy tab) previews a caption with unsaved settings, without creating a post. In the queue, **Rewrite with AI** takes an optional one-line direction.
- Tags are the blog's fixed tags, a sample of `RotatingTagsPerPost` from the rotating pool, and the AI's suggestions, capped at `MaxTags` (Tumblr allows 30).

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
| 5xx, timeout or unknown | Retried after 2, 5, 15, 30 and 60 minutes. Fails after 5 attempts with a Discord alert |

Discord alerts go through KliveBot, at most one per problem every 6 hours.

### Analytics

| Data | Source | Refresh |
|---|---|---|
| Followers (history) | `/blog/{blog}/followers` | 12 h |
| Blog info, post count | `/blog/{blog}/info` | 6 h |
| Notes per post | `/blog/{blog}/posts` (`npf=true`) | 1 h for active blogs, 6 h for idle ones |
| Likes / reblogs / replies per post | `/blog/{blog}/notes` | read 1, 3, 7 and 30 days after publishing |
| Activity (follows, likes, reblogs, replies) | `/blog/{blog}/notifications` | 30 min |
| Account limits | `/user/limits` | 1 h |

From these the website shows follower growth, notes per post (mean and median), engagement (notes per post ÷ followers), notes by publishing day, best weekday and hour (in the blog's time zone), top posts (including ones made outside OmniTumblr), and average notes by tag, content source and caption style.

**API budget.** Tumblr allows 1,000 calls an hour and 5,000 a day per consumer key. Background sync pauses above 60% of the hourly or 70% of the daily allowance, or when Tumblr's own headers report fewer than 150 calls left this hour or 600 today, so publishing always has headroom. The Settings page shows usage.

## Settings and kill switches

Both are OmniSettings and take effect without a restart:

| Setting | Default | Effect when false |
|---|---|---|
| `OmniTumblr_Enabled` | true | The whole engine stops (no planning, publishing or sync) |
| `OmniTumblrV2_PublishingEnabled` | true | Nothing is posted. Planning, captions and analytics carry on |

The second has a v2 name on purpose: OmniSettings writes a default on first read, so a renamed setting is the only way to ship a new default.

## Data

Everything lives under `SavedData/OmniTumblr/v2/`:

```
app.json            Tumblr app (consumer key; the secret is encrypted)
connections.json    connected accounts (tokens encrypted), limits, health
auth-flows.json     pending authorizations (survive restarts; expire after 30 minutes)
engine.json         engine state, migration result
events.jsonl        event log
secrets.key         root key for the vault: back it up together with the rest
blogs/{blogId}/     blog.json (settings, strategy, health), posts.json, insights.json
library/  uploads/  thumbs/  cache/
```

Secrets are encrypted with AES-256-GCM, using a per-purpose key derived (HKDF-SHA256) from `secrets.key`. They are decrypted only for the duration of an API call. Writes are atomic, and a background flusher persists changes.

On first start, v2 imports v1 data once: app keys, OAuth 1.0a tokens, each blog with its settings mapped onto a strategy, published history, and content v1 already used, so nothing is reposted. Imported blogs arrive with **autopilot off**. The v1 files are left where they were.

## HTTP API

All routes are under `/omnitumblr/`, registered with typed `CreateRoute`. Live GETs are marked uncacheable for the response cache.

- **Read:** `overview`, `dashboard-stats`, `blog`, `analytics`, `post`, `posts`, `events`, `settings`, `library`, `memescraper/options`, `media` (thumbnails and video), `content/thumb`
- **Accounts:** `settings/app`, `connect/begin`, `connect/status`, `oauth/callback`, `connections/refresh`, `connections/remove`
- **Blogs:** `blogs/add`, `blogs/update` (autopilot, pause, approval, strategy, notes), `blogs/plan-now`, `blogs/refresh`, `blogs/remove`
- **Posts:** `posts/create`, `posts/update`, `posts/approve`, `posts/publish-now`, `posts/retry`, `posts/skip`, `posts/swap-content`, `posts/regenerate-caption`, `posts/cancel`, `posts/delete-remote`
- **Captions and media:** `captions/preview`, `media/upload`, `library/delete`

## Website

`pages/schemery/omnitumblr/` in the KM site, on the OmniTrader design system:

- **Overview**: needs-attention items, KPIs, blog cards, the queue, recently published, followers chart, activity log
- **Blog**: tabs for overview, queue, published, analytics, strategy and activity
- **Compose**: video, photo, text or link posts to one or more blogs, now or scheduled
- **Settings**: Tumblr app, connected accounts and their limits, engine status

## Tests

```
DOTNET_ROLL_FORWARD=Major dotnet test Omnipotent.Tests/Omnipotent.Tests.csproj -c Release --filter "FullyQualifiedName~OmniTumblr"
```

These cover the OAuth 1.0a signature vectors, error classification, NPF multipart, schedule maths (including DST), the planner, the publisher (idempotency and every failure policy), the captioner's output parsing, the store and vault, migration, and analytics.

Website: `npx playwright test tests/e2e/omnitumblr.spec.ts` runs every page against a stateful API mock. It checks that request bodies are real JSON (v1's `"[object Object]"` bug), and that nothing scrolls sideways at phone width.
