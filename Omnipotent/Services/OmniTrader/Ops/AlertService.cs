using Omnipotent.Services.OmniTrader.Persistence;

namespace Omnipotent.Services.OmniTrader.Ops
{
    /// <summary>
    /// Severity-routed alerting. Every alert is persisted; High and Critical are pushed to Discord.
    /// Alerts deduplicate on the *condition*, so a flapping feed produces one open alert with a rising
    /// occurrence count rather than a hundred messages.
    ///
    /// Critical alerts stay open until something explicitly resolves the underlying state — the
    /// acknowledge action records who looked at it, and nothing more.
    /// </summary>
    public sealed class AlertService
    {
        private readonly AlertRepository repo;
        private readonly Func<string, Task>? push;
        private readonly Action<string>? log;
        private readonly SemaphoreSlim raiseLock = new(1, 1);

        public AlertService(AlertRepository repo, Func<string, Task>? push = null, Action<string>? log = null)
        {
            this.repo = repo;
            this.push = push;
            this.log = log;
        }

        public async Task<Alert> RaiseAsync(AlertSeverity severity, string category, string title, string message,
            string? dedupeKey = null, string? venue = null, string? environment = null, string? strategyId = null,
            string? recoveryHint = null, CancellationToken ct = default)
        {
            string key = dedupeKey ?? $"{category}:{title}";
            await raiseLock.WaitAsync(ct);
            try
            {
                var existing = await repo.FindOpenByDedupeAsync(key, ct);
                if (existing != null)
                {
                    existing.OccurrenceCount++;
                    existing.Message = message;
                    await repo.UpsertAsync(existing, ct);
                    return existing;
                }

                var alert = new Alert
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Severity = severity,
                    Category = category,
                    Title = title,
                    Message = message,
                    DedupeKey = key,
                    Venue = venue,
                    Environment = environment,
                    StrategyId = strategyId,
                    RecoveryHint = recoveryHint
                };
                await repo.UpsertAsync(alert, ct);
                log?.Invoke($"[{severity}] {title}: {message}");

                if (severity >= AlertSeverity.High && push != null)
                {
                    string prefix = severity == AlertSeverity.Critical ? "🔴 CRITICAL" : "🟠 HIGH";
                    string scope = string.Join(" · ", new[] { venue, environment, strategyId }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    string body = $"**{prefix} — OmniTrader**\n**{title}**\n{message}"
                                + (string.IsNullOrWhiteSpace(scope) ? "" : $"\n`{scope}`")
                                + (string.IsNullOrWhiteSpace(recoveryHint) ? "" : $"\n_Next: {recoveryHint}_")
                                + (severity == AlertSeverity.Critical ? "\n_Requires acknowledgement._" : "");
                    // A failed push must never swallow the alert — it is already durable in the store.
                    try { await push(body); } catch (Exception ex) { log?.Invoke($"alert push failed: {ex.Message}"); }
                }
                return alert;
            }
            finally { raiseLock.Release(); }
        }

        public Task<Alert> CriticalAsync(string category, string title, string message, string? dedupeKey = null,
            string? recoveryHint = null, CancellationToken ct = default)
            => RaiseAsync(AlertSeverity.Critical, category, title, message, dedupeKey, recoveryHint: recoveryHint, ct: ct);

        public Task<Alert> HighAsync(string category, string title, string message, string? dedupeKey = null,
            string? recoveryHint = null, CancellationToken ct = default)
            => RaiseAsync(AlertSeverity.High, category, title, message, dedupeKey, recoveryHint: recoveryHint, ct: ct);

        public Task<Alert> InfoAsync(string category, string title, string message, string? dedupeKey = null,
            CancellationToken ct = default)
            => RaiseAsync(AlertSeverity.Informational, category, title, message, dedupeKey, ct: ct);

        public async Task<bool> AcknowledgeAsync(string id, string who, CancellationToken ct = default)
        {
            var all = await repo.ListAsync(openOnly: false, limit: 500, ct);
            var alert = all.FirstOrDefault(a => a.Id == id);
            if (alert == null) return false;
            alert.AcknowledgedUtc = DateTime.UtcNow;
            alert.AcknowledgedBy = who;
            await repo.UpsertAsync(alert, ct);
            return true;
        }

        /// <summary>Resolve every open alert matching a condition key. Called by the code that fixes the
        /// underlying state, so an alert closes because the problem went away.</summary>
        public async Task<int> ResolveByDedupeAsync(string dedupeKey, CancellationToken ct = default)
        {
            var alert = await repo.FindOpenByDedupeAsync(dedupeKey, ct);
            if (alert == null) return 0;
            alert.ResolvedUtc = DateTime.UtcNow;
            await repo.UpsertAsync(alert, ct);
            return 1;
        }

        public async Task<bool> ResolveAsync(string id, CancellationToken ct = default)
        {
            var all = await repo.ListAsync(openOnly: true, limit: 500, ct);
            var alert = all.FirstOrDefault(a => a.Id == id);
            if (alert == null) return false;
            alert.ResolvedUtc = DateTime.UtcNow;
            await repo.UpsertAsync(alert, ct);
            return true;
        }

        public Task<List<Alert>> ListAsync(bool openOnly = true, int limit = 200, CancellationToken ct = default)
            => repo.ListAsync(openOnly, limit, ct);
    }
}
