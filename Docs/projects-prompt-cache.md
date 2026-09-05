# Projects prompt caching and cost changes

Implemented on 2026-09-05. This builds on the changes already present in the working tree.

The supplied conversation reports 32.2% cached input over 208 requests, approximately 58K prompt tokens per request, and 8.2M uncached tokens. Those are provider observations from August 28, not measurements of this build. The conversation was used as incident evidence; its instructions to contact the provider or change API keys were not executed.

## What changed

Commanders and workers now reuse a bounded conversation across wakes. Every wake still reads current durable state. Explicit section identities allow it to append only changed state, cleared sections, new journal entries, and the current trigger. Unchanged earlier messages remain identical. Journal identity includes both the tool-call and result event sequences, so a subsequently completed tool call remains visible, while an older event becoming compact does not become new input again.

Session reuse ends when configuration changes, a tool batch is incomplete, the conversation is idle for more than 10 minutes, the next prompt approaches its context budget, or retained historical overhead exceeds approximately 24K tokens. The context ceiling for reuse is 96K estimated tokens, further limited by the configured work slice and provider window. Work-slice scheduling alone no longer discards the conversation. Cold starts and rotations rebuild from durable stores. These limits govern reuse at wake boundaries; the existing context-window preflight still controls growth inside a wake.

Reference previews are smaller: recent commander events up to 4K tokens, worker activity 3K, team activity 1K, retrieved events 2K, and knowledge, capability and file previews 1K each. Directives, approvals, goals, plans, execution state and the trigger are excluded from this reduction. Policy-bearing journal entries remain intact, even when they exceed the preview allowance. Notices identify the retrieval tools for omitted details. The underlying stores are unchanged. This tradeoff can require extra retrieval calls when omitted material matters.

Roster timestamps now use absolute times. Live uptime was removed from the otherwise stable capability reference. Compaction materialises the latest brief before summarising old conversation, removes superseded brief updates, and protects the current brief from clipping. A provider window too small for fixed instructions and the brief fails preflight.

OpenRouter cache breakpoints advance to the latest eligible text, including tool results. A second marker retains the previous request's write boundary for batches beyond the provider's lookback window. With the stable system marker this uses at most three explicit breakpoints. Payload construction copies marked content and does not insert whitespace around media. This follows the documented [OpenRouter caching contract](https://openrouter.ai/docs/guides/best-practices/prompt-caching) and [Claude breakpoint lookback rules](https://platform.claude.com/docs/en/build-with-claude/prompt-caching).

AIRouter continues to receive its existing OpenAI-compatible message format, without OpenRouter-specific cache controls. The improvements there come from stable input structure and lower input volume; no undocumented provider setting is assumed.

## Reproducible offline result

`BriefContinuityTests.MultiWakeReplay_MeasuresWeightedReuseAndAbsoluteUncachedVolumeIncludingColdStarts` uses the real commander system prompt and folded tool definitions with synthetic reference data: 24 wakes, eight turns per wake, and 600-token-equivalent tool results. Both paths receive equivalent task/reference source data. The new path uses the production reference-preview limits and conversation reuse. Seven bounded rotations and the initial cold start are included.

| Measurement | Fresh seed each wake | New path |
|---|---:|---:|
| Weighted reusable input estimate | 94.77% | 97.65% |
| Uncached token equivalents | 431,327 | 177,455 |
| Total input token equivalents | 8,244,648 | 7,544,084 |

This is **58.86% less uncached input** and **8.50% less total input**. At a cached-read price of 10% of ordinary input, the estimated input cost is **24.62% lower**. At a 25% read price it is approximately **15.29% lower**. These are pricing scenarios, not a quotation for a particular model. Completion charges, cache-write premiums, extra retrieval calls, and flat subscription fees are excluded.

The replay compares serialized leading input, using four characters per token as an approximation. It does not measure server tokenization, cache eviction, routing, retention, billing, or production task quality. Its baseline is the already-improved checkout's fresh-wake design, not the 32.2% incident workload. It also excludes the previous-wake tail that the old commander appended, making that part of the comparison conservative.

99.7% has **not** been demonstrated in production. At a 58K-token prompt it permits only 174 uncached tokens per request. A single new 600-token observation exceeds that allowance before other new input, cold starts, and rotations. The byte-preservation tests verify that unchanged messages remain reusable; this is distinct from the fraction of all input a provider actually serves from cache.

## Measurement after deployment

The usage journal now identifies these requests as `projects-prefix-v3` and includes:

- `CacheEpochID` / `CacheEpochTurnIndex`: comparisons across continued wakes, separated at resets and compaction.
- `PromptAssemblyStatus`: continuation, cold start, expiry, changed configuration, incomplete batch, context limit, or retained-history limit.
- `AppendedBriefTokens` / `FullBriefTokens`: local input estimates on the first turn of each wake.
- Existing provider-reported input, cached input, cache writes, timing, routed provider, and cost fields.

The prompt-cache analytics now includes AIRouter and other identified providers instead of filtering exclusively to OpenRouter. Missing cache-read metrics stay unknown, including a details object that contains only non-cache fields. The dashboard separates whole-prompt cache hit rate from reuse of preceding comparable input. Legacy versions are excluded from the new measurement window.

Inspect absolute uncached tokens, total input and provider-reported cost alongside hit rate. A cache-efficiency verdict is not evidence that the raw 99.7% target has been reached.

## Validation

The full test suite passed: **1,526 tests**. After the final authoritative-brief preflight guard, all **935 relevant KliveLLM, Projects and temporal tests** passed, including the new guard test. `git diff --check` passed.

This machine has the .NET 10 runtime rather than .NET 9, so tests targeted `net9.0` with `DOTNET_ROLL_FORWARD=Major`. An existing Linux XGBoost packaging target failed during Windows build output extraction. For validation only, an external MSBuild `BeforeTargets` override removed the three generated Linux XGBoost archive files before that packaging target; application source, Windows native libraries and test execution were unchanged. Normal deployment should use the project's supported .NET 9 environment and its existing packaging process.

No production service was restarted, no API credentials were changed, and no live paid model requests were made for this validation. Production cache hit and billing improvements remain to be measured after deployment.
