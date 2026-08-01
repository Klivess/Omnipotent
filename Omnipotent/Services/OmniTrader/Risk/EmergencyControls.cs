using Omnipotent.Services.OmniTrader.Venues;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniTrader.Risk
{
    public enum KillScopeKind { Firm, Venue, Account, Strategy }

    public sealed class KillSwitch
    {
        public required KillScopeKind Kind { get; init; }
        /// <summary>Empty for <see cref="KillScopeKind.Firm"/>; otherwise the venue/account/strategy key.</summary>
        public string Scope { get; init; } = "";
        public required string Reason { get; init; }
        public required string TriggeredBy { get; init; }
        public DateTime TriggeredUtc { get; init; } = DateTime.UtcNow;
        public bool Automatic { get; init; }

        public string Key => $"{Kind}:{Scope}".ToLowerInvariant();
    }

    /// <summary>
    /// Firm-wide emergency authority: disable new automated proposals, and trip safe mode when the
    /// platform's own thresholds are breached.
    ///
    /// This class deliberately does **not** close positions. Reducing exposure changes the firm's
    /// economic state and is a separate, strongly-confirmed action that previews what it will do —
    /// killing automation and unwinding a book are different decisions with different blast radii.
    /// </summary>
    public sealed class EmergencyControls
    {
        private readonly ConcurrentDictionary<string, KillSwitch> switches = new(StringComparer.OrdinalIgnoreCase);
        private readonly Action<string, string>? onChange;

        public bool SafeModeActive { get; private set; }
        public string? SafeModeReason { get; private set; }
        public DateTime? SafeModeSinceUtc { get; private set; }
        public string? SafeModeTriggeredBy { get; private set; }

        public EmergencyControls(Action<string, string>? onChange = null) => this.onChange = onChange;

        public IReadOnlyList<KillSwitch> Active => switches.Values.OrderByDescending(s => s.TriggeredUtc).ToList();

        /// <summary>Trip safe mode. Automated proposals stop firm-wide until it is explicitly cleared.</summary>
        public void EnterSafeMode(string reason, string triggeredBy, bool automatic = false)
        {
            if (SafeModeActive && SafeModeReason == reason) return;
            SafeModeActive = true;
            SafeModeReason = reason;
            SafeModeSinceUtc = DateTime.UtcNow;
            SafeModeTriggeredBy = triggeredBy;
            onChange?.Invoke("safe_mode_entered", $"{reason} (by {triggeredBy}{(automatic ? ", automatic" : "")})");
        }

        public void ExitSafeMode(string clearedBy)
        {
            if (!SafeModeActive) return;
            SafeModeActive = false;
            string was = SafeModeReason ?? "";
            SafeModeReason = null;
            SafeModeSinceUtc = null;
            SafeModeTriggeredBy = null;
            onChange?.Invoke("safe_mode_cleared", $"cleared by {clearedBy} (was: {was})");
        }

        public void Engage(KillSwitch killSwitch)
        {
            switches[killSwitch.Key] = killSwitch;
            onChange?.Invoke("killswitch_engaged", $"{killSwitch.Key}: {killSwitch.Reason} (by {killSwitch.TriggeredBy})");
        }

        public bool Release(KillScopeKind kind, string scope, string releasedBy)
        {
            string key = $"{kind}:{scope}".ToLowerInvariant();
            if (!switches.TryRemove(key, out _)) return false;
            onChange?.Invoke("killswitch_released", $"{key} released by {releasedBy}");
            return true;
        }

        /// <summary>Whether automated exposure is currently permitted for a given scope. Returns the
        /// blocking reason so the UI and the risk decision can both explain the block.</summary>
        public bool IsBlocked(VenueId venue, string? accountId, string? strategyId, out string? reason)
        {
            reason = null;
            if (SafeModeActive) { reason = SafeModeReason ?? "safe mode active"; return true; }

            if (switches.TryGetValue($"{KillScopeKind.Firm}:".ToLowerInvariant(), out var firm))
            { reason = firm.Reason; return true; }

            if (switches.TryGetValue($"{KillScopeKind.Venue}:{venue}".ToLowerInvariant(), out var v))
            { reason = v.Reason; return true; }

            if (!string.IsNullOrWhiteSpace(accountId)
                && switches.TryGetValue($"{KillScopeKind.Account}:{accountId}".ToLowerInvariant(), out var a))
            { reason = a.Reason; return true; }

            if (!string.IsNullOrWhiteSpace(strategyId)
                && switches.TryGetValue($"{KillScopeKind.Strategy}:{strategyId}".ToLowerInvariant(), out var s))
            { reason = s.Reason; return true; }

            return false;
        }

        /// <summary>Evaluate the automatic safe-mode triggers. Called after every fill, reconciliation
        /// pass and health sweep; trips at most once per distinct reason.</summary>
        public void EvaluateAutomaticTriggers(RiskPortfolioState portfolio, RiskOperationalState ops, RiskLimits limits)
        {
            if (portfolio.DailyRealizedPnL <= -Math.Abs(limits.MaxFirmDailyLoss))
                EnterSafeMode($"Daily loss {portfolio.DailyRealizedPnL:F2} breached the {limits.MaxFirmDailyLoss:F2} firm limit",
                    "risk-engine", automatic: true);

            else if (portfolio.DrawdownPercent > limits.MaxDrawdownPercent)
                EnterSafeMode($"Drawdown {portfolio.DrawdownPercent:F2}% breached the {limits.MaxDrawdownPercent:F2}% limit",
                    "risk-engine", automatic: true);

            else if (ops.UnknownOrders > limits.MaxUnresolvedUnknownOrders)
                EnterSafeMode($"{ops.UnknownOrders} order(s) with an unproven outcome", "risk-engine", automatic: true);

            else if (ops.UnreconciledBreaks > limits.MaxUnreconciledBreaks)
                EnterSafeMode($"{ops.UnreconciledBreaks} unresolved reconciliation break(s)", "risk-engine", automatic: true);

            else if (ops.RecentRejections >= limits.RepeatedRejectionThreshold)
                EnterSafeMode($"{ops.RecentRejections} repeated broker rejections", "risk-engine", automatic: true);
        }
    }
}
