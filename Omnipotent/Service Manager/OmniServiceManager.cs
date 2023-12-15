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
        Low,
        Standard,
        High,
        Critical,
    }
    public class OmniServiceManager
    {
        List<OmniService> activeServices;
        DataUtil fileHandlerService;
        private OmniServiceMonitor monitor;
        
        public OmniServiceManager()
        {
            activeServices = new List<OmniService>();
            fileHandlerService = new DataUtil();
            monitor = new OmniServiceMonitor();
            monitor.ReplaceDataHandler(this.fileHandlerService);
            monitor.ReplaceDataManager(this);
            fileHandlerService.ServiceStart();
            monitor.ServiceStart();
        }

        public void CreateAndStartNewMonitoredOmniService(OmniService service)
        {
            service.ReplaceDataHandler(fileHandlerService);
            service.ReplaceDataManager(this);
            service.ServiceStart();
            activeServices.Add(service);
        }

        public OmniService GetServiceByID(string id)
        {
            try
            {
                var services = activeServices.Where(k => k.serviceID == id);
                return services.ElementAt(0);
            }
            catch(Exception ex)
            {
                LogError(ex, "Couldn't get OmniService by ID");
            }
            return null;
        }
    }
    public class OmniServiceMonitor : OmniService
    {
        public struct OmniMonitoredThread
        {
            public string name;
            public Thread monitoredThread;
            public int cpuUsage;
            public int totalFileOperations;
        }
        public static OmniMonitoredThread ConvertOmniServiceToOmniMonitoredThread(OmniService service)
        {
            OmniMonitoredThread omniMonitoredThread = new OmniMonitoredThread();
            omniMonitoredThread.name = service.GetName();
            omniMonitoredThread.monitoredThread = service.GetThread();
            omniMonitoredThread.cpuUsage = -1;
            return omniMonitoredThread;
        }
        List<OmniMonitoredThread> monitoredThreads;
        public void SetServiceToMonitor(OmniMonitoredThread omniMonitored) 
        { 
            monitoredThreads.Add(omniMonitored);
        }
        public List<OmniMonitoredThread> GetCurrentServicesBeingMonitored{ get { return monitoredThreads; } }
        protected override void ServiceMain()
        {
            foreach (var item in monitoredThreads)
            {

            }
        }
    }
}
