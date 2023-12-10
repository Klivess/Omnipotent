using Omnipotent.Data_Handling;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Service_Manager
{

    public enum ThreadAnteriority
    {
        Critical,
        High,
        Standard,
        Low,
    }
    public class OmniServiceManager
    {
        List<OmniService> activeServices;
        DataUtil fileHandlerService;
        public OmniServiceManager()
        {
            activeServices = new List<OmniService>();
            fileHandlerService = new DataUtil();
            fileHandlerService.ServiceStart();
        }

        public void CreateAndStartNewMonitoredOmniService(OmniService service)
        {
            service.ReplaceDataHandler(fileHandlerService);
            service.ServiceStart();
            activeServices.Add(service);
        }

        public OmniService GetServiceByID(string id)
        {
            try
            {
                
            }
            catch(Exception ex)
            {
                LogError(ex, "Couldn't get OmniService by ID");
            }
            return null;
        }

        public void BeginPerformanceMonitoring()
        {

        }
    }
    /*
    public class OmniServiceMonitor : OmniService
    {
        public struct OmniMonitoredThread
        {
            public string name;
            public Thread monitoredThread;
            public int cpuUsage;
        }

        public static OmniMonitoredThread ConvertOmniServiceToOmniMonitoredThread(OmniService service)
        {
            OmniMonitoredThread omniMonitoredThread = new OmniMonitoredThread();
            omniMonitoredThread.name = service.GetName();
            omniMonitoredThread.threadID = service.;
            omniMonitoredThread.cpuUsage = -1;
        }
        List<OmniMonitoredThread> monitoredThreads;

        private bool IsMonitoring;
        public void SetServicesToMonitor(string name, long threadID) 
        { 
            OmniMonitoredThread omniMonitoredThread = new OmniMonitoredThread();
            omniMonitoredThread.name = name;
            omniMonitoredThread.threadID = threadID;
            omniMonitoredThread.cpuUsage = -1;
            monitoredThreads.Add(omniMonitoredThread);
        }
        public List<OmniMonitoredThread> GetCurrentServicesBeingMonitored{ get { return monitoredThreads; } }

        public void BeginMonitoring()
        {
            foreach (var item in monitoredThreads)
            {
                Thread.
            }
        }
        public void StopMonitoring()
        {

        }
    }
    */
}
