# OmniGram API Documentation

## Overview
`OmniGram` is a managed Instagram automation service for Omnipotent.

Core capabilities:
- commander-managed Instagram account onboarding through API
- account credentials onboarding with **username + password only**
- autonomous influencer-style posting from `MemeScraper` on boot and continuously thereafter (if configured)
- single-account or all-account campaign scheduling
- AI caption generation through `KliveLLM`
- persistent event logs and upload metrics on disk
- analytics endpoints for posting and reliability

## Base URL
OmniGram routes are served by `KliveAPI`.

Typical local base URL:
- `http://localhost:5000`

---

## Authentication and Permissions
All OmniGram routes use existing KMProfile auth behavior from `KliveAPI`.

- Write operations: `KMPermissions.Manager`
- Read/analytics operations: `KMPermissions.Guest`

---

## Data Contracts

### OmniGramAddAccountRequest
```json
{
  "username": "my_instagram_account",
  "password": "superSecretPassword",
  "useMemeScraperSource": true,
  "memeScraperSourceAccountId": "1234567890",
  "autonomousPostingEnabled": true,
  "autonomousPostingIntervalMinutes": 240,
  "autonomousCaptionPrompt": "Write a short meme-style influencer caption with CTA and hashtags"
}
```

Validation notes:
- `username` required
- `password` required
- if `useMemeScraperSource=true`, `memeScraperSourceAccountId` required and must exist in `MemeScraper`
- `autonomousPostingIntervalMinutes` should be positive if supplied

### OmniGramScheduleRequest
```json
{
  "dispatchMode": 1,
  "accountId": null,
  "target": 0,
  "captionMode": 1,
  "userCaption": null,
  "aiCaptionPrompt": "Write a short, funny meme caption",
  "mediaPath": null,
  "scheduledForUtc": "2026-04-05T15:30:00Z"
}
```

Enum notes:
- `dispatchMode`: `0=SingleAccount`, `1=AllManagedAccounts`
- `target`: `0=Feed`, `1=Reel`, `2=Story`
- `captionMode`: `0=User`, `1=AI`

---

## Routes

## 1) Add or Update Managed Account
### `POST /omnigram/accounts/add`
Adds a new managed Instagram account or updates an existing one.

### Request Body
```json
{
  "username": "my_instagram_account",
  "password": "superSecretPassword",
  "useMemeScraperSource": true,
  "memeScraperSourceAccountId": "1234567890"
}
```

### Validation Rules
- `username` required
- `password` required
- if `useMemeScraperSource=true`, `memeScraperSourceAccountId` is required
- if `useMemeScraperSource=true`, source must exist in `MemeScraper`

### Success Response (`200`)
```json
{
  "AccountId": "c79eac177cb84f5e82995744d777ab6e",
  "Username": "my_instagram_account",
  "Status": 0,
  "UseMemeScraperSource": true,
  "MemeScraperSourceAccountId": "1234567890",
  "LastAuthenticatedUtc": "2026-04-05T10:51:16.912Z",
  "CreatedAtUtc": "2026-04-05T10:51:16.452Z",
  "UpdatedAtUtc": "2026-04-05T10:51:16.909Z"
}
```

### Error Response (`400`)
```json
{
  "error": "Specified MemeScraper source does not exist."
}
```

### Example cURL
```bash
curl -X POST "http://localhost:5000/omnigram/accounts/add" \
  -H "Content-Type: application/json" \
  -d '{
    "username":"my_instagram_account",
    "password":"superSecretPassword",
    "useMemeScraperSource":true,
    "memeScraperSourceAccountId":"1234567890"
  }'
```

---

## 2) List Managed Accounts
### `GET /omnigram/accounts/list`
Returns all managed OmniGram accounts including autonomous posting configuration.

### Success Response (`200`)
```json
[
  {
    "AccountId": "c79eac177cb84f5e82995744d777ab6e",
    "Username": "my_instagram_account",
    "Status": 0,
    "UseMemeScraperSource": true,
    "MemeScraperSourceAccountId": "1234567890",
    "CreatedAtUtc": "2026-04-05T10:51:16.452Z",
    "UpdatedAtUtc": "2026-04-05T10:51:16.909Z",
    "LastAuthenticatedUtc": "2026-04-05T10:51:16.912Z"
  }
]
```

### Example cURL
```bash
curl "http://localhost:5000/omnigram/accounts/list"
```

---

## 3) Schedule Post Campaign
### `POST /omnigram/posts/schedule`
Schedules posts for one or multiple managed accounts.

If `dispatchMode=AllManagedAccounts`, OmniGram fans out one post job per active managed account.

### Request Body (broadcast with AI + MemeScraper source)
```json
{
  "dispatchMode": 1,
  "target": 0,
  "captionMode": 1,
  "aiCaptionPrompt": "Write a punchy meme caption with 3 hashtags",
  "scheduledForUtc": "2026-04-06T12:00:00Z"
}
```

### Request Body (single account + manual media + manual caption)
```json
{
  "dispatchMode": 0,
  "accountId": "c79eac177cb84f5e82995744d777ab6e",
  "target": 0,
  "captionMode": 0,
  "userCaption": "New update just dropped 🚀",
  "mediaPath": "C:/Media/launch.jpg",
  "scheduledForUtc": "2026-04-06T13:00:00Z"
}
```

### Success Response (`200`)
```json
{
  "CampaignId": "1e8cb980f2864f569127c622f36bd52f",
  "CreatedAtUtc": "2026-04-05T11:02:33.711Z",
  "CreatedBy": "CommanderProfile",
  "DispatchMode": 1,
  "Status": "Scheduled",
  "PlannedPostIds": [
    "9e4868ed79e84f10bc4b1519d13bb3ff",
    "1f2d6d2d6dd64d34b916f015af0cdb93"
  ]
}
```

### Error Response (`400`)
```json
{
  "error": "No active accounts matched dispatch criteria."
}
```

### Example cURL
```bash
curl -X POST "http://localhost:5000/omnigram/posts/schedule" \
  -H "Content-Type: application/json" \
  -d '{
    "dispatchMode":1,
    "target":0,
    "captionMode":1,
    "aiCaptionPrompt":"Write a punchy meme caption with 3 hashtags",
    "scheduledForUtc":"2026-04-06T12:00:00Z"
  }'
```

---

## 4) List Recent Posts
### `GET /omnigram/posts/list?take=500`
Returns recent scheduled/published/failure post jobs.

### Query Parameters
- `take` (optional, int): max rows, capped at `5000`, default `500`

### Success Response (`200`)
```json
[
  {
    "PostId": "9e4868ed79e84f10bc4b1519d13bb3ff",
    "CampaignId": "1e8cb980f2864f569127c622f36bd52f",
    "AccountId": "c79eac177cb84f5e82995744d777ab6e",
    "Target": 0,
    "CaptionMode": 1,
    "UserCaption": null,
    "AICaptionPrompt": "Write a punchy meme caption with 3 hashtags",
    "MediaPath": null,
    "ScheduledForUtc": "2026-04-06T12:00:00Z",
    "Status": 2,
    "RetryCount": 0,
    "LastError": null,
    "CreatedAtUtc": "2026-04-05T11:02:33.722Z",
    "LastAttemptUtc": "2026-04-06T12:00:02.018Z",
    "PostedAtUtc": "2026-04-06T12:00:03.194Z",
    "ProviderPostId": "local-a840f7ef4ff0457687e6e40ecb7f0f15",
    "SelectedMemeReelPostId": "3569822751912668778"
  }
]
```

---

## 5) Analytics Overview
### `GET /omnigram/analytics/overview?fromUtc=&toUtc=`
Returns aggregate reliability and execution insights.

Includes:
- post success/failure rates
- caption usage
- dispatch usage
- autonomous posting counters
- event error counters
- per-account breakdowns

### Query Parameters
- `fromUtc` (optional, ISO date)
- `toUtc` (optional, ISO date)

### Success Response (`200`)
```json
{
  "RangeStartUtc": "2026-04-01T00:00:00Z",
  "RangeEndUtc": "2026-04-07T00:00:00Z",
  "TotalPosts": 82,
  "Posted": 75,
  "Failed": 7,
  "SuccessRate": 91.46341463414635,
  "CaptionUsage": {
    "User": 19,
    "AI": 63
  },
  "DispatchUsage": {
    "SingleAccount": 12,
    "AllManagedAccounts": 18
  },
  "ByAccount": [
    {
      "AccountId": "c79eac177cb84f5e82995744d777ab6e",
      "Username": "my_instagram_account",
      "Total": 44,
      "Posted": 41,
      "Failed": 3,
      "AvgRetries": 0.18
    }
  ],
  "TopFailureReasons": [
    {
      "error": "No valid media available for this post.",
      "count": 3
    }
  ]
}
```

---

## 6) Event Logs
### `GET /omnigram/logs/events?take=500`
Returns recent persistent OmniGram service events (boot checks, schedules, publish results, failures).

### Query Parameters
- `take` (optional, int): max rows, capped at `5000`, default `500`

### Success Response (`200`)
```json
[
  {
    "EventId": "1",
    "EventTimeUtc": "2026-04-05T10:51:16.912Z",
    "Level": "Information",
    "Message": "Service started successfully.",
    "AccountId": null,
    "PostId": null,
    "CampaignId": null
  },
  {
    "EventId": "2",
    "EventTimeUtc": "2026-04-05T10:52:00Z",
    "Level": "Warning",
    "Message": "Instagram authentication failed for account my_instagram_account.",
    "AccountId": "c79eac177cb84f5e82995744d777ab6e",
    "PostId": null,
    "CampaignId": null
  }
]
```

---

## 7) Service Health
### `GET /omnigram/health`
Returns OmniGram service health details.

### Success Response (`200`)
```json
{
  "Service": "OmniGram",
  "Uptime": "00:21:08.5514108",
  "ManagerUptime": "02:18:33.9181604"
}
```

---

## Autonomous Influencer Posting Lifecycle
When service boots:
1. OmniGram loads all managed accounts.
2. For each account where:
   - `Status=Active`
   - `UseMemeScraperSource=true`
   - `AutonomousPostingEnabled=true`
3. OmniGram ensures a future pending post exists.
4. If none exists, it creates a new autonomous campaign and post.

After each post is processed:
- OmniGram persists post status and upload metric.
- OmniGram re-checks autonomous queue and schedules the next autonomous post for that account.

This allows the bot to continue posting as its own influencer without commander intervention.

---

## Persistence and Logging
OmniGram writes extensive persistent data to disk:
- Accounts: `SavedData/OmniGram/Accounts`
- Posts: `SavedData/OmniGram/Posts`
- Campaigns: `SavedData/OmniGram/Campaigns`
- Session states: `SavedData/OmniGram/Sessions`
- Events: `SavedData/OmniGram/Logs/Events`
- Upload metrics: `SavedData/OmniGram/Logs/UploadMetrics`

Each upload metric stores when a post was scheduled, attempted, published/failed, retry count, provider id, selected meme reel id, caption length, and failure reason.

---

## Example Commander Workflow (Autonomous)
1. Add account with MemeScraper source and autonomous settings via `POST /omnigram/accounts/add`.
2. Do not schedule manual posts.
3. Let service boot and run.
4. Monitor with:
   - `GET /omnigram/posts/list`
   - `GET /omnigram/logs/events`
   - `GET /omnigram/analytics/overview`
