# OmniGram Developer Guide

## Purpose
This guide explains how `OmniGram` is implemented, how autonomous posting works, how media is resolved from `MemeScraper`, how uploads are stored, and what to be careful about when extending the service.

## Core Architecture

### Main files
- `Services/OmniGram/OmniGram.cs`
  - service lifecycle
  - account onboarding
  - scheduling
  - autonomous scheduling
  - post processing
  - analytics composition
  - persistent event/upload metric writes
- `Services/OmniGram/OmniGramModels.cs`
  - account, campaign, post, event, and upload metric models
- `Services/OmniGram/OmniGramStore.cs`
  - persistent load/save for accounts/posts/campaigns/events
- `Services/OmniGram/OmniGramRoutes.cs`
  - API endpoints and request mode handling
- `Services/OmniGram/OmniGram.API.md`
  - full API documentation
- `Services/OmniGram/OmniGramDocumentation.cs`
  - embedded summary documentation text

### Persistence layout
Configured in `OmniPaths.GlobalPaths`:
- `OmniGramAccountsDirectory`
- `OmniGramPostsDirectory`
- `OmniGramCampaignsDirectory`
- `OmniGramSessionsDirectory`
- `OmniGramUploadsDirectory`
- `OmniGramEventsDirectory`
- `OmniGramUploadMetricsDirectory`

All persisted files are JSON except session-state files and uploaded media binaries.

## Account Onboarding

### API input contract
`OmniGramAddAccountRequest` uses:
- `username`
- `password`
- `useMemeScraperSource`
- `memeNiches`
- autonomous settings

### Important behavior
If MemeScraper mode is enabled:
- at least one niche must be supplied
- at least one configured MemeScraper source must match those niches

Credentials are encrypted at rest using DPAPI (`ProtectedData`).

## Autonomous Posting

### Trigger points
Autonomous queue checks happen:
1. on service boot (`EnsureAutonomousPostingSchedulesOnBoot`)
2. after each post processing attempt (success or failure)
3. after account save

### Eligibility checks
An account is considered for autonomous scheduling only when:
- account is active
- autonomous enabled
- MemeScraper mode enabled
- niche list present
- no already pending/processing post

### Schedule timing
Due time is computed with:
- base interval = `AutonomousPostingIntervalMinutes`
- random offset range = `[-AutonomousPostingRandomOffsetMinutes, +AutonomousPostingRandomOffsetMinutes]`
- minimum future clamp to avoid immediate past-due scheduling

## MemeScraper Media Resolution

### Old behavior (removed)
Direct source account id mapping.

### Current behavior
Niche-based mapping:
1. read account `PreferredMemeNiches`
2. find MemeScraper sources with overlapping niche tags
3. collect reels by source owner id/username
4. pick newest unposted reel first; fallback to newest available reel
5. persist used reel id to avoid repeats where possible

## Post Scheduling Modes

### JSON scheduling mode
Existing schedule payload with caption/dispatch metadata.

### File upload scheduling mode
Route supports raw binary uploads (`req.userMessageBytes`) with metadata via query params.

Upload flow:
1. route detects upload mode by `fileName|uploadedFileName` + body bytes
2. calls `SaveUploadedCampaignMedia`
3. media is written to `OmniGramUploadsDirectory`
4. schedule request is created using saved media path

## Event Logging and Upload Metrics

### Event logs (`OmniGramServiceEvent`)
Events capture:
- boot checks
- scheduling operations
- processing start
- success/failure outcomes
- exception metadata

Stored in `OmniGramEventsDirectory`.

### Upload metrics (`OmniGramUploadMetric`)
Metrics capture:
- account/post ids
- schedule/post times
- retry count
- provider id
- selected reel id
- caption length
- failure reason

Stored in `OmniGramUploadMetricsDirectory`.

## Analytics Composition
`GetAnalytics` currently aggregates from in-memory persisted models:
- post success/failure summary
- caption mode breakdown
- dispatch mode breakdown
- autonomous counters
- recent event error counters
- per-account stats
- top failure reasons

## Extension Guidance

### If adding real Instagram upload methods
Current `PublishWithInstagramApi` is a placeholder success path.
When implementing actual upload:
- keep typed API calls only
- do not use reflection
- preserve event + metric writes on all outcomes
- preserve autonomous rescheduling behavior

### If adding new schedule strategies
Prefer creating a helper method for due-time calculation.
Keep all schedule creation pathways writing both campaign and post records.

### If adding new routes
Follow existing route pattern used by `KliveCloudRoutes`:
- parse parameters safely
- return explicit status codes
- avoid throwing raw exceptions to user
- log persistent events for operational visibility

## Operational Caveats
- `ServiceMain` and some service patterns are `async void` by architecture; avoid blocking operations.
- `processLock` serializes post processing; this is safe but can limit throughput.
- source matching depends on meme niche quality in `MemeScraper` source data.
- uploaded media can grow storage quickly; retention/cleanup policy is a future improvement.

## Testing Checklist
1. Add account with MemeScraper niches and autonomous config.
2. Restart service and verify autonomous post appears.
3. Verify `GET /omnigram/posts/list` shows scheduled post.
4. Verify `GET /omnigram/logs/events` includes boot/schedule events.
5. Schedule upload-mode campaign with raw bytes and confirm upload file saved.
6. Trigger post processing and verify upload metrics file is created.
7. Verify `GET /omnigram/analytics/overview` reflects new data.
