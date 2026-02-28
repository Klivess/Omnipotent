using Humanizer;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using Omnipotent.Service_Manager;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;

namespace Omnipotent.Klives_Management.General_Analytics
{
    public class GeneralBotStatisticsService : OmniService
    {
        public FrontPageStatistics fpstats;
        private GeneralBotStatisticsServiceRoutes routes;

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
                    stats.BotUptime = serviceManager.GetOverallUptime();
                    stats.BotUptimeHumanized = stats.BotUptime.Humanize();

                    // --- Logging ---
                    var logger = serviceManager.GetLogger();
                    var messages = logger.overallMessages;
                    stats.TotalLogs = messages.Count;
                    int statusCount = 0, errorCount = 0;
                    for (int i = 0; i < messages.Count; i++)
                    {
                        var t = messages[i].type;
                        if (t == OmniLogging.LogType.Status) statusCount++;
                        else if (t == OmniLogging.LogType.Error) errorCount++;
                    }
                    stats.TotalStatusLogs = statusCount;
                    stats.TotalErrorLogs = errorCount;

                    // --- System Hardware (from OmniServiceMonitor) ---
                    try
                    {
                        var monitor = serviceManager.GetMonitor();
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
                    try
                    {
                        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                            .ToArray();
                        stats.NetworkInterfaces = new NetInterfaceStats[interfaces.Length];
                        for (int i = 0; i < interfaces.Length; i++)
                        {
                            var ni = interfaces[i];
                            var ipStats = ni.GetIPv4Statistics();
                            stats.NetworkInterfaces[i] = new NetInterfaceStats
                            {
                                Name = ni.Name,
                                Description = ni.Description,
                                SpeedMbps = ni.Speed / 1_000_000.0,
                                BytesSent = ipStats.BytesSent,
                                BytesReceived = ipStats.BytesReceived
                            };
                        }
                    }
                    catch { stats.NetworkInterfaces = []; }

                    // --- Active services ---
                    try
                    {
                        var services = serviceManager.activeServices;
                        stats.TotalServicesRegistered = services.Count;
                        int active = 0;
                        var summaries = new List<ServiceSummary>(services.Count);
                        for (int i = 0; i < services.Count; i++)
                        {
                            var svc = services[i];
                            bool isActive = svc.IsServiceActive();
                            if (isActive) active++;
                            summaries.Add(new ServiceSummary
                            {
                                Name = svc.GetName(),
                                ServiceID = svc.serviceID,
                                IsActive = isActive,
                                Uptime = isActive ? svc.GetServiceUptime() : TimeSpan.Zero,
                                UptimeHumanized = isActive ? svc.GetServiceUptime().Humanize() : "Inactive"
                            });
                        }
                        stats.TotalServicesActive = active;
                        stats.Services = summaries.ToArray();
                    }
                    catch { stats.Services = []; }

                    // --- Scheduled tasks ---
                    try
                    {
                        var tasks = serviceManager.GetTimeManager().tasks;
                        stats.TotalScheduledTasks = tasks.Count;
                        var nextTask = tasks.Where(t => !t.HasTaskTimePassed()).OrderBy(t => t.dateTimeDue).FirstOrDefault();
                        if (nextTask != null)
                        {
                            stats.NextTaskScheduledSummary = $"{nextTask.taskName} due {nextTask.dateTimeDue:g} ({nextTask.GetTimespanRemaining().Humanize()} remaining)";
                        }
                    }
                    catch { }

                    stats.TimeStatisticsGenerated = DateTime.Now;

                    // Atomic swap – readers always see a complete snapshot
                    fpstats = stats;
                }
                catch (Exception ex)
                {
                    ServiceLogError(ex);
                }

                await Task.Delay(5000);
            }
        }

        // Helper to get ready drives without throwing on unavailable ones
        private static class DriveInfo
        {
            public static System.IO.DriveInfo[] GetReady()
            {
                return System.IO.DriveInfo.GetDrives().Where(d => d.IsReady).ToArray();
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

            // Logging
            public int TotalStatusLogs;
            public int TotalErrorLogs;
            public int TotalLogs;

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

            // KliveGadgets connected
            public string[] ConnectedKliveGadgets;

            // Omniscience
            public int TotalOmniDiscordMessagesLogged;
            public int TotalOmniDiscordImagesLogged;
            public int TotalOmniDiscordVideosLogged;
            public int TotalOmniDiscordFilesLogged;
            public int OmniDiscordMessagesLoggedToday;

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
        }

        public class ServiceSummary
        {
            public string Name;
            public string ServiceID;
            public bool IsActive;
            public TimeSpan Uptime;
            public string UptimeHumanized;
        }
    }
}
