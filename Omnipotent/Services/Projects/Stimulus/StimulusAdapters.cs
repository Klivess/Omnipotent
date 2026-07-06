using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Services.Projects.Stimulus
{
    /// <summary>
    /// Built-in stimulus source adapters (§5.2). Each adapter observes a source and calls
    /// <see cref="StimulusBus.IngestAsync"/> for the hook it belongs to. Only the ones that need
    /// no new infrastructure are wired for V1 — timer, webhook, file-watch, inter-agent and
    /// Klives messages. Screen-diff is driven by P2's VNC frame capture; Discord/email adapters
    /// arrive with P5 and the mail integration. Commander-authored C# script adapters are P7.
    ///
    /// The manager arms one live adapter per enabled hook and re-arms on hook CRUD.
    /// </summary>
    public class StimulusAdapterManager : IDisposable
    {
        private readonly StimulusBus bus;
        private readonly StimulusHookStore hooks;
        private readonly Action<string> log;

        // Live per-hook resources so re-arming can tear the old one down.
        private readonly ConcurrentDictionary<string, IDisposable> live = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource cts = new();

        // Screen-diff dependencies (set by the service when the desktop subsystem is up).
        public ContainerDesktopManager? Desktops { get; set; }
        public ProjectStore? Projects { get; set; }
        public ProjectArtifactStore? Artifacts { get; set; }

        public StimulusAdapterManager(StimulusBus bus, StimulusHookStore hooks, Action<string> log)
        {
            this.bus = bus;
            this.hooks = hooks;
            this.log = log ?? (_ => { });
        }

        /// <summary>Arms adapters for every currently-enabled hook (called on boot and after CRUD).</summary>
        public void ArmAll()
        {
            var wanted = hooks.AllHooks().Where(h => h.Enabled).ToDictionary(h => h.HookID, h => h);

            // Disarm hooks that no longer exist / were disabled.
            foreach (var id in live.Keys.ToList())
                if (!wanted.ContainsKey(id) && live.TryRemove(id, out var d))
                    d.Dispose();

            // Arm newly-seen hooks.
            foreach (var hook in wanted.Values)
                if (!live.ContainsKey(hook.HookID))
                    Arm(hook);
        }

        private void Arm(StimulusHookRecord hook)
        {
            try
            {
                IDisposable? resource = hook.SourceKind switch
                {
                    "timer" => ArmTimer(hook),
                    "file-watch" => ArmFileWatch(hook),
                    "script" => ArmScript(hook),
                    "screen-diff" => ArmScreenDiff(hook),
                    // webhook / inter-agent / klives are push sources: no standing resource, the
                    // service routes into IngestForHookAsync directly. They still "exist" so ArmAll
                    // doesn't try to re-arm them — represent with a no-op token.
                    "webhook" or "inter-agent" or "klives" or "discord" or "email" => new NoopToken(),
                    _ => new NoopToken(),
                };
                if (resource != null) live[hook.HookID] = resource;
            }
            catch (Exception ex) { log($"Failed to arm hook {hook.HookID} ({hook.SourceKind}): {ex.Message}"); }
        }

        // ── timer ──
        private IDisposable ArmTimer(StimulusHookRecord hook)
        {
            var spec = ParseSpec(hook.SourceSpecJson);
            int seconds = spec["intervalSeconds"]?.Value<int?>() ?? 3600;
            seconds = Math.Max(5, seconds);
            var timer = new System.Threading.Timer(async _ =>
            {
                try { await bus.IngestAsync(hook, $"Timer fired ({seconds}s interval)."); }
                catch (Exception ex) { log($"Timer hook {hook.HookID} ingest failed: {ex.Message}"); }
            }, null, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds));
            return timer;
        }

        // ── file-watch ──
        private IDisposable ArmFileWatch(StimulusHookRecord hook)
        {
            var spec = ParseSpec(hook.SourceSpecJson);
            string path = spec["path"]?.Value<string>() ?? "";
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return new NoopToken();
            var watcher = new FileSystemWatcher(path) { EnableRaisingEvents = true, IncludeSubdirectories = true };
            FileSystemEventHandler handler = async (_, e) =>
            {
                try { await bus.IngestAsync(hook, $"File {e.ChangeType}: {e.FullPath}"); }
                catch (Exception ex) { log($"File-watch hook {hook.HookID} ingest failed: {ex.Message}"); }
            };
            watcher.Created += handler; watcher.Changed += handler; watcher.Deleted += handler;
            return watcher;
        }

        // ── screen-diff (pixel-diff gate over a container desktop, §5.3) ──
        private IDisposable ArmScreenDiff(StimulusHookRecord hook)
        {
            if (Desktops == null || Projects == null || Artifacts == null || !OperatingSystem.IsWindows())
            {
                log($"Screen-diff hook {hook.HookID}: desktop subsystem unavailable — hook armed as no-op.");
                return new NoopToken();
            }
            return new ScreenDiffAdapter(hook, bus, Desktops, Projects, Artifacts, log);
        }

        // ── Commander-authored C# script adapter (§5.2) ──
        private IDisposable ArmScript(StimulusHookRecord hook)
        {
            var spec = ParseSpec(hook.SourceSpecJson);
            string script = spec["script"]?.Value<string>() ?? "";
            int poll = spec["pollSeconds"]?.Value<int?>() ?? 30;
            if (string.IsNullOrWhiteSpace(script)) return new NoopToken();
            return new StimulusScriptAdapter(hook, script, poll,
                emit: payload => bus.IngestAsync(hook, payload),
                log: log);
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

        private sealed class NoopToken : IDisposable { public void Dispose() { } }

        public void Dispose()
        {
            cts.Cancel();
            foreach (var d in live.Values) { try { d.Dispose(); } catch { } }
            live.Clear();
        }
    }
}
