using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAgent.Models;
using System.Collections.Concurrent;
using System.Text;

namespace Omnipotent.Services.KliveAgent
{
    /// <summary>
    /// KliveAgent's prospective memory: durable future intentions that fire as FULL agent turns.
    /// The agent schedules them itself (schedule_task) — "check the deploy in 2h", "every morning
    /// summarise overnight errors" — and the firing turn runs with every tool available, then its
    /// answer is delivered to Klives on Discord. Tasks live one-per-file on disk (same pattern as
    /// memories), so they survive restarts; a task whose due time passed while Omnipotent was down
    /// fires on the next loop tick with an explicit lateness note, because a temporally honest
    /// agent must know it is acting late.
    /// </summary>
    public class KliveAgentScheduler
    {
        /// <summary>Floor for recurrence so a runaway recurring task can't hammer the LLM.</summary>
        public static readonly TimeSpan MinimumRepeatInterval = TimeSpan.FromMinutes(5);
        /// <summary>Firing more than this late gets an explicit "late" note in the wake message.</summary>
        public static readonly TimeSpan LatenessNoteThreshold = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(20);
        private const int MaxActiveTasks = 64;

        private readonly KliveAgent service;
        private readonly ConcurrentDictionary<string, AgentScheduledTask> tasks = new();
        private readonly HashSet<string> firing = new(StringComparer.Ordinal);
        private readonly object firingLock = new();

        public KliveAgentScheduler(KliveAgent service)
        {
            this.service = service;
        }

        public async Task InitializeAsync()
        {
            var dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentScheduledTasksDirectory);
            if (!Directory.Exists(dir))
            {
                await service.GetDataHandler().CreateDirectory(dir);
                return;
            }
            int loaded = 0;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var task = await service.GetDataHandler().ReadAndDeserialiseDataFromFile<AgentScheduledTask>(file);
                    if (task == null || string.IsNullOrWhiteSpace(task.Id)) continue;
                    tasks[task.Id] = task;
                    loaded++;
                }
                catch { }
            }
            if (loaded > 0)
            {
                int due = tasks.Values.Count(t => t.Enabled && t.DueAtUtc <= DateTime.UtcNow);
                await service.ServiceLog($"[KliveAgent] Restored {loaded} scheduled task(s){(due > 0 ? $"; {due} came due while offline and will fire now" : "")}.");
            }
        }

        /// <summary>Starts the firing loop. Call once, after the agent is ready to take messages.</summary>
        public void StartLoop(CancellationToken cancellationToken = default)
        {
            _ = Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(LoopInterval, cancellationToken);
                        if (!service.TryGetApiAvailability(out _, out _)) continue;

                        var now = DateTime.UtcNow;
                        var due = tasks.Values
                            .Where(t => t.Enabled && t.DueAtUtc <= now && !IsFiring(t.Id))
                            .OrderBy(t => t.DueAtUtc)
                            .ToList();
                        // Serialize firings: parallel scheduled turns would contend for shared
                        // conversations and stack LLM load for no benefit.
                        foreach (var task in due)
                            await FireAsync(task);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        try { await service.ServiceLogError(ex, "[KliveAgent] Scheduler loop iteration failed (non-fatal)."); } catch { }
                    }
                }
            }, cancellationToken);
        }

        private bool IsFiring(string id) { lock (firingLock) return firing.Contains(id); }

        private async Task FireAsync(AgentScheduledTask task)
        {
            lock (firingLock)
            {
                if (!firing.Add(task.Id)) return;
            }
            try
            {
                var now = DateTime.UtcNow;
                string message = ComposeFireMessage(task, now);
                string conversationId = string.IsNullOrWhiteSpace(task.ConversationId)
                    ? $"scheduled-{task.Id}"
                    : task.ConversationId;

                AgentChatResponse response;
                try
                {
                    response = await service.HandleIncomingMessage(
                        message, AgentSourceChannel.API, conversationId, senderName: "Scheduler");
                }
                catch (Exception ex)
                {
                    response = new AgentChatResponse { Success = false, Response = $"(scheduled turn crashed: {ex.Message})" };
                }

                task.LastFiredAt = now;
                task.FiredCount++;
                task.LastOutcome = Truncate(response?.Response, 500);
                if (task.RepeatEvery.HasValue)
                    task.DueAtUtc = AdvanceRecurrence(task.DueAtUtc, task.RepeatEvery.Value, DateTime.UtcNow);
                else
                    task.Enabled = false;
                await PersistAsync(task);

                // Report to Klives — a scheduled action he never hears about is a silent success or
                // a silent failure, and both erode trust in delegation.
                try
                {
                    string header = response?.Success == true ? "⏰" : "⏰⚠️";
                    await service.ExecuteServiceMethod<Services.KliveBot_Discord.KliveBotDiscord>(
                        "SendMessageToKlives",
                        $"{header} Scheduled task fired ({TemporalFormat.Stamp(now)}): {Truncate(task.Instruction, 160)}\n{Truncate(response?.Response, 1200)}");
                }
                catch { }
            }
            finally
            {
                lock (firingLock) firing.Remove(task.Id);
            }
        }

        /// <summary>The message the agent's future self receives — self-explanatory with full temporal context.</summary>
        internal static string ComposeFireMessage(AgentScheduledTask task, DateTime nowUtc)
        {
            string late = nowUtc - task.DueAtUtc > LatenessNoteThreshold
                ? $" It is firing {TemporalFormat.Span(nowUtc - task.DueAtUtc)} LATE (Omnipotent was likely offline at the due time) — judge whether the instruction is still relevant before acting."
                : "";
            string repeat = task.RepeatEvery.HasValue
                ? $" This task repeats every {TemporalFormat.Span(task.RepeatEvery.Value)} (firing #{task.FiredCount + 1})."
                : "";
            return $"⏰ SCHEDULED TASK {ShortId(task.Id)} FIRED — you set this for yourself {TemporalFormat.StampWithAge(task.CreatedAt)}, due {TemporalFormat.Stamp(task.DueAtUtc)}.{late}{repeat}\n" +
                   $"Carry it out NOW with your tools and finish with a report of the outcome (it is delivered to Klives): {task.Instruction}";
        }

        /// <summary>
        /// Next due time for a recurring task: advances past now WITHOUT firing once per missed
        /// interval — if Omnipotent slept through five occurrences, the task fires once (late) and
        /// then resumes its rhythm at the next future slot.
        /// </summary>
        internal static DateTime AdvanceRecurrence(DateTime dueAtUtc, TimeSpan interval, DateTime nowUtc)
        {
            if (interval <= TimeSpan.Zero) interval = MinimumRepeatInterval;
            var next = dueAtUtc;
            while (next <= nowUtc) next += interval;
            return next;
        }

        // ── agent-facing operations (returns are model-readable strings) ──

        public async Task<string> ScheduleAsync(string instruction, string dueAtText, string repeatEveryText = null, string conversationId = null)
        {
            if (string.IsNullOrWhiteSpace(instruction))
                return "Error: 'instruction' is required — tell your future self exactly what to do.";
            var now = DateTime.UtcNow;
            if (!TemporalParse.TryParseFutureInstant(dueAtText, now, out var dueAt))
                return $"Error: could not parse dueAt '{dueAtText}'. Use a UTC date-time like '2026-07-15 09:00' or a relative delay like 'in 2h30m' / '45m'. Current time: {TemporalFormat.NowStamp()}.";
            if (dueAt <= now)
                return $"Error: dueAt {TemporalFormat.Stamp(dueAt)} is in the past — current time is {TemporalFormat.NowStamp()}. Schedule a future instant.";

            TimeSpan? repeat = null;
            if (!string.IsNullOrWhiteSpace(repeatEveryText))
            {
                if (!TemporalParse.TryParseDuration(repeatEveryText, out var interval))
                    return $"Error: could not parse repeatEvery '{repeatEveryText}'. Use a duration like '1d', '2h30m', '45m'.";
                if (interval < MinimumRepeatInterval)
                    return $"Error: repeatEvery must be at least {TemporalFormat.Span(MinimumRepeatInterval)} (got {TemporalFormat.Span(interval)}).";
                repeat = interval;
            }

            if (tasks.Values.Count(t => t.Enabled) >= MaxActiveTasks)
                return $"Error: {MaxActiveTasks} scheduled tasks already active — cancel stale ones first (list_scheduled_tasks / cancel_scheduled_task).";

            var task = new AgentScheduledTask
            {
                Instruction = instruction.Trim(),
                DueAtUtc = dueAt,
                RepeatEvery = repeat,
                ConversationId = conversationId,
            };
            tasks[task.Id] = task;
            await PersistAsync(task);
            return $"Scheduled task {ShortId(task.Id)}: fires {TemporalFormat.StampWithAge(dueAt)}" +
                   (repeat.HasValue ? $", repeating every {TemporalFormat.Span(repeat.Value)}" : " (one-shot)") +
                   ". It runs as a full agent turn (all tools) and the outcome is reported to Klives.";
        }

        public async Task<string> CancelAsync(string idOrPrefix)
        {
            if (string.IsNullOrWhiteSpace(idOrPrefix)) return "Error: 'id' is required (from list_scheduled_tasks).";
            var matches = tasks.Values
                .Where(t => t.Enabled && t.Id.StartsWith(idOrPrefix.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0) return $"No active scheduled task matches '{idOrPrefix}'.";
            if (matches.Count > 1) return $"'{idOrPrefix}' is ambiguous ({matches.Count} matches) — use a longer prefix.";
            var task = matches[0];
            task.Enabled = false;
            task.LastOutcome = $"cancelled at {TemporalFormat.NowStamp()}";
            await PersistAsync(task);
            return $"Cancelled scheduled task {ShortId(task.Id)} (\"{Truncate(task.Instruction, 80)}\", was due {TemporalFormat.StampWithAge(task.DueAtUtc)}).";
        }

        public string Describe()
        {
            var active = tasks.Values.Where(t => t.Enabled).OrderBy(t => t.DueAtUtc).ToList();
            var done = tasks.Values.Where(t => !t.Enabled).OrderByDescending(t => t.LastFiredAt ?? t.CreatedAt).Take(5).ToList();
            if (active.Count == 0 && done.Count == 0)
                return "No scheduled tasks. Create one with schedule_task to act at a future time.";
            var sb = new StringBuilder();
            if (active.Count > 0)
            {
                sb.AppendLine($"{active.Count} active scheduled task(s) (now: {TemporalFormat.NowStamp()}):");
                foreach (var t in active)
                    sb.AppendLine($"[{ShortId(t.Id)}] due {TemporalFormat.StampWithAge(t.DueAtUtc)}" +
                        (t.RepeatEvery.HasValue ? $" · repeats every {TemporalFormat.Span(t.RepeatEvery.Value)}" : "") +
                        (t.FiredCount > 0 ? $" · fired {t.FiredCount}× (last {TemporalFormat.StampWithAge(t.LastFiredAt!.Value)})" : "") +
                        $" — {Truncate(t.Instruction, 140)}");
            }
            if (done.Count > 0)
            {
                sb.AppendLine("Recent completed/cancelled:");
                foreach (var t in done)
                    sb.AppendLine($"[{ShortId(t.Id)}] {(t.FiredCount > 0 ? $"fired {TemporalFormat.StampWithAge(t.LastFiredAt!.Value)}" : "never fired")} — {Truncate(t.Instruction, 100)}" +
                        (string.IsNullOrWhiteSpace(t.LastOutcome) ? "" : $" → {Truncate(t.LastOutcome, 120)}"));
            }
            return sb.ToString().TrimEnd();
        }

        public List<AgentScheduledTask> ListActive() =>
            tasks.Values.Where(t => t.Enabled).OrderBy(t => t.DueAtUtc).ToList();

        private async Task PersistAsync(AgentScheduledTask task)
        {
            try
            {
                var path = Path.Combine(
                    OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentScheduledTasksDirectory),
                    $"{task.Id}.json");
                await service.GetDataHandler().SerialiseObjectToFile(path, task);
            }
            catch (Exception ex)
            {
                try { await service.ServiceLogError(ex, "[KliveAgent] Failed to persist scheduled task.", false); } catch { }
            }
        }

        private static string ShortId(string id) => string.IsNullOrEmpty(id) || id.Length <= 8 ? id : id[..8];
        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
    }
}
