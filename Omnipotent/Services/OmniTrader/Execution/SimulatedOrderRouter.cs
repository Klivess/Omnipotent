using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Execution
{
    /// <summary>
    /// In-memory order router that fills orders against candle highs/lows.
    /// Used by both BacktestSession and PaperSession. Maintains its own balance/position state
    /// (the session reads back via the OnFill callback to update its own ledger).
    ///
    /// Spot vs margin: when <see cref="State.Leverage"/> is 1 the router behaves exactly like the
    /// original spot engine (buys bounded by quote balance, sells bounded by base balance, no
    /// shorting, no liquidation). When leverage &gt; 1, margin mode is enabled: positions (long or
    /// short) may be opened up to equity × leverage, a per-bar borrow/rollover fee accrues on the
    /// borrowed notional, and the position is force-liquidated if the margin level falls to
    /// <see cref="State.LiquidationMarginLevel"/>.
    /// </summary>
    public sealed class SimulatedOrderRouter : IOrderRouter
    {
        private const decimal YearSeconds = 365m * 24m * 3600m;

        public sealed class State
        {
            public decimal QuoteBalance;
            public decimal BaseBalance;
            public decimal Fees;
            public Position? Position;
            public decimal FeeFraction;
            public decimal SlippageFraction;

            // ── Margin (only consulted when Leverage > 1) ───────────────────────────
            public decimal Leverage = 1m;
            public decimal LiquidationMarginLevel = 0.40m;
            public decimal BorrowAnnualRate = 0.20m;
            public decimal OpeningFeeFraction = 0.0002m;
            public int SecondsPerBar = 3600;
            public bool Liquidated;

            public List<OrderIntent> OpenOrders { get; } = new();

            public decimal EffectiveLeverage => Math.Clamp(Leverage, 1m, 10m);
            public bool MarginEnabled => EffectiveLeverage > 1m;

            // ── Portfolio mode (multi-symbol) ───────────────────────────────────────
            // When false, the single Position/BaseBalance fields above are used exactly as the
            // original single-symbol engine. When true, per-symbol books below are used instead and
            // margin/liquidation are evaluated portfolio-wide (gross notional vs equity × leverage).
            public bool PortfolioMode;
            public Dictionary<string, decimal> BaseBalances { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, Position> Positions { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, decimal> Marks { get; } = new(StringComparer.OrdinalIgnoreCase);
            public DateTime CurrentBarTs;

            public decimal GetBaseBalance(string symbol) => BaseBalances.TryGetValue(symbol, out var b) ? b : 0m;
            public decimal Mark(string symbol) => Marks.TryGetValue(symbol, out var m) ? m : 0m;

            /// <summary>Cash + marked value of every position.</summary>
            public decimal PortfolioEquity()
            {
                decimal eq = QuoteBalance;
                foreach (var kv in BaseBalances) eq += kv.Value * Mark(kv.Key);
                return eq;
            }

            /// <summary>Σ |position notional| across all symbols at current marks.</summary>
            public decimal GrossNotional()
            {
                decimal g = 0m;
                foreach (var kv in BaseBalances) g += Math.Abs(kv.Value) * Mark(kv.Key);
                return g;
            }

            /// <summary>Net signed quantity per symbol, flat symbols omitted.</summary>
            public IReadOnlyDictionary<string, decimal> NonZeroPositions()
            {
                var d = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in BaseBalances) if (kv.Value != 0m) d[kv.Key] = kv.Value;
                return d;
            }
        }

        private readonly State state;
        private readonly Func<FillEvent, Task> onFillAsync;
        private readonly Action<string>? log;
        private OHLCCandle? lastCandle;

        public SimulatedOrderRouter(State state, Func<FillEvent, Task> onFillAsync, Action<string>? log = null)
        {
            this.state = state;
            this.onFillAsync = onFillAsync;
            this.log = log;
        }

        public void UpdateLastCandle(OHLCCandle candle) => lastCandle = candle;

        /// <summary>Portfolio mode: set the current mark price for each symbol and the bar timestamp.
        /// Market orders fill at these marks and equity/margin is evaluated against them.</summary>
        public void UpdateMarks(IReadOnlyDictionary<string, decimal> marks, DateTime ts)
        {
            foreach (var kv in marks) state.Marks[kv.Key] = kv.Value;
            state.CurrentBarTs = ts;
        }

        public async Task<OrderIntent> PlaceOrderAsync(string deploymentId, OrderRequest request, CancellationToken ct = default)
        {
            string orderId = Guid.NewGuid().ToString("N");
            var intent = new OrderIntent
            {
                Id = orderId,
                IntentId = request.IntentId,
                DeploymentId = deploymentId,
                Request = request,
                Status = OrderStatus.Pending,
                PlacedUtc = DateTime.UtcNow
            };

            switch (request.Type)
            {
                case OrderType.Market:
                    if (state.PortfolioMode)
                    {
                        decimal mark = state.Mark(request.Symbol);
                        if (mark <= 0m)
                        {
                            intent.Status = OrderStatus.Rejected;
                            intent.Error = $"No mark for {request.Symbol}";
                            return intent;
                        }
                        await FillMarketAsync(intent, request, ApplySlippage(mark, request.Side), state.CurrentBarTs);
                        break;
                    }
                    if (!lastCandle.HasValue)
                    {
                        intent.Status = OrderStatus.Rejected;
                        intent.Error = "No market data yet";
                        return intent;
                    }
                    decimal price = ApplySlippage(lastCandle.Value.Close, request.Side);
                    await FillMarketAsync(intent, request, price, lastCandle.Value.Timestamp);
                    break;
                case OrderType.Limit:
                case OrderType.StopLoss:
                case OrderType.TakeProfit:
                    intent.Status = OrderStatus.Open;
                    state.OpenOrders.Add(intent);
                    break;
            }
            return intent;
        }

        public Task CancelOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            state.OpenOrders.RemoveAll(o => o.Id == intent.Id);
            intent.Status = OrderStatus.Cancelled;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Per-bar processing: accrue margin borrow cost, check for liquidation, then fill any
        /// open conditional orders against the candle highs/lows.
        /// </summary>
        public async Task OnCandleAsync(OHLCCandle candle, CancellationToken ct = default)
        {
            lastCandle = candle;

            if (state.MarginEnabled)
            {
                AccrueBorrowFee(candle.Close);
                await CheckLiquidationAsync(candle, ct);
            }

            // Snapshot to allow mutation during iteration.
            var toCheck = state.OpenOrders.ToList();
            foreach (var intent in toCheck)
            {
                if (intent.Status != OrderStatus.Open && intent.Status != OrderStatus.PartiallyFilled) continue;
                var req = intent.Request;
                bool triggered = req.Type switch
                {
                    OrderType.Limit => req.Side == OrderSide.Buy
                        ? candle.Low <= req.LimitPrice
                        : candle.High >= req.LimitPrice,
                    OrderType.StopLoss => req.Side == OrderSide.Sell
                        ? candle.Low <= req.StopPrice
                        : candle.High >= req.StopPrice,
                    OrderType.TakeProfit => req.Side == OrderSide.Sell
                        ? candle.High >= req.LimitPrice
                        : candle.Low <= req.LimitPrice,
                    _ => false
                };
                if (!triggered) continue;

                decimal fillPrice = req.Type switch
                {
                    OrderType.Limit => req.LimitPrice!.Value,
                    OrderType.StopLoss => req.StopPrice!.Value,
                    OrderType.TakeProfit => req.LimitPrice!.Value,
                    _ => candle.Close
                };
                fillPrice = ApplySlippage(fillPrice, req.Side);
                state.OpenOrders.Remove(intent);
                await FillMarketAsync(intent, req, fillPrice, candle.Timestamp);
            }
        }

        private async Task FillMarketAsync(OrderIntent intent, OrderRequest req, decimal price, DateTime ts)
        {
            if (state.PortfolioMode)
            {
                await FillPortfolioAsync(intent, req, price, ts);
                return;
            }

            decimal notional = req.Qty * price;
            decimal fee = notional * state.FeeFraction;

            if (!state.MarginEnabled)
            {
                // ── Spot mode (unchanged from the original engine) ─────────────────
                if (req.Side == OrderSide.Buy)
                {
                    decimal cost = notional + fee;
                    if (cost > state.QuoteBalance)
                    {
                        intent.Status = OrderStatus.Rejected;
                        intent.Error = "Insufficient quote balance";
                        return;
                    }
                    state.QuoteBalance -= cost;
                    state.BaseBalance += req.Qty;
                }
                else
                {
                    if (req.Qty > state.BaseBalance)
                    {
                        intent.Status = OrderStatus.Rejected;
                        intent.Error = "Insufficient base balance";
                        return;
                    }
                    state.BaseBalance -= req.Qty;
                    state.QuoteBalance += notional - fee;
                }
            }
            else
            {
                // ── Margin mode: allow leveraged longs and shorts up to equity × leverage ──
                decimal signed = req.Side == OrderSide.Buy ? req.Qty : -req.Qty;
                decimal newBase = state.BaseBalance + signed;
                decimal equity = state.QuoteBalance + state.BaseBalance * price;
                bool increasing = Math.Abs(newBase) > Math.Abs(state.BaseBalance) + 1e-12m;

                if (increasing)
                {
                    decimal newNotional = Math.Abs(newBase) * price;
                    decimal maxNotional = Math.Max(0m, equity) * state.EffectiveLeverage;
                    if (newNotional > maxNotional * 1.0000001m)
                    {
                        intent.Status = OrderStatus.Rejected;
                        intent.Error = $"Exceeds margin: notional {newNotional:F2} > {maxNotional:F2} ({state.EffectiveLeverage:F1}x)";
                        return;
                    }
                    fee += notional * state.OpeningFeeFraction; // margin opening fee
                }

                // Balances may go negative (quote = borrowed cash; base = short).
                if (req.Side == OrderSide.Buy) state.QuoteBalance -= notional + fee;
                else                            state.QuoteBalance += notional - fee;
                state.BaseBalance = newBase;
            }

            state.Fees += fee;
            UpdatePosition(req.Side, req.Qty, price, ts, req.Symbol);

            intent.Status = OrderStatus.Filled;
            var fill = new FillEvent
            {
                OrderId = intent.Id,
                IntentId = intent.IntentId,
                Qty = req.Qty,
                Price = price,
                Fee = fee,
                FeeCurrency = "USD",
                FilledUtc = ts,
                Symbol = req.Symbol
            };
            await onFillAsync(fill);
        }

        // ══ Portfolio mode ═══════════════════════════════════════════════════════

        /// <summary>
        /// Portfolio-mode fill: one shared cash account, per-symbol base balances. Spot (1×) forbids
        /// shorting and bounds buys by cash, exactly like the single-symbol path but keyed by symbol.
        /// Margin (&gt;1×) is evaluated portfolio-wide: an increase is rejected if it would push gross
        /// notional beyond equity × leverage.
        /// </summary>
        private async Task FillPortfolioAsync(OrderIntent intent, OrderRequest req, decimal price, DateTime ts)
        {
            string sym = req.Symbol;
            decimal notional = req.Qty * price;
            decimal fee = notional * state.FeeFraction;
            decimal curBase = state.GetBaseBalance(sym);

            if (!state.MarginEnabled)
            {
                if (req.Side == OrderSide.Buy)
                {
                    decimal cost = notional + fee;
                    if (cost > state.QuoteBalance)
                    {
                        intent.Status = OrderStatus.Rejected;
                        intent.Error = "Insufficient quote balance";
                        return;
                    }
                    state.QuoteBalance -= cost;
                    state.BaseBalances[sym] = curBase + req.Qty;
                }
                else
                {
                    if (req.Qty > curBase)
                    {
                        intent.Status = OrderStatus.Rejected;
                        intent.Error = "Insufficient base balance";
                        return;
                    }
                    state.BaseBalances[sym] = curBase - req.Qty;
                    state.QuoteBalance += notional - fee;
                }
            }
            else
            {
                decimal signed = req.Side == OrderSide.Buy ? req.Qty : -req.Qty;
                decimal newBase = curBase + signed;
                bool increasing = Math.Abs(newBase) > Math.Abs(curBase) + 1e-12m;

                if (increasing)
                {
                    // Replace this symbol's contribution to gross with its post-fill notional.
                    decimal grossOthers = state.GrossNotional() - Math.Abs(curBase) * state.Mark(sym);
                    decimal grossAfter = grossOthers + Math.Abs(newBase) * price;
                    decimal equity = state.PortfolioEquity();
                    decimal maxNotional = Math.Max(0m, equity) * state.EffectiveLeverage;
                    if (grossAfter > maxNotional * 1.0000001m)
                    {
                        intent.Status = OrderStatus.Rejected;
                        intent.Error = $"Exceeds margin: gross {grossAfter:F2} > {maxNotional:F2} ({state.EffectiveLeverage:F1}x)";
                        return;
                    }
                    fee += notional * state.OpeningFeeFraction;
                }

                if (req.Side == OrderSide.Buy) state.QuoteBalance -= notional + fee;
                else                            state.QuoteBalance += notional - fee;
                state.BaseBalances[sym] = newBase;
            }

            state.Fees += fee;
            UpdatePortfolioPosition(sym, req.Side, req.Qty, price, ts);

            intent.Status = OrderStatus.Filled;
            var fill = new FillEvent
            {
                OrderId = intent.Id,
                IntentId = intent.IntentId,
                Qty = req.Qty,
                Price = price,
                Fee = fee,
                FeeCurrency = "USD",
                FilledUtc = ts,
                Symbol = req.Symbol
            };
            await onFillAsync(fill);
        }

        /// <summary>Per-symbol mirror of <see cref="UpdatePosition"/> for portfolio mode.</summary>
        private void UpdatePortfolioPosition(string symbol, OrderSide side, decimal qty, decimal price, DateTime ts)
        {
            state.Positions.TryGetValue(symbol, out var pos);
            pos ??= new Position { Symbol = symbol, OpenedUtc = ts };
            decimal signed = side == OrderSide.Buy ? qty : -qty;
            decimal newQty = pos.Qty + signed;
            if (Math.Sign(pos.Qty) != Math.Sign(newQty) || pos.Qty == 0)
            {
                pos.AveragePrice = price;
                pos.OpenedUtc = ts;
            }
            else if (Math.Sign(signed) == Math.Sign(pos.Qty))
            {
                decimal totalCost = pos.AveragePrice * Math.Abs(pos.Qty) + price * qty;
                pos.AveragePrice = totalCost / (Math.Abs(pos.Qty) + qty);
            }
            pos.Qty = newQty;
            if (newQty == 0) state.Positions.Remove(symbol);
            else state.Positions[symbol] = pos;
        }

        /// <summary>
        /// Portfolio per-bar processing. Sets marks from the bar, accrues borrow cost on total borrowed
        /// notional, force-liquidates the whole book if the portfolio margin level breaches the
        /// threshold, then fills any open conditional orders against their symbol's candle.
        /// </summary>
        public async Task OnPortfolioCandlesAsync(IReadOnlyDictionary<string, OHLCCandle> bar, CancellationToken ct = default)
        {
            DateTime ts = state.CurrentBarTs;
            foreach (var kv in bar)
            {
                state.Marks[kv.Key] = kv.Value.Close;
                if (kv.Value.Timestamp > ts) ts = kv.Value.Timestamp;
            }
            state.CurrentBarTs = ts;

            if (state.MarginEnabled)
            {
                AccruePortfolioBorrowFee();
                await CheckPortfolioLiquidationAsync(bar, ts, ct);
            }

            var toCheck = state.OpenOrders.ToList();
            foreach (var intent in toCheck)
            {
                if (intent.Status != OrderStatus.Open && intent.Status != OrderStatus.PartiallyFilled) continue;
                var req = intent.Request;
                if (!bar.TryGetValue(req.Symbol, out var candle)) continue;
                bool triggered = req.Type switch
                {
                    OrderType.Limit => req.Side == OrderSide.Buy
                        ? candle.Low <= req.LimitPrice
                        : candle.High >= req.LimitPrice,
                    OrderType.StopLoss => req.Side == OrderSide.Sell
                        ? candle.Low <= req.StopPrice
                        : candle.High >= req.StopPrice,
                    OrderType.TakeProfit => req.Side == OrderSide.Sell
                        ? candle.High >= req.LimitPrice
                        : candle.Low <= req.LimitPrice,
                    _ => false
                };
                if (!triggered) continue;

                decimal fillPrice = req.Type switch
                {
                    OrderType.Limit => req.LimitPrice!.Value,
                    OrderType.StopLoss => req.StopPrice!.Value,
                    OrderType.TakeProfit => req.LimitPrice!.Value,
                    _ => candle.Close
                };
                fillPrice = ApplySlippage(fillPrice, req.Side);
                state.OpenOrders.Remove(intent);
                await FillMarketAsync(intent, req, fillPrice, candle.Timestamp);
            }
        }

        /// <summary>Per-bar borrow/rollover cost on the portfolio's total borrowed notional.</summary>
        private void AccruePortfolioBorrowFee()
        {
            decimal gross = state.GrossNotional();
            if (gross <= 0m) return;
            decimal equity = state.PortfolioEquity();
            decimal borrowed = gross - Math.Max(0m, equity);
            if (borrowed <= 0m) return;
            decimal feeAmt = state.BorrowAnnualRate * borrowed * state.SecondsPerBar / YearSeconds;
            if (feeAmt <= 0m) return;
            state.QuoteBalance -= feeAmt;
            state.Fees += feeAmt;
        }

        /// <summary>
        /// Cross-margin liquidation: if the portfolio margin level breaches the threshold at the bar's
        /// worst-case (each position marked at its adverse intrabar extreme — longs at the low, shorts
        /// at the high), force-flatten the whole book at those adverse prices. For a single position
        /// this reduces to the usual intrabar liquidation on the candle low/high.
        /// </summary>
        private async Task CheckPortfolioLiquidationAsync(IReadOnlyDictionary<string, OHLCCandle> bar, DateTime ts, CancellationToken ct)
        {
            decimal AdversePrice(string sym, decimal b)
                => bar.TryGetValue(sym, out var c) ? (b > 0m ? c.Low : c.High) : state.Mark(sym);

            decimal grossAdverse = 0m, equityAdverse = state.QuoteBalance;
            foreach (var kv in state.BaseBalances)
            {
                if (kv.Value == 0m) continue;
                decimal p = AdversePrice(kv.Key, kv.Value);
                equityAdverse += kv.Value * p;
                grossAdverse += Math.Abs(kv.Value) * p;
            }
            if (grossAdverse <= 0m) return;

            decimal usedMargin = grossAdverse / state.EffectiveLeverage;
            decimal marginLevel = usedMargin <= 0m ? decimal.MaxValue : equityAdverse / usedMargin;
            if (marginLevel > state.LiquidationMarginLevel) return;

            state.Liquidated = true;
            log?.Invoke($"PORTFOLIO LIQUIDATION: marginLevel {marginLevel:F2} <= {state.LiquidationMarginLevel:F2}, " +
                        $"flattening {state.NonZeroPositions().Count} positions");
            foreach (var symbol in state.BaseBalances.Keys.ToList())
            {
                decimal b = state.GetBaseBalance(symbol);
                if (b == 0m) continue;
                var side = b > 0m ? OrderSide.Sell : OrderSide.Buy;
                var req = new OrderRequest
                {
                    IntentId = "liq-" + Guid.NewGuid().ToString("N"),
                    Side = side,
                    Type = OrderType.Market,
                    Symbol = symbol,
                    Qty = Math.Abs(b)
                };
                var intent = new OrderIntent
                {
                    Id = Guid.NewGuid().ToString("N"),
                    IntentId = req.IntentId,
                    DeploymentId = "sim",
                    Request = req,
                    Status = OrderStatus.Pending,
                    PlacedUtc = DateTime.UtcNow
                };
                await FillMarketAsync(intent, req, ApplySlippage(AdversePrice(symbol, b), side), ts);
            }
        }

        /// <summary>Charge the per-bar borrow/rollover cost on the borrowed notional.</summary>
        private void AccrueBorrowFee(decimal markPrice)
        {
            if (state.Position == null || state.BaseBalance == 0m) return;
            decimal equity = state.QuoteBalance + state.BaseBalance * markPrice;
            decimal notional = Math.Abs(state.BaseBalance) * markPrice;
            decimal borrowed = notional - Math.Max(0m, equity); // portion funded by the broker
            if (borrowed <= 0m) return;

            decimal feeAmt = state.BorrowAnnualRate * borrowed * state.SecondsPerBar / YearSeconds;
            if (feeAmt <= 0m) return;
            state.QuoteBalance -= feeAmt;
            state.Fees += feeAmt;
        }

        /// <summary>
        /// Force-close the position if the bar's adverse extreme reaches the liquidation price
        /// (where margin level == LiquidationMarginLevel). Solves equity/usedMargin == m for P.
        /// </summary>
        private async Task CheckLiquidationAsync(OHLCCandle candle, CancellationToken ct)
        {
            decimal b = state.BaseBalance;
            if (state.Position == null || b == 0m) return;

            decimal lev = state.EffectiveLeverage;
            decimal m = state.LiquidationMarginLevel;
            decimal absB = Math.Abs(b);

            // marginLevel(P) = lev*(Q + b*P) / (|b|*P) == m  ->  P = lev*Q / (m*|b| - lev*b)
            decimal denom = m * absB - lev * b;
            if (denom == 0m) return;
            decimal liqPrice = lev * state.QuoteBalance / denom;
            if (liqPrice <= 0m) return;

            bool hit = b > 0m ? candle.Low <= liqPrice : candle.High >= liqPrice;
            if (!hit) return;

            // Fill at the liquidation price, clamped into the candle's range.
            decimal fillP = b > 0m
                ? Math.Min(Math.Max(liqPrice, candle.Low), candle.High)
                : Math.Max(Math.Min(liqPrice, candle.High), candle.Low);

            var side = b > 0m ? OrderSide.Sell : OrderSide.Buy;
            var req = new OrderRequest
            {
                IntentId = "liq-" + Guid.NewGuid().ToString("N"),
                Side = side,
                Type = OrderType.Market,
                Symbol = state.Position!.Symbol,
                Qty = absB
            };
            var intent = new OrderIntent
            {
                Id = Guid.NewGuid().ToString("N"),
                IntentId = req.IntentId,
                DeploymentId = "sim",
                Request = req,
                Status = OrderStatus.Pending,
                PlacedUtc = DateTime.UtcNow
            };

            state.Liquidated = true;
            log?.Invoke($"LIQUIDATION: {(b > 0m ? "long" : "short")} {absB} @ {fillP:F2} " +
                        $"(liqPrice={liqPrice:F2}, {lev:F1}x)");
            await FillMarketAsync(intent, req, ApplySlippage(fillP, side), candle.Timestamp);
        }

        private void UpdatePosition(OrderSide side, decimal qty, decimal price, DateTime ts, string symbol)
        {
            state.Position ??= new Position { Symbol = symbol, OpenedUtc = ts };
            decimal signed = side == OrderSide.Buy ? qty : -qty;
            decimal newQty = state.Position.Qty + signed;
            if (Math.Sign(state.Position.Qty) != Math.Sign(newQty) || state.Position.Qty == 0)
            {
                state.Position.AveragePrice = price;
                state.Position.OpenedUtc = ts;
            }
            else if (Math.Sign(signed) == Math.Sign(state.Position.Qty))
            {
                decimal totalCost = state.Position.AveragePrice * Math.Abs(state.Position.Qty) + price * qty;
                state.Position.AveragePrice = totalCost / (Math.Abs(state.Position.Qty) + qty);
            }
            state.Position.Qty = newQty;
            if (newQty == 0) state.Position = null;
        }

        private decimal ApplySlippage(decimal price, OrderSide side)
            => side == OrderSide.Buy
                ? price * (1 + state.SlippageFraction)
                : price * (1 - state.SlippageFraction);
    }
}
