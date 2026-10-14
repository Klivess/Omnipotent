using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Technicals;
using Omnipotent.Services.OmniTrader.Technicals.Formula;

namespace Omnipotent.Services.OmniTrader.Screening
{
    public enum AlertMode
    {
        /// <summary>Fire the first time the condition is true on a closed bar, then disable.</summary>
        Once,
        /// <summary>Fire when the condition turns true (false → true on consecutive closed bars).</summary>
        OnChange,
        /// <summary>Fire on every closed bar where the condition is true.</summary>
        EveryBar
    }

    /// <summary>A condition in the formula language, watched on one symbol's closed bars.</summary>
    public sealed class MarketAlert
    {
        public string Id { get; set; } = "al_" + Guid.NewGuid().ToString("N")[..12];
        public string Name { get; set; } = "";
        public string Symbol { get; set; } = "BTCUSDT";
        public TimeInterval Interval { get; set; } = TimeInterval.OneHour;
        public string Condition { get; set; } = "";
        public string? Message { get; set; }
        public AlertMode Mode { get; set; } = AlertMode.OnChange;
        public bool Enabled { get; set; } = true;
        public bool Notify { get; set; } = true;
        public string CreatedBy { get; set; } = "klives";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? ExpiresUtc { get; set; }

        public DateTime? LastCheckedUtc { get; set; }
        public DateTime? LastBarUtc { get; set; }
        public bool? LastValue { get; set; }
        public DateTime? LastTriggeredUtc { get; set; }
        public int TriggerCount { get; set; }
        public string? LastError { get; set; }
    }

    public sealed class AlertEvent
    {
        public string Id { get; set; } = "ae_" + Guid.NewGuid().ToString("N")[..12];
        public required string AlertId { get; set; }
        public string AlertName { get; set; } = "";
        public string Symbol { get; set; } = "";
        public string Condition { get; set; } = "";
        public DateTime BarUtc { get; set; }
        public DateTime TriggeredUtc { get; set; } = DateTime.UtcNow;
        public double Close { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// Evaluates every enabled alert once per new closed bar of its interval and records (and
    /// optionally pushes) the bars where it fires. Conditions are compiled once per save.
    /// </summary>
    public sealed class MarketAlertService
    {
        private readonly DocumentStore<MarketAlert> alerts;
        private readonly DocumentStore<AlertEvent> events;
        private readonly MarketDataRouter marketData;
        private readonly Func<string, Task> notify;
        private readonly Action<string, Exception?> err;
        private readonly SemaphoreSlim gate = new(1, 1);

        public MarketAlertService(OmniTraderDb db, MarketDataRouter marketData, Func<string, Task> notify, Action<string, Exception?> err)
        {
            alerts = new DocumentStore<MarketAlert>(db, "market-alerts", a => a.Id,
                a => new DocumentKeys(K1: a.Symbol, K2: a.Enabled ? "enabled" : "disabled", T1: a.UpdatedUtc));
            events = new DocumentStore<AlertEvent>(db, "market-alert-events", e => e.Id,
                e => new DocumentKeys(K1: e.AlertId, K2: e.Symbol, T1: e.TriggeredUtc));
            this.marketData = marketData;
            this.notify = notify;
            this.err = err;
        }

        public Task<List<MarketAlert>> ListAsync(string? symbol = null, CancellationToken ct = default)
            => alerts.QueryAsync(new DocumentQuery { K1 = symbol, OrderBy = "t1", Limit = 1000 }, ct);

        public Task<MarketAlert?> GetAsync(string id, CancellationToken ct = default) => alerts.GetAsync(id, ct);

        public Task<List<AlertEvent>> EventsAsync(string? alertId = null, int limit = 200, CancellationToken ct = default)
            => events.QueryAsync(new DocumentQuery { K1 = alertId, OrderBy = "t1", Limit = Math.Clamp(limit, 1, 2000) }, ct);

        public static List<string> Validate(MarketAlert a)
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(a.Symbol)) errors.Add("symbol required");
            if (string.IsNullOrWhiteSpace(a.Condition)) { errors.Add("condition required"); return errors; }
            var (compiled, diag) = FormulaCompiler.Check(a.Condition);
            if (compiled == null) errors.AddRange(diag.Select(d => $"condition: {d.Message} (at {d.Position})"));
            else if (!compiled.IsBoolean) errors.Add("condition must be true/false, e.g. close > sma(close, 200)");
            return errors;
        }

        public async Task<MarketAlert> SaveAsync(MarketAlert a, CancellationToken ct = default)
        {
            var errors = Validate(a);
            if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));
            a.Symbol = a.Symbol.Trim().ToUpperInvariant();
            a.Name = string.IsNullOrWhiteSpace(a.Name) ? $"{a.Symbol} {a.Condition}" : a.Name.Trim();
            var existing = await alerts.GetAsync(a.Id, ct);
            if (existing != null && (existing.Condition != a.Condition || existing.Symbol != a.Symbol || existing.Interval != a.Interval))
            {
                // A different question starts with no memory of the old one's state.
                a.LastBarUtc = null; a.LastValue = null; a.LastError = null;
            }
            a.UpdatedUtc = DateTime.UtcNow;
            await alerts.UpsertAsync(a, ct);
            return a;
        }

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => alerts.DeleteAsync(id, ct);

        /// <summary>Evaluate a condition on the latest closed bars without recording anything.</summary>
        public async Task<(bool? Value, DateTime? BarUtc, double? Close, int TrueBars, int Bars, string? Error)> TestAsync(MarketAlert a, CancellationToken ct = default)
        {
            var errors = Validate(a);
            if (errors.Count > 0) return (null, null, null, 0, 0, string.Join("; ", errors));
            var compiled = FormulaCompiler.Compile(a.Condition);
            var bars = await marketData.GetHistoricalCandlesAsync(a.Symbol.Trim().ToUpperInvariant(), a.Interval, Math.Clamp(compiled.Lookback + 250, 300, 5000), ct);
            if (bars.Count == 0) return (null, null, null, 0, 0, "no bars for symbol");
            var values = await EvaluateAsync(compiled, bars, a.Interval, ct);
            int trueBars = values.Count(v => v == 1);
            double last = values[^1];
            return (TA.IsNum(last) ? last == 1 : null, bars[^1].Timestamp, (double)bars[^1].Close, trueBars, bars.Count, null);
        }

        public void Start(CancellationToken ct)
        {
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(45), ct); } catch { return; }
                while (!ct.IsCancellationRequested)
                {
                    try { await TickAsync(ct); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { err("market alert loop", ex); }
                    try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { return; }
                }
            }, ct);
        }

        private static TimeSpan CheckEvery(TimeInterval interval)
        {
            double sec = CandleClock.Seconds(interval) / 10.0;
            return TimeSpan.FromSeconds(Math.Clamp(sec, 30, 600));
        }

        public async Task TickAsync(CancellationToken ct = default)
        {
            if (!await gate.WaitAsync(0, ct)) return;
            try
            {
                var now = DateTime.UtcNow;
                var due = (await alerts.QueryAsync(new DocumentQuery { K2 = "enabled", Limit = 1000 }, ct))
                    .Where(a => a.LastCheckedUtc == null || now - a.LastCheckedUtc.Value >= CheckEvery(a.Interval)).ToList();
                foreach (var a in due)
                {
                    ct.ThrowIfCancellationRequested();
                    try { await CheckAsync(a, now, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        a.LastError = ex.Message;
                        a.LastCheckedUtc = now;
                        await alerts.UpsertAsync(a, ct);
                    }
                }
            }
            finally { gate.Release(); }
        }

        private async Task CheckAsync(MarketAlert a, DateTime now, CancellationToken ct)
        {
            a.LastCheckedUtc = now;
            if (a.ExpiresUtc is { } exp && now >= exp)
            {
                a.Enabled = false;
                await alerts.UpsertAsync(a, ct);
                return;
            }
            var compiled = FormulaCompiler.Compile(a.Condition);
            var bars = await marketData.GetHistoricalCandlesAsync(a.Symbol, a.Interval, Math.Clamp(compiled.Lookback + 60, 100, 5000), ct);
            if (bars.Count == 0) { a.LastError = "no bars"; await alerts.UpsertAsync(a, ct); return; }
            var lastBar = bars[^1];
            if (a.LastBarUtc == lastBar.Timestamp) { await alerts.UpsertAsync(a, ct); return; }

            var values = await EvaluateAsync(compiled, bars, a.Interval, ct);
            double v = values[^1];
            bool? value = TA.IsNum(v) ? v == 1 : null;
            bool fire = value == true && a.Mode switch
            {
                AlertMode.EveryBar => true,
                AlertMode.Once => true,
                // First evaluation of an OnChange alert only fires on a genuine edge in the data.
                AlertMode.OnChange => a.LastValue.HasValue ? a.LastValue == false : values.Length > 1 && values[^2] == 0,
                _ => false
            };
            a.LastBarUtc = lastBar.Timestamp;
            a.LastValue = value;
            a.LastError = null;
            if (fire)
            {
                a.LastTriggeredUtc = now;
                a.TriggerCount++;
                if (a.Mode == AlertMode.Once) a.Enabled = false;
                var e = new AlertEvent
                {
                    AlertId = a.Id, AlertName = a.Name, Symbol = a.Symbol, Condition = a.Condition,
                    BarUtc = lastBar.Timestamp, Close = (double)lastBar.Close, Message = a.Message
                };
                await events.UpsertAsync(e, ct);
                if (a.Notify)
                {
                    try
                    {
                        await notify($"🔔 {a.Name}: {a.Symbol} {CandleClock.Short(a.Interval)} close {e.Close:G8} — {a.Condition}"
                            + (string.IsNullOrWhiteSpace(a.Message) ? "" : $"\n{a.Message}"));
                    }
                    catch (Exception ex) { err($"alert {a.Id} notify", ex); }
                }
            }
            await alerts.UpsertAsync(a, ct);
        }

        private async Task<double[]> EvaluateAsync(CompiledFormula compiled, IReadOnlyList<OHLCCandle> bars, TimeInterval interval, CancellationToken ct)
        {
            var ext = new Dictionary<string, BarSeries?>(StringComparer.OrdinalIgnoreCase);
            foreach (var sym in compiled.ExternalSymbols.Distinct(StringComparer.OrdinalIgnoreCase).Take(5))
            {
                var eb = await marketData.GetHistoricalCandlesAsync(sym.ToUpperInvariant(), interval, bars.Count + 50, ct);
                ext[sym] = eb.Count == 0 ? null : BarSeries.From(eb);
            }
            return compiled.Evaluate(new FormulaContext { Bars = BarSeries.From(bars), External = s => ext.TryGetValue(s, out var b) ? b : null });
        }
    }
}
