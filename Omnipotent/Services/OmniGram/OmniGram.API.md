# OmniGram API Documentation

## Overview
`OmniGram` manages Instagram accounts using `InstagramApiSharp` and supports:
- managed account onboarding
- autonomous meme posting
- direct media upload scheduling
- live account data retrieval from Instagram
- live account analytics from Instagram
- Instagram profile updates (bio/display/profile fields/pfp)
- Instagram media deletion

## Base URL
- `http://localhost:5000`

## Permissions
- Write routes: `KMPermissions.Manager`
- Read routes: `KMPermissions.Guest`

---

## Account Onboarding
## `POST /omnigram/accounts/add`
Adds/updates managed account credentials.

Request body:
```json
{
  "username": "my_instagram_account",
  "password": "superSecretPassword"
}
```

Current implementation also accepts optional settings fields (`useMemeScraperSource`, `memeNiches`, autonomous fields), but onboarding core is username/password.

Response includes:
- saved managed-account config
- `LiveVerification` object fetched via `InstagramApiSharp` live call

---

## Managed Account Configuration Routes

## `POST /omnigram/accounts/updateSettings`
Updates existing managed account configuration.

Request body:
```json
{
  "accountId": "abc123",
  "useMemeScraperSource": true,
  "memeNiches": ["fitness", "motivation"],
  "autonomousPostingEnabled": true,
  "autonomousPostingIntervalMinutes": 240,
  "autonomousPostingRandomOffsetMinutes": 45,
  "autonomousCaptionPrompt": "Write a short influencer caption"
}
```

## `GET /omnigram/accounts/list`
Lists all managed accounts and current config.

## `GET /omnigram/accounts/live?accountId=...`
Fetches **live Instagram account data** for the managed account via `InstagramApiSharp` (`GetUserInfoByUsernameAsync`).

## `GET /omnigram/accounts/liveAnalytics`
Fetches **live Instagram data for all active managed accounts**.

---

## Instagram Profile Management
## `POST /omnigram/accounts/updateProfile`
Updates a managed account's Instagram profile via live API calls.

Supported changes:
- biography (`SetBiographyAsync`)
- profile picture (`ChangeProfilePictureAsync`) using request body bytes
- profile fields (`EditProfileAsync`) for display name, external URL, email, phone, gender, username

Request body:
```json
{
  "accountId": "abc123",
  "displayName": "My New Display Name",
  "biography": "Updated bio",
  "externalUrl": "https://example.com",
  "email": "brand@example.com",
  "phoneNumber": "+123456789",
  "gender": 1,
  "username": "new_username_optional"
}
```

If raw image bytes are included in request body, OmniGram attempts profile picture update.

---

## Campaign Scheduling

## `POST /omnigram/posts/schedule` (JSON mode)
Request body example:
```json
{
  "dispatchMode": 1,
  "target": 0,
  "captionMode": 1,
  "aiCaptionPrompt": "Write a punchy meme caption",
  "scheduledForUtc": "2026-04-06T12:00:00Z"
}
```

## `POST /omnigram/posts/schedule` (file-upload mode)
Supply query params + raw body bytes.

Required query parameters:
- `fileName` (or `uploadedFileName`)
- `dispatchMode`
- `target`
- `captionMode`
- `accountId` (when single-account mode)

Body:
- raw file bytes

Supported extensions:
- video: `.mp4`, `.mov`, `.m4v`, `.avi`, `.webm`
- image: `.jpg`, `.jpeg`, `.png`

## `GET /omnigram/posts/list?take=500`
Returns recent post jobs.

---

## Instagram Media Deletion
## `POST /omnigram/posts/deleteFromInstagram`
Deletes a previously uploaded Instagram media item through `InstagramApiSharp` (`DeleteMediaAsync`).

Request body:
```json
{
  "accountId": "abc123",
  "mediaId": "3571234567890123456_123456789",
  "mediaType": 1
}
```

`mediaType` maps to `InstaMediaType` enum integer values.

---

## Analytics and Logs
## `GET /omnigram/analytics/overview?fromUtc=&toUtc=`
Returns persisted OmniGram analytics plus autonomous/event counters.

## `GET /omnigram/logs/events?take=500`
Returns persistent service event logs.

## `GET /omnigram/health`
Returns service uptime details.

---

## Account Creation
Automatic Instagram account creation is not supported in OmniGram.
