using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Razor.Tokenizer.Symbols;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using static Omnipotent.Threading.WindowsInvokes;

namespace Omnipotent.Service_Manager
{

    public class OmniService
    {
        protected ThreadAnteriority threadAnteriority;
        protected string name;
        public string serviceID;
        private Thread serviceThread;
        public OmniServiceManager serviceManager;
        protected Stopwatch serviceUptime;

        protected event Action ServiceQuitRequest;

        private bool ServiceActive;
        protected CancellationTokenSource cancellationToken;
        public void ReplaceDataManager(OmniServiceManager manager)
        {
            serviceManager = manager;
        }
        public string GetName() { return name; }
        public TimeSpan GetServiceUptime() { return serviceUptime.Elapsed; }
        public ThreadAnteriority GetThreadAnteriority() { return threadAnteriority; }
        public bool IsServiceActive() { return ServiceActive; }
        public Thread GetThread()
        {
            return serviceThread;
        }
        public async Task<LoggedMessage> ServiceLog(string message, bool appearInConsole = true)
        {
            return serviceManager.GetLogger().LogStatus(name, message, appearInConsole);
        }
        public async Task<LoggedMessage> ServiceLogError(string error, bool appearInConsole = true)
        {
            return serviceManager.GetLogger().LogError(name, error, appearInConsole);
        }
        public async Task<LoggedMessage> ServiceLogError(Exception error, string specialMessage = "", bool appearInConsole = true)
        {
            return serviceManager.GetLogger().LogError(name, error, specialMessage, appearInConsole);
        }
        public async Task ServiceUpdateLoggedMessage(LoggedMessage message, string newMessage)
        {
            serviceManager.GetLogger().UpdateLogMessage(message, newMessage);
        }
        public async Task CreateScheduledTimeTask(DateTime duedateTime, string taskname, string reason = "", bool important = true, Action embeddedAction = null)
        {
            serviceManager.GetTimeManager().CreateNewScheduledTask(duedateTime, taskname, name, serviceID, reason, important, embeddedAction);
        }
        public ref DataUtil GetDataHandler()
        {
            while (serviceManager.fileHandlerService.IsServiceActive() == false) { Task.Delay(100); }
            return ref serviceManager.fileHandlerService;
        }

        //intialise OmniService, don't actually use this here this class is meant to be a "template" to derive from.
        public OmniService(string name, ThreadAnteriority anteriority)
        {
            this.serviceID = RandomGeneration.GenerateRandomLengthOfNumbers(8);
            this.name = name;
            this.threadAnteriority = anteriority;
        }

        //DO NOT USE THIS (outside of this class)!!
        private Action<object> Convert<T>(Action<T> myActionT)
        {
            if (myActionT == null) return null;
            else return new Action<object>(o => myActionT((T)o));
        }
        public void ServiceStart()
        {
            ServiceQuitRequest = new Action(() => { });
            if (string.IsNullOrEmpty(name))
            {
                name = "Deduced Service " + this.GetType().Name;
                threadAnteriority = ThreadAnteriority.Low;
                ServiceLog("Service is unnamed, so filling in deduced name.");
            }
            if (ServiceActive)
            {
                ServiceLog("Service is already active.");
            }
            else
            {
                //serviceTask = Task.Run(ServiceMain, cancellationToken.Token);
                serviceUptime = Stopwatch.StartNew();
                serviceThread = new Thread(() =>
                {
                    try
                    {
                        try
                        {
                            //Thread Error Handler
                            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(async (sender, e) =>
                            {
                                ServiceLogError((Exception)e.ExceptionObject, "Thread Unhandled Error!");
                                await serviceManager.GetKliveBotDiscordService().SendMessageToKlives(KliveBotDiscord.MakeSimpleEmbed($"Unhandled error caught in {name} service!",
                                    new ErrorInformation((Exception)e.ExceptionObject).FullFormattedMessage, DSharpPlus.Entities.DiscordColor.Red));
                            });
                            ServiceMain(); Task.Delay(-1).Wait();
                        }
                        catch (ThreadInterruptedException interrupt) { }
                    }
                    catch (Exception ex)
                    {
                        CatchError(ex);
                    }
                });
                serviceThread.Name = "OmniServiceThread_" + name;
                serviceThread.Start();
                ServiceActive = true;
                serviceThread.Priority = (ThreadPriority)(threadAnteriority + 1);
                ServiceLog("Started service.");
            }
        }

        //Meant to intentionally be overridden by child classes
        protected virtual void ServiceMain() { }

        protected void CatchError(Exception ex)
        {
            //Replace with proper error handling.
            ServiceLogError(new Exception($"ERROR! Task {name} has crashed due to: " + ex.Message.ToString()));
        }

        public OmniService()
        {
            cancellationToken = new CancellationTokenSource();
        }

        public async Task<bool> TerminateService()
        {
            try
            {
                ServiceQuitRequest.Invoke();
                Task.Delay(500).Wait();
                ServiceLog("Ending " + name + " service.");
                ServiceActive = false;
                GC.Collect();
                serviceThread.Interrupt();
                serviceUptime = new Stopwatch();
                return true;
            }
            catch (Exception ex)
            {
                CatchError(ex);
                return false;
            }
        }

        public async Task<bool> RestartService()
        {
            try
            {
                TerminateService();
                //Bit odd
                ServiceStart();
                return true;
            }
            catch (Exception ex)
            {
                CatchError(ex);
                return false;
            }
        }

    }
}
