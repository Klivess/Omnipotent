global using static Omnipotent.Logging.OmniLogging;
global using static Omnipotent.Services.KliveAPI.KliveAPI;
using DSharpPlus.Entities;
using Humanizer;
using Microsoft.PowerShell.Commands;
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
using static System.Net.Mime.MediaTypeNames;

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

                //Error Handlers
                AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(async (sender, e) =>
                {
                    OmniLogging.LogErrorStatic("Main Thread", (Exception)e.ExceptionObject, "Unhandled Error!");
                    (await omniServiceManager.GetKliveBotDiscordService()).SendMessageToKlives(KliveBotDiscord.MakeSimpleEmbed("Unhandled error caught in main thread!",
                        new ErrorInformation((Exception)e.ExceptionObject).FullFormattedMessage, DSharpPlus.Entities.DiscordColor.Red));
                });

                omniServiceManager.CreateAndStartNewMonitoredOmniService(new Omniscience());
                omniServiceManager.CreateAndStartNewMonitoredOmniService(new NotificationsService());
                omniServiceManager.CreateAndStartNewMonitoredOmniService(new GeneralBotStatisticsService());
                //omniServiceManager.CreateAndStartNewMonitoredOmniService(new KliveLocalLLM());
                if (KliveTechHub.CheckIfBluetoothProtocolExistsOnDevice())
                {
                    omniServiceManager.CreateAndStartNewMonitoredOmniService(new KliveTechHub());
                }

                //Services to only execute on debug
                if (!OmniPaths.CheckIfOnServer())
                {
                }

                Task.Delay(4000).Wait();

                if (OmniPaths.CheckIfOnServer())
                {
                    ((KliveBotDiscord)(omniServiceManager.GetServiceByClassType<KliveBotDiscord>().GetAwaiter().GetResult())[0]).SendMessageToKlives("Omnipotent online!");
                }
                if (args.Any())
                {
                    OmniLogging.LogStatusStatic("Arguments Passed: ", string.Join(", ", args));
                    if (args[0].Trim().StartsWith("errorOccurred"))
                    {
                        string pathOfErrorFile = args[0].Replace("errorOccurred=", "");
                        //Get file created time of that file
                        DateTime fileCreatedTime = File.GetCreationTime(pathOfErrorFile);
                        string errorMessage = $"Omnipotent process crashed at {fileCreatedTime.Humanize()}. Error log file created at: {pathOfErrorFile}";
                        OmniLogging.LogStatusStatic("Main Thread", errorMessage);
                        DiscordMessageBuilder embedBuilder = KliveBotDiscord.MakeSimpleEmbed("Omnipotent Process Monitor Error",
                            errorMessage, DSharpPlus.Entities.DiscordColor.Red);
                        FileStream fileStream = new FileStream(pathOfErrorFile, FileMode.Open, FileAccess.Read);
                        embedBuilder.AddFile("Error Log", fileStream);
                        ((KliveBotDiscord)(omniServiceManager.GetServiceByClassType<KliveBotDiscord>().GetAwaiter().GetResult())[0]).SendMessageToKlives(embedBuilder);
                    }
                }
            }
            catch (Exception ex)
            {
                var errorinfo = new ErrorInformation(ex);
                OmniLogging.LogErrorStatic("Main Thread", ex, "Unhandled Error!");
                omniServiceManager.GetKliveBotDiscordService().GetAwaiter().GetResult().SendMessageToKlives(KliveBotDiscord.MakeSimpleEmbed("Unhandled error caught in main thread!",
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