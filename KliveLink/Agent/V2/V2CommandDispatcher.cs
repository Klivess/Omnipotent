using System.Diagnostics;
using System.Management;
using System.Text;
using KliveLink.Contracts;

namespace KliveLink.Agent.V2;

public sealed class V2CommandDispatcher
{
    private readonly string _agentId;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public V2CommandDispatcher(string agentId) => _agentId = agentId;

    public V2SystemInfo GetSystemInfo()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        long free = 0;
        try { free = new DriveInfo(root).AvailableFreeSpace / (1024 * 1024); } catch { }
        return new V2SystemInfo
        {
            MachineName = Environment.MachineName,
            OsVersion = Environment.OSVersion.ToString(),
            UserName = Environment.UserName,
            Domain = Environment.UserDomainName,
            ProcessorCount = Environment.ProcessorCount,
            TotalMemoryMB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024),
            FreeDiskMB = free,
            AgentStartTime = _startedAt,
            AgentVersion = "2.0.0",
            AgentId = _agentId,
            Capabilities = V2Capabilities.All,
        };
    }

    public async Task<V2RunProcessResult> RunProcessAsync(V2RunProcessRequest request, CancellationToken ct)
    {
        var result = new V2RunProcessResult();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = request.FileName,
                Arguments = request.Arguments ?? "",
                WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                    ? Environment.CurrentDirectory
                    : request.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = request.WaitForExit,
                RedirectStandardError = request.WaitForExit,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var process = Process.Start(psi);
            if (process == null)
            {
                result.ExitCode = -1;
                result.StandardError = "Failed to start process.";
                return result;
            }
            result.ProcessId = process.Id;
            if (!request.WaitForExit) return result;

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
            Task wait = process.WaitForExitAsync(ct);
            Task completed = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 600)), ct));
            if (completed != wait)
            {
                result.TimedOut = true;
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            else
            {
                result.ExitCode = process.ExitCode;
            }
            result.StandardOutput = Truncate(await stdout, 2 * 1024 * 1024);
            result.StandardError = Truncate(await stderr, 2 * 1024 * 1024);
        }
        catch (Exception ex)
        {
            result.ExitCode = -1;
            result.StandardError = ex.Message;
        }
        return result;
    }

    public async Task<V2TerminalResult> RunTerminalAsync(V2TerminalRequest request, CancellationToken ct)
    {
        var result = new V2TerminalResult();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /s /c {request.Command}",
                WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                    ? Environment.CurrentDirectory
                    : request.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var process = Process.Start(psi);
            if (process == null)
            {
                result.Error = "Failed to start terminal.";
                result.ExitCode = -1;
                return result;
            }
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
            Task wait = process.WaitForExitAsync(ct);
            Task completed = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 600)), ct));
            if (completed != wait)
            {
                result.TimedOut = true;
                try { process.Kill(entireProcessTree: true); } catch { }
                result.ExitCode = -1;
            }
            else
            {
                result.ExitCode = process.ExitCode;
            }
            result.Output = Truncate(await stdout, 2 * 1024 * 1024);
            result.Error = Truncate(await stderr, 2 * 1024 * 1024);
        }
        catch (Exception ex)
        {
            result.ExitCode = -1;
            result.Error = ex.Message;
        }
        return result;
    }

    public List<V2ProcessInfo> ListProcesses()
    {
        var list = new List<V2ProcessInfo>();
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { }
                list.Add(new V2ProcessInfo
                {
                    ProcessId = process.Id,
                    ProcessName = process.ProcessName,
                    Path = path,
                    MemoryMB = process.WorkingSet64 / (1024 * 1024),
                });
            }
            catch { }
        }
        return list.OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public V2KillProcessResult KillProcess(V2KillProcessRequest request)
    {
        try
        {
            using var process = Process.GetProcessById(request.ProcessId);
            string name = process.ProcessName;
            process.Kill(entireProcessTree: request.Tree);
            return new V2KillProcessResult { Success = true, Message = $"Killed {name} (PID {request.ProcessId})." };
        }
        catch (Exception ex)
        {
            return new V2KillProcessResult { Success = false, Message = ex.Message };
        }
    }

    public V2ListDirectoryResult ListDirectory(V2ListDirectoryRequest request)
    {
        var result = new V2ListDirectoryResult { Path = request.Path };
        try
        {
            var info = new DirectoryInfo(request.Path);
            if (!info.Exists)
            {
                result.Error = "Directory not found.";
                return result;
            }
            foreach (var dir in info.GetDirectories())
            {
                try
                {
                    result.Entries.Add(new V2DirectoryEntry
                    {
                        Name = dir.Name,
                        FullPath = dir.FullName,
                        IsDirectory = true,
                        LastModified = dir.LastWriteTimeUtc,
                    });
                }
                catch { }
            }
            foreach (var file in info.GetFiles())
            {
                try
                {
                    result.Entries.Add(new V2DirectoryEntry
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        IsDirectory = false,
                        SizeBytes = file.Length,
                        LastModified = file.LastWriteTimeUtc,
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

    public V2DownloadFileResult DownloadFile(V2DownloadFileRequest request)
    {
        var result = new V2DownloadFileResult { FilePath = request.FilePath };
        try
        {
            const long max = 50 * 1024 * 1024;
            var info = new FileInfo(request.FilePath);
            if (!info.Exists) { result.Error = "File not found."; return result; }
            if (info.Length > max) { result.Error = $"File too large ({info.Length} bytes)."; return result; }
            byte[] data = File.ReadAllBytes(request.FilePath);
            result.Base64Data = Convert.ToBase64String(data);
            result.SizeBytes = data.Length;
        }
        catch (Exception ex) { result.Error = ex.Message; }
        return result;
    }

    public V2UploadFileResult UploadFile(V2UploadFileRequest request)
    {
        try
        {
            byte[] data = Convert.FromBase64String(request.Base64Data ?? "");
            string? dir = Path.GetDirectoryName(request.DestinationPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            string temp = request.DestinationPath + ".kl-tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, request.DestinationPath, true);
            return new V2UploadFileResult { Success = true, Message = $"Wrote {data.Length} bytes." };
        }
        catch (Exception ex)
        {
            return new V2UploadFileResult { Success = false, Message = ex.Message };
        }
    }

    public static string? FindWatchdogPid()
    {
        try
        {
            string name = Path.GetFileName(Environment.ProcessPath ?? "KliveLink.exe");
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='{name.Replace("'", "''")}'");
            foreach (ManagementObject item in searcher.Get())
            {
                string? command = item["CommandLine"] as string;
                if (command != null && command.Contains("--watchdog", StringComparison.OrdinalIgnoreCase))
                    return Convert.ToString(item["ProcessId"]);
            }
        }
        catch { }
        return null;
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "\n[truncated]";
}
