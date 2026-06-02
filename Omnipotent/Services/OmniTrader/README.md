# OmniTrader

OmniTrader is the algorithmic-trading service inside Omnipotent. It is a **modular strategy engine** that runs the *same* strategy code across three execution modes — **backtest**, **paper**, and **live** (real money on Kraken) — over **any symbol(s)**, single-asset or multi-asset.

The guiding principle: **the engine knows nothing about any specific strategy.** A strategy declares what it trades and reacts to bars; the engine handles data, execution, accounting, risk, margin, and persistence generically. There are no `if (strategy is X)` branches in the core.

---

## Table of contents

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
