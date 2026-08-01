using Omnipotent.Services.OmniTrader.Instruments;
using Omnipotent.Services.OmniTrader.Ledger;
using Omnipotent.Services.OmniTrader.OrderFlow;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Risk;
using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Services.OmniTrader.Ops
{
    /// <summary>
    /// Continuously answers one question: is the firm allowed to trade right now, and if not, why?
    ///
    /// Areas are reported independently — read-only analytics may be degraded while the order path is
    /// perfectly healthy, and the platform says so rather than collapsing everything into one
    /// green/red light.
    /// </summary>
    public sealed class HealthMonitor
    {
        private readonly VenueRegistry venues;
        private readonly InstrumentMaster instruments;
        private readonly FirmOrderRepository orderRepo;
        private readonly ReconciliationRepository reconRepo;
        private readonly EmergencyControls emergency;
        private readonly AlertService alerts;
        private readonly Func<DateTime?> lastReconciliation;

        public HealthMonitor(VenueRegistry venues, InstrumentMaster instruments, FirmOrderRepository orderRepo,
            ReconciliationRepository reconRepo, EmergencyControls emergency, AlertService alerts,
            Func<DateTime?> lastReconciliation)
        {
            this.venues = venues;
            this.instruments = instruments;
            this.orderRepo = orderRepo;
            this.reconRepo = reconRepo;
            this.emergency = emergency;
            this.alerts = alerts;
            this.lastReconciliation = lastReconciliation;
        }

        public async Task<FirmHealth> EvaluateAsync(CancellationToken ct = default)
        {
            var areas = new List<HealthArea>();
            var blockers = new List<string>();

            areas.Add(BuildConnectionArea(blockers));
            areas.Add(BuildSessionArea());
            areas.Add(BuildDataArea(blockers));
            areas.Add(await BuildOrderAreaAsync(blockers, ct));
            areas.Add(await BuildReconciliationAreaAsync(blockers, ct));
            areas.Add(BuildControlArea(blockers));

            bool permitted = blockers.Count == 0;
            return new FirmHealth
            {
                TradingPermitted = permitted,
                Summary = permitted
                    ? "All trading-critical services healthy."
                    : $"Trading blocked: {string.Join("; ", blockers)}",
                Areas = areas,
                Blockers = blockers
            };
        }

        private HealthArea BuildConnectionArea(List<string> blockers)
        {
            var signals = new List<HealthSignal>();
            bool anyOrderPath = false;

            foreach (var snapshot in venues.HealthSnapshots)
            {
                foreach (var channel in snapshot.Channels)
                {
                    signals.Add(new HealthSignal
                    {
                        Name = $"{snapshot.Venue}/{snapshot.Environment} · {channel.Channel}",
                        Ok = channel.Connected,
                        Value = channel.Connected ? "connected" : "down",
                        Detail = channel.LastError
                               ?? (channel.QuotaRemaining.HasValue ? $"quota {channel.QuotaRemaining.Value:P0} remaining" : null)
                    });
                }
                if (snapshot.OrderPathHealthy) anyOrderPath = true;
            }

            if (venues.All.Count == 0)
                blockers.Add("no venue adapters registered");
            else if (!anyOrderPath)
                blockers.Add("no venue has a healthy order path");

            return new HealthArea
            {
                Area = "Connections",
                Healthy = anyOrderPath && venues.All.Count > 0,
                Detail = $"{venues.All.Count} venue connection(s)",
                Signals = signals
            };
        }

        private HealthArea BuildSessionArea()
        {
            var signals = venues.All.Select(a => new HealthSignal
            {
                Name = $"{a.Venue}/{a.Environment} session",
                Ok = a.IsConfigured,
                Value = a.IsConfigured ? "authenticated" : "not configured",
                Detail = a.IsConfigured ? null : "credentials missing or login failed"
            }).ToList();

            return new HealthArea
            {
                Area = "Sessions",
                Healthy = signals.All(s => s.Ok),
                Detail = $"{signals.Count(s => s.Ok)}/{signals.Count} authenticated",
                Signals = signals
            };
        }

        private HealthArea BuildDataArea(List<string> blockers)
        {
            var freshness = instruments.AllFreshness();
            var stale = freshness.Where(f => f.Stale).ToList();

            var signals = freshness.Take(25).Select(f => new HealthSignal
            {
                Name = f.InstrumentId,
                Ok = !f.Stale,
                Value = f.Age == TimeSpan.MaxValue ? "never" : $"{f.Age.TotalMinutes:F1} min",
                Detail = f.Issue
            }).ToList();

            // Stale data blocks the *affected* instruments (enforced per-order by the risk engine's
            // data-integrity layer), not the whole firm — so it is reported, not treated as a blocker.
            return new HealthArea
            {
                Area = "Market data",
                Healthy = stale.Count == 0,
                Detail = stale.Count == 0
                    ? $"{freshness.Count} instrument feed(s) fresh"
                    : $"{stale.Count} of {freshness.Count} instrument feed(s) stale — automated actions on those instruments are blocked",
                Signals = signals
            };
        }

        private async Task<HealthArea> BuildOrderAreaAsync(List<string> blockers, CancellationToken ct)
        {
            var unknown = await orderRepo.ListUnknownAsync(ct);
            var open = await orderRepo.ListOpenAsync(ct);
            var awaiting = await orderRepo.ListAwaitingApprovalAsync(ct);
            int rejections = await orderRepo.CountRejectionsSinceAsync(DateTime.UtcNow.AddHours(-1), ct);

            if (unknown.Count > 0)
                blockers.Add($"{unknown.Count} order(s) with an unproven outcome");

            var signals = new List<HealthSignal>
            {
                new() { Name = "Unknown submissions", Ok = unknown.Count == 0, Value = unknown.Count.ToString(),
                        Detail = unknown.Count == 0 ? null : "these are never retried automatically — resolve by reconciling" },
                new() { Name = "Working orders", Ok = true, Value = open.Count.ToString() },
                new() { Name = "Awaiting approval", Ok = true, Value = awaiting.Count.ToString() },
                new() { Name = "Rejections (1h)", Ok = rejections < 5, Value = rejections.ToString() }
            };

            return new HealthArea
            {
                Area = "Order flow",
                Healthy = unknown.Count == 0 && rejections < 5,
                Detail = unknown.Count == 0 ? "every submission has resolved" : $"{unknown.Count} unresolved",
                Signals = signals
            };
        }

        private async Task<HealthArea> BuildReconciliationAreaAsync(List<string> blockers, CancellationToken ct)
        {
            var openBreaks = await reconRepo.ListOpenBreaksAsync(ct);
            int material = openBreaks.Count(b => b.Material);
            var last = lastReconciliation();
            var age = last.HasValue ? DateTime.UtcNow - last.Value : (TimeSpan?)null;

            if (material > 0) blockers.Add($"{material} unresolved reconciliation break(s)");

            var signals = new List<HealthSignal>
            {
                new() { Name = "Material breaks", Ok = material == 0, Value = material.ToString() },
                new() { Name = "Timing differences", Ok = true, Value = (openBreaks.Count - material).ToString(),
                        Detail = "expected to clear on their own" },
                new() { Name = "Last run", Ok = age is null or { TotalMinutes: < 60 },
                        Value = age.HasValue ? $"{age.Value.TotalMinutes:F0} min ago" : "never",
                        Detail = age is null ? "reconciliation has not run in this process" : null }
            };

            return new HealthArea
            {
                Area = "Reconciliation",
                Healthy = material == 0,
                Detail = material == 0 ? "internal and broker state agree" : $"{material} unexplained difference(s)",
                Signals = signals
            };
        }

        private HealthArea BuildControlArea(List<string> blockers)
        {
            if (emergency.SafeModeActive)
                blockers.Add($"safe mode: {emergency.SafeModeReason}");
            foreach (var killSwitch in emergency.Active)
                blockers.Add($"kill switch {killSwitch.Key}: {killSwitch.Reason}");

            var signals = new List<HealthSignal>
            {
                new() { Name = "Safe mode", Ok = !emergency.SafeModeActive,
                        Value = emergency.SafeModeActive ? "ACTIVE" : "clear",
                        Detail = emergency.SafeModeReason },
                new() { Name = "Kill switches", Ok = emergency.Active.Count == 0,
                        Value = emergency.Active.Count.ToString(),
                        Detail = emergency.Active.Count == 0 ? null : string.Join(", ", emergency.Active.Select(k => k.Key)) }
            };

            return new HealthArea
            {
                Area = "Controls",
                Healthy = !emergency.SafeModeActive && emergency.Active.Count == 0,
                Detail = emergency.SafeModeActive ? "safe mode engaged" : "no emergency controls engaged",
                Signals = signals
            };
        }

        /// <summary>Run a sweep and raise/resolve alerts from what it finds. Called on a timer, so an
        /// operator learns about a degraded venue without having to be looking at the page.</summary>
        public async Task<FirmHealth> SweepAsync(CancellationToken ct = default)
        {
            var health = await EvaluateAsync(ct);

            foreach (var snapshot in venues.HealthSnapshots)
            {
                foreach (var channel in snapshot.Channels.Where(c => !c.Connected && c.ConsecutiveFailures >= 3))
                {
                    await alerts.HighAsync("connectivity", $"{snapshot.Venue} {channel.Channel} down",
                        $"{channel.ConsecutiveFailures} consecutive failures. Last error: {channel.LastError ?? "unknown"}.",
                        dedupeKey: $"channel:{snapshot.Venue}:{snapshot.Environment}:{channel.Channel}",
                        recoveryHint: "Check credentials and network reachability on the Systems page.", ct: ct);
                }
                foreach (var channel in snapshot.Channels.Where(c => c.Connected))
                    await alerts.ResolveByDedupeAsync($"channel:{snapshot.Venue}:{snapshot.Environment}:{channel.Channel}", ct);
            }

            var stale = instruments.AllFreshness().Where(f => f.Stale).ToList();
            if (stale.Count > 0)
                await alerts.HighAsync("data", "Market data stale",
                    $"{stale.Count} instrument feed(s) are stale: {string.Join(", ", stale.Take(5).Select(f => f.InstrumentId))}"
                    + (stale.Count > 5 ? $" and {stale.Count - 5} more." : "."),
                    dedupeKey: "data:stale", recoveryHint: "Automated actions on those instruments are already blocked.", ct: ct);
            else
                await alerts.ResolveByDedupeAsync("data:stale", ct);

            return health;
        }
    }
}
