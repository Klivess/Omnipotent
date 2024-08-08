using ByteSizeLib;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using Omnipotent.Services.OmniStartupManager;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography.X509Certificates;
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
        public readonly List<OmniService> activeServices;
        public DataUtil fileHandlerService;
        private OmniServiceMonitor monitor;
        public TimeManager timeManager;
        public OmniLogging logger;


        public OmniServiceManager()
        {
            //Initialise in order of priority

            activeServices = new List<OmniService>();
            //Logger
            logger = new();
            logger.ReplaceDataHandler(this.fileHandlerService);
            logger.ReplaceDataManager(this);
            logger.ServiceStart();
            //Instantiate file handler service
            fileHandlerService = new DataUtil();
            fileHandlerService.ReplaceDataManager(this);
            fileHandlerService.ReplaceDataHandler(this.fileHandlerService);
            fileHandlerService.ServiceStart();
            //Instantiate service performance monitor
            monitor = new OmniServiceMonitor();
            monitor.ReplaceDataHandler(this.fileHandlerService);
            monitor.ReplaceDataManager(this);
            monitor.ServiceStart();
            //Create prerequisite items
            var manager = new OmniStartupManager();
            CreateAndStartNewMonitoredOmniService(manager);
            //Tight loop
            //---TODO--: replace this
            while (manager.IsServiceActive()) { Task.Delay(100); }
            //Initialising time manager
            timeManager = new();
            timeManager.ReplaceDataHandler(this.fileHandlerService);
            timeManager.ReplaceDataManager(this);
            timeManager.ServiceStart();
        }
        public void CreateAndStartNewMonitoredOmniService(OmniService service)
        {
            service.ReplaceDataHandler(fileHandlerService);
            service.ReplaceDataManager(this);
            service.ServiceStart();
            activeServices.Add(service);
            monitor.SetServiceToMonitor(service);
        }
        public OmniService GetServiceByID(string id)
        {
            try
            {
                var services = activeServices.Where(k => k.serviceID == id);
                return services.ElementAt(0);
            }
            catch (Exception ex)
            {
                logger.LogError("Omni Service Manager", ex, "Couldn't get OmniService by ID");
            }
            return null;
        }

        public OmniService[] GetServiceByClassType<T>()
        {
            try
            {
                var services = activeServices.Where(k => k.GetType().Name == typeof(T).Name);
                return services.ToArray();
            }
            catch (Exception ex)
            {
                logger.LogError("Omni Service Manager", ex, "Couldn't get OmniService by class");
            }
            return null;
        }

        [Route("api/omniservicemanager")]
        [Controller]
        public class OmniServiceManagerController
        {
            [HttpGet("GetAllOmniServices")]
            public IActionResult GetAllOmniServices()
            {
                try
                {
                    return new OkObjectResult(JsonConvert.SerializeObject(GetAllOmniServices(), Formatting.Indented));
                }
                catch (Exception ex)
                {
                    LogErrorStatic("Omni Service Manager", ex, "Error fulfilling GetAllOmniServices endpoint request.");
                    return new ContentResult { Content = OmniLogging.FormatErrorMessage(new OmniLogging.ErrorInformation(ex)), StatusCode = (int)HttpStatusCode.InternalServerError };
                }
            }
        }
    }
    public class OmniServiceMonitor : OmniService
    {
        public OmniServiceMonitor()
        {
            name = "Omnipotent Service Monitoring";
            threadAnteriority = ThreadAnteriority.Standard;
        }
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
        List<OmniMonitoredThread> monitoredThreads = new();
        public void SetServiceToMonitor(OmniService omniMonitored)
        {
            try
            {
                var monitored = ConvertOmniServiceToOmniMonitoredThread(omniMonitored);
                monitoredThreads.Add(monitored);
            }
            catch (Exception ex)
            {
                serviceManager.logger.LogError("Omni Service Monitor", ex, "Error setting service to monitor.");
            }
        }
        public List<OmniMonitoredThread> GetCurrentServicesBeingMonitored { get { return monitoredThreads; } }
        protected override async void ServiceMain()
        {
            Process myProcess = Process.GetCurrentProcess();
            ProcessThreadCollection threads = myProcess.Threads;
            foreach (ProcessThread thread in threads)
            {
                if (monitoredThreads.Select(k => k.ManagedThreadID).Contains(thread.Id))
                {
                    //LogStatus(name, "Found thread!");
                }
                else
                {
                    //LogStatus(name, $"Did not find thread {thread.Id}. Monitored Threads: {string.Join("-", monitoredThreads.Select(k=>k.ManagedThreadID))}");
                }
            }
            await Task.Delay(3000);
            GC.Collect();
            ServiceMain();
        }
    }
}
