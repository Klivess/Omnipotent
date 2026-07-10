using System.Collections.Concurrent;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Stall detection and SELF-HEALING (design doc §9 + the one explicit developer note: "prevent
    /// the project from stalling before the goal is achieved"). A background loop, architecturally
    /// independent of the Commander's own execution, computes stall signals purely from the event
    /// log + digest — so it keeps working even if the Commander's wake logic is the thing that's
    /// wedged.
    ///
    /// Recovery is automatic and silent: a diagnosed stall force-wakes the Commander immediately
    /// (never a hard kill — the standing no-hard-timeouts rule). Klives is only pinged when the
    /// watchdog's own medicine isn't working — repeated recoveries inside a rolling window, or
    /// wakes that keep failing outright — and never more than once per cooldown. An unanswered
    /// approval gate is "waiting on Klives", not a stall: it gets one aged reminder per gate and
    /// is never force-woken (that would cancel the gate wait).
    /// </summary>
    public class ProjectWatchdog
    {
        private readonly Projects parent;
        private readonly Action<string> log;
        private CancellationTokenSource? cts;

        // Per-project self-heal state. Recovery attempts are tracked in a rolling window so the
        // watchdog can tell "one bad wake" (heal silently) from "it keeps happening" (tell Klives).
        private sealed class HealState
        {
            public readonly List<DateTime> ForceWakesUtc = new();
            public DateTime LastForceWakeUtc = DateTime.MinValue;
            public DateTime LastEscalationUtc = DateTime.MinValue;
            public readonly HashSet<string> RemindedGateIDs = new(StringComparer.Ordinal);
        }
        private readonly ConcurrentDictionary<string, HealState> heals = new(StringComparer.Ordinal);

        // Tunables (could become OmniSettings; kept as constants for V1).
        private static readonly TimeSpan MaxWakeGap = TimeSpan.FromMinutes(30);      // heartbeat staleness
        private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ForceWakePacing = TimeSpan.FromMinutes(15); // min gap between self-heals
        private static readonly TimeSpan HealWindow = TimeSpan.FromHours(3);         // window for "it keeps happening"
        private const int HealsBeforeEscalation = 3;                                 // heals in window before pinging Klives
        private static readonly TimeSpan EscalationCooldown = TimeSpan.FromHours(6); // max one ping per project per this
        private static readonly TimeSpan GateReminderAge = TimeSpan.FromHours(2);    // unanswered gate → one reminder
        private const int ZeroProgressWakes = 3;                                     // wakes with no action = stalled

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
                if (project.Status is not (ProjectStatus.Active or ProjectStatus.Planning)) { heals.TryRemove(project.ProjectID, out _); continue; }

                // A pending approval gate means the project is waiting on Klives, not stalled.
                // Never force-wake it (that cancels the gate wait); remind him once per aged gate.
                var pending = parent.Gates?.ListPending(project.ProjectID) ?? new List<ProjectGate>();
                if (pending.Count > 0)
                {
                    await RemindAgedGatesAsync(project, pending);
                    continue;
                }

                var (diagnosis, wedgedAgentID) = DiagnoseCore(project);
                if (diagnosis == null) continue;

                await SelfHealAsync(project, diagnosis, wedgedAgentID);
            }
        }

        /// <summary>Returns a stall diagnosis string, or null if the project looks healthy.</summary>
        public string? Diagnose(Project project) => DiagnoseCore(project).diagnosis;

        private (string? diagnosis, string? wedgedAgentID) DiagnoseCore(Project project)
        {
            var digest = parent.Digests.GetDigest(project.ProjectID);
            var tail = parent.EventLog.ReadTail(project.ProjectID, 300);
            var now = DateTime.UtcNow;

            // Waiting on Klives is never a stall (also short-circuited in the tick loop; kept
            // here so direct callers of Diagnose get the same answer).
            if ((parent.Gates?.ListPending(project.ProjectID).Count ?? 0) > 0) return (null, null);

            // 1. Heartbeat staleness — measured on COMMANDER ACTIVITY, not just wake starts. A
            //    single long wake emits tool calls and thoughts for hours without a new
            //    CommanderWake event, and must not read as stalled while it is visibly working.
            //    The keepalive guarantees a wake at least every ~15 min when healthy, so a 30-min
            //    activity gap really does mean the wake pipeline is wedged.
            var lastActivity = tail.Where(e => IsCommanderActivity(e)).Select(e => (DateTime?)e.Timestamp).Max();
            var lastBeat = lastActivity ?? project.CreatedAt;
            if (tail.Count > 0 && now - lastBeat > MaxWakeGap)
                return ($"No Commander activity in over {MaxWakeGap.TotalMinutes:0} minutes (last: {(lastActivity == null ? "never" : lastActivity.Value.ToString("u"))}).", null);

            // 2. Zero-progress-over-N-wakes: the last N completed wakes produced no tool-call /
            //    artifact / spawn / sub-agent-activity events, and the plan hasn't changed.
            var recentWakeStarts = tail.Where(e => e.Type == ProjectEventTypes.CommanderWake).TakeLast(ZeroProgressWakes).ToList();
            if (recentWakeStarts.Count >= ZeroProgressWakes)
            {
                long firstWakeSeq = recentWakeStarts[0].Sequence;
                bool anyProgress = tail.Any(e => e.Sequence >= firstWakeSeq &&
                    (e.Type is ProjectEventTypes.ToolCall or ProjectEventTypes.ArtifactAdded
                        or ProjectEventTypes.AgentSpawned or ProjectEventTypes.MoneySpent
                        or ProjectEventTypes.AgentWake or ProjectEventTypes.AgentMessage or ProjectEventTypes.AgentThought));
                if (!anyProgress)
                    return ($"No progress across the last {ZeroProgressWakes} wakes (no tool calls, artifacts, spawns, or sub-agent activity).", null);
            }

            // 3. Repeated-action loops: the last wake tripped the stuck-loop guard heavily.
            if (digest.RecentStuckLoopTrips >= 3)
                return ($"Commander tripped its stuck-loop guard {digest.RecentStuckLoopTrips}× in its last wake.", null);

            // 4. Wedged sub-agent: it woke, never finished within MaxWakeGap, AND has gone silent
            //    (a live worker emits tool calls constantly — silence, not duration, is the wedge
            //    signal, so a slow-but-working desktop agent is left alone).
            var staleAgentWake = tail
                .Where(e => e.Type == ProjectEventTypes.AgentWake && now - e.Timestamp > MaxWakeGap)
                .FirstOrDefault(w => !tail.Any(e => e.WakeID == w.WakeID &&
                        (e.Type is ProjectEventTypes.WakeCompleted or ProjectEventTypes.WakeFailed))
                    && !tail.Any(e => e.AgentID == w.AgentID && now - e.Timestamp <= MaxWakeGap));
            if (staleAgentWake != null)
                return ($"Sub-agent {staleAgentWake.AgentID} has been awake without finishing or emitting any activity for over {MaxWakeGap.TotalMinutes:0} minutes.", staleAgentWake.AgentID);

            return (null, null);
        }

        /// <summary>Commander-side liveness: wake starts, thoughts, closing messages, and any
        /// tool activity or wake outcome attributed to the commander.</summary>
        private static bool IsCommanderActivity(ProjectEvent e) =>
            e.Type is ProjectEventTypes.CommanderWake or ProjectEventTypes.CommanderThought or ProjectEventTypes.CommanderMessage
            || (e.AgentID == "commander" &&
                e.Type is ProjectEventTypes.ToolCall or ProjectEventTypes.ToolResult
                    or ProjectEventTypes.WakeCompleted or ProjectEventTypes.WakeFailed);

        /// <summary>
        /// Recovers a diagnosed stall without involving Klives: cancel a wedged sub-agent if that
        /// is the diagnosis, then force-wake the Commander with the diagnosis as its trigger.
        /// Klives is pinged only when recoveries keep happening or wakes keep failing outright.
        /// </summary>
        private async Task SelfHealAsync(Project project, string diagnosis, string? wedgedAgentID)
        {
            var heal = heals.GetOrAdd(project.ProjectID, _ => new HealState());
            var now = DateTime.UtcNow;

            // Provider-outage guard: if wakes themselves keep failing, another force-wake is just
            // another failure — the keepalive's exponential backoff owns retries. Tell Klives
            // (rate-limited) because this is usually a provider/credit problem only he can fix.
            var outcomes = parent.EventLog.ReadTail(project.ProjectID, 40)
                .Where(e => e.Type is ProjectEventTypes.WakeCompleted or ProjectEventTypes.WakeFailed).ToList();
            int consecutiveFailures = 0;
            for (int i = outcomes.Count - 1; i >= 0 && outcomes[i].Type == ProjectEventTypes.WakeFailed; i--) consecutiveFailures++;
            if (consecutiveFailures >= 3)
            {
                await EscalateAsync(project, heal,
                    $"Wakes are failing repeatedly ({consecutiveFailures} in a row — most recent: {Trunc(outcomes[^1].Text, 200)}). " +
                    "This usually means the LLM provider, its credit, or the model configuration is the problem rather than the project itself. Retries continue automatically with backoff.");
                return;
            }

            if (now - heal.LastForceWakeUtc < ForceWakePacing) return; // healed recently — give it time to land

            // Consume the stuck-loop signal so one bad wake can't re-trigger this diagnosis forever.
            var digest = parent.Digests.GetDigest(project.ProjectID);
            if (digest.RecentStuckLoopTrips > 0)
            {
                digest.RecentStuckLoopTrips = 0;
                parent.Digests.SaveDigest(digest);
            }

            // A wedged sub-agent holds its single-flight slot until cancelled; free it so the
            // force-woken Commander can re-dispatch the work.
            if (wedgedAgentID != null)
            {
                try { parent.SubAgentRunner.CancelAgent(project.ProjectID, wedgedAgentID); } catch { }
            }

            heal.LastForceWakeUtc = now;
            heal.ForceWakesUtc.Add(now);
            heal.ForceWakesUtc.RemoveAll(t => now - t > HealWindow);

            parent.EventLog.Append(new ProjectEvent
            {
                ProjectID = project.ProjectID,
                Type = ProjectEventTypes.WatchdogEscalation,
                Author = "system",
                Text = $"Watchdog: {diagnosis} Self-healing — force-waking the Commander (recovery {heal.ForceWakesUtc.Count} in the last {HealWindow.TotalHours:0}h; Klives is only pinged if this keeps happening).",
            });
            log($"Watchdog self-heal on project {project.ProjectID}: {diagnosis}");
            parent.CommanderRunner.ForceWake(project,
                $"{diagnosis} The watchdog force-woke you to recover. Assess what wedged, avoid repeating it (change approach if you were looping), and continue toward the goal.");

            if (heal.ForceWakesUtc.Count >= HealsBeforeEscalation)
                await EscalateAsync(project, heal,
                    $"{diagnosis} I've auto-recovered the Commander {heal.ForceWakesUtc.Count}× in the last {HealWindow.TotalHours:0} hours, but it keeps stalling — it may need your steering.");
        }

        /// <summary>One aged reminder per unanswered gate — waiting on Klives is his stall, not the project's.</summary>
        private async Task RemindAgedGatesAsync(Project project, List<ProjectGate> pending)
        {
            var heal = heals.GetOrAdd(project.ProjectID, _ => new HealState());
            foreach (var gate in pending)
            {
                if (DateTime.UtcNow - gate.CreatedAt < GateReminderAge) continue;
                if (!heal.RemindedGateIDs.Add(gate.GateID)) continue;
                parent.EventLog.Append(new ProjectEvent
                {
                    ProjectID = project.ProjectID,
                    Type = ProjectEventTypes.WatchdogEscalation,
                    Author = "system",
                    Text = $"Watchdog: approval '{gate.Title}' has been waiting on Klives for over {GateReminderAge.TotalHours:0}h — reminded him.",
                });
                if (parent.DiscordManager != null)
                    await parent.DiscordManager.PostAttentionAsync(project, "⏳ Waiting on your approval",
                        $"'{gate.Title}' has been pending for over {(DateTime.UtcNow - gate.CreatedAt).TotalHours:0} hours. The project is blocked until you approve or deny it.");
            }
        }

        /// <summary>Rate-limited attention ping: at most one per project per cooldown, always with
        /// what the watchdog already tried, so a ping means "I couldn't fix this myself".</summary>
        private async Task EscalateAsync(Project project, HealState heal, string message)
        {
            var now = DateTime.UtcNow;
            if (now - heal.LastEscalationUtc < EscalationCooldown) return;
            heal.LastEscalationUtc = now;

            parent.EventLog.Append(new ProjectEvent
            {
                ProjectID = project.ProjectID,
                Type = ProjectEventTypes.WatchdogEscalation,
                Author = "system",
                Text = $"Watchdog escalation to Klives: {message}",
            });
            if (parent.DiscordManager != null)
                await parent.DiscordManager.PostAttentionAsync(project, "⚠️ Project needs attention",
                    $"{message}\n\nReply here to steer the Commander. Automatic recovery keeps running regardless.");
            log($"Watchdog escalated project {project.ProjectID}: {message}");
        }

        private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
    }
}
