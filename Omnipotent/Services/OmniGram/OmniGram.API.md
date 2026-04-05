# OmniGram API Documentation

## Overview
`OmniGram` is a managed Instagram automation service for Omnipotent.

Key behavior:
- Account onboarding uses **username + password** only for Instagram login.
- Commander can enable MemeScraper content by providing **niches** (not source account id).
- Autonomous posting is supported and boot-time auto-scheduling is performed for eligible accounts.
- Autonomous scheduling supports a random time offset to reduce deterministic posting patterns.
- Campaigns can be scheduled either with normal JSON payloads or with **direct uploaded media bytes**.
- Persistent data is written for accounts, campaigns, posts, events, and upload metrics.

## Base URL
Routes are served through `KliveAPI`.

Typical local base URL:
- `http://localhost:5000`

---

## Authentication and Permissions
- Write routes: `KMPermissions.Manager`
- Read routes: `KMPermissions.Guest`

---

## Routes

## 1) Add or Update Managed Account
### `POST /omnigram/accounts/add`
Adds or updates managed account credentials and posting behavior.

### Request Body
```json
{
  "username": "my_instagram_account",
  "password": "superSecretPassword",
  "useMemeScraperSource": true,
  "memeNiches": ["fitness", "motivation"],
  "autonomousPostingEnabled": true,
  "autonomousPostingIntervalMinutes": 240,
  "autonomousPostingRandomOffsetMinutes": 45,
  "autonomousCaptionPrompt": "Write a short influencer caption with CTA and 3 hashtags"
}
```

### Validation Rules
- `username` required
- `password` required
- if `useMemeScraperSource=true`, `memeNiches` must contain at least one niche
- if `useMemeScraperSource=true`, at least one MemeScraper source must match supplied niches
- `autonomousPostingIntervalMinutes` must be `> 0` when provided
- `autonomousPostingRandomOffsetMinutes` must be `>= 0` when provided

---

## 2) List Managed Accounts
### `GET /omnigram/accounts/list`
Returns all managed accounts with autonomous settings.

Returned fields include:
- `AccountId`
- `Username`
- `Status`
- `UseMemeScraperSource`
- `PreferredMemeNiches`
- `AutonomousPostingEnabled`
- `AutonomousPostingIntervalMinutes`
- `AutonomousPostingRandomOffsetMinutes`
- `AutonomousCaptionPrompt`
- timestamps

---

## 3) Schedule Post Campaign (JSON mode)
### `POST /omnigram/posts/schedule`
Schedules campaign posts via JSON payload.

### JSON Request Example
```json
{
  "dispatchMode": 1,
  "target": 0,
  "captionMode": 1,
  "aiCaptionPrompt": "Write a punchy meme caption",
  "scheduledForUtc": "2026-04-06T12:00:00Z"
}
```

Notes:
- `dispatchMode`: `0=SingleAccount`, `1=AllManagedAccounts`
- `target`: `0=Feed`, `1=Reel`, `2=Story`
- `captionMode`: `0=User`, `1=AI`

---

## 4) Schedule Post Campaign (file upload mode)
### `POST /omnigram/posts/schedule?fileName=...&dispatchMode=...&...`
Allows commander to upload a media file directly in the request body bytes.

### Required query parameters for upload mode
- `fileName` (or `uploadedFileName`)
- `dispatchMode`
- `target`
- `captionMode`
- For single-account mode: `accountId`

### Optional query parameters
- `userCaption`
- `aiCaptionPrompt`
- `scheduledForUtc`

### Body
- Raw file bytes (`req.userMessageBytes`)

### Example
`POST /omnigram/posts/schedule?fileName=clip.mp4&dispatchMode=0&accountId=abc123&target=1&captionMode=0&userCaption=Fresh%20upload`

Body: binary file bytes.

---

## 5) List Recent Posts
### `GET /omnigram/posts/list?take=500`
Returns recent post jobs and statuses.

---

## 6) Analytics Overview
### `GET /omnigram/analytics/overview?fromUtc=&toUtc=`
Returns:
- success/failure rates
- caption usage
- dispatch usage
- autonomous posting counters
- event counters
- account-level performance breakdown
- top failure reasons

---

## 7) Event Logs
### `GET /omnigram/logs/events?take=500`
Returns recent persistent OmniGram service events (boot checks, scheduling events, publish results, failures).

---

## 8) Service Health
### `GET /omnigram/health`
Returns service and manager uptime.

---

## Autonomous Posting Behavior
When service boots, OmniGram checks all managed accounts and automatically schedules the next autonomous post when:
- account is active
- autonomous posting is enabled
- MemeScraper source mode is enabled
- niche preferences are configured
- no pending/processing post already exists for the account

The schedule time is:
- base interval: `autonomousPostingIntervalMinutes`
- plus random offset in range `[-autonomousPostingRandomOffsetMinutes, +autonomousPostingRandomOffsetMinutes]`
- minimum clamp keeps due time safely in the future

---

## Persistence Paths
All relative to app base directory.
- `SavedData/OmniGram/Accounts`
- `SavedData/OmniGram/Posts`
- `SavedData/OmniGram/Campaigns`
- `SavedData/OmniGram/Sessions`
- `SavedData/OmniGram/Uploads`
- `SavedData/OmniGram/Logs/Events`
- `SavedData/OmniGram/Logs/UploadMetrics`
