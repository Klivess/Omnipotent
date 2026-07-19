using System.Collections.Concurrent;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Services.Projects.Stimulus
{
    /// <summary>Runtime arming outcome for a hook (not persisted — recomputed on each ArmAll).</summary>
    public enum HookArmState
    {
        /// <summary>Actively observing its source (a live timer/watcher, or a wired push source).</summary>
        Armed,
        /// <summary>Known source kind, but no standing resource is needed (pure push: webhook/inter-agent/klives).</summary>
        Passive,
        /// <summary>Could not arm — its source is unavailable or misconfigured. The hook will never fire.</summary>
        Error,
    }

    /// <summary>Per-hook arm status surfaced to Klives (UI) and the Commander (list_stimulus_hooks).</summary>
    public sealed record HookArmInfo(HookArmState State, string Detail);

    /// <summary>Lightweight inbound-mail event shape the email adapter matches against (decoupled from KliveMail models).</summary>
    public sealed class InboundMailStimulus
    {
        public string To { get; init; } = "";
        public string From { get; init; } = "";
        public string Subject { get; init; } = "";
        public string BodyPreview { get; init; } = "";
    }

    /// <summary>Lightweight inbound-Discord event shape the discord adapter matches against.</summary>
    public sealed class InboundDiscordStimulus
    {
        public string ChannelId { get; init; } = "";
        public string AuthorId { get; init; } = "";
        public string AuthorName { get; init; } = "";
        public string Content { get; init; } = "";
        public bool IsPrivate { get; init; }
    }

    /// <summary>
    /// Built-in stimulus source adapters (§5.2). Each adapter observes a source and calls
    /// <see cref="StimulusBus.IngestAsync"/> for the hook it belongs to.
    ///
    /// Standing-resource sources (timer, file-watch, screen-diff, script, process-exit) get one
    /// live adapter per hook. Push sources fall in two groups:
    ///   * webhook / inter-agent / klives — routed in directly via <see cref="IngestForHookAsync"/>;
    ///     they hold no standing resource (Passive).
    ///   * email / discord — fan out from a single shared subscription to KliveMail / the Discord
    ///     bot, wired by the service through <see cref="MailSource"/> / <see cref="DiscordSource"/>.
    ///
    /// The manager arms one adapter per enabled hook and re-arms on hook CRUD, tracking a per-hook
    /// <see cref="HookArmInfo"/> so an inert hook (source unavailable, bad path) is visible rather
    /// than silently doing nothing.
    /// </summary>
    public class StimulusAdapterManager : IDisposable
    {
        private readonly StimulusBus bus;
        private readonly StimulusHookStore hooks;
        private readonly Action<string> log;

        // Live per-hook resources so re-arming can tear the old one down.
        private readonly ConcurrentDictionary<string, IDisposable> live = new(StringComparer.Ordinal);
        // Per-hook arm outcome, surfaced to the UI / Commander.
        private readonly ConcurrentDictionary<string, HookArmInfo> armInfo = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource cts = new();

        // Shared push-source subscriptions (one per source, fanned out to all matching hooks).
        private IDisposable? mailSubscription;
        private IDisposable? discordSubscription;

        // Screen-diff dependencies (set by the service when the desktop subsystem is up).
        public ContainerDesktopManager? Desktops { get; set; }
        public ProjectStore? Projects { get; set; }
        public ProjectArtifactStore? Artifacts { get; set; }

        /// <summary>Subscribe-to-inbound-mail factory (set by the service when KliveMail is up). Returns an unsubscribe token.</summary>
        public Func<Func<InboundMailStimulus, Task>, IDisposable>? MailSource { get; set; }
        /// <summary>Subscribe-to-inbound-Discord factory (set by the service when the Discord bot is up). Returns an unsubscribe token.</summary>
        public Func<Func<InboundDiscordStimulus, Task>, IDisposable>? DiscordSource { get; set; }

        public StimulusAdapterManager(StimulusBus bus, StimulusHookStore hooks, Action<string> log)
        {
            this.bus = bus;
            this.hooks = hooks;
            this.log = log ?? (_ => { });
        }

        /// <summary>Runtime arm status for a hook, or null if it isn't currently armed (disabled/removed).</summary>
        public HookArmInfo? GetArmInfo(string hookID) => armInfo.TryGetValue(hookID, out var info) ? info : null;

        /// <summary>Arms adapters for every currently-enabled hook (called on boot and after CRUD).</summary>
        public void ArmAll()
        {
            var wanted = hooks.AllHooks().Where(h => h.Enabled).ToDictionary(h => h.HookID, h => h);

            // Disarm hooks that no longer exist / were disabled.
            foreach (var id in live.Keys.ToList())
                if (!wanted.ContainsKey(id) && live.TryRemove(id, out var d))
                {
                    try { d.Dispose(); } catch { }
                    armInfo.TryRemove(id, out _);
                }

            // Arm newly-seen hooks.
            foreach (var hook in wanted.Values)
                if (!live.ContainsKey(hook.HookID))
                    Arm(hook);

            // Attach/detach the shared push-source subscriptions to match demand.
            EnsureSharedSubscriptions(wanted.Values);
        }

        private void Arm(StimulusHookRecord hook)
        {
            try
            {
                IDisposable resource = hook.SourceKind switch
                {
                    "timer" => ArmTimer(hook),
                    "file-watch" => ArmFileWatch(hook),
                    "script" => ArmScript(hook),
                    "screen-diff" => ArmScreenDiff(hook),
                    "process-exit" => ArmProcessExit(hook),
                    // Fan-out push sources: no per-hook resource; the shared subscription dispatches
                    // to every matching hook. Arm-state reflects whether the source is wired.
                    "email" => SetArm(hook, MailSource != null ? HookArmState.Armed : HookArmState.Error,
                        MailSource != null ? "Observing inbound mail (KliveMail)." : "KliveMail unavailable — email hook will never fire."),
                    "discord" => SetArm(hook, DiscordSource != null ? HookArmState.Armed : HookArmState.Error,
                        DiscordSource != null ? "Observing Discord messages." : "Discord bot unavailable — discord hook will never fire."),
                    // Direct-route push sources: the service/routes call IngestForHookAsync.
                    "webhook" or "inter-agent" or "klives" => SetArm(hook, HookArmState.Passive, "Push source — delivered directly when the event arrives."),
                    _ => SetArm(hook, HookArmState.Error, $"Unknown source kind '{hook.SourceKind}' — nothing observes it."),
                };
                live[hook.HookID] = resource;
            }
            catch (Exception ex)
            {
                log($"Failed to arm hook {hook.HookID} ({hook.SourceKind}): {ex.Message}");
                armInfo[hook.HookID] = new HookArmInfo(HookArmState.Error, $"Arm failed: {ex.Message}");
                live[hook.HookID] = new NoopToken();
            }
        }

        /// <summary>Records arm status and returns a no-op resource (used by sources with no standing handle).</summary>
        private NoopToken SetArm(StimulusHookRecord hook, HookArmState state, string detail)
        {
            armInfo[hook.HookID] = new HookArmInfo(state, detail);
            if (state == HookArmState.Error) log($"Hook {hook.HookID} ({hook.SourceKind}) inert: {detail}");
            return new NoopToken();
        }

        // ── timer ──
        private IDisposable ArmTimer(StimulusHookRecord hook)
        {
            var spec = ParseSpec(hook.SourceSpecJson);
            int seconds = spec["intervalSeconds"]?.Value<int?>() ?? 3600;
            seconds = Math.Clamp(seconds, 5, 365 * 24 * 60 * 60);
            DateTime firstRunUtc = hook.CreatedAt.ToUniversalTime().AddSeconds(seconds);
            if (spec["firstRunUtc"] is JToken firstToken)
            {
                if (firstToken.Type != JTokenType.String
                    || !DateTimeOffset.TryParse(firstToken.Value<string>(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                    return SetArm(hook, HookArmState.Error,
                        "timer firstRunUtc must be an ISO-8601 timestamp, e.g. 2026-07-14T09:00:00Z.");
                firstRunUtc = parsed.UtcDateTime;
            }

            var timer = new RecurringTimerToken(hook, firstRunUtc, TimeSpan.FromSeconds(seconds),
                payload => bus.IngestAsync(hook, payload), log, cts.Token);
            armInfo[hook.HookID] = new HookArmInfo(HookArmState.Armed,
                $"Fires every {seconds}s, anchored at {firstRunUtc:O}; an overdue schedule emits one catch-up event after restart.");
            return timer;
        }

        // ── file-watch ──
        private IDisposable ArmFileWatch(StimulusHookRecord hook)
        {
            var spec = ParseSpec(hook.SourceSpecJson);
            string requestedPath = spec["path"]?.Value<string>() ?? "";
            if (string.IsNullOrWhiteSpace(requestedPath))
                return SetArm(hook, HookArmState.Error, "No 'path' in spec — nothing to watch.");
            string path;
            try { path = ResolveProjectWatchPath(hook.ProjectID, requestedPath); }
            catch (Exception ex) { return SetArm(hook, HookArmState.Error, ex.Message); }
            bool isFile = File.Exists(path);
            if (!isFile && !Directory.Exists(path))
                return SetArm(hook, HookArmState.Error, $"Watched project path does not exist: {requestedPath}");
            string watchDirectory = isFile ? Path.GetDirectoryName(path)! : path;
            var watcher = new FileSystemWatcher(watchDirectory)
            {
                Filter = isFile ? Path.GetFileName(path) : "*",
                EnableRaisingEvents = true,
                IncludeSubdirectories = !isFile,
            };
            FileSystemEventHandler handler = async (_, e) =>
            {
                string relative = Path.GetRelativePath(ProjectWorkspaceLocator.HostRoot(hook.ProjectID), e.FullPath)
                    .Replace('\\', '/');
                try { await bus.IngestAsync(hook, $"File {e.ChangeType}: /project/{relative}"); }
                catch (Exception ex) { log($"File-watch hook {hook.HookID} ingest failed: {ex.Message}"); }
            };
            watcher.Created += handler; watcher.Changed += handler; watcher.Deleted += handler;
            armInfo[hook.HookID] = new HookArmInfo(HookArmState.Armed,
                $"Watching {requestedPath}{(isFile ? "" : " recursively")} in the shared project workspace.");
            return watcher;
        }

        private static string ResolveProjectWatchPath(string projectID, string requestedPath)
        {
            string root = Path.GetFullPath(ProjectWorkspaceLocator.HostRoot(projectID));
            string relative = requestedPath.Replace('\\', '/').Trim();
            if (relative.Equals("/project", StringComparison.Ordinal)) relative = "";
            else if (relative.StartsWith("/project/", StringComparison.Ordinal)) relative = relative[9..];
            else if (Path.IsPathRooted(requestedPath))
                throw new InvalidOperationException("file-watch paths must be relative to /project, not an arbitrary host path.");
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            string rel = Path.GetRelativePath(root, full);
            if (Path.IsPathRooted(rel) || rel == ".."
                || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("file-watch path escapes the shared project workspace.");
            return full;
        }

        internal static DateTime NextTimerOccurrence(DateTime anchorUtc, TimeSpan period, DateTime nowUtc)
        {
            if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));
            anchorUtc = anchorUtc.ToUniversalTime();
            nowUtc = nowUtc.ToUniversalTime();
            if (nowUtc < anchorUtc) return anchorUtc;
            long periodsElapsed = (nowUtc.Ticks - anchorUtc.Ticks) / period.Ticks + 1;
            return anchorUtc.AddTicks(checked(period.Ticks * periodsElapsed));
        }

        /// <summary>Non-overlapping, wall-clock-anchored recurring timer with one catch-up delivery
        /// after downtime. A durable queue/ledger makes that at-least-once wake idempotent.</summary>
        private sealed class RecurringTimerToken : IDisposable
        {
            private readonly CancellationTokenSource cancellation;
            private readonly Task loop;

            public RecurringTimerToken(StimulusHookRecord hook, DateTime firstRunUtc, TimeSpan period,
                Func<string, Task> emit, Action<string> log, CancellationToken parentToken)
            {
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
                loop = Task.Run(() => RunAsync(hook, firstRunUtc, period, emit, log, cancellation.Token));
            }

            private static async Task RunAsync(StimulusHookRecord hook, DateTime anchorUtc, TimeSpan period,
                Func<string, Task> emit, Action<string> log, CancellationToken ct)
            {
                try
                {
                    async Task EmitSafely(string payload)
                    {
                        try { await emit(payload); }
                        catch (Exception ex) { log($"Timer hook {hook.HookID} ingest failed: {ex.Message}"); }
                    }

                    DateTime now = DateTime.UtcNow;
                    DateTime next = anchorUtc;
                    if (now >= anchorUtc)
                    {
                        next = NextTimerOccurrence(anchorUtc, period, now);
                        DateTime missedOccurrence = next - period;
                        await EmitSafely($"Timer catch-up fired for scheduled occurrence {missedOccurrence:O} after startup/downtime (schedule anchor {anchorUtc:O}, interval {period.TotalSeconds:0}s).");
                    }

                    while (!ct.IsCancellationRequested)
                    {
                        TimeSpan delay = next - DateTime.UtcNow;
                        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                        DateTime scheduled = next;
                        await EmitSafely($"Timer fired for scheduled occurrence {scheduled:O} ({period.TotalSeconds:0}s interval).");
                        next = scheduled.Add(period);
                        while (next <= DateTime.UtcNow) next = next.Add(period);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex) { log($"Timer hook {hook.HookID} ingest failed: {ex.Message}"); }
            }

            public void Dispose()
            {
                cancellation.Cancel();
                cancellation.Dispose();
                _ = loop.Exception;
            }
        }

        // ── screen-diff (pixel-diff gate over a container desktop, §5.3) ──
        private IDisposable ArmScreenDiff(StimulusHookRecord hook)
        {
            if (Desktops == null || Projects == null || Artifacts == null || !OperatingSystem.IsWindows())
                return SetArm(hook, HookArmState.Error, "Desktop subsystem unavailable — screen-diff cannot observe.");
            armInfo[hook.HookID] = new HookArmInfo(HookArmState.Armed, "Watching a container desktop for changes.");
            return new ScreenDiffAdapter(hook, bus, Desktops, Projects, Artifacts, log);
        }

        // ── Commander-authored C# script adapter (§5.2) ──
        private IDisposable ArmScript(StimulusHookRecord hook)
        {
            var spec = ParseSpec(hook.SourceSpecJson);
            string script = spec["script"]?.Value<string>() ?? "";
            int poll = spec["pollSeconds"]?.Value<int?>() ?? 30;
            if (string.IsNullOrWhiteSpace(script))
                return SetArm(hook, HookArmState.Error, "No 'script' in spec — nothing to poll.");
            armInfo[hook.HookID] = new HookArmInfo(HookArmState.Armed, $"Polling a C# script every {poll}s.");
            return new StimulusScriptAdapter(hook, script, poll,
                emit: payload => bus.IngestAsync(hook, payload),
                log: log);
        }

        // ── process-exit (poll a named process / pid; emit on the running→exited edge) ──
        private IDisposable ArmProcessExit(StimulusHookRecord hook)
        {
            var spec = ParseSpec(hook.SourceSpecJson);
            string processName = spec["processName"]?.Value<string>() ?? "";
            int? pid = spec["pid"]?.Value<int?>();
            int poll = Math.Max(2, spec["pollSeconds"]?.Value<int?>() ?? 10);
            if (string.IsNullOrWhiteSpace(processName) && pid == null)
                return SetArm(hook, HookArmState.Error, "Provide 'processName' or 'pid' to watch.");

            string label = pid != null ? $"pid {pid}" : $"process '{processName}'";
            bool wasRunning = IsProcessRunning(processName, pid);
            var timer = new System.Threading.Timer(async _ =>
            {
                try
                {
                    bool running = IsProcessRunning(processName, pid);
                    if (wasRunning && !running)
                        await bus.IngestAsync(hook, $"Process exited: {label}.");
                    wasRunning = running;
                }
                catch (Exception ex) { log($"Process-exit hook {hook.HookID} poll failed: {ex.Message}"); }
            }, null, TimeSpan.FromSeconds(poll), TimeSpan.FromSeconds(poll));
            armInfo[hook.HookID] = new HookArmInfo(HookArmState.Armed, $"Watching {label} for exit (every {poll}s).");
            return timer;
        }

        private static bool IsProcessRunning(string processName, int? pid)
        {
            try
            {
                if (pid != null)
                {
                    try { Process.GetProcessById(pid.Value); return true; }
                    catch (ArgumentException) { return false; }
                }
                return Process.GetProcessesByName(processName).Length > 0;
            }
            catch { return false; }
        }

        // ── shared push-source subscriptions (email / discord) ──
        private void EnsureSharedSubscriptions(IEnumerable<StimulusHookRecord> enabledHooks)
        {
            bool wantMail = MailSource != null && enabledHooks.Any(h => h.SourceKind == "email");
            bool wantDiscord = DiscordSource != null && enabledHooks.Any(h => h.SourceKind == "discord");

            if (wantMail && mailSubscription == null)
                mailSubscription = MailSource!(OnMailAsync);
            else if (!wantMail && mailSubscription != null) { try { mailSubscription.Dispose(); } catch { } mailSubscription = null; }

            if (wantDiscord && discordSubscription == null)
                discordSubscription = DiscordSource!(OnDiscordAsync);
            else if (!wantDiscord && discordSubscription != null) { try { discordSubscription.Dispose(); } catch { } discordSubscription = null; }
        }

        private async Task OnMailAsync(InboundMailStimulus mail)
        {
            foreach (var hook in hooks.AllHooks().Where(h => h.Enabled && h.SourceKind == "email"))
            {
                try
                {
                    if (!MailMatches(hook, mail)) continue;
                    // The durable stimulus exists only to wake the owner. Verification codes and
                    // reset links stay in KliveMail and are fetched through the live native tools;
                    // putting a body/subject into the event journal would leak short-lived secrets
                    // into CSV exports, digests and RAG.
                    string payload = $"Email received.\nTo: {mail.To}\nFrom: {mail.From}\nSubject/body omitted from durable history; inspect the canonical mailbox with klivemail_list_messages or klivemail_wait_for_code.";
                    await bus.IngestAsync(hook, payload);
                }
                catch (Exception ex) { log($"Email hook {hook.HookID} ingest failed: {ex.Message}"); }
            }
        }

        private async Task OnDiscordAsync(InboundDiscordStimulus msg)
        {
            foreach (var hook in hooks.AllHooks().Where(h => h.Enabled && h.SourceKind == "discord"))
            {
                try
                {
                    if (!DiscordRouteMatches(hook, msg)) continue;
                    string where = msg.IsPrivate ? "DM" : $"channel {msg.ChannelId}";
                    string payload = $"Discord message in {where} from {msg.AuthorName} ({msg.AuthorId}):\n{Truncate(msg.Content, 800)}";
                    await bus.IngestAsync(hook, payload);
                }
                catch (Exception ex) { log($"Discord hook {hook.HookID} ingest failed: {ex.Message}"); }
            }
        }

        // Optional spec filters — empty spec matches everything (the criterion still triages).
        private bool MailMatches(StimulusHookRecord hook, InboundMailStimulus mail)
        {
            var spec = ParseSpec(hook.SourceSpecJson);
            string? to = spec["to"]?.Value<string>();
            string? from = spec["from"]?.Value<string>();
            string? subj = spec["subjectContains"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(to) && mail.To.IndexOf(to, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (!string.IsNullOrWhiteSpace(from) && mail.From.IndexOf(from, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (!string.IsNullOrWhiteSpace(subj) && mail.Subject.IndexOf(subj, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        // Channel and author are routing scope. Content recognition belongs in StimulusAgent so
        // rejection is observable and the hook's natural-language criterion is actually reached.
        private bool DiscordRouteMatches(StimulusHookRecord hook, InboundDiscordStimulus msg)
        {
            var spec = ParseSpec(hook.SourceSpecJson);
            string? channelId = spec["channelId"]?.Value<string>();
            string? authorId = spec["authorId"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(channelId) && msg.ChannelId != channelId) return false;
            if (!string.IsNullOrWhiteSpace(authorId) && msg.AuthorId != authorId) return false;
            return true;
        }

        /// <summary>
        /// Push-source entry point: a webhook body, an inter-agent message, or a Klives message
        /// routed to a specific hook. The service's routes/hooks call this.
        /// </summary>
        public Task IngestForHookAsync(string projectID, string hookID, string payload, List<string>? artifactIDs = null)
        {
            var hook = hooks.Get(projectID, hookID);
            if (hook == null) return Task.CompletedTask;
            return bus.IngestAsync(hook, payload, artifactIDs);
        }

        private static JObject ParseSpec(string json)
        {
            try { return JObject.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
            catch { return new JObject(); }
        }

        private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

        private sealed class NoopToken : IDisposable { public void Dispose() { } }

        public void Dispose()
        {
            cts.Cancel();
            try { mailSubscription?.Dispose(); } catch { }
            try { discordSubscription?.Dispose(); } catch { }
            foreach (var d in live.Values) { try { d.Dispose(); } catch { } }
            live.Clear();
            armInfo.Clear();
        }
    }
}
