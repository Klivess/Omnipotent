using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.KliveTechHub
{
    public sealed partial class KliveTechHub
    {
        internal const int FirmwareChunkSize = 2048;
        internal const long MaximumFirmwareBytes = 8L * 1024 * 1024;
        private const int MaximumProjectEntries = 512;
        private const long MaximumProjectBytes = 64L * 1024 * 1024;
        private const int MaximumCompilerOutputCharacters = 128 * 1024;
        private const int MaximumRememberedFirmwareJobs = 100;
        private static readonly TimeSpan FirmwareRequestTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FirmwareCompleteTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan FirmwareCompileTimeout = TimeSpan.FromMinutes(10);

        private readonly ConcurrentDictionary<string, FirmwareJob> firmwareJobs =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> activeFirmwareJobsByGadget =
            new(StringComparer.OrdinalIgnoreCase);
        private string firmwareInboxDirectory = string.Empty;
        private string firmwareBuildsDirectory = string.Empty;
        private string arduinoCliPath = "arduino-cli";
        private string defaultFirmwareFqbn = "esp32:esp32:esp32";
        private string defaultPartitionScheme = "min_spiffs";
        private string kliveTechLibraryPath = string.Empty;

        public sealed class FirmwareJob
        {
            public string jobID { get; init; } = Guid.NewGuid().ToString("N");
            public string kind { get; init; } = "Compile";
            public string project { get; init; } = string.Empty;
            public string gadgetID { get; init; } = string.Empty;
            public string gadgetName { get; init; } = string.Empty;
            public string fqbn { get; init; } = string.Empty;
            public string partitionScheme { get; init; } = string.Empty;
            public string state { get; internal set; } = "Queued";
            public int progressPercent { get; internal set; }
            public long bytesTransferred { get; internal set; }
            public long totalBytes { get; internal set; }
            public string firmwareSha256 { get; internal set; } = string.Empty;
            public string firmwareFileName { get; internal set; } = string.Empty;
            public string compilerOutput { get; internal set; } = string.Empty;
            public string error { get; internal set; } = string.Empty;
            public DateTime createdUtc { get; init; } = DateTime.UtcNow;
            public DateTime? startedUtc { get; internal set; }
            public DateTime? completedUtc { get; internal set; }

            [JsonIgnore]
            internal CancellationTokenSource Cancellation { get; set; } = new();
        }

        public sealed class FirmwareProject
        {
            public string name { get; init; } = string.Empty;
            public IReadOnlyList<string> sketches { get; init; } = Array.Empty<string>();
            public bool valid { get; init; }
            public string error { get; init; } = string.Empty;
        }

        private sealed record CompiledFirmware(string Path, long Size, string Sha256);

        private async Task InitializeFirmwareManagerAsync()
        {
            firmwareInboxDirectory = Path.GetFullPath(
                OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveTechFirmwareInboxDirectory));
            firmwareBuildsDirectory = Path.GetFullPath(
                OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveTechFirmwareBuildsDirectory));
            Directory.CreateDirectory(firmwareInboxDirectory);
            Directory.CreateDirectory(firmwareBuildsDirectory);

            arduinoCliPath = await GetStringOmniSetting(
                "KliveTechArduinoCliPath",
                defaultValue: "arduino-cli",
                askKlivesForFulfillment: false);
            defaultFirmwareFqbn = await GetStringOmniSetting(
                "KliveTechFirmwareFqbn",
                defaultValue: "esp32:esp32:esp32",
                askKlivesForFulfillment: false);
            defaultPartitionScheme = await GetStringOmniSetting(
                "KliveTechFirmwarePartitionScheme",
                defaultValue: "min_spiffs",
                askKlivesForFulfillment: false);
            kliveTechLibraryPath = await GetStringOmniSetting(
                "KliveTechLibraryPath",
                defaultValue: string.Empty,
                askKlivesForFulfillment: false);

            if (string.IsNullOrWhiteSpace(arduinoCliPath))
            {
                arduinoCliPath = "arduino-cli";
            }
            ValidateBoardValue(defaultFirmwareFqbn, "KliveTechFirmwareFqbn", allowColon: true);
            ValidateBoardValue(defaultPartitionScheme, "KliveTechFirmwarePartitionScheme", allowColon: false);
        }

        private void StopFirmwareJobs()
        {
            foreach (FirmwareJob job in firmwareJobs.Values)
            {
                try { job.Cancellation.Cancel(); } catch { }
            }
        }

        internal object GetFirmwareConfiguration()
        {
            EnsureFirmwareManagerStarted();
            return new
            {
                inboxDirectory = firmwareInboxDirectory,
                buildsDirectory = firmwareBuildsDirectory,
                defaultFqbn = defaultFirmwareFqbn,
                defaultPartitionScheme,
                chunkSize = FirmwareChunkSize,
                maximumFirmwareBytes = MaximumFirmwareBytes
            };
        }

        internal IReadOnlyList<FirmwareProject> GetFirmwareProjects()
        {
            EnsureFirmwareManagerStarted();
            List<FirmwareProject> projects = new();
            foreach (string directory in Directory.EnumerateDirectories(firmwareInboxDirectory)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string projectName = Path.GetFileName(directory);
                try
                {
                    ValidateFirmwareProject(projectName);
                    projects.Add(new FirmwareProject
                    {
                        name = projectName,
                        sketches = Directory.EnumerateFiles(directory, "*.ino", SearchOption.AllDirectories)
                            .Select(path => Path.GetRelativePath(directory, path))
                            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                        valid = true
                    });
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    projects.Add(new FirmwareProject
                    {
                        name = projectName,
                        valid = false,
                        error = ex.Message
                    });
                }
            }
            return projects;
        }

        internal IReadOnlyList<FirmwareJob> GetFirmwareJobs(string? jobId = null)
        {
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                return firmwareJobs.TryGetValue(jobId.Trim(), out FirmwareJob? job)
                    ? new[] { job }
                    : Array.Empty<FirmwareJob>();
            }

            return firmwareJobs.Values
                .OrderByDescending(job => job.createdUtc)
                .ToList();
        }

        internal FirmwareJob StartFirmwareCompile(
            string project,
            string? fqbn,
            string? partitionScheme)
        {
            string projectPath = ValidateFirmwareProject(project);
            (string selectedFqbn, string selectedPartition) = ResolveBoardSettings(fqbn, partitionScheme);
            FirmwareJob job = CreateFirmwareJob(
                "Compile",
                project,
                gadget: null,
                selectedFqbn,
                selectedPartition);
            _ = Task.Run(() => RunFirmwareJobAsync(job, projectPath, gadget: null), CancellationToken.None);
            return job;
        }

        internal FirmwareJob StartFirmwareUpdate(
            string gadgetId,
            string project,
            string? fqbn,
            string? partitionScheme)
        {
            KliveTechGadget gadget = GetKliveTechGadgetByID(gadgetId)
                ?? throw new KeyNotFoundException("Gadget not found.");
            if (!gadget.isOnline)
            {
                throw new IOException("The gadget is offline.");
            }

            string projectPath = ValidateFirmwareProject(project);
            (string selectedFqbn, string selectedPartition) = ResolveBoardSettings(fqbn, partitionScheme);
            FirmwareJob job = CreateFirmwareJob(
                "Update",
                project,
                gadget,
                selectedFqbn,
                selectedPartition);
            if (!activeFirmwareJobsByGadget.TryAdd(gadget.gadgetID, job.jobID))
            {
                firmwareJobs.TryRemove(job.jobID, out _);
                job.Cancellation.Dispose();
                throw new InvalidOperationException("This gadget already has an active firmware update.");
            }

            _ = Task.Run(() => RunFirmwareJobAsync(job, projectPath, gadget), CancellationToken.None);
            return job;
        }

        internal bool CancelFirmwareJob(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId) ||
                !firmwareJobs.TryGetValue(jobId.Trim(), out FirmwareJob? job) ||
                IsTerminalFirmwareState(job.state))
            {
                return false;
            }
            job.Cancellation.Cancel();
            return true;
        }

        private FirmwareJob CreateFirmwareJob(
            string kind,
            string project,
            KliveTechGadget? gadget,
            string fqbn,
            string partitionScheme)
        {
            EnsureFirmwareManagerStarted();
            PruneFirmwareJobs();
            FirmwareJob job = new()
            {
                kind = kind,
                project = project.Trim(),
                gadgetID = gadget?.gadgetID ?? string.Empty,
                gadgetName = gadget?.name ?? string.Empty,
                fqbn = fqbn,
                partitionScheme = partitionScheme,
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken.Token)
            };
            if (!firmwareJobs.TryAdd(job.jobID, job))
            {
                job.Cancellation.Dispose();
                throw new InvalidOperationException("Could not allocate a firmware job ID.");
            }
            return job;
        }

        private async Task RunFirmwareJobAsync(
            FirmwareJob job,
            string projectPath,
            KliveTechGadget? gadget)
        {
            bool updateBegan = false;
            bool updateCompleted = false;
            CancellationToken token = job.Cancellation.Token;
            job.startedUtc = DateTime.UtcNow;
            try
            {
                CompiledFirmware firmware = await CompileFirmwareAsync(job, projectPath, token);
                if (gadget == null)
                {
                    job.progressPercent = 100;
                    job.state = "Completed";
                    return;
                }

                token.ThrowIfCancellationRequested();
                if (!gadget.isOnline)
                {
                    throw new IOException("The gadget went offline before the firmware upload started.");
                }

                job.state = "Uploading";
                job.progressPercent = 5;
                await EnsureFirmwareResponseOkAsync(
                    gadget,
                    KliveTechActions.OperationNumber.BeginFirmwareUpdate,
                    JsonConvert.SerializeObject(new
                    {
                        Size = firmware.Size,
                        Sha256 = firmware.Sha256,
                        Name = Path.GetFileName(firmware.Path)
                    }),
                    FirmwareRequestTimeout,
                    token);
                updateBegan = true;

                byte[] buffer = new byte[FirmwareChunkSize];
                await using FileStream stream = new(
                    firmware.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    FirmwareChunkSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                long offset = 0;
                while (offset < firmware.Size)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("The compiled firmware ended unexpectedly.");
                    }

                    await EnsureFirmwareResponseOkAsync(
                        gadget,
                        KliveTechActions.OperationNumber.FirmwareUpdateChunk,
                        JsonConvert.SerializeObject(new
                        {
                            Offset = offset,
                            Data = Convert.ToBase64String(buffer, 0, read)
                        }),
                        FirmwareRequestTimeout,
                        token);
                    offset += read;
                    job.bytesTransferred = offset;
                    job.progressPercent = 5 + (int)((offset * 94L) / firmware.Size);
                }

                await EnsureFirmwareResponseOkAsync(
                    gadget,
                    KliveTechActions.OperationNumber.CompleteFirmwareUpdate,
                    "{}",
                    FirmwareCompleteTimeout,
                    token);
                updateCompleted = true;
                job.progressPercent = 100;
                job.state = "Completed";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                job.state = "Cancelled";
                job.error = "The firmware job was cancelled.";
            }
            catch (Exception ex)
            {
                job.state = "Failed";
                job.error = ex.Message;
                await ServiceLogError(ex, $"KliveTech firmware job {job.jobID} failed.");
            }
            finally
            {
                if (updateBegan && !updateCompleted && gadget != null)
                {
                    await TryAbortFirmwareUpdateAsync(gadget);
                }
                job.completedUtc = DateTime.UtcNow;
                if (gadget != null)
                {
                    activeFirmwareJobsByGadget.TryGetValue(gadget.gadgetID, out string? activeJobId);
                    if (string.Equals(activeJobId, job.jobID, StringComparison.OrdinalIgnoreCase))
                    {
                        activeFirmwareJobsByGadget.TryRemove(gadget.gadgetID, out _);
                    }
                }
                job.Cancellation.Dispose();
            }
        }

        private async Task<CompiledFirmware> CompileFirmwareAsync(
            FirmwareJob job,
            string projectPath,
            CancellationToken token)
        {
            job.state = "Compiling";
            job.progressPercent = 1;
            string buildDirectory = Path.Combine(firmwareBuildsDirectory, job.jobID);
            string compilerWorkDirectory = Path.Combine(buildDirectory, "work");
            string artifactDirectory = Path.Combine(buildDirectory, "output");
            Directory.CreateDirectory(compilerWorkDirectory);
            Directory.CreateDirectory(artifactDirectory);

            ProcessStartInfo startInfo = BuildArduinoCompileStartInfo(
                arduinoCliPath,
                projectPath,
                buildDirectory,
                job.fqbn,
                job.partitionScheme,
                kliveTechLibraryPath);
            using Process process = new() { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("arduino-cli did not start.");
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"Could not start '{arduinoCliPath}'. Install Arduino CLI or set " +
                    "KliveTechArduinoCliPath to its executable.",
                    ex);
            }

            StringBuilder compilerOutput = new();
            Task stdout = CaptureProcessOutputAsync(process.StandardOutput, compilerOutput, token);
            Task stderr = CaptureProcessOutputAsync(process.StandardError, compilerOutput, token);
            using CancellationTokenSource compileDeadline =
                CancellationTokenSource.CreateLinkedTokenSource(token);
            compileDeadline.CancelAfter(FirmwareCompileTimeout);
            try
            {
                await process.WaitForExitAsync(compileDeadline.Token);
                await Task.WhenAll(stdout, stderr);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                TryKillProcess(process);
                await IgnoreProcessOutputErrorsAsync(stdout, stderr);
                throw new TimeoutException(
                    $"arduino-cli did not finish within {FirmwareCompileTimeout.TotalMinutes:0} minutes.");
            }
            catch
            {
                TryKillProcess(process);
                await IgnoreProcessOutputErrorsAsync(stdout, stderr);
                throw;
            }
            finally
            {
                job.compilerOutput = compilerOutput.ToString();
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidDataException(
                    $"arduino-cli exited with code {process.ExitCode}. See compilerOutput on the job.");
            }

            string firmwarePath = SelectFirmwareBinary(artifactDirectory, Path.GetFileName(projectPath));
            FileInfo firmwareFile = new(firmwarePath);
            if (firmwareFile.Length <= 0 || firmwareFile.Length > MaximumFirmwareBytes)
            {
                throw new InvalidDataException(
                    $"Compiled firmware must be between 1 and {MaximumFirmwareBytes} bytes.");
            }

            string sha256;
            await using (FileStream firmwareStream = firmwareFile.OpenRead())
            {
                using SHA256 hasher = SHA256.Create();
                sha256 = Convert.ToHexString(
                    await hasher.ComputeHashAsync(firmwareStream, token)).ToLowerInvariant();
            }

            job.totalBytes = firmwareFile.Length;
            job.firmwareFileName = firmwareFile.Name;
            job.firmwareSha256 = sha256;
            job.progressPercent = 5;
            return new CompiledFirmware(firmwarePath, firmwareFile.Length, sha256);
        }

        private async Task EnsureFirmwareResponseOkAsync(
            KliveTechGadget gadget,
            KliveTechActions.OperationNumber operation,
            string data,
            TimeSpan timeout,
            CancellationToken token)
        {
            KliveTechGadgetResponse response = await SendData(
                gadget,
                operation,
                data,
                expectResponse: true,
                timeout,
                token);
            if (response.STATUS != (int)HttpStatusCode.OK)
            {
                string error = response.DATA?["Error"]?.ToString()
                    ?? $"Gadget returned status {response.STATUS}.";
                throw new InvalidDataException(error);
            }
        }

        private async Task TryAbortFirmwareUpdateAsync(KliveTechGadget gadget)
        {
            if (!gadget.isOnline)
            {
                return;
            }
            try
            {
                using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
                await SendData(
                    gadget,
                    KliveTechActions.OperationNumber.AbortFirmwareUpdate,
                    "{}",
                    expectResponse: true,
                    TimeSpan.FromSeconds(5),
                    deadline.Token);
            }
            catch
            {
                // The inactive OTA partition is harmless if the connection vanished mid-update.
            }
        }

        private string ValidateFirmwareProject(string project)
        {
            EnsureFirmwareManagerStarted();
            ValidateProjectName(project);
            string path = Path.GetFullPath(Path.Combine(firmwareInboxDirectory, project.Trim()));
            EnsurePathIsWithinDirectory(path, firmwareInboxDirectory);
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException("Firmware project folder was not found in the inbox.");
            }

            int entries = 0;
            long bytes = 0;
            int sketchCount = 0;
            Stack<string> pendingDirectories = new();
            pendingDirectories.Push(path);
            while (pendingDirectories.Count > 0)
            {
                string current = pendingDirectories.Pop();
                foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException(
                            "Firmware projects cannot contain symbolic links or junctions.");
                    }

                    entries++;
                    if (entries > MaximumProjectEntries)
                    {
                        throw new InvalidDataException(
                            $"Firmware projects are limited to {MaximumProjectEntries} files and folders.");
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pendingDirectories.Push(entry);
                    }
                    else
                    {
                        FileInfo file = new(entry);
                        bytes += file.Length;
                        if (bytes > MaximumProjectBytes)
                        {
                            throw new InvalidDataException(
                                $"Firmware project sources are limited to {MaximumProjectBytes} bytes.");
                        }
                        if (string.Equals(file.Extension, ".ino", StringComparison.OrdinalIgnoreCase))
                        {
                            sketchCount++;
                        }
                    }
                }
            }

            if (sketchCount == 0)
            {
                throw new InvalidDataException("Firmware project must contain at least one .ino file.");
            }
            string expectedSketch = Path.Combine(path, $"{Path.GetFileName(path)}.ino");
            if (!File.Exists(expectedSketch))
            {
                throw new InvalidDataException(
                    $"Firmware project must contain a top-level '{Path.GetFileName(path)}.ino' file.");
            }
            return path;
        }

        internal static void ValidateProjectName(string? project)
        {
            string value = project?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 100 ||
                !string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal) ||
                value is "." or ".." ||
                value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException(
                    "project must be the name of a direct child folder in the firmware inbox.");
            }
        }

        private (string Fqbn, string PartitionScheme) ResolveBoardSettings(
            string? fqbn,
            string? partitionScheme)
        {
            string selectedFqbn = string.IsNullOrWhiteSpace(fqbn) ? defaultFirmwareFqbn : fqbn.Trim();
            string selectedPartition = string.IsNullOrWhiteSpace(partitionScheme)
                ? defaultPartitionScheme
                : partitionScheme.Trim();
            ValidateBoardValue(selectedFqbn, "fqbn", allowColon: true);
            ValidateBoardValue(selectedPartition, "partitionScheme", allowColon: false);
            return (selectedFqbn, selectedPartition);
        }

        private static void ValidateBoardValue(string value, string name, bool allowColon)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 160 ||
                value.Any(character => !char.IsLetterOrDigit(character) &&
                    character is not '_' and not '-' and not '.' &&
                    (!allowColon || character != ':')))
            {
                throw new InvalidDataException($"{name} contains unsupported characters.");
            }
        }

        internal static ProcessStartInfo BuildArduinoCompileStartInfo(
            string executable,
            string projectDirectory,
            string buildDirectory,
            string fqbn,
            string partitionScheme,
            string? libraryPath)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("compile");
            startInfo.ArgumentList.Add("--fqbn");
            startInfo.ArgumentList.Add(fqbn);
            startInfo.ArgumentList.Add("--board-options");
            startInfo.ArgumentList.Add($"PartitionScheme={partitionScheme}");
            if (!string.IsNullOrWhiteSpace(libraryPath))
            {
                string resolvedLibraryPath = Path.GetFullPath(libraryPath.Trim());
                if (!Directory.Exists(resolvedLibraryPath))
                {
                    throw new DirectoryNotFoundException(
                        $"Configured KliveTechLibraryPath '{resolvedLibraryPath}' does not exist.");
                }
                startInfo.ArgumentList.Add("--library");
                startInfo.ArgumentList.Add(resolvedLibraryPath);
            }
            startInfo.ArgumentList.Add("--build-path");
            startInfo.ArgumentList.Add(Path.Combine(buildDirectory, "work"));
            startInfo.ArgumentList.Add("--output-dir");
            startInfo.ArgumentList.Add(Path.Combine(buildDirectory, "output"));
            startInfo.ArgumentList.Add(projectDirectory);
            return startInfo;
        }

        internal static string SelectFirmwareBinary(string buildDirectory, string sketchName)
        {
            string expected = Path.Combine(buildDirectory, $"{sketchName}.ino.bin");
            if (File.Exists(expected))
            {
                return expected;
            }

            List<string> candidates = Directory.EnumerateFiles(
                    buildDirectory,
                    "*.bin",
                    SearchOption.TopDirectoryOnly)
                .Where(path =>
                {
                    string name = Path.GetFileName(path);
                    return !name.EndsWith(".bootloader.bin", StringComparison.OrdinalIgnoreCase) &&
                           !name.EndsWith(".partitions.bin", StringComparison.OrdinalIgnoreCase) &&
                           !name.EndsWith(".merged.bin", StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(name, "boot_app0.bin", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
            return candidates.Count switch
            {
                1 => candidates[0],
                0 => throw new FileNotFoundException(
                    "arduino-cli completed but did not produce an application .bin file."),
                _ => throw new InvalidDataException(
                    "arduino-cli produced multiple application .bin files; the target is ambiguous.")
            };
        }

        private static async Task CaptureProcessOutputAsync(
            StreamReader reader,
            StringBuilder output,
            CancellationToken token)
        {
            while (await reader.ReadLineAsync(token) is string line)
            {
                lock (output)
                {
                    if (output.Length < MaximumCompilerOutputCharacters)
                    {
                        int remaining = MaximumCompilerOutputCharacters - output.Length;
                        output.AppendLine(line.Length <= remaining ? line : line[..remaining]);
                    }
                }
            }
        }

        private static void TryKillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The process may have exited between the state check and Kill.
            }
        }

        private static async Task IgnoreProcessOutputErrorsAsync(params Task[] outputTasks)
        {
            try
            {
                await Task.WhenAll(outputTasks);
            }
            catch
            {
                // Cancellation and a killed process can close redirected streams abruptly.
            }
        }

        private void EnsureFirmwareManagerStarted()
        {
            if (string.IsNullOrWhiteSpace(firmwareInboxDirectory) ||
                string.IsNullOrWhiteSpace(firmwareBuildsDirectory))
            {
                throw new InvalidOperationException("The KliveTech firmware manager has not started.");
            }
        }

        private void PruneFirmwareJobs()
        {
            FirmwareJob[] removable = firmwareJobs.Values
                .Where(job => IsTerminalFirmwareState(job.state))
                .OrderByDescending(job => job.createdUtc)
                .Skip(MaximumRememberedFirmwareJobs - 1)
                .ToArray();
            foreach (FirmwareJob job in removable)
            {
                firmwareJobs.TryRemove(job.jobID, out _);
            }
        }

        private static bool IsTerminalFirmwareState(string state)
        {
            return state is "Completed" or "Failed" or "Cancelled";
        }
    }
}
