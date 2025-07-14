global using static Omnipotent.Logging.OmniLogging;
global using static Omnipotent.Services.KliveAPI.KliveAPI;
using DSharpPlus.Entities;
using Humanizer;
using Json.More;
using Microsoft.PowerShell.Commands;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Klives_Management.General_Analytics;
using Omnipotent.Logging;
using Omnipotent.Profiles;
using Omnipotent.Service_Manager;
using Omnipotent.Services.CS2ArbitrageBot;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.KliveLocalLLM;
using Omnipotent.Services.KliveTechHub;
using Omnipotent.Services.Notifications;
using Omnipotent.Services.Omniscience;
using Omnipotent.Services.OmniStartupManager;
using Omnipotent.Services.OmniTrader;
using Omnipotent.Services.TestService;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.DevTools;
using OpenQA.Selenium.Support.UI;
using System;
using System.Drawing;
using System.Text.RegularExpressions;
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

                testTask();

                //Services to only execute on debug
                if (!OmniPaths.CheckIfOnServer())
                {
                    //omniServiceManager.CreateAndStartNewMonitoredOmniService(new TestService());
                    omniServiceManager.CreateAndStartNewMonitoredOmniService(new OmniTrader());
                }
                //omniServiceManager.CreateAndStartNewMonitoredOmniService(new CS2ArbitrageBot());

                if (OmniPaths.CheckIfOnServer())
                {
                    omniServiceManager.CreateAndStartNewMonitoredOmniService(new CS2ArbitrageBot());
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
                //ExistentialBotUtilities.RestartBot();
            }
        }
        public static async Task testTask()
        {

        }

        private static void CurrentDomain_UnhandledException(Exception e)
        {
            //Notify Klives
            OmniLogging.LogErrorStatic("Main Thread", (Exception)e, "Unhandled Error!");
        }
    }
}