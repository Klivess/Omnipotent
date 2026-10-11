# OmniGram

Runs Instagram accounts on autopilot through Instagram's official API. For each managed account it picks content (MemeScraper reels, an upload library or a server folder), makes the media Instagram-ready, writes the caption with a vision model, publishes reels, photos, carousels or stories on a weekly schedule in the account's own time zone, and measures what works. Rebuilt as v2 in October 2026 after v1 had stopped working entirely.

## Why v1 stopped working

1. **No account could be added.** `OmniGramAccountManager.BuildInstaApi` ended with an unconditional `api.SetDevice(JsonConvert.DeserializeObject<AndroidDevice>(account.DeviceData))`. A new account has no device data, so this called `SetDevice(null)`, which throws `ArgumentNullException` before the account was saved. The website ignored the 500 and showed "Success". At startup the same line failed for every account without saved device data: it was marked Error and never got an API client, so it was not managed at all.
2. **It drove Instagram's private mobile API.** InstagramApiSharp 1.8.1 (unmaintained) logged in with each account's password while impersonating an Android phone. Instagram answers that with checkpoints, 2FA and "suspicious login" challenges, which v1 relayed to Discord with 5-minute timeouts. It also puts accounts at risk of being flagged. Posting and analytics both required a logged-in session, so once logins failed, nothing ran.
3. **Passwords were stored in plain text** in `SavedData/OmniGram/Accounts/{username}.json`, next to the login sessions.
4. **Scheduling ran on TimeManager tasks in server-local time** (`DateTime.Now`). Every queued post was re-registered as a task on each start.
5. **"AI captions" were not AI.** `AIGenerated` posted the configured prompt text. The MemeScraper "AI caption" option used the source reel's own description.
6. **Analytics were thin and partly invented.** Daily snapshots held only followers and average likes and comments (read through the private API), and "reach" was estimated as followers × engagement × 3. There were no views, reach, saves, shares, per-post metrics, best times or audience data.
7. **The website hid failures.** Most actions never checked the response, so errors looked like successes.

## What v2 uses

The **Instagram API with Instagram Login** (`graph.instagram.com`, Graph API v25.0 by default). It works with **professional accounts only** (Creator or Business). Switching is free in the Instagram app (Settings → Account type and tools → Switch to professional account). No Facebook Page is needed. OmniGram asks for four permissions: `instagram_business_basic`, `instagram_business_content_publish`, `instagram_business_manage_insights` and `instagram_business_manage_comments`.

There are no passwords and no device emulation. Each account has a long-lived token (60 days) that OmniGram renews automatically.

v1's profile editing is gone: the official API cannot change an account's bio or picture. It cannot follow or like other accounts either.

## Setup

1. **Create a Meta app.** At [developers.facebook.com/apps](https://developers.facebook.com/apps), create an app with the Instagram use case ("Manage messaging & content on Instagram").
2. **Add the redirect URL.** Open *API setup with Instagram business login*. Under *Set up Instagram business login → Business login settings*, add `https://klive.dev/omnigram/oauth/callback` as an OAuth redirect URI.
3. **Save the app on the KM site.** Open **OmniGram → Settings**, then paste the **Instagram app ID** and **Instagram app secret** from that page. These are the Instagram ones, not the Meta app ID shown at the top of the dashboard.
4. **Give accounts a role while the app is unpublished.** Until the Meta app is published (Live), only accounts with a role on it can authorize it. Add each account under *App roles → Instagram testers* and accept the invite from that account.
5. **Connect each account.** Press **Connect an account**, then either:
   - **Log in with Instagram** (recommended). A popup signs in as the account. It closes itself and the account appears.
   - **Paste an access token.** In the app dashboard, use *Generate access tokens → Add account → Generate token* and paste the token. This path needs no app secret.

   Pick a starting preset (one reel a week, three a week, daily, or manual) and choose whether autopilot starts at once.
6. **Review the account's Strategy tab**, then switch **Autopilot** on.
7. **Make sure Instagram can download media.** See *Public media link* below. The Settings page checks it, and a post that Instagram cannot fetch fails with error 2207052.

Accounts imported from v1 appear with **Connect** buttons. Connecting one attaches the token to that account by id, even if it was renamed since, and keeps its v1 schedule, captions and hashtags. Autopilot stays off until you switch it on.

## How it works

Three loops run inside the service. Nothing uses TimeManager.

| Loop | Cadence | Job |
|---|---|---|
| plan | every 45 s, each account at least every 10 min, and on any change | Fills the account's weekly slots up to `PlanAheadDays` ahead: picks content, prepares the media with FFmpeg, makes a thumbnail, writes the caption and hashtags, then marks the post Ready (or Awaiting approval) |
| publish | sleeps until the next due post, at most 15 s | Claims the post under the store lock, creates the Instagram media container, waits for Instagram to process it, publishes it, posts the first comment |
| sync | every 60 s, within the API budget | Token renewal, profile, publishing quota, media list, per-post insights, comments, daily account insights, online followers, demographics, the public-link check and housekeeping |

Every action is written to the event log (`events.jsonl`), which feeds the activity lists on the website.

### Scheduling

- Slots are weekly (`day + minute`) in the account's IANA time zone and stay correct across DST changes.
- Each post lands a deterministic few minutes either side of its slot (`JitterMinutes`, default ±9).
- `MaxPostsPerDay` (3) and `MinGapMinutes` (90) cap bursts. Instagram's own 24-hour publishing quota is read hourly and checked before every post.
- A missed slot (after downtime, for example) is published late within `MissedSlotGraceHours` (6), or skipped if the policy is Skip.
- Posts you edit in the queue, publish now or retry are manual overrides: autopilot's caps and spacing do not apply to them.
- **Approval:** with "Hold autopilot posts for my approval" on, planned posts wait in the queue as *Needs approval* until approved (one by one, or **Approve all**).

### Content sources

| Source | Behaviour |
|---|---|
| MemeScraper reels | Filters by niche, Instagram account, maximum age, minimum views and maximum length (default 90 s). Picks by Best (views, likes, freshness), Newest, Oldest or Random. Each reel is used once per account. Reuse across accounts is opt-in |
| Upload library | Files uploaded on the account's Strategy tab, oldest first |
| Server folder | A folder on the server (videos and/or images, optionally recursive) |
| Manual only | Nothing is planned. Compose posts yourself |

Videos become reels (or stories) and images become photo posts (or stories), per the strategy. The account page shows the **content runway**: how many unused items match the filters, and how many weeks that lasts at the current schedule. When the runway runs out, the account shows a warning.

### Formats

| Format | Media | Notes |
|---|---|---|
| Reel | one video, 3 s to 15 min | Optional: also show in the profile grid (`share_to_feed`), trial reel (shown to non-followers first; shared by hand or automatically if it performs), cover frame, up to 3 collaborators, share to the story after publishing |
| Photo | one image | Alt text |
| Carousel | 2–10 images and/or videos | Alt text. Composed by hand |
| Story | one image or video (videos trimmed to 60 s) | No caption. Gone after 24 hours |

### Media preparation

Instagram downloads every file itself and is strict about format, so each item gets an Instagram-ready copy under `prepared/` before it is scheduled:

- **Videos.** A compliant MP4 (H.264 or HEVC, yuv420p, 23–60 fps, AAC stereo at 48 kHz or less, at most 1920 px wide, under 25 Mbps) is only remuxed: `faststart` with no edit lists. Anything else is transcoded to H.264 High and AAC 128k. A video without sound gets a silent audio track, because Instagram rejects reels with no audio stream. Stories are trimmed to 60 s.
- **Images.** Converted to JPEG, 320–1440 px wide and at most 8 MB. Feed images outside 4:5 to 1.91:1 are fitted per the strategy: a blurred background (default), black bars or a crop. Story images get a 1080×1920 canvas unless they are already close to 9:16.
- If Instagram still refuses a copy that was only repackaged, it is fully converted once and retried.

FFmpeg comes from the shared `FFmpeg/` folder (OmniTube and KliveCloud install it there), falling back to the PATH. OmniGram checks that both `ffmpeg` and `ffprobe` actually start: a truncated download exists on disk but does not count. While they don't work it re-checks every 10 minutes, the overview warns, and Settings shows why. Without FFmpeg, MP4 and JPEG files are used as they are, and anything else waits with "FFmpeg is needed".

### Captions and hashtags

- **AI**: KliveLLM writes the caption from the account's persona, extra instructions and example captions. It sends up to 4 frames of a video (or the image) to a vision model, plus the original caption as context. The output is strict JSON: caption, alt text and suggested hashtags. If the model fails, the strategy's fallback applies: post without a caption, use the fixed caption, or hold the post until it works. Failed attempts retry with backoff.
- **Fixed**, **Rotate** (a pool used in turn), **Original** (the source's caption without its hashtags, mentions and "follow for more") or **None**.
- **Try it on the next item** (Strategy tab) previews a caption with unsaved settings, without creating a post. In the queue, **Rewrite with AI** takes an optional direction and replaces the previous AI hashtag suggestions instead of piling them up.
- Hashtags are the fixed ones, a sample of `RotatingHashtagsPerPost` from the rotating pool, and the AI's suggestions, capped at `MaxHashtags` (default 8; Instagram allows 30). They go at the end of the caption, into the first comment, or nowhere.
- A **credit line** (for example `🎥 @{origin}`) credits the source account on MemeScraper reels, and a **footer** is added to every caption.
- Instagram's limits are enforced before sending: 2,200 characters, 30 hashtags and 20 @mentions. Hashtags are dropped before caption text is shortened.
- **AI info label:** a per-strategy (and per-post) switch applies Instagram's "AI info" label. It is meant for media made with AI, not for AI-written captions.

### Public media link

Instagram's API publishes from a public URL: it downloads each photo and video itself. OmniGram serves each prepared file at `{OmniGram_PublicBaseUrl}/omnigram/public/media?t=…` through an HMAC-signed link that expires after 6 hours and can only reach files under `prepared/`. Range requests are supported.

- `OmniGram_PublicBaseUrl` defaults to `https://klive.dev`. Change it if KliveAPI is reachable from the internet at another address.
- Every 6 hours, and on demand from Settings, a self-check fetches a probe file through the public URL. A real download by Instagram's crawler (`facebookexternalhit`) is recorded too, and is the strongest proof it works.
- If Instagram cannot fetch a post's media 3 times (error 2207052), the post fails, the overview shows it, and a Discord alert is sent.

### Publishing and failures

Publishing has two steps: create a media container (Instagram then downloads and processes the media), then publish the container. Carousels create one container per item, then an album container. A container that is younger than 23 hours is reused after a restart. The state is flushed to disk before `media_publish` is called. If that call's outcome is unknown (a timeout or a crash), the container's status is checked before anything is resent, and a `PUBLISHED` container is adopted. A post is never published twice.

| Instagram response | What happens |
|---|---|
| 190, revoked or expired token (or permission refused) | Account marked *Reconnect needed*. Its posts wait. A Discord alert is sent. Not counted as a failure |
| Account restricted, checkpointed or inactive | Account marked *Restricted*. Posts wait until the account works again. Discord alert |
| 2207051, flagged as spam | That account's posting backs off for 24 hours. Discord alert |
| 2207042 / 9, daily publishing limit | Deferred until the oldest post of the last 24 hours ages out, plus 2 minutes |
| Rate limited (4, 17, 32, 613) | Deferred until Instagram says, or 15 minutes |
| Cover frame refused | Retried once with Instagram's default cover |
| Caption refused | Retried once with 5 hashtags and at most 1,500 characters |
| Media refused (a repackaged copy) | Converted fully and retried once |
| Media refused (a converted copy) or other 400 | Failed. Refused content is never picked again, and autopilot refills the slot |
| 2207052, media could not be downloaded | Retried. Fails after 3 download failures, with an overview warning and a Discord alert |
| Still processing after 30 minutes | Treated as a failure of that attempt |
| 5xx, timeout or unknown | Retried after 2, 5, 15, 30 and 60 minutes. Fails after 5 attempts with a Discord alert |

Discord alerts go through KliveBot, at most one per problem every 6 hours. Each post's drawer shows its publish log: every step, with Instagram's error code and how long it took.

### Analytics

| Data | Source | Refresh |
|---|---|---|
| Followers, follows, post count (history) | `/me` | 3 h |
| Token renewal | `refresh_access_token` (renewed when 25+ hours old and under 50 days left) | checked every 12 h |
| Publishing quota | `content_publishing_limit` | 1 h |
| The account's posts (including ones made in the app), likes and comments | `/me/media` | 1 h when active, 6 h when idle |
| Per-post insights: views, reach, likes, comments, shares, saves, interactions, follows, profile visits; for reels also average watch time and skip rate | `/{media}/insights` | 2 h, 1, 3, 7 and 30 days after publishing (stories at 1, 6 and 20 h) |
| Daily account insights: reach, views, accounts engaged, interactions, follows and unfollows, link taps | `/me/insights` | 6 h |
| When followers are online | `online_followers` | daily |
| Audience: age, gender, country, city | `follower_demographics` | weekly |
| Comments | `/{media}/comments` | 30 min |

From these the website shows follower growth, views per post (mean and median), engagement (interactions ÷ reach), follows and unfollows, reach and views per day, views by publishing day, best weekday and hour (in the account's time zone), when followers are online, top posts (including ones made outside OmniGram), average views by hashtag, content source, caption style and format, and the audience breakdown. Instagram only reports demographics and online followers for accounts with 100+ followers. Metrics it refuses for an account are learned and not requested again.

**API budget.** Instagram reports each app's and account's rate-limit usage in response headers. Background sync pauses above 70%, or while Instagram says access is throttled, so publishing always has headroom. The Settings page shows usage and calls per endpoint.

### Comments

The **Comments** tab lists recent comments across the account's posts, newest first, with the ones that have no reply highlighted. Replies, hiding and deleting go through the API. New comments without a reply also show as a badge on the account card.

## Settings and kill switches

All are OmniSettings and take effect within a minute, without a restart:

| Setting | Default | Effect |
|---|---|---|
| `OmniGram_Enabled` | true | false stops the whole engine (no planning, publishing or sync) |
| `OmniGramV2_PublishingEnabled` | true | false stops posting. Planning, captions and analytics carry on |
| `OmniGram_PublicBaseUrl` | `https://klive.dev` | Where Instagram downloads media from |
| `OmniGram_GraphApiVersion` | `v25.0` | The Graph API version used for every call |
| `OmniGram_Proxy` | empty (direct) | Sends Instagram traffic through `http://`, `https://` or `socks5://host:port`, optionally with `user:pass@` |

`OmniGramV2_PublishingEnabled` has a v2 name on purpose: OmniSettings writes a default on first read, so a new name is the only way to ship a new default.

## Data

Everything lives under `SavedData/OmniGram/v2/`:

```
app.json            Instagram app (app id; the secret is encrypted)
auth-flows.json     pending Instagram logins (survive restarts; expire after 30 minutes)
engine.json         engine state: next-due times, API usage, learned limits, migration result
events.jsonl        event log
secrets.key         root key for the vault: back it up together with the rest
accounts/{id}/      account.json (strategy, token, health), posts.json, insights.json
library/            per-account upload libraries
uploads/            files uploaded from the composer
prepared/           Instagram-ready copies, the only files the public link serves
thumbs/  cache/
```

Tokens and the app secret are encrypted with AES-256-GCM, using a per-purpose key derived (HKDF-SHA256) from `secrets.key`. They are decrypted only for the duration of an API call. Writes are atomic (temporary file, fsync, rename), and a background flusher persists changes.

On first start, v2 imports v1 data once: each account with its schedule, caption and hashtag settings mapped onto a strategy, and the content v1 already posted, so nothing is reposted. **Passwords are never copied.** Imported accounts arrive not connected, with autopilot off. The v1 files stay where they were. Because they hold plain-text passwords, Settings shows a warning with a **Remove v1 passwords** button: it strips the password from every v1 account file and deletes the v1 session files.

Housekeeping deletes prepared copies 6 hours after their post is published or dropped, unreferenced thumbnails after 2 days, composer uploads after 14 days and cache files after 3 days.

## HTTP API

All routes are under `/omnigram/` and registered with typed `CreateRoute`. Errors come back as JSON `{"error": "..."}` with a real status code. Live GETs are marked uncacheable for the response cache.

- **Read:** `overview`, `dashboard-stats`, `account`, `analytics`, `posts`, `post`, `media`, `content/thumb`, `library`, `memescraper/options`, `events`, `comments`, `settings`, `connect/status`
- **App and connections:** `settings/app`, `settings/check-public-url`, `settings/remove-v1-passwords`, `connect/begin`, `connect/token`, `accounts/refresh-token`, `oauth/callback` (public)
- **Accounts:** `accounts/update` (autopilot, pause, approval, strategy, notes, tags), `accounts/refresh`, `accounts/plan-now`, `accounts/remove`
- **Posts:** `posts/create`, `posts/update`, `posts/approve`, `posts/publish-now`, `posts/retry`, `posts/skip`, `posts/cancel`, `posts/regenerate-caption`, `posts/swap-content`, `posts/delete-remote`
- **Captions, media and comments:** `captions/preview`, `media/upload` (streaming, 500 MB), `library/delete`, `comments/reply`, `comments/hide`, `comments/delete`
- **Public:** `public/media` (signed links for Instagram's downloader)

Permissions (`OmniGramPerms`): `overview.view`, `accounts.read`, `accounts.act`, `accounts.manage`, `content.read`, `content.publish`, `comments.act`, `settings.view` and `settings.manage`.

## Website

`pages/schemery/omnigram/` in the KM site, on the OmniTrader design system:

- **Overview**: needs-attention items, KPIs, account cards, the queue, recently published, followers chart, activity log
- **Account**: tabs for overview, queue, published, analytics, strategy, comments and activity
- **Compose**: a reel, photo, carousel or story to one or more accounts, now or scheduled, with an AI caption draft
- **Settings**: the Instagram app and setup guide, the public media link check, connected accounts and their tokens, the engine and API usage, and v1 password removal

## Tests

```
DOTNET_ROLL_FORWARD=Major dotnet test Omnipotent.Tests/Omnipotent.Tests.csproj -c Release --filter "FullyQualifiedName~OmniGram"
```

These cover the API protocol (token exchange, containers, insights parsing, rate-limit headers), error classification, schedule maths (including DST), the planner, the publisher (idempotency, carousels and every failure policy), caption composition and the captioner's output parsing, the store and vault, the signed media links, the auth flows, migration, sync, and the website's read models.

Four tests run the real FFmpeg (remux, transcode, silent audio, image fitting). They are skipped unless a working build is found: set `OMNIGRAM_FFMPEG_DIR` to a folder with `ffmpeg` and `ffprobe`, or run the app once so it downloads FFmpeg.

Website: `npx playwright test tests/e2e/omnigram.spec.ts` runs every page against a stateful API mock. It checks that request bodies are real JSON, and that nothing scrolls sideways at phone width.
