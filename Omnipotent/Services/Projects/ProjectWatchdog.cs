using System.Collections.Concurrent;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Stall detection and escalation (design doc §9 + the one explicit developer note: "prevent
    /// the project from stalling before the goal is achieved"). A background loop, architecturally
    /// independent of the Commander's own execution, computes stall signals purely from the event
    /// log + digest — so it keeps working even if the Commander's wake logic is the thing that's
    /// wedged.
    ///
    /// On a stall it posts a diagnosis to Discord and starts a grace window; if Klives doesn't
    /// respond in time it force-wakes the Commander from the log (a forced fresh wake). It never
    /// hard-kills — consistent with the standing no-hard-timeouts rule.
    /// </summary>
    public class ProjectWatchdog
    {
        private readonly Projects parent;
        private readonly Action<string> log;
        private CancellationTokenSource? cts;

        // Per-project escalation state so a diagnosis + grace window fires once, not every tick.
        private sealed class Escalation
        {
            public DateTime RaisedUtc;
            public long EventCountAtRaise;
            public bool Diagnosed;
            public bool ForceWoken;
        }
        private readonly ConcurrentDictionary<string, Escalation> escalations = new(StringComparer.Ordinal);

        // Tunables (could become OmniSettings; kept as constants for V1).
        private static readonly TimeSpan MaxWakeGap = TimeSpan.FromMinutes(30);   // heartbeat staleness
        private static readonly TimeSpan GraceWindow = TimeSpan.FromMinutes(45);  // reply window before force-wake
        private const int ZeroProgressWakes = 3;                                  // wakes with no action = stalled
        private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(2);

        public ProjectWatchdog(Projects parent, Action<string> log)
        {
            this.parent = parent;
            this.log = log ?? (_ => { });
        }

        public void Start()
        {
            cts = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(cts.Token));
        }

        public void Stop() => cts?.Cancel();

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await TickAsync(); }
                catch (Exception ex) { log($"Watchdog tick failed: {ex.Message}"); }
                try { await Task.Delay(TickInterval, ct); } catch { break; }
            }
        }

        private async Task TickAsync()
        {
            foreach (var project in parent.Store.ListProjects())
            {
                if (project.Status != ProjectStatus.Active) { escalations.TryRemove(project.ProjectID, out _); continue; }

                var diagnosis = Diagnose(project);
                if (diagnosis == null)
                {
                    // Healthy — clear any standing escalation.
                    escalations.TryRemove(project.ProjectID, out _);
                    continue;
                }

                await HandleStallAsync(project, diagnosis);
            }
        }

        /// <summary>Returns a stall diagnosis string, or null if the project looks healthy.</summary>
        public string? Diagnose(Project project)
        {
            var digest = parent.Digests.GetDigest(project.ProjectID);
            var tail = parent.EventLog.ReadTail(project.ProjectID, 300);
            var now = DateTime.UtcNow;

            // 1. Heartbeat staleness: no wake within MaxWakeGap, on a project that has some history
            //    (so a brand-new idle project isn't flagged before it has ever run). A keepalive
            //    timer hook should fire at least this often; its absence is the "no stimuli" stall.
            var lastWake = tail.LastOrDefault(e => e.Type == ProjectEventTypes.CommanderWake);
            if (tail.Count > 0 && (lastWake == null || now - lastWake.Timestamp > MaxWakeGap))
                return $"No Commander wake in over {MaxWakeGap.TotalMinutes:0} minutes (last: {(lastWake == null ? "never" : lastWake.Timestamp.ToString("u"))}).";

            // 2. Zero-progress-over-N-wakes: the last N completed wakes produced no tool-call /
            //    artifact / spawn events, and the plan hasn't changed.
            var recentWakeStarts = tail.Where(e => e.Type == ProjectEventTypes.CommanderWake).TakeLast(ZeroProgressWakes).ToList();
            if (recentWakeStarts.Count >= ZeroProgressWakes)
            {
                long firstWakeSeq = recentWakeStarts[0].Sequence;
                bool anyProgress = tail.Any(e => e.Sequence >= firstWakeSeq &&
                    (e.Type is ProjectEventTypes.ToolCall or ProjectEventTypes.ArtifactAdded or ProjectEventTypes.AgentSpawned or ProjectEventTypes.MoneySpent));
                if (!anyProgress)
                    return $"No progress across the last {ZeroProgressWakes} wakes (no tool calls, artifacts, or spawns).";
            }

            // 3. Repeated-action loops: the last wake tripped the stuck-loop guard heavily.
            if (digest.RecentStuckLoopTrips >= 3)
                return $"Commander tripped its stuck-loop guard {digest.RecentStuckLoopTrips}× in the last wake.";

            return null;
        }

        private async Task HandleStallAsync(Project project, string diagnosis)
        {
            long eventCount = parent.EventLog.GetLastSequence(project.ProjectID);
            var esc = escalations.GetOrAdd(project.ProjectID, _ => new Escalation
            {
                RaisedUtc = DateTime.UtcNow,
                EventCountAtRaise = eventCount,
            });

            // Any new event since we raised (a Klives reply, or the Commander stirring) counts as a
            // response — clear the escalation and let the next tick re-evaluate from scratch.
            if (esc.Diagnosed && eventCount > esc.EventCountAtRaise + WatchdogSelfEvents)
            {
                escalations.TryRemove(project.ProjectID, out _);
                return;
            }

            // First detection: post the diagnosis + start the grace window (once).
            if (!esc.Diagnosed)
            {
                esc.Diagnosed = true;
                parent.EventLog.Append(new ProjectEvent
                {
                    ProjectID = project.ProjectID,
                    Type = ProjectEventTypes.WatchdogEscalation,
                    Author = "system",
                    Text = $"Watchdog: {diagnosis} Escalated to Klives; auto-rehydrating the Commander if there's no response within {GraceWindow.TotalMinutes:0} minutes.",
                });
                if (parent.DiscordManager != null)
                    await parent.DiscordManager.PostReportAsync(project, "⚠️ Project stalled",
                        $"{diagnosis}\n\nReply here to steer, or I'll auto-rehydrate the Commander in {GraceWindow.TotalMinutes:0} minutes.");
                log($"Watchdog escalated project {project.ProjectID}: {diagnosis}");
                return;
            }

            // Grace window elapsed with no response → force a fresh wake (never a kill).
            if (!esc.ForceWoken && DateTime.UtcNow - esc.RaisedUtc >= GraceWindow)
            {
                esc.ForceWoken = true;
                parent.EventLog.Append(new ProjectEvent
                {
                    ProjectID = project.ProjectID,
                    Type = ProjectEventTypes.WatchdogEscalation,
                    Author = "system",
                    Text = "Watchdog grace window elapsed with no response — force-waking the Commander (fresh rehydration, no state lost).",
                });
                parent.CommanderRunner.ForceWake(project, diagnosis);
                escalations.TryRemove(project.ProjectID, out _); // re-evaluate cleanly next tick
            }
        }

        /// <summary>The watchdog's own escalation event is written to the log; don't count it as a response.</summary>
        private const int WatchdogSelfEvents = 1;
    }
}
