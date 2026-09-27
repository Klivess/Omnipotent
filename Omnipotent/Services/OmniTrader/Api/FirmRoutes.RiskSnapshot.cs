using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.OmniTrader.Api
{
    public sealed partial class FirmRoutes
    {
        private RouteSnapshotWorker? riskWorker;

        public void StartRiskSnapshotLoop(CancellationToken cancellationToken)
        {
            string path = Path.Combine(Path.GetDirectoryName(parent.Db.DbPath)!, "firm-risk-snapshot.json");
            riskWorker = new RouteSnapshotWorker(
                new PrecomputedRouteSnapshot(path, JTokenType.Object, TimeSpan.FromMinutes(5)),
                BuildRiskSnapshotAsync,
                ex => parent.ServiceLogError(ex, "Firm risk snapshot refresh failed"),
                TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
            riskWorker.Start(cancellationToken);
        }

        private string? GetRiskSnapshot() => riskWorker?.GetBody(DateTime.UtcNow);
        private void NotifyRiskMutation() => riskWorker?.RequestRefresh();

        private async Task<SnapshotBuild> BuildRiskSnapshotAsync(CancellationToken cancellationToken)
        {
            // The valuation can wait on several venues. Report when that observation began,
            // rather than making its figures look newer than the underlying account reads.
            DateTime observedFromUtc = DateTime.UtcNow;
            var state = await Firm.Portfolio.BuildRiskStateAsync(cancellationToken);
            var operations = await Firm.Orders.BuildOperationalStateAsync(null, cancellationToken);
            var decisions = await Firm.RiskRepo.ListRecentAsync(50, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var limits = Firm.Limits;
            string body = JsonConvert.SerializeObject(new
            {
                AsOfUtc = observedFromUtc,
                Limits = limits,
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
                Utilisation = new
                {
                    Gross = Pct(state.GrossExposure, limits.MaxGrossExposure),
                    Net = Pct(Math.Abs(state.NetExposure), limits.MaxNetExposure),
                    DailyLoss = Pct(Math.Abs(Math.Min(0m, state.DailyRealizedPnL)), limits.MaxFirmDailyLoss),
                    Drawdown = Pct(state.DrawdownPercent, limits.MaxDrawdownPercent)
                },
                RecentDecisions = decisions.Select(d => new
                {
                    d.Id,
                    d.ProposalId,
                    Verdict = d.Verdict.ToString(),
                    d.DecidedUtc,
                    d.Summary,
                    Failures = d.Failures.Select(f => new
                    {
                        Layer = f.Layer.ToString(), f.Rule, Severity = f.Severity.ToString(),
                        f.Detail, f.Observed, f.Limit
                    })
                })
            }, JsonSettings);
            return new SnapshotBuild(body, observedFromUtc);
        }
    }
}
