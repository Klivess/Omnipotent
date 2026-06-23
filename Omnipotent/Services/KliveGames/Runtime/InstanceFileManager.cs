namespace Omnipotent.Services.KliveGames.Runtime
{
    /// <summary>
    /// Path-traversal-safe file operations scoped to a single server instance directory. Every relative
    /// path supplied by the website is resolved and re-checked to ensure it stays inside the root.
    /// </summary>
    public sealed class InstanceFileManager
    {
        public sealed class FileEntry
        {
            public string Name { get; set; } = "";
            public string Path { get; set; } = "";
            public bool IsDirectory { get; set; }
            public long Size { get; set; }
            public DateTime ModifiedUtc { get; set; }
        }

        private readonly string _rootFull;

        public InstanceFileManager(string root)
        {
            _rootFull = System.IO.Path.GetFullPath(root);
        }

        /// <summary>Resolves a user-supplied relative path to an absolute path inside the root, or throws.</summary>
        public string ResolveSafe(string? rel)
        {
            rel ??= "";
            rel = rel.Replace('\\', '/').Trim();
            while (rel.StartsWith("/")) rel = rel.Substring(1);

            if (System.IO.Path.IsPathRooted(rel))
                throw new UnauthorizedAccessException("Absolute paths are not allowed.");
            if (rel.Split('/').Any(seg => seg == ".."))
                throw new UnauthorizedAccessException("Path traversal is not allowed.");

            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(_rootFull, rel));
            if (!full.Equals(_rootFull, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(_rootFull + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Path escapes the server directory.");

            // Reject symlinks/junctions that could redirect outside the root.
            try
            {
                var info = new FileInfo(full);
                if (info.Exists && info.LinkTarget != null)
                    throw new UnauthorizedAccessException("Symlinked targets are not allowed.");
                var dinfo = new DirectoryInfo(full);
                if (dinfo.Exists && dinfo.LinkTarget != null)
                    throw new UnauthorizedAccessException("Symlinked directories are not allowed.");
            }
            catch (UnauthorizedAccessException) { throw; }
            catch { /* non-existent paths are fine for writes */ }

            return full;
        }

        public IReadOnlyList<FileEntry> List(string? rel)
        {
            string full = ResolveSafe(rel);
            if (!Directory.Exists(full))
                throw new DirectoryNotFoundException("Directory not found.");

            string baseRel = (rel ?? "").Replace('\\', '/').Trim('/');
            var entries = new List<FileEntry>();

            foreach (var dir in Directory.EnumerateDirectories(full))
            {
                var di = new DirectoryInfo(dir);
                entries.Add(new FileEntry
                {
                    Name = di.Name,
                    Path = string.IsNullOrEmpty(baseRel) ? di.Name : $"{baseRel}/{di.Name}",
                    IsDirectory = true,
                    ModifiedUtc = di.LastWriteTimeUtc,
                });
            }
            foreach (var file in Directory.EnumerateFiles(full))
            {
                var fi = new FileInfo(file);
                entries.Add(new FileEntry
                {
                    Name = fi.Name,
                    Path = string.IsNullOrEmpty(baseRel) ? fi.Name : $"{baseRel}/{fi.Name}",
                    IsDirectory = false,
                    Size = fi.Length,
                    ModifiedUtc = fi.LastWriteTimeUtc,
                });
            }

            return entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public string ResolveExistingFile(string? rel)
        {
            string full = ResolveSafe(rel);
            if (!File.Exists(full)) throw new FileNotFoundException("File not found.");
            return full;
        }

        public string ReadText(string? rel)
        {
            string full = ResolveExistingFile(rel);
            return File.ReadAllText(full);
        }

        public void WriteText(string? rel, string content)
        {
            string full = ResolveSafe(rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public async Task SaveUploadAsync(string? relDir, string fileName, byte[] data)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(".."))
                throw new UnauthorizedAccessException("Invalid file name.");
            string targetRel = string.IsNullOrWhiteSpace(relDir) ? fileName : $"{relDir.Replace('\\', '/').Trim('/')}/{fileName}";
            string full = ResolveSafe(targetRel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, data);
        }

        public void Delete(string? rel)
        {
            string full = ResolveSafe(rel);
            if (full.Equals(_rootFull, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Cannot delete the server root.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            else if (File.Exists(full)) File.Delete(full);
        }
    }
}
