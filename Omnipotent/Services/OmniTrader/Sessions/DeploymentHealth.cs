using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;

namespace Omnipotent.Services.OmniTrader.Sessions
{
    /// <summary>One deployment's health, as the Deploy page shows it.</summary>
    public sealed class DeploymentHealthView
    {
        /// <summary>ok | starting | warming | quiet | erroring | stalled | detached | disarmed | paused | stopped | errored</summary>
        public required string State { get; init; }
        /// <summary>ok | info | warn | crit | neutral</summary>
        public required string Tone { get; init; }
        public required string Title { get; init; }
        public string? Detail { get; init; }
        public DateTime? LastBarUtc { get; init; }
        public DateTime? LastBarSeenUtc { get; init; }
        public int BarsThisRun { get; init; }
        public int BarsTotal { get; init; }
        public int Orders { get; init; }
        public int Filled { get; init; }
        public int Rejected { get; init; }
        public DateTime? LastOrderUtc { get; init; }
        public double StallAfterMinutes { get; init; }
        public int Errors { get; init; }
        public string? LastError { get; init; }
        public bool InMemory { get; init; }
    }

    /// <summary>
    /// Rules that turn a deployment's row, its session's pulse and its order counts into one state. Built
    /// after a basket deployment ran 25 days, 143 bars, without one order and without anything saying so.
    /// </summary>
    public static class DeploymentHealth
    {
        public static DeploymentHealthView Assess(DeploymentRow row, SessionPulse? pulse, OrderSummary? orders, bool armed, DateTime nowUtc)
        {
            var origin = row.Config.Origin;
            int placed = orders?.Orders ?? 0;
            double stallAfter = StallAfter(row.Config.Interval, row.Config.Symbol).TotalMinutes;

            DeploymentHealthView View(string state, string tone, string title, string? detail = null) => new()
            {
                State = state, Tone = tone, Title = title, Detail = detail,
                LastBarUtc = pulse?.LastBarUtc,
                LastBarSeenUtc = pulse?.LastBarSeenUtc,
                BarsThisRun = pulse?.BarsThisRun ?? 0,
                BarsTotal = pulse?.BarsTotal ?? 0,
                Orders = placed,
                Filled = orders?.Filled ?? 0,
                Rejected = orders?.Rejected ?? 0,
                LastOrderUtc = orders?.LastPlacedUtc,
                StallAfterMinutes = stallAfter,
                Errors = pulse?.Errors ?? 0,
                LastError = pulse?.LastError,
                InMemory = pulse != null,
            };

            if (row.Status == DeploymentStatus.Stopped) return View("stopped", "neutral", "Stopped");
            if (row.Status == DeploymentStatus.Errored) return View("errored", "crit", "Stopped by an error", row.Error);

            if (row.Status == DeploymentStatus.Paused)
            {
                if (row.Mode == SessionMode.Live)
                    return pulse == null
                        ? View("detached", "crit", "Live session not running", "It is paused but no session runs in this process (Kraken may be unconfigured), so it cannot be armed.")
                        : View("disarmed", "info", armed ? "Armed" : "Disarmed", "It follows the market but sends nothing to Kraken until you arm it.");
                return View("paused", "neutral", "Paused", "Positions are kept and carried on when you resume.");
            }

            // Running.
            if (pulse == null)
                return View("detached", "crit", "Not running in this process",
                    "Marked running, but no session is attached: it failed to start or recover. Stop it and deploy again.");

            if (pulse.LastBarUtc == null)
                return View("starting", "info", "Waiting for data", "No price history has arrived for it yet.");

            var age = nowUtc - pulse.LastBarUtc.Value;
            if (age.TotalMinutes > stallAfter)
                return View("stalled", "crit", $"No new bar for {Span(age)}",
                    $"It expects a {Short(row.Config.Interval)} bar from {row.Config.Symbol}; the newest it has opened {Span(age)} ago.");

            if (pulse.Errors > 0 && pulse.LastErrorUtc is { } lastErr && nowUtc - lastErr < TimeSpan.FromHours(24))
                return View("erroring", "warn", $"{pulse.Errors} error{(pulse.Errors == 1 ? "" : "s")} since it started", pulse.LastError);

            int warmup = origin?.WarmupBars ?? 0;
            int preload = row.Config.HistoryBars ?? (pulse.Kind == "single" ? PaperSession.PreloadCandles : MultiAssetSession.PreloadBars);
            int short_ = warmup - preload - pulse.BarsTotal;
            if (placed == 0 && short_ > 0)
                return View("warming", "info", $"Warming up: about {short_} more bar{(short_ == 1 ? "" : "s")}",
                    $"Its inputs look back about {warmup} bars and {preload} were preloaded.");

            if (placed == 0)
            {
                double? every = origin?.BarsPerTrade;
                int quietAfter = every is > 0 ? (int)Math.Ceiling(Math.Max(30, every.Value * 3)) : 120;
                if (pulse.BarsTotal >= quietAfter)
                    return View("quiet", "warn", $"No orders in {pulse.BarsTotal} bars",
                        every is > 0
                            ? $"In its backtest it traded about every {Math.Round(every.Value)} bars."
                            : "It has processed bars for a while without placing anything.");
            }

            string last = $"last bar {Span(age)} ago";
            return View("ok", "ok", placed > 0 ? $"Trading: {placed} order{(placed == 1 ? "" : "s")}" : "Running", last);
        }

        /// <summary>How long without a new bar before a running deployment counts as stalled.</summary>
        public static TimeSpan StallAfter(TimeInterval interval, string symbol)
        {
            // A bar is stamped with its open time, so the newest closed bar is up to two bars old; a third
            // bar and a polling margin on top. Equity, FX and futures feeds also close for weekends.
            var span = TimeSpan.FromMinutes((int)interval * 3 + 20);
            if (MarketDataRouter.UsesEquityFeed(symbol, Venues.AssetClass.Unknown)) span += TimeSpan.FromHours(74);
            return span;
        }

        public static string Short(TimeInterval i) => i switch
        {
            TimeInterval.OneMinute => "1m", TimeInterval.FiveMinute => "5m", TimeInterval.FifteenMinute => "15m",
            TimeInterval.ThirtyMinute => "30m", TimeInterval.OneHour => "1h", TimeInterval.TwoHour => "2h",
            TimeInterval.FourHour => "4h", TimeInterval.TwelveHour => "12h", TimeInterval.OneDay => "1d",
            TimeInterval.OneWeek => "1w", TimeInterval.OneMonth => "1M", _ => i.ToString(),
        };

        public static string Span(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalMinutes < 1) return "under a minute";
            if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min";
            if (t.TotalDays < 1) return t.Minutes > 0 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{(int)t.TotalHours} h";
            return t.Hours > 0 ? $"{(int)t.TotalDays} d {t.Hours} h" : $"{(int)t.TotalDays} d";
        }
    }
}
