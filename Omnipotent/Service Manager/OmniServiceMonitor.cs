using ByteSizeLib;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.Net;
using Newtonsoft.Json;
using Omnipotent.Profiles;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Data_Handling;
using System.Diagnostics;
using System.Text;

namespace Omnipotent.Service_Manager
{
    public class OmniServiceMonitor : OmniService
    {
        public OmniServiceMonitor()
        {
            name = "Omnipotent Service Monitoring";
            threadAnteriority = ThreadAnteriority.Standard;
        }
        public int CPUUsagePercentage { get; private set; }
        List<OmniMonitoredThread> monitoredThreads = new();
        public ByteSize MemoryUsage { get; private set; }
        public ByteSize TotalSystemRAM { get; private set; }

        private UptimeStatistics uptimeStatistics = new UptimeStatistics();
        private string UptimeLogsFilePath;

        public class UptimePeriod
        {
            public DateTime StartTime { get; set; }
            public DateTime LastKnownUpTime { get; set; }
            [JsonIgnore]
            public TimeSpan Duration => LastKnownUpTime - StartTime;
        }

        public class UptimeStatistics
        {
            public List<UptimePeriod> Periods { get; set; } = new List<UptimePeriod>();
            [JsonIgnore]
            public TimeSpan TotalUptime => TimeSpan.FromSeconds(Periods.Sum(x => x.Duration.TotalSeconds));
            [JsonIgnore]
            public double AverageUptimeHours => Periods.Count > 0 ? TotalUptime.TotalHours / Periods.Count : 0;
            [JsonIgnore]
            public TimeSpan CurrentUptime => Periods.Count > 0 ? Periods.Last().Duration : TimeSpan.Zero;
        }

        private sealed class CommandRecord
        {
            public string CommandId { get; set; } = Guid.NewGuid().ToString().Substring(0, 8);
            public string Command { get; set; } = string.Empty;
            public DateTime ExecutedAtUtc { get; set; }
            public DateTime? CompletedAtUtc { get; set; }
            public string Output { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
            public int? ExitCode { get; set; }
            public string Status { get; set; } = "pending"; // pending, running, completed, error
            public string ExecutedByUser { get; set; } = string.Empty;
        }

        private UptimePeriod currentUptimePeriod;

        private readonly List<CommandRecord> commandHistory = new();
        private readonly object historyLock = new();
        private const int MaxHistorySize = 1000;
        private const int CommandTimeout = 60000; // 60 seconds

        public struct OmniMonitoredThread
        {
            public string name;
            public int ManagedThreadID;
            public int cpuUsage;
            public int totalFileOperations;
            public ByteSize memoryUsageInBytes;
        }
        private OmniMonitoredThread ConvertOmniServiceToOmniMonitoredThread(OmniService service)
        {
            OmniMonitoredThread omniMonitoredThread = new OmniMonitoredThread();
            omniMonitoredThread.name = service.GetName();
            omniMonitoredThread.ManagedThreadID = service.GetThread().ManagedThreadId;
            omniMonitoredThread.cpuUsage = -1;
            omniMonitoredThread.totalFileOperations = 0;
            omniMonitoredThread.memoryUsageInBytes = ByteSize.FromBytes(0);
            return omniMonitoredThread;
        }
        public void SetServiceToMonitor(OmniService omniMonitored)
        {
            try
            {
                var monitored = ConvertOmniServiceToOmniMonitoredThread(omniMonitored);
                monitoredThreads.Add(monitored);
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Error setting service to monitor.");
                TerminateService();
            }
        }
        public List<OmniMonitoredThread> GetCurrentServicesBeingMonitored { get { return monitoredThreads; } }
        protected override async void ServiceMain()
        {
            try
            {
                UptimeLogsFilePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.ProcessMonitorLogsUptimes), "OmniServiceMonitor_UptimeLogs.json");


                Thread ramAndCpuCounters = new(UpdateCPUandRAMCounters);
                ramAndCpuCounters.Start();
                _ = SetupUptimeTrackingAsync();
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Error in ServiceMain.");
            }
        }

        private async Task SetupUptimeTrackingAsync()
        {
            await InitializeUptimeDataAsync();
            _ = StartUptimeSaveLoopAsync();
            await SetupUptimeApiRoutesAsync();
        }

        private async Task InitializeUptimeDataAsync()
        {
            try
            {
                uptimeStatistics = await GetDataHandler().ReadAndDeserialiseDataFromFile<UptimeStatistics>(UptimeLogsFilePath);
            }
            catch
            {
                uptimeStatistics = new UptimeStatistics();
            }

            if (uptimeStatistics == null) uptimeStatistics = new UptimeStatistics();
            if (uptimeStatistics.Periods == null) uptimeStatistics.Periods = new List<UptimePeriod>();

            currentUptimePeriod = new UptimePeriod
            {
                StartTime = DateTime.UtcNow,
                LastKnownUpTime = DateTime.UtcNow
            };
            uptimeStatistics.Periods.Add(currentUptimePeriod);
        }

        private async Task StartUptimeSaveLoopAsync()
        {
            while (true)
            {
                await Task.Delay(60000); // Wait 1 minute
                if (currentUptimePeriod != null)
                {
                    currentUptimePeriod.LastKnownUpTime = DateTime.UtcNow;
                    try
                    {
                        await GetDataHandler().SerialiseObjectToFile(UptimeLogsFilePath, uptimeStatistics);
                    }
                    catch (Exception ex)
                    {
                        await ServiceLogError(ex, "Failed to save uptime logs.", false);
                    }
                }
            }
        }

        private async Task SetupUptimeApiRoutesAsync()
        {
            await CreateAPIRoute("/System/UptimeStatistics", HandleUptimeStatisticsRequest, HttpMethod.Get, KMProfileManager.KMPermissions.Admin);
            await SetupTerminalApiRoutesAsync();
        }

        private async Task HandleUptimeStatisticsRequest(UserRequest req)
        {
            var stats = CalculateUptimeStatistics();
            string json = JsonConvert.SerializeObject(stats, Formatting.Indented);
            await req.ReturnResponse(json, code: HttpStatusCode.OK);
        }

        private object CalculateUptimeStatistics()
        {
            // Calculate total possible time since the very first recorded start time
            TimeSpan totalOutage = TimeSpan.Zero;
            if (uptimeStatistics.Periods.Count > 0)
            {
                DateTime firstStart = uptimeStatistics.Periods.First().StartTime;
                TimeSpan totalTimeSinceFirstStart = DateTime.UtcNow - firstStart;
                totalOutage = totalTimeSinceFirstStart - uptimeStatistics.TotalUptime;
                if (totalOutage.TotalSeconds < 0) totalOutage = TimeSpan.Zero;
            }

            return new
            {
                TotalUptimeSeconds = uptimeStatistics.TotalUptime.TotalSeconds,
                AverageUptimeHours = uptimeStatistics.AverageUptimeHours,
                CurrentUptimeSeconds = uptimeStatistics.CurrentUptime.TotalSeconds,
                TotalOutageSeconds = totalOutage.TotalSeconds,
                TotalPeriods = uptimeStatistics.Periods.Count,
                Periods = uptimeStatistics.Periods
            };
        }

        private async void UpdateCPUandRAMCounters()
        {
            // Cache total RAM once – it never changes at runtime
            long totalRamMiB = PerformanceInfo.GetTotalMemoryInMiB();
            if (totalRamMiB > 0)
            {
                TotalSystemRAM = ByteSize.FromMegaBytes(totalRamMiB);
            }

            // Take an initial CPU snapshot so the first delta is valid
            GetSystemTimes(out var prevIdle, out var prevKernel, out var prevUser);
            await Task.Delay(1000);

            while (true)
            {
                try
                {
                    // CPU usage via GetSystemTimes delta
                    if (GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
                    {
                        long idleDelta = FileTimeToLong(idleTime) - FileTimeToLong(prevIdle);
                        long kernelDelta = FileTimeToLong(kernelTime) - FileTimeToLong(prevKernel);
                        long userDelta = FileTimeToLong(userTime) - FileTimeToLong(prevUser);
                        long totalDelta = kernelDelta + userDelta; // kernel includes idle
                        CPUUsagePercentage = totalDelta > 0
                            ? (int)(((totalDelta - idleDelta) * 100) / totalDelta)
                            : 0;
                        prevIdle = idleTime;
                        prevKernel = kernelTime;
                        prevUser = userTime;
                    }

                    // RAM usage via P/Invoke (fast, no WMI)
                    long availableMiB = PerformanceInfo.GetPhysicalAvailableMemoryInMiB();
                    if (availableMiB >= 0 && totalRamMiB > 0)
                    {
                        long usedMiB = totalRamMiB - availableMiB;
                        MemoryUsage = ByteSize.FromMegaBytes(usedMiB);
                    }
                }
                catch (Exception ex)
                {
                    ServiceLogError(ex, "Error reading CPU/RAM counters, retrying...");
                }

                await Task.Delay(2000);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(
            out System.Runtime.InteropServices.ComTypes.FILETIME lpIdleTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime,
            out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime);

        private static long FileTimeToLong(System.Runtime.InteropServices.ComTypes.FILETIME ft)
        {
            return ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        }

        public static class PerformanceInfo
        {
            [DllImport("psapi.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetPerformanceInfo([Out] out PerformanceInformation PerformanceInformation, [In] int Size);

            [StructLayout(LayoutKind.Sequential)]
            public struct PerformanceInformation
            {
                public int Size;
                public IntPtr CommitTotal;
                public IntPtr CommitLimit;
                public IntPtr CommitPeak;
                public IntPtr PhysicalTotal;
                public IntPtr PhysicalAvailable;
                public IntPtr SystemCache;
                public IntPtr KernelTotal;
                public IntPtr KernelPaged;
                public IntPtr KernelNonPaged;
                public IntPtr PageSize;
                public int HandlesCount;
                public int ProcessCount;
                public int ThreadCount;
            }

            public static long GetPhysicalAvailableMemoryInMiB()
            {
                PerformanceInformation pi = new PerformanceInformation();
                if (GetPerformanceInfo(out pi, Marshal.SizeOf(pi)))
                {
                    return pi.PhysicalAvailable.ToInt64() * pi.PageSize.ToInt64() / 1048576;
                }
                return -1;
            }

            public static long GetTotalMemoryInMiB()
            {
                PerformanceInformation pi = new PerformanceInformation();
                if (GetPerformanceInfo(out pi, Marshal.SizeOf(pi)))
                {
                    return pi.PhysicalTotal.ToInt64() * pi.PageSize.ToInt64() / 1048576;
                }
                return -1;
            }
        }

        private async Task SetupTerminalApiRoutesAsync()
        {
            await CreateAPIRoute("/admin/terminal/execute", HandleTerminalExecute, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);
            await CreateAPIRoute("/admin/terminal/status", HandleTerminalStatus, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);
            await CreateAPIRoute("/admin/terminal/history", HandleTerminalHistory, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);
            await CreateAPIRoute("/admin/terminal/clear", HandleTerminalClear, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);
        }

        private async Task HandleTerminalExecute(UserRequest req)
        {
            try
            {
                // Extract command from request body
                var command = req.userMessageContent?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(command))
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new { error = "Command cannot be empty" }),
                        "application/json",
                        code: HttpStatusCode.BadRequest);
                    return;
                }

                // Create command record
                var record = new CommandRecord
                {
                    Command = command,
                    ExecutedAtUtc = DateTime.UtcNow,
                    Status = "running",
                    ExecutedByUser = req.user?.Name ?? "Unknown"
                };

                lock (historyLock)
                {
                    commandHistory.Add(record);
                    if (commandHistory.Count > MaxHistorySize)
                    {
                        commandHistory.RemoveAt(0); // Remove oldest
                    }
                }

                // Execute command asynchronously
                _ = ExecuteCommandAsync(record);

                // Return immediate response with command ID
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(new
                    {
                        commandId = record.CommandId,
                        status = "queued",
                        message = "Command queued for execution"
                    }),
                    "application/json",
                    code: HttpStatusCode.Accepted);

                await ServiceLog($"Command queued by {req.user?.Name}: {command.Substring(0, Math.Min(100, command.Length))}");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error in terminal execute endpoint");
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(new { error = "Failed to execute command", details = ex.Message }),
                    "application/json",
                    code: HttpStatusCode.InternalServerError);
            }
        }

        private async Task HandleTerminalStatus(UserRequest req)
        {
            try
            {
                var commandId = req.userParameters["commandId"];
                if (string.IsNullOrWhiteSpace(commandId))
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new { error = "Command ID required" }),
                        "application/json",
                        code: HttpStatusCode.BadRequest);
                    return;
                }

                CommandRecord? record = null;
                lock (historyLock)
                {
                    record = commandHistory.FirstOrDefault(c => c.CommandId == commandId);
                }

                if (record == null)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new { error = "Command not found" }),
                        "application/json",
                        code: HttpStatusCode.NotFound);
                    return;
                }

                var response = new
                {
                    commandId = record.CommandId,
                    command = record.Command,
                    status = record.Status,
                    output = record.Output,
                    error = record.Error,
                    exitCode = record.ExitCode,
                    executedAt = record.ExecutedAtUtc,
                    completedAt = record.CompletedAtUtc,
                    isComplete = record.Status == "completed" || record.Status == "error"
                };

                await req.ReturnResponse(JsonConvert.SerializeObject(response), "application/json");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error in terminal status endpoint");
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(new { error = "Failed to retrieve status", details = ex.Message }),
                    "application/json",
                    code: HttpStatusCode.InternalServerError);
            }
        }

        private async Task HandleTerminalHistory(UserRequest req)
        {
            try
            {
                var limit = int.TryParse(req.userParameters["limit"], out var l) ? Math.Min(l, 100) : 50;

                List<object> history = new();
                lock (historyLock)
                {
                    history = commandHistory
                        .OrderByDescending(c => c.ExecutedAtUtc)
                        .Take(limit)
                        .Select(c => new
                        {
                            commandId = c.CommandId,
                            command = c.Command,
                            status = c.Status,
                            executedAt = c.ExecutedAtUtc,
                            completedAt = c.CompletedAtUtc,
                            executedBy = c.ExecutedByUser,
                            outputPreview = c.Output.Length > 200 ? c.Output.Substring(0, 200) + "..." : c.Output
                        })
                        .Cast<object>()
                        .ToList();
                }

                await req.ReturnResponse(JsonConvert.SerializeObject(history), "application/json");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error in terminal history endpoint");
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(new { error = "Failed to retrieve history", details = ex.Message }),
                    "application/json",
                    code: HttpStatusCode.InternalServerError);
            }
        }

        private async Task HandleTerminalClear(UserRequest req)
        {
            try
            {
                lock (historyLock)
                {
                    commandHistory.Clear();
                }

                await ServiceLog($"Terminal history cleared by {req.user?.Name}");
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(new { message = "History cleared" }),
                    "application/json");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error in terminal clear endpoint");
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(new { error = "Failed to clear history", details = ex.Message }),
                    "application/json",
                    code: HttpStatusCode.InternalServerError);
            }
        }

        private async Task ExecuteCommandAsync(CommandRecord record)
        {
            try
            {
                using (var process = new Process())
                {
                    // Configure process to run command
                    process.StartInfo.FileName = "cmd.exe";
                    process.StartInfo.Arguments = $"/c {record.Command}";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                    process.StartInfo.StandardErrorEncoding = Encoding.UTF8;

                    var outputBuilder = new StringBuilder();
                    var errorBuilder = new StringBuilder();

                    // Capture output
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            outputBuilder.AppendLine(e.Data);
                        }
                    };

                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                        {
                            errorBuilder.AppendLine(e.Data);
                        }
                    };

                    // Start process
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // Wait for completion with timeout
                    if (process.WaitForExit(CommandTimeout))
                    {
                        record.Output = outputBuilder.ToString();
                        record.Error = errorBuilder.ToString();
                        record.ExitCode = process.ExitCode;
                        record.Status = process.ExitCode == 0 ? "completed" : "error";
                    }
                    else
                    {
                        process.Kill();
                        record.Error = "Command execution timeout";
                        record.Status = "error";
                        record.ExitCode = -1;
                    }
                }

                record.CompletedAtUtc = DateTime.UtcNow;
                await ServiceLog($"Command completed: {record.Command.Substring(0, Math.Min(50, record.Command.Length))} - {record.Status}");
            }
            catch (Exception ex)
            {
                record.Error = $"Execution error: {ex.Message}";
                record.Status = "error";
                record.CompletedAtUtc = DateTime.UtcNow;
                await ServiceLogError(ex, $"Error executing command: {record.Command}");
            }
        }
    }
}
