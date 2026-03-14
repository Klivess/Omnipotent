using ByteSizeLib;
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
    }
}
