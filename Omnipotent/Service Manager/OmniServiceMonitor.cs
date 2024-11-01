using ByteSizeLib;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

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
        // CPU usage counter
        private PerformanceCounter cpuCounter;

        // RAM usage counter
        private PerformanceCounter ramCounter;
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
                Thread ramAndCpuCounters = new(UpdateCPUandRAMCounters);
                ramAndCpuCounters.Start();
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Error in ServiceMain.");
            }
        }

        private async void UpdateCPUandRAMCounters()
        {
            try
            {
                cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                while (true)
                {
                    // Getting CPU usage
                    float cpuUsage = cpuCounter.NextValue();

                    long totalPhysicalMemory = 0;

                    // Query for physical memory
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            totalPhysicalMemory = Convert.ToInt64(obj["TotalPhysicalMemory"]);
                        }
                    }

                    // Convert to MB
                    double totalPhysicalMemoryInMB = totalPhysicalMemory / (1024 * 1024);

                    // Calculating used RAM
                    float usedRAM = (totalPhysicalMemory / (1024 * 1024)) - PerformanceInfo.GetPhysicalAvailableMemoryInMiB();

                    CPUUsagePercentage = (int)cpuUsage;
                    MemoryUsage = ByteSize.FromMegaBytes(usedRAM);
                    TotalSystemRAM = ByteSize.FromMegaBytes(totalPhysicalMemoryInMB);
                    // Sleep for 1 second before next measurement
                    await Task.Delay(2000);
                }
            }
            catch (Exception ex)
            {
                ServiceLogError(ex, "Couldn't get CPU and RAM usage, terminating service.");
                TerminateService();
            }
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

            public static Int64 GetPhysicalAvailableMemoryInMiB()
            {
                PerformanceInformation pi = new PerformanceInformation();
                if (GetPerformanceInfo(out pi, Marshal.SizeOf(pi)))
                {
                    return Convert.ToInt64((pi.PhysicalAvailable.ToInt64() * pi.PageSize.ToInt64() / 1048576));
                }
                else
                {
                    return -1;
                }

            }

            public static Int64 GetTotalMemoryInMiB()
            {
                PerformanceInformation pi = new PerformanceInformation();
                if (GetPerformanceInfo(out pi, Marshal.SizeOf(pi)))
                {
                    return Convert.ToInt64((pi.PhysicalTotal.ToInt64() * pi.PageSize.ToInt64() / 1048576));
                }
                else
                {
                    return -1;
                }

            }
        }
    }
}
