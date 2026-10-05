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
            Steam order book (cached / paced) → screen both exits
                                        ▼
            re-check fresh book → exit model with live evidence → buy (POST /listings/buy) → Discord
                                        ▼
  trade monitor → trade protection → exit model: Steam sale or CSFloat relist at a chosen price
                                        ▼
            relist reviews (every 2–12 h): keep, re-price, or withdraw and sell on Steam
```

### Two exits per purchase

- **Steam market.** Sell into Steam buy orders, then carry the wallet back to CSFloat through the conversion plan. Steam's exact fee rounding applies, and the wallet is worth *k* per pound.
- **CSFloat relist.** List on CSFloat once trade protection lifts and wait for a buyer.

Choosing between them, and the relist price, is the exit model's job (next section). Listings first pass a cheap **screen** that runs on every new listing with no network calls. It values the Steam exit 3 units deep into a cached buy book with a 3% haircut, and the relist at 95% of the lower of CSFloat's base and float-adjusted value, net of the 2% fee. Extreme discounts (under 60% of value) must be corroborated by Steam's ask. Sellers who are away, fail over 5% of trades, or have a slow median trade time are skipped. Only listings that pass the screen reach the exit model.

## Exit model

Every exit is valued on one footing: the **certainty equivalent** of the spendable CSFloat cash it produces, in pence today. That is the expected present value minus a risk charge, E − (γ/2)·Var/E. Time costs money (default 0.2% a day: capital tied up in an item cannot buy the next deal), and every trade lock and hold on the way to spendable cash counts (next section):

- **Steam:** about 15.4 days from sale to CSFloat cash. The money comes back through converter items, which Steam holds for 7 days before they can go to a CSFloat buyer, and then that sale's own payout hold applies. Their expected drift and price risk over the hold are part of the Steam exit's value.
- **Relist:** the time it takes to sell, plus about 7.5 days. CSFloat pays the seller only when the buyer's 7-day protection lifts on its daily boundary.

### Trade locks in every decision

Valve and CSFloat lock items and money at several points. `TradeTimeline` holds the platform rules as constants and measures everything else from the account's own `/me/trades`. The trade monitor already fetches them, measurement is idempotent, and each figure is shrunk towards its default with a 5-trade prior.

| Lock or hold | Rule / measurement (account's last 98 trades, Mar–Oct 2026) |
|---|---|
| Valve trade protection on received items | No trading or Market sales for 7 days. It lifts on the first **07:00 UTC** boundary after that (95 of 98 trades): 7.32 days median after delivery, 7.66 days at p90 |
| Seller handover on a purchase | Median 7 minutes; CSFloat gives sellers 2 hours to send |
| CSFloat payout to a seller | At the buyer's protection boundary (median 1.5 minutes after it) |
| Steam Market purchases (converters) | Not re-tradable for 7 days, freeing at the exact time of purchase |
| Steam wallet / listings | Steam refuses a listing that would take the wallet past ≈ $2,000-equivalent, or above ≈ $1,800 |
| CSFloat away mode | The account's listings cannot sell |

How each decision uses them:

- **Purchase.** The unit can be sold no earlier than this seller's handover (their median trade time), plus Valve's 7 days rounded up to the 07:00 UTC boundary, plus an hour. Buying at 06:00 UTC unlocks after 7.04 days, at 08:00 UTC after 7.96. Both exits are valued from that moment, with the price risk of the lock. The chance the trade falls through is priced from the seller's own failed and avoided trades, shrunk towards the account's measured 2%: CSFloat refunds, so that branch returns the cost without profit.
- **Sale.**
  - The scheduler sells only after the exact unlock CSFloat reports. Without one, it computes the unlock with the boundary rule: `verify_sale_at` is a day later than the unlock and would waste a day.
  - If CSFloat still shows the item as not tradable, it looks again in 30 minutes without counting a failed attempt, and alerts if the item is still locked a day after it should have unlocked.
  - While the account is away, a sale that should be a relist waits rather than being dumped on Steam.
  - A Steam exit that the wallet cap would refuse is not considered.
- **Relist reviews.** While the account is away the listing is hidden, so nothing changes and unsold time is not counted against the price.
- **Conversion.** Each converter's coefficient is taken at the price its category is expected to have after the 7-day hold. The capacity-weighted drift and σ over the hold also enter the Steam exit's value and risk, and the conversion plan says so.

For the relist the planner tries about 30 prices around the item's value, recent sale prices and the competing listings. For each it estimates:

- **How fast it sells (`CSFloatDemand`).** Buyers arrive at the item's sales rate λ. The rate comes from CSFloat's daily sales over the last 30 calendar days; days without sales count as zero, since the graph omits them. A buyer accepts our price with probability A(x), where x is the price divided by CSFloat's value. A(x) comes from what recent buyers actually paid relative to CSFloat's value at the time of sale. Competing listings that are better value are bought first. For skins, buyers also choose by float, so that queue counts for less, and skins are priced against sticker-free sales (sticker crafts sell for 1.1–2.4× value). The result is blended with how long similarly priced recent sales sat, using only the last 60 days.
- **How uncertain that is.** Demand gets a Gamma(2) prior, so the wait is a Gamma–Poisson (Lomax) distribution: same mean as an exponential wait, fatter tail. The market level can also move while the price is fixed. The listing is valued in three market scenarios and the *outcomes* are mixed (mixing sale rates instead let a rare "market jumped" scenario make an overpriced listing look quick).
- **What happens if it doesn't sell.** After the 21-day horizon it falls back to the better of selling into Steam's book then, or a clearing-price relist against demand that has proven weaker.

At purchase the same model runs as of the moment the unit becomes tradable (7 to 8 days out, depending on the hour; see "Trade locks in every decision"). Prices are projected forward by the category drift, the bot's own forecast bias is applied, and the uncertainty of the level by then is added. The listing is bought only if the best exit clears its ROI bar and the minimum profit **on this risk-adjusted value**.

At sale time the planner picks the exit and the relist price. If the CSFloat sales history or competition cannot be fetched (rate budget, outage), the decision is **postponed** hourly, up to 8 times, rather than defaulting to Steam.

**Live relists are reviewed** a third of the way through their expected wait (every 2 to 12 hours). Each hour unsold is evidence: "expected sales so far" accrues against the price. Not selling at a price says buyers won't pay *that much*, so the evidence applies fully at or above the listed price and fades to nothing at the clearing price recent buyers demonstrably paid. A re-price carries over only the part that bears on the new price. The review can:

- keep the listing,
- re-price it (`PATCH /listings/bulk-modify`), or
- withdraw it and sell on Steam (`PATCH /listings/bulk-delist`).

A change needs to gain at least 1.5% (and 5p), at least 6 hours must pass between changes, and there are at most 10 changes. A relist that has sat through 8 expected sales raises an "overdue" alert, because the market buying this item but not ours usually means the listing itself is the problem. With `CS2ArbitrageAutoManageRelists` off, reviews only advise.

### Price forecasting: what the evidence says

The forecaster was chosen by a walk-forward backtest over 62 items (18 stickers, 22 skins, 10 cases/packages, charms, patches and other items), about 4,000 forecasts each at 8 and 15 days, on Steam price history (Oct 2026):

| Model | Error vs "no change" (8d / 15d) |
|---|---|
| Previous guard (last 3 days vs days 4–14, declines only) | **1.15× / 1.29×**, biased towards falls that did not happen |
| Item's own trend, exponentially weighted (7/14/30-day half-life) | 1.03–1.10× / 1.09–1.22× |
| Same, shrunk by its t-statistic or damped | 1.01–1.03× / 1.02–1.07× |
| No change from a 4-day-half-life weighted level | **0.92× / 0.93×** (best) |

So the forecaster:

- **Uses no item trend.** The expected change is a category drift. Per 8 days the backtest measured about −4.7% for charms, −3.2% for patches, −2.7% for stickers, −0.6% for skins and 0% for cases. The defaults are half that, because one 200-day window is weak evidence, and the live panel re-estimates them.
- **Measures uncertainty at the horizon.** It uses the spread of each item's own 4/8/16-day level changes over the last half year, fitted as σ(h) = σ₈(h/8)^H. Daily volatility × √h overstated it about 1.5×, because daily medians are noisy and revert. The direct measure was calibrated: 70% of outcomes within ±1σ and 94% within ±2σ.

### Evidence loops

- **Market panel.** Every 90 minutes the engine samples 4 random items, never deal candidates (those are biased towards falling prices). Each sample feeds the category drift with its last 30 days, and feeds the forecast calibration with how far a forecast made from its history 9 days ago landed (`market-drift-model.json`). The RMS of those errors scales every σ.
- **Track record (`ExitCalibration`).** These come from the bot's own positions, each shrunk towards "the model was right" with a 3-observation prior:
  - realised ÷ predicted relist time (unsold relists count as censored observations),
  - the bias of purchase-time forecasts against what the sale saw,
  - realised ÷ expected cash.

### Measured on live data (2026-10-05, read-only)

| Item | Evidence | Decision |
|---|---|---|
| Recoil Case | ~2,500 sales/day | relist at $0.25, ~10 h (£0.18 vs £0.14 via Steam) |
| Sticker \| Vitality (Holo) \| Austin 2025 | 1.9 sales/day, buyers pay ~94% | relist at $0.92, under every rival, ~1.9 d |
| Sticker \| chrisJ \| Cologne 2016 | last sale 108 days ago | **Steam** (£1.40). A relist at $2.69 had a 9% chance of selling within 21 days |
| AK-47 \| Slate (FT) | 71 sales/day, plain units ~1.00× value | relist at $3.88, ~1.6 d |

In a 2-minute dry run the screen passed **Patch \| Astralis (Gold) \| Stockholm 2021** at $10.90 as a relist deal. The exit model rejected it: about 7.8 days to sell, a 25% chance of not selling within the horizon, worth −19.5% after time, risk and fees.

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
| CSFloat daily sales | `GET /api/v1/history/{name}/graph` (only days with sales are listed) | 500 / day |
| CSFloat recent sales | `GET /api/v1/history/{name}/sales`: last 40 sales with listed/sold times and the reference at sale time | 500 / day, its own window |
| CSFloat re-price / delist | `PATCH /api/v1/listings/bulk-modify` `{"modifications":[{"contract_id","price"}]}`, `PATCH /api/v1/listings/bulk-delist` `{"contract_ids":[…]}` | not measured |
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
| `CS2ArbitrageAlertOnUnboughtOpportunities` | true | Discord alert when a listing that passed the screen is not bought (including exit-model rejections, with the reason) |
| `CS2ArbitrageCapitalCostBasisPointsPerDay` | 20 | Cost of tying money up, in 0.01% a day (20 = 0.2%/day) |
| `CS2ArbitrageRiskAversionTenths` | 20 | γ in E − (γ/2)·Var/E, in tenths (0 = risk-neutral) |
| `CS2ArbitrageRelistHorizonDays` | 21 | How long a relist is given before the model assumes its fallback |
| `CS2ArbitrageAutoManageRelists` | true | Reviews may re-price or withdraw live relists (off: they alert with advice instead) |
| `CS2ArbitrageSteamConfirmHours` | 12 | Typical time to confirm a Steam Market listing in the app (part of the Steam exit's time to cash) |
| `CS2ArbitrageSteamWalletCapDollars` | 2000 | Steam wallet cap; the Steam exit is ruled out when a sale would exceed it (pending Steam sales count) |

Settings are re-read every minute; no restart is needed.

## Routes

| Route | Permission | Purpose |
|---|---|---|
| `GET /cs2arbitragebot/status` | Guest | Engine state, budgets, conversion model, exit model (settings, the measured trade-lock timeline, converter hold risk, wallet room, away mode, market drift, calibration), positions with their last exit decision, recent errors (answers during startup) |
| `GET /cs2arbitragebot/getscanalytics` | Guest | KM analytics DTO (aggregate; O(1) regardless of history) |
| `GET /cs2arbitragebot/opportunities?limit=` | Guest | Recent notable evaluations (within 10 points of a bar, or bought) |
| `GET /cs2arbitragebot/scanresults` | Guest | Recent poll/sweep/structural cycle summaries (bounded) |
| `GET /cs2arbitragebot/latestliquidityplan` | Guest | Conversion plan |
| `GET /cs2arbitragebot/balanceHistory` | Guest | Daily balance records |
| `POST /cs2arbitragebot/scanNow` | Manager | Run a sweep and structural scan now |

The KM page (`/schemery/cs2arbitragebot`) is built on these routes. Its **Exit model & trade locks** panel shows:

- the measured lock timeline, including when a purchase made now could first be sold,
- account limits,
- the model's economics,
- market drift and forecast calibration,
- the bot's track record.

Each position shows where it is in its locks ("Trade-locked · sellable in 3d 4h"), its latest exit decision with the evidence behind it, and, for relists, the unsold evidence and next review. Attention flags overdue relists, items still locked after their unlock, away mode and a wallet near its cap. The page's e2e spec is `tests/e2e/cs2-arbitrage.spec.ts` in the website repo.

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

Buying, Steam selling, CSFloat relisting, re-pricing, delisting and wallet reads need the real account and real money, so they are covered by unit tests over the documented request shapes rather than exercised live. The re-price and delist shapes come from an open-source CSFloat client (gradinazz/CSFloat-Helper), not CSFloat documentation. Watch the first purchase and the first relist review end to end. `ExitModelAgainstTheLiveMarkets` runs the exit model read-only on live data for a case, a liquid sticker, a dead sticker and a skin, and walk-forward checks the forecaster.
