using System.IO.Compression;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.KliveGames.Runtime
{
    /// <summary>
    /// Creates/lists/restores zip backups of an instance's server directory. Backups live outside the
    /// server folder (under KliveGames/Backups/{instanceId}) so they're never recursively included.
    /// Backup creation reads files with shared access so it can run while a server is up; restore must
    /// be performed while the server is stopped (enforced by the orchestrator).
    /// </summary>
    public sealed class BackupManager
    {
        public sealed class BackupInfo
        {
            public string Id { get; set; } = "";   // the zip filename
            public long SizeBytes { get; set; }
            public DateTime CreatedUtc { get; set; }
        }

        private static string BackupDirFor(string instanceId)
        {
            string dir = OmniPaths.GetPath(Path.Combine(OmniPaths.GlobalPaths.KliveGamesBackupsDirectory, instanceId));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public BackupInfo Create(string instanceId, string serverDir)
        {
            string backupDir = BackupDirFor(instanceId);
            string id = $"backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
            string destZip = Path.Combine(backupDir, id);

            using (var zip = ZipFile.Open(destZip, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(serverDir, "*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(file);
                    if (name.Equals("session.lock", StringComparison.OrdinalIgnoreCase)) continue;

                    string rel = Path.GetRelativePath(serverDir, file).Replace('\\', '/');
                    try
                    {
                        using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                        using var dst = entry.Open();
                        src.CopyTo(dst);
                    }
                    catch
                    {
                        // Skip files that are momentarily locked/unreadable — best-effort hot backup.
                    }
                }
            }

            var fi = new FileInfo(destZip);
            return new BackupInfo { Id = id, SizeBytes = fi.Length, CreatedUtc = fi.CreationTimeUtc };
        }

        public IReadOnlyList<BackupInfo> List(string instanceId)
        {
            string backupDir = BackupDirFor(instanceId);
            return Directory.EnumerateFiles(backupDir, "*.zip")
                .Select(f => new FileInfo(f))
                .OrderByDescending(fi => fi.CreationTimeUtc)
                .Select(fi => new BackupInfo { Id = fi.Name, SizeBytes = fi.Length, CreatedUtc = fi.CreationTimeUtc })
                .ToList();
        }

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
    }
}
