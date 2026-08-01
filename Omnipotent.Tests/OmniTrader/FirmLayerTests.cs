using Omnipotent.Services.OmniTrader.Analytics;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Instruments;
using Omnipotent.Services.OmniTrader.Journal;
using Omnipotent.Services.OmniTrader.Ledger;
using Omnipotent.Services.OmniTrader.Ops;
using Omnipotent.Services.OmniTrader.Performance;
using Omnipotent.Services.OmniTrader.Risk;
using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// Cross-cutting firm-layer behaviour: emergency controls, reconciliation classification, the
    /// exposure model that keeps CFD notional out of owned inventory, and the shared analytics.
    /// </summary>
    public class FirmLayerTests
    {
        // ── emergency controls ────────────────────────────────────────────────────

        [Fact]
        public void SafeModeBlocksEveryScope()
        {
            var controls = new EmergencyControls();
            controls.EnterSafeMode("daily loss breached", "risk-engine", automatic: true);

            Assert.True(controls.IsBlocked(VenueId.Kraken, "kraken-live", "strategy-a", out var reason));
            Assert.Equal("daily loss breached", reason);
            Assert.True(controls.IsBlocked(VenueId.IG, null, null, out _));

            controls.ExitSafeMode("klives");
            Assert.False(controls.IsBlocked(VenueId.Kraken, "kraken-live", "strategy-a", out _));
        }

        [Fact]
        public void KillSwitchesScopeCorrectly()
        {
            var controls = new EmergencyControls();
            controls.Engage(new KillSwitch
            {
                Kind = KillScopeKind.Venue,
                Scope = VenueId.Kraken.ToString(),
                Reason = "venue misbehaving",
                TriggeredBy = "klives"
            });

            Assert.True(controls.IsBlocked(VenueId.Kraken, "any", "any", out var reason));
            Assert.Equal("venue misbehaving", reason);
            // Another venue is unaffected — a scoped switch must not become a firm-wide one.
            Assert.False(controls.IsBlocked(VenueId.IG, "any", "any", out _));

            Assert.True(controls.Release(KillScopeKind.Venue, VenueId.Kraken.ToString(), "klives"));
            Assert.False(controls.IsBlocked(VenueId.Kraken, "any", "any", out _));
        }

        [Fact]
        public void StrategyKillSwitchOnlyStopsThatStrategy()
        {
            var controls = new EmergencyControls();
            controls.Engage(new KillSwitch
            { Kind = KillScopeKind.Strategy, Scope = "strategy-a", Reason = "misfiring", TriggeredBy = "klives" });

            Assert.True(controls.IsBlocked(VenueId.Kraken, "acct", "strategy-a", out _));
            Assert.False(controls.IsBlocked(VenueId.Kraken, "acct", "strategy-b", out _));
        }

        [Fact]
        public void AutomaticTriggersTripSafeMode()
        {
            var controls = new EmergencyControls();
            var limits = new RiskLimits { MaxFirmDailyLoss = 250m, MaxDrawdownPercent = 15m };

            controls.EvaluateAutomaticTriggers(
                new RiskPortfolioState { DailyRealizedPnL = -300m, Equity = 1000m, PeakEquity = 1000m },
                new RiskOperationalState(), limits);
            Assert.True(controls.SafeModeActive);
            Assert.Contains("Daily loss", controls.SafeModeReason);
        }

        [Fact]
        public void UnknownOrdersTripSafeModeAutomatically()
        {
            var controls = new EmergencyControls();
            controls.EvaluateAutomaticTriggers(
                new RiskPortfolioState { Equity = 1000m, PeakEquity = 1000m },
                new RiskOperationalState { UnknownOrders = 1 },
                new RiskLimits());
            Assert.True(controls.SafeModeActive);
            Assert.Contains("unproven outcome", controls.SafeModeReason);
        }

        // ── reconciliation model ──────────────────────────────────────────────────

        [Fact]
        public void TimingDifferencesAreNotMaterial_UnexplainedOnesAre()
        {
            var timing = Break(BreakClassification.Timing);
            var unexplained = Break(BreakClassification.Unexplained);
            var external = Break(BreakClassification.ExternalManualActivity);

            Assert.False(timing.Material);
            Assert.True(unexplained.Material);
            Assert.True(external.Material);

            unexplained.ResolvedUtc = DateTime.UtcNow;
            Assert.False(unexplained.Material);
            Assert.False(unexplained.Open);
        }

        private static ReconciliationBreak Break(BreakClassification classification) => new()
        {
            Id = "b1",
            RunId = "r1",
            Venue = VenueId.Kraken,
            Environment = TradingEnvironment.Live,
            Kind = BreakKind.Position,
            Classification = classification,
            Subject = "crypto:BTC/USD",
            Detail = "difference"
        };

        // ── exposure model ────────────────────────────────────────────────────────

        [Fact]
        public void DerivativeAndInventoryExposureStaySeparate()
        {
            var spot = new FirmPosition
            {
                InstrumentId = "crypto:BTC/USD", Venue = VenueId.Kraken, Environment = TradingEnvironment.Live,
                AccountId = "kraken-live", Exposure = ExposureKind.Inventory,
                Quantity = 0.5m, AveragePrice = 90_000m, MarkPrice = 100_000m
            };
            var cfd = new FirmPosition
            {
                InstrumentId = "index:UK100/GBP", Venue = VenueId.IG, Environment = TradingEnvironment.Live,
                AccountId = "ig-live", Exposure = ExposureKind.Derivative,
                Quantity = 2m, AveragePrice = 8_000m, MarkPrice = 8_100m
            };

            Assert.Equal(ExposureKind.Inventory, spot.Exposure);
            Assert.Equal(ExposureKind.Derivative, cfd.Exposure);
            Assert.Equal(50_000m, spot.Notional);
            Assert.Equal(5_000m, spot.UnrealizedPnL);
            Assert.Equal(16_200m, cfd.Notional);
            Assert.Equal(200m, cfd.UnrealizedPnL);
        }

        [Fact]
        public void APositionDisagreeingWithTheVenueIsFlagged()
        {
            var position = new FirmPosition
            {
                InstrumentId = "crypto:BTC/USD", Venue = VenueId.Kraken, Environment = TradingEnvironment.Live,
                AccountId = "kraken-live", Exposure = ExposureKind.Inventory, Quantity = 1m
            };
            Assert.False(position.Disagrees);

            position.VenueQuantity = 1m;
            Assert.False(position.Disagrees);

            position.VenueQuantity = 0.9m;
            Assert.True(position.Disagrees);
        }

        // ── venue capabilities ────────────────────────────────────────────────────

        [Fact]
        public void KrakenSpotNeverAdvertisesMarginOrShorting()
        {
            var capabilities = new KrakenVenueAdapter(null!, null!).Capabilities;
            Assert.False(capabilities.SupportsShort);
            Assert.False(capabilities.SupportsLeverage);
            Assert.Equal(1m, capabilities.MaxLeverage);
            Assert.Equal(ExposureKind.Inventory, capabilities.Exposure);
            // A missing capability must carry a stated reason, not just be absent.
            Assert.NotNull(capabilities.WhyNot("SupportsShort"));
            Assert.NotNull(capabilities.WhyNot("SupportsLeverage"));
        }

        [Fact]
        public void VenueRegistryKeepsDemoAndLiveApart()
        {
            var registry = new VenueRegistry();
            var demo = new IGVenueAdapter(new IGRestClient("k", "u", "p", TradingEnvironment.Demo));
            var live = new IGVenueAdapter(new IGRestClient("k2", "u2", "p2", TradingEnvironment.Live));
            registry.Register(demo);
            registry.Register(live);

            Assert.Same(demo, registry.Resolve(VenueId.IG, TradingEnvironment.Demo));
            Assert.Same(live, registry.Resolve(VenueId.IG, TradingEnvironment.Live));
            // With both registered, an environment-less lookup is ambiguous and must refuse to guess.
            Assert.Null(registry.ResolveUnambiguous(VenueId.IG));
        }

        [Fact]
        public void IgDemoAndLiveUseDifferentBaseUrls()
        {
            Assert.Equal(IGRestClient.DemoBase, new IGRestClient("k", "u", "p", TradingEnvironment.Demo).BaseUrl);
            Assert.Equal(IGRestClient.LiveBase, new IGRestClient("k", "u", "p", TradingEnvironment.Live).BaseUrl);
        }

        // ── instrument master ─────────────────────────────────────────────────────

        [Fact]
        public void VenueMappingRoundsQuantityDownToTheStep()
        {
            var mapping = new VenueMapping
            { Venue = VenueId.Kraken, VenueSymbol = "XBTUSD", QuantityStep = 0.001m, TickSize = 0.1m };

            // Rounding down never creates size the caller did not ask for.
            Assert.Equal(1.234m, mapping.RoundQuantity(1.2349m));
            Assert.Equal(0m, mapping.RoundQuantity(0.0009m));
            Assert.Equal(-1.234m, mapping.RoundQuantity(-1.2349m));
            Assert.Equal(100.1m, mapping.RoundPrice(100.19m));
        }

        [Fact]
        public void EnginePairSplittingNormalisesStablecoinQuotes()
        {
            Assert.Equal(("BTC", "USD"), InstrumentMaster.SplitEnginePair("BTCUSDT"));
            Assert.Equal(("ETH", "USD"), InstrumentMaster.SplitEnginePair("ETHUSDC"));
            Assert.Equal(("SOL", "GBP"), InstrumentMaster.SplitEnginePair("SOLGBP"));
        }

        [Fact]
        public void KrakenAssetNamesAreNormalised()
        {
            Assert.Equal("BTC", KrakenVenueAdapter.NormalizeAsset("XXBT"));
            Assert.Equal("USD", KrakenVenueAdapter.NormalizeAsset("ZUSD"));
            Assert.Equal("ETH", KrakenVenueAdapter.NormalizeAsset("XETH"));
            Assert.Equal("XRP", KrakenVenueAdapter.NormalizeAsset("XRP"));
        }

        // ── analytics ─────────────────────────────────────────────────────────────

        private static List<OHLCCandle> Series(Func<int, decimal> close, int count = 120, decimal volume = 1000m)
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var candles = new List<OHLCCandle>(count);
            for (int i = 0; i < count; i++)
            {
                decimal c = close(i);
                candles.Add(new OHLCCandle(start.AddHours(i), c, c * 1.002m, c * 0.998m, c, volume));
            }
            return candles;
        }

        [Fact]
        public void SteadyUptrendIsClassifiedAsTrendingUp()
        {
            var reading = MarketAnalytics.ClassifyRegime(Series(i => 100m + i));
            Assert.Equal(MarketRegime.TrendingUp, reading.Regime);
            Assert.True(reading.AboveMovingAverage);
            Assert.True(reading.ChangePercent > 0m);
        }

        [Fact]
        public void SteadyDowntrendIsClassifiedAsTrendingDown()
        {
            var reading = MarketAnalytics.ClassifyRegime(Series(i => 300m - i));
            Assert.Equal(MarketRegime.TrendingDown, reading.Regime);
            Assert.False(reading.AboveMovingAverage);
        }

        [Fact]
        public void AFlatOscillatingMarketIsRangeBound()
        {
            var reading = MarketAnalytics.ClassifyRegime(Series(i => 100m + (i % 2 == 0 ? 1m : -1m)));
            Assert.Equal(MarketRegime.RangeBound, reading.Regime);
        }

        [Fact]
        public void MomentumRanksTheStrongerSeriesHigher()
        {
            var series = new Dictionary<string, IReadOnlyList<OHLCCandle>>
            {
                ["fast"] = Series(i => 100m + i * 2),
                ["slow"] = Series(i => 100m + i * 0.1m),
                ["down"] = Series(i => 300m - i)
            };
            var ranked = MarketAnalytics.RankByMomentum(series);
            Assert.Equal("fast", ranked[0].InstrumentId);
            Assert.Equal("down", ranked[^1].InstrumentId);
        }

        [Fact]
        public void PriceInsideItsRangeIsNotABreakout()
        {
            var reading = MarketAnalytics.AnalyseBreakout(Series(i => 100m + (i % 3)));
            Assert.Equal(0, reading.Direction);
            Assert.Equal(0, reading.Quality);
        }

        [Fact]
        public void AConvincingBreakoutScoresHigherThanAWeakOne()
        {
            // Flat base, then a decisive close well above it on heavy volume.
            var strong = Series(_ => 100m, 40);
            strong.Add(new OHLCCandle(strong[^1].Timestamp.AddHours(1), 100m, 112m, 100m, 111.5m, 20_000m));

            var weak = Series(_ => 100m, 40);
            weak.Add(new OHLCCandle(weak[^1].Timestamp.AddHours(1), 100m, 101m, 99m, 100.05m, 200m));

            var strongReading = MarketAnalytics.AnalyseBreakout(strong);
            var weakReading = MarketAnalytics.AnalyseBreakout(weak);

            Assert.Equal(1, strongReading.Direction);
            Assert.True(strongReading.Quality > weakReading.Quality,
                $"strong {strongReading.Quality:F3} should beat weak {weakReading.Quality:F3}");
        }

        [Fact]
        public void AlignmentIsPositiveWhenEveryTimeframeAgrees()
        {
            var reading = MarketAnalytics.AnalyseAlignment(new Dictionary<string, IReadOnlyList<OHLCCandle>>
            {
                ["1h"] = Series(i => 100m + i),
                ["1d"] = Series(i => 100m + i * 3)
            });
            Assert.Equal(1.0, reading.Score, 3);
        }

        [Fact]
        public void AlignmentCancelsWhenTimeframesDisagree()
        {
            var reading = MarketAnalytics.AnalyseAlignment(new Dictionary<string, IReadOnlyList<OHLCCandle>>
            {
                ["1h"] = Series(i => 100m + i),
                ["1d"] = Series(i => 300m - i)
            });
            Assert.Equal(0.0, reading.Score, 3);
        }

        [Fact]
        public void BreadthMeasuresParticipationAcrossTheUniverse()
        {
            var universe = new Dictionary<string, IReadOnlyList<OHLCCandle>>
            {
                ["a"] = Series(i => 100m + i),
                ["b"] = Series(i => 100m + i),
                ["c"] = Series(i => 300m - i),
                ["d"] = Series(i => 300m - i)
            };
            var reading = MarketAnalytics.AnalyseBreadth(universe);
            Assert.Equal(4, reading.Members);
            Assert.Equal(50m, reading.AdvancingPercent);
            Assert.Equal(50m, reading.AboveTrendPercent);
        }

        [Fact]
        public void LiquidityReportsTradedValue()
        {
            var reading = MarketAnalytics.AnalyseLiquidity(Series(_ => 100m, 40, volume: 500m));
            Assert.Equal(50_000m, reading.AverageQuoteVolume);
            Assert.True(reading.EstimatedSpreadPercent > 0m);
        }

        [Fact]
        public void VolatilityRisesWithNoise()
        {
            double calm = MarketAnalytics.RealizedVolatility(Series(i => 100m + i * 0.01m));
            double wild = MarketAnalytics.RealizedVolatility(Series(i => 100m + (i % 2 == 0 ? 15m : -15m)));
            Assert.True(wild > calm);
        }

        // ── alerts ────────────────────────────────────────────────────────────────

        [Fact]
        public void CriticalAlertsNeedAcknowledgementUntilTheyAreAcknowledged()
        {
            var alert = new Alert
            {
                Id = "a1",
                Severity = AlertSeverity.Critical,
                Category = "orders",
                Title = "Unknown live order",
                Message = "could not resolve",
                DedupeKey = "unknown:o1"
            };
            Assert.True(alert.NeedsAcknowledgement);

            alert.AcknowledgedUtc = DateTime.UtcNow;
            Assert.False(alert.NeedsAcknowledgement);
            // Acknowledging is not resolving — the condition is still open.
            Assert.True(alert.Open);

            alert.ResolvedUtc = DateTime.UtcNow;
            Assert.False(alert.Open);
        }

        [Fact]
        public void LowerSeveritiesDoNotDemandAcknowledgement()
        {
            var alert = new Alert
            {
                Id = "a2", Severity = AlertSeverity.High, Category = "data",
                Title = "stale", Message = "stale", DedupeKey = "data:stale"
            };
            Assert.False(alert.NeedsAcknowledgement);
        }

        // ── display data (sparklines, distributions, daily series) ────────────────
        // These feed charts, and a chart that quietly loses a spike or interpolates over a gap is
        // worse than no chart — it is a confident wrong answer.

        [Fact]
        public void DownsamplingKeepsTheSpikeAndTheLatestPrice()
        {
            var series = Enumerable.Repeat(100m, 200).ToList();
            series[57] = 180m;      // a spike an averaging downsample would erase
            series[^1] = 123.45m;   // the latest price, which is read as the mark

            var reduced = WatchlistService.Downsample(series, 20);

            Assert.Equal(20, reduced.Count);
            Assert.Contains(180m, reduced);
            Assert.Equal(123.45m, reduced[^1]);
        }

        [Fact]
        public void DownsamplingLeavesAShortSeriesAlone()
        {
            var series = new List<decimal> { 1m, 2m, 3m };
            Assert.Equal(series, WatchlistService.Downsample(series, 48));
        }

        [Fact]
        public void EveryValueLandsInExactlyOneDistributionBucket()
        {
            var values = new[] { -50m, -12m, -3m, 0m, 1m, 4m, 9m, 22m, 61m, 140m };
            var buckets = PerformanceService.Distribute(values, 5);

            Assert.Equal(5, buckets.Count);
            Assert.Equal(values.Length, buckets.Sum(b => b.Count));
            Assert.Equal(-50m, buckets[0].From);
            Assert.Equal(140m, buckets[^1].To);
        }

        [Fact]
        public void ADistributionWithNoSpreadIsEmptyRatherThanASingleFakeBar()
        {
            Assert.Empty(PerformanceService.Distribute(new[] { 7m, 7m, 7m }, 9));
            Assert.Empty(PerformanceService.Distribute(new[] { 7m }, 9));
        }

        [Fact]
        public void TheDailySeriesShowsQuietDaysAsZeroAndCarriesTheRunningTotal()
        {
            var start = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            var records = new List<JournalRecord>
            {
                ClosedTrade(start.AddHours(9), 100m),
                ClosedTrade(start.AddHours(15), -40m),
                // 2 July has no trades at all.
                ClosedTrade(start.AddDays(2).AddHours(11), 25m)
            };

            var daily = PerformanceService.BuildDaily(records, start, start.AddDays(2));

            Assert.Equal(3, daily.Count);
            Assert.Equal(60m, daily[0].NetPnL);
            Assert.Equal(2, daily[0].Trades);
            Assert.Equal(0m, daily[1].NetPnL);
            Assert.Equal(0, daily[1].Trades);
            // A quiet day holds the running total rather than resetting or interpolating it.
            Assert.Equal(60m, daily[1].Cumulative);
            Assert.Equal(85m, daily[2].Cumulative);
        }

        private static JournalRecord ClosedTrade(DateTime ts, decimal pnl) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Ts = ts,
            InstrumentId = "crypto:BTC/USD",
            Venue = VenueId.Kraken,
            Environment = TradingEnvironment.Paper,
            RealizedPnL = pnl
        };
    }
}
