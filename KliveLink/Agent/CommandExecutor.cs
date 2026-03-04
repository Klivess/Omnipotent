using System.Diagnostics;
using KliveLink.Protocol;

namespace KliveLink.Agent
{
    /// <summary>
    /// Executes commands received from the Omnipotent server.
    /// All operations are gated behind ConsentManager — if consent is revoked, nothing runs.
    /// </summary>
    public class CommandExecutor
    {

        public CommandExecutor()
        {
        }


        public SystemInfoPayload GetSystemInfo()
        {
            return new SystemInfoPayload
            {
                MachineName = Environment.MachineName,
                OSVersion = Environment.OSVersion.ToString(),
                UserName = Environment.UserName,
                ProcessorCount = Environment.ProcessorCount,
                TotalMemoryMB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024),
                AgentStartTime = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                AgentVersion = "1.0.0"
            };
        }

        public RunProcessResultPayload RunProcess(RunProcessPayload request)
        {
            var result = new RunProcessResultPayload();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = request.FileName,
                    Arguments = request.Arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = request.WaitForExit,
                    RedirectStandardError = request.WaitForExit,
                    CreateNoWindow = false
                };

                var process = Process.Start(psi);
                if (process == null)
                {
                    result.StandardError = "Failed to start process.";
                    result.ExitCode = -1;
                    return result;
                }

                result.ProcessId = process.Id;

                if (request.WaitForExit)
                {
                    bool exited = process.WaitForExit(request.TimeoutSeconds * 1000);
                    if (!exited)
                    {
                        result.TimedOut = true;
                        result.StandardOutput = process.StandardOutput.ReadToEnd();
                        result.StandardError = process.StandardError.ReadToEnd();
                        return result;
                    }
                    result.ExitCode = process.ExitCode;
                    result.StandardOutput = process.StandardOutput.ReadToEnd();
                    result.StandardError = process.StandardError.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                result.ExitCode = -1;
                result.StandardError = ex.Message;
            }
            return result;
        }

        public TerminalCommandResultPayload RunTerminalCommand(TerminalCommandPayload request)
        {
            var result = new TerminalCommandResultPayload();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {request.Command}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    result.Error = "Failed to start terminal.";
                    result.ExitCode = -1;
                    return result;
                }

                bool exited = process.WaitForExit(request.TimeoutSeconds * 1000);
                if (!exited)
                {
                    result.TimedOut = true;
                    try { process.Kill(); } catch { }
                }

                result.Output = process.StandardOutput.ReadToEnd();
                result.Error = process.StandardError.ReadToEnd();
                result.ExitCode = process.HasExited ? process.ExitCode : -1;
            }
            catch (Exception ex)
            {
                result.ExitCode = -1;
                result.Error = ex.Message;
            }
            return result;
        }

        public List<ProcessInfoPayload> ListProcesses()
        {
            return Process.GetProcesses()
                .Select(p =>
                {
                    try
                    {
                        return new ProcessInfoPayload
                        {
                            ProcessId = p.Id,
                            ProcessName = p.ProcessName,
                            MemoryMB = p.WorkingSet64 / (1024 * 1024)
                        };
                    }
                    catch
                    {
                        return new ProcessInfoPayload
                        {
                            ProcessId = p.Id,
                            ProcessName = p.ProcessName,
                            MemoryMB = 0
                        };
                    }
                })
                .OrderBy(p => p.ProcessName)
                .ToList();
        }

        public KillProcessResultPayload KillProcess(KillProcessPayload request)
        {
            try
            {
                var process = Process.GetProcessById(request.ProcessId);
                string name = process.ProcessName;
                process.Kill();
                return new KillProcessResultPayload { Success = true, Message = $"Killed process {name} (PID {request.ProcessId})." };
            }
            catch (Exception ex)
            {
                return new KillProcessResultPayload { Success = false, Message = ex.Message };
            }
        }

        public ListDirectoryResultPayload ListDirectory(ListDirectoryPayload request)
        {
            var result = new ListDirectoryResultPayload { Path = request.Path };
            try
            {
                var dirInfo = new DirectoryInfo(request.Path);
                if (!dirInfo.Exists)
                {
                    result.Error = "Directory not found.";
                    return result;
                }

                foreach (var dir in dirInfo.GetDirectories())
                {
                    try
                    {
                        result.Entries.Add(new DirectoryEntryPayload
                        {
                            Name = dir.Name,
                            FullPath = dir.FullName,
                            IsDirectory = true,
                            SizeBytes = 0,
                            LastModified = dir.LastWriteTimeUtc
                        });
                    }
                    catch { }
                }

                foreach (var file in dirInfo.GetFiles())
                {
                    try
                    {
                        result.Entries.Add(new DirectoryEntryPayload
                        {
                            Name = file.Name,
                            FullPath = file.FullName,
                            IsDirectory = false,
                            SizeBytes = file.Length,
                            LastModified = file.LastWriteTimeUtc
                        });
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        public DownloadFileResultPayload ReadFile(DownloadFilePayload request)
        {
            var result = new DownloadFileResultPayload { FilePath = request.FilePath };
            try
            {
                const long maxSize = 50 * 1024 * 1024; // 50 MB limit
                var fi = new FileInfo(request.FilePath);
                if (!fi.Exists)
                {
                    result.Error = "File not found.";
                    return result;
                }
                if (fi.Length > maxSize)
                {
                    result.Error = $"File too large ({fi.Length} bytes). Maximum is {maxSize} bytes.";
                    return result;
                }

                byte[] data = File.ReadAllBytes(request.FilePath);
                result.Base64Data = Convert.ToBase64String(data);
                result.SizeBytes = data.Length;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        public UploadFileAckPayload WriteFile(UploadFilePayload request)
        {
            try
            {
                byte[] data = Convert.FromBase64String(request.Base64Data);
                string? dir = Path.GetDirectoryName(request.DestinationPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(request.DestinationPath, data);
                return new UploadFileAckPayload { Success = true, Message = $"Written {data.Length} bytes to {request.DestinationPath}." };
            }
            catch (Exception ex)
            {
                return new UploadFileAckPayload { Success = false, Message = ex.Message };
            }
        }

        public SelfDestructResultPayload SelfDestruct()
        {
            try
            {
                string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
                string? installDir = Path.GetDirectoryName(exePath);

                // Build a cmd script that waits for the process to exit, then deletes the install
                // directory and finally removes itself.
                string tempScript = Path.Combine(Path.GetTempPath(), $"kl_cleanup_{Guid.NewGuid():N}.cmd");
                string script =
                    "@echo off\r\n" +
                    $"timeout /t 3 /nobreak >nul\r\n" +
                    $"taskkill /F /PID {Environment.ProcessId} >nul 2>&1\r\n" +
                    $"timeout /t 2 /nobreak >nul\r\n" +
                    (installDir != null ? $"rmdir /s /q \"{installDir}\"\r\n" : "") +
                    $"del /f /q \"{tempScript}\"\r\n";

                File.WriteAllText(tempScript, script);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{tempScript}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                return new SelfDestructResultPayload { Acknowledged = true, Message = "Self-destruct initiated." };
            }
            catch (Exception ex)
            {
                return new SelfDestructResultPayload { Acknowledged = false, Message = ex.Message };
            }
        }
    }
}
