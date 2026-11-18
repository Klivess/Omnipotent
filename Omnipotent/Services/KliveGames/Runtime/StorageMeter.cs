using System.Collections.Concurrent;
using System.Diagnostics;

namespace Omnipotent.Services.KliveGames.Runtime
{
    public sealed class StorageReading
    {
        public long ServerBytes { get; set; }
        public int ServerFiles { get; set; }
        public long BackupBytes { get; set; }
        public int BackupCount { get; set; }
        public DateTime MeasuredUtc { get; set; }
        public long Ms { get; set; }
    }

    /// <summary>
    /// How much disk each server and its backups take. Walking a world folder is tens of thousands of files
    /// on a large server, so it never happens on a request: readings are taken in the background, one server
    /// at a time, every <see cref="RefreshEvery"/> or soon after something changed the folder
    /// (<see cref="Invalidate"/>), and the routes serve the last reading.
    /// </summary>
    public sealed class StorageMeter
    {
        public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(20);

        private readonly ConcurrentDictionary<string, StorageReading> _readings = new();
        private readonly ConcurrentDictionary<string, DateTime> _wanted = new();
        private readonly SemaphoreSlim _one = new(1, 1);

        public StorageReading? Get(string id) => _readings.TryGetValue(id, out var r) ? r : null;

        /// <summary>Asks for a fresh reading of this server soon (after a backup, restore, upload or delete).</summary>
        public void Invalidate(string id) => _wanted[id] = DateTime.UtcNow.AddSeconds(5);

        public void Forget(string id)
        {
            _readings.TryRemove(id, out _);
            _wanted.TryRemove(id, out _);
        }

        /// <summary>Called by the monitor loop: measures, one at a time, every server whose reading is
        /// missing, stale or invalidated. Never throws.</summary>
        public async Task RefreshDueAsync(IEnumerable<(string id, string serverDir, string backupDir)> servers, CancellationToken ct)
        {
            if (!await _one.WaitAsync(0, ct)) return; // a pass is already running
            try
            {
                var now = DateTime.UtcNow;
                foreach (var (id, serverDir, backupDir) in servers)
                {
                    if (ct.IsCancellationRequested) return;
                    bool missing = !_readings.TryGetValue(id, out var reading);
                    bool stale = reading != null && now - reading.MeasuredUtc > RefreshEvery;
                    bool asked = _wanted.TryGetValue(id, out var due) && due <= now;
                    if (!missing && !stale && !asked) continue;
                    _wanted.TryRemove(id, out _);
                    var fresh = await Task.Run(() => Measure(serverDir, backupDir), ct);
                    _readings[id] = fresh;
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { _one.Release(); }
        }

        internal static StorageReading Measure(string serverDir, string backupDir)
        {
            var sw = Stopwatch.StartNew();
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            long serverBytes = 0; int files = 0;
            try
            {
                if (Directory.Exists(serverDir))
                {
                    foreach (var fi in new DirectoryInfo(serverDir).EnumerateFiles("*", options))
                    {
                        try { serverBytes += fi.Length; files++; } catch { }
                    }
                }
            }
            catch { }

            long backupBytes = 0; int backups = 0;
            try
            {
                if (Directory.Exists(backupDir))
                {
                    foreach (var fi in new DirectoryInfo(backupDir).EnumerateFiles("*.zip", SearchOption.TopDirectoryOnly))
                    {
                        try { backupBytes += fi.Length; backups++; } catch { }
                    }
                }
            }
            catch { }

            return new StorageReading
            {
                ServerBytes = serverBytes,
                ServerFiles = files,
                BackupBytes = backupBytes,
                BackupCount = backups,
                MeasuredUtc = DateTime.UtcNow,
                Ms = sw.ElapsedMilliseconds,
            };
        }
    }
}
