using System.IO.Compression;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.KliveGames.Runtime
{
    /// <summary>
    /// Creates/lists/restores zip backups of an instance's server directory. Backups live outside the
    /// server folder (under KliveGames/Backups/{instanceId}) so they're never recursively included.
    /// Backup creation reads files with shared access so it can run while a server is up; restore must
    /// be performed while the server is stopped (enforced by the orchestrator).
    /// The file name says who made it: <c>backup-*.zip</c> by hand, <c>auto-*.zip</c> by the schedule. Retention
    /// (<see cref="Prune"/>) only ever removes <c>auto-*</c> files, so a backup taken by hand is never deleted for you.
    /// </summary>
    public sealed class BackupManager
    {
        public const string ManualPrefix = "backup-";
        public const string ScheduledPrefix = "auto-";
        /// <summary>Taken automatically just before a restore overwrites the server, so a restore can be undone.</summary>
        public const string SafetyPrefix = "before-restore-";

        public sealed class BackupInfo
        {
            public string Id { get; set; } = "";   // the zip filename
            public long SizeBytes { get; set; }
            public DateTime CreatedUtc { get; set; }
            /// <summary>"manual", "scheduled" or "safety" (taken before a restore), from the file name.</summary>
            public string Kind { get; set; } = "manual";
        }

        /// <summary>Progress of one backup as it is written: files and bytes done out of the totals counted first.</summary>
        public sealed class BackupProgress
        {
            public int FilesTotal { get; set; }
            public long BytesTotal { get; set; }
            public int FilesDone;
            public long BytesDone;
            public int FilesSkipped;
        }

        private readonly string? _root;

        /// <param name="root">Where backups live; null = KliveGames/Backups under the saved-data folder.</param>
        public BackupManager(string? root = null) { _root = root; }

        private string BackupDirFor(string instanceId)
        {
            string dir = _root != null
                ? Path.Combine(_root, instanceId)
                : OmniPaths.GetPath(Path.Combine(OmniPaths.GlobalPaths.KliveGamesBackupsDirectory, instanceId));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public string BackupDirectory(string instanceId) => BackupDirFor(instanceId);

        public BackupInfo Create(string instanceId, string serverDir)
            => Create(instanceId, serverDir, ManualPrefix, null, CancellationToken.None);

        /// <summary>
        /// Zips the server folder. Uses the fastest compression: world region files barely shrink further, and
        /// the box running this is a 4-core machine that is often busy — a backup should not take a core for minutes
        /// to save a few percent. A cancelled or failed backup leaves no partial zip behind.
        /// </summary>
        public BackupInfo Create(string instanceId, string serverDir, string prefix, BackupProgress? progress, CancellationToken ct)
        {
            string backupDir = BackupDirFor(instanceId);
            string id = $"{prefix}{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
            string destZip = Path.Combine(backupDir, id);
            if (File.Exists(destZip)) { id = $"{prefix}{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.zip"; destZip = Path.Combine(backupDir, id); }
            string partial = destZip + ".partial";

            var files = Directory.Exists(serverDir)
                ? Directory.EnumerateFiles(serverDir, "*", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).Equals("session.lock", StringComparison.OrdinalIgnoreCase))
                    .ToList()
                : new List<string>();
            if (progress != null)
            {
                progress.FilesTotal = files.Count;
                long total = 0;
                foreach (var f in files) { try { total += new FileInfo(f).Length; } catch { } }
                progress.BytesTotal = total;
            }

            try
            {
                using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
                {
                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        string rel = Path.GetRelativePath(serverDir, file).Replace('\\', '/');
                        try
                        {
                            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            var entry = zip.CreateEntry(rel, CompressionLevel.Fastest);
                            using var dst = entry.Open();
                            src.CopyTo(dst);
                            if (progress != null)
                            {
                                Interlocked.Increment(ref progress.FilesDone);
                                Interlocked.Add(ref progress.BytesDone, src.Length);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch
                        {
                            // Skip files that are momentarily locked/unreadable — best-effort hot backup.
                            if (progress != null) Interlocked.Increment(ref progress.FilesSkipped);
                        }
                    }
                }
                File.Move(partial, destZip, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                throw;
            }

            var fi = new FileInfo(destZip);
            return new BackupInfo { Id = id, SizeBytes = fi.Length, CreatedUtc = fi.CreationTimeUtc, Kind = KindOf(id) };
        }

        public IReadOnlyList<BackupInfo> List(string instanceId)
        {
            string backupDir = BackupDirFor(instanceId);
            return Directory.EnumerateFiles(backupDir, "*.zip")
                .Select(f => new FileInfo(f))
                .OrderByDescending(fi => fi.CreationTimeUtc)
                .Select(fi => new BackupInfo { Id = fi.Name, SizeBytes = fi.Length, CreatedUtc = fi.CreationTimeUtc, Kind = KindOf(fi.Name) })
                .ToList();
        }

        public static string KindOf(string backupId) =>
            backupId.StartsWith(ScheduledPrefix, StringComparison.OrdinalIgnoreCase) ? "scheduled"
            : backupId.StartsWith(SafetyPrefix, StringComparison.OrdinalIgnoreCase) ? "safety"
            : "manual";

        public string ResolveBackupPath(string instanceId, string backupId)
        {
            if (string.IsNullOrWhiteSpace(backupId) || backupId.Contains('/') || backupId.Contains('\\') || backupId.Contains("..") || !backupId.EndsWith(".zip"))
                throw new UnauthorizedAccessException("Invalid backup id.");
            string path = Path.Combine(BackupDirFor(instanceId), backupId);
            if (!File.Exists(path)) throw new FileNotFoundException("Backup not found.");
            return path;
        }

        /// <summary>Restores a backup over the server directory. The server MUST be stopped.</summary>
        public void Restore(string instanceId, string serverDir, string backupId)
        {
            string zipPath = ResolveBackupPath(instanceId, backupId);

            // Clear the existing server directory contents.
            if (Directory.Exists(serverDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(serverDir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
                foreach (var file in Directory.EnumerateFiles(serverDir))
                    try { File.Delete(file); } catch { }
            }
            Directory.CreateDirectory(serverDir);

            ZipFile.ExtractToDirectory(zipPath, serverDir, overwriteFiles: true);
        }

        public void Delete(string instanceId, string backupId)
        {
            string path = ResolveBackupPath(instanceId, backupId);
            File.Delete(path);
        }

        /// <summary>Keeps the newest <paramref name="keep"/> scheduled backups and deletes older scheduled ones.
        /// Manual backups are never touched. <paramref name="keep"/> ≤ 0 keeps everything.</summary>
        public IReadOnlyList<BackupInfo> Prune(string instanceId, int keep)
        {
            if (keep <= 0) return Array.Empty<BackupInfo>();
            var excess = List(instanceId).Where(b => b.Kind == "scheduled").Skip(keep).ToList();
            var removed = new List<BackupInfo>();
            foreach (var b in excess)
            {
                try { Delete(instanceId, b.Id); removed.Add(b); } catch { }
            }
            return removed;
        }
    }
}
