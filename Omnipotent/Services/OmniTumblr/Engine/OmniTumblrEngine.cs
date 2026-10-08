using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    public sealed class EngineLoopStatus
    {
        public string Name { get; set; } = "";
        public string State { get; set; } = "stopped";
        public DateTime? LastTickUtc { get; set; }
        public DateTime? LastActivityUtc { get; set; }
        public string? LastActivity { get; set; }
        public string? LastError { get; set; }
        public DateTime? LastErrorUtc { get; set; }
        public long Ticks { get; set; }
    }

    /// <summary>
    /// The heartbeat: three independent loops, each woken early by a signal and otherwise polling.
    /// <list type="bullet">
    /// <item>publish — sleeps until the next post is due (≤20 s) and publishes it;</item>
    /// <item>plan — fills schedule slots and prepares posts (media info, thumbnails, captions);</item>
    /// <item>sync — analytics jobs on their own cadence, within the API budget.</item>
    /// </list>
    /// This replaces the TimeManager task chains v1 used, which double-fired after every restart (a
    /// replaced task's timer is never cancelled) and so could publish the same post twice.
    /// </summary>
    internal sealed class OmniTumblrEngine
    {
        private static readonly TimeSpan PublishPoll = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan PlanPoll = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan SyncPoll = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan PlanEachBlogEvery = TimeSpan.FromMinutes(10);

        private readonly OmniTumblrStore store;
        private readonly OmniTumblrPlanner planner;
        private readonly OmniTumblrPublisher publisher;
        private readonly OmniTumblrAnalyticsSync sync;
        private readonly Func<DateTime> clock;
        private readonly Action<Exception, string> logError;
        private readonly SemaphoreSlim publishWake = new(0, int.MaxValue);
        private readonly SemaphoreSlim planWake = new(0, int.MaxValue);
        private readonly SemaphoreSlim syncWake = new(0, int.MaxValue);
        private readonly ConcurrentDictionary<string, DateTime> lastPlannedUtc = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> forcedPlans = new(StringComparer.Ordinal);
        private CancellationTokenSource? cts;
        private Task[] loops = Array.Empty<Task>();

        public EngineLoopStatus PublishStatus { get; } = new() { Name = "publish" };
        public EngineLoopStatus PlanStatus { get; } = new() { Name = "plan" };
        public EngineLoopStatus SyncStatus { get; } = new() { Name = "sync" };
        public bool Running => cts != null && !cts.IsCancellationRequested;

        public OmniTumblrEngine(OmniTumblrStore store, OmniTumblrPlanner planner, OmniTumblrPublisher publisher,
            OmniTumblrAnalyticsSync sync, Func<DateTime> clock, Action<Exception, string> logError)
        {
            this.store = store;
            this.planner = planner;
            this.publisher = publisher;
            this.sync = sync;
            this.clock = clock;
            this.logError = logError;
        }

        public IReadOnlyList<EngineLoopStatus> Statuses => new[] { PublishStatus, PlanStatus, SyncStatus };

        public void Start()
        {
            if (Running) return;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            loops = new[]
            {
                Task.Run(() => RunLoopAsync(PublishStatus, publishWake, token, PublishTickAsync, NextPublishWait, TimeSpan.FromSeconds(5))),
                Task.Run(() => RunLoopAsync(PlanStatus, planWake, token, PlanTickAsync, () => PlanPoll, TimeSpan.FromSeconds(10))),
                Task.Run(() => RunLoopAsync(SyncStatus, syncWake, token, SyncTickAsync, () => SyncPoll, TimeSpan.FromSeconds(25))),
            };
        }

        public async Task StopAsync()
        {
            var source = cts;
            if (source == null) return;
            try { source.Cancel(); } catch { }
            try { await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            cts = null;
            foreach (var s in Statuses) s.State = "stopped";
        }

        public void WakePublisher() => Signal(publishWake);
        public void WakeSync() => Signal(syncWake);

        /// <summary>Plans now: one blog (bypassing the 10-minute throttle) or every blog.</summary>
        public void WakePlanner(string? blogId = null)
        {
            if (blogId != null) forcedPlans[blogId] = 0;
            Signal(planWake);
        }

        private static void Signal(SemaphoreSlim wake)
        {
            try { wake.Release(); } catch (SemaphoreFullException) { }
        }

        private async Task RunLoopAsync(EngineLoopStatus status, SemaphoreSlim wake, CancellationToken token,
            Func<CancellationToken, Task<string?>> tick, Func<TimeSpan> nextWait, TimeSpan initialDelay)
        {
            status.State = "starting";
            try { await Task.Delay(initialDelay, token); } catch (OperationCanceledException) { return; }
            while (!token.IsCancellationRequested)
            {
                status.State = "working";
                try
                {
                    string? activity = await tick(token);
                    status.LastTickUtc = clock();
                    status.Ticks++;
                    if (activity != null)
                    {
                        status.LastActivity = activity;
                        status.LastActivityUtc = status.LastTickUtc;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    status.LastError = ex.Message;
                    status.LastErrorUtc = clock();
                    logError(ex, $"OmniTumblr {status.Name} loop tick failed");
                }
                status.State = "idle";
                try
                {
                    var wait = nextWait();
                    if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
                    await wake.WaitAsync(wait, token);
                    while (wake.CurrentCount > 0) wake.Wait(0); // coalesce bursts of wakes
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            status.State = "stopped";
        }

        private TimeSpan NextPublishWait()
        {
            var next = publisher.NextDueUtc();
            if (next == null) return PublishPoll;
            var wait = next.Value - clock();
            return wait < PublishPoll ? wait : PublishPoll;
        }

        private async Task<string?> PublishTickAsync(CancellationToken token)
        {
            int published = await publisher.PublishDueAsync(token);
            if (published > 0) WakeSync();
            return published > 0 ? $"published {published} post(s)" : null;
        }

        private async Task<string?> PlanTickAsync(CancellationToken token)
        {
            DateTime now = clock();
            var blogIds = store.Read(s => s.Blogs.Values.Where(b => b.Autopilot && !b.Paused).Select(b => b.BlogId).ToList());
            int planned = 0;
            foreach (string blogId in blogIds)
            {
                if (token.IsCancellationRequested) break;
                bool forced = forcedPlans.TryRemove(blogId, out _);
                if (!forced && lastPlannedUtc.TryGetValue(blogId, out var last) && now - last < PlanEachBlogEvery) continue;
                lastPlannedUtc[blogId] = now;
                try
                {
                    var outcome = await planner.PlanBlogAsync(blogId, token);
                    planned += outcome.Planned;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logError(ex, $"OmniTumblr: planning blog {blogId} failed");
                }
            }
            foreach (string stale in forcedPlans.Keys.Except(blogIds).ToList()) forcedPlans.TryRemove(stale, out _);

            int prepared = await planner.PrepareDueAsync(4, token);
            if (prepared > 0) WakePublisher();
            if (planned == 0 && prepared == 0) return null;
            return $"planned {planned}, prepared {prepared}";
        }

        private async Task<string?> SyncTickAsync(CancellationToken token)
        {
            int ran = await sync.RunDueAsync(token);
            return ran > 0 ? $"ran {ran} sync job(s)" : null;
        }
    }
}
