# KliveAgent interactive latency

## Changes

- `KliveAgent_CompactPrompt` defaults to `true`. The operating policy is approximately 640 tokens without computer instructions and 809 with them, using the existing four-characters-per-token estimate. The previous policy was approximately 5,322 / 6,868 tokens. This is an 88% reduction in the policy section, not in the entire request: tool schemas, service descriptions, history and retrieved context still contribute tokens. Set this setting to `false` to restore the detailed policy and helper guide.
- Settings, provider capability checks, connection warmup, prompt preparation and service tool preparation overlap where independent.
- Streaming emits the first text delta immediately, then coalesces snapshot updates while tokens arrive, at most once per 50 ms. A final flush preserves the remaining text. Disabling `KliveAgent_StreamTokens` keeps buffered delivery.
- Chat readiness requires durable state, memory and execution tools. Optional source indexing and symbol ranking warm in the background. `/kliveagent/status` reports their separate `codebase` readiness. Code intelligence tools return their existing not-ready result until publication; direct file reads and searches remain available.
- Source indexing prunes generated/dependency directories before traversal and uses at most four parse workers. Graph construction resolves type references using a dictionary rather than repeatedly scanning every symbol. Background builds observe service cancellation.
- Conversation completion uses its existing awaited persistence operation without an additional identical write.

## Progress delivery contract

`GET /kliveagent/chat/pending?requestId=<id>&afterSequence=<last-sequence>&waitMs=15000`

When a running snapshot has no newer revision, the server waits for progress, completion or timeout. `waitMs` is optional and clamped to 0–20,000. A newer or terminal snapshot returns immediately. Multiple clients are notified together. Every response is a complete snapshot, so clients can reconnect using their last applied sequence.

Responses carry `Cache-Control: no-store` and `X-Klive-Pending-Wait: supported`. The website immediately requests the next update when this header is present. It retains the 600 ms polling interval for older servers during deployment. Navigation aborts the browser's outstanding wait; the server's bounded wait expires independently.

This provides an existing transport for a future voice client to receive incremental text. Speech synthesis, turn detection and audio transport are separate work.

## Measurement and remaining limits

`performance.recent[].stages.firstVisibleText.totalMs` measures brain/run start to the first server-side streamed text snapshot, including preparation. It is recorded once per streamed run with text. Missing values mean unmeasured, not zero. `firstToken` measures first text token latency for each streamed model attempt; it excludes run preparation. Network and browser rendering add latency to both server measurements.

The pre-change live sample showed about 20.1 seconds of average provider processing per request and 0.6 seconds of average queue time across 20 recent requests. First model requests had no reported cached tokens; continuation requests had about 95% cache hits. These are a small historical sample, not a benchmark of the new implementation. A startup probe could not execute because the deployed service was still indexing at 55% readiness.

After deployment, compare first-text median/p95, full-run median/p95, model request count, provider processing time and prompt-cache coverage for the same conversational and tool tasks. Smaller instructions and faster delivery cannot guarantee subsecond responses from a slow model/provider. No provider-specific thinking flags or model defaults were changed without a verified contract for the configured model alias.

## Verification

Backend latency tests exercise prompt reduction, immediate first text, lossless batching, progress fan-out, revision races, terminal delivery, timeouts, cancellation, directory pruning and dependency resolution. Existing KliveAgent tests cover durable run control, attachments, script execution and analytics. Browser tests cover the waiting protocol, compatibility with older servers and cancellation on new-chat navigation.

Local validation: 71 backend tests passed with the `FullyQualifiedName~KliveAgent` filter, including related project tool tests. Four browser tests passed across `kliveagent-latency.spec.ts` and the existing attachment/analytics test. The Nuxt production build succeeded. Backend tests used `DOTNET_ROLL_FORWARD=Major` because this machine has no .NET 9 runtime installed. Existing compiler/build warnings remain.
