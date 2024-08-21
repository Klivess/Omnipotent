global using static Omnipotent.Logging.OmniLogging;
global using static Omnipotent.Services.KliveAPI.KliveAPI;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using Omnipotent.Profiles;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.Notifications;
using Omnipotent.Services.Omniscience;
using Omnipotent.Services.OmniStartupManager;
using Omnipotent.Services.TestService;
using System;

namespace Omnipotent
{
    public class Program
    {
        public static void Main(string[] args)
        {
            try
            {
                //Error Handlers
                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

                OmniServiceManager omniServiceManager = new OmniServiceManager();
                //Create services
                omniServiceManager.CreateAndStartNewMonitoredOmniService(new KliveAPI());
                omniServiceManager.CreateAndStartNewMonitoredOmniService(new KliveBotDiscord());
                omniServiceManager.CreateAndStartNewMonitoredOmniService(new Omniscience());
                omniServiceManager.CreateAndStartNewMonitoredOmniService(new NotificationsService());
                omniServiceManager.CreateAndStartNewMonitoredOmniService(new KMProfileManager());

                Task.Delay(4000).Wait();

                if (OmniPaths.CheckIfOnServer())
                {
                    ((KliveBotDiscord)omniServiceManager.GetServiceByClassType<KliveBotDiscord>()[0]).SendMessageToKlives("Omnipotent online!");
                }

                //Main thread keep-alive very hacky probably wont cause problems hopefully probably
                Task.Delay(-1).Wait();
            }
            catch (Exception ex)
            {
                CurrentDomain_UnhandledException(ex);
                ExistentialBotUtilities.RestartBot();
            }
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            //Notify Klives
            OmniLogging.LogErrorStatic("Main Thread", (Exception)e.ExceptionObject, "Unhandled Error!");
        }

        private static void CurrentDomain_UnhandledException(Exception e)
        {
            //Notify Klives
            OmniLogging.LogErrorStatic("Main Thread", (Exception)e, "Unhandled Error!");
        }
    }
}