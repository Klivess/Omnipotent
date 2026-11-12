using Omnipotent.Services.KliveAgent.Models;

namespace Omnipotent.Services.KliveAgent
{
    /// <summary>One thing the watch wants Klives to know. Kind is "scripts" or "provider".</summary>
    public sealed record KliveAgentHealthAlert(string Kind, string Title, string Message);

    /// <summary>
    /// Two rolling checks that turn a slow decline into one message.
    ///
    /// Script failures climbed from 36% to 46% between October and November 2026, and on six days the
    /// provider averaged over 50 s a call. From the chat both looked like "KliveAgent is slow today" and
    /// neither told anyone. The watch says so once, then stays quiet for its cooldown.
    /// </summary>
    public partial class KliveAgentStats
    {
        /// <summary>The script check looks at this many of the newest scripts, and only once it has them all.</summary>
        internal const int WatchScriptWindow = 50;
        /// <summary>Below this share of scripts running, the script check speaks.</summary>
        internal const double WatchFirstPassFloorPct = 60.0;
        internal static readonly TimeSpan WatchScriptCooldown = TimeSpan.FromHours(24);

        internal static readonly TimeSpan WatchProviderWindow = TimeSpan.FromMinutes(15);
        /// <summary>Fewer calls than this in the window is one slow call, not a slow provider.</summary>
        internal const int WatchProviderMinCalls = 6;
        internal const long WatchProviderWaitLimitMs = 30_000;
        internal static readonly TimeSpan WatchProviderCooldown = TimeSpan.FromHours(6);

        /// <summary>Where an alert goes. Set by the service; unset in tests that only read the state.</summary>
        public Action<KliveAgentHealthAlert>? Alert { get; set; }

        /// <summary>The watch's clock, replaced in tests.</summary>
        internal Func<DateTime> WatchClock { get; set; } = () => DateTime.UtcNow;

        private readonly object _watchLock = new();
        /// <summary>Newest scripts, oldest first: "" for one that ran, else what its failure was filed under.</summary>
        private readonly Queue<string> _watchScripts = new();
        private readonly Queue<(DateTime At, long WaitMs)> _watchWaits = new();
        private DateTime? _watchScriptAlertAt;
        private DateTime? _watchProviderAlertAt;

        private void WatchScript(AgentScriptResult result)
        {
            // A script someone stopped says nothing about how well scripts are written.
            if (!result.Success && result.FailureKind == "stopped") return;

            KliveAgentHealthAlert? alert = null;
            lock (_watchLock)
            {
                _watchScripts.Enqueue(result.Success ? "" : KliveAgentBrain.ClassifyScriptError(result.ErrorMessage));
                while (_watchScripts.Count > WatchScriptWindow) _watchScripts.Dequeue();
                if (_watchScripts.Count < WatchScriptWindow) return;

                int failed = _watchScripts.Count(code => code.Length > 0);
                double ran = (WatchScriptWindow - failed) * 100.0 / WatchScriptWindow;
                if (ran >= WatchFirstPassFloorPct) return;

                var now = WatchClock();
                if (_watchScriptAlertAt is { } last && now - last < WatchScriptCooldown) return;
                _watchScriptAlertAt = now;

                var common = _watchScripts.Where(code => code.Length > 0)
                    .GroupBy(code => code, StringComparer.Ordinal)
                    .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
                    .Take(3).Select(group => $"{group.Key} ×{group.Count()}");
                alert = new KliveAgentHealthAlert("scripts", "Script failures are up",
                    $"KliveAgent's scripts are failing more than usual: {failed} of the last {WatchScriptWindow} did not run "
                    + $"(the alert level is {100 - WatchFirstPassFloorPct:0}%). Most common: {string.Join(", ", common)}. "
                    + "The KliveAgent page, Insights, Script health has the breakdown.");
            }
            Raise(alert);
        }

        /// <summary>
        /// One model call's wait for the provider to start answering, in ms. Only streamed calls have one:
        /// a buffered call's wait is its whole reply and says nothing about how quickly the provider started.
        /// </summary>
        public void RecordModelWait(long waitMs)
        {
            if (waitMs < 0) return;
            KliveAgentHealthAlert? alert = null;
            lock (_watchLock)
            {
                var now = WatchClock();
                _watchWaits.Enqueue((now, waitMs));
                TrimWaitsLocked(now);
                if (_watchWaits.Count < WatchProviderMinCalls) return;

                long median = MedianWaitLocked();
                if (median <= WatchProviderWaitLimitMs) return;
                if (_watchProviderAlertAt is { } last && now - last < WatchProviderCooldown) return;
                _watchProviderAlertAt = now;

                alert = new KliveAgentHealthAlert("provider", "The model provider is slow",
                    $"The model provider is slow right now: over the last {WatchProviderWindow.TotalMinutes:0} minutes KliveAgent "
                    + $"waited a median of {median / 1000.0:0} s for each reply to start ({_watchWaits.Count} calls). "
                    + "That is time after the request was sent, not time in the server's own queue, so replies stay slow until the provider recovers.");
            }
            Raise(alert);
        }

        private void Raise(KliveAgentHealthAlert? alert)
        {
            if (alert == null) return;
            // The time of the alert is what keeps a restart from repeating it.
            QueueSave();
            try { Alert?.Invoke(alert); }
            catch (Exception ex) { ErrorLogger?.Invoke(ex, "[KliveAgent] A health alert could not be delivered."); }
        }

        private void TrimWaitsLocked(DateTime now)
        {
            while (_watchWaits.Count > 0 && now - _watchWaits.Peek().At > WatchProviderWindow) _watchWaits.Dequeue();
        }

        private long MedianWaitLocked()
        {
            var sorted = _watchWaits.Select(wait => wait.WaitMs).OrderBy(ms => ms).ToList();
            int middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        }

        /// <summary>What the two checks see right now, for the script-health route.</summary>
        public object BuildWatch()
        {
            lock (_watchLock)
            {
                TrimWaitsLocked(WatchClock());
                int sampled = _watchScripts.Count;
                int failed = _watchScripts.Count(code => code.Length > 0);
                double? ran = sampled > 0 ? Math.Round((sampled - failed) * 100.0 / sampled, 1) : null;
                long? median = _watchWaits.Count > 0 ? MedianWaitLocked() : null;
                return new
                {
                    scripts = new
                    {
                        window = WatchScriptWindow,
                        sampled,
                        failed,
                        firstPassPct = ran,
                        floorPct = WatchFirstPassFloorPct,
                        belowFloor = sampled >= WatchScriptWindow && ran < WatchFirstPassFloorPct,
                        lastAlertAt = _watchScriptAlertAt,
                    },
                    provider = new
                    {
                        windowMinutes = (int)WatchProviderWindow.TotalMinutes,
                        calls = _watchWaits.Count,
                        medianWaitMs = median,
                        limitMs = WatchProviderWaitLimitMs,
                        slow = _watchWaits.Count >= WatchProviderMinCalls && median > WatchProviderWaitLimitMs,
                        lastAlertAt = _watchProviderAlertAt,
                    },
                };
            }
        }

        /// <summary>The part of the watch worth keeping across a restart: the script window and when each
        /// check last spoke, so fifteen restarts in a day are still one message.</summary>
        private (List<string> Scripts, DateTime? ScriptAlertAt, DateTime? ProviderAlertAt) WatchState()
        {
            lock (_watchLock) return (_watchScripts.ToList(), _watchScriptAlertAt, _watchProviderAlertAt);
        }

        private void RestoreWatch(List<string>? scripts, DateTime? scriptAlertAt, DateTime? providerAlertAt)
        {
            lock (_watchLock)
            {
                _watchScripts.Clear();
                foreach (var code in (scripts ?? new List<string>()).TakeLast(WatchScriptWindow)) _watchScripts.Enqueue(code ?? "");
                _watchScriptAlertAt = scriptAlertAt;
                _watchProviderAlertAt = providerAlertAt;
            }
        }
    }
}
