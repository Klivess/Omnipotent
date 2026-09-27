using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Ops;
using System.Threading;

namespace Omnipotent.Services.OmniTrader.Api
{
    /// <summary>
    /// The overview can spend many seconds waiting on market data and repositories. Build
    /// every supported trend window once on a worker, then let requests read only a
    /// serialized, immutable snapshot. A failed refresh leaves the last good snapshot
    /// available until its age limit; it never starts work from an HTTP request.
    /// </summary>
    public sealed partial class FirmRoutes
    {
        private static readonly TimeSpan OverviewRefreshDelay = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan OverviewMaxAge = TimeSpan.FromMinutes(5);
        private FirmOverviewSnapshots? overviewSnapshots;
        private int overviewLoopStarted;
        private CancellationToken overviewLifetimeToken;

        private string OverviewSnapshotPath => Path.Combine(
            Path.GetDirectoryName(parent.Db.DbPath)!, "firm-overview-snapshot.json");

        private string? GetOverviewSnapshot(int trendDays)
        {
            // A persisted body can outlive the service that produced it. Never present
            // its trading controls while firm startup failed or shutdown is in progress.
            if (Volatile.Read(ref overviewLoopStarted) == 0 || overviewLifetimeToken.IsCancellationRequested)
                return null;
            var snapshot = Volatile.Read(ref overviewSnapshots);
            return snapshot?.GetBody(trendDays, DateTime.UtcNow, OverviewMaxAge);
        }

        private void LoadOverviewSnapshotFromDisk()
        {
            try
            {
                FirmOverviewSnapshots? loaded = FirmOverviewSnapshots.LoadDefault(
                    OverviewSnapshotPath, DateTime.UtcNow, OverviewMaxAge);
                if (loaded != null) Volatile.Write(ref overviewSnapshots, loaded);
            }
            catch
            {
                // A damaged or inaccessible snapshot must not affect firm startup.
            }
        }

        public void StartOverviewSnapshotLoop(CancellationToken cancellationToken)
        {
            overviewLifetimeToken = cancellationToken;
            if (Interlocked.Exchange(ref overviewLoopStarted, 1) != 0) return;
            _ = Task.Run(() => RunOverviewSnapshotLoopAsync(cancellationToken));
        }

        private async Task RunOverviewSnapshotLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    FirmOverviewSnapshots next = await BuildOverviewSnapshotsAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    Volatile.Write(ref overviewSnapshots, next);
                    try { await next.SaveDefaultAsync(OverviewSnapshotPath, cancellationToken); }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        await parent.ServiceLogError(ex, "Firm overview snapshot persistence failed");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await parent.ServiceLogError(ex, "Firm overview snapshot refresh failed");
                }

                try { await Task.Delay(OverviewRefreshDelay, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task<FirmOverviewSnapshots> BuildOverviewSnapshotsAsync(CancellationToken cancellationToken)
        {
            // Age the snapshot from the first account observation, not from the
            // end of several potentially slow venue and repository calls.
            DateTime asOf = DateTime.UtcNow;
            var portfolio = await Firm.Portfolio.BuildAsync(cancellationToken);
            var health = await Firm.Health.EvaluateAsync(cancellationToken);
            var alerts = await Firm.Alerts.ListAsync(openOnly: true, limit: 50, ct: cancellationToken);
            var awaiting = await Firm.OrderRepo.ListAwaitingApprovalAsync(cancellationToken);
            var unknown = await Firm.OrderRepo.ListUnknownAsync(cancellationToken);
            var breaks = await Firm.Reconciliation.ListOpenBreaksAsync(cancellationToken);
            var deployments = await parent.DeploymentRepo.ListAllAsync(cancellationToken);
            var history = await Firm.Portfolio.ValueSeriesAsync(asOf.AddDays(-365), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var orderedHistory = history
                .Select(p => new FirmValueTrendPoint { Ts = p.Ts, Value = p.TotalValue })
                .OrderBy(p => p.Ts)
                .ToArray();

            // Convert the common fields just once. This freezes enumerables and mutable
            // firm state at the same instant for every trendDays variant.
            var serializer = JsonSerializer.Create(JsonSettings);
            JObject shared = JObject.FromObject(new
            {
                AsOfUtc = asOf,
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
                    Positions = portfolio.Real.Positions,
                    portfolio.HasRealAccounts,
                    portfolio.Warnings
                },
                Simulated = new
                {
                    portfolio.Simulated.TotalValue,
                    portfolio.Simulated.Cash,
                    portfolio.Simulated.InventoryValue,
                    portfolio.Simulated.UnrealizedPnL,
                    portfolio.Simulated.GrossExposure,
                    portfolio.Simulated.Positions,
                    RealizedPnLToday = portfolio.SimulatedRealizedPnLToday
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
                Trend = (FirmValueTrend?)null
            }, serializer);

            var bodies = new string?[366];
            for (int days = 1; days <= 365; days++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DateTime from = asOf.AddDays(-days);
                int start = FirstHistoryPointAtOrAfter(orderedHistory, from);
                shared["Trend"] = JToken.FromObject(
                    BuildValueTrendFromOrdered(orderedHistory, start, days, asOf), serializer);
                bodies[days] = shared.ToString(Formatting.None);
            }
            return new FirmOverviewSnapshots(asOf, bodies);
        }

        private static int FirstHistoryPointAtOrAfter(IReadOnlyList<FirmValueTrendPoint> points, DateTime from)
        {
            int low = 0, high = points.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (points[middle].Ts < from) low = middle + 1;
                else high = middle;
            }
            return low;
        }
    }

    internal sealed class FirmOverviewSnapshots
    {
        private readonly string?[] bodies;
        public DateTime AsOfUtc { get; }

        public FirmOverviewSnapshots(DateTime asOfUtc, string?[] bodies)
        {
            if (bodies.Length != 366) throw new ArgumentException("Expected trend windows 1 through 365.", nameof(bodies));
            AsOfUtc = asOfUtc;
            this.bodies = (string?[])bodies.Clone();
        }

        public string? GetBody(int trendDays, DateTime nowUtc, TimeSpan maxAge)
        {
            if (trendDays is < 1 or > 365 || nowUtc - AsOfUtc > maxAge || AsOfUtc > nowUtc.AddMinutes(1)) return null;
            return bodies[trendDays];
        }

        public static FirmOverviewSnapshots? LoadDefault(string path, DateTime nowUtc, TimeSpan maxAge)
        {
            if (!File.Exists(path)) return null;
            string body = File.ReadAllText(path);
            JObject parsed = JObject.Parse(body);
            DateTime? asOf = parsed.Value<DateTime?>("AsOfUtc");
            if (asOf == null || parsed["Portfolio"] == null || parsed["Trend"]?.Value<int?>("WindowDays") != 30)
                return null;
            var bodies = new string?[366];
            bodies[30] = body;
            var snapshot = new FirmOverviewSnapshots(asOf.Value.ToUniversalTime(), bodies);
            return snapshot.GetBody(30, nowUtc, maxAge) == null ? null : snapshot;
        }

        public async Task SaveDefaultAsync(string path, CancellationToken cancellationToken)
        {
            string body = bodies[30] ?? throw new InvalidOperationException("Default overview missing.");
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, body, cancellationToken);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}
