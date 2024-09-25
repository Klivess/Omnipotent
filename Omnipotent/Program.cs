global using static Omnipotent.Logging.OmniLogging;
global using static Omnipotent.Services.KliveAPI.KliveAPI;
using Humanizer;
using Omnipotent.Data_Handling;
using Omnipotent.Klives_Management.General_Analytics;
using Omnipotent.Logging;
using Omnipotent.Profiles;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.KliveLocalLLM;
using Omnipotent.Services.KliveTechHub;
using Omnipotent.Services.Notifications;
using Omnipotent.Services.Omniscience;
using Omnipotent.Services.OmniStartupManager;
using Omnipotent.Services.TestService;
using System;
using System.Drawing;

namespace Omnipotent
{
    public class Program
    {
        public static void Main(string[] args)
        {
            OmniLogging.LogStatusStatic("Main Thread", $"Omnipotent last updated {File.GetLastWriteTime(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Omnipotent.exe")).Humanize()}");
            OmniServiceManager omniServiceManager = new OmniServiceManager();
            try
            {
                //Create services
                omniServiceManager.CreateAndStartNewMonitoredOmniService(new KliveAPI());
                omniServiceManager.CreateAndStartNewMonitoredOmniService(new KliveBotDiscord());
                omniServiceManager.CreateAndStartNewMonitoredOmniService(new Omniscience());
                omniServiceManager.CreateAndStartNewMonitoredOmniService(new NotificationsService());
                omniServiceManager.CreateAndStartNewMonitoredOmniService(new KliveLocalLLM());
                omniServiceManager.CreateAndStartNewMonitoredOmniService(new GeneralBotStatisticsService());
                if (KliveTechHub.CheckIfBluetoothProtocolExistsOnDevice())
                {
                    omniServiceManager.CreateAndStartNewMonitoredOmniService(new KliveTechHub());
                }

                //Only works on my desktop, need to fix
                if (OmniPaths.CheckIfOnServer())
                {
                    omniServiceManager.GetKliveLocalLLMService().TerminateService();
                }

                Task.Delay(4000).Wait();

                if (OmniPaths.CheckIfOnServer())
                {
                    ((KliveBotDiscord)omniServiceManager.GetServiceByClassType<KliveBotDiscord>()[0]).SendMessageToKlives("Omnipotent online!");
                }
                //Error Handlers
                AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(async (sender, e) =>
                {
                    OmniLogging.LogErrorStatic("Main Thread", (Exception)e.ExceptionObject, "Unhandled Error!");
                    await omniServiceManager.GetKliveBotDiscordService().SendMessageToKlives(KliveBotDiscord.MakeSimpleEmbed("Unhandled error caught in main thread!",
                        new ErrorInformation((Exception)e.ExceptionObject).FullFormattedMessage, DSharpPlus.Entities.DiscordColor.Red));
                });
            }
            catch (Exception ex)
            {
                var errorinfo = new ErrorInformation(ex);
                OmniLogging.LogErrorStatic("Main Thread", ex, "Unhandled Error!");
                omniServiceManager.GetKliveBotDiscordService().SendMessageToKlives(KliveBotDiscord.MakeSimpleEmbed("Unhandled error caught in main thread!",
                    errorinfo.FullFormattedMessage, DSharpPlus.Entities.DiscordColor.Red)).Wait();
                ExistentialBotUtilities.RestartBot();
            }
        }

        private static void CurrentDomain_UnhandledException(Exception e)
        {
            //Notify Klives
            OmniLogging.LogErrorStatic("Main Thread", (Exception)e, "Unhandled Error!");
        }
    }
}