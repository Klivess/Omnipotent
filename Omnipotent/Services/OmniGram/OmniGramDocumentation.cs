namespace Omnipotent.Services.OmniGram
{
    public static class OmniGramDocumentation
    {
        public static string BuildMarkdown()
        {
            return """
# OmniGram API Documentation

## Overview
OmniGram supports:
- managed Instagram accounts (username/password onboarding)
- niche-based MemeScraper sourcing
- autonomous influencer-style posting
- random scheduling offset to reduce deterministic timing
- direct media upload campaign scheduling
- persistent logs and upload metrics

## Routes

### POST `/omnigram/accounts/add`
Add/update account credentials and settings.

Body fields include:
- `username`, `password`
- `useMemeScraperSource`
- `memeNiches` (list)
- `autonomousPostingEnabled`
- `autonomousPostingIntervalMinutes`
- `autonomousPostingRandomOffsetMinutes`
- `autonomousCaptionPrompt`

### GET `/omnigram/accounts/list`
Returns account status and autonomous/niche configuration.

### POST `/omnigram/posts/schedule`
Supports two modes:
1. JSON payload scheduling
2. Raw file bytes upload mode with schedule metadata in query parameters (`fileName`, `dispatchMode`, etc.)

### GET `/omnigram/posts/list?take=500`
Returns recent post jobs.

### GET `/omnigram/analytics/overview?fromUtc=&toUtc=`
Returns reliability, autonomous posting, and event metrics.

### GET `/omnigram/logs/events?take=500`
Returns persistent service events.

### GET `/omnigram/health`
Returns service uptime and manager uptime.

## Operational Notes
- MemeScraper mapping is niche-based; source account ID is not used.
- Autonomous posting queue is ensured on service boot and after post processing.
- Upload metrics and events are persisted under `SavedData/OmniGram/Logs`.
""";
        }
    }
}
