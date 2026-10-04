# CS2 Arbitrage Bot

Buys CS2 items on CSFloat below what they can be resold for, then drives each purchase through trade, Valve's 7-day trade protection and resale. Rebuilt in October 2026 after a year without a single opportunity.

## Why it found nothing for a year

Verified against the live sites on 2026-10-04:

1. **Every Steam price lookup failed.** Steam's market redesign (13 May 2026) turned `/market/listings/730/{name}/render` into a 302 to an HTML page. The bot parsed that HTML as JSON, so every comparison threw and nothing was ever evaluated.
2. **Even before that, the profit bar was unreachable.** The old model sold into the Steam buy order, paid the 15% Steam fee, and converted the Steam wallet back at 0.75–0.85. That needs Steam's buy order to be about 1.5–1.8× the CSFloat price. Live order books show about 1.28× (1.2–1.4) for typical items.
3. **The conversion coefficient was a hidden constant.** The liquidity search pulled price history from `/market/pricehistory`, which needs a login (400 anonymously). Every candidate was filtered out, so the coefficient silently fell back to 0.75. Measured conversion through cases is about 0.65–0.68.
4. **Startup hung before the routes existed.** Startup synchronously loaded every per-listing JSON file ever written (one per evaluated listing, each with a full Steam order book), so `/cs2arbitragebot/*` answered "Route not found".
5. **Other defects:**
   - `sort_by` was never sent to CSFloat; both "strategies" scanned the same random pages.
   - Any unexpected CSFloat response called `Environment.Exit(0)`, killing all of Omnipotent.
   - On a Steam 429 the retry wait doubled each time, up to 2^100 s.
   - The trade monitor polled `/me/trades` every 3 s, against a budget of 100 per window, and had no branch for cancelled trades.

## How it works now

```
CSFloat feed (most_recent, ~every 19 s) ─┐
CSFloat sweeps (highest_discount / best_deal, every 5 min; catches price cuts on old listings) ─┤
Structural scan (all ~28k items, hourly) ─┤
                                        ▼
            prefilter (bulk Steam prices, cached books; no network)
                                        ▼
            Steam order book (cached / paced) → evaluate both exits
                                        ▼
            re-check fresh book → buy (POST /listings/buy) → Discord
                                        ▼
  trade monitor → trade protection → sale scheduler → Steam sale or CSFloat relist
```

### Two exits per purchase

- **Steam market.** Sell into Steam buy orders, then carry the wallet back to CSFloat through the conversion plan.
  - The sale price is taken 3 units deep into the buy book, minus a 3% drift haircut, with Steam's exact fee rounding.
  - Wallet value is multiplied by the measured conversion coefficient *k*.
- **CSFloat relist.** Relist on CSFloat when trade protection lifts.
  - The item is valued at the lower of CSFloat's base and float-adjusted price, so no float premium is assumed.
  - Value is reduced by a 3% undercut, a 2% haircut and the 2% CSFloat fee.
  - Extreme discounts (under 60% of value) must be corroborated by Steam's ask.

A listing is bought only if an exit clears its ROI bar **and** the absolute profit is at least the minimum. Sellers who are away, fail over 5% of trades, or have a slow median trade time are skipped.

Right before buying, the bot projects the item's Steam price trend over the 7-day hold and values both exits at the projected price. It compares the last 3 days with days 4–14; only declines are counted, and the projection is clamped to −50%. This stops it buying cheap items that are cheap because they are sliding, such as a Major's stickers after the event.

At sale time the scheduler re-prices both exits and takes the better one.

### Conversion model (*k*)

Runs every 3 hours:

1. Rank floatless items (cases, capsules, stickers, charms) from CSFloat's price list against bulk Steam medians.
2. Price the best 60 on Steam's live sell side, at the cost of 5 units.
3. Verify the most liquid against real CSFloat sales history; the lower of the ask-based and sales-based rates wins.
4. Weight by how much volume each converter can absorb. Volume beyond that goes through cases.
5. Apply a ×0.97 haircut.

The result is published as the "liquidity plan" (KM page and `/produceliquidityplan`), which lists which items to buy on Steam to bring funds back.

### Data sources

| Source | Endpoint | Limit (measured) |
|---|---|---|
| Steam order book | `GET /market/orderbook?q=Load&qp=[730,"name"]` + header `x-valve-request-type: queryAction` | ~1.7 req/s without 429; paced at 1/s |
| Steam price history | `GET /market/actions?q=QueryPriceHistory&qp=[730,"name"]` | anonymous |
| Steam fallback | legacy `itemordershistogram` (needs item_nameid table) | — |
| CSFloat listing search | `GET /api/v1/listings` | **200 / hour / key** (fixed window) |
| CSFloat price list | `GET /api/v1/listings/price-list` (CDN snapshot, ~hourly) | 50k |
| CSFloat sales history | `GET /api/v1/history/{name}/graph` | 500 / day |
| CSFloat trades / me / inventory | `/me/trades`, `/me`, `/me/inventory` | 100 / 50k / 120 |
| Bulk Steam medians | `prices.csgotrader.app/latest/steam.json` (prior only, never a decision input) | — |
| Steam wallet | `IUserAccountService/GetClientWalletDetails` (SteamKit2 access token), HTML fallback | — |

### Market facts behind the defaults (Oct 2026)

- CSFloat receives about 190 new buy-now listings per minute between $0.50 and $40. One 50-listing page per poll cannot cover that, so the feed ignores listings under $2. Below that, no realistic deal clears the minimum profit.
- Most listings are priced at market. In a 150-listing live sample, none cleared 10% at a realistic *k*, and the median Steam-exit ROI was −21.5%. Deals are rare and short-lived, which is why the feed runs continuously.
- Cases carry about a 1.45× Steam premium (Steam buy order / CSFloat ask). A few liquid stickers trade near 1.0×. That spread is the only structural edge in the Steam route, and its capacity is small.

### Live dry run (2026-10-04, 20 minutes, read-only)

- **Setup:** the conversion model took 51 s (k = 0.730, 7 verified converters). The structural scan took 85 s (28,060 items ranked, 1,920 plausible, 0 confirmed for the Steam exit at that k).
- **Feed:** 65 polls (one every ~18.5 s), **0 coverage gaps**; 612 listings valued, 236 without any Steam call, 314 Steam lookups. Zero 429s on any CSFloat or Steam endpoint, and no errors.
- **Outcome:** seven listings at 5–20% ROI, all CSFloat-relist near misses on stickers at 10–11% (≈£0.75–0.84 profit each), just under the bar. No purchase would have been made, which is consistent with deals being rare. The near misses are why the relist bar now matches the original 10% and the trend guard exists.

## Settings (OmniSettings)

| Setting | Default | Meaning |
|---|---|---|
| `PerformCS2Scans` | true | Run the feed, sweeps and structural scan |
| `PurchaseCSFloatArbitrageOpportunities` | true | Buy; when false, opportunities are only alerted |
| `CS2ArbitrageMinimumSteamROIPercent` | 10 | ROI bar for the Steam exit |
| `CS2ArbitrageMinimumRelistROIPercent` | 10 | ROI bar for the CSFloat-relist exit |
| `CS2ArbitrageAllowCSFloatRelistExit` | true | Consider relisting on CSFloat |
| `CS2ArbitrageMinimumProfitPence` | 40 | Minimum absolute profit per trade (each trade costs a manual trade accept) |
| `CS2ArbitrageMinimumListingPriceCents` | 200 | Feed and sweep price floor (USD cents) |
| `CS2ArbitrageMaximumListingPriceDollars` | 0 | Absolute price cap (0 = none) |
| `CS2ArbitrageMaxSpendPerItemPercent` | 40 | Largest share of the CSFloat balance one item may cost |
| `CS2ArbitrageMaxUnitsPerItem` | 3 | Units of one item held at once |
| `CS2ArbitrageDailySpendLimitPounds` | 0 | Daily spend cap (0 = none) |
| `CS2ArbitrageDefaultConversionPercent` | 68 | *k* until the model has verified converters |
| `CS2ArbitrageSteamPriceHaircutPercent` | 3 | Steam price drift allowance over trade protection |
| `CS2ArbitrageMinimumSteamBuyOrders` | 15 | Minimum Steam buy-order count for an item to count as sellable |
| `CS2ArbitrageFeedMinIntervalSeconds` | 15 | Fastest feed poll (the 200/h budget usually sets ~19 s) |
| `CS2ArbitrageSteamRequestSpacingMs` | 1000 | Steam request spacing (backs off automatically on 429) |
| `CS2ArbitrageAlertOnUnboughtOpportunities` | true | Discord alert when a qualifying listing is not bought |

Settings are re-read every minute; no restart is needed.

## Routes

| Route | Permission | Purpose |
|---|---|---|
| `GET /cs2arbitragebot/status` | Guest | Engine state, budgets, conversion model, positions, recent errors (answers during startup) |
| `GET /cs2arbitragebot/getscanalytics` | Guest | KM analytics DTO (aggregate; O(1) regardless of history) |
| `GET /cs2arbitragebot/opportunities?limit=` | Guest | Recent notable evaluations (within 10 points of a bar, or bought) |
| `GET /cs2arbitragebot/scanresults` | Guest | Recent poll/sweep/structural cycle summaries (bounded) |
| `GET /cs2arbitragebot/latestliquidityplan` | Guest | Conversion plan |
| `GET /cs2arbitragebot/balanceHistory` | Guest | Daily balance records |
| `POST /cs2arbitragebot/scanNow` | Manager | Run a sweep and structural scan now |

The KM page (`/schemery/cs2arbitragebot`) has an **Engine Status** panel built on `/status`.

## Operating notes

- **Automation runs only on the server.** Elsewhere the routes work but nothing is bought or sold; set `CS2_ENGINE_LOCAL=1` to override.
- **Storage is bounded.**
  - Purchases live in `PurchasedItems/`.
  - Evaluations feed `scan-aggregate.json`.
  - Notable evaluations go to `NotableEvaluations/yyyy-MM-dd.jsonl`, kept for 30 days.
  - The old `ScannedComparisons/` and `ScanResults/` folders are no longer read; their file count is shown on `/status`, and they can be archived.
- **Purchases you still act on manually:**
  - Accepting the seller's Steam trade offer (Discord tells you).
  - Confirming Steam market listings in the mobile app.
  - Sending the trade when a relisted item sells on CSFloat.
  - Converting the Steam wallet back via the plan.
- **Pre-rewrite positions** whose sale date passed more than 30 days ago, and positions that failed to sell 5 times, are not auto-sold; Discord asks for a manual look.

## Verifying after Steam or CSFloat changes

    DOTNET_ROLL_FORWARD=Major dotnet test Omnipotent.Tests/Omnipotent.Tests.csproj -c Release --filter "FullyQualifiedName~Omnipotent.Tests.CS2ArbitrageBot"
    CS2_LIVE=1 DOTNET_ROLL_FORWARD=Major dotnet test Omnipotent.Tests/Omnipotent.Tests.csproj -c Release --filter "FullyQualifiedName~CS2LiveTests"

The live tests hit Steam, CSFloat and the bulk feed. `EngineDryRunAgainstTheLiveMarkets` runs the real engine read-only: purchasing is off, and a transport guard rejects any non-GET request. It reads the CSFloat key from `CS2_CSFLOAT_KEY` or the dev SavedData copy, and `CS2_LIVE_SECONDS` sets the feed duration.

## Not verified live

Buying, Steam selling, CSFloat relisting and wallet reads need the real account and real money, so they are covered by unit tests over the documented request shapes rather than exercised live. Watch the first purchase end to end.
