using ByteSizeLib;
using System.Diagnostics;
using System.Management;

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
        private PerformanceCounter cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");

        // RAM usage counter
        private PerformanceCounter ramCounter = new PerformanceCounter("Memory", "Available MBytes");
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
                while (true)
                {
                    // Getting CPU usage
                    float cpuUsage = cpuCounter.NextValue();

                    // Getting Available RAM in MB
                    float availableRAM = ramCounter.NextValue();

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
                    float usedRAM = (totalPhysicalMemory / (1024 * 1024)) - availableRAM;

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
    }
}
