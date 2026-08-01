using Newtonsoft.Json;
using Omnipotent.Profiles;
using Omnipotent.Services.OmniTrader.Analytics;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Journal;
using Omnipotent.Services.OmniTrader.Ledger;
using Omnipotent.Services.OmniTrader.Ops;
using Omnipotent.Services.OmniTrader.OrderFlow;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Research;
using Omnipotent.Services.OmniTrader.Risk;
using Omnipotent.Services.OmniTrader.Venues;
using System.Globalization;
using System.Net;
// `KliveAPI` alone binds to the namespace here, not the class, so the nested request type needs the
// full path. Aliasing it keeps the handler signatures readable.
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniTrader.Api
{
    /// <summary>
    /// HTTP surface for the firm layer, organised by operating function rather than by broker:
    /// command centre, markets, portfolio, risk, execution, research, performance, journal, systems.
    ///
    /// Reads are <c>Guest</c>. Anything that changes exposure, authority or emergency state is
    /// <c>Klives</c>, and the genuinely dangerous actions additionally require a typed confirmation
    /// token so a mis-click cannot arm live trading or unwind a book.
    /// </summary>
    public sealed class FirmRoutes
    {
        private readonly OmniTrader parent;
        private FirmContext Firm => parent.Firm;

        public FirmRoutes(OmniTrader parent) => this.parent = parent;

        public async Task RegisterAsync()
        {
            await RegisterCommandCentreAsync();
            await RegisterMarketsAsync();
            await RegisterPortfolioAsync();
            await RegisterRiskAsync();
            await RegisterExecutionAsync();
            await RegisterResearchAsync();
            await RegisterPerformanceAsync();
            await RegisterJournalAsync();
            await RegisterSystemsAsync();
        }

        // ── command centre ────────────────────────────────────────────────────────

        private async Task RegisterCommandCentreAsync()
        {
            await Get("/api/omnitrader/firm/overview", async req =>
            {
                var portfolio = await Firm.Portfolio.BuildAsync();
                var health = await Firm.Health.EvaluateAsync();
                var alerts = await Firm.Alerts.ListAsync(openOnly: true, limit: 50);
                var awaiting = await Firm.OrderRepo.ListAwaitingApprovalAsync();
                var unknown = await Firm.OrderRepo.ListUnknownAsync();
                var breaks = await Firm.Reconciliation.ListOpenBreaksAsync();
                var deployments = await parent.DeploymentRepo.ListAllAsync();

                // The command centre needs a baseline, not just a level: a firm value with no
                // reference point cannot be read as good, bad or unremarkable.
                int trendDays = ParseInt(req.userParameters.Get("trendDays"), 30, 1, 365);
                var trend = BuildValueTrend(
                    await Firm.Portfolio.ValueSeriesAsync(DateTime.UtcNow.AddDays(-trendDays)), trendDays);

                await Json(req, new
                {
                    AsOfUtc = DateTime.UtcNow,
                    Portfolio = new
                    {
                        portfolio.ReportingCurrency,
                        portfolio.TotalValue,
                        portfolio.Cash,
                        portfolio.InventoryValue,
                        portfolio.DerivativeEquity,
                        portfolio.DerivativeNotional,
                        portfolio.GrossExposure,
                        portfolio.NetExposure,
                        portfolio.UnrealizedPnL,
                        portfolio.RealizedPnLToday,
                        portfolio.CostsToday,
                        Positions = portfolio.Lines.Count,
                        portfolio.Warnings
                    },
                    Health = new { health.TradingPermitted, health.Summary, health.Blockers },
                    Controls = new
                    {
                        Firm.Emergency.SafeModeActive,
                        Firm.Emergency.SafeModeReason,
                        Firm.Emergency.SafeModeSinceUtc,
                        KillSwitches = Firm.Emergency.Active.Select(k => new { k.Key, k.Reason, k.TriggeredBy, k.TriggeredUtc, k.Automatic })
                    },
                    Exceptions = new
                    {
                        AwaitingApproval = awaiting.Count,
                        UnknownOrders = unknown.Count,
                        MaterialBreaks = breaks.Count(b => b.Material),
                        CriticalAlerts = alerts.Count(a => a.Severity == AlertSeverity.Critical),
                        UnacknowledgedCritical = alerts.Count(a => a.NeedsAcknowledgement)
                    },
                    Alerts = alerts.Take(20).Select(AlertDto),
                    Venues = Firm.Venues.All.Select(v => new
                    {
                        Venue = v.Venue.ToString(),
                        Environment = v.Environment.ToString(),
                        v.IsConfigured,
                        v.Capabilities.DisplayName,
                        Exposure = v.Capabilities.Exposure.ToString(),
                        OrderPathHealthy = SafeOrderPath(v)
                    }),
                    Strategies = new
                    {
                        Total = parent.StrategyRegistry.All.Count,
                        Running = deployments.Count(d => d.Status == DeploymentStatus.Running),
                        Deployments = deployments.Count
                    },
                    Trend = trend
                });
            });

            await Get("/api/omnitrader/firm/environments", async req =>
            {
                var accounts = await Firm.Accounts.ListAsync();
                await Json(req, new
                {
                    Environments = Enum.GetNames<TradingEnvironment>(),
                    Accounts = accounts.Select(a => new
                    {
                        a.Id,
                        Venue = a.Venue.ToString(),
                        Environment = a.Environment.ToString(),
                        a.DisplayName,
                        a.BaseCurrency,
                        Authority = a.Authority.ToString(),
                        a.Enabled
                    }),
                    Capabilities = Firm.Venues.Capabilities.Select(c => new
                    {
                        Venue = c.Venue.ToString(),
                        c.DisplayName,
                        Exposure = c.Exposure.ToString(),
                        AssetClasses = c.AssetClasses.Select(a => a.ToString()),
                        c.SupportsShort,
                        c.SupportsLeverage,
                        c.MaxLeverage,
                        c.SupportsAttachedProtection,
                        c.SupportsStreamingPrices,
                        c.SupportsStreamingAccount,
                        c.SupportsDemoEnvironment,
                        OrderTypes = c.OrderTypes.Select(o => o.ToString()),
                        c.Limitations
                    }),
                    Firm.Portfolio.ReportingCurrency
                });
            });
        }

        // ── markets ───────────────────────────────────────────────────────────────

        private async Task RegisterMarketsAsync()
        {
            await Get("/api/omnitrader/firm/markets/watchlists", async req =>
            {
                var lists = await Firm.Watchlists.ListAsync();
                await Json(req, lists);
            });

            await Get("/api/omnitrader/firm/markets", async req =>
            {
                string? watchlistId = req.userParameters.Get("watchlist");
                string? symbols = req.userParameters.Get("instruments");
                var interval = ParseInterval(req.userParameters.Get("interval")) ?? TimeInterval.OneHour;

                List<string> ids;
                if (!string.IsNullOrWhiteSpace(symbols))
                    ids = symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                else
                {
                    var lists = await Firm.Watchlists.ListAsync();
                    var chosen = lists.FirstOrDefault(w => w.Id == watchlistId) ?? lists.FirstOrDefault();
                    ids = chosen?.InstrumentIds ?? new List<string>();
                }

                var rows = await Firm.Watchlists.EvaluateAsync(ids, interval);
                var breadth = await Firm.Watchlists.BreadthAsync(ids);
                await Json(req, new
                {
                    Interval = interval.ToString(),
                    Rows = rows,
                    Breadth = breadth,
                    Session = new
                    {
                        UtcNow = DateTime.UtcNow,
                        // Crypto never closes; the equity/index session drives the IG side.
                        CryptoOpen = true,
                        LondonOpen = IsLondonSessionOpen(DateTime.UtcNow)
                    }
                });
            });

            await Get("/api/omnitrader/firm/instruments", async req =>
            {
                string? search = req.userParameters.Get("q");
                var results = Firm.Instruments.Search(search ?? "", 200);
                await Json(req, results.Select(i => new
                {
                    i.Id,
                    i.DisplayName,
                    AssetClass = i.AssetClass.ToString(),
                    i.BaseAsset,
                    i.QuoteCurrency,
                    Exposure = i.Exposure.ToString(),
                    Venues = i.Venues.Select(v => new
                    {
                        Venue = v.Venue.ToString(),
                        v.VenueSymbol,
                        v.TickSize,
                        v.QuantityStep,
                        v.MinQuantity,
                        v.MaxQuantity,
                        v.MarginFactor,
                        v.Tradeable,
                        v.TradingStatus
                    }),
                    Freshness = Firm.Instruments.GetFreshness(i.Id)
                }));
            });

            await Post("/api/omnitrader/firm/instruments/refresh", async req =>
            {
                int added = await Firm.Instruments.RefreshFromVenuesAsync();
                await Firm.Audit.AppendAsync(Actor(req), "instruments.refreshed", "instrument-master", $"{added} new");
                await Json(req, new { Added = added, Total = Firm.Instruments.Count, Firm.Instruments.LastRefreshUtc });
            });

            await Post("/api/omnitrader/firm/markets/watchlist/save", async req =>
            {
                var dto = Body<WatchlistDto>(req);
                if (dto == null || string.IsNullOrWhiteSpace(dto.Name)) { await Bad(req, "name required"); return; }
                var watchlist = new Watchlist
                {
                    Id = string.IsNullOrWhiteSpace(dto.Id) ? Guid.NewGuid().ToString("N") : dto.Id!,
                    Name = dto.Name!
                };
                watchlist.InstrumentIds.AddRange(dto.InstrumentIds ?? new List<string>());
                await Firm.Watchlists.SaveAsync(watchlist);
                await Json(req, watchlist);
            });

            await Post("/api/omnitrader/firm/markets/watchlist/delete", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                await Firm.Watchlists.DeleteAsync(id);
                await Json(req, new { Deleted = true });
            });
        }

        // ── portfolio ─────────────────────────────────────────────────────────────

        private async Task RegisterPortfolioAsync()
        {
            await Get("/api/omnitrader/firm/portfolio", async req =>
            {
                var view = await Firm.Portfolio.BuildAsync();
                await Json(req, view);
            });

            await Get("/api/omnitrader/firm/portfolio/value-series", async req =>
            {
                var from = ParseUtc(req.userParameters.Get("from")) ?? DateTime.UtcNow.AddDays(-30);
                var series = await Firm.Portfolio.ValueSeriesAsync(from);
                await Json(req, series.Select(s => new { s.Ts, Venue = s.Venue.ToString(), s.Value }));
            });

            await Get("/api/omnitrader/firm/ledger", async req =>
            {
                var from = ParseUtc(req.userParameters.Get("from"));
                string? account = req.userParameters.Get("account");
                int limit = ParseInt(req.userParameters.Get("limit"), 200, 1, 2000);
                var entries = await Firm.LedgerRepo.ListAsync(from, account, limit);
                await Json(req, entries);
            });

            await Get("/api/omnitrader/firm/reconciliation", async req =>
            {
                var breaks = await Firm.Reconciliation.ListOpenBreaksAsync();
                var runs = await Firm.Reconciliation.ListRunsAsync(25);
                await Json(req, new
                {
                    Firm.Reconciliation.LastRunUtc,
                    OpenBreaks = breaks,
                    MaterialBreaks = breaks.Count(b => b.Material),
                    Runs = runs.Select(r => new
                    {
                        r.Id,
                        Venue = r.Venue.ToString(),
                        Environment = r.Environment.ToString(),
                        r.Trigger,
                        r.StartedUtc,
                        r.FinishedUtc,
                        r.Checked,
                        Breaks = r.Breaks.Count,
                        r.MaterialBreaks,
                        r.Error
                    })
                });
            });

            await Post("/api/omnitrader/firm/reconciliation/run", async req =>
            {
                var runs = await Firm.Reconciliation.ReconcileAllAsync("manual");
                await Firm.Audit.AppendAsync(Actor(req), "reconciliation.manual_run", "firm",
                    $"{runs.Sum(r => r.MaterialBreaks)} material break(s)");
                await Json(req, new
                {
                    Runs = runs.Count,
                    Checked = runs.Sum(r => r.Checked),
                    MaterialBreaks = runs.Sum(r => r.MaterialBreaks)
                });
            });

            await Post("/api/omnitrader/firm/reconciliation/resolve", async req =>
            {
                string? id = req.userParameters.Get("id");
                string? resolution = req.userParameters.Get("resolution");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                await Firm.Reconciliation.ResolveBreakAsync(id, resolution ?? "resolved", Actor(req));
                await Firm.Audit.AppendAsync(Actor(req), "reconciliation.break_resolved", id, resolution);
                await Json(req, new { Resolved = true });
            });
        }

        // ── risk ──────────────────────────────────────────────────────────────────

        private async Task RegisterRiskAsync()
        {
            await Get("/api/omnitrader/firm/risk", async req =>
            {
                var state = await Firm.Portfolio.BuildRiskStateAsync();
                var operations = await Firm.Orders.BuildOperationalStateAsync(null);
                var decisions = await Firm.RiskRepo.ListRecentAsync(50);
                await Json(req, new
                {
                    Limits = Firm.Limits,
                    Portfolio = new
                    {
                        state.GrossExposure,
                        state.NetExposure,
                        state.Equity,
                        state.PeakEquity,
                        state.DailyRealizedPnL,
                        state.DrawdownPercent,
                        state.AvailableFunds,
                        ExposureByInstrument = state.ExposureByInstrument,
                        ExposureByVenue = state.ExposureByVenue.ToDictionary(k => k.Key.ToString(), v => v.Value),
                        state.DailyPnLByStrategy,
                        state.FreeInventory
                    },
                    Operations = operations,
                    Controls = new
                    {
                        Firm.Emergency.SafeModeActive,
                        Firm.Emergency.SafeModeReason,
                        Firm.Emergency.SafeModeSinceUtc,
                        Firm.Emergency.SafeModeTriggeredBy,
                        KillSwitches = Firm.Emergency.Active
                    },
                    // Utilisation is what an operator actually reads: how close each limit is to biting.
                    Utilisation = new
                    {
                        Gross = Pct(state.GrossExposure, Firm.Limits.MaxGrossExposure),
                        Net = Pct(Math.Abs(state.NetExposure), Firm.Limits.MaxNetExposure),
                        DailyLoss = Pct(Math.Abs(Math.Min(0m, state.DailyRealizedPnL)), Firm.Limits.MaxFirmDailyLoss),
                        Drawdown = Pct(state.DrawdownPercent, Firm.Limits.MaxDrawdownPercent)
                    },
                    RecentDecisions = decisions.Select(d => new
                    {
                        d.Id,
                        d.ProposalId,
                        Verdict = d.Verdict.ToString(),
                        d.DecidedUtc,
                        d.Summary,
                        Failures = d.Failures.Select(f => new { Layer = f.Layer.ToString(), f.Rule, Severity = f.Severity.ToString(), f.Detail, f.Observed, f.Limit })
                    })
                });
            });

            await Post("/api/omnitrader/firm/risk/limits", async req =>
            {
                var dto = Body<RiskLimits>(req);
                if (dto == null) { await Bad(req, "invalid limits body"); return; }
                await Firm.UpdateLimitsAsync(dto);
                await Firm.Audit.AppendAsync(Actor(req), "risk.limits_changed", "firm", null, dto);
                await Json(req, Firm.Limits);
            });

            await Post("/api/omnitrader/firm/risk/safe-mode", async req =>
            {
                bool enable = string.Equals(req.userParameters.Get("enable"), "true", StringComparison.OrdinalIgnoreCase);
                string reason = req.userParameters.Get("reason") ?? "manual";
                if (enable) Firm.Emergency.EnterSafeMode(reason, Actor(req));
                else Firm.Emergency.ExitSafeMode(Actor(req));
                await Firm.Audit.AppendAsync(Actor(req), enable ? "risk.safe_mode_entered" : "risk.safe_mode_cleared", "firm", reason);
                await Json(req, new { Firm.Emergency.SafeModeActive, Firm.Emergency.SafeModeReason });
            });

            await Post("/api/omnitrader/firm/risk/killswitch", async req =>
            {
                var dto = Body<KillSwitchDto>(req);
                if (dto == null) { await Bad(req, "invalid body"); return; }
                if (!Enum.TryParse<KillScopeKind>(dto.Kind, true, out var kind)) { await Bad(req, "unknown scope kind"); return; }

                if (dto.Release)
                {
                    bool released = Firm.Emergency.Release(kind, dto.Scope ?? "", Actor(req));
                    await Firm.Audit.AppendAsync(Actor(req), "risk.killswitch_released", $"{kind}:{dto.Scope}", dto.Reason);
                    await Json(req, new { Released = released, Active = Firm.Emergency.Active });
                    return;
                }

                Firm.Emergency.Engage(new KillSwitch
                {
                    Kind = kind,
                    Scope = dto.Scope ?? "",
                    Reason = dto.Reason ?? "engaged from the risk page",
                    TriggeredBy = Actor(req)
                });
                await Firm.Audit.AppendAsync(Actor(req), "risk.killswitch_engaged", $"{kind}:{dto.Scope}", dto.Reason);
                await Json(req, new { Engaged = true, Active = Firm.Emergency.Active });
            });

            // Reducing exposure is deliberately separate from disabling automation, and previews what
            // it will do before it does it.
            await Post("/api/omnitrader/firm/risk/reduce/preview", async req =>
            {
                var view = await Firm.Portfolio.BuildAsync();
                string? venueFilter = req.userParameters.Get("venue");
                var affected = view.Lines
                    .Where(l => string.IsNullOrWhiteSpace(venueFilter) || l.Venue.ToString().Equals(venueFilter, StringComparison.OrdinalIgnoreCase))
                    .Where(l => l.Quantity != 0m)
                    .ToList();

                await Json(req, new
                {
                    Positions = affected.Select(l => new
                    {
                        l.InstrumentId,
                        l.DisplayName,
                        Venue = l.Venue.ToString(),
                        Environment = l.Environment.ToString(),
                        Exposure = l.Exposure.ToString(),
                        l.Quantity,
                        ClosingSide = l.Quantity > 0 ? "Sell" : "Buy",
                        l.MarkPrice,
                        l.Notional,
                        l.UnrealizedPnL
                    }),
                    TotalNotional = affected.Sum(l => l.Notional),
                    ExpectedRealized = affected.Sum(l => l.UnrealizedPnL),
                    ConfirmToken = ReduceToken(affected.Count, affected.Sum(l => l.Notional)),
                    Warning = "This submits real closing orders through the risk engine. Review each line before confirming."
                });
            });

            await Post("/api/omnitrader/firm/risk/reduce/execute", async req =>
            {
                var view = await Firm.Portfolio.BuildAsync();
                string? venueFilter = req.userParameters.Get("venue");
                string? token = req.userParameters.Get("confirm");
                var affected = view.Lines
                    .Where(l => string.IsNullOrWhiteSpace(venueFilter) || l.Venue.ToString().Equals(venueFilter, StringComparison.OrdinalIgnoreCase))
                    .Where(l => l.Quantity != 0m)
                    .ToList();

                // The token is derived from the exact preview the operator saw; if the book moved, the
                // token no longer matches and the operator must look again.
                string expected = ReduceToken(affected.Count, affected.Sum(l => l.Notional));
                if (token != expected)
                {
                    await Bad(req, "confirmation token does not match the current book — re-run the preview");
                    return;
                }

                var results = new List<object>();
                foreach (var line in affected)
                {
                    var proposal = new TradeProposal
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        InstrumentId = line.InstrumentId,
                        Venue = line.Venue,
                        Environment = line.Environment,
                        AccountId = $"{line.Venue}".ToLowerInvariant(),
                        Side = line.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy,
                        Type = OrderType.Market,
                        Quantity = Math.Abs(line.Quantity),
                        Authority = ExecutionAuthority.Automated,
                        DecisionPrice = line.MarkPrice ?? line.AveragePrice,
                        Rationale = "Emergency exposure reduction",
                        ExpiresUtc = DateTime.UtcNow.AddMinutes(2)
                    };
                    var result = await Firm.Orders.SubmitProposalAsync(proposal, Actor(req));
                    results.Add(new
                    {
                        line.InstrumentId,
                        Submitted = result.Submitted,
                        State = result.Order?.State.ToString(),
                        Verdict = result.Decision.Verdict.ToString(),
                        result.Message
                    });
                }
                await Firm.Audit.AppendAsync(Actor(req), "risk.exposure_reduced", venueFilter ?? "firm",
                    $"{affected.Count} position(s)", results);
                await Json(req, new { Requested = affected.Count, Results = results });
            });
        }

        // ── execution ─────────────────────────────────────────────────────────────

        private async Task RegisterExecutionAsync()
        {
            await Get("/api/omnitrader/firm/orders", async req =>
            {
                string? state = req.userParameters.Get("state");
                string? search = req.userParameters.Get("q");
                string? venue = req.userParameters.Get("venue");
                string? environment = req.userParameters.Get("environment");
                string? strategy = req.userParameters.Get("strategy");
                var since = ParseUtc(req.userParameters.Get("from"));
                int limit = ParseInt(req.userParameters.Get("limit"), 200, 1, 1000);
                int offset = ParseInt(req.userParameters.Get("offset"), 0, 0, 100_000);

                // The blotter is read as evidence, so the counts must be honest: the scan is over the
                // whole recent set and the response says how much of it the page is showing.
                var scanned = await Firm.OrderRepo.ListRecentAsync(2000);

                var filtered = scanned.AsEnumerable();
                if (!string.IsNullOrWhiteSpace(state))
                {
                    var states = state.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(s => Enum.TryParse<FirmOrderState>(s, true, out var v) ? v : (FirmOrderState?)null)
                        .Where(v => v.HasValue).Select(v => v!.Value).ToHashSet();
                    if (states.Count > 0) filtered = filtered.Where(o => states.Contains(o.State));
                }
                if (!string.IsNullOrWhiteSpace(venue))
                    filtered = filtered.Where(o => o.Venue.ToString().Equals(venue, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(environment))
                    filtered = filtered.Where(o => o.Environment.ToString().Equals(environment, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(strategy))
                    filtered = filtered.Where(o => string.Equals(o.StrategyId ?? "manual", strategy, StringComparison.OrdinalIgnoreCase));
                if (since.HasValue)
                    filtered = filtered.Where(o => o.CreatedUtc >= since.Value);
                if (!string.IsNullOrWhiteSpace(search))
                    filtered = filtered.Where(o => Matches(search, o.InstrumentId, o.ClientReference, o.VenueSymbol,
                        o.VenueOrderId, o.StrategyId, o.Id, o.Error));

                var rows = filtered.ToList();
                await Json(req, new
                {
                    Rows = rows.Skip(offset).Take(limit).Select(OrderDto),
                    Filtered = rows.Count,
                    Total = scanned.Count,
                    Returned = Math.Max(0, Math.Min(limit, rows.Count - offset)),
                    Offset = offset,
                    // Facets let the filter chips show what selecting them would actually yield,
                    // rather than offering states with nothing behind them.
                    StateCounts = scanned.GroupBy(o => o.State.ToString())
                                         .ToDictionary(g => g.Key, g => g.Count()),
                    Venues = scanned.Select(o => o.Venue.ToString()).Distinct().OrderBy(v => v),
                    Strategies = scanned.Select(o => o.StrategyId ?? "manual").Distinct().OrderBy(s => s)
                });
            });

            await Get("/api/omnitrader/firm/order", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                var order = await Firm.OrderRepo.GetAsync(id);
                if (order == null) { await NotFound(req); return; }
                var decision = await Firm.RiskRepo.GetAsync(order.RiskDecisionId);
                var proposal = await Firm.RiskRepo.GetProposalAsync(order.ProposalId);
                await Json(req, new
                {
                    Order = OrderDto(order),
                    History = order.History.Select(h => new { From = h.From.ToString(), To = h.To.ToString(), h.AtUtc, h.Actor, h.Reason }),
                    Decision = decision == null ? null : new
                    {
                        decision.Id,
                        Verdict = decision.Verdict.ToString(),
                        decision.DecidedUtc,
                        decision.ProjectedGrossExposure,
                        decision.ProjectedNetExposure,
                        Rules = decision.Rules.Select(r => new { Layer = r.Layer.ToString(), r.Rule, Severity = r.Severity.ToString(), r.Detail, r.Observed, r.Limit })
                    },
                    Proposal = proposal
                });
            });

            await Get("/api/omnitrader/firm/ticket", async req =>
            {
                // The order ticket is capability-driven: the UI renders exactly what the venue can do
                // and shows a reason next to everything it cannot.
                string? instrumentId = req.userParameters.Get("instrument");
                string? venueRaw = req.userParameters.Get("venue");
                if (string.IsNullOrWhiteSpace(instrumentId)) { await Bad(req, "instrument required"); return; }

                var instrument = Firm.Instruments.Resolve(instrumentId);
                if (instrument == null) { await NotFound(req); return; }

                var venue = Enum.TryParse<VenueId>(venueRaw, true, out var v) ? v
                          : instrument.Venues.FirstOrDefault()?.Venue ?? VenueId.Internal;
                var adapter = Firm.Venues.All.FirstOrDefault(a => a.Venue == venue);
                var mapping = instrument.MappingFor(venue);

                decimal mark = await Firm.Portfolio.GetMarkAsync(instrument.Id, venue);
                var freshness = Firm.Instruments.GetFreshness(instrument.Id);
                var riskState = await Firm.Portfolio.BuildRiskStateAsync();

                await Json(req, new
                {
                    Instrument = new
                    {
                        instrument.Id,
                        instrument.DisplayName,
                        AssetClass = instrument.AssetClass.ToString(),
                        instrument.BaseAsset,
                        instrument.QuoteCurrency,
                        Exposure = instrument.Exposure.ToString()
                    },
                    Venue = venue.ToString(),
                    VenueSymbol = mapping?.VenueSymbol ?? Firm.Instruments.VenueSymbolFor(instrument.Id, venue),
                    Capabilities = adapter?.Capabilities,
                    DealingRules = mapping,
                    Mark = mark,
                    Freshness = freshness,
                    Limits = Firm.Limits,
                    FreeInventory = riskState.FreeInventory.TryGetValue(instrument.BaseAsset, out var free) ? free : 0m,
                    riskState.AvailableFunds,
                    Accounts = (await Firm.Accounts.ListAsync())
                        .Where(a => a.Venue == venue)
                        .Select(a => new { a.Id, a.DisplayName, Environment = a.Environment.ToString(), Authority = a.Authority.ToString() })
                });
            });

            await Post("/api/omnitrader/firm/order/propose", async req =>
            {
                var dto = Body<ProposeOrderDto>(req);
                if (dto == null) { await Bad(req, "invalid body"); return; }
                try
                {
                    var proposal = dto.ToProposal();
                    // A manual ticket carries the live mark as its decision price when the caller does
                    // not supply one, so slippage is measured against something real.
                    if (proposal.DecisionPrice <= 0m)
                    {
                        decimal mark = await Firm.Portfolio.GetMarkAsync(proposal.InstrumentId, proposal.Venue);
                        proposal = dto.ToProposal(mark);
                    }
                    var result = await Firm.Orders.SubmitProposalAsync(proposal, Actor(req));
                    await Json(req, new
                    {
                        Order = result.Order == null ? null : OrderDto(result.Order),
                        Decision = new
                        {
                            result.Decision.Id,
                            Verdict = result.Decision.Verdict.ToString(),
                            result.Decision.Summary,
                            result.Decision.ProjectedGrossExposure,
                            result.Decision.ProjectedNetExposure,
                            Rules = result.Decision.Rules.Select(r => new { Layer = r.Layer.ToString(), r.Rule, Severity = r.Severity.ToString(), r.Detail, r.Observed, r.Limit })
                        },
                        result.Submitted,
                        result.AwaitingApproval,
                        result.Blocked,
                        result.Message
                    });
                }
                catch (Exception ex)
                {
                    await parent.ServiceLogError(ex, "propose order failed");
                    await Bad(req, ex.Message);
                }
            });

            await Post("/api/omnitrader/firm/order/approve", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                var order = await Firm.Orders.ApproveAsync(id, Actor(req));
                if (order == null) { await NotFound(req); return; }
                await Json(req, OrderDto(order));
            });

            await Post("/api/omnitrader/firm/order/reject", async req =>
            {
                string? id = req.userParameters.Get("id");
                string reason = req.userParameters.Get("reason") ?? "rejected by operator";
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                var order = await Firm.Orders.RejectAsync(id, Actor(req), reason);
                if (order == null) { await NotFound(req); return; }
                await Json(req, OrderDto(order));
            });

            await Post("/api/omnitrader/firm/order/cancel", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                bool ok = await Firm.Orders.CancelAsync(id, Actor(req));
                await Json(req, new { Cancelled = ok });
            });

            await Post("/api/omnitrader/firm/orders/reconcile", async req =>
            {
                int count = await Firm.Orders.ReconcileOutstandingAsync();
                await Firm.Audit.AppendAsync(Actor(req), "orders.reconciled", "firm", $"{count} order(s)");
                var unknown = await Firm.OrderRepo.ListUnknownAsync();
                await Json(req, new { Reconciled = count, StillUnknown = unknown.Count });
            });
        }

        // ── research ──────────────────────────────────────────────────────────────

        private async Task RegisterResearchAsync()
        {
            await Get("/api/omnitrader/firm/experiments", async req =>
            {
                var experiments = await Firm.Research.ListAsync();
                await Json(req, experiments);
            });

            await Post("/api/omnitrader/firm/experiment/create", async req =>
            {
                var dto = Body<ExperimentDto>(req);
                if (dto == null || string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.StrategyClass))
                { await Bad(req, "name and strategyClass required"); return; }
                var experiment = await Firm.Research.CreateAsync(dto.Name!, dto.StrategyClass!, dto.Hypothesis ?? "", dto.Parameters);
                await Json(req, experiment);
            });

            await Post("/api/omnitrader/firm/experiment/attach", async req =>
            {
                string? id = req.userParameters.Get("id");
                string? jobId = req.userParameters.Get("job");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(jobId)) { await Bad(req, "id and job required"); return; }
                var experiment = await Firm.Research.AttachJobAsync(id, jobId);
                if (experiment == null) { await NotFound(req); return; }
                await Json(req, experiment);
            });

            await Post("/api/omnitrader/firm/experiment/update", async req =>
            {
                string? id = req.userParameters.Get("id");
                var dto = Body<ExperimentDto>(req);
                if (string.IsNullOrWhiteSpace(id) || dto == null) { await Bad(req, "id and body required"); return; }
                ExperimentStatus? status = Enum.TryParse<ExperimentStatus>(dto.Status, true, out var s) ? s : null;
                var experiment = await Firm.Research.UpdateAsync(id, dto.Name, dto.Hypothesis, dto.Notes, status);
                if (experiment == null) { await NotFound(req); return; }
                await Json(req, experiment);
            });

            await Get("/api/omnitrader/firm/strategy-versions", async req =>
            {
                string? strategyClass = req.userParameters.Get("strategy");
                var versions = await Firm.Research.ListVersionsAsync(strategyClass);
                await Json(req, versions.Select(v => new
                {
                    v.Id,
                    v.StrategyClass,
                    v.Version,
                    v.Status,
                    Authority = v.Authority.ToString(),
                    v.CreatedUtc,
                    v.ApprovedBy,
                    v.ApprovedUtc,
                    v.Parameters,
                    v.Notes
                }));
            });

            await Post("/api/omnitrader/firm/strategy-version/create", async req =>
            {
                var dto = Body<StrategyVersionDto>(req);
                if (dto == null || string.IsNullOrWhiteSpace(dto.StrategyClass)) { await Bad(req, "strategyClass required"); return; }
                var record = await Firm.Research.CreateVersionAsync(dto.StrategyClass!, dto.Parameters, dto.Notes);
                await Firm.Audit.AppendAsync(Actor(req), "strategy.version_created", record.Id, dto.Notes);
                await Json(req, record);
            });

            await Get("/api/omnitrader/firm/promotion/assess", async req =>
            {
                string? strategyClass = req.userParameters.Get("strategy");
                int version = ParseInt(req.userParameters.Get("version"), 1, 1, 10_000);
                var requested = Enum.TryParse<ExecutionAuthority>(req.userParameters.Get("authority"), true, out var a)
                    ? a : ExecutionAuthority.Paper;
                if (string.IsNullOrWhiteSpace(strategyClass)) { await Bad(req, "strategy required"); return; }
                var assessment = await Firm.Research.AssessPromotionAsync(strategyClass, version, requested);
                await Json(req, new
                {
                    assessment.StrategyClass,
                    assessment.Version,
                    CurrentAuthority = assessment.CurrentAuthority.ToString(),
                    RequestedAuthority = assessment.RequestedAuthority.ToString(),
                    assessment.Eligible,
                    assessment.Summary,
                    assessment.Requirements
                });
            });

            await Post("/api/omnitrader/firm/promotion/promote", async req =>
            {
                string? strategyClass = req.userParameters.Get("strategy");
                int version = ParseInt(req.userParameters.Get("version"), 1, 1, 10_000);
                var requested = Enum.TryParse<ExecutionAuthority>(req.userParameters.Get("authority"), true, out var a)
                    ? a : ExecutionAuthority.Paper;
                if (string.IsNullOrWhiteSpace(strategyClass)) { await Bad(req, "strategy required"); return; }

                var (promoted, assessment) = await Firm.Research.PromoteAsync(strategyClass, version, requested, Actor(req));
                await Firm.Audit.AppendAsync(Actor(req), promoted ? "strategy.promoted" : "strategy.promotion_refused",
                    $"{strategyClass}:v{version}", assessment.Summary);
                await Json(req, new
                {
                    Promoted = promoted,
                    assessment.Eligible,
                    assessment.Summary,
                    assessment.Requirements,
                    CurrentAuthority = assessment.CurrentAuthority.ToString(),
                    RequestedAuthority = assessment.RequestedAuthority.ToString()
                });
            });
        }

        // ── performance ───────────────────────────────────────────────────────────

        private async Task RegisterPerformanceAsync()
        {
            await Get("/api/omnitrader/firm/performance", async req =>
            {
                var from = ParseUtc(req.userParameters.Get("from")) ?? DateTime.UtcNow.AddDays(-30);
                var report = await Firm.PerformanceService.BuildAsync(from);
                await Json(req, report);
            });
        }

        // ── journal ───────────────────────────────────────────────────────────────

        private async Task RegisterJournalAsync()
        {
            await Get("/api/omnitrader/firm/journal", async req =>
            {
                string? reviewState = req.userParameters.Get("review");
                string? strategy = req.userParameters.Get("strategy");
                string? search = req.userParameters.Get("q");
                string? tag = req.userParameters.Get("tag");
                string? venue = req.userParameters.Get("venue");
                string? environment = req.userParameters.Get("environment");
                string? outcome = req.userParameters.Get("outcome");
                var since = ParseUtc(req.userParameters.Get("from"));
                int limit = ParseInt(req.userParameters.Get("limit"), 200, 1, 1000);
                int offset = ParseInt(req.userParameters.Get("offset"), 0, 0, 100_000);

                var scanned = await Firm.Journal.ListAsync(reviewState, strategy, 2000);

                var filtered = scanned.AsEnumerable();
                if (!string.IsNullOrWhiteSpace(venue))
                    filtered = filtered.Where(r => r.Venue.ToString().Equals(venue, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(environment))
                    filtered = filtered.Where(r => r.Environment.ToString().Equals(environment, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(tag))
                    filtered = filtered.Where(r => r.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)));
                if (since.HasValue)
                    filtered = filtered.Where(r => r.Ts >= since.Value);
                filtered = outcome?.ToLowerInvariant() switch
                {
                    "wins" => filtered.Where(r => (r.RealizedPnL ?? 0m) > 0m),
                    "losses" => filtered.Where(r => r.RealizedPnL.HasValue && r.RealizedPnL.Value <= 0m),
                    "open" => filtered.Where(r => !r.RealizedPnL.HasValue),
                    "intervened" => filtered.Where(r => r.Interventions.Count > 0),
                    _ => filtered
                };
                if (!string.IsNullOrWhiteSpace(search))
                    filtered = filtered.Where(r => Matches(search, r.InstrumentId, r.StrategyId, r.Notes,
                        r.Rationale, r.ExitReason, r.RiskSummary, string.Join(' ', r.Tags)));

                var rows = filtered.ToList();
                await Json(req, new
                {
                    Rows = rows.Skip(offset).Take(limit),
                    Filtered = rows.Count,
                    Total = scanned.Count,
                    Offset = offset,
                    Tags = scanned.SelectMany(r => r.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t),
                    Strategies = scanned.Select(r => r.StrategyId ?? "manual").Distinct().OrderBy(s => s),
                    ReviewCounts = scanned.GroupBy(r => r.ReviewState.ToString()).ToDictionary(g => g.Key, g => g.Count())
                });
            });

            await Get("/api/omnitrader/firm/journal/record", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                var record = await Firm.Journal.GetAsync(id);
                if (record == null) { await NotFound(req); return; }
                await Json(req, record);
            });

            await Post("/api/omnitrader/firm/journal/annotate", async req =>
            {
                var dto = Body<AnnotateDto>(req);
                if (dto == null || string.IsNullOrWhiteSpace(dto.Id)) { await Bad(req, "id required"); return; }
                ReviewState? review = Enum.TryParse<ReviewState>(dto.ReviewState, true, out var r) ? r : null;
                var record = await Firm.Journal.AnnotateAsync(dto.Id!, dto.Notes, dto.Tags, review);
                if (record == null) { await NotFound(req); return; }
                await Firm.Audit.AppendAsync(Actor(req), "journal.annotated", dto.Id!, dto.ReviewState);
                await Json(req, record);
            });

            await Post("/api/omnitrader/firm/journal/intervention", async req =>
            {
                var dto = Body<InterventionDto>(req);
                if (dto == null || string.IsNullOrWhiteSpace(dto.Id)) { await Bad(req, "id required"); return; }
                var record = await Firm.Journal.RecordInterventionAsync(dto.Id!, Actor(req), dto.Action ?? "manual",
                    dto.StateBefore, dto.StateAfter, dto.Reason);
                if (record == null) { await NotFound(req); return; }
                await Json(req, record);
            });
        }

        // ── systems ───────────────────────────────────────────────────────────────

        private async Task RegisterSystemsAsync()
        {
            await Get("/api/omnitrader/firm/systems", async req =>
            {
                var health = await Firm.Health.EvaluateAsync();
                var audit = await Firm.Audit.ListAsync(100);
                await Json(req, new
                {
                    Health = health,
                    Venues = Firm.Venues.HealthSnapshots.Select(h => new
                    {
                        Venue = h.Venue.ToString(),
                        Environment = h.Environment.ToString(),
                        h.Configured,
                        h.OrderPathHealthy,
                        h.AsOfUtc,
                        Channels = h.Channels.Select(c => new
                        {
                            c.Channel, c.Connected, c.LastOkUtc, c.LastErrorUtc, c.LastError,
                            c.ConsecutiveFailures, c.QuotaRemaining, c.Degraded
                        })
                    }),
                    DataFreshness = Firm.Instruments.AllFreshness().Take(50),
                    Audit = audit,
                    Service = new
                    {
                        Name = parent.GetName(),
                        DbPath = parent.GetDbPath(),
                        Uptime = parent.GetServiceUptime().ToString(),
                        Strategies = parent.StrategyRegistry.All.Count,
                        Instruments = Firm.Instruments.Count,
                        KrakenConfigured = parent.IsKrakenConfigured,
                        IGConfigured = Firm.Venues.All.Any(v => v.Venue == VenueId.IG && v.IsConfigured)
                    }
                });
            });

            await Get("/api/omnitrader/firm/alerts", async req =>
            {
                bool openOnly = !string.Equals(req.userParameters.Get("all"), "true", StringComparison.OrdinalIgnoreCase);
                var alerts = await Firm.Alerts.ListAsync(openOnly, 200);
                await Json(req, alerts.Select(AlertDto));
            });

            await Post("/api/omnitrader/firm/alert/acknowledge", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                bool ok = await Firm.Alerts.AcknowledgeAsync(id, Actor(req));
                await Firm.Audit.AppendAsync(Actor(req), "alert.acknowledged", id);
                await Json(req, new { Acknowledged = ok });
            });

            await Post("/api/omnitrader/firm/alert/resolve", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await Bad(req, "id required"); return; }
                bool ok = await Firm.Alerts.ResolveAsync(id);
                await Firm.Audit.AppendAsync(Actor(req), "alert.resolved", id);
                await Json(req, new { Resolved = ok });
            });

            await Post("/api/omnitrader/firm/venues/connect", async req =>
            {
                var failures = await Firm.Venues.ConnectAllAsync();
                await Firm.Audit.AppendAsync(Actor(req), "venues.reconnected", "firm",
                    failures.Count == 0 ? "all connected" : $"failed: {string.Join(", ", failures)}");
                // A reconnect is one of the moments the design requires reconciliation to run.
                if (failures.Count < Firm.Venues.All.Count)
                    await Firm.Reconciliation.ReconcileAllAsync("reconnect");
                await Json(req, new { Failures = failures, Health = Firm.Venues.HealthSnapshots });
            });

            await Post("/api/omnitrader/firm/account/authority", async req =>
            {
                string? id = req.userParameters.Get("id");
                string? authorityRaw = req.userParameters.Get("authority");
                string? confirm = req.userParameters.Get("confirm");
                if (string.IsNullOrWhiteSpace(id) || !Enum.TryParse<ExecutionAuthority>(authorityRaw, true, out var authority))
                { await Bad(req, "id and authority required"); return; }

                var accounts = await Firm.Accounts.ListAsync();
                var account = accounts.FirstOrDefault(a => a.Id == id);
                if (account == null) { await NotFound(req); return; }

                // Granting real-money authority requires an explicit typed confirmation.
                if (authority >= ExecutionAuthority.ApprovalRequired && account.Environment == TradingEnvironment.Live
                    && confirm != account.Id)
                {
                    await Bad(req, "granting live authority requires confirm=<accountId>");
                    return;
                }

                var previous = account.Authority;
                account.Authority = authority;
                await Firm.Accounts.UpsertAsync(account);
                await Firm.Audit.AppendAsync(Actor(req), "account.authority_changed", account.Id, $"{previous} → {authority}");
                await Firm.Alerts.RaiseAsync(AlertSeverity.High, "security", "Execution authority changed",
                    $"{account.DisplayName} moved from {previous} to {authority}.",
                    dedupeKey: $"authority:{account.Id}:{authority}");
                await Json(req, account);
            });
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        private Task Get(string path, Func<UserRequest, Task> handler)
            => parent.CreateAPIRoute(path, handler, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

        private Task Post(string path, Func<UserRequest, Task> handler)
            => parent.CreateAPIRoute(path, handler, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

        private static Task Json(UserRequest req, object payload)
            => req.ReturnResponse(JsonConvert.SerializeObject(payload, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Include,
                DateTimeZoneHandling = DateTimeZoneHandling.Utc
            }));

        private static Task Bad(UserRequest req, string message)
            => req.ReturnResponse(JsonConvert.SerializeObject(new { Error = message }), code: HttpStatusCode.BadRequest);

        private static Task NotFound(UserRequest req)
            => req.ReturnResponse(JsonConvert.SerializeObject(new { Error = "Not found" }), code: HttpStatusCode.NotFound);

        private static T? Body<T>(UserRequest req) where T : class
        {
            try { return JsonConvert.DeserializeObject<T>(req.userMessageContent ?? ""); }
            catch { return null; }
        }

        /// <summary>Who is acting. Every audited action is attributed, and an unattributed call is
        /// recorded as such rather than silently credited to the owner.</summary>
        private static string Actor(UserRequest req)
        {
            try
            {
                string? name = req.user?.Name;
                return string.IsNullOrWhiteSpace(name) ? "api" : name!;
            }
            catch { return "api"; }
        }

        private static bool SafeOrderPath(IVenueAdapter adapter)
        {
            try { return adapter.Health.OrderPathHealthy; }
            catch { return false; }
        }

        private static object OrderDto(FirmOrder o) => new
        {
            o.Id,
            o.ClientReference,
            o.ProposalId,
            o.RiskDecisionId,
            Venue = o.Venue.ToString(),
            Environment = o.Environment.ToString(),
            o.AccountId,
            o.InstrumentId,
            o.VenueSymbol,
            Side = o.Side.ToString(),
            Type = o.Type.ToString(),
            o.Quantity,
            o.LimitPrice,
            o.StopPrice,
            o.StopLossPrice,
            o.TakeProfitPrice,
            State = o.State.ToString(),
            o.VenueOrderId,
            o.FilledQuantity,
            o.RemainingQuantity,
            o.AverageFillPrice,
            o.Fees,
            o.FeeCurrency,
            o.Error,
            o.DecisionPrice,
            o.SlippageBps,
            LatencyMs = o.SubmissionLatency?.TotalMilliseconds,
            o.StrategyId,
            o.StrategyVersion,
            o.DeploymentId,
            o.CreatedUtc,
            o.SubmittedUtc,
            o.AcknowledgedUtc,
            o.CompletedUtc,
            o.ApprovedBy,
            o.ApprovedUtc,
            o.IsTerminal,
            o.IsLive,
            o.BlocksAutomation
        };

        private static object AlertDto(Alert a) => new
        {
            a.Id,
            Severity = a.Severity.ToString(),
            a.Category,
            a.Title,
            a.Message,
            a.RaisedUtc,
            a.AcknowledgedUtc,
            a.AcknowledgedBy,
            a.ResolvedUtc,
            a.OccurrenceCount,
            a.Venue,
            a.Environment,
            a.StrategyId,
            a.RecoveryHint,
            a.Open,
            a.NeedsAcknowledgement
        };

        private static decimal Pct(decimal value, decimal limit) => limit <= 0m ? 0m : Math.Round(value / limit * 100m, 1);

        /// <summary>Case-insensitive substring match across a record's identifying fields — the one
        /// search box an operator uses when they only remember part of a reference.</summary>
        private static bool Matches(string query, params string?[] fields)
            => fields.Any(f => !string.IsNullOrEmpty(f) && f!.Contains(query, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Firm value over time, downsampled for display, with the 24-hour comparison the command
        /// centre reads. <c>Points</c> is empty rather than fabricated when no snapshot exists —
        /// an unknown value must never render as zero.
        /// </summary>
        private static object BuildValueTrend(List<(DateTime Ts, VenueId Venue, decimal Value)> series, int windowDays)
        {
            var totals = series
                .GroupBy(s => s.Ts)
                .Select(g => (Ts: g.Key, Value: g.Sum(x => x.Value)))
                .OrderBy(p => p.Ts)
                .ToList();

            if (totals.Count == 0)
                return new { WindowDays = windowDays, Points = Array.Empty<object>(), Change24h = (decimal?)null, ChangePercent24h = (decimal?)null, PeakValue = (decimal?)null, TroughValue = (decimal?)null };

            // Keep at most ~120 points: enough shape for a wide chart, small enough to stay cheap.
            const int MaxPoints = 120;
            var points = totals;
            if (totals.Count > MaxPoints)
            {
                double step = (double)totals.Count / MaxPoints;
                points = Enumerable.Range(0, MaxPoints)
                    .Select(i => totals[Math.Min(totals.Count - 1, (int)(i * step))])
                    .ToList();
                points[^1] = totals[^1];
            }

            decimal latest = totals[^1].Value;
            var dayAgo = totals.LastOrDefault(p => p.Ts <= DateTime.UtcNow.AddHours(-24));
            decimal? change = dayAgo.Ts == default ? null : latest - dayAgo.Value;

            return new
            {
                WindowDays = windowDays,
                Points = points.Select(p => new { p.Ts, p.Value }),
                Change24h = change,
                ChangePercent24h = change.HasValue && dayAgo.Value != 0m
                    ? Math.Round(change.Value / Math.Abs(dayAgo.Value) * 100m, 2) : (decimal?)null,
                PeakValue = totals.Max(p => p.Value),
                TroughValue = totals.Min(p => p.Value),
                FirstUtc = totals[0].Ts,
                LastUtc = totals[^1].Ts
            };
        }

        /// <summary>Deterministic token over the exact preview shown, so confirming stale numbers fails.</summary>
        private static string ReduceToken(int positions, decimal notional)
            => $"REDUCE-{positions}-{Math.Round(notional, 2).ToString(CultureInfo.InvariantCulture)}";

        private static bool IsLondonSessionOpen(DateTime utc)
        {
            if (utc.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
            return utc.TimeOfDay >= TimeSpan.FromHours(8) && utc.TimeOfDay < TimeSpan.FromHours(16.5);
        }

        private static DateTime? ParseUtc(string? raw)
            => string.IsNullOrWhiteSpace(raw) ? null
             : DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                 DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;

        private static int ParseInt(string? raw, int fallback, int min, int max)
            => int.TryParse(raw, out var v) ? Math.Clamp(v, min, max) : fallback;

        private static TimeInterval? ParseInterval(string? raw)
            => Enum.TryParse<TimeInterval>(raw, true, out var v) ? v : null;
    }

    // ── request DTOs ──────────────────────────────────────────────────────────────

    public sealed class WatchlistDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public List<string>? InstrumentIds { get; set; }
    }

    public sealed class KillSwitchDto
    {
        public string Kind { get; set; } = "Firm";
        public string? Scope { get; set; }
        public string? Reason { get; set; }
        public bool Release { get; set; }
    }

    public sealed class ProposeOrderDto
    {
        public string InstrumentId { get; set; } = "";
        public string Venue { get; set; } = "Internal";
        public string Environment { get; set; } = "Paper";
        public string AccountId { get; set; } = "paper";
        public string Side { get; set; } = "Buy";
        public string Type { get; set; } = "Market";
        public decimal Quantity { get; set; }
        public decimal? LimitPrice { get; set; }
        public decimal? StopPrice { get; set; }
        public decimal? StopLossPrice { get; set; }
        public decimal? TakeProfitPrice { get; set; }
        public decimal DecisionPrice { get; set; }
        public string? StrategyId { get; set; }
        public string? DeploymentId { get; set; }
        public string Authority { get; set; } = "ApprovalRequired";
        public string? Rationale { get; set; }
        public int ExpirySeconds { get; set; } = 300;

        public TradeProposal ToProposal(decimal? markOverride = null) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            InstrumentId = InstrumentId,
            Venue = Enum.Parse<VenueId>(Venue, true),
            Environment = Enum.Parse<TradingEnvironment>(Environment, true),
            AccountId = AccountId,
            Side = Enum.Parse<OrderSide>(Side, true),
            Type = Enum.Parse<OrderType>(Type, true),
            Quantity = Quantity,
            LimitPrice = LimitPrice,
            StopPrice = StopPrice,
            StopLossPrice = StopLossPrice,
            TakeProfitPrice = TakeProfitPrice,
            DecisionPrice = markOverride ?? DecisionPrice,
            StrategyId = StrategyId,
            DeploymentId = DeploymentId,
            Authority = Enum.Parse<ExecutionAuthority>(Authority, true),
            Rationale = Rationale,
            DataTimestampUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Clamp(ExpirySeconds, 10, 3600))
        };
    }

    public sealed class ExperimentDto
    {
        public string? Name { get; set; }
        public string? StrategyClass { get; set; }
        public string? Hypothesis { get; set; }
        public string? Notes { get; set; }
        public string? Status { get; set; }
        public Dictionary<string, object?>? Parameters { get; set; }
    }

    public sealed class StrategyVersionDto
    {
        public string? StrategyClass { get; set; }
        public string? Notes { get; set; }
        public Dictionary<string, object?>? Parameters { get; set; }
    }

    public sealed class AnnotateDto
    {
        public string? Id { get; set; }
        public string? Notes { get; set; }
        public List<string>? Tags { get; set; }
        public string? ReviewState { get; set; }
    }

    public sealed class InterventionDto
    {
        public string? Id { get; set; }
        public string? Action { get; set; }
        public string? StateBefore { get; set; }
        public string? StateAfter { get; set; }
        public string? Reason { get; set; }
    }
}
