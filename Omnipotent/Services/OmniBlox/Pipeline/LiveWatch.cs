using Omnipotent.Services.OmniBlox.Core;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>
    /// The read-only watch over the games already out, run whether or not the studio is switched on: whether players
    /// can join them (<see cref="PlayerReach"/>, every 30 minutes) and Roblox's analytics (every 12 hours). Switching
    /// the studio off stops work on games, not the dashboard's view of the ones players have: when it lived in the
    /// orchestrator's housekeeping, the live game's numbers froze the moment the studio was switched off (Oct 27 2026).
    /// No model calls, no writes to Roblox.
    /// </summary>
    public sealed class LiveWatch
    {
        public static readonly TimeSpan ReachEvery = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan AnalyticsEvery = TimeSpan.FromHours(12);

        private readonly StudioContext ctx;
        private DateTime lastReachUtc = DateTime.MinValue;
        private DateTime lastAnalyticsUtc = DateTime.MinValue;

        public LiveWatch(StudioContext ctx) => this.ctx = ctx;

        /// <summary>Runs whatever is due.</summary>
        public async Task StepAsync(CancellationToken ct)
        {
            var now = ctx.Clock();
            var games = ctx.Store.Read(s => s.Games.Values
                .Where(g => g.Stage is GameStage.Live or GameStage.Sunset && g.Slot != null)
                .Select(g => (g.GameId, g.Publish.IsPublic)).ToList());

            if (now - lastReachUtc >= ReachEvery)
            {
                lastReachUtc = now;
                foreach (var (id, _) in games.Where(g => g.IsPublic))
                {
                    try { await PlayerReach.CheckAsync(ctx, id, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Log($"[OmniBlox] reach check {id}: {ex.Message}"); }
                }
            }

            if (ctx.Cloud.HasKey && now - lastAnalyticsUtc >= AnalyticsEvery)
            {
                lastAnalyticsUtc = now;
                foreach (var (id, _) in games)
                {
                    try { await ctx.Analytics.SyncAsync(id, ct, 3); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Log($"[OmniBlox] analytics sync {id}: {ex.Message}"); }
                }
                try { ctx.Telemetry.Prune(now); } catch (Exception ex) { ctx.Log("[OmniBlox] telemetry prune: " + ex.Message); }
            }
        }
    }
}
