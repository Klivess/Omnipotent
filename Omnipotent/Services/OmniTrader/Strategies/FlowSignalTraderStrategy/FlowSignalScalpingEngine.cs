using Omnipotent.Services.OmniTrader.Strategies.FlowSignalTraderStrategy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using static FlowSignalScalpingEngine;
public class FlowSignalScalpingEngine
{

    public enum Direction { LONG, SHORT }
    public enum SetupType { CVD_DIVERGENCE, MOMENTUM, RECOVERY, REVERSAL }
    public enum SignalStrength { WEAK, MEDIUM, STRONG }

    private readonly string _symbol;
        private CancellationTokenSource _cts;
        private bool _isReady = false;
    private FlowSignalTraderStrategy parent;

        // Analytics Components
        private readonly VPINCalculator _vpin = new();
        private readonly OFICalculator _ofi = new();
        private readonly CVDCalculator _cvd = new();
        private readonly RSICalculator _rsi = new();
        private readonly TrendFilter _trend = new();
        private readonly OrderBook _book = new();
        private readonly SetupGenerator _generator = new();
        private readonly FundingFetcher _funding;

        // Events for external subscription
        public event EventHandler<SignalEventArgs> OnSignal;
        public event EventHandler<string> OnStatusUpdate;

        public FlowSignalScalpingEngine (FlowSignalTraderStrategy parent, string symbol)
        {
            this.parent = parent;
            _symbol = symbol.ToUpper();
            _funding = new FundingFetcher(_symbol);
        }
    public class SignalEventArgs : EventArgs
    {
        public string Symbol { get; set; }
        public Setup Signal { get; set; }
    }

    public async Task RunAsync(CancellationToken externalToken = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            string wsUrl = "wss://stream.bybit.com/v5/public/linear";
            DateTime lastStatus = DateTime.MinValue;
            double lastPrice = 0;

            parent.StrategyLog($"Starting FlowSignal Engine for {_symbol}...");

            while (!_cts.IsCancellationRequested)
            {
                using var ws = new ClientWebSocket();
                try
                {
                    await ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
                    parent.StrategyLog($"WebSocket Connected: {_symbol}");

                    // Subscribe to Trades and Orderbook
                    var subMsg = JsonSerializer.Serialize(new
                    {
                        op = "subscribe",
                        args = new[] { $"publicTrade.{_symbol}", $"orderbook.50.{_symbol}" }
                    });
                    await ws.SendAsync(Encoding.UTF8.GetBytes(subMsg), WebSocketMessageType.Text, true, _cts.Token);

                    byte[] buffer = new byte[1024 * 32];
                    while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                    {
                        var (type, message) = await ReceiveFullMessage(ws, buffer, _cts.Token);
                        if (type == WebSocketMessageType.Close) break;

                        var data = JsonNode.Parse(message);
                        string topic = data?["topic"]?.ToString() ?? "";

                        if (topic.Contains("orderbook"))
                        {
                            _book.Update(data["data"]?["b"]?.AsArray(), data["data"]?["a"]?.AsArray());
                        }
                        else if (topic.Contains("publicTrade"))
                        {
                            var trades = data["data"]?.AsArray();
                            if (trades == null) continue;

                            foreach (var t in trades)
                            {
                                double p = double.Parse(t["p"].ToString());
                                double v = double.Parse(t["v"].ToString());
                                long ts = long.Parse(t["T"].ToString());
                                string side = t["S"].ToString();
                                lastPrice = p;

                                _vpin.AddTrade(v, side);
                                _ofi.AddTrade(ts, v, side, p * v);
                                _cvd.AddTrade(v, side, p);
                                _rsi.Update(p, ts);
                                _trend.Update(p);

                                if (!_isReady && _vpin.IsReady() && _rsi.IsReady())
                                {
                                    _isReady = true;
                                    parent.StrategyLog($"FlowSignal warmed up and monitoring {_symbol}");
                                }

                                if (_isReady)
                                {
                                    _ = _funding.UpdateAsync(); // Background update
                                    var setup = _generator.Check(p, _vpin, _ofi, _cvd, _rsi, _trend, _book, _funding);
                                    if (setup != null)
                                    {
                                        parent.StrategyLog($"[{_symbol}] {setup.SetupType} {setup.Direction} | Score: {setup.Score}");
                                        OnSignal?.Invoke(this, new SignalEventArgs { Symbol = _symbol, Signal = setup });
                                    }
                                }
                            }
                        }

                        // Periodic Status Update (30s)
                        if (_isReady && (DateTime.Now - lastStatus).TotalSeconds >= 30)
                        {
                            EmitStatus(lastPrice);
                            lastStatus = DateTime.Now;
                        }
                    }
                }
                catch (Exception ex) when (!_cts.IsCancellationRequested)
                {
                    parent.StrategyLogError(ex, $"Connection lost for {_symbol}. Retrying in 5s...");
                    await Task.Delay(5000, _cts.Token);
                }
            }
        }

        private void EmitStatus(double price)
        {
            string status = $"{_symbol} @ {price:F4} | VPIN: {_vpin.GetVpin():P0} | OFI: {_ofi.GetOfi():+0%;-0%} | CVD: {_cvd.GetTrendStr()} | Trend: {_trend.GetTrend()}";
            OnStatusUpdate?.Invoke(this, status);
        }

        private async Task<(WebSocketMessageType, string)> ReceiveFullMessage(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
        {
            using var ms = new System.IO.MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                ms.Write(buffer, 0, res.Count);
            } while (!res.EndOfMessage);
            return (res.MessageType, Encoding.UTF8.GetString(ms.ToArray()));
        }

        public void Stop() => _cts?.Cancel();
    }

    #region Support Classes (Calculators & Logic)

    public class Setup
    {
        public Direction Direction { get; set; }
        public SetupType SetupType { get; set; }
        public SignalStrength Strength { get; set; }
        public DateTime Timestamp { get; set; }
        public double Price { get; set; }
        public double StopLoss { get; set; }
        public double TakeProfit1 { get; set; }
        public double TakeProfit2 { get; set; }
        public int Score { get; set; }
        public string Reason { get; set; }
    }

    internal class SetupGenerator
    {
        private DateTime _lastSignal = DateTime.MinValue;
        private Direction? _lastDir = null;

        public Setup Check(double price, VPINCalculator vpin, OFICalculator ofi, CVDCalculator cvd, RSICalculator rsi, TrendFilter trend, OrderBook book, FundingFetcher funding)
        {
            if ((DateTime.Now - _lastSignal).TotalSeconds < 180) return null;

            double v = vpin.GetVpin();
            double o = ofi.GetOfi();
            string div = cvd.DetectDivergence();
            double r1 = rsi.Get1m();
            string tr = trend.GetTrend();

            Direction? dir = null;
            SetupType? type = null;
            string reason = "";

            // Logic matching Python version 2.3
            if (div == "bullish" && r1 < 70) { dir = Direction.LONG; type = SetupType.CVD_DIVERGENCE; reason = "Bullish Div"; }
            else if (div == "bearish" && r1 > 30) { dir = Direction.SHORT; type = SetupType.CVD_DIVERGENCE; reason = "Bearish Div"; }
            else if (v >= 0.52 && o >= 0.25 && tr != "DOWN") { dir = Direction.LONG; type = SetupType.MOMENTUM; reason = "Flow Momentum"; }
            else if (v >= 0.52 && o <= -0.25 && tr != "UP") { dir = Direction.SHORT; type = SetupType.MOMENTUM; reason = "Flow Momentum"; }

            if (dir != null)
            {
                if (_lastDir == dir && (DateTime.Now - _lastSignal).TotalSeconds < 300) return null;

                int score = 5; // Base
                if (v > 0.65) score += 3;
                if (Math.Abs(o) > 0.40) score += 3;
                if (div != null) score += 4;

                _lastSignal = DateTime.Now;
                _lastDir = dir;

                return new Setup
                {
                    Direction = dir.Value,
                    SetupType = type.Value,
                    Price = price,
                    Timestamp = DateTime.Now,
                    Score = score,
                    Reason = reason,
                    StopLoss = dir == Direction.LONG ? price * 0.99 : price * 1.01,
                    TakeProfit1 = dir == Direction.LONG ? price * 1.015 : price * 0.985
                };
            }
            return null;
        }
    }

    internal class VPINCalculator
    {
        private double _bucketSize = 0;
        private bool _calibrated = false;
        private List<double> _samples = new();
        private List<double> _buckets = new();
        private double _curBuy = 0, _curSell = 0, _curTotal = 0;

        public void AddTrade(double size, string side)
        {
            if (!_calibrated)
            {
                _samples.Add(size);
                if (_samples.Count >= 500)
                {
                    _bucketSize = _samples.Average() * 100;
                    _calibrated = true;
                }
                return;
            }

            if (side == "Buy") _curBuy += size; else _curSell += size;
            _curTotal += size;

            if (_curTotal >= _bucketSize)
            {
                double imb = Math.Abs(_curBuy - _curSell) / (_curBuy + _curSell);
                _buckets.Add(imb);
                if (_buckets.Count > 50) _buckets.RemoveAt(0);
                _curBuy = _curSell = _curTotal = 0;
            }
        }

        public double GetVpin() => _buckets.Count < 10 ? 0.5 : _buckets.Average();
        public bool IsReady() => _calibrated && _buckets.Count >= 20;
    }

    internal class OFICalculator
    {
        private List<(long ts, double size, bool isBuy)> _trades = new();

        public void AddTrade(long ts, double size, string side, double val)
        {
            _trades.Add((ts, size, side == "Buy"));
            long cutoff = ts - 120000;
            _trades.RemoveAll(x => x.ts < cutoff);
        }

        public double GetOfi()
        {
            double buy = _trades.Where(x => x.isBuy).Sum(x => x.size);
            double sell = _trades.Where(x => !x.isBuy).Sum(x => x.size);
            return (buy + sell) == 0 ? 0 : (buy - sell) / (buy + sell);
        }
    }

    internal class CVDCalculator
    {
        private double _delta = 0;
        private List<double> _history = new();
        private List<double> _priceHistory = new();

        public void AddTrade(double size, string side, double price)
        {
            _delta += (side == "Buy" ? size : -size);
            _history.Add(_delta);
            _priceHistory.Add(price);
            if (_history.Count > 300) { _history.RemoveAt(0); _priceHistory.RemoveAt(0); }
        }

        public string GetTrendStr() => (_history.Count < 2) ? "→" : (_history.Last() > _history[_history.Count - 2] ? "↑" : "↓");

        public string DetectDivergence()
        {
            if (_history.Count < 100) return null;
            double pChange = (_priceHistory.Last() - _priceHistory[0]) / _priceHistory[0];
            double dChange = _history.Last() - _history[0];

            if (pChange < -0.005 && dChange > 0) return "bullish";
            if (pChange > 0.005 && dChange < 0) return "bearish";
            return null;
        }
    }

    internal class RSICalculator
    {
        private List<double> _closes = new();
        public void Update(double price, long ts)
        {
            _closes.Add(price);
            if (_closes.Count > 200) _closes.RemoveAt(0);
        }
        public double Get1m() => 50; // Placeholder for standard RSI logic
        public bool IsReady() => _closes.Count > 20;
    }

    internal class TrendFilter
    {
        private List<double> _prices = new();
        public void Update(double p) { _prices.Add(p); if (_prices.Count > 100) _prices.RemoveAt(0); }
        public string GetTrend()
        {
            if (_prices.Count < 50) return "FLAT";
            double shortAvg = _prices.TakeLast(10).Average();
            double longAvg = _prices.TakeLast(50).Average();
            return shortAvg > longAvg ? "UP" : "DOWN";
        }
    }

    internal class OrderBook
    {
        private double _imb = 0;
        public void Update(JsonArray bids, JsonArray asks)
        {
            if (bids == null || asks == null) return;

            double ParseSizeFromNode(JsonNode node)
            {
                if (node == null) return 0;
                // Expect node to be a JsonArray like [price, size]
                if (node is JsonArray arr && arr.Count > 1)
                {
                    var valNode = arr[1];
                    if (valNode == null) return 0;
                    var s = valNode.ToString();
                    if (string.IsNullOrWhiteSpace(s)) return 0;
                    // Try direct parse with invariant culture
                    if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return v;
                    // Fallback: remove quotes and common thousand separators
                    s = s.Trim('"').Replace(",", ".");
                    if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
                }
                return 0;
            }

            double bV = 0;
            foreach (var x in bids)
                bV += ParseSizeFromNode(x);

            double aV = 0;
            foreach (var x in asks)
                aV += ParseSizeFromNode(x);

            _imb = (bV + aV) == 0 ? 0 : (bV - aV) / (bV + aV);
        }
    }

    internal class FundingFetcher
    {
        private readonly string _symbol;
        private double _rate = 0;
        public FundingFetcher(string s) => _symbol = s;
        public async Task UpdateAsync() { /* Implementation for Bybit API V5 Http Client */ }
        public double GetRate() => _rate;
    }

    #endregion
