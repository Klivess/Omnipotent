using Omnipotent.Services.OmniTrader.Analytics;
using Omnipotent.Services.OmniTrader.Api;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Instruments;
using Omnipotent.Services.OmniTrader.Journal;
using Omnipotent.Services.OmniTrader.Ledger;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Ops;
using Omnipotent.Services.OmniTrader.Performance;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Portfolio;
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

        // ── a channel nobody called is not a channel that failed ──────────────────

        [Fact]
        public void AnUnprobedChannelIsUnknownRatherThanDown()
        {
            var never = new ChannelHealth { Channel = "kraken-rest" };
            Assert.Equal(ChannelState.Unknown, never.State);
            Assert.False(never.Probed);
            Assert.False(never.Degraded);

            never.Connected = true;
            never.LastOkUtc = DateTime.UtcNow;
            Assert.Equal(ChannelState.Up, never.State);

            never.Connected = false;
            never.LastErrorUtc = DateTime.UtcNow;
            Assert.Equal(ChannelState.Down, never.State);
            Assert.True(never.Degraded);
        }

        [Fact]
        public void AnUnsupportedChannelIsNeverAnOutage()
        {
            // IG's Lightstreamer is not implemented. Reporting a feature the platform never built as
            // "down" is noise an operator can do nothing about.
            var stream = new ChannelHealth { Channel = "ig-lightstreamer", Unsupported = true };
            Assert.Equal(ChannelState.Unsupported, stream.State);
            Assert.False(stream.Degraded);
        }

        [Fact]
        public void TheOrderPathIsHealthyUntilSomethingActuallyFails()
        {
            var snapshot = new VenueHealthSnapshot
            {
                Venue = VenueId.Kraken,
                Environment = TradingEnvironment.Live,
                Configured = true,
                Channels =
                {
                    new ChannelHealth { Channel = "kraken-rest" },                                  // never called
                    new ChannelHealth { Channel = "kraken-private-rest", Connected = true, LastOkUtc = DateTime.UtcNow }
                }
            };

            // Nothing has failed — an endpoint simply has not been exercised yet. Reporting an
            // outage here is an outage invented from an absence of activity.
            Assert.True(snapshot.OrderPathHealthy);

            snapshot.Channels[0].LastErrorUtc = DateTime.UtcNow;
            Assert.False(snapshot.OrderPathHealthy);
        }

        // ── rejected credentials stop being retried ───────────────────────────────

        [Fact]
        public void RepeatedCredentialRejectionsOpenTheBreaker()
        {
            var breaker = new AuthCircuitBreaker { RejectionsBeforeOpening = 3 };

            breaker.RecordRejection("api-key-invalid");
            breaker.RecordRejection("api-key-invalid");
            Assert.False(breaker.IsOpen);

            breaker.RecordRejection("api-key-invalid");
            Assert.True(breaker.IsOpen);
            Assert.Contains("Reconnect venues", breaker.Reason);

            // Fixing the key and reconnecting is the only thing that can change the outcome.
            breaker.Reset();
            Assert.False(breaker.IsOpen);
        }

        [Fact]
        public void OnlyAuthenticationFailuresOpenTheBreaker()
        {
            // A timeout or a server error is transient; taking the venue offline for those would
            // turn a blip into an outage.
            Assert.True(AuthCircuitBreaker.IsRejection(System.Net.HttpStatusCode.Unauthorized));
            Assert.True(AuthCircuitBreaker.IsRejection(System.Net.HttpStatusCode.Forbidden));
            Assert.False(AuthCircuitBreaker.IsRejection(System.Net.HttpStatusCode.InternalServerError));
            Assert.False(AuthCircuitBreaker.IsRejection(System.Net.HttpStatusCode.TooManyRequests));
            Assert.False(AuthCircuitBreaker.IsRejection(System.Net.HttpStatusCode.RequestTimeout));
        }

        [Fact]
        public void ASuccessClearsTheRejectionCount()
        {
            var breaker = new AuthCircuitBreaker { RejectionsBeforeOpening = 3 };
            breaker.RecordRejection("bad");
            breaker.RecordRejection("bad");
            breaker.RecordSuccess();
            breaker.RecordRejection("bad");
            Assert.False(breaker.IsOpen);
        }

        // ── breaks close themselves ───────────────────────────────────────────────

        [Fact]
        public void ABreakClosesItselfOnceTheConditionIsGone()
        {
            var stale = Break(BreakClassification.ExternalManualActivity);
            var checkedKinds = new[] { BreakKind.Position };

            // The sweep looked at positions and did not find this difference: it has gone away, so
            // the operator should never be asked to explain it.
            Assert.True(ReconciliationService.ShouldRetire(stale, checkedKinds, new HashSet<string>()));

            // Still there — it stays open.
            var stillDetected = new HashSet<string> { ReconciliationService.BreakIdentity(stale) };
            Assert.False(ReconciliationService.ShouldRetire(stale, checkedKinds, stillDetected));
        }

        [Fact]
        public void ABreakSurvivesWhenTheVenueCouldNotBeChecked()
        {
            var positionBreak = Break(BreakClassification.Unexplained);

            // The venue failed to answer, so positions were never evaluated. Not looking is not the
            // same as finding nothing — clearing it here would silently drop a real discrepancy.
            Assert.False(ReconciliationService.ShouldRetire(positionBreak, Array.Empty<BreakKind>(), new HashSet<string>()));
            Assert.False(ReconciliationService.ShouldRetire(positionBreak, new[] { BreakKind.Balance }, new HashSet<string>()));
        }

        // ── venues, feeds and money that is not real ──────────────────────────────

        [Fact]
        public void OnlyLiveAccountsHoldRealMoney()
        {
            Assert.True(PortfolioService.IsRealMoney(TradingEnvironment.Live));
            // The built-in paper trader and a broker demo account are simulations. Counting either
            // toward firm value would report wealth that does not exist.
            Assert.False(PortfolioService.IsRealMoney(TradingEnvironment.Paper));
            Assert.False(PortfolioService.IsRealMoney(TradingEnvironment.Demo));
            Assert.False(PortfolioService.IsRealMoney(TradingEnvironment.Historical));
        }

        // ── data freshness ────────────────────────────────────────────────────────

        /// <summary>
        /// The bug this locks down: a bar series' newest bar is at least one bar old the moment it
        /// closes, but freshness judged it against a flat 15-minute threshold meant for live ticks.
        /// Every instrument on hourly bars was therefore permanently "stale" — and staleness is a
        /// *hard* rule in the risk engine, so it silently blocked every order as well.
        /// </summary>
        [Fact]
        public void AnHourlyBarIsNotStaleForBeingAnHourOld()
        {
            var (stale, _, _, _) = InstrumentMaster.Judge(
                dataAge: TimeSpan.FromMinutes(19),
                observationAge: TimeSpan.Zero,
                cadence: TimeSpan.FromHours(1),
                continuous: true,
                feedThreshold: TimeSpan.FromMinutes(15));

            Assert.False(stale);
        }

        [Fact]
        public void AContinuousMarketThatSkipsBarsIsStale()
        {
            // Crypto never closes, so a gap of several hourly bars is a real fault.
            var (stale, marketClosed, _, dataOld) = InstrumentMaster.Judge(
                dataAge: TimeSpan.FromHours(5),
                observationAge: TimeSpan.Zero,
                cadence: TimeSpan.FromHours(1),
                continuous: true,
                feedThreshold: TimeSpan.FromMinutes(15));

            Assert.True(dataOld);
            Assert.True(stale);
            Assert.False(marketClosed);
        }

        [Fact]
        public void AShutExchangeIsReportedClosedRatherThanStale()
        {
            // The feed answered just now; it simply has no newer bar because the market is shut.
            // Blocking trading on that would block every evening and every weekend.
            var (stale, marketClosed, _, _) = InstrumentMaster.Judge(
                dataAge: TimeSpan.FromHours(16),
                observationAge: TimeSpan.FromSeconds(3),
                cadence: TimeSpan.FromHours(1),
                continuous: false,
                feedThreshold: TimeSpan.FromMinutes(15));

            Assert.False(stale);
            Assert.True(marketClosed);
        }

        [Fact]
        public void AnUnreachableFeedIsStaleWhateverTheMarketIsDoing()
        {
            // The one failure that is always ours: we have not managed to read anything.
            var (stale, marketClosed, feedSilent, _) = InstrumentMaster.Judge(
                dataAge: TimeSpan.FromMinutes(1),
                observationAge: TimeSpan.FromHours(2),
                cadence: TimeSpan.FromHours(1),
                continuous: false,
                feedThreshold: TimeSpan.FromMinutes(15));

            Assert.True(feedSilent);
            Assert.True(stale);
            Assert.False(marketClosed);
        }

        [Fact]
        public void ALiveTickWithNoCadenceStillUsesTheTickThreshold()
        {
            // No cadence means this was a live price, not a bar: 40 minutes old really is stale.
            var (fresh, _, _, _) = InstrumentMaster.Judge(
                TimeSpan.FromMinutes(4), TimeSpan.Zero, null, true, TimeSpan.FromMinutes(15));
            var (stale, _, _, _) = InstrumentMaster.Judge(
                TimeSpan.FromMinutes(40), TimeSpan.Zero, null, true, TimeSpan.FromMinutes(15));

            Assert.False(fresh);
            Assert.True(stale);
        }

        [Fact]
        public void ToleranceAllowsForOpenVersusCloseStampedBars()
        {
            // Providers disagree about whether a bar carries its open or its close time, so one
            // interval of the difference is convention rather than lateness.
            Assert.True(InstrumentMaster.ToleranceFor(TimeSpan.FromHours(1)) > TimeSpan.FromHours(2));
            Assert.True(InstrumentMaster.ToleranceFor(TimeSpan.FromMinutes(15)) > TimeSpan.FromMinutes(30));
        }

        // ── firm value history ────────────────────────────────────────────────────

        /// <summary>
        /// The bug this locks down: firm value used to be assembled by summing per-account broker
        /// snapshots at each timestamp. Because every venue writes its snapshot at its own instant,
        /// each "total" was really one account — and the chart sawtoothed between a demo account's
        /// £10,000 opening balance and a live account's cash. A value point is now the whole firm at
        /// one instant, so a demo balance has nowhere to enter the total.
        /// </summary>
        [Fact]
        public void SimulatedMoneyNeverReachesFirmValue()
        {
            var view = new FirmPortfolioView { ReportingCurrency = "GBP", AsOfUtc = DateTime.UtcNow };
            view.Real.Cash = 11m;
            view.Real.Positions = 1;
            // An IG demo account opens at £10,000 and the built-in paper trader at another 10,000.
            view.Simulated.Cash = 10_000m;
            view.Simulated.InventoryValue = 2_500m;

            var point = PortfolioService.ToValuePoint(view);

            Assert.Equal(11m, point.TotalValue);
            Assert.Equal(11m, point.Cash);
            Assert.Equal(12_500m, point.SimulatedValue);
        }

        [Fact]
        public void ATrendPointIsOneWholeFirmValuation()
        {
            var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
            var series = new List<FirmValuePoint>
            {
                ValuePoint(start, 100m),
                ValuePoint(start.AddMinutes(5), 120m),
                ValuePoint(start.AddMinutes(10), 90m)
            };

            var trend = FirmRoutes.BuildValueTrend(series, 30);

            // Three valuations in, three points out: nothing is grouped, summed or interleaved.
            Assert.Equal(3, trend.Points.Count);
            Assert.Equal(new[] { 100m, 120m, 90m }, trend.Points.Select(p => p.Value));
            Assert.Equal(120m, trend.PeakValue);
            Assert.Equal(90m, trend.TroughValue);
        }

        [Fact]
        public void NoValuationsMeansNoNumbersRatherThanZeroes()
        {
            var trend = FirmRoutes.BuildValueTrend(new List<FirmValuePoint>(), 30);

            // A firm whose value has never been measured is not a firm worth £0.
            Assert.Empty(trend.Points);
            Assert.Null(trend.PeakValue);
            Assert.Null(trend.TroughValue);
            Assert.Null(trend.Change24h);
        }

        [Fact]
        public void TheTwentyFourHourChangeComparesLikeWithLike()
        {
            var now = DateTime.UtcNow;
            var series = new List<FirmValuePoint>
            {
                ValuePoint(now.AddHours(-48), 800m),
                ValuePoint(now.AddHours(-25), 1_000m),
                ValuePoint(now.AddHours(-1), 1_250m)
            };

            var trend = FirmRoutes.BuildValueTrend(series, 30);

            Assert.Equal(250m, trend.Change24h);
            Assert.Equal(25m, trend.ChangePercent24h);
        }

        [Fact]
        public void DownsamplingAlwaysKeepsTheLatestValue()
        {
            var start = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            // Five-minute valuations over 30 days — far more than a chart can draw.
            var series = Enumerable.Range(0, 2_000)
                .Select(i => ValuePoint(start.AddMinutes(5 * i), 1_000m + i))
                .ToList();

            var trend = FirmRoutes.BuildValueTrend(series, 30);

            Assert.True(trend.Points.Count <= 120);
            // The right-hand end of the chart is the number the operator reads as "now".
            Assert.Equal(series[^1].TotalValue, trend.Points[^1].Value);
            Assert.Equal(series[^1].Ts, trend.LastUtc);
        }

        private static FirmValuePoint ValuePoint(DateTime ts, decimal total) => new()
        {
            Ts = ts,
            Currency = "GBP",
            TotalValue = total,
            Cash = total,
            HasRealAccounts = true
        };

        [Theory]
        [InlineData("BTCUSDT", false)]
        [InlineData("ETHGBP", false)]
        [InlineData("AAPL", true)]
        [InlineData("VOD.L", true)]
        [InlineData("^FTSE", true)]
        [InlineData("GBPUSD=X", true)]
        public void SymbolShapeDecidesWhichFeedAnswers(string symbol, bool equityFeed)
        {
            Assert.Equal(equityFeed, MarketDataRouter.UsesEquityFeed(symbol, AssetClass.Unknown));
        }

        [Fact]
        public void AnExplicitAssetClassOverridesTheSymbolShape()
        {
            // "SOL" looks like a ticker but is crypto; the caller knowing that must win.
            Assert.False(MarketDataRouter.UsesEquityFeed("SOL", AssetClass.Crypto));
            Assert.True(MarketDataRouter.UsesEquityFeed("BTCUSD", AssetClass.Equity));
        }

        [Theory]
        [InlineData("AAPL_US_EQ", "AAPL")]
        [InlineData("VUSAl_EQ", "VUSA.L")]
        [InlineData("TSLA", "TSLA")]
        public void Trading212TickersMapOntoMarketDataSymbols(string venueSymbol, string expected)
        {
            Assert.Equal(expected, Trading212VenueAdapter.ToMarketSymbol(venueSymbol));
        }

        // ── Trading 212 wire contract ─────────────────────────────────────────────

        [Fact]
        public void ATrading212KeyPairIsSentAsHttpBasic()
        {
            // Documented scheme: the key is the username and the secret the password.
            string header = Trading212VenueAdapter.BuildAuthorization("key-id", "the-secret");
            Assert.StartsWith("Basic ", header);
            Assert.Equal("key-id:the-secret",
                System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..])));
        }

        [Fact]
        public void AKeyWithNoSecretFallsBackToTheLegacyHeader()
        {
            // Older single-token keys are still accepted by T212; half a Basic credential is not.
            Assert.Equal("legacy-token", Trading212VenueAdapter.BuildAuthorization("legacy-token", null));
            Assert.Equal("legacy-token", Trading212VenueAdapter.BuildAuthorization("legacy-token", "   "));
        }

        [Fact]
        public void PlacingAnOrderIsNeverPacedLikeListingOne()
        {
            // The two share a path prefix but differ by a factor of four in their allowance, and
            // throttling execution to the speed of a report is the bug this guards.
            var place = Trading212VenueAdapter.LimitFor("/equity/orders/market");
            var list = Trading212VenueAdapter.LimitFor("/equity/orders");
            var read = Trading212VenueAdapter.LimitFor("/equity/orders/987654321");

            Assert.NotNull(place); Assert.NotNull(list); Assert.NotNull(read);
            Assert.True(place!.Value.MinInterval < list!.Value.MinInterval);
            Assert.NotEqual(list.Value.Bucket, place.Value.Bucket);
            // Resolving an ambiguous submission walks several ids; five seconds each would time out
            // the reconciliation before it finished.
            Assert.True(read!.Value.MinInterval <= TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void TheOrderSideComesFromTheVenueRatherThanTheSignOfTheQuantity()
        {
            // T212 takes a negative quantity to mean "sell" on the way *in*, but reports the side
            // explicitly on the way out — and reports the quantity unsigned. Inferring from the sign
            // would read every sell back as a buy.
            var sell = Newtonsoft.Json.Linq.JObject.Parse(
                @"{ 'id': 1, 'quantity': 10, 'side': 'SELL', 'status': 'FILLED',
                    'filledQuantity': 10, 'filledValue': 2500,
                    'instrument': { 'ticker': 'AAPL_US_EQ' } }".Replace('\'', '"'));

            var snapshot = Trading212VenueAdapter.ToOrderSnapshot(sell);

            Assert.NotNull(snapshot);
            Assert.Equal(OrderSide.Sell, snapshot!.Side);
            Assert.Equal("AAPL_US_EQ", snapshot.VenueSymbol);
            // There is no average-fill field: it is the value over the quantity.
            Assert.Equal(250m, snapshot.AverageFillPrice);
        }

        [Theory]
        [InlineData("NEW")]
        [InlineData("CONFIRMED")]
        [InlineData("CANCELLING")]
        [InlineData("REPLACING")]
        public void AnOrderStillOnTheBookIsOpenWhateverItIsCalled(string status)
        {
            // T212 has eleven statuses. Everything that is not a terminal outcome is still live, and
            // must not be read as resolved — an order treated as gone is an order that gets re-sent.
            var order = Newtonsoft.Json.Linq.JObject.Parse(
                $"{{ 'id': 1, 'quantity': 1, 'status': '{status}' }}".Replace('\'', '"'));

            Assert.Equal(OrderStatus.Open, Trading212VenueAdapter.ToOrderSnapshot(order)!.Status);
        }

        [Fact]
        public void AnExhaustedQuotaIsNotABadCredential()
        {
            // IG answers a spent request allowance with 403 — the same status as a wrong key. Opening
            // the auth breaker there takes a working venue offline for the cooldown, and no amount of
            // fixing the credential would have helped.
            Assert.False(AuthCircuitBreaker.IsRejection(
                System.Net.HttpStatusCode.Forbidden, "error.public-api.exceeded-account-allowance"));
            Assert.True(AuthCircuitBreaker.IsRejection(
                System.Net.HttpStatusCode.Forbidden, "error.security.api-key-invalid"));
            Assert.True(AuthCircuitBreaker.IsRejection(
                System.Net.HttpStatusCode.Unauthorized, null));
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
