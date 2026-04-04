namespace Omnipotent.Services.OmniGram
{
    public static class OmniGramDocumentation
    {
        public static string BuildMarkdown()
        {
            return """
# OmniGram API Documentation

## Overview
OmniGram is an Instagram management service inside Omnipotent with:
- credential-only onboarding (username + password)
- autonomous influencer posting from MemeScraper for configured accounts
- single-account or broadcast scheduling
- AI captions via KliveLLM
- persistent event logs and upload metrics

## Routes

### POST `/omnigram/accounts/add`
Adds or updates managed account credentials and autonomous posting settings.

Request body:
```json
{
  "username": "my_account",
  "password": "superSecret",
  "useMemeScraperSource": true,
  "memeScraperSourceAccountId": "1234567890",
  "autonomousPostingEnabled": true,
  "autonomousPostingIntervalMinutes": 240,
  "autonomousCaptionPrompt": "Write a short meme-style influencer caption"
}
```

### GET `/omnigram/accounts/list`
Returns managed accounts and autonomous posting configuration.

### POST `/omnigram/posts/schedule`
Schedules a post campaign.

### GET `/omnigram/posts/list?take=500`
Returns recent post jobs.

### GET `/omnigram/analytics/overview?fromUtc=&toUtc=`
Returns reliability, autonomous posting, and error analytics.

### GET `/omnigram/logs/events?take=500`
Returns persistent OmniGram service events.

### GET `/omnigram/health`
Returns service uptime and manager uptime.

## Operational Notes
- On service boot, OmniGram auto-schedules future posts for eligible autonomous accounts.
- After each post is processed, OmniGram persists upload metrics and schedules the next autonomous post.
- Event logs and upload metrics are persisted under `SavedData/OmniGram/Logs`.
""";
        }
    }
}
