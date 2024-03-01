using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using static Omnipotent.Threading.WindowsInvokes;

namespace Omnipotent.Service_Manager
{

    public class OmniService
    {
        protected ThreadAnteriority threadAnteriority;
        protected string name;
        public string serviceID;
        private Thread serviceThread;
        protected DataUtil dataHandler;
        protected OmniServiceManager serviceManager;

        private bool ServiceActive;
        protected CancellationTokenSource cancellationToken;

        public void ReplaceDataHandler(DataUtil util)
        {
            dataHandler = util;
        }
        public void ReplaceDataManager(OmniServiceManager manager)
        {
            serviceManager = manager;
        }
        public string GetName() { return name; }
        public ThreadAnteriority GetThreadAnteriority() {  return threadAnteriority; }
        public bool IsServiceActive() { return ServiceActive; }
        public Thread GetThread()
        {
            return serviceThread;
        }


        //intialise OmniService, don't actually use this here this class is meant to be a "template" to derive from.
        public OmniService(string name, ThreadAnteriority anteriority)
        {
            this.serviceID = RandomGeneration.GenerateRandomLengthOfNumbers(8);
            this.name = name;
            this.threadAnteriority = anteriority;
        }

        //DO NOT USE THIS (outside of this class)!!
        public Action<object> Convert<T>(Action<T> myActionT)
        {
            if (myActionT == null) return null;
            else return new Action<object>(o => myActionT((T)o));
        }
        public void ServiceStart()
        {
            if (string.IsNullOrEmpty(name))
            {
                name="Random Service "+RandomGeneration.GenerateRandomLengthOfNumbers(8);
                threadAnteriority = ThreadAnteriority.Low;
                serviceManager.logger.LogStatus(name, "Service is unnamed, so filling in random name.");
            }
            if (ServiceActive)
            {
                serviceManager.logger.LogStatus(name, "Service is already active.");                
            }
            else
            {
                //serviceTask = Task.Run(ServiceMain, cancellationToken.Token);
                serviceThread = new Thread(() =>
                {
                    try { try { ServiceMain(); Task.Delay(-1); } catch (ThreadInterruptedException interrupt) { } } catch (Exception ex) { CatchError(ex); }
                });
                serviceThread.Start();
                ServiceActive = true;
                serviceThread.Priority = (ThreadPriority)(threadAnteriority+1);
                serviceManager.logger.LogStatus(name, "Started service.");
            }
        }

        //Meant to intentionally be overridden by child classes
        protected virtual void ServiceMain() { }

        protected void CatchError(Exception ex)
        {
            //Replace with proper error handling.
            serviceManager.logger.LogStatus(name, $"ERROR! Task {name} has crashed due to: "+ex.Message.ToString());   
        }

        public OmniService()
        {
            cancellationToken = new CancellationTokenSource();
        }

        public bool TerminateService()
        {
            try
            {
                serviceManager.logger.LogStatus(name, "Ending " + name + " service.");
                ServiceActive = false;
                serviceThread.Interrupt();
                return true;
            }
            catch (Exception ex)
            {
                CatchError(ex);
                return false;
            }
        }

        public bool RestartService()
        {
            try
            {
                TerminateService();
                //Bit odd
                ServiceStart();
                return true;
            }
            catch(Exception ex)
            {
                CatchError(ex);
                return false;
            }
        }

    }
}
