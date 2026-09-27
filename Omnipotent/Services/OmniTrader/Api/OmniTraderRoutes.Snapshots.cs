using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;

namespace Omnipotent.Services.OmniTrader.Api
{
    public sealed partial class OmniTraderRoutes
    {
        private RouteSnapshotWorker? deploymentListWorker;
        private RouteSnapshotWorker? backtestListWorker;

        public void StartListSnapshotLoops(CancellationToken cancellationToken)
        {
            string directory = Path.GetDirectoryName(parent.Db.DbPath)!;
            deploymentListWorker = new RouteSnapshotWorker(
                new PrecomputedRouteSnapshot(Path.Combine(directory, "deployments-list-snapshot.json"),
                    JTokenType.Array, TimeSpan.FromMinutes(2)),
                BuildDeploymentListAsync,
                ex => parent.ServiceLogError(ex, "Deployment list snapshot refresh failed"),
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15));
            backtestListWorker = new RouteSnapshotWorker(
                new PrecomputedRouteSnapshot(Path.Combine(directory, "backtests-list-snapshot.json"),
                    JTokenType.Array, TimeSpan.FromMinutes(2)),
                BuildBacktestListAsync,
                ex => parent.ServiceLogError(ex, "Backtest list snapshot refresh failed"),
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15));
            deploymentListWorker.Start(cancellationToken);
            backtestListWorker.Start(cancellationToken);
        }

        private string? GetDeploymentListSnapshot() => deploymentListWorker?.GetBody(DateTime.UtcNow);
        private string? GetBacktestListSnapshot() => backtestListWorker?.GetBody(DateTime.UtcNow);
        private void NotifyDeploymentMutation() => deploymentListWorker?.RequestRefresh();
        private void NotifyBacktestMutation() => backtestListWorker?.RequestRefresh();

        private async Task<SnapshotBuild> BuildDeploymentListAsync(CancellationToken cancellationToken)
        {
            DateTime observedFromUtc = DateTime.UtcNow;
            var deployments = await parent.DeploymentRepo.ListAllAsync(cancellationToken);
            var dtos = deployments.Select(d => new
            {
                d.Id,
                d.StrategyClass,
                d.Config.Symbol,
                Interval = d.Config.Interval.ToString(),
                Mode = d.Mode.ToString(),
                Status = d.Status.ToString(),
                Armed = parent.SessionManager.IsDeploymentArmed(d.Id),
                d.EquityInitial,
                d.EquityCurrent,
                PnLPercent = d.EquityInitial == 0 ? 0 : (d.EquityCurrent - d.EquityInitial) / d.EquityInitial * 100m,
                d.CreatedUtc,
                d.ArmedLiveUtc,
                d.PausedUtc,
                d.Error
            }).ToList();
            return new SnapshotBuild(JsonConvert.SerializeObject(dtos), observedFromUtc);
        }

        private async Task<SnapshotBuild> BuildBacktestListAsync(CancellationToken cancellationToken)
        {
            DateTime observedFromUtc = DateTime.UtcNow;
            var jobs = await parent.BacktestJobRepo.ListRecentSummariesAsync(50, cancellationToken);
            var dtos = jobs.Select(j => new
            {
                j.Id,
                j.StrategyClass,
                j.Config.Coin,
                j.Config.Currency,
                Interval = j.Config.Interval.ToString(),
                j.Config.CandleCount,
                Status = j.Status.ToString(),
                j.ProgressPct,
                j.CandlesTotal,
                j.CandlesDone,
                j.QueuedUtc,
                j.StartedUtc,
                j.FinishedUtc,
                j.Error,
                j.TotalPnLPercent,
                j.WinRate,
                j.SharpeRatio,
                j.MaxDrawdownPercent,
                j.TotalTrades
            }).ToList();
            return new SnapshotBuild(JsonConvert.SerializeObject(dtos), observedFromUtc);
        }
    }
}
