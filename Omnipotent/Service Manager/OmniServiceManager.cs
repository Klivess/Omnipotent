using ByteSizeLib;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.KliveLocalLLM;
using Omnipotent.Services.Notifications;
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
using System.Web.Http.Filters;

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
        public List<OmniService> activeServices;
        public DataUtil fileHandlerService;
        private OmniServiceMonitor monitor;
        public TimeManager timeManager;
        protected OmniLogging logger;
        private Stopwatch OverallUptime;
        public OmniServiceManager()
        {
            //Initialise in order of priority
            OverallUptime = Stopwatch.StartNew();
            activeServices = new List<OmniService>();
            //Logger
            logger = new();
            logger.ReplaceDataManager(this);
            logger.ServiceStart();
            //Instantiate file handler service
            fileHandlerService = new DataUtil();
            fileHandlerService.ReplaceDataManager(this);
            fileHandlerService.ServiceStart();
            //Instantiate service performance monitor
            monitor = new OmniServiceMonitor();
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
            timeManager.ReplaceDataManager(this);
            timeManager.ServiceStart();
        }
        public TimeSpan GetOverallUptime()
        {
            return OverallUptime.Elapsed;
        }
        public bool CreateAndStartNewMonitoredOmniService(OmniService service, bool overrideDuplicatedServiceCheck = false)
        {
            if (GetServiceByName(service.GetName()) == null || overrideDuplicatedServiceCheck == true)
            {
                service.ReplaceDataManager(this);
                service.ServiceStart();
                activeServices.Add(service);
                monitor.SetServiceToMonitor(service);
                return true;
            }
            else
            {
                return false;
            }
        }
        public ref OmniLogging GetLogger()
        {
            while (logger.IsServiceActive() == false) { Task.Delay(100).Wait(); }
            return ref logger;
        }
        public ref OmniServiceMonitor GetMonitor()
        {
            while (monitor.IsServiceActive() == false) { Task.Delay(100).Wait(); }
            return ref monitor;
        }
        public ref TimeManager GetTimeManager()
        {
            while (timeManager.IsServiceActive() == false) { Task.Delay(100).Wait(); }
            return ref timeManager;
        }

        public KliveLocalLLM GetKliveLocalLLMService()
        {
            return (KliveLocalLLM)(GetServiceByClassType<KliveLocalLLM>()[0]);
        }
        public OmniService GetServiceByID(string id)
        {
            try
            {
                var services = activeServices.Where(k => k.serviceID == id).First();
                return services;
            }
            catch (Exception ex)
            {
                logger.LogError("Omni Service Manager", ex, "Couldn't get OmniService by ID");
                throw new Exception("Couldn't get OmniService by ID");
            }
        }
        public OmniService[] GetServiceByClassType<T>()
        {
            try
            {
                while (true)
                {
                    var ser = activeServices.Where(k => k.GetType().Name == typeof(T).Name);
                    if (ser.Any())
                    {
                        break;
                    }
                }
                var services = activeServices.Where(k => k.GetType().Name == typeof(T).Name);
                return services.ToArray();
            }
            catch (Exception ex)
            {
                logger.LogError("Omni Service Manager", ex, "Couldn't get OmniService by class");
                return GetServiceByClassType<T>();
            }
            return null;
        }

        public KliveAPI GetKliveAPIService()
        {

            var service = (KliveAPI)(GetServiceByClassType<KliveAPI>()[0]);
            while (service.listener.IsListening != true) { Task.Delay(100).Wait(); }
            return service;
        }
        public KliveBotDiscord GetKliveBotDiscordService()
        {
            return (KliveBotDiscord)(GetServiceByClassType<KliveBotDiscord>()[0]);
        }

        public NotificationsService GetNotificationsService()
        {
            return (NotificationsService)(GetServiceByClassType<NotificationsService>()[0]);
        }

        public OmniService GetServiceByName(string name)
        {
            var result = activeServices.Where(k => k.GetName().ToLower() == name.ToLower()).ToArray();
            if (result.Any())
            {
                return result[0];
            }
            else
            {
                return null;
            }
        }
    }
}
