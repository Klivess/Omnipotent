using Docker.DotNet;
using Docker.DotNet.Models;
using Omnipotent.Data_Handling;
using Omnipotent.Services.Demiurge.Containers;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// Moves Demiurge's data from SavedData/Projects — its folder before Projects became Demiurge
    /// (November 2026) — to SavedData/Demiurge, once, at startup, before any store opens.
    ///
    /// Desktop containers bind-mount Steward volumes by host path, so the folder only moves once
    /// nothing holds it: every Demiurge container is removed first (when Docker answers), and a
    /// desktop Docker starts later still pointing at the old folder is replaced on reconcile instead
    /// of adopted (<see cref="MountsLegacyFolder"/>). Desktops are recreated on first use from the
    /// moved volumes: the desktop session restarts once; files, browser profiles and installed
    /// runtimes live in the volume and carry over.
    ///
    /// When the folder cannot be moved (something holds a file in it open) this run keeps working
    /// from the old folder and the next start tries again. The files renamed with the folder are
    /// renamed in whichever folder is in use, so the old folder stays fully usable meanwhile.
    /// Nothing is deleted except an empty skeleton of the new folder.
    /// </summary>
    public static class DemiurgeStorageMigration
    {
        public sealed record Outcome(string Root, bool Moved, bool UsingLegacyFolder, int ContainersRemoved, string? Problem);

        /// <summary>Files renamed along with the folder: legacy relative path → current.</summary>
        internal static readonly (string Legacy, string Current)[] RenamedFiles =
        {
            ("projects.json", "stewards.json"),
            ("FileMetadata/project-files.db", "FileMetadata/steward-files.db"),
            ("FileMetadata/project-files.db-wal", "FileMetadata/steward-files.db-wal"),
            ("FileMetadata/project-files.db-shm", "FileMetadata/steward-files.db-shm"),
        };

        /// <summary>The folders OmniStartupManager created before Demiurge's paths became properties.</summary>
        internal static readonly string[] LayoutDirectories =
        {
            "EventLog", "Digests", "Vaults", "Stimulus", "Volumes", "FileMetadata", "FileStaging",
            "Observables", "Councils", "GrandPlans", "Memory",
        };

        private static readonly TimeSpan DockerBudget = TimeSpan.FromSeconds(90);

        /// <summary>True while this run works from the pre-rename folder because it could not be moved.</summary>
        public static bool UsingLegacyFolder { get; private set; }

        /// <summary>Runs the migration against the live SavedData folder and points
        /// <see cref="OmniPaths.GlobalPaths.DemiurgeDirectory"/> at the folder to use.</summary>
        public static async Task<Outcome> RunAsync(Action<string> log, CancellationToken ct = default)
        {
            string current = $"{OmniPaths.GlobalPaths.SavedDataDirectory}/Demiurge";
            string legacy = OmniPaths.GlobalPaths.DemiurgeLegacyDirectory;
            Outcome outcome;
            try
            {
                outcome = await RunAsync(OmniPaths.GetPath(legacy), OmniPaths.GetPath(current),
                    RemoveDemiurgeContainersAsync, log, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Housekeeping must never stop Demiurge: work from whichever folder holds the data.
                bool legacyHoldsData = HasFiles(OmniPaths.GetPath(legacy)) && !HasFiles(OmniPaths.GetPath(current));
                string root = OmniPaths.GetPath(legacyHoldsData ? legacy : current);
                log($"Demiurge: the data folder migration failed ({ex.Message}); working from {root}.");
                outcome = new Outcome(root, false, legacyHoldsData, 0, ex.Message);
            }
            OmniPaths.GlobalPaths.DemiurgeDirectory = outcome.UsingLegacyFolder ? legacy : current;
            UsingLegacyFolder = outcome.UsingLegacyFolder;
            return outcome;
        }

        /// <summary>The migration over explicit folders (the test seam). <paramref name="removeContainers"/>
        /// removes every container that could mount the old folder and returns how many it removed; it
        /// throws when Docker cannot be asked.</summary>
        internal static async Task<Outcome> RunAsync(string legacyRoot, string currentRoot,
            Func<CancellationToken, Task<int>> removeContainers, Action<string> log, CancellationToken ct = default,
            TimeSpan? retryStep = null)
        {
            bool currentHasData = HasFiles(currentRoot);
            bool legacyHasData = HasFiles(legacyRoot);

            if (currentHasData || !legacyHasData)
            {
                if (currentHasData && legacyHasData)
                    log($"Demiurge: both {currentRoot} and the pre-rename {legacyRoot} hold files. Using {currentRoot}; the old folder was left untouched.");
                RenameFilesIn(currentRoot, log);
                EnsureLayout(currentRoot);
                return new Outcome(currentRoot, false, false, 0, null);
            }

            // The old folder holds the data. Make it usable under today's file names whatever happens next.
            RenameFilesIn(legacyRoot, log);

            int removed = 0;
            string? dockerProblem = null;
            try { removed = await removeContainers(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // No answer means no desktop is running now; one Docker starts later is replaced on reconcile.
                dockerProblem = ex.Message;
            }

            Exception? moveFailure = null;
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(currentRoot)) DeleteEmptySkeleton(currentRoot);
                    Directory.Move(legacyRoot, currentRoot);
                    moveFailure = null;
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    moveFailure = ex;
                    if (attempt < 5) await Task.Delay((retryStep ?? TimeSpan.FromSeconds(1)) * attempt, ct);
                }
            }

            if (moveFailure != null)
            {
                EnsureLayout(legacyRoot);
                string problem = $"the folder could not be moved yet ({moveFailure.Message}); working from it until the next start";
                log($"Demiurge: {legacyRoot} holds the Steward data — {problem}.");
                return new Outcome(legacyRoot, false, true, removed, problem);
            }

            // Every desktop the registry listed mounted the old path and has been removed (or will be
            // replaced on reconcile), so none of its records still describe a usable desktop.
            ClearContainerRegistry(currentRoot, log);
            EnsureLayout(currentRoot);
            log($"Demiurge: moved the Steward data from {legacyRoot} to {currentRoot}" +
                (removed > 0 ? $"; removed {removed} desktop container(s), recreated on the moved volumes on first use." : ".") +
                (dockerProblem != null ? $" Docker did not answer ({dockerProblem}); any desktop it starts later on the old path is replaced on reconcile." : ""));
            return new Outcome(currentRoot, true, false, removed, null);
        }

        /// <summary>
        /// True when a desktop's mounts point into the pre-rename SavedData/Projects/Volumes folder.
        /// Docker reports a Windows bind source either as a drive path or under /run/desktop/mnt/host,
        /// so this matches the folder's distinctive tail rather than a full path.
        /// </summary>
        public static bool MountsLegacyFolder(IEnumerable<string?>? mountSources)
        {
            if (mountSources == null) return false;
            foreach (string? source in mountSources)
            {
                if (string.IsNullOrWhiteSpace(source)) continue;
                string normalized = source.Replace('\\', '/').ToLowerInvariant();
                if (normalized.Contains("/saveddata/projects/volumes/", StringComparison.Ordinal)
                    || normalized.EndsWith("/saveddata/projects/volumes", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool HasFiles(string root)
        {
            try { return Directory.Exists(root) && Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        }

        internal static void RenameFilesIn(string root, Action<string> log)
        {
            if (!Directory.Exists(root)) return;
            foreach (var (legacyName, currentName) in RenamedFiles)
            {
                string from = Path.Combine(root, legacyName.Replace('/', Path.DirectorySeparatorChar));
                string to = Path.Combine(root, currentName.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(from)) continue;
                try
                {
                    // An empty file under the new name is a placeholder, not data.
                    if (File.Exists(to) && new FileInfo(to).Length == 0 && new FileInfo(from).Length > 0) File.Delete(to);
                    if (File.Exists(to))
                    {
                        log($"Demiurge: kept {to} and left the pre-rename {from} alongside it (both exist).");
                        continue;
                    }
                    File.Move(from, to);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log($"Demiurge: could not rename {from} to {Path.GetFileName(to)} ({ex.Message}).");
                }
            }
        }

        private static void EnsureLayout(string root)
        {
            Directory.CreateDirectory(root);
            foreach (string folder in LayoutDirectories) Directory.CreateDirectory(Path.Combine(root, folder));
        }

        /// <summary>Removes a folder tree that holds no files (only then); anything else throws.</summary>
        private static void DeleteEmptySkeleton(string root)
        {
            if (Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any())
                throw new IOException($"{root} is not empty");
            Directory.Delete(root, recursive: true);
        }

        private static void ClearContainerRegistry(string root, Action<string> log)
        {
            string path = Path.Combine(root, "containers.json");
            try { if (File.Exists(path)) File.WriteAllText(path, "[]"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"Demiurge: could not clear {path} ({ex.Message}); reconcile marks its desktops lost instead.");
            }
        }

        /// <summary>Removes every Demiurge desktop container (current and pre-rename labels).</summary>
        private static async Task<int> RemoveDemiurgeContainersAsync(CancellationToken ct)
        {
            using var docker = new DockerClientConfiguration(new Uri(StewardContainerConfig.ResolveDockerUri())).CreateClient();
            var owned = await ContainerOrchestrator.WithDeadlineAsync(
                token => ContainerOrchestrator.ListOwnedAsync(docker, token), DockerBudget, "listing desktop containers", ct);
            int removed = 0;
            foreach (var container in owned)
            {
                await ContainerOrchestrator.WithDeadlineAsync(async token =>
                {
                    await docker.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters { Force = true }, token);
                    return true;
                }, DockerBudget, "removing a desktop container", ct);
                removed++;
            }
            return removed;
        }
    }
}
