using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveGames.Games;
using Omnipotent.Services.KliveGames.Models;
using Omnipotent.Services.KliveGames.Runtime;

namespace Omnipotent.Services.KliveGames
{
    /// <summary>
    /// KliveGames — deploy and fully manage game servers (Minecraft first). Owns the registry of server
    /// instances, their long-lived processes, per-instance console hubs, lifecycle orchestration (with a
    /// per-instance lock), a resource/status monitor loop, crash auto-restart, backups, and UPnP exposure.
    /// </summary>
    public partial class KliveGames : OmniService
    {
        public sealed class CreateServerRequest
        {
            public string Name { get; set; } = "";
            public GameType GameType { get; set; } = GameType.Minecraft;
            public ServerFlavor Flavor { get; set; } = ServerFlavor.Paper;
            public string Version { get; set; } = "";
            public int Port { get; set; } = 0;       // 0 => auto-allocate
            public int RamMb { get; set; } = 2048;
            public bool UseAikarFlags { get; set; } = true;
            public bool Public { get; set; } = false;
            public bool AutoStart { get; set; } = false;
            public bool StartAfterCreate { get; set; } = true;
            public bool EulaAccepted { get; set; } = false;
            /// <summary>Game-specific deploy options (e.g. Terraria world size/difficulty/maxPlayers/password).</summary>
            public Dictionary<string, string> Options { get; set; } = new();
        }

        private readonly ConcurrentDictionary<string, GameServerInstance> _instances = new();
        private readonly ConcurrentDictionary<string, ManagedGameProcess> _processes = new();
        private readonly ConcurrentDictionary<string, GameConsoleHub> _consoleHubs = new();
        private readonly ConcurrentDictionary<string, IRemoteConsole> _remoteConsoles = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
        private readonly ConcurrentDictionary<string, List<DateTime>> _restartHistory = new();
        private readonly ConcurrentDictionary<string, DateTime> _pendingListPoll = new();
        private readonly ConcurrentDictionary<string, string> _unreadableRosterReply = new();

        private GameProviderRegistry _providers = null!;
        private readonly BackupManager _backups = new();

        public KliveGames()
        {
            name = "KliveGames";
            threadAnteriority = ThreadAnteriority.Standard; // never Critical: must not auto-restart ServiceMain and orphan game processes
        }

        protected override async void ServiceMain()
        {
            var enabled = await GetBoolOmniSetting("KliveGames_Enabled", defaultValue: true);
            if (!enabled)
            {
                await ServiceLog("[KliveGames] Disabled via OmniSettings. Exiting.");
                return;
            }

            await ServiceLog("[KliveGames] Initializing...");
            try
            {
                _providers = new GameProviderRegistry(async msg => await ServiceLogError(msg));
                EnsureDirectories();

                // Stop all running servers cleanly if the service is asked to quit.
                ServiceQuitRequest += () => { try { StopAllAsync().GetAwaiter().GetResult(); } catch { } };

                var routes = new KliveGamesRoutes(this);
                await routes.RegisterRoutes();

                await LoadAndReconcileInstancesAsync();

                _ = MonitorLoopAsync(cancellationToken.Token);

                // Auto-start flagged servers — skipping any that were just adopted, which StartAsync
                // already treats as running. A failure here is invisible unless it is logged.
                foreach (var inst in _instances.Values.Where(i => i.AutoStart))
                {
                    string id = inst.Id;
                    _ = Task.Run(async () =>
                    {
                        try { await StartAsync(id); }
                        catch (Exception ex) { await ServiceLogError(ex, $"[KliveGames] Auto-start of '{inst.Name}' failed."); }
                    });
                }

                await ServiceLog($"[KliveGames] Ready. {_instances.Count} instance(s) loaded.");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "[KliveGames] Initialization failed.");
            }
        }

        // ----------------------------------------------------------------- paths & persistence

        private void EnsureDirectories()
        {
            foreach (var dir in new[]
            {
                OmniPaths.GlobalPaths.KliveGamesDirectory,
                OmniPaths.GlobalPaths.KliveGamesInstancesDirectory,
                OmniPaths.GlobalPaths.KliveGamesJarCacheDirectory,
                OmniPaths.GlobalPaths.KliveGamesRuntimesDirectory,
                OmniPaths.GlobalPaths.KliveGamesBackupsDirectory,
            })
            {
                Directory.CreateDirectory(OmniPaths.GetPath(dir));
            }
        }

        private static string InstanceDir(string id) =>
            OmniPaths.GetPath(Path.Combine(OmniPaths.GlobalPaths.KliveGamesInstancesDirectory, id));

        private static string InstanceMetaPath(string id) => Path.Combine(InstanceDir(id), "instance.json");

        public async Task SaveInstanceAsync(GameServerInstance inst)
        {
            try { await GetDataHandler().SerialiseObjectToFile(InstanceMetaPath(inst.Id), inst); }
            catch (Exception ex) { await ServiceLogError(ex, $"[KliveGames] Failed to persist instance {inst.Id}."); }
        }

        private async Task LoadAndReconcileInstancesAsync()
        {
            string root = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveGamesInstancesDirectory);
            if (!Directory.Exists(root)) return;

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                string meta = Path.Combine(dir, "instance.json");
                if (!File.Exists(meta)) continue;
                try
                {
                    var inst = JsonConvert.DeserializeObject<GameServerInstance>(File.ReadAllText(meta));
                    if (inst == null || string.IsNullOrEmpty(inst.Id)) continue;

                    _instances[inst.Id] = inst;
                    _consoleHubs.GetOrAdd(inst.Id, _ => new GameConsoleHub());

                    // A game server outlives the app that launched it. If its process is still up, take it
                    // back rather than starting a second copy on top of it (which would leave the first
                    // one running unmanaged, holding the port, with players still on it).
                    if (!await TryAdoptRunningProcessAsync(inst))
                    {
                        inst.Status = GameServerStatus.Stopped;
                        inst.ChildPid = null;
                        inst.ChildStartedUtc = null;
                        inst.Adopted = false;
                        inst.OnlinePlayers = new();
                        inst.CpuPercent = 0;
                        inst.RamUsedBytes = 0;
                        inst.RunningSinceUtc = null;
                    }

                    await SaveInstanceAsync(inst);
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, $"[KliveGames] Failed to load instance from {dir}.");
                }
            }
        }

        /// <summary>
        /// Re-attaches to this instance's process if it is still running after an Omnipotent restart.
        /// Everything is restored except stdio, which belonged to the dead host: resource sampling, exit
        /// handling, auto-restart, and — for games with a remote console (Rust/RCON) — the live console,
        /// the player roster and the graceful stop. Returns false when there is nothing to adopt.
        /// </summary>
        private async Task<bool> TryAdoptRunningProcessAsync(GameServerInstance inst)
        {
            if (inst.ChildPid is not int pid || inst.Status == GameServerStatus.Provisioning) return false;
            if (_processes.TryGetValue(inst.Id, out var owned) && owned.IsRunning) return true; // already managed

            Process process;
            try
            {
                process = Process.GetProcessById(pid);
                if (process.HasExited || !IsRecordedProcess(process, inst)) { process.Dispose(); return false; }
            }
            catch { return false; } // the process died with the app, or the PID now belongs to a stranger

            try
            {
                var provider = _providers.Get(inst.GameType);
                var hub = _consoleHubs.GetOrAdd(inst.Id, _ => new GameConsoleHub());
                var proc = ManagedGameProcess.Adopt(process, ringCapacity: 500,
                    logError: async m => await ServiceLogError($"[KliveGames:{inst.Name}] {m}"));
                proc.OnConsoleLine += line => HandleConsoleLine(inst, hub, provider, line);
                proc.OnExited += code => { _ = OnProcessExitedAsync(inst.Id, code); };
                _processes[inst.Id] = proc;

                // It may have exited in the moment between the liveness check and subscribing, in which
                // case nothing was listening for the Exited event — treat it as never adopted.
                if (!proc.IsRunning)
                {
                    _processes.TryRemove(inst.Id, out _);
                    proc.Dispose();
                    return false;
                }

                inst.Status = GameServerStatus.Running;
                inst.Adopted = true;
                inst.ChildStartedUtc = proc.StartTimeUtc;
                inst.RunningSinceUtc = proc.StartTimeUtc;
                inst.OnlinePlayers = new();
                inst.LastError = null;

                AttachRemoteConsole(inst, hub, provider);

                proc.AppendConsoleLine($"[KliveGames] Re-attached to this server (pid {pid}) after an Omnipotent restart — it never stopped running. Console history from before the restart is not available.");
                proc.AppendConsoleLine(_remoteConsoles.ContainsKey(inst.Id)
                    ? "[KliveGames] Reconnecting its RCON console; commands and the player list come back in a moment."
                    : "[KliveGames] Console output and commands stay unavailable for this server until it is restarted from KliveGames.");

                if (inst.Public) _ = EnsurePublicAsync(inst, true);
                await ServiceLog($"[KliveGames] Adopted the still-running '{inst.Name}' (pid {pid}) instead of starting a second copy.");
                return true;
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, $"[KliveGames] Failed to adopt the running process for '{inst.Name}'.");
                _processes.TryRemove(inst.Id, out _);
                DetachRemoteConsole(inst.Id);
                return false;
            }
        }

        /// <summary>Is this really the process this instance started? PIDs are recycled, so adopting on a
        /// PID alone could hand a stranger's process the stop/kill button.</summary>
        internal static bool IsRecordedProcess(Process process, GameServerInstance inst)
        {
            DateTime startedUtc;
            try { startedUtc = process.StartTime.ToUniversalTime(); }
            catch { return false; }

            if (inst.ChildStartedUtc is DateTime recorded)
                return Math.Abs((startedUtc - recorded).TotalSeconds) <= 2;

            // Instances written before the creation time was recorded: fall back to matching the runtime
            // by name, and require the process to be no older than the launch this instance remembers.
            if (inst.LastStartedUtc is DateTime lastStarted && startedUtc < lastStarted.AddMinutes(-5)) return false;

            string expected = Path.GetFileNameWithoutExtension(inst.LaunchTarget ?? "");
            bool matches = !string.IsNullOrWhiteSpace(expected)
                && process.ProcessName.Equals(expected, StringComparison.OrdinalIgnoreCase);
            matches |= inst.GameType == GameType.Minecraft
                && process.ProcessName.Contains("java", StringComparison.OrdinalIgnoreCase);
            matches |= inst.GameType == GameType.Terraria && inst.Flavor == ServerFlavor.TModLoader
                && process.ProcessName.Equals("cmd", StringComparison.OrdinalIgnoreCase);
            return matches;
        }

        // ----------------------------------------------------------------- accessors for routes

        public GameProviderRegistry Providers => _providers;
        public BackupManager Backups => _backups;

        public IReadOnlyList<GameServerInstance> ListInstances() => _instances.Values.OrderBy(i => i.Name).ToList();
        public GameServerInstance? GetInstance(string id) => _instances.TryGetValue(id, out var i) ? i : null;
        public GameConsoleHub? GetConsoleHub(string id) => _consoleHubs.TryGetValue(id, out var h) ? h : null;

        public IReadOnlyList<string> GetRecentConsole(string id, int max = 500)
            => _processes.TryGetValue(id, out var p) ? p.SnapshotRecentLines(max) : Array.Empty<string>();

        public InstanceFileManager GetFileManager(string id)
        {
            var inst = GetInstance(id) ?? throw new InvalidOperationException("Server not found.");
            return new InstanceFileManager(inst.ServerDirectory);
        }

        private SemaphoreSlim LockFor(string id) => _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

        // ----------------------------------------------------------------- create / provision

        public async Task<GameServerInstance> CreateServerAsync(CreateServerRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Name)) throw new ArgumentException("A server name is required.");
            if (string.IsNullOrWhiteSpace(req.Version)) throw new ArgumentException("A version is required.");

            var provider = _providers.Get(req.GameType);
            if (!provider.Implemented) throw new InvalidOperationException($"{provider.DisplayName} is not available yet.");
            if (provider.RequiresEula && !req.EulaAccepted)
                throw new InvalidOperationException($"The {provider.DisplayName} EULA must be accepted to deploy a server.");

            int port = AllocatePort(req.Port > 0 ? req.Port : provider.DefaultPort, provider);

            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            string serverDir = Path.Combine(InstanceDir(id), "server");
            Directory.CreateDirectory(serverDir);

            var inst = new GameServerInstance
            {
                Id = id,
                Name = req.Name.Trim(),
                GameType = req.GameType,
                Flavor = req.Flavor,
                Version = req.Version,
                Port = port,
                RamMb = provider.UsesMemoryLimit ? Math.Clamp(req.RamMb, 512, 32768) : req.RamMb,
                UseAikarFlags = req.UseAikarFlags,
                Public = req.Public,
                AutoStart = req.AutoStart,
                Status = GameServerStatus.Provisioning,
                ServerDirectory = serverDir,
                DeployOptions = req.Options ?? new(),
                CreatedUtc = DateTime.UtcNow,
            };

            _instances[id] = inst;
            _consoleHubs[id] = new GameConsoleHub();
            await SaveInstanceAsync(inst);

            _ = ProvisionAsync(inst, provider, req.StartAfterCreate);
            return inst;
        }

        private async Task ProvisionAsync(GameServerInstance inst, IGameProvider provider, bool startAfter)
        {
            var hub = _consoleHubs[inst.Id];
            var progress = new Progress<string>(msg =>
            {
                _ = hub.BroadcastLineAsync($"[provision] {msg}");
            });

            try
            {
                inst.Status = GameServerStatus.Provisioning;
                await hub.BroadcastEventAsync("status", new { status = inst.Status.ToString() });
                await provider.PrepareServerAsync(inst, progress, cancellationToken.Token);

                inst.Status = GameServerStatus.Stopped;
                inst.LastError = null;
                await SaveInstanceAsync(inst);
                await hub.BroadcastLineAsync("[provision] Done.");
                await hub.BroadcastEventAsync("status", new { status = inst.Status.ToString() });
                await ServiceLog($"[KliveGames] Provisioned '{inst.Name}' ({provider.DisplayName} {inst.Version} {inst.Flavor}).");

                if (startAfter) await StartAsync(inst.Id);
            }
            catch (Exception ex)
            {
                inst.Status = GameServerStatus.Crashed;
                inst.LastError = ex.Message;
                await SaveInstanceAsync(inst);
                await hub.BroadcastLineAsync($"[provision] FAILED: {ex.Message}");
                await hub.BroadcastEventAsync("status", new { status = inst.Status.ToString(), error = ex.Message });
                await ServiceLogError(ex, $"[KliveGames] Provisioning failed for '{inst.Name}'.");
            }
        }

        // ----------------------------------------------------------------- lifecycle

        public async Task StartAsync(string id)
        {
            var inst = GetInstance(id) ?? throw new InvalidOperationException("Server not found.");
            var provider = _providers.Get(inst.GameType);
            var gate = LockFor(id);
            await gate.WaitAsync();
            try
            {
                if (_processes.TryGetValue(id, out var existing) && existing.IsRunning) return;
                if (inst.Status == GameServerStatus.Provisioning)
                    throw new InvalidOperationException("Server is still provisioning.");

                var hub = _consoleHubs.GetOrAdd(id, _ => new GameConsoleHub());
                await EnsurePortsAvailableAsync(inst, provider, hub);
                var spec = await provider.BuildLaunchSpecAsync(inst, cancellationToken.Token);

                var proc = new ManagedGameProcess(spec, ringCapacity: 500, logError: async m => await ServiceLogError($"[KliveGames:{inst.Name}] {m}"));
                proc.OnConsoleLine += line => HandleConsoleLine(inst, hub, provider, line);
                proc.OnExited += code => { _ = OnProcessExitedAsync(inst.Id, code); };

                inst.OnlinePlayers = new();
                inst.Status = GameServerStatus.Starting;
                inst.LastError = null;

                await proc.StartAsync(cancellationToken.Token);
                _processes[id] = proc;
                AttachRemoteConsole(inst, hub, provider);
                inst.ChildPid = proc.Pid;
                inst.ChildStartedUtc = proc.StartTimeUtc; // with the PID, this is what lets a later Omnipotent re-adopt it
                inst.Adopted = false;
                inst.LastStartedUtc = DateTime.UtcNow;
                await SaveInstanceAsync(inst);
                await hub.BroadcastEventAsync("status", new { status = inst.Status.ToString() });
                await ServiceLog($"[KliveGames] Starting '{inst.Name}' (pid {proc.Pid}).");

                if (inst.Public) _ = EnsurePublicAsync(inst, true);
            }
            finally { gate.Release(); }
        }

        public async Task StopAsync(string id)
        {
            var inst = GetInstance(id) ?? throw new InvalidOperationException("Server not found.");
            var provider = _providers.Get(inst.GameType);
            var gate = LockFor(id);
            await gate.WaitAsync();
            try
            {
                if (!_processes.TryGetValue(id, out var proc) || !proc.IsRunning)
                {
                    inst.Status = GameServerStatus.Stopped;
                    return;
                }

                inst.Status = GameServerStatus.Stopping;
                await BroadcastStatus(inst);

                // Games with a remote console cannot be stopped through stdin — deliver the stop command
                // there first, then let the process wrapper do nothing but wait.
                string stopCommand = provider.GetGracefulStopCommand();
                bool canRequestStop = true;
                if (_remoteConsoles.ContainsKey(id))
                {
                    try { await SendCommandAsync(id, stopCommand, echo: false); } catch { }
                    stopCommand = "";
                }
                else if (!proc.ConsoleAttached)
                {
                    // Adopted after an Omnipotent restart with no remote console: there is no way left to
                    // ask this server to shut down, so waiting out the grace window would only delay a
                    // termination. Say so rather than letting it look like a clean stop.
                    stopCommand = "";
                    canRequestStop = false;
                    proc.AppendConsoleLine("[KliveGames] This server was re-attached after an Omnipotent restart, so its shutdown command cannot be delivered — terminating the process instead.");
                    await ServiceLogError($"[KliveGames] Stopping adopted '{inst.Name}' by termination: no console to send '{provider.GetGracefulStopCommand()}' to.");
                }

                int grace = await GetIntOmniSetting("KliveGames_StopGraceSeconds", 90);
                await proc.StopGracefullyAsync(stopCommand, TimeSpan.FromSeconds(canRequestStop ? grace : 5), killOnExpiry: true);
                DetachRemoteConsole(id);

                inst.Status = GameServerStatus.Stopped;
                inst.ChildPid = null;
                inst.ChildStartedUtc = null;
                inst.Adopted = false;
                inst.OnlinePlayers = new();
                inst.CpuPercent = 0;
                inst.RamUsedBytes = 0;
                inst.RunningSinceUtc = null;
                await SaveInstanceAsync(inst);
                await BroadcastStatus(inst);
                await ServiceLog($"[KliveGames] Stopped '{inst.Name}'.");
            }
            finally { gate.Release(); }
        }

        public async Task RestartAsync(string id)
        {
            await StopAsync(id);
            await StartAsync(id);
        }

        public async Task KillAsync(string id)
        {
            var inst = GetInstance(id) ?? throw new InvalidOperationException("Server not found.");
            var gate = LockFor(id);
            await gate.WaitAsync();
            try
            {
                if (_processes.TryGetValue(id, out var proc)) proc.Kill();
                DetachRemoteConsole(id);
                inst.Status = GameServerStatus.Stopped;
                inst.ChildPid = null;
                inst.ChildStartedUtc = null;
                inst.Adopted = false;
                inst.OnlinePlayers = new();
                await SaveInstanceAsync(inst);
                await BroadcastStatus(inst);
                await ServiceLog($"[KliveGames] Killed '{inst.Name}'.");
            }
            finally { gate.Release(); }
        }

        public async Task DeleteAsync(string id)
        {
            var inst = GetInstance(id) ?? throw new InvalidOperationException("Server not found.");
            try { await KillAsync(id); } catch { }

            if (inst.Public) await RemovePublicPortsAsync(inst);

            _instances.TryRemove(id, out _);
            _processes.TryRemove(id, out var p); p?.Dispose();
            DetachRemoteConsole(id);
            _consoleHubs.TryRemove(id, out _);
            _locks.TryRemove(id, out _);
            _restartHistory.TryRemove(id, out _);
            _unreadableRosterReply.TryRemove(id, out _);

            try { if (Directory.Exists(InstanceDir(id))) Directory.Delete(InstanceDir(id), true); } catch { }
            try
            {
                string backupDir = OmniPaths.GetPath(Path.Combine(OmniPaths.GlobalPaths.KliveGamesBackupsDirectory, id));
                if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true);
            }
            catch { }

            await ServiceLog($"[KliveGames] Deleted '{inst.Name}'.");
        }

        public async Task SendCommandAsync(string id, string command, bool echo = true)
        {
            if (string.IsNullOrWhiteSpace(command)) return;
            _consoleHubs.TryGetValue(id, out var hub);

            bool sent;
            if (_remoteConsoles.TryGetValue(id, out var console))
            {
                // Silent commands are internal polls: their replies are parsed, never echoed.
                sent = await console.SendAsync(command, silent: !echo);
            }
            else
            {
                sent = _processes.TryGetValue(id, out var proc) && proc.IsRunning && proc.ConsoleAttached;
                if (sent) await proc!.SendCommandAsync(command);
            }

            if (!echo || hub == null) return;
            if (sent) { await hub.BroadcastLineAsync($"> {command}"); return; }

            bool adopted = _processes.TryGetValue(id, out var running) && running.IsRunning && !running.ConsoleAttached;
            string reason = adopted
                ? "this server was re-attached after an Omnipotent restart and its console input is gone — restart the server to get it back"
                : "the server console is not connected yet";
            await hub.BroadcastLineAsync($"> {command}    [not sent — {reason}]");
        }

        public async Task<string?> SendPlayerActionAsync(string id, string action, string player)
        {
            var inst = GetInstance(id) ?? throw new InvalidOperationException("Server not found.");
            var provider = _providers.Get(inst.GameType);
            var command = provider.BuildPlayerActionCommand(action, player);
            if (command == null) throw new ArgumentException("Unsupported player action.");
            await SendCommandAsync(id, command);
            return command;
        }

        private async Task StopAllAsync()
        {
            var running = _instances.Values.Where(i => _processes.TryGetValue(i.Id, out var p) && p.IsRunning).ToList();
            foreach (var inst in running)
            {
                try { await StopAsync(inst.Id); } catch { }
            }
        }

        // ----------------------------------------------------------------- console + exit handling

        /// <summary>Opens the provider's out-of-band console, if it has one (Rust: RCON). Everything the
        /// orchestrator sends then travels down it instead of stdin, which that server ignores.</summary>
        private void AttachRemoteConsole(GameServerInstance inst, GameConsoleHub hub, IGameProvider provider)
        {
            DetachRemoteConsole(inst.Id);

            IRemoteConsole? console;
            try { console = provider.CreateRemoteConsole(inst); }
            catch (Exception ex)
            {
                _ = ServiceLogError(ex, $"[KliveGames] Could not open the remote console for '{inst.Name}'.");
                return;
            }
            if (console == null) return;

            console.OnMessage += message => HandleRemoteConsoleMessage(inst, hub, provider, message);
            console.OnNotice += notice => _ = hub.BroadcastLineAsync(notice);
            _remoteConsoles[inst.Id] = console;
            _ = console.StartAsync(cancellationToken.Token);
        }

        private void DetachRemoteConsole(string id)
        {
            if (_remoteConsoles.TryRemove(id, out var console))
            {
                try { console.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// A reply from the remote console. Unlike stdout this arrives a whole message at a time, which is
        /// what makes the roster readable — Rust answers <c>playerlist</c> with a multi-line JSON document.
        /// </summary>
        private void HandleRemoteConsoleMessage(GameServerInstance inst, GameConsoleHub hub, IGameProvider provider, RemoteConsoleMessage message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(message.Text)) return;

                // Server-pushed log lines normally duplicate stdout and are dropped. An adopted server has
                // no stdout to duplicate, so there they are the only console output there is.
                bool stdoutAttached = !_processes.TryGetValue(inst.Id, out var proc) || proc.ConsoleAttached;
                bool broadcast = message.Kind == RemoteConsoleMessageKind.Reply
                    || (message.Kind == RemoteConsoleMessageKind.Broadcast && !stdoutAttached);

                if (provider.TryParseListReply(message.Text, out _, out int max, out var names))
                {
                    inst.OnlinePlayers = names.ToList();
                    if (max > 0) inst.MaxPlayers = max;
                    _unreadableRosterReply.TryRemove(inst.Id, out _);
                    if (!broadcast) return;
                }
                else if (message.Kind == RemoteConsoleMessageKind.InternalReply)
                {
                    // The roster poll answered with something we cannot read (an unsupported command, an
                    // error). Show it once per distinct reply: swallowing it would leave an empty players
                    // panel with nothing anywhere to explain it.
                    if (!_unreadableRosterReply.TryGetValue(inst.Id, out var previous) || previous != message.Text)
                    {
                        _unreadableRosterReply[inst.Id] = message.Text;
                        broadcast = true;
                    }
                }

                foreach (string line in message.Text.Split('\n'))
                {
                    string text = line.TrimEnd('\r');
                    // For an adopted server this is the console log, so it belongs in the replay buffer
                    // too — AppendConsoleLine stores it and raises the same handling as stdout would.
                    if (broadcast && !stdoutAttached && proc != null) proc.AppendConsoleLine(text);
                    else HandleConsoleLine(inst, hub, provider, text, broadcast);
                }
            }
            catch { }
        }

        private void HandleConsoleLine(GameServerInstance inst, GameConsoleHub hub, IGameProvider provider, string line, bool broadcast = true)
        {
            try
            {
                bool suppress = !broadcast;

                if (provider.TryParseListReply(line, out _, out int max, out var names))
                {
                    inst.OnlinePlayers = names.ToList();
                    if (max > 0) inst.MaxPlayers = max;
                    // Hide the reply to an internal (monitor) roster poll so the live console isn't spammed.
                    if (_pendingListPoll.TryRemove(inst.Id, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(3))
                        suppress = true;
                }
                else if (provider.TryParsePlayerJoin(line, out var joined))
                {
                    // Copy-on-write: console output and the remote console arrive on different threads,
                    // and the routes serialize this list concurrently. A lost update is corrected by the
                    // next authoritative roster poll; a torn list is not.
                    var roster = inst.OnlinePlayers;
                    if (!roster.Contains(joined)) inst.OnlinePlayers = new List<string>(roster) { joined };
                }
                else if (provider.TryParsePlayerLeave(line, out var left))
                {
                    var roster = inst.OnlinePlayers;
                    if (roster.Contains(left)) inst.OnlinePlayers = roster.Where(player => player != left).ToList();
                }

                if (!suppress) _ = hub.BroadcastLineAsync(line);

                // A server may be marked stalled after a quiet startup window and then finish loading.
                // Always let a later provider-specific ready marker recover it to Running.
                if ((inst.Status == GameServerStatus.Starting || inst.Status == GameServerStatus.Stalled)
                    && provider.TryParseStarted(line))
                {
                    inst.Status = GameServerStatus.Running;
                    inst.RunningSinceUtc = DateTime.UtcNow;
                    _ = BroadcastStatus(inst);
                    _ = SaveInstanceAsync(inst);
                }
            }
            catch { }
        }

        private async Task OnProcessExitedAsync(string id, int code)
        {
            if (!_instances.TryGetValue(id, out var inst)) return;
            bool expected = _processes.TryGetValue(id, out var proc) && proc.StopRequested;

            DetachRemoteConsole(id); // nothing to talk to once the process is gone
            inst.ChildPid = null;
            inst.ChildStartedUtc = null;
            inst.Adopted = false;
            inst.OnlinePlayers = new();
            inst.CpuPercent = 0;
            inst.RamUsedBytes = 0;
            inst.RunningSinceUtc = null;

            if (expected || inst.Status == GameServerStatus.Stopping)
            {
                inst.Status = GameServerStatus.Stopped;
                await BroadcastStatus(inst);
                await SaveInstanceAsync(inst);
                return;
            }

            // Unexpected exit = crash.
            inst.Status = GameServerStatus.Crashed;
            inst.LastError = $"Process exited unexpectedly with code {code}.";
            await BroadcastStatus(inst);
            await SaveInstanceAsync(inst);
            await ServiceLogError($"[KliveGames] '{inst.Name}' crashed (exit {code}).");

            if (inst.AutoRestart && ShouldAutoRestart(id))
            {
                await Task.Delay(3000);
                try { await StartAsync(id); }
                catch (Exception ex) { await ServiceLogError(ex, $"[KliveGames] Auto-restart of '{inst.Name}' failed."); }
            }
        }

        /// <summary>Debounced crash-restart: at most 3 restarts within a rolling 5-minute window.</summary>
        private bool ShouldAutoRestart(string id)
        {
            var now = DateTime.UtcNow;
            var window = TimeSpan.FromMinutes(5);
            var history = _restartHistory.GetOrAdd(id, _ => new List<DateTime>());
            lock (history)
            {
                history.RemoveAll(t => now - t > window);
                if (history.Count >= 3) return false;
                history.Add(now);
                return true;
            }
        }

        private async Task BroadcastStatus(GameServerInstance inst)
        {
            if (_consoleHubs.TryGetValue(inst.Id, out var hub))
                await hub.BroadcastEventAsync("status", new { status = inst.Status.ToString(), error = inst.LastError });
        }

        // ----------------------------------------------------------------- monitor loop

        private async Task MonitorLoopAsync(CancellationToken ct)
        {
            int tick = 0;
            int stallMinutes = await GetIntOmniSetting("KliveGames_StallTimeoutMinutes", 3);
            var stallWindow = TimeSpan.FromMinutes(Math.Max(1, stallMinutes));

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(4000, ct);
                    tick++;

                    foreach (var kv in _processes)
                    {
                        if (!_instances.TryGetValue(kv.Key, out var inst)) continue;
                        var proc = kv.Value;
                        if (!proc.IsRunning) continue;

                        var (cpu, ram) = proc.SampleResources();
                        inst.CpuPercent = cpu;
                        inst.RamUsedBytes = ram;

                        if (inst.Status == GameServerStatus.Starting && DateTime.UtcNow - proc.LastOutputUtc > stallWindow)
                        {
                            inst.Status = GameServerStatus.Stalled; // surfaced, never auto-killed
                            await BroadcastStatus(inst);
                        }
                    }

                    // Refresh the authoritative player roster ~every 12s.
                    if (tick % 3 == 0)
                    {
                        foreach (var inst in _instances.Values.Where(i => i.Status == GameServerStatus.Running))
                        {
                            var provider = _providers.Get(inst.GameType);
                            try
                            {
                                var listCmd = provider.BuildListCommand();
                                if (string.IsNullOrEmpty(listCmd)) continue; // game uses join/leave parsing only
                                _pendingListPoll[inst.Id] = DateTime.UtcNow;
                                await SendCommandAsync(inst.Id, listCmd, echo: false);
                            }
                            catch { }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { await ServiceLogError(ex, "[KliveGames] Monitor loop error.", appearInConsole: false); }
            }
        }

        // ----------------------------------------------------------------- networking / config

        /// <summary>Allocates a free primary port and every companion port required by the provider.</summary>
        private int AllocatePort(int preferred, IGameProvider provider)
        {
            int port = Math.Clamp(preferred, 1024, 65000);
            for (int candidate = port; candidate <= 65000; candidate++)
            {
                var bindings = provider.GetNetworkPorts(candidate);
                if (bindings.All(binding => binding.Port is >= 1024 and <= 65535)
                    && bindings.All(binding => IsPortFree(binding)))
                    return candidate;
            }
            throw new InvalidOperationException("No free port available.");
        }

        /// <summary>
        /// Refuses to spawn a server onto a port something else is already holding. That something is
        /// usually this server's own earlier process — one that outlived an Omnipotent restart and could
        /// not be identified for adoption. Starting on top of it yields one server that cannot bind and
        /// one that nothing is managing, which is worse than not starting at all.
        /// </summary>
        private async Task EnsurePortsAvailableAsync(GameServerInstance inst, IGameProvider provider, GameConsoleHub hub)
        {
            List<GameNetworkPort> taken = new();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) await Task.Delay(1000); // a process that just died may still be releasing its sockets
                taken = provider.GetNetworkPorts(inst.Port)
                    .Where(binding => !IsPortFree(binding, ignoreInstanceId: inst.Id))
                    .ToList();
                if (taken.Count == 0) return;
            }

            string ports = string.Join(", ", taken.Select(binding => $"{binding.Port}/{binding.Protocol}"));
            string message = $"Port {ports} is already in use, so '{inst.Name}' was not started. This is usually the server's own "
                + "previous process, still running from before Omnipotent restarted — end that process (or reboot) and start again.";
            inst.LastError = message;
            _ = SaveInstanceAsync(inst);
            await hub.BroadcastLineAsync($"[KliveGames] {message}");
            await ServiceLogError($"[KliveGames] {message}");
            throw new InvalidOperationException(message);
        }

        private bool IsPortFree(GameNetworkPort binding, string? ignoreInstanceId = null)
        {
            foreach (var instance in _instances.Values)
            {
                if (instance.Id == ignoreInstanceId) continue;
                var existingProvider = _providers.Get(instance.GameType);
                if (existingProvider.GetNetworkPorts(instance.Port).Any(existing =>
                    existing.Port == binding.Port
                    && existing.Protocol.Equals(binding.Protocol, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            try
            {
                if (binding.Protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase))
                {
                    using var listener = new UdpClient(new IPEndPoint(IPAddress.Any, binding.Port));
                }
                else
                {
                    var listener = new TcpListener(IPAddress.Any, binding.Port);
                    listener.Start();
                    listener.Stop();
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Applies or clears the per-server UPnP forward and resolves the public join address.</summary>
        public async Task<string> SetPublicAsync(string id, bool makePublic)
        {
            var inst = GetInstance(id) ?? throw new InvalidOperationException("Server not found.");
            inst.Public = makePublic;

            if (makePublic)
            {
                var message = await EnsurePublicAsync(inst, true);
                await SaveInstanceAsync(inst);
                return message;
            }
            else
            {
                await RemovePublicPortsAsync(inst);
                inst.PublicJoinAddress = null;
                await SaveInstanceAsync(inst);
                return "This server is now local-only.";
            }
        }

        private async Task<string> EnsurePublicAsync(GameServerInstance inst, bool persist)
        {
            // The public join address is always the domain (klive.dev resolves to this host's public IP);
            // we still try to open the UPnP forward so it's reachable without manual router config.
            inst.PublicJoinAddress = $"klive.dev:{inst.Port}";
            try
            {
                var availableObj = await ExecuteServiceMethod<global::Omnipotent.Services.PortForwardManager.PortForwardManager>("IsUpnpAvailable");
                bool available = availableObj is bool b && b;
                if (available)
                {
                    foreach (var binding in _providers.Get(inst.GameType).GetNetworkPorts(inst.Port))
                    {
                        await ExecuteServiceMethod<global::Omnipotent.Services.PortForwardManager.PortForwardManager>(
                            "EnsurePortForwarded", binding.Port, binding.Port, binding.Protocol,
                            $"KliveGames: {inst.Name} ({binding.Purpose})");
                    }
                    if (persist) await SaveInstanceAsync(inst);
                    return $"Server is public at {inst.PublicJoinAddress}.";
                }

                if (persist) await SaveInstanceAsync(inst);
                string ports = string.Join(", ", _providers.Get(inst.GameType).GetNetworkPorts(inst.Port)
                    .Select(binding => $"{binding.Protocol} {binding.Port}"));
                return $"Join at {inst.PublicJoinAddress} — no UPnP router found, so forward {ports} to this machine.";
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "[KliveGames] Port-forward setup failed.");
                if (persist) await SaveInstanceAsync(inst);
                return $"Join at {inst.PublicJoinAddress} (auto port-forward failed: {ex.Message}).";
            }
        }

        public async Task ApplyConfigAsync(string id, Dictionary<string, string> values)
        {
            var inst = GetInstance(id) ?? throw new InvalidOperationException("Server not found.");
            var provider = _providers.Get(inst.GameType);
            int oldPort = inst.Port;
            int? requestedPort = null;
            if (values.TryGetValue(provider.PortConfigKey, out var portText)
                && int.TryParse(portText, out int parsedPort)
                && parsedPort != oldPort)
            {
                if (parsedPort is < 1024 or > 65000)
                    throw new ArgumentOutOfRangeException(provider.PortConfigKey, "The game port must be between 1024 and 65000.");
                if (!provider.GetNetworkPorts(parsedPort).All(binding => IsPortFree(binding, inst.Id)))
                    throw new InvalidOperationException("The selected game or companion port is already in use.");
                requestedPort = parsedPort;
            }

            await provider.ApplyConfigAsync(inst, values);

            // Keep the managed port in sync if the operator changed the port via config.
            if (requestedPort is int newPort)
            {
                if (inst.Public) await RemovePublicPortsAsync(inst, provider.GetNetworkPorts(oldPort));
                inst.Port = newPort;
                if (inst.Public) _ = EnsurePublicAsync(inst, true);
            }
            await SaveInstanceAsync(inst);
        }

        private async Task RemovePublicPortsAsync(GameServerInstance inst, IReadOnlyList<GameNetworkPort>? bindings = null)
        {
            bindings ??= _providers.Get(inst.GameType).GetNetworkPorts(inst.Port);
            foreach (var binding in bindings)
            {
                try
                {
                    await ExecuteServiceMethod<global::Omnipotent.Services.PortForwardManager.PortForwardManager>(
                        "RemovePortForward", binding.Port, binding.Protocol);
                }
                catch { }
            }
        }
    }
}
