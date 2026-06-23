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
        }

        private readonly ConcurrentDictionary<string, GameServerInstance> _instances = new();
        private readonly ConcurrentDictionary<string, ManagedGameProcess> _processes = new();
        private readonly ConcurrentDictionary<string, GameConsoleHub> _consoleHubs = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
        private readonly ConcurrentDictionary<string, List<DateTime>> _restartHistory = new();
        private readonly ConcurrentDictionary<string, DateTime> _pendingListPoll = new();

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

                // Auto-start flagged servers.
                foreach (var inst in _instances.Values.Where(i => i.AutoStart))
                    _ = StartAsync(inst.Id);

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

                    // Reconcile: an app restart loses control of any prior child process. If the old PID is
                    // still a live java process, terminate it to free the port, then mark Stopped.
                    if (inst.ChildPid is int pid)
                    {
                        try
                        {
                            var p = Process.GetProcessById(pid);
                            if (p.ProcessName.Contains("java", StringComparison.OrdinalIgnoreCase))
                            {
                                p.Kill(entireProcessTree: true);
                                await ServiceLog($"[KliveGames] Reclaimed orphaned process {pid} for '{inst.Name}'.");
                            }
                        }
                        catch { /* no such process — fine */ }
                    }

                    inst.Status = GameServerStatus.Stopped;
                    inst.ChildPid = null;
                    inst.OnlinePlayers = new();
                    inst.CpuPercent = 0;
                    inst.RamUsedBytes = 0;
                    inst.RunningSinceUtc = null;

                    _instances[inst.Id] = inst;
                    _consoleHubs[inst.Id] = new GameConsoleHub();
                    await SaveInstanceAsync(inst);
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, $"[KliveGames] Failed to load instance from {dir}.");
                }
            }
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
            if (!req.EulaAccepted) throw new InvalidOperationException("The Minecraft EULA must be accepted to deploy a server.");

            var provider = _providers.Get(req.GameType);
            if (!provider.Implemented) throw new InvalidOperationException($"{provider.DisplayName} is not available yet.");

            int port = AllocatePort(req.Port > 0 ? req.Port : provider.DefaultPort);

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
                RamMb = Math.Clamp(req.RamMb, 512, 32768),
                UseAikarFlags = req.UseAikarFlags,
                Public = req.Public,
                AutoStart = req.AutoStart,
                Status = GameServerStatus.Provisioning,
                ServerDirectory = serverDir,
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
                var spec = await provider.BuildLaunchSpecAsync(inst, cancellationToken.Token);

                var proc = new ManagedGameProcess(spec, ringCapacity: 500, logError: async m => await ServiceLogError($"[KliveGames:{inst.Name}] {m}"));
                proc.OnConsoleLine += line => HandleConsoleLine(inst, hub, provider, line);
                proc.OnExited += code => { _ = OnProcessExitedAsync(inst.Id, code); };

                inst.OnlinePlayers = new();
                inst.Status = GameServerStatus.Starting;
                inst.LastError = null;

                await proc.StartAsync(cancellationToken.Token);
                _processes[id] = proc;
                inst.ChildPid = proc.Pid;
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

                int grace = await GetIntOmniSetting("KliveGames_StopGraceSeconds", 90);
                await proc.StopGracefullyAsync(provider.GetGracefulStopCommand(), TimeSpan.FromSeconds(grace), killOnExpiry: true);

                inst.Status = GameServerStatus.Stopped;
                inst.ChildPid = null;
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
                inst.Status = GameServerStatus.Stopped;
                inst.ChildPid = null;
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

            if (inst.Public)
                try { await ExecuteServiceMethod<global::Omnipotent.Services.PortForwardManager.PortForwardManager>("RemovePortForward", inst.Port, "TCP"); } catch { }

            _instances.TryRemove(id, out _);
            _processes.TryRemove(id, out var p); p?.Dispose();
            _consoleHubs.TryRemove(id, out _);
            _locks.TryRemove(id, out _);
            _restartHistory.TryRemove(id, out _);

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
            if (_processes.TryGetValue(id, out var proc) && proc.IsRunning)
            {
                await proc.SendCommandAsync(command);
                if (echo && _consoleHubs.TryGetValue(id, out var hub))
                    await hub.BroadcastLineAsync($"> {command}");
            }
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

        private void HandleConsoleLine(GameServerInstance inst, GameConsoleHub hub, IGameProvider provider, string line)
        {
            try
            {
                bool suppress = false;

                if (provider.TryParseListReply(line, out _, out int max, out var names))
                {
                    inst.OnlinePlayers = names.ToList();
                    inst.MaxPlayers = max;
                    // Hide the reply to an internal (monitor) roster poll so the live console isn't spammed.
                    if (_pendingListPoll.TryRemove(inst.Id, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(3))
                        suppress = true;
                }
                else if (provider.TryParsePlayerJoin(line, out var joined))
                {
                    if (!inst.OnlinePlayers.Contains(joined)) inst.OnlinePlayers.Add(joined);
                }
                else if (provider.TryParsePlayerLeave(line, out var left))
                {
                    inst.OnlinePlayers.Remove(left);
                }

                if (!suppress) _ = hub.BroadcastLineAsync(line);

                if (inst.Status == GameServerStatus.Starting && provider.TryParseStarted(line))
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

            inst.ChildPid = null;
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
                                _pendingListPoll[inst.Id] = DateTime.UtcNow;
                                await SendCommandAsync(inst.Id, provider.BuildListCommand(), echo: false);
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

        /// <summary>Allocates a free TCP port at or above <paramref name="preferred"/>.</summary>
        private int AllocatePort(int preferred)
        {
            int port = Math.Clamp(preferred, 1024, 65000);
            for (int candidate = port; candidate <= 65000; candidate++)
            {
                if (IsPortFree(candidate)) return candidate;
            }
            throw new InvalidOperationException("No free port available.");
        }

        private bool IsPortFree(int port)
        {
            if (_instances.Values.Any(i => i.Port == port)) return false;
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                listener.Stop();
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
                try { await ExecuteServiceMethod<global::Omnipotent.Services.PortForwardManager.PortForwardManager>("RemovePortForward", inst.Port, "TCP"); } catch { }
                inst.PublicJoinAddress = null;
                await SaveInstanceAsync(inst);
                return "This server is now local-only.";
            }
        }

        private async Task<string> EnsurePublicAsync(GameServerInstance inst, bool persist)
        {
            try
            {
                var availableObj = await ExecuteServiceMethod<global::Omnipotent.Services.PortForwardManager.PortForwardManager>("IsUpnpAvailable");
                bool available = availableObj is bool b && b;
                if (!available)
                {
                    inst.PublicJoinAddress = null;
                    if (persist) await SaveInstanceAsync(inst);
                    return $"No UPnP router was found — forward TCP {inst.Port} manually to make this server reachable.";
                }

                await ExecuteServiceMethod<global::Omnipotent.Services.PortForwardManager.PortForwardManager>(
                    "EnsurePortForwarded", inst.Port, inst.Port, "TCP", $"KliveGames: {inst.Name}");

                var extObj = await ExecuteServiceMethod<global::Omnipotent.Services.PortForwardManager.PortForwardManager>("GetExternalIPAddress");
                string? ext = extObj as string;
                inst.PublicJoinAddress = string.IsNullOrEmpty(ext) ? null : $"{ext}:{inst.Port}";
                if (persist) await SaveInstanceAsync(inst);

                return inst.PublicJoinAddress != null
                    ? $"Server is public at {inst.PublicJoinAddress}."
                    : $"Port {inst.Port} forwarded (external IP unavailable).";
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "[KliveGames] Port-forward setup failed.");
                return $"Port-forward setup failed: {ex.Message}";
            }
        }

        public async Task ApplyConfigAsync(string id, Dictionary<string, string> values)
        {
            var inst = GetInstance(id) ?? throw new InvalidOperationException("Server not found.");
            var provider = _providers.Get(inst.GameType);
            await provider.ApplyConfigAsync(inst, values);

            // Keep the managed port in sync if the operator changed server-port.
            if (values.TryGetValue("server-port", out var sp) && int.TryParse(sp, out int newPort) && newPort != inst.Port)
            {
                inst.Port = newPort;
                if (inst.Public) _ = EnsurePublicAsync(inst, true);
            }
            await SaveInstanceAsync(inst);
        }
    }
}
