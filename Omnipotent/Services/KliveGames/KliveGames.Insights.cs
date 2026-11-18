using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveGames.Models;
using Omnipotent.Services.KliveGames.Runtime;

namespace Omnipotent.Services.KliveGames
{
    /// <summary>
    /// What the KliveGames page needs beyond the instance records: each server's history, who has played,
    /// what it used over the last hour and day, how much disk it takes, whether its router forwards exist,
    /// and backups that run in the background (on a schedule, with retention, and with a safety copy before a
    /// restore). Everything here is read from memory by <see cref="BuildOverview"/>; the slow parts (walking a
    /// world folder, the router's table, zipping) happen in the background.
    /// </summary>
    public partial class KliveGames
    {
        private readonly ServerActivityLog _activity;
        private readonly ConcurrentDictionary<string, PlayerBook> _playerBooks = new();
        private readonly ConcurrentDictionary<string, VitalsRing> _vitals = new();
        private readonly StorageMeter _storage = new();
        private readonly ConcurrentDictionary<string, BackupJob> _backupJobs = new();
        private readonly ConcurrentDictionary<string, (DateTime atUtc, IReadOnlyList<BackupManager.BackupInfo> list)> _backupLists = new();
        private readonly ConcurrentDictionary<string, DateTime> _consoleStopUtc = new();
        private readonly ConcurrentDictionary<string, (Regex rx, TaskCompletionSource<bool> done)> _lineWaiters = new();
        private readonly object _jobGate = new();
        private int _choresRunning;
        private int _forwardRefreshRunning;

        private volatile ForwardTable? _forwardTable;
        private DateTime _forwardsReadUtc = DateTime.MinValue;
        private DateTime _overviewSeenUtc = DateTime.MinValue;
        private DateTime _selfHealUtc = DateTime.MinValue;

        public ServerActivityLog Activity => _activity;

        private static string PlayersPath(string id) => Path.Combine(InstanceDir(id), "players.json");
        public PlayerBook PlayersOf(string id) => _playerBooks.GetOrAdd(id, key => new PlayerBook(PlayersPath(key)));
        public VitalsRing VitalsOf(string id) => _vitals.GetOrAdd(id, _ => new VitalsRing());

        private bool IsRunning(string id) => _processes.TryGetValue(id, out var p) && p.IsRunning;

        private static void SetLastError(GameServerInstance inst, string? message)
        {
            inst.LastError = message;
            inst.LastErrorUtc = message == null ? null : DateTime.UtcNow;
        }

        /// <summary>Every roster change goes through here, so joins and leaves are written down once.</summary>
        private void SetRoster(GameServerInstance inst, IEnumerable<string> names)
        {
            var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            inst.OnlinePlayers = list;
            var (joined, left) = PlayersOf(inst.Id).Apply(list, DateTime.UtcNow);
            foreach (var name in joined)
                _activity.Record(inst.Id, ActivityKinds.PlayerJoin, $"{name} joined.", e => e.Player = name);
            foreach (var (name, seconds) in left)
                _activity.Record(inst.Id, ActivityKinds.PlayerLeave, $"{name} left.", e => { e.Player = name; e.Seconds = Math.Round(seconds); });
        }

        /// <summary>The server stopped: close everyone's session without a "left" line each — the stop says it.</summary>
        private void ClearRoster(GameServerInstance inst)
        {
            inst.OnlinePlayers = new();
            PlayersOf(inst.Id).CloseAll(DateTime.UtcNow);
        }

        private void ForgetInsights(string id)
        {
            _activity.Forget(id);
            _playerBooks.TryRemove(id, out _);
            _vitals.TryRemove(id, out _);
            _storage.Forget(id);
            _backupJobs.TryRemove(id, out _);
            _backupLists.TryRemove(id, out _);
            _consoleStopUtc.TryRemove(id, out _);
        }

        // ----------------------------------------------------------------- waiting for a console line

        private async Task<bool> WaitForLineAsync(string id, Regex rx, Func<Task> send, TimeSpan timeout)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _lineWaiters[id] = (rx, done);
            try
            {
                await send();
                var first = await Task.WhenAny(done.Task, Task.Delay(timeout));
                return first == done.Task;
            }
            finally { _lineWaiters.TryRemove(id, out _); }
        }

        private void CompleteLineWaiter(string id, string line)
        {
            if (_lineWaiters.TryGetValue(id, out var waiter) && waiter.rx.IsMatch(line)) waiter.done.TrySetResult(true);
        }

        // ----------------------------------------------------------------- backups

        public sealed class BackupJob
        {
            public string ServerId { get; init; } = "";
            /// <summary>"backup" or "restore".</summary>
            public string Operation { get; init; } = "backup";
            /// <summary>"manual" or "scheduled".</summary>
            public string Reason { get; init; } = "manual";
            public string? Actor { get; init; }
            public string? RestoreFrom { get; init; }
            public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
            public DateTime? FinishedUtc { get; set; }
            /// <summary>queued → saving (asking the game to write) → zipping | safety → restoring → done | failed | cancelled.</summary>
            public string Stage { get; set; } = "queued";
            public BackupManager.BackupProgress Progress { get; } = new();
            public string? Error { get; set; }
            public string? Note { get; set; }
            public BackupManager.BackupInfo? Result { get; set; }
            public BackupManager.BackupInfo? Safety { get; set; }
            internal readonly CancellationTokenSource Cts = new();
            internal Task Completion { get; set; } = Task.CompletedTask;
            public bool Finished => FinishedUtc != null;
            public void Cancel() { try { Cts.Cancel(); } catch { } }

            public object View() => new
            {
                operation = Operation,
                reason = Reason,
                actor = Actor,
                restoreFrom = RestoreFrom,
                startedUtc = StartedUtc,
                finishedUtc = FinishedUtc,
                stage = Stage,
                filesTotal = Progress.FilesTotal,
                filesDone = Progress.FilesDone,
                filesSkipped = Progress.FilesSkipped,
                bytesTotal = Progress.BytesTotal,
                bytesDone = Progress.BytesDone,
                error = Error,
                note = Note,
                backupId = Result?.Id,
                sizeBytes = Result?.SizeBytes,
                safetyId = Safety?.Id,
            };
        }

        public BackupJob? BackupJobOf(string id)
        {
            if (!_backupJobs.TryGetValue(id, out var job)) return null;
            // A finished job stays visible for ten minutes so the page can show how it ended.
            if (job.FinishedUtc is DateTime done && DateTime.UtcNow - done > TimeSpan.FromMinutes(10))
            {
                _backupJobs.TryRemove(new KeyValuePair<string, BackupJob>(id, job));
                return null;
            }
            return job;
        }

        public IReadOnlyList<BackupManager.BackupInfo> BackupListOf(string id)
        {
            if (_backupLists.TryGetValue(id, out var cached) && DateTime.UtcNow - cached.atUtc < TimeSpan.FromSeconds(60))
                return cached.list;
            IReadOnlyList<BackupManager.BackupInfo> list;
            try { list = _backups.List(id); }
            catch { list = Array.Empty<BackupManager.BackupInfo>(); }
            _backupLists[id] = (DateTime.UtcNow, list);
            return list;
        }

        private void BackupsChanged(string id)
        {
            _backupLists.TryRemove(id, out _);
            _storage.Invalidate(id);
        }

        private DateTime? LastBackupUtcOf(GameServerInstance inst)
            => inst.LastBackupUtc ?? BackupListOf(inst.Id).FirstOrDefault()?.CreatedUtc;

        /// <summary>Starts a backup in the background, or returns the one already running for this server.</summary>
        public (BackupJob job, bool started) StartBackup(string id, string reason, string? actor)
        {
            var inst = GetInstance(id) ?? throw new InvalidOperationException("Server not found.");
            if (inst.Status == GameServerStatus.Provisioning) throw new InvalidOperationException("The server is still installing.");
            lock (_jobGate)
            {
                if (_backupJobs.TryGetValue(id, out var current) && !current.Finished) return (current, false);
                var job = new BackupJob { ServerId = id, Operation = "backup", Reason = reason, Actor = actor };
                _backupJobs[id] = job;
                job.Completion = Task.Run(() => RunBackupAsync(inst, job));
                return (job, true);
            }
        }

        private async Task RunBackupAsync(GameServerInstance inst, BackupJob job)
        {
            var provider = _providers.Get(inst.GameType);
            var hooks = provider.GetBackupSaveHooks();
            bool running = _processes.TryGetValue(inst.Id, out var proc) && proc.IsRunning && inst.Status == GameServerStatus.Running;
            bool canTalk = running && (_remoteConsoles.ContainsKey(inst.Id) || proc!.ConsoleAttached);
            bool held = false;
            var began = DateTime.UtcNow;
            string prefix = job.Reason == "scheduled" ? BackupManager.ScheduledPrefix : BackupManager.ManualPrefix;
            try
            {
                if (running && hooks != null)
                {
                    if (canTalk)
                    {
                        job.Stage = "saving";
                        if (hooks.Hold != null) { await SendCommandAsync(inst.Id, hooks.Hold); held = true; }
                        bool confirmed = await WaitForLineAsync(inst.Id, hooks.FlushDone, () => SendCommandAsync(inst.Id, hooks.Flush), TimeSpan.FromSeconds(30));
                        if (!confirmed) job.Note = "The server did not confirm its save within 30 s, so its files were copied as they were.";
                    }
                    else job.Note = "This server's console is not connected, so it could not be asked to save before the copy.";
                }

                job.Stage = "zipping";
                var info = await Task.Run(() => _backups.Create(inst.Id, inst.ServerDirectory, prefix, job.Progress, job.Cts.Token));
                job.Result = info;
                inst.LastBackupUtc = DateTime.UtcNow;
                await SaveInstanceAsync(inst);

                double seconds = Math.Round((DateTime.UtcNow - began).TotalSeconds, 1);
                string skipped = job.Progress.FilesSkipped > 0 ? $"; {job.Progress.FilesSkipped} locked file(s) skipped" : "";
                _activity.Record(inst.Id, ActivityKinds.BackupCreated,
                    $"{(job.Reason == "scheduled" ? "Scheduled backup" : "Backup")}: {FormatBytes(info.SizeBytes)} in {FormatSeconds(seconds)}{skipped}.",
                    e => { e.Actor = job.Actor; e.Bytes = info.SizeBytes; e.Seconds = seconds; });

                if (job.Reason == "scheduled" && inst.BackupKeep > 0)
                {
                    var removed = _backups.Prune(inst.Id, inst.BackupKeep);
                    if (removed.Count > 0)
                        _activity.Record(inst.Id, ActivityKinds.BackupPruned,
                            $"Removed {removed.Count} older scheduled backup(s), keeping {inst.BackupKeep}.",
                            e => e.Bytes = removed.Sum(b => b.SizeBytes));
                }
                job.Stage = "done";
            }
            catch (OperationCanceledException)
            {
                job.Stage = "cancelled";
            }
            catch (Exception ex)
            {
                job.Stage = "failed";
                job.Error = ex.Message;
                _activity.Record(inst.Id, ActivityKinds.BackupFailed, $"Backup failed: {ex.Message}", e => e.Actor = job.Actor);
                await ServiceLogError(ex, $"[KliveGames] Backup of '{inst.Name}' failed.");
            }
            finally
            {
                if (held && hooks?.Release != null)
                {
                    try { await SendCommandAsync(inst.Id, hooks.Release); } catch { }
                }
                job.FinishedUtc = DateTime.UtcNow;
                BackupsChanged(inst.Id);
            }
        }

        /// <summary>Restores a backup in the background, by default copying the current files aside first
        /// (a "before-restore-" backup that retention never deletes) so the restore can itself be undone.</summary>
        public (BackupJob job, bool started) StartRestore(string id, string backupId, bool safetyFirst, string? actor)
        {
            var inst = GetInstance(id) ?? throw new InvalidOperationException("Server not found.");
            if (inst.Status is GameServerStatus.Running or GameServerStatus.Starting or GameServerStatus.Stopping or GameServerStatus.Stalled || IsRunning(id))
                throw new InvalidOperationException("Stop the server before restoring a backup.");
            if (inst.Status == GameServerStatus.Provisioning) throw new InvalidOperationException("The server is still installing.");
            _backups.ResolveBackupPath(id, backupId); // throws for a bad or missing id
            lock (_jobGate)
            {
                if (_backupJobs.TryGetValue(id, out var current) && !current.Finished) return (current, false);
                var job = new BackupJob { ServerId = id, Operation = "restore", Reason = "manual", Actor = actor, RestoreFrom = backupId };
                _backupJobs[id] = job;
                job.Completion = Task.Run(() => RunRestoreAsync(inst, job, safetyFirst));
                return (job, true);
            }
        }

        private async Task RunRestoreAsync(GameServerInstance inst, BackupJob job, bool safetyFirst)
        {
            var began = DateTime.UtcNow;
            try
            {
                if (safetyFirst)
                {
                    job.Stage = "safety";
                    job.Safety = await Task.Run(() => _backups.Create(inst.Id, inst.ServerDirectory, BackupManager.SafetyPrefix, job.Progress, job.Cts.Token));
                }
                job.Stage = "restoring";
                await Task.Run(() => _backups.Restore(inst.Id, inst.ServerDirectory, job.RestoreFrom!));
                double seconds = Math.Round((DateTime.UtcNow - began).TotalSeconds, 1);
                string kept = job.Safety != null ? $" The files it replaced are kept as {job.Safety.Id}." : "";
                _activity.Record(inst.Id, ActivityKinds.BackupRestored, $"Restored {job.RestoreFrom}.{kept}", e => { e.Actor = job.Actor; e.Seconds = seconds; });
                job.Stage = "done";
            }
            catch (OperationCanceledException) { job.Stage = "cancelled"; }
            catch (Exception ex)
            {
                job.Stage = "failed";
                job.Error = ex.Message;
                _activity.Record(inst.Id, ActivityKinds.BackupFailed, $"Restore of {job.RestoreFrom} failed: {ex.Message}", e => e.Actor = job.Actor);
                await ServiceLogError(ex, $"[KliveGames] Restore of '{inst.Name}' from {job.RestoreFrom} failed.");
            }
            finally
            {
                job.FinishedUtc = DateTime.UtcNow;
                BackupsChanged(inst.Id);
            }
        }

        public void DeleteBackup(string id, string backupId, string? actor)
        {
            var inst = GetInstance(id) ?? throw new InvalidOperationException("Server not found.");
            if (_backupJobs.TryGetValue(id, out var job) && !job.Finished && job.RestoreFrom == backupId)
                throw new InvalidOperationException("That backup is being restored right now.");
            long size = 0;
            try { size = new FileInfo(_backups.ResolveBackupPath(id, backupId)).Length; } catch { }
            _backups.Delete(id, backupId);
            BackupsChanged(id);
            _activity.Record(inst.Id, ActivityKinds.BackupDeleted, $"Deleted backup {backupId}.", e => { e.Actor = actor; e.Bytes = size; });
        }

        /// <summary>Is a scheduled backup due? Never-backed-up servers are due at once; after that, every
        /// <see cref="GameServerInstance.BackupEveryHours"/> — but a stopped server that has not run since its last
        /// backup is skipped, because its files have not changed.</summary>
        internal static bool BackupIsDue(GameServerInstance inst, DateTime? lastBackupUtc, bool running, DateTime nowUtc)
        {
            if (inst.BackupEveryHours <= 0 || inst.Status == GameServerStatus.Provisioning) return false;
            if (lastBackupUtc is not DateTime last) return true;
            if (nowUtc - last < TimeSpan.FromHours(inst.BackupEveryHours)) return false;
            return running || PlayedSince(inst, last);
        }

        /// <summary>When the next scheduled backup will run; null when there is no schedule or the server
        /// is idle (stopped and unchanged since its last backup), in which case it runs after the next start.</summary>
        internal static DateTime? NextBackupDueUtc(GameServerInstance inst, DateTime? lastBackupUtc, bool running, DateTime nowUtc)
        {
            if (inst.BackupEveryHours <= 0) return null;
            if (lastBackupUtc is not DateTime last) return nowUtc;
            if (!running && !PlayedSince(inst, last)) return null;
            var due = last.AddHours(inst.BackupEveryHours);
            return due < nowUtc ? nowUtc : due;
        }

        private static bool PlayedSince(GameServerInstance inst, DateTime since)
        {
            var active = inst.LastStoppedUtc ?? inst.LastStartedUtc;
            return active is DateTime a && a > since;
        }

        // ----------------------------------------------------------------- background chores

        /// <summary>Once a minute from the monitor loop: due scheduled backups, disk readings, router forwards.</summary>
        private async Task RunInsightChoresAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _choresRunning, 1) == 1) return;
            try
            {
                var now = DateTime.UtcNow;
                foreach (var inst in _instances.Values.ToList())
                {
                    try
                    {
                        if (BackupJobOf(inst.Id) is { Finished: false }) continue;
                        if (BackupIsDue(inst, LastBackupUtcOf(inst), IsRunning(inst.Id), now))
                            StartBackup(inst.Id, "scheduled", null);
                    }
                    catch (Exception ex) { await ServiceLogError(ex, $"[KliveGames] Could not start the scheduled backup of '{inst.Name}'.", appearInConsole: false); }
                }

                await _storage.RefreshDueAsync(_instances.Values.ToList()
                    .Select(i => (i.Id, i.ServerDirectory, _backups.BackupDirectory(i.Id))), ct);

                bool anyPublic = _instances.Values.Any(i => i.Public);
                bool publicRunning = _instances.Values.Any(i => i.Public && IsRunning(i.Id));
                bool watched = DateTime.UtcNow - _overviewSeenUtc < TimeSpan.FromMinutes(3);
                if (anyPublic && (publicRunning || watched) && DateTime.UtcNow - _forwardsReadUtc > TimeSpan.FromSeconds(60))
                    await RefreshForwardsAsync();

                if (publicRunning && _forwardTable is { Available: true } table && DateTime.UtcNow - _selfHealUtc > TimeSpan.FromMinutes(10))
                {
                    _selfHealUtc = DateTime.UtcNow;
                    await RestoreDroppedForwardsAsync(table);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { await ServiceLogError(ex, "[KliveGames] Background chores failed.", appearInConsole: false); }
            finally { Interlocked.Exchange(ref _choresRunning, 0); }
        }

        // ----------------------------------------------------------------- router forwards

        /// <summary>The router's UPnP table, reduced to what KliveGames asks: is "UDP:28015" forwarded, and to whom.</summary>
        internal sealed class ForwardTable
        {
            public DateTime AsOfUtc { get; init; }
            public bool Available { get; init; }
            public string? Error { get; init; }
            public string? LocalIp { get; init; }
            /// <summary>"PROTO:port" → the LAN address it is forwarded to.</summary>
            public Dictionary<string, string> Mapped { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        }

        internal static ForwardTable ParseForwardTable(string json, DateTime nowUtc)
        {
            var jo = JObject.Parse(json);
            bool ok = (bool?)jo["success"] ?? false;
            var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in jo["mappings"] as JArray ?? new JArray())
            {
                string proto = ((string?)m["Protocol"] ?? "").ToUpperInvariant();
                int port = (int?)m["PublicPort"] ?? 0;
                if (port <= 0 || proto.Length == 0) continue;
                mapped[$"{proto}:{port}"] = (string?)m["PrivateIp"] ?? "";
            }
            return new ForwardTable
            {
                AsOfUtc = (DateTime?)jo["asOfUtc"] ?? nowUtc,
                Available = ok,
                Error = ok ? null : (string?)jo["error"],
                LocalIp = (string?)jo["localIp"],
                Mapped = mapped,
            };
        }

        /// <summary>"forwarded", "missing", "elsewhere" (forwarded to another machine), "no-upnp" or "unknown".</summary>
        internal static string PortState(ForwardTable? table, GameNetworkPort binding)
        {
            if (table == null) return "unknown";
            if (!table.Available) return "no-upnp";
            if (!table.Mapped.TryGetValue($"{binding.Protocol.ToUpperInvariant()}:{binding.Port}", out var ip)) return "missing";
            return string.IsNullOrEmpty(table.LocalIp) || string.IsNullOrEmpty(ip) || ip == table.LocalIp ? "forwarded" : "elsewhere";
        }

        private void InvalidateReachability() => _forwardsReadUtc = DateTime.MinValue;

        private async Task RefreshForwardsAsync()
        {
            if (Interlocked.Exchange(ref _forwardRefreshRunning, 1) == 1) return;
            try
            {
                var result = await ExecuteServiceMethod<global::Omnipotent.Services.PortForwardManager.PortForwardManager>("GetMappingSnapshotJson");
                if (result is string json) _forwardTable = ParseForwardTable(json, DateTime.UtcNow);
                _forwardsReadUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _forwardTable = new ForwardTable { AsOfUtc = DateTime.UtcNow, Available = false, Error = ex.Message };
                _forwardsReadUtc = DateTime.UtcNow;
            }
            finally { Interlocked.Exchange(ref _forwardRefreshRunning, 0); }
        }

        /// <summary>A router drops UPnP forwards when it reboots, and KliveGames used to add them only when a
        /// server started — so a server stayed "public" with nothing reaching it. Put back any that are gone.</summary>
        private async Task RestoreDroppedForwardsAsync(ForwardTable table)
        {
            bool changed = false;
            foreach (var inst in _instances.Values.Where(i => i.Public && IsRunning(i.Id)).ToList())
            {
                var missing = _providers.Get(inst.GameType).GetNetworkPorts(inst.Port)
                    .Where(binding => PortState(table, binding) == "missing")
                    .ToList();
                if (missing.Count == 0) continue;
                var restored = new List<string>();
                foreach (var binding in missing)
                {
                    try
                    {
                        var added = await ExecuteServiceMethod<global::Omnipotent.Services.PortForwardManager.PortForwardManager>(
                            "EnsurePortForwarded", binding.Port, binding.Port, binding.Protocol, $"KliveGames: {inst.Name} ({binding.Purpose})");
                        if (added is true) restored.Add($"{binding.Protocol} {binding.Port}");
                    }
                    catch (Exception ex) { await ServiceLogError(ex, $"[KliveGames] Could not restore the forward for '{inst.Name}'.", appearInConsole: false); }
                }
                if (restored.Count > 0)
                {
                    changed = true;
                    _activity.Record(inst.Id, ActivityKinds.ForwardRestored, $"The router had dropped the forward for {string.Join(", ", restored)}; added it back.");
                    await ServiceLog($"[KliveGames] Restored dropped router forwards for '{inst.Name}': {string.Join(", ", restored)}.");
                }
            }
            if (changed)
            {
                InvalidateReachability();
                await RefreshForwardsAsync();
            }
        }

        // ----------------------------------------------------------------- overview

        /// <summary>Everything the page shows, in one read from memory. Safe to call every few seconds.</summary>
        public object BuildOverview()
        {
            var now = DateTime.UtcNow;
            _overviewSeenUtc = now;
            if (_instances.Values.Any(i => i.Public) && now - _forwardsReadUtc > TimeSpan.FromSeconds(90))
                _ = Task.Run(RefreshForwardsAsync);

            var table = _forwardTable;
            var servers = ListInstances().Select(inst => ServerView(inst, table, now)).ToList();
            return new
            {
                success = true,
                host = HostView(),
                upnp = table == null ? null : new { available = table.Available, error = table.Error, asOfUtc = table.AsOfUtc, localIp = table.LocalIp },
                servers,
                activity = _activity.RecentAll(30).Select(ActivityView).ToList(),
            };
        }

        private object ServerView(GameServerInstance inst, ForwardTable? table, DateTime now)
        {
            var provider = _providers.Get(inst.GameType);
            bool running = IsRunning(inst.Id);
            bool consoleAttached = _remoteConsoles.ContainsKey(inst.Id) || (_processes.TryGetValue(inst.Id, out var proc) && proc.IsRunning && proc.ConsoleAttached);
            var backups = BackupListOf(inst.Id);
            var lastBackupUtc = inst.LastBackupUtc ?? backups.FirstOrDefault()?.CreatedUtc;
            var job = BackupJobOf(inst.Id);
            var storage = _storage.Get(inst.Id);
            var book = PlayersOf(inst.Id);
            var lastActivity = _activity.Recent(inst.Id, 1).FirstOrDefault();
            var lastCrash = _activity.Last(inst.Id, ActivityKinds.Crashed, ActivityKinds.ProvisionFailed, ActivityKinds.StartFailed);
            DateTime? lastErrorAt = inst.LastErrorUtc ?? (inst.LastError != null ? (inst.LastStartedUtc ?? inst.CreatedUtc) : null);

            return new
            {
                id = inst.Id,
                name = inst.Name,
                gameType = inst.GameType.ToString(),
                flavor = inst.Flavor.ToString(),
                version = inst.Version,
                flavorBuild = inst.FlavorBuild,
                loaderVersion = inst.LoaderVersion,
                javaMajor = provider.UsesMemoryLimit ? inst.JavaMajor : (int?)null,
                port = inst.Port,
                protocol = provider.Protocol,
                ramMb = inst.RamMb,
                usesMemoryLimit = provider.UsesMemoryLimit,
                useAikarFlags = inst.UseAikarFlags,
                autoStart = inst.AutoStart,
                autoRestart = inst.AutoRestart,
                isPublic = inst.Public,
                status = inst.Status.ToString(),
                adopted = inst.Adopted,
                consoleAttached,
                onlinePlayers = inst.OnlinePlayers,
                maxPlayers = inst.MaxPlayers,
                cpuPercent = Math.Round(inst.CpuPercent, 1),
                ramUsedBytes = inst.RamUsedBytes,
                createdUtc = inst.CreatedUtc,
                runningSinceUtc = inst.RunningSinceUtc,
                lastStartedUtc = inst.LastStartedUtc,
                lastStoppedUtc = inst.LastStoppedUtc,
                lastExitCode = inst.LastExitCode,
                lastStartupSeconds = inst.LastStartupSeconds,
                lastError = inst.LastError,
                lastErrorAt,
                lastErrorApprox = inst.LastError != null && inst.LastErrorUtc == null,
                lastCrashUtc = lastCrash?.AtUtc,
                crashes7d = _activity.Count(inst.Id, ActivityKinds.Crashed, now.AddDays(-7)),
                supportedPlayerActions = provider.SupportedPlayerActions,
                backups = new
                {
                    count = backups.Count,
                    bytes = backups.Sum(b => b.SizeBytes),
                    lastUtc = lastBackupUtc,
                    lastKind = backups.FirstOrDefault()?.Kind,
                    everyHours = inst.BackupEveryHours,
                    keep = inst.BackupKeep,
                    nextDueUtc = NextBackupDueUtc(inst, lastBackupUtc, running, now),
                    job = job?.View(),
                },
                storage = storage == null ? null : new
                {
                    serverBytes = storage.ServerBytes,
                    files = storage.ServerFiles,
                    backupBytes = storage.BackupBytes,
                    measuredUtc = storage.MeasuredUtc,
                },
                network = new
                {
                    joinAddress = inst.Public ? $"klive.dev:{inst.Port}" : null,
                    lanAddress = string.IsNullOrEmpty(table?.LocalIp) ? null : $"{table!.LocalIp}:{inst.Port}",
                    ports = provider.GetNetworkPorts(inst.Port).Select(binding => new
                    {
                        port = binding.Port,
                        protocol = binding.Protocol,
                        purpose = binding.Purpose,
                        state = inst.Public ? PortState(table, binding) : "local",
                    }),
                },
                players = new
                {
                    online = inst.OnlinePlayers.Count,
                    max = inst.MaxPlayers,
                    known = book.Count,
                    lastSeenUtc = book.LastSeenUtc,
                    peak24h = VitalsOf(inst.Id).PeakPlayers(now),
                },
                spark = running ? VitalsOf(inst.Id).Snapshot("1h", now, 40) : null,
                lastActivity = lastActivity == null ? null : ActivityView(lastActivity),
            };
        }

        private object HostView()
        {
            double? cpu = null, ramTotalMb = null, ramUsedMb = null;
            try
            {
                var monitor = GetServiceMonitor();
                cpu = monitor.CPUUsagePercentage;
                ramTotalMb = Math.Round(monitor.TotalSystemRAM.MegaBytes);
                ramUsedMb = Math.Round(monitor.MemoryUsage.MegaBytes);
            }
            catch { }

            object? drive = null;
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveGamesInstancesDirectory))) ?? "";
                var d = new DriveInfo(root);
                drive = new { name = d.Name, totalBytes = d.TotalSize, freeBytes = d.AvailableFreeSpace };
            }
            catch { }

            var running = _instances.Values.Where(i => IsRunning(i.Id)).ToList();
            return new
            {
                cores = Environment.ProcessorCount,
                cpuPercent = cpu,
                ramTotalMb,
                ramUsedMb,
                gamesRamUsedMb = Math.Round(running.Sum(i => i.RamUsedBytes) / 1048576.0),
                gamesRamReservedMb = running.Where(i => _providers.Get(i.GameType).UsesMemoryLimit).Sum(i => i.RamMb),
                drive,
            };
        }

        public static object ActivityView(ServerActivityEntry e) => new
        {
            atUtc = e.AtUtc,
            serverId = e.ServerId,
            kind = e.Kind,
            text = e.Text,
            actor = e.Actor,
            player = e.Player,
            exitCode = e.ExitCode,
            seconds = e.Seconds,
            bytes = e.Bytes,
        };

        internal static string FormatBytes(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.#} MB";
            if (bytes >= 1L << 10) return $"{bytes / 1024.0:0} KB";
            return $"{bytes} B";
        }

        internal static string FormatSeconds(double seconds)
        {
            if (seconds < 60) return $"{seconds:0.#} s";
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min {t.Seconds} s";
        }
    }
}
