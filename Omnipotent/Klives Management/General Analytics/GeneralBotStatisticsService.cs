using Humanizer;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.OmniUpdater;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace Omnipotent.Klives_Management.General_Analytics
{
    public class GeneralBotStatisticsService : OmniService
    {
        public FrontPageStatistics fpstats;
        private GeneralBotStatisticsServiceRoutes routes;

        // One sample per refresh of the snapshot below: an hour at the 5 s refresh rate.
        private const int RefreshSeconds = 5;
        public readonly VitalsHistory History = new(capacity: 3600 / RefreshSeconds, stepSeconds: RefreshSeconds);

        // Cached values that rarely change – refreshed once at startup
        private string cachedOSVersion;
        private string cachedMachineName;
        private int cachedProcessorCount;
        private string cachedProcessorName;

        public GeneralBotStatisticsService()
        {
            name = "General Bot Statistics Service (KM)";
            threadAnteriority = ThreadAnteriority.Low;
        }

        protected override void ServiceMain()
        {
            CacheStaticHardwareInfo();
            routes = new GeneralBotStatisticsServiceRoutes(this);
            _ = GetGeneralBotStatistics();
        }

        private void CacheStaticHardwareInfo()
        {
            cachedOSVersion = Environment.OSVersion.ToString();
            cachedMachineName = Environment.MachineName;
            cachedProcessorCount = Environment.ProcessorCount;
            cachedProcessorName = string.Empty;
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                foreach (ManagementObject obj in searcher.Get())
                {
                    cachedProcessorName = obj["Name"]?.ToString() ?? string.Empty;
                    break;
                }
            }
            catch { }
        }

        private async Task GetGeneralBotStatistics()
        {
            while (true)
            {
                try
                {
                    var stats = new FrontPageStatistics();

                    // --- Bot / Application ---
                    stats.lastOmnipotentUpdate = File.GetLastWriteTime(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Omnipotent.exe"));
                    stats.lastOmnipotentUpdateHumanized = stats.lastOmnipotentUpdate.Humanize();
                    stats.BotUptime = GetManagerUptime();
                    stats.BotUptimeHumanized = stats.BotUptime.Humanize();

                    stats.Build = BuildIdentity.Current;
                    stats.Updater = OmniUpdater.LatestStatus;

                    // --- Logging ---
                    var logger = GetLoggerService();
                    var messages = logger.overallMessages;
                    stats.TotalLogs = messages.Count;
                    int statusCount = 0, errorCount = 0;
                    var errorsByService = new ServiceErrorTally(DateTime.Now);
                    foreach (var msg in messages)
                    {
                        var t = msg.type;
                        if (t == OmniLogging.LogType.Status) statusCount++;
                        else if (t == OmniLogging.LogType.Error)
                        {
                            errorCount++;
                            errorsByService.Add(msg.serviceName, msg.TimeOfLog);
                        }
                    }
                    stats.TotalStatusLogs = statusCount;
                    stats.TotalErrorLogs = errorCount;
                    stats.ErrorsLastHour = errorsByService.TotalLastHour;
                    stats.ErrorsLoggedTotal = logger.TotalErrorsLogged;

                    // --- System Hardware (from OmniServiceMonitor) ---
                    try
                    {
                        var monitor = GetServiceMonitor();
                        double totalBytes = monitor.TotalSystemRAM.Bytes;
                        stats.RamTotalGB = monitor.TotalSystemRAM.GigaBytes;
                        stats.RamUsedGB = monitor.MemoryUsage.GigaBytes;
                        stats.RamUsagePercentage = totalBytes > 0 ? (monitor.MemoryUsage.Bytes / totalBytes) * 100 : 0;
                        stats.CpuUsagePercentage = monitor.CPUUsagePercentage;
                    }
                    catch { }

                    // --- Process-level stats ---
                    try
                    {
                        var proc = Process.GetCurrentProcess();
                        stats.ProcessMemoryMB = proc.WorkingSet64 / (1024.0 * 1024.0);
                        stats.ProcessThreadCount = proc.Threads.Count;
                        stats.GCTotalMemoryMB = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
                        stats.GCGen0Collections = GC.CollectionCount(0);
                        stats.GCGen1Collections = GC.CollectionCount(1);
                        stats.GCGen2Collections = GC.CollectionCount(2);
                    }
                    catch { }

                    // --- Cached machine info ---
                    stats.OSVersion = cachedOSVersion;
                    stats.MachineName = cachedMachineName;
                    stats.ProcessorCount = cachedProcessorCount;
                    stats.ProcessorName = cachedProcessorName;

                    // --- Disk info ---
                    try
                    {
                        var drives = DriveInfo.GetReady();
                        stats.DiskStatistics = new DiskStats[drives.Length];
                        for (int i = 0; i < drives.Length; i++)
                        {
                            var d = drives[i];
                            stats.DiskStatistics[i] = new DiskStats
                            {
                                DriveName = d.Name,
                                VolumeLabel = d.VolumeLabel,
                                TotalSizeGB = d.TotalSize / (1024.0 * 1024.0 * 1024.0),
                                FreeSpaceGB = d.TotalFreeSpace / (1024.0 * 1024.0 * 1024.0),
                                UsedSpaceGB = (d.TotalSize - d.TotalFreeSpace) / (1024.0 * 1024.0 * 1024.0),
                                UsagePercentage = d.TotalSize > 0 ? ((d.TotalSize - d.TotalFreeSpace) / (double)d.TotalSize) * 100 : 0,
                                DriveFormat = d.DriveFormat
                            };
                        }
                    }
                    catch { stats.DiskStatistics = []; }

                    // --- Network interfaces ---
                    long netSent = 0, netReceived = 0;
                    try
                    {
                        // Windows lists every filter driver bound to an adapter as an interface of its own
                        // (the server reports 52, for one physical card): the same wire with the same
                        // counters. Those, and pseudo-adapters that have never carried a byte, are left out.
                        var listed = new List<NetInterfaceStats>();
                        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                            if (IsFilterLayer(ni.Name)) continue;
                            var ipStats = ni.GetIPv4Statistics();
                            if (ipStats.BytesSent == 0 && ipStats.BytesReceived == 0) continue;
                            bool isVirtual = IsVirtualAdapter(ni.Name, ni.Description);
                            listed.Add(new NetInterfaceStats
                            {
                                Name = ni.Name,
                                Description = ni.Description,
                                SpeedMbps = ni.Speed / 1_000_000.0,
                                BytesSent = ipStats.BytesSent,
                                BytesReceived = ipStats.BytesReceived,
                                IsVirtual = isVirtual
                            });
                            // Virtual switches carry the same packets as the adapter behind them: adding
                            // them would count that traffic twice.
                            if (!isVirtual)
                            {
                                netSent += ipStats.BytesSent;
                                netReceived += ipStats.BytesReceived;
                            }
                        }
                        stats.NetworkInterfaces = listed.ToArray();
                    }
                    catch { stats.NetworkInterfaces = []; }

                    // --- Active services ---
                    try
                    {
                        // Read by index: services are still being appended to this list during startup.
                        var services = GetActiveServices();
                        stats.TotalServicesRegistered = services.Count;
                        int active = 0;
                        KliveAPI? api = null;
                        var summaries = new List<ServiceSummary>(services.Count);
                        for (int i = 0; i < services.Count; i++)
                        {
                            var svc = services[i];
                            if (svc is KliveAPI apiService) api = apiService;
                            bool isActive = svc.IsServiceActive();
                            if (isActive) active++;
                            TimeSpan uptime = isActive ? svc.GetServiceUptime() : TimeSpan.Zero;
                            string serviceName = svc.GetName();
                            errorsByService.TryGet(serviceName, out var errors);
                            summaries.Add(new ServiceSummary
                            {
                                Name = serviceName,
                                ServiceID = svc.serviceID,
                                IsActive = isActive,
                                Uptime = uptime,
                                UptimeHumanized = isActive ? uptime.Humanize() : "Inactive",
                                UptimeSeconds = (long)uptime.TotalSeconds,
                                Anteriority = svc.GetThreadAnteriority().ToString(),
                                RunsOnce = svc.RunsToCompletion,
                                ErrorsLastHour = errors.LastHour,
                                ErrorsRetained = errors.Retained,
                                LastErrorAt = errors.Retained > 0 ? errors.LastAt : null
                            });
                        }
                        stats.TotalServicesActive = active;
                        stats.Services = summaries.ToArray();

                        // Running totals the admin page turns into request and error rates.
                        if (api != null)
                        {
                            stats.ApiRequestsTotal = api.TotalRequestsRecorded;
                            stats.ApiServerErrorsTotal = api.ServerErrorsRecorded;
                        }
                    }
                    catch { stats.Services ??= []; }

                    // --- Scheduled tasks ---
                    try
                    {
                        var tasks = GetTimeManagerService().tasks;
                        stats.TotalScheduledTasks = tasks.Count;
                        var nextTask = tasks.Where(t => !t.HasTaskTimePassed()).OrderBy(t => t.dateTimeDue).FirstOrDefault();
                        if (nextTask != null)
                        {
                            stats.NextTaskScheduledSummary = $"{nextTask.taskName} due {nextTask.dateTimeDue:g} ({nextTask.GetTimespanRemaining().Humanize()} remaining)";
                        }
                    }
                    catch { }

                    stats.TimeStatisticsGenerated = DateTime.Now;

                    History.Append(new VitalsHistory.Sample(
                        DateTime.UtcNow,
                        stats.CpuUsagePercentage,
                        stats.RamUsagePercentage,
                        stats.ProcessMemoryMB,
                        stats.GCTotalMemoryMB,
                        stats.ProcessThreadCount,
                        stats.GCGen2Collections,
                        netSent,
                        netReceived,
                        stats.ErrorsLoggedTotal,
                        stats.ApiRequestsTotal,
                        stats.ApiServerErrorsTotal));

                    // Atomic swap – readers always see a complete snapshot
                    fpstats = stats;
                }
                catch (Exception ex)
                {
                    ServiceLogError(ex);
                }

                await Task.Delay(RefreshSeconds * 1000);
            }
        }

        /// <summary>
        /// Virtual switches and tunnels (Hyper-V, WSL, Docker, VPN adapters) mirror traffic that a physical
        /// adapter already counted.
        /// </summary>
        public static bool IsVirtualAdapter(string? name, string? description)
        {
            string text = $"{name} {description}";
            string[] markers = ["vEthernet", "vSwitch", "Hyper-V", "Virtual", "WSL", "Docker", "VMware", "VirtualBox", "TAP-", "WireGuard", "Tailscale", "Loopback", "Pseudo", "Teredo", "ISATAP", "Miniport"];
            return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// One filter driver bound to another adapter, which Windows names "adapter-driver-0000"
        /// ("Ethernet-QoS Packet Scheduler-0000"). It reports that adapter's traffic a second time.
        /// </summary>
        public static bool IsFilterLayer(string? name) => name != null && FilterLayerName.IsMatch(name);

        private static readonly Regex FilterLayerName = new(@"-[^-]+-\d{4}$", RegexOptions.Compiled);

        /// <summary>Errors in the retained log buffer, counted per service in one pass.</summary>
        public sealed class ServiceErrorTally
        {
            public readonly record struct Entry(int LastHour, int Retained, DateTime LastAt);

            private readonly Dictionary<string, Entry> byService = new(StringComparer.OrdinalIgnoreCase);
            private readonly DateTime hourAgo;

            /// <param name="now">In the clock the log entries were stamped with (local time).</param>
            public ServiceErrorTally(DateTime now) { hourAgo = now.AddHours(-1); }

            public int TotalLastHour { get; private set; }

            public void Add(string? serviceName, DateTime loggedAt)
            {
                bool recent = loggedAt >= hourAgo;
                if (recent) TotalLastHour++;
                if (string.IsNullOrWhiteSpace(serviceName)) return;
                byService.TryGetValue(serviceName, out Entry entry);
                byService[serviceName] = new Entry(
                    entry.LastHour + (recent ? 1 : 0),
                    entry.Retained + 1,
                    loggedAt > entry.LastAt ? loggedAt : entry.LastAt);
            }

            public bool TryGet(string? serviceName, out Entry entry)
            {
                entry = default;
                return !string.IsNullOrWhiteSpace(serviceName) && byService.TryGetValue(serviceName, out entry);
            }
        }

        // Helper to get ready drives without throwing on unavailable ones. Optical media is always "full"
        // (the server's E:\ is a mounted CD at 100%), so it is left out of disk space reporting.
        private static class DriveInfo
        {
            public static System.IO.DriveInfo[] GetReady()
            {
                return System.IO.DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType != DriveType.CDRom).ToArray();
            }
        }

        public class FrontPageStatistics
        {
            public DateTime TimeStatisticsGenerated;

            // Bot core
            public DateTime lastOmnipotentUpdate;
            public string lastOmnipotentUpdateHumanized;
            public TimeSpan BotUptime;
            public string BotUptimeHumanized;

            // What is running, and what the updater last saw on GitHub
            public BuildIdentity.Info Build;
            /// <summary>Null until the updater's first poll of this process has finished.</summary>
            public OmniUpdater.UpdaterStatus? Updater;

            // Logging. The three counts describe the retained buffer (it is capped), so they fall as old
            // entries are evicted; ErrorsLoggedTotal only ever rises while the process lives.
            public int TotalStatusLogs;
            public int TotalErrorLogs;
            public int TotalLogs;
            public int ErrorsLastHour;
            public long ErrorsLoggedTotal;

            // KliveAPI running totals (lifetime, persisted across restarts)
            public long ApiRequestsTotal;
            public long ApiServerErrorsTotal;

            // System RAM
            public double RamTotalGB;
            public double RamUsedGB;
            public double RamUsagePercentage;

            // System CPU
            public double CpuUsagePercentage;

            // Machine info
            public string OSVersion;
            public string MachineName;
            public int ProcessorCount;
            public string ProcessorName;

            // Process-level
            public double ProcessMemoryMB;
            public int ProcessThreadCount;
            public double GCTotalMemoryMB;
            public int GCGen0Collections;
            public int GCGen1Collections;
            public int GCGen2Collections;

            // Disk
            public DiskStats[] DiskStatistics;

            // Network
            public NetInterfaceStats[] NetworkInterfaces;

            // Services
            public int TotalServicesRegistered;
            public int TotalServicesActive;
            public ServiceSummary[] Services;

            // Scheduled tasks
            public int TotalScheduledTasks;
            public string NextTaskScheduledSummary;
        }

        public class DiskStats
        {
            public string DriveName;
            public string VolumeLabel;
            public double TotalSizeGB;
            public double FreeSpaceGB;
            public double UsedSpaceGB;
            public double UsagePercentage;
            public string DriveFormat;
        }

        public class NetInterfaceStats
        {
            public string Name;
            public string Description;
            public double SpeedMbps;
            public long BytesSent;
            public long BytesReceived;
            public bool IsVirtual;
        }

        public class ServiceSummary
        {
            public string Name;
            public string ServiceID;
            public bool IsActive;
            public TimeSpan Uptime;
            public string UptimeHumanized;
            public long UptimeSeconds;
            /// <summary>Low, Standard, High or Critical. A Critical service restarts itself after a crash.</summary>
            public string Anteriority;
            /// <summary>Does its work at startup and stops by design: inactive is its normal state, not a fault.</summary>
            public bool RunsOnce;
            // From the retained log buffer, so a service can be seen failing without opening the logs.
            public int ErrorsLastHour;
            public int ErrorsRetained;
            public DateTime? LastErrorAt;
        }
    }
}
