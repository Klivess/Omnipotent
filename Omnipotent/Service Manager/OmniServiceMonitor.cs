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
using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

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
            public string SessionId { get; set; } = string.Empty;
            public string Command { get; set; } = string.Empty;
            public DateTime ExecutedAtUtc { get; set; }
            public DateTime? CompletedAtUtc { get; set; }
            public string Output { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
            public int? ExitCode { get; set; }
            public string Status { get; set; } = "pending"; // pending, running, completed, error
            public string ExecutedByUser { get; set; } = string.Empty;
            public string WorkingDirectory { get; set; } = string.Empty;
        }

        private sealed class TerminalSession : IDisposable
        {
            public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
            public string ExecutedByUser { get; init; } = string.Empty;
            public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
            public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
            public string CurrentPath { get; set; } = string.Empty;
            public Runspace Runspace { get; init; } = null!;
            public SemaphoreSlim ExecutionLock { get; } = new(1, 1);
            public List<CommandRecord> History { get; } = new();

            public void Dispose()
            {
                try
                {
                    Runspace.Dispose();
                }
                catch { }

                ExecutionLock.Dispose();
            }
        }

        private sealed class TerminalSessionRequest
        {
            public string? SessionId { get; set; }
            public string? Command { get; set; }
        }

        private UptimePeriod currentUptimePeriod;

        private readonly List<CommandRecord> commandHistory = new();
        private readonly ConcurrentDictionary<string, TerminalSession> terminalSessions = new();
        private readonly object historyLock = new();
        private const int MaxHistorySize = 1000;
        private const int MaxSessionHistorySize = 250;
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
            await CreateAPIRoute("/admin/terminal/session/open", HandleTerminalSessionOpen, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);
            await CreateAPIRoute("/admin/terminal/session/execute", HandleTerminalSessionExecute, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);
            await CreateAPIRoute("/admin/terminal/session/reset", HandleTerminalSessionReset, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);
            await CreateAPIRoute("/admin/terminal/session/close", HandleTerminalSessionClose, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);
            await CreateAPIRoute("/admin/terminal/execute", HandleTerminalExecute, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);
            await CreateAPIRoute("/admin/terminal/status", HandleTerminalStatus, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);
            await CreateAPIRoute("/admin/terminal/history", HandleTerminalHistory, HttpMethod.Get, KMProfileManager.KMPermissions.Klives);
            await CreateAPIRoute("/admin/terminal/clear", HandleTerminalClear, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);
        }

        private static TerminalSessionRequest ParseTerminalSessionRequest(string requestBody)
        {
            if (string.IsNullOrWhiteSpace(requestBody))
            {
                return new TerminalSessionRequest();
            }

            try
            {
                return JsonConvert.DeserializeObject<TerminalSessionRequest>(requestBody) ?? new TerminalSessionRequest();
            }
            catch
            {
                return new TerminalSessionRequest();
            }
        }

        private static string GetDefaultTerminalDirectory()
        {
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        }

        private async Task<TerminalSession> CreateTerminalSessionAsync(string userName)
        {
            var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault());
            runspace.Open();

            var session = new TerminalSession
            {
                SessionId = Guid.NewGuid().ToString("N"),
                ExecutedByUser = userName,
                Runspace = runspace,
                CurrentPath = GetDefaultTerminalDirectory(),
                CreatedAtUtc = DateTime.UtcNow,
                LastActivityUtc = DateTime.UtcNow,
            };

            await SetRunspaceLocationAsync(session, session.CurrentPath);
            terminalSessions[session.SessionId] = session;
            return session;
        }

        private async Task SetRunspaceLocationAsync(TerminalSession session, string path)
        {
            using var ps = PowerShell.Create();
            ps.Runspace = session.Runspace;
            ps.AddCommand("Set-Location").AddParameter("LiteralPath", path);
            await Task.Run(() => ps.Invoke());
            session.CurrentPath = await GetRunspaceLocationAsync(session);
        }

        private async Task<string> GetRunspaceLocationAsync(TerminalSession session)
        {
            using var ps = PowerShell.Create();
            ps.Runspace = session.Runspace;
            ps.AddScript("(Get-Location).Path");
            var result = await Task.Run(() => ps.Invoke());
            return result.FirstOrDefault()?.ToString()?.Trim() ?? session.CurrentPath ?? GetDefaultTerminalDirectory();
        }

        private static void AppendPrefixedStream<TRecord>(StringBuilder builder, string prefix, IEnumerable<TRecord> records)
        {
            foreach (var record in records)
            {
                string text = record?.ToString()?.TrimEnd() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (builder.Length > 0 && builder[^1] != '\n')
                {
                    builder.AppendLine();
                }

                builder.AppendLine($"[{prefix}] {text}");
            }
        }

        private static string BuildTerminalOutput(
            ICollection<PSObject> results,
            PSDataCollection<WarningRecord> warnings,
            PSDataCollection<VerboseRecord> verbose,
            PSDataCollection<DebugRecord> debug,
            PSDataCollection<InformationRecord> information)
        {
            var builder = new StringBuilder();

            foreach (var result in results)
            {
                string text = result?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                builder.Append(text);
                if (!text.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                {
                    builder.AppendLine();
                }
            }

            AppendPrefixedStream(builder, "warning", warnings);
            AppendPrefixedStream(builder, "verbose", verbose);
            AppendPrefixedStream(builder, "debug", debug);

            foreach (var info in information)
            {
                string text = info?.MessageData?.ToString()?.TrimEnd()
                    ?? info?.ToString()?.TrimEnd()
                    ?? string.Empty;

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (builder.Length > 0 && builder[^1] != '\n')
                {
                    builder.AppendLine();
                }

                builder.AppendLine(text);
            }

            return builder.ToString().TrimEnd();
        }

        private static string BuildTerminalError(PSDataCollection<ErrorRecord> errors)
        {
            return string.Join(Environment.NewLine, errors
                .Select(error => error?.ToString()?.TrimEnd())
                .Where(text => string.IsNullOrWhiteSpace(text) == false));
        }

        private void AddCommandToGlobalHistory(CommandRecord record)
        {
            lock (historyLock)
            {
                commandHistory.Add(record);
                if (commandHistory.Count > MaxHistorySize)
                {
                    commandHistory.RemoveAt(0);
                }
            }
        }

        private static void AddCommandToSessionHistory(TerminalSession session, CommandRecord record)
        {
            lock (session.History)
            {
                session.History.Add(record);
                if (session.History.Count > MaxSessionHistorySize)
                {
                    session.History.RemoveAt(0);
                }
            }
        }

        private async Task<CommandRecord> ExecuteTerminalSessionCommandAsync(TerminalSession session, string userName, string command)
        {
            var record = new CommandRecord
            {
                SessionId = session.SessionId,
                Command = command,
                ExecutedAtUtc = DateTime.UtcNow,
                Status = "running",
                ExecutedByUser = userName,
                WorkingDirectory = session.CurrentPath
            };

            await session.ExecutionLock.WaitAsync();
            try
            {
                session.LastActivityUtc = DateTime.UtcNow;

                using var ps = PowerShell.Create();
                ps.Runspace = session.Runspace;
                ps.AddScript(command);
                ps.AddCommand("Out-String").AddParameter("Width", 4096);

                var results = await Task.Run(() => ps.Invoke());
                record.Output = BuildTerminalOutput(results, ps.Streams.Warning, ps.Streams.Verbose, ps.Streams.Debug, ps.Streams.Information);
                record.Error = BuildTerminalError(ps.Streams.Error);
                record.ExitCode = ps.HadErrors ? 1 : 0;
                record.Status = ps.HadErrors ? "error" : "completed";
                record.CompletedAtUtc = DateTime.UtcNow;

                session.CurrentPath = await GetRunspaceLocationAsync(session);
                record.WorkingDirectory = session.CurrentPath;
                session.LastActivityUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                record.Error = $"Execution error: {ex.Message}";
                record.ExitCode = -1;
                record.Status = "error";
                record.CompletedAtUtc = DateTime.UtcNow;
                await ServiceLogError(ex, $"Error executing terminal session command for {userName}");
            }
            finally
            {
                session.ExecutionLock.Release();
            }

            AddCommandToSessionHistory(session, record);
            AddCommandToGlobalHistory(record);
            return record;
        }

        private object BuildTerminalSessionResponse(TerminalSession session)
        {
            List<object> history;
            lock (session.History)
            {
                history = session.History
                    .OrderBy(record => record.ExecutedAtUtc)
                    .Select(record => new
                    {
                        commandId = record.CommandId,
                        sessionId = record.SessionId,
                        command = record.Command,
                        output = record.Output,
                        error = record.Error,
                        exitCode = record.ExitCode,
                        status = record.Status,
                        executedAt = record.ExecutedAtUtc,
                        completedAt = record.CompletedAtUtc,
                        workingDirectory = record.WorkingDirectory
                    })
                    .Cast<object>()
                    .ToList();
            }

            return new
            {
                sessionId = session.SessionId,
                createdAt = session.CreatedAtUtc,
                lastActivityAt = session.LastActivityUtc,
                currentPath = session.CurrentPath,
                history,
                welcomeMessage = $"Connected to Omnipotent PowerShell. Session state is preserved in {session.CurrentPath}."
            };
        }

        private bool TryGetTerminalSessionForUser(string? sessionId, string userName, out TerminalSession? session)
        {
            session = null;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            if (!terminalSessions.TryGetValue(sessionId, out var existingSession))
            {
                return false;
            }

            if (!string.Equals(existingSession.ExecutedByUser, userName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            session = existingSession;
            return true;
        }

        private async Task HandleTerminalSessionOpen(UserRequest req)
        {
            try
            {
                string userName = req.user?.Name ?? "Unknown";
                var payload = ParseTerminalSessionRequest(req.userMessageContent);
                bool createdNewSession = false;

                if (!TryGetTerminalSessionForUser(payload.SessionId, userName, out var session) || session == null)
                {
                    session = await CreateTerminalSessionAsync(userName);
                    createdNewSession = true;
                    await ServiceLog($"Terminal session created for {userName}: {session.SessionId}");
                }

                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    success = true,
                    isNewSession = createdNewSession,
                    session = BuildTerminalSessionResponse(session)
                }));
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error opening terminal session");
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(new { success = false, error = "Failed to open terminal session", details = ex.Message }),
                    code: HttpStatusCode.InternalServerError);
            }
        }

        private async Task HandleTerminalSessionExecute(UserRequest req)
        {
            try
            {
                string userName = req.user?.Name ?? "Unknown";
                var payload = ParseTerminalSessionRequest(req.userMessageContent);
                if (string.IsNullOrWhiteSpace(payload.Command))
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new { success = false, error = "Command cannot be empty." }),
                        code: HttpStatusCode.BadRequest);
                    return;
                }

                if (!TryGetTerminalSessionForUser(payload.SessionId, userName, out var session) || session == null)
                {
                    await req.ReturnResponse(
                        JsonConvert.SerializeObject(new { success = false, error = "Terminal session not found." }),
                        code: HttpStatusCode.NotFound);
                    return;
                }

                var record = await ExecuteTerminalSessionCommandAsync(session, userName, payload.Command.Trim());
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    success = true,
                    sessionId = session.SessionId,
                    commandId = record.CommandId,
                    command = record.Command,
                    output = record.Output,
                    error = record.Error,
                    exitCode = record.ExitCode,
                    status = record.Status,
                    executedAt = record.ExecutedAtUtc,
                    completedAt = record.CompletedAtUtc,
                    currentPath = session.CurrentPath,
                }));
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error executing terminal session command");
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(new { success = false, error = "Failed to execute terminal command", details = ex.Message }),
                    code: HttpStatusCode.InternalServerError);
            }
        }

        private async Task HandleTerminalSessionReset(UserRequest req)
        {
            try
            {
                string userName = req.user?.Name ?? "Unknown";
                var payload = ParseTerminalSessionRequest(req.userMessageContent);

                if (TryGetTerminalSessionForUser(payload.SessionId, userName, out var existingSession) && existingSession != null)
                {
                    terminalSessions.TryRemove(existingSession.SessionId, out _);
                    existingSession.Dispose();
                }

                var newSession = await CreateTerminalSessionAsync(userName);
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    success = true,
                    session = BuildTerminalSessionResponse(newSession)
                }));
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error resetting terminal session");
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(new { success = false, error = "Failed to reset terminal session", details = ex.Message }),
                    code: HttpStatusCode.InternalServerError);
            }
        }

        private async Task HandleTerminalSessionClose(UserRequest req)
        {
            try
            {
                string userName = req.user?.Name ?? "Unknown";
                var payload = ParseTerminalSessionRequest(req.userMessageContent);

                if (TryGetTerminalSessionForUser(payload.SessionId, userName, out var session) && session != null)
                {
                    terminalSessions.TryRemove(session.SessionId, out _);
                    session.Dispose();
                }

                await req.ReturnResponse(JsonConvert.SerializeObject(new { success = true }));
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error closing terminal session");
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(new { success = false, error = "Failed to close terminal session", details = ex.Message }),
                    code: HttpStatusCode.InternalServerError);
            }
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
                string sessionId = req.userParameters["sessionId"] ?? string.Empty;
                string userName = req.user?.Name ?? "Unknown";

                if (!string.IsNullOrWhiteSpace(sessionId)
                    && TryGetTerminalSessionForUser(sessionId, userName, out var session)
                    && session != null)
                {
                    List<object> sessionHistory;
                    lock (session.History)
                    {
                        sessionHistory = session.History
                            .OrderByDescending(c => c.ExecutedAtUtc)
                            .Take(limit)
                            .Select(c => new
                            {
                                commandId = c.CommandId,
                                sessionId = c.SessionId,
                                command = c.Command,
                                status = c.Status,
                                executedAt = c.ExecutedAtUtc,
                                completedAt = c.CompletedAtUtc,
                                executedBy = c.ExecutedByUser,
                                workingDirectory = c.WorkingDirectory,
                                outputPreview = c.Output.Length > 200 ? c.Output.Substring(0, 200) + "..." : c.Output
                            })
                            .Cast<object>()
                            .ToList();
                    }

                    await req.ReturnResponse(JsonConvert.SerializeObject(sessionHistory), "application/json");
                    return;
                }

                List<object> history = new();
                lock (historyLock)
                {
                    history = commandHistory
                        .OrderByDescending(c => c.ExecutedAtUtc)
                        .Take(limit)
                        .Select(c => new
                        {
                            commandId = c.CommandId,
                            sessionId = c.SessionId,
                            command = c.Command,
                            status = c.Status,
                            executedAt = c.ExecutedAtUtc,
                            completedAt = c.CompletedAtUtc,
                            executedBy = c.ExecutedByUser,
                            workingDirectory = c.WorkingDirectory,
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
                var payload = ParseTerminalSessionRequest(req.userMessageContent);
                string userName = req.user?.Name ?? "Unknown";

                if (TryGetTerminalSessionForUser(payload.SessionId, userName, out var session) && session != null)
                {
                    lock (session.History)
                    {
                        session.History.Clear();
                    }
                }

                lock (historyLock)
                {
                    if (string.IsNullOrWhiteSpace(payload.SessionId))
                    {
                        commandHistory.Clear();
                    }
                    else
                    {
                        commandHistory.RemoveAll(record => string.Equals(record.SessionId, payload.SessionId, StringComparison.OrdinalIgnoreCase));
                    }
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
