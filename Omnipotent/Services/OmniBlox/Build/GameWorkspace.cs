using System.IO.Compression;
using System.Text;

namespace Omnipotent.Services.OmniBlox.Build
{
    public sealed record WorkspaceFile(string Path, long Bytes, DateTime ModifiedUtc);

    /// <summary>
    /// One game's source tree on disk: game.json (settings), scene.json (the world) and src/ (Luau, laid out
    /// the way Rojo does it: <c>.server.luau</c> → Script, <c>.client.luau</c> → LocalScript, other <c>.luau</c> →
    /// ModuleScript, <c>init*.luau</c> makes its folder a script). Every successful build snapshots the tree, so
    /// any change can be reviewed and rolled back. The agent can only touch files inside it.
    /// </summary>
    public sealed class GameWorkspace
    {
        public const long MaxFileBytes = 256 * 1024;
        public const int MaxFiles = 400;
        private static readonly string[] AllowedExtensions = { ".luau", ".lua", ".json", ".md", ".txt" };

        public GameWorkspace(string root, string snapshotsDirectory)
        {
            Root = Path.GetFullPath(root);
            SnapshotsDirectory = Path.GetFullPath(snapshotsDirectory);
        }

        public string Root { get; }
        public string SnapshotsDirectory { get; }
        public string ManifestPath => Path.Combine(Root, "game.json");
        public string ScenePath => Path.Combine(Root, "scene.json");
        public string SourceRoot => Path.Combine(Root, "src");

        public bool Exists => File.Exists(ManifestPath);

        public void EnsureCreated(GameManifest manifest)
        {
            Directory.CreateDirectory(Root);
            foreach (string sub in new[] { "src/server", "src/client", "src/shared", "src/tests" })
                Directory.CreateDirectory(Path.Combine(Root, sub));
            if (!File.Exists(ManifestPath)) manifest.Save(ManifestPath);
            if (!File.Exists(ScenePath)) new SceneDocument().Save(ScenePath);
        }

        public GameManifest LoadManifest() => GameManifest.Load(ManifestPath);
        public SceneDocument LoadScene() => SceneDocument.Load(ScenePath);
        public void SaveScene(SceneDocument scene) => scene.Save(ScenePath);

        /// <summary>Maps a workspace-relative path to an absolute one, refusing anything outside the tree.</summary>
        public string ResolveSafe(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("A file path is required.");
            string rel = relativePath.Replace('\\', '/').Trim().TrimStart('/');
            if (rel.Split('/').Any(seg => seg is ".." or "." || seg.Length == 0))
                throw new ArgumentException($"'{relativePath}' is not a valid workspace path.");
            string full = Path.GetFullPath(Path.Combine(Root, rel));
            if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"'{relativePath}' is outside the game workspace.");
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
                throw new ArgumentException($"Only {string.Join(", ", AllowedExtensions)} files can live in the workspace.");
            return full;
        }

        public IReadOnlyList<WorkspaceFile> ListFiles()
        {
            if (!Directory.Exists(Root)) return Array.Empty<WorkspaceFile>();
            return Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                .Where(f => AllowedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => new FileInfo(f))
                .Select(fi => new WorkspaceFile(Relative(fi.FullName), fi.Length, fi.LastWriteTimeUtc))
                .OrderBy(f => f.Path, StringComparer.Ordinal)
                .ToList();
        }

        public string? ReadFile(string relativePath)
        {
            string full = ResolveSafe(relativePath);
            return File.Exists(full) ? File.ReadAllText(full) : null;
        }

        public void WriteFile(string relativePath, string content)
        {
            string full = ResolveSafe(relativePath);
            if (Encoding.UTF8.GetByteCount(content) > MaxFileBytes)
                throw new ArgumentException($"Files are limited to {MaxFileBytes / 1024} KB; split the module up.");
            if (!File.Exists(full) && ListFiles().Count >= MaxFiles)
                throw new InvalidOperationException($"The workspace already holds {MaxFiles} files.");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            Core.OmniBloxStore.WriteAtomic(full, content);
        }

        public bool DeleteFile(string relativePath)
        {
            string full = ResolveSafe(relativePath);
            if (full.Equals(ManifestPath, StringComparison.OrdinalIgnoreCase) || full.Equals(ScenePath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("game.json and scene.json cannot be deleted.");
            if (!File.Exists(full)) return false;
            File.Delete(full);
            return true;
        }

        public string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

        // ─────────────── Snapshots ───────────────

        public string SnapshotPath(int revision) => Path.Combine(SnapshotsDirectory, $"r{revision:D4}.zip");

        public string Snapshot(int revision)
        {
            Directory.CreateDirectory(SnapshotsDirectory);
            string path = SnapshotPath(revision);
            string tmp = path + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
                foreach (var file in ListFiles())
                    zip.CreateEntryFromFile(Path.Combine(Root, file.Path), file.Path, CompressionLevel.Optimal);
            File.Move(tmp, path, overwrite: true);
            return path;
        }

        public IReadOnlyList<int> Revisions()
        {
            if (!Directory.Exists(SnapshotsDirectory)) return Array.Empty<int>();
            return Directory.EnumerateFiles(SnapshotsDirectory, "r*.zip")
                .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f)[1..], out int r) ? r : -1)
                .Where(r => r >= 0).OrderBy(r => r).ToList();
        }

        /// <summary>Replaces the working tree with a snapshot (the current tree is snapshotted first by the caller if wanted).</summary>
        public void Restore(int revision)
        {
            string path = SnapshotPath(revision);
            if (!File.Exists(path)) throw new FileNotFoundException($"No snapshot for revision {revision}.");
            foreach (var file in ListFiles()) File.Delete(Path.Combine(Root, file.Path));
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries)
            {
                string target = ResolveSafe(entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        /// <summary>
        /// Writes a snapshot's files into another directory and returns a workspace there: a copy of an older
        /// revision to compile from, leaving the working tree alone.
        /// </summary>
        public GameWorkspace Checkout(int revision, string directory)
        {
            string path = SnapshotPath(revision);
            if (!File.Exists(path)) throw new FileNotFoundException($"No snapshot for revision {revision}.");
            var copy = new GameWorkspace(directory, SnapshotsDirectory);
            Directory.CreateDirectory(copy.Root);
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries)
            {
                string target = copy.ResolveSafe(entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
            return copy;
        }

        /// <summary>Which files differ between a snapshot and the working tree (added / changed / removed).</summary>
        public (List<string> Added, List<string> Changed, List<string> Removed) DiffAgainst(int revision)
        {
            var added = new List<string>();
            var changed = new List<string>();
            var removed = new List<string>();
            string path = SnapshotPath(revision);
            var current = ListFiles().ToDictionary(f => f.Path, f => File.ReadAllText(Path.Combine(Root, f.Path)), StringComparer.Ordinal);
            if (!File.Exists(path)) return (current.Keys.ToList(), changed, removed);
            using var zip = ZipFile.OpenRead(path);
            var old = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                old[entry.FullName] = reader.ReadToEnd();
            }
            foreach (var (p, text) in current)
            {
                if (!old.TryGetValue(p, out var before)) added.Add(p);
                else if (before != text) changed.Add(p);
            }
            foreach (var p in old.Keys) if (!current.ContainsKey(p)) removed.Add(p);
            return (added, changed, removed);
        }

        // ─────────────── Script layout ───────────────

        public sealed record ScriptFile(string RelativePath, string ClassName, string Container, string[] FolderPath, string Name, string Source);

        /// <summary>Every Luau file under src/, with the Roblox class and container it compiles to.</summary>
        public List<ScriptFile> Scripts()
        {
            var list = new List<ScriptFile>();
            if (!Directory.Exists(SourceRoot)) return list;
            foreach (string full in Directory.EnumerateFiles(SourceRoot, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(full).ToLowerInvariant();
                if (ext is not (".luau" or ".lua")) continue;
                string rel = Path.GetRelativePath(SourceRoot, full).Replace('\\', '/');
                var segments = rel.Split('/');
                if (segments.Length < 2) continue; // files directly in src/ have no container
                string container = segments[0].ToLowerInvariant();
                string file = segments[^1];
                string stem = file[..^ext.Length];
                string className = "ModuleScript";
                if (stem.EndsWith(".server", StringComparison.OrdinalIgnoreCase)) { className = "Script"; stem = stem[..^7]; }
                else if (stem.EndsWith(".client", StringComparison.OrdinalIgnoreCase)) { className = "LocalScript"; stem = stem[..^7]; }
                var folders = segments.Skip(1).Take(segments.Length - 2).ToArray();
                string name = stem;
                if (stem.Equals("init", StringComparison.OrdinalIgnoreCase))
                {
                    if (folders.Length == 0) continue; // init directly under a container is meaningless
                    name = folders[^1];
                    folders = folders[..^1];
                }
                list.Add(new ScriptFile("src/" + rel, className, container, folders, name, File.ReadAllText(full)));
            }
            return list.OrderBy(s => s.RelativePath, StringComparer.Ordinal).ToList();
        }
    }
}
