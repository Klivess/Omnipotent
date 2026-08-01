# OmniTrader

OmniTrader is the trading service inside Omnipotent. It has **two layers**:

- **The strategy engine** (§1–§18) — a modular engine that runs the *same* strategy code across **backtest**, **paper** and **live**, over any symbol(s), single-asset or multi-asset. It knows nothing about any specific strategy.
- **The firm layer** (§19–§31) — the trading *operating system* on top: venue adapters (Kraken spot + IG CFD), a canonical instrument master, a mandatory risk decision before every order, an audited order lifecycle, an immutable ledger reconciled against broker truth, a journal, alerting and operations.

The engine answers *"what should we trade?"*. The firm layer answers *"is the firm allowed to, did it actually happen, and can we prove it?"*

> **Design principle that shapes the firm layer:** broker-reported orders and fills determine external reality; the internal ledger provides attribution, history and reconciliation. Where the two disagree, the platform raises a classified break rather than quietly adopting either side.

---

## Table of contents

**Strategy engine**

1. [Mental model](#1-mental-model)
2. [Directory map](#2-directory-map)
3. [The strategy contract](#3-the-strategy-contract)
4. [Strategy types](#4-strategy-types)
5. [Writing a strategy](#5-writing-a-strategy)
6. [Strategy parameters](#6-strategy-parameters)
7. [Execution modes & sessions](#7-execution-modes--sessions)
8. [Orders, brackets & routers](#8-orders-brackets--routers)
9. [Margin & leverage](#9-margin--leverage)
10. [Backtesting](#10-backtesting)
11. [Live trading](#11-live-trading)
12. [Market data](#12-market-data)
13. [Persistence](#13-persistence)
14. [HTTP API](#14-http-api)
15. [Built-in strategies](#15-built-in-strategies)
16. [Indicators](#16-indicators)
17. [Testing](#17-testing)
18. [Gotchas & conventions](#18-gotchas--conventions)

**Firm layer**

19. [Firm layer overview](#19-firm-layer-overview)
20. [Venues & environments](#20-venues--environments)
21. [Instrument master](#21-instrument-master)
22. [Risk service](#22-risk-service)
23. [Order flow](#23-order-flow)
24. [Ledger & reconciliation](#24-ledger--reconciliation)
25. [Portfolio & valuation](#25-portfolio--valuation)
26. [Journal, research & performance](#26-journal-research--performance)
27. [Operations, alerts & health](#27-operations-alerts--health)
28. [Firm HTTP API](#28-firm-http-api)
29. [Any symbol, live data](#any-symbol-not-just-the-instrument-master)
30. [Real money, simulated money, adopted holdings](#30-real-money-simulated-money-and-holdings-you-already-own)
31. [The UI](#31-the-ui)

---

## 1. Mental model

```
                    ┌──────────────────────────────────────────────┐
   Strategy  ──────▶│  declares symbols + reacts to bars            │
   (your code)      │  OnCandleClose(candle)   |  OnUniverseBar(bar)│
                    └───────────────┬──────────────────────────────┘
                                    │ SubmitOrder(OrderRequest)
                                    ▼
        ┌───────────── Session (one per deployment / backtest job) ─────────────┐
        │  feeds bars, owns the book, persists state                            │
        │   • BacktestSession   — historical bars, deterministic                │
        │   • PaperSession      — live data, simulated fills                    │
        │   • LiveSession       — live data, real Kraken orders + RiskGate      │
        │   • MultiAssetSession — paper/live for universe (multi-symbol) strats │
        └───────────────┬───────────────────────────────────────────────────────┘
                        │ PlaceOrderAsync
                        ▼
            ┌──────────── IOrderRouter ────────────┐
            │  SimulatedOrderRouter (backtest/paper)│   spot + margin, brackets,
            │  KrakenOrderRouter    (live)          │   liquidation, funding
            └───────────────────────────────────────┘
                        │ marks / fills
                        ▼
            MarketDataRouter (Binance primary, Kraken fallback) + SQLite (omnitrader.db)
```

A **deployment** is a running paper/live strategy instance. A **backtest job** is a one-shot historical run. Both wrap the same strategy and the same execution core.

The entry point is [`OmniTrader.cs`](OmniTrader.cs), which wires the DB, repositories, `MarketDataRouter`, `StrategyRegistry`, `SessionManager`, `BacktestJobQueue`, and the HTTP routes.

---

## 2. Directory map

| Folder | What's in it |
|---|---|
| [`Api/`](Api) | `OmniTraderRoutes.cs` — all HTTP endpoints + request DTOs. |
| [`Backtesting/`](Backtesting) | `BacktestSession` (the one engine), `BacktestJobQueue` (worker), `BacktestResult` + `BacktestMetrics`, `MomentumBacktestRunner` (opt-in validation), `Validation/` (walk-forward, deflated Sharpe, survivorship, turnover, cost sensitivity). |
| [`Contracts/`](Contracts) | `Models.cs` (candles, orders, fills, positions, configs, margin, `PortfolioBar`), `IStrategyHost`, `IOrderRouter`, `IMarketDataProvider`, `ExchangeFill`. |
| [`Execution/`](Execution) | `SimulatedOrderRouter` (sim fills, spot+margin+brackets+liquidation), `KrakenOrderRouter` (real REST), `RiskGate` (live caps), `LiveLedger` (live position/PnL accounting), `KrakenSymbolMap`. |
| [`MarketData/`](MarketData) | `MarketDataRouter` (cache-first + websocket multiplexer), `BinanceMarketDataProvider`, `KrakenMarketDataProvider`, `BinanceUniverseProvider` (top-N by volume). |
| [`Persistence/`](Persistence) | `OmniTraderDb` (SQLite), one repository per table, `Schema/OmniTraderSchema.cs` (migrations). |
| [`Sessions/`](Sessions) | `SessionManager` (creates/recovers/kills deployments), `PaperSession`, `LiveSession`, `MultiAssetSession`, `StrategyHost`. |
| [`Strategy/`](Strategy) | `TradingStrategy` (base class), `StrategyRegistry` (attribute discovery), `StrategySymbols`/`UniverseSpec`, `StrategyContext`, `Indicators`, `Params/` (the `[Param]` system), `Momentum/` (helpers for the momentum strategy), `Strategies/` (concrete strategies). |

---

## 3. The strategy contract

Every strategy derives from [`TradingStrategy`](Strategy/TradingStrategy.cs) and is decorated with `[TradingStrategy]`:

```csharp
[TradingStrategy("Display Name", "What it does.", RequiresUniverse = false)]
public sealed class MyStrategy : TradingStrategy
{
    // Must have a public parameterless constructor (the registry uses Activator.CreateInstance).

    public override StrategySymbols DeclareSymbols() => StrategySymbols.Of("BTCUSDT");

    public override Task OnStart(CancellationToken ct)                 => Task.CompletedTask;
    public override Task OnCandleClose(OHLCCandle c, CancellationToken ct) => Task.CompletedTask; // single-symbol
    public override Task OnUniverseBar(PortfolioBar b, CancellationToken ct) => Task.CompletedTask; // multi-asset
    public override Task OnOrderFilled(FillEvent f, CancellationToken ct)  => Task.CompletedTask;
    public override Task OnStop(CancellationToken ct)                 => Task.CompletedTask;
}
```

### Lifecycle callbacks

| Callback | When | Notes |
|---|---|---|
| `DeclareSymbols()` | Once, **after** parameters are applied, before data subscription. | Returns either a fixed symbol set or a `UniverseSpec`. This is how the engine decides single- vs multi-symbol. |
| `OnStart(ct)` | Once before the first bar. | In a **backtest**, `History` is empty here (bars stream in afterwards). |
| `OnCandleClose(candle, ct)` | Each closed bar (single-symbol strategies). | The candle is final; `History` includes it. Orders execute on the **next** bar. Live/paper enforce a **10-second** timeout. |
| `OnUniverseBar(bar, ct)` | Each synchronized bar (multi-asset strategies). | `bar.Histories` is point-in-time per-symbol history; nothing looks ahead. |
| `OnOrderFilled(fill, ct)` | When a fill is booked. | |
| `OnStop(ct)` | On stop/kill/finish. | |

### Protected helpers (available inside callbacks)

```csharp
IReadOnlyList<OHLCCandle> History            // growing candle buffer (single-symbol; capped at 5000 live/paper)
Position? Position                           // current position (single-symbol book)
decimal QuoteBalance, BaseBalance            // single-symbol balances
string Symbol                                // the host symbol
decimal Leverage                             // account leverage (1 = spot)
IReadOnlyDictionary<string,decimal> Positions// signed qty per symbol (portfolio book)
decimal Equity                               // total account equity (cash + marked positions)
Task<OrderIntent> SubmitOrder(OrderRequest)  // place an order
Task CancelOrder(string intentId)
void Log(string), void LogError(string, Exception?)
Ctx                                          // StrategyContext: Host + CandleHistory
```

> **Read `Leverage` to size into margin.** For example the TCN strategy sets its max weight to ±`Leverage` (spot = long-only `[0,1]`, margin = `[-N, +N]`).

---

## 4. Strategy types

There are exactly **two** shapes. The engine routes on `DeclareSymbols().IsUniverse` — never on the strategy's identity.

### A. Single-symbol strategies

- `DeclareSymbols()` returns `StrategySymbols.Of("BTCUSDT")` (or a `[Param]`-driven symbol).
- Override **`OnCandleClose`**.
- Use `History`, `Position`, `QuoteBalance`/`BaseBalance`.
- Run as `BacktestSession` (N=1), `PaperSession`, or `LiveSession`.
- Examples: **IBS Mean Reversion**, **TCN Volatility Signal**, **Flow Signal Trader**.

### B. Cross-sectional / universe (multi-asset) strategies

- Set `RequiresUniverse = true` on the attribute.
- `DeclareSymbols()` returns `StrategySymbols.FromUniverse(new UniverseSpec { TopN = …, RegimeSymbol = … })`.
- Override **`OnUniverseBar`** (ignore `OnCandleClose`).
- Use `Positions` (whole-book) and `Equity`; place per-symbol orders.
- Run as `BacktestSession.RunPortfolioAsync` (backtest) or `MultiAssetSession` (paper/live).
- The engine resolves the universe (top-N by volume via `BinanceUniverseProvider`), fetches each symbol's history, and feeds a `PortfolioBar` each step. **No strategy-specific data source is required.**
- Example: **Cross-Sectional Momentum**.

`UniverseSpec`:

```csharp
public sealed class UniverseSpec {
    public int    TopN        = 100;      // universe size (top-N by 24h quote volume)
    public string QuoteAsset  = "USDT";   // venue quote asset
    public string RegimeSymbol = "BTCUSDT"; // benchmark + default regime/chart asset
}
```

> The backtester is **multi-asset-native**: a single-symbol backtest is just the N=1 case of the same portfolio engine ([`BacktestSession.RunCoreAsync`](Backtesting/BacktestSession.cs)).

---

## 5. Writing a strategy

1. Create a class in [`Strategy/Strategies/`](Strategy/Strategies) deriving from `TradingStrategy`.
2. Decorate it with `[TradingStrategy("Name", "Desc")]` (add `RequiresUniverse = true` if multi-asset).
3. Give it a **public parameterless constructor**.
4. Implement `DeclareSymbols()` and the relevant bar callback.
5. Expose tunables as `[Param]` properties (see §6).
6. Place orders with `SubmitOrder(...)`.

The `StrategyRegistry` auto-discovers it via reflection at startup ([`StrategyRegistry.DiscoverFrom`](Strategy/StrategyRegistry.cs)) — no manual registration. It immediately appears in `/api/omnitrader/strategies` and the deploy/backtest UI, with its parameter schema.

### Minimal single-symbol example

```csharp
[TradingStrategy("SMA Cross", "Long when price closes above its SMA, flat otherwise.")]
public sealed class SmaCrossStrategy : TradingStrategy
{
    [Param("SMA Period", Group = "Signal", Min = 5, Max = 200)]
    public int Period { get; set; } = 50;

    [Param("Symbol", Group = "Universe", IsSymbol = true)]
    public string TradeSymbol { get; set; } = "BTCUSDT";

    public override StrategySymbols DeclareSymbols() => StrategySymbols.Of(TradeSymbol);

    public override async Task OnCandleClose(OHLCCandle candle, CancellationToken ct)
    {
        var h = History;
        if (h.Count < Period) return;
        decimal sma = Indicators.SMA(h, Period, h.Count - 1);
        bool wantLong = candle.Close > sma;
        bool isLong = Position is { IsLong: true };

        if (wantLong && !isLong)
            await SubmitOrder(Market(OrderSide.Buy, QuoteBalance * 0.95m / candle.Close), ct);
        else if (!wantLong && isLong)
            await SubmitOrder(Market(OrderSide.Sell, Position!.Qty), ct);
    }

    private OrderRequest Market(OrderSide side, decimal qty) => new()
    {
        IntentId = Guid.NewGuid().ToString("N"),
        Side = side, Type = OrderType.Market, Symbol = Symbol, Qty = qty,
    };
}
```

### Key rules

- **Causality:** in `OnCandleClose` the candle is the just-closed bar; orders fill on the **next** bar (market orders at `close ± slippage`). Never assume look-ahead.
- **Idempotency:** give every `OrderRequest` a unique `IntentId`. Duplicate intent IDs are rejected by paper/live sessions.
- **Determinism:** seed any randomness with a fixed value so backtests reproduce.
- **Speed:** `OnCandleClose`/`OnUniverseBar` must finish within **10 s** in live/paper (backtests are untimed). Offload heavy work (e.g. model training) to a background task.

---

## 6. Strategy parameters

Mark any public settable property with `[Param]` ([`ParamAttribute`](Strategy/Params/ParamAttribute.cs)). The registry reflects these into a JSON schema the frontend renders as a form; `StrategyParams.Apply` writes chosen values onto the instance **before** `DeclareSymbols()` and the run.

```csharp
public sealed class ParamAttribute : Attribute {
    public string  Label   { get; }              // display label (required)
    public string  Group   { get; init; } = "General"; // form section
    public double  Min, Max, Step;               // numeric bounds (NaN = unbounded)
    public string? Help;                          // tooltip
    public bool    IsSymbol;                       // render as a symbol picker
}
```

Supported property types: `int`, `double`/`decimal`, `bool` (checkbox), `enum` (dropdown), `string` (text or symbol picker). Example bounds: `[Param("Top Fraction", Group="Selection", Min=0.05, Max=0.5, Step=0.05)]`.

A strategy can also expose **views** over a nested config object (the momentum strategy does this — each `[Param]` is a getter/setter onto its `MomentumConfig`). This keeps the tunable surface flat for the UI while the strategy logic reads a single config struct.

---

## 7. Execution modes & sessions

`SessionMode` = `Backtest | Paper | Live`. The [`SessionManager`](Sessions/SessionManager.cs) creates the right session from a `DeploymentConfig`, choosing **portfolio vs single** generically off `strategy.DeclareSymbols().IsUniverse`:

| Strategy shape | Backtest | Paper | Live |
|---|---|---|---|
| Single-symbol | `BacktestSession.RunAsync` | `PaperSession` | `LiveSession` |
| Universe | `BacktestSession.RunPortfolioAsync` | `MultiAssetSession` | `MultiAssetSession` (armed) |

- **PaperSession** ([`PaperSession.cs`](Sessions/PaperSession.cs)) — streams live candles (websocket + REST fallback for robustness), fills against the `SimulatedOrderRouter`, all P&L synthetic. Preloads ~500 candles so indicator strategies act on the next bar.
- **LiveSession** ([`LiveSession.cs`](Sessions/LiveSession.cs)) — places real Kraken orders behind a `RiskGate`; starts **disarmed** (must be armed). Reconciles fills into a `LiveLedger` (see §11).
- **MultiAssetSession** ([`MultiAssetSession.cs`](Sessions/MultiAssetSession.cs)) — the paper/live counterpart for universe strategies; resolves the universe, REST-steps synchronized bars, dispatches `OnUniverseBar`, paper uses portfolio-mode `SimulatedOrderRouter`, live places per-symbol Kraken orders.

`DeploymentConfig` (the per-deployment settings):

```csharp
StrategyClass, Symbol, Interval, Mode
InitialQuoteBalance (10_000), InitialBaseBalance (0)
FeeFraction (0.001), SlippageFraction (0.0005)
MarginSettings Margin
RiskCaps? Caps              // live only
Dictionary<string,object?>? Parameters   // [Param] values
```

`TimeInterval`: `OneMinute, FiveMinute, FifteenMinute, ThirtyMinute, OneHour, FourHour, OneDay, OneWeek`.

---

## 8. Orders, brackets & routers

### OrderRequest

```csharp
IntentId (unique), Side (Buy/Sell), Type, Symbol, Qty
decimal? LimitPrice, StopPrice
decimal  Leverage = 1
decimal? TakeProfitPrice, StopLossPrice   // optional protective bracket on an entry
```

`OrderType`: `Market | Limit | StopLoss | TakeProfit`.

- **Market** fills immediately at the bar/mark price `± SlippageFraction`.
- **Limit / StopLoss / TakeProfit** become open conditional orders, triggered against candle highs/lows.
- **Brackets:** set `TakeProfitPrice`/`StopLossPrice` on an entry order and the engine attaches an OCO pair (one fills → the other cancels; flattening cancels both). Backtest/paper manage them internally; live sends Kraken conditional-close orders.

### Routers (`IOrderRouter`)

- **`SimulatedOrderRouter`** ([`SimulatedOrderRouter.cs`](Execution/SimulatedOrderRouter.cs)) — backtest & paper. In-memory book. Two modes:
  - *Single-symbol* (`PortfolioMode = false`): one `Position`, spot or margin.
  - *Portfolio* (`PortfolioMode = true`): per-symbol books keyed by symbol, one shared cash account, portfolio-wide margin. Driven by `OnPortfolioCandlesAsync` (sets marks, accrues funding, checks liquidation, fills conditionals) then `PlaceOrderAsync`.
- **`KrakenOrderRouter`** ([`KrakenOrderRouter.cs`](Execution/KrakenOrderRouter.cs)) — live. Signs REST requests (HMAC-SHA512), adds the `leverage` param for margin, and exposes `QueryFillsAsync` (parses Kraken `QueryOrders` into cumulative `ExchangeFill`s for reconciliation).

`OrderStatus`: `Pending, Open, PartiallyFilled, Filled, Rejected, Cancelled`.

---

## 9. Margin & leverage

`MarginSettings` (on `DeploymentConfig`/`BacktestConfig`, default = spot):

```csharp
decimal Leverage              = 1     // 1–10; 1 = spot
decimal LiquidationMarginLevel = 0.40 // liquidate when equity / posted margin hits this
decimal BorrowAnnualRate      = 0.20  // per-bar borrow/rollover cost on borrowed notional
decimal OpeningFeeFraction    = 0.0002// margin open fee
```

Behaviour in the simulator:

- **`Leverage == 1` (spot):** byte-for-byte the original engine — buys bounded by cash, sells bounded by inventory, **no shorting, no liquidation, no funding**.
- **`Leverage > 1` (margin):** positions (long **or short**) up to `equity × leverage`; a per-bar borrow fee accrues on borrowed notional; an opening fee is charged on increases; the position is **force-liquidated** at the maintenance price if the bar's adverse extreme reaches it.

Live orders inject the deployment's leverage into the Kraken `leverage` param. The `RiskGate`'s notional cap still bounds absolute exposure.

> Strategies opt into leverage by sizing into it (read `Leverage`). A spot deployment of a long/short strategy simply can't open shorts (they're rejected), so size long-only at 1×.

---

## 10. Backtesting

### Flow

```
POST /api/omnitrader/backtest/create  (CreateBacktestDto)
        → BacktestJobQueue.EnqueueAsync(config)   → row in backtest_jobs (status=Queued)
        → worker picks it up → RunSingleJobAsync:
              create strategy (registry) → apply params → DeclareSymbols()
              ├─ IsUniverse  → RunUniverseBacktestAsync → BacktestSession.RunPortfolioAsync
              └─ single      → BacktestSession.RunAsync
        → BacktestResult stored as JSON on the job row
```

The dispatch is **generic** — single vs multi is decided purely by `DeclareSymbols().IsUniverse` ([`BacktestJobQueue.RunSingleJobAsync`](Backtesting/BacktestJobQueue.cs)). A universe backtest resolves the universe via `BinanceUniverseProvider` and fetches each symbol's history via `MarketDataRouter`, exactly like the live `MultiAssetSession`.

### BacktestSession

[`BacktestSession`](Backtesting/BacktestSession.cs) is *the* engine — multi-asset-native, single-symbol = N=1. `RunAsync` dispatches `OnCandleClose`; `RunPortfolioAsync` dispatches `OnUniverseBar`. Both share `RunCoreAsync`, the same `SimulatedOrderRouter` (portfolio mode), and the same metrics. Pure in-memory; the job worker persists results.

### BacktestResult

[`BacktestResult`](Backtesting/BacktestResult.cs) contains returns (`TotalPnL`, `TotalPnLPercent`, `AnnualizedReturnPercent`), benchmark (`BuyAndHoldPnLPercent`, `AlphaVsBuyAndHoldPercent`, `BeatsBuyAndHold`), risk (`SharpeRatio`, `SortinoRatio`, `CalmarRatio`, `MaxDrawdownPercent`, `RecoveryFactor`), trade analytics (`WinRate`, `ProfitFactor`, `Expectancy`, `PayoffRatio`, streaks), exposure/duration, and the full `Trades`, `EquityCurve`, and `Candles`. Metrics are computed in [`BacktestMetrics`](Backtesting/BacktestMetrics.cs).

### Optional momentum validation suite

When a request carries advanced universe settings (`Config.Momentum`), the universe path runs [`MomentumBacktestRunner`](Backtesting/MomentumBacktestRunner.cs) instead — a point-in-time (survivorship-free) universe run plus a validation suite ([`Validation/`](Backtesting/Validation)): cost sensitivity, walk-forward + deflated Sharpe, survivorship audit, turnover/capacity. **This is an opt-in research add-on, not part of the engine's strategy dispatch.** A normal backtest of a universe strategy uses the generic path.

---

## 11. Live trading

Live deployments **start disarmed** (`DeploymentStatus.Paused`); no orders are placed until armed via `arm-live`. Pipeline in [`LiveSession`](Sessions/LiveSession.cs):

1. **Preload** history, run the strategy each closed bar.
2. **RiskGate** ([`RiskGate.cs`](Execution/RiskGate.cs)) checks every order against `RiskCaps`: `MaxPositionQuoteUsd`, `MaxDailyLossUsd`, `MaxOrdersPerHour`, `AllowedSymbols`. If realized daily loss breaches the cap the gate **trips**, the session flattens and disarms.
3. **Fill reconciliation:** each bar `ReconcileFillsAsync` polls `KrakenOrderRouter.QueryFillsAsync` for tracked orders, diffs cumulative executed qty/fee, and books increments into the **`LiveLedger`** ([`LiveLedger.cs`](Execution/LiveLedger.cs)) — which updates position, cash, and realized PnL (net of fees). Realized PnL feeds the RiskGate; fills are persisted (so the live chart shows trade markers).

`RiskCaps` defaults: `MaxPositionQuoteUsd = 100`, `MaxDailyLossUsd = 50`, `MaxOrdersPerHour = 30`, `AllowedSymbols = {deployment symbol}`.

> Kraken credentials must be configured (`status.KrakenConfigured`). Arming requires a confirm token equal to the deployment id.

---

## 12. Market data

[`MarketDataRouter`](MarketData/MarketDataRouter.cs):

- `GetHistoricalCandlesAsync(symbol, interval, count)` — **cache-first** (SQLite `candle_cache`), falls back to Binance REST then Kraken, and caches what it fetches.
- `StreamCandlesAsync(symbol, interval)` — multiplexed websocket stream (one producer per symbol/interval, fan-out to subscribers); every streamed candle is upserted into the cache.
- `GetLatestPriceAsync(symbol)` — live ticker (used for the chart's forming candle and live marks).

[`BinanceUniverseProvider`](MarketData/BinanceUniverseProvider.cs) resolves a dynamic universe (top-N by 24h quote volume) and, for the momentum research path, caches point-in-time daily universe data.

`OHLCCandle` is `(DateTime Timestamp, decimal Open, High, Low, Close, Volume)`. Timestamps are bar-open UTC.

---

## 13. Persistence

SQLite at `…/SavedData/OmniTrader/omnitrader.db` ([`OmniTraderDb`](Persistence/OmniTraderDb.cs), WAL mode, write-locked). Schema/migrations in [`OmniTraderSchema.cs`](Persistence/Schema/OmniTraderSchema.cs):

| Table | Purpose |
|---|---|
| `deployments` | live/paper session state + full `config_json` |
| `orders` | every order intent |
| `fills` | actual fills (FK → orders) |
| `equity_ticks` | per-bar equity snapshots (mark, quote, base, equity) |
| `backtest_jobs` | async backtest queue + `result_json` |
| `candle_cache` | OHLCV cache keyed (symbol, interval, ts) |
| `kraken_nonce` | monotonic nonce store |
| universe tables | point-in-time universe data for momentum research |

Configs are stored as JSON, so adding a field to `DeploymentConfig`/`BacktestConfig` needs **no migration**.

---

## 14. HTTP API

All under `/api/omnitrader/` (see [`OmniTraderRoutes.cs`](Api/OmniTraderRoutes.cs)). Reads are `Guest`; mutations are `Klives`.

| Method | Route | Purpose |
|---|---|---|
| GET | `status` | service health, deployment count, Kraken-configured flag |
| GET | `strategies` | discovered strategies + parameter schema + `RequiresUniverse` |
| GET | `deployments` | all deployments (equity, PnL%, mode, status, armed) |
| GET | `deployment?id=` | one deployment + recent orders + fills |
| GET | `deployment/equity?id=` | equity time series |
| GET | `deployment/chart?id=&limit=` | recent candles + buy/sell/exit markers |
| GET | `deployment/ticks?id=` | live price + forming candle (generic; any session type) |
| GET | `portfolio/equity?mode=` | summed equity over time across deployments of a mode (the "total account value") |
| POST | `deployment/create` | launch a paper/live deployment |
| POST | `deployment/arm-live?id=&confirm=` | arm a live deployment |
| POST | `deployment/pause` · `resume` · `kill` · `delete` | lifecycle |
| GET | `backtests` | recent backtest jobs |
| GET | `backtest?id=` | full backtest result |
| POST | `backtest/create` | enqueue a backtest |
| POST | `backtest/cancel?id=` | request cancellation |
| POST | `signals/flowsignal` | webhook for signal-driven strategies |

The frontend is `pages/schemery/omnitrader.vue` in the management website (charts via TradingView `lightweight-charts` + Chart.js).

---

## 15. Built-in strategies

| Strategy | Class | Type | Summary |
|---|---|---|---|
| IBS Mean Reversion | [`IBSMeanReversionStrategy`](Strategy/Strategies/IBSMeanReversionStrategy.cs) | single | Buy-the-dip in an uptrend using smoothed IBS; **triple-barrier exit** (ATR stop, revert-to-mean target, measured time barrier). |
| TCN Volatility Signal | [`TCNSignalStrategy`](Strategy/Strategies/TCNSignalStrategy.cs) | single | Self-training Temporal Convolutional Network (pure C#, see [`TcnNetwork.cs`](Strategy/TcnNetwork.cs)) → calibrated next-bar probability → deadband → EWMA vol-scaling → target weight clipped to ±`Leverage`. Auto-trains on first run and caches. |
| Flow Signal Trader | [`FlowSignalTraderStrategy`](Strategy/Strategies/FlowSignalTraderStrategy.cs) | single | Webhook-driven: trades on external signals posted to `/signals/flowsignal`. |
| Cross-Sectional Momentum | [`CrossSectionalMomentumStrategy`](Strategy/Strategies/CrossSectionalMomentumStrategy.cs) | universe | Weekly-rebalanced crypto momentum: point-in-time universe → risk-adjusted momentum ranking → top/bottom selection → BTC regime filter → inverse-vol sizing to a target portfolio vol → drawdown killswitch. Helpers in [`Strategy/Momentum/`](Strategy/Momentum). |

---

## 16. Indicators

[`Indicators`](Strategy/Indicators.cs) (all `endIndex`-based, causal):

```csharp
decimal SMA(IList<OHLCCandle> candles, int period, int endIndex)
decimal EMA(IList<OHLCCandle> candles, int period, int endIndex)   // windowed EMA seeded at window start
decimal RSI(IList<OHLCCandle> candles, int period, int endIndex)
decimal ATR(IList<OHLCCandle> candles, int period, int endIndex)
decimal IBS(OHLCCandle candle)                                     // (close-low)/(high-low)
decimal IBSSmoothed(IList<OHLCCandle> candles, int endIndex, int smoothing = 2)
```

Compute anything richer inline (MACD, Bollinger %B, realized vol, etc. — see the TCN strategy's feature builder for examples).

---

## 17. Testing

Tests live in `Omnipotent.Tests/OmniTrader/`. The test project targets **net9.0** but the machine has only the **.NET 10** runtime, so run with roll-forward:

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Omnipotent.Tests/Omnipotent.Tests.csproj \
    --filter "FullyQualifiedName~OmniTrader"
```

Existing coverage includes the TCN network (learnability/serialization/determinism), margin (leverage sizing, shorting, liquidation, funding, spot-unchanged), live reconciliation (ledger math + Kraken fill parsing), the IBS triple-barrier, and the generic universe backtest. Strategies are best tested by driving a `BacktestSession`/`RunPortfolioAsync` with synthetic candles.

---

## 18. Gotchas & conventions

- **Parameterless constructor required** — the registry uses `Activator.CreateInstance`.
- **No look-ahead** — `OnCandleClose` gets the just-closed bar; orders fill next bar.
- **Unique `IntentId`** per order; duplicates are rejected (idempotency).
- **10-second** callback budget in live/paper (backtests untimed) — background heavy work.
- **Spot vs margin** — at `Leverage == 1` shorting is impossible (sells beyond inventory are rejected). Set leverage > 1 to short.
- **Universe vs single is generic** — never branch on a concrete strategy type in the engine; declare it via `DeclareSymbols()`/`RequiresUniverse`.
- **Backtest leakage** — the TCN strategy trains only on a warmup prefix and never loads a disk cache in backtest mode; follow that pattern for any learned model.
- **Configs are JSON-persisted** — new config fields need no DB migration; old rows deserialize with defaults.
- **Determinism** — seed RNGs; backtests must reproduce.

---

# The firm layer

## 19. Firm layer overview

Everything from here on is the *trading operating system* that sits on top of the strategy engine. It
is organised by **operating function**, not by broker: a venue is an execution endpoint behind an
adapter, and every higher-level component works on normalized internal records.

```
     Markets ── Strategies ── Research          (what to trade, and does it work)
        │            │            │
        ▼            ▼            ▼
   ┌──────────────── TradeProposal ────────────────┐
   │  instrument · venue · environment · authority │
   └───────────────────┬───────────────────────────┘
                       ▼
                 ╔═══════════╗   7 layers, hard + soft controls,
                 ║ RiskEngine║   evaluates the portfolio AFTER the trade
                 ╚═════╤═════╝
              RiskDecision (persisted, rule-level)
                       ▼
                ┌────────────┐   idempotency key · state machine
                │OrderService│   approvals · Unknown never retried
                └─────┬──────┘
                      ▼
        IVenueAdapter: Kraken (spot) · IG (CFD) · Internal (paper)
                      │ broker truth
                      ▼
        FirmLedger ⇄ ReconciliationService ⇄ Portfolio · Journal · Performance
                      │
                 AlertService · HealthMonitor · AuditRepository
```

| Folder | What's in it |
|---|---|
| [`Venues/`](Venues) | `IVenueAdapter`, `VenueRegistry`, `VenueContracts` (capabilities, environments, health), `KrakenVenueAdapter`, `IGVenueAdapter` + `IGRestClient`, `InternalPaperVenueAdapter`. |
| [`Instruments/`](Instruments) | `Instrument`/`VenueMapping` (canonical identity + dealing rules), `InstrumentMaster` (folding, resolution, freshness). |
| [`Risk/`](Risk) | `RiskContracts` (proposal, decision, limits), `RiskEngine` (the 7 layers), `EmergencyControls` (safe mode + kill switches). |
| [`OrderFlow/`](OrderFlow) | `FirmOrder` + `OrderStateMachine`, `OrderService` (the only path to a venue). |
| [`Ledger/`](Ledger) | `LedgerContracts`, `FirmLedger` (immutable entries), `ReconciliationService` (classified breaks). |
| [`Portfolio/`](Portfolio) | `PortfolioService` — firm view, exposure, FX valuation, risk state. |
| [`Journal/`](Journal) | `JournalService` + `JournalRecord` — the automatic decision record. |
| [`Research/`](Research) | `ExperimentRegistry` — experiments, strategy versions, the promotion gate. |
| [`Performance/`](Performance) | `PerformanceService` — attribution, execution quality, behaviour analysis. |
| [`Analytics/`](Analytics) | `MarketAnalytics` (regime/momentum/breakout/alignment/liquidity/breadth), `WatchlistService`. |
| [`Ops/`](Ops) | `AlertService` (severity-routed, deduped, Discord), `HealthMonitor`, `OpsContracts`. |
| [`FirmContext.cs`](FirmContext.cs) | Wires it all together and owns the background reconciliation/health/journal loops. |
| [`Api/FirmRoutes.cs`](Api/FirmRoutes.cs) | The `/api/omnitrader/firm/*` surface. |

`FirmContext` starts **after** the engine's stores and market data exist and **before** deployments
are recovered — a recovered deployment must find risk and reconciliation already in place.

Schema migration **v3** ([`OmniTraderSchema.cs`](Persistence/Schema/OmniTraderSchema.cs)) adds the
firm tables: `firm_accounts`, `instruments`, `trade_proposals`, `risk_decisions`, `firm_orders`,
`ledger_entries`, `reconciliation_runs`/`_breaks`, `journal_records`, `alerts`, `audit_events`,
`experiments`, `strategy_versions`, `watchlists`, `firm_settings`, `account_snapshots`. Rich records
go in a `json` column with the query-relevant fields lifted into indexed columns — the same convention
the engine's `config_json` already uses, so adding a contract field needs no migration.

---

## 20. Venues and environments

A venue is an external counterparty behind an adapter. The internal paper simulator is itself a venue
(`VenueId.Internal`) so paper fills exercise the same order, ledger and reconciliation code path as
real ones — and can never be confused with broker truth.

| Venue | Product | Exposure | Shorting | Leverage | Demo |
|---|---|---|---|---|---|
| **Kraken** | Crypto spot | `Inventory` (owned) | no | no (1x) | no — use internal paper |
| **IG** | CFDs | `Derivative` (notional) | yes | yes, per-instrument margin factor | yes, separate adapter |
| **Internal** | Paper simulator | `Inventory` | yes | yes | n/a |

**`VenueCapabilities`** is the contract everything else reads. Unsupported features are not simulated
— they are disabled with a stated reason in `Limitations`, which the order ticket renders next to the
disabled control and the risk engine quotes verbatim when it blocks an order.

### Environments

`TradingEnvironment` = `Historical | Paper | Demo | Live`. The registry keys adapters by
**(venue, environment)**, so IG demo and IG live are genuinely distinct entries with separate
credentials, base URLs, accounts, ledgers and audit scope. `ResolveUnambiguous` deliberately returns
`null` when both are registered — the caller must be explicit, which is what stops a demo instruction
reaching a live account.

`ExecutionAuthority` is the progressive-authority ladder:
`Observe → Paper → Demo → ApprovalRequired → Automated`. Live accounts are created at `Observe`;
nothing gains real-money authority implicitly.

### IG credentials

Read from Omni settings, per environment, and never returned by the API:

```
OmniTrader.IG.Demo.ApiKey / .Username / .Password
OmniTrader.IG.Live.ApiKey / .Username / .Password
```

`IGRestClient` owns the session (`CST` + `X-SECURITY-TOKEN`), refreshes it once on a 401, and captures
IG's historical-price allowance so quota pressure is visible on the Systems page. `IGVenueAdapter`
resolves **every** submission through `/confirms/{dealReference}` — a deal reference is not an
accepted deal.

> **Known limitation, stated rather than faked:** IG's Lightstreamer streaming is not implemented.
> Prices and account state are polled over REST. The `ig-lightstreamer` channel reports itself as down
> with that reason, and `Capabilities.SupportsStreamingPrices` is `false` with an explanation —
> nothing pretends the feed is live.

---

## 21. Instrument master

Every instrument gets an internal identity (`crypto:BTC/USD`, `index:UK100/GBP`) independent of any
broker symbol. Strategies and analytics reference only that; venue symbols live inside adapters.

```csharp
Instrument {
    Id, DisplayName, AssetClass, BaseAsset, QuoteCurrency, ContractMultiplier, Exposure
    List<VenueMapping> Venues   // per venue: symbol, tick size, qty step, min/max size,
                                //            margin factor, tradeable, status, hours
    FreshnessThreshold          // how old a price may be before automated actions are blocked
}
```

`InstrumentMaster.RefreshFromVenuesAsync()` folds each venue's directory into these records.
Stablecoin quotes collapse onto `USD` for *identity* while the venue mapping keeps the real quote
asset, so orders are still sized in the right currency. `VenueMapping.RoundQuantity` always rounds
**down**, so rounding can never create size the caller did not ask for.

**Freshness is tracked centrally.** `NoteDataUpdate` records when data was last observed;
`GetFreshness` returns a verdict the risk engine's data-integrity layer blocks on and the UI
underlines. An instrument never seen in this process is `Stale = true`, not silently fresh.

---

## 22. Risk service

Mandatory. Every proposal — strategy or manual, paper or live — passes through `RiskEngine.Evaluate`,
and `OrderService` refuses to submit anything without an approved decision id.

The engine is **pure**: proposal + instrument + capabilities + freshness + portfolio state +
operational state produce a decision. That makes every rule unit-testable and leaves the engine unable
to rewrite strategy logic.

### The seven layers

| Layer | Checks |
|---|---|
| **DataIntegrity** | instrument known, decision-data age, feed freshness, usable price, proposal not expired |
| **OrderValidity** | positive qty, order type supported, limit/stop present, protection supported, stop and target on the correct side of entry, venue mapping exists, market tradeable, min/max size, quantity precision |
| **TradeRisk** | hard and soft notional caps, requested loss implied by the stop, missing protection (soft), limit distance from mark |
| **StrategyRisk** | authority permits execution, strategy daily loss, concurrent position count |
| **VenueRisk** | spot sell within free inventory (quoting the venue's own "cannot short" reason), spot buy within available cash, CFD margin within available funds |
| **PortfolioRisk** | gross, net, per-instrument concentration, per-venue exposure, firm daily loss, drawdown — all measured on the **portfolio that would exist after** the trade |
| **OperationalRisk** | safe mode, unknown orders outstanding, unreconciled breaks, repeated rejections, venue order path healthy |

Every layer emits a rule result — passes included — so a rejection is explainable and a decision record
proves which controls ran.

**Verdicts:** `Approved`, `RequiresApproval` (a soft control fired, or the authority is
`ApprovalRequired`), `Rejected` (any hard control failed).

### Emergency controls

`EmergencyControls` owns **safe mode** (firm-wide stop on new automated proposals) and scoped
**kill switches** (`Firm | Venue | Account | Strategy`). `EvaluateAutomaticTriggers` trips safe mode on
daily loss, drawdown, unknown orders, unresolved breaks or repeated rejections.

It deliberately **does not close positions**. Killing automation and unwinding a book are different
decisions with different blast radii; exposure reduction is a separate, preview-then-confirm action
(section 28).

---

## 23. Order flow

```
Proposed → AwaitingApproval → RiskApproved → Submitting → Acknowledged → Working
         ↘ RiskRejected                    ↘ Rejected   ↘ PartiallyFilled → Filled
                                           ↘ Unknown ──(reconciliation only)──┘
```

`OrderStateMachine` enumerates the legal transitions; anything else is refused and logged. Three
invariants hold the whole thing together:

1. **No order bypasses risk.** `Proposed → Submitting` is not a legal transition. Submission requires
   an approved `RiskDecisionId`.
2. **Idempotency is structural.** `OrderService.BuildClientReference` hashes the proposal's identity
   (not a clock or an RNG), so resubmitting the same proposal yields the same key — and
   `firm_orders.client_reference` is `UNIQUE`, making a duplicate broker order impossible rather than
   merely unlikely. The key is 24 characters of `[A-Za-z0-9]`, which fits IG's 30-character
   deal-reference rule and doubles as Kraken's `cl_ord_id`.
3. **`Unknown` is never retried.** A submission whose outcome cannot be proven parks in `Unknown`,
   trips safe mode, raises a Critical alert, and can only leave via reconciliation proving what the
   broker actually did. Absence of a confirmation is *not* proof of rejection.

Fills are booked to the ledger **only as increments** (`newlyFilled = venueFilled - alreadyBooked`), so
replaying a reconciliation pass cannot double-count.

---

## 24. Ledger and reconciliation

`FirmLedger` appends **immutable** `LedgerEntry` records — cash, inventory, exposure, cost, realized
P&L, adjustment. A mistake is corrected by posting an `Adjustment` that references the original; the
original is never overwritten, so the audit trail survives every correction.

Each fill books up to four entries: the cash leg (spot only — a CFD deal posts margin, not cash), the
quantity change, the cost with its `CostKind` **and `CostQuality`** (`Observed | Estimated |
Unavailable`), and realized P&L on whatever portion closed. Performance reports surface that quality
split, because an estimated cost presented as observed is a lie about the P&L.

`RehydrateAsync` rebuilds the in-memory book from the entry log at startup, so a restart never invents
or loses a position.

### Reconciliation

Runs at **startup, after reconnect, after ambiguous outcomes, after fills, and every 5 minutes**.
Orders are reconciled before positions, because an unbooked fill explains most position differences
and resolving it first avoids raising breaks that are pure timing.

Every difference becomes a classified `ReconciliationBreak`:

| Classification | Meaning | Material? |
|---|---|---|
| `Timing` | a real event not yet booked — expected to clear | no |
| `MissingEvent` | the venue reports something we never received | yes |
| `MappingError` | our symbol or account mapping is wrong | yes |
| `ExternalManualActivity` | someone traded this account outside the platform | yes |
| `Unexplained` | needs a human | yes |

Material breaks block new automated exposure and trip safe mode. Resolving one is a recorded human
judgement, not an edit to the ledger.

---

## 25. Portfolio and valuation

`PortfolioService.BuildAsync()` produces the firm view under two rules:

- **Native values are never overwritten.** Conversion into the reporting currency is recorded
  alongside the native amount with its rate and source (`Valuation`).
- **CFD notional is never added to owned inventory as though both were assets.** `TotalValue` is
  `Cash + InventoryValue + DerivativeEquity` (the broker's own equity figure); `DerivativeNotional` is
  reported separately as exposure.

`BuildRiskStateAsync()` compresses the same picture into what the risk engine measures against —
including free inventory per asset (quantity minus reserved) and available funds. An unreachable venue
contributes no free inventory, which fails safe.

FX is derived from the venues' own crypto pairs (a BTC cross) rather than requiring another data
provider; an unresolvable rate returns 1 and records its source as `identity` rather than silently
distorting the total.

---

## 26. Journal, research and performance

**Journal** — a `JournalRecord` is written automatically when an order reaches a terminal state, and
carries the whole decision trail: signal time, data snapshot, risk verdict and rule failures, approval
and its delay, intended-versus-actual size and price, slippage, protection, fees, realized P&L,
maximum favourable and adverse excursion (computed from candles, so a restart cannot lose it), holding
period, and every manual intervention with the state either side of it.

**Research** — `ExperimentRegistry` records a hypothesis *before* the test, attaches backtest jobs and
folds their headline metrics in, and owns immutable `StrategyVersionRecord`s.

**The promotion gate** is a gate, not a suggestion. `AssessPromotionAsync` returns named requirements
and says exactly which are unmet:

- the version exists, and the request moves **one rung** on the ladder;
- at least one completed experiment documents the strategy;
- for `Demo` and above: at least 30 trades, positive Sharpe, drawdown under 50%;
- for `Automated`: the version must already have run under `ApprovalRequired`.

**Performance** — attribution by firm, venue, strategy, instrument and context tag, plus execution
quality (fill and rejection rates, median and worst slippage, latency, rejection reasons) and a
behaviour analysis comparing trades that were intervened on against those left alone. All from the
same ledger and journal the accounting uses, so a performance number and a balance can never disagree.

---

## 27. Operations, alerts and health

`AlertService` deduplicates on the **condition**, not the occurrence — a flapping feed produces one
open alert with a rising `OccurrenceCount`. Severity routes the delivery:

| Severity | Examples | Delivery |
|---|---|---|
| **Critical** | unknown live order, material reconciliation mismatch, hard risk breach, safe mode engaged | store + Discord, **requires acknowledgement** |
| **High** | stale data, venue channel down, execution authority changed | store + Discord |
| **Medium** | order awaiting approval, broker rejection | store |
| **Informational** | routine events | store |

Acknowledging records who looked at it. It does **not** close the alert — only the code that fixes the
underlying state resolves it (`ResolveByDedupeAsync`).

`HealthMonitor` reports six areas independently (connections, sessions, market data, order flow,
reconciliation, controls) and answers one question: is the firm allowed to trade, and if not, why?
Read-only analytics may be degraded while the order path is perfectly healthy — stale data blocks the
*affected instruments* (enforced per-order by the risk engine) rather than the whole firm.

Background loops in `FirmContext`: reconciliation plus automatic risk triggers every 5 minutes, a
health sweep every minute, and the journal writer every 2 minutes.

---

## 28. Firm HTTP API

All under `/api/omnitrader/firm/`. Reads are `Guest`; mutations are `Klives`. The engine's original
`/api/omnitrader/*` routes (section 14) are unchanged and still serve strategies, deployments and
backtests.

| Method | Route | Purpose |
|---|---|---|
| GET | `overview` | command centre: value, exposure, health, exceptions, alerts, venues |
| GET | `environments` | accounts, authorities and every venue's capability matrix |
| GET | `markets`, `markets/watchlists` | evaluated market rows plus breadth; watchlists |
| POST | `markets/watchlist/save`, `markets/watchlist/delete` | manage watchlists |
| GET | `instruments` | canonical instruments, venue mappings, freshness |
| POST | `instruments/refresh` | fold venue directories into the master |
| GET | `portfolio`, `portfolio/value-series`, `ledger` | firm view, value history, ledger entries |
| GET | `reconciliation` | open breaks and recent runs |
| POST | `reconciliation/run`, `reconciliation/resolve` | reconcile now; explain a break |
| GET | `risk` | limits, utilisation, portfolio and operational state, recent decisions |
| POST | `risk/limits`, `risk/safe-mode`, `risk/killswitch` | change the risk budget and controls |
| POST | `risk/reduce/preview` then `risk/reduce/execute` | **two-step** exposure reduction |
| GET | `orders`, `order`, `ticket` | blotter; order with decision and lifecycle; capability-driven ticket |
| POST | `order/propose`, `order/approve`, `order/reject`, `order/cancel` | the order lifecycle |
| POST | `orders/reconcile` | resolve outstanding and unknown orders |
| GET | `experiments`, `strategy-versions`, `promotion/assess` | research and the promotion gate |
| POST | `experiment/create`, `experiment/attach`, `experiment/update`, `strategy-version/create`, `promotion/promote` | research mutations |
| GET | `performance` | attribution, execution quality, behaviour |
| GET | `journal`, `journal/record` | trade records |
| POST | `journal/annotate`, `journal/intervention` | review workflow |
| GET | `systems`, `alerts` | health, venue channels, freshness, audit; alerts |
| POST | `alert/acknowledge`, `alert/resolve`, `venues/connect`, `account/authority` | operations |

**Confirmation tokens.** `risk/reduce/execute` requires a token derived from the exact preview the
operator saw (`REDUCE-{positions}-{notional}`); if the book moves before they confirm, the token stops
matching and they are sent back to the preview. Granting live authority to a live account requires
`confirm=<accountId>`.

### Query, counts and comparison

Three routes answer more than "here are some rows", because a dashboard that cannot state its scope
cannot be trusted with it.

**`orders`** and **`journal`** return an envelope rather than a bare array:

```jsonc
{
  "Rows":     [ /* the page you asked for */ ],
  "Filtered": 42,      // matched the filters
  "Total":    417,     // in the scanned recent set
  "Offset":   0,
  "StateCounts": { "Filled": 380, "Unknown": 2 },   // orders: facet counts for the filter chips
  "ReviewCounts": { "Unreviewed": 11 }              // journal: the same, per review state
}
```

Both accept `q` (substring over the identifying fields), `venue`, `environment`, `from`, `limit` and
`offset`; `orders` additionally takes `state` (comma-separated) and `strategy`, and `journal` takes
`review`, `strategy`, `tag` and `outcome` (`wins` · `losses` · `open` · `intervened`). Filtering runs
on the server so the counts describe the whole set, not the page.

**`performance`** carries its own baseline. Alongside the current window it returns `Previous` and
`PreviousExecution` over the immediately preceding window of equal length, `PreviousFromUtc`/`ToUtc`,
and `HasBaseline` — false when there is nothing to compare against, so the UI says "no baseline"
instead of presenting a comparison against zero as growth. It also returns `Daily` (one point per
calendar day including quiet ones, with the running total), `PnLDistribution` and
`SlippageDistribution` (equal-width histograms, empty rather than degenerate when there is no spread).

**`overview`** returns `Trend`: the firm value series over `trendDays` (default 30, downsampled to
≤120 points), `Change24h`, `ChangePercent24h`, `PeakValue` and `TroughValue`. `Points` is empty rather
than fabricated when no snapshot exists.

**`markets`** rows carry `Spark`, ~48 downsampled closes. The downsampler keeps each bucket's extreme
in the direction the bucket moved, so a spike survives rather than being averaged flat, and the final
point is the exact latest price.

### Any symbol, not just the instrument master

Three routes work on *symbols*, so anything listed can be charted and quoted whether or not the firm
has ever traded it:

| Route | Returns |
|---|---|
| GET `candles` `?symbol&interval&count` | OHLCV bars, plus `Streaming` — whether "live" means a push feed or a poll |
| GET `quote` `?symbol` | Price, previous close, change, currency, exchange and `MarketState` |
| GET `search` `?q` | Instrument-master matches first, then every other listed symbol |

Crypto is served by Binance/Kraken; **everything else — shares, ETFs, indices, FX, commodities — is
served by `YahooMarketDataProvider`**, which is keyless and covers the listings IG and Trading 212
deal in. `MarketDataRouter.UsesEquityFeed` picks the feed from an explicit `AssetClass` when the
caller has one and from the symbol's own shape when it does not (`BTCUSDT` → exchange; `VOD.L`,
`^FTSE`, `GBPUSD=X`, `AAPL` → equities). Equity "streaming" is polling at the bar interval and says
so — only crypto is a real push stream.

**Enums serialise as names, not ordinals**, via a `StringEnumConverter` on the shared settings. A UI
that renders "Classification 3" has told the operator nothing.

---

## 30. Real money, simulated money, and holdings you already own

Two rules learned on the first live run.

**Only `Live` accounts hold real money.** `PortfolioService.IsRealMoney` is the single definition and
`FirmPortfolioView` carries two totals, `Real` and `Simulated`. Every headline figure (`TotalValue`,
`Cash`, `InventoryValue`, `DerivativeEquity`, `GrossExposure`, `RealizedPnLToday`) is the *real* one;
the paper simulator and broker demo accounts are reported in their own block and never added in.
`HasRealAccounts` is false when nothing live is connected, so the UI can say so instead of presenting
£0 as a loss. Daily P&L is filtered by environment in SQL.

**A holding the platform did not trade is an asset, not a discrepancy.** When reconciliation finds a
venue position with no internal counterpart — an account that predates the platform, a manual top-up,
an airdrop — it calls `FirmLedger.AdoptExternalHoldingAsync`: the broker's quantity becomes truth,
the entry is marked `ExternalManual`, and the cost basis is recorded as the current mark because the
real one is unknowable from here (which keeps unrealized P&L at zero rather than inventing a profit).
Fragments below `ReconciliationService.DustThreshold` are left alone entirely. Raising a break per
pre-existing coin produced a screen of red that said only "you own things", and tripped safe mode for
the crime of holding assets.

Breaks are still raised for real disagreements — a position we *do* track that does not match, an
unprovable order, a cash difference — and they now **deduplicate on the condition**
(`{venue}:{environment}:{kind}:{subject}`), so one unresolved problem is one row however many times
the sweep re-detects it. `CollapseDuplicateBreaksAsync` runs at startup to fold away repeats left by
earlier versions.

### Venues

| Venue | Exposure | Environments | Notes |
|---|---|---|---|
| Kraken | Inventory (spot crypto) | Live | Reuses the engine's order router |
| IG | Derivative (CFD) | Demo · Live | No Lightstreamer — prices polled over REST |
| **Trading 212** | **Inventory (shares, ETFs)** | **Demo · Live** | No historical-bar endpoint, so charts come from the equities feed; no client-reference field, so an ambiguous submission is reported `Unknown` rather than retried |
| Internal | Inventory (simulated) | Paper | Never counted as value |

### Credentials: each broker issues them differently

`FirmContext.ResolveCredentialAsync` reads the environment-specific setting first and falls back to a
shared one. This exists because the two brokers genuinely differ, and forcing one shape on both left
an operator pasting the same key into two settings — or, worse, getting silence when they didn't.

| Setting | Used by | Required? |
|---|---|---|
| `OmniTrader.Kraken.ApiKey` · `.ApiSecret` | Kraken live | Yes. Exclude withdrawal permission. |
| `OmniTrader.IG.ApiKey` | **Both IG environments** | Yes — IG allows only **one API key per account** |
| `OmniTrader.IG.Username` · `.Password` | IG live | Yes for live |
| `OmniTrader.IG.Demo.Username` · `.Password` | IG demo | Yes for demo — IG makes you create *separate demo details*: log in live, switch to the demo account, then My Account → Settings → Web API |
| `OmniTrader.IG.Demo.ApiKey` · `.Live.ApiKey` | Override | Optional; only if you somehow hold two keys |
| `OmniTrader.Trading212.ApiKey` | T212 live (and demo, if nothing more specific) | Shared convenience |
| `OmniTrader.Trading212.Demo.ApiKey` · `.Live.ApiKey` | Per environment | **A T212 key only works in the environment it was generated in** — switch the app to Practice mode *before* generating the demo key |

The Systems page renders this table live, including which setting actually supplied each connection's
key, so a shared key doing the work of two is visible rather than inferred.

---

## 31. The UI

`pages/omnitrader/` in the management website — ten pages over a shared component library in
`components/OmniTrader/`, `composables/useOmniTrader.ts` and `assets/scss/omnitrader-os.scss`.

**Components.** Nuxt registers these path-prefixed, so a page uses `<OmniTraderShell>`, not `<Shell>`:

| Component | Responsibility |
|---|---|
| `Shell` | environment band, scope selector, freshness, grouped nav with exception badges, blocker banners, density toggle, ⌘K palette |
| `Card` | card frame with a question subtitle, local controls, and the loading / empty / filtered / stale / partial / error states |
| `Kpi` | label, value, unit, comparison **with its baseline named**, target, spark, drill-down |
| `DataTable` | sticky header, sort, search, column visibility, pinned first column, paging, filtered/total counts, keyboard row access |
| `LineChart` | one y-axis, crosshair + tooltip, keyboard readout, direct-labelled last value, axis-truncation disclosure, table view |
| `BarList` | sorted horizontal bars, diverging around zero, top-N with the tail folded into "Other" |
| `CandleChart` | candlesticks + volume via `lightweight-charts`, with a UTC OHLC readout under the crosshair |
| `Sparkline`, `Meter`, `RuleList`, `Drawer`, `StateBlock` | trend shape; bullet meter with its threshold; the rule-level decision record; right-side inspection; typed non-success states |

| Page | Primary decision |
|---|---|
| **Command Centre** (`/omnitrader`) | assess the operation and find what needs action |
| **Instrument** (`/omnitrader/instrument?symbol=`) | study one symbol: candlesticks, live quote, analytics, position |
| **Markets** | discover and evaluate market conditions |
| **Strategies** | control the strategy lifecycle and authority |
| **Portfolio** | understand aggregate exposure and reconciliation |
| **Risk** | prevent or reduce unacceptable exposure |
| **Execution** | manage broker interaction and investigate fills |
| **Research** | validate strategy logic and build promotion evidence |
| **Performance** | measure outcomes and detect deterioration |
| **Journal** | review the complete decision record |
| **Systems** | operate and troubleshoot the platform |

Six design rules run through all of it:

- **Environment is never ambiguous.** A coloured band sits above every page (live red, demo amber,
  paper blue, historical grey), the selector in the header names it, and the same colours label every
  order, position and venue row. `All environments` is a real scope with its own striped band and an
  `includes live` chip — never the absence of a choice.
- **Degraded state is visible, not implied.** Blockers are banner-level on *every* page, not just Risk.
  Stale prices are underlined with their reason. Unsupported venue controls are disabled with the
  venue's own explanation beside them. `Unknown` orders get the loudest treatment in the UI.
- **Every number carries its context** — unit, window, baseline and freshness. A comparison is only
  rendered when a real baseline exists, and a direction is only coloured when the caller has said
  which way is good (rising slippage is not a win).
- **Unknown never renders as zero, and never renders as green.** Empty, filtered-empty, stale, partial,
  failed and no-permission are six different states with six different treatments.
- **Exceptions are loud; normality is quiet.** Colour is reserved for status, selection, thresholds and
  anomalies; the default card is neutral.
- **Progressive disclosure, in order.** Value → hover detail → drawer → page. Inspecting a row never
  costs you the table's scroll, sort, filters or selection.

Charts follow the shared data-viz rules: one y-axis (never a dual axis), position and length before
area or angle, a fixed categorical palette assigned in order and never re-assigned by rank, gridlines
quieter than the data, a crosshair with exact values, keyboard readout via arrow keys, and a table
view on every chart. The eight categorical slots are validated for the dark surface — inside the
lightness band, above the chroma floor, ≥3:1 against the surface, worst adjacent CVD ΔE 8.4.

Filters live in the URL, so a view can be linked, bookmarked and returned to. Density (standard or
compact) and the environment scope persist per operator.

`/schemery/omnitrader` redirects here; the dashboard and schemes cards point at `/omnitrader`.

### Firm-layer testing

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Omnipotent.Tests/Omnipotent.Tests.csproj \
    --filter "FullyQualifiedName~RiskEngineTests|FullyQualifiedName~OrderFlowTests|FullyQualifiedName~FirmLayerTests"
```

`RiskEngineTests` asserts the blocking behaviour rule by rule; `OrderFlowTests` covers the state
machine and the idempotency-key invariants; `FirmLayerTests` covers emergency controls, break
classification, the exposure model, venue capabilities and the shared analytics.

### Firm-layer gotchas

- **Never write to a venue outside `OrderService`.** It is the only component allowed to submit, and
  the only one that assigns idempotency keys.
- **Never resolve an `Unknown` order by resubmitting.** Query the broker by client reference.
- **Book fills as increments, not totals** — `ReconcileOrderAsync` already diffs against
  `FilledQuantity`.
- **Never correct a ledger entry in place.** Post an `Adjustment` that references it.
- **A capability you cannot support gets a `Limitations` entry**, not a silent simulation. The UI and
  the risk engine both surface that text to the operator.
- **Live authority is never implicit.** New live accounts start at `Observe`, and raising them
  requires a typed confirmation.
