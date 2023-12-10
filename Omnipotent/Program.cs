global using static Omnipotent.Logging.OmniLogging;

using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.OmniStartupManager;
using Omnipotent.Services.TestService;
using System;

namespace Omnipotent
{
    public class Program
    {
        public static void Main(string[] args)
        {
            OmniServiceManager omniServiceManager = new OmniServiceManager();
            omniServiceManager.CreateAndStartNewMonitoredOmniService(new KliveAPI());
            omniServiceManager.CreateAndStartNewMonitoredOmniService(new TestService());
            omniServiceManager.CreateAndStartNewMonitoredOmniService(new KliveBotDiscord());
            omniServiceManager.CreateAndStartNewMonitoredOmniService(new OmniStartupManager());

            //Main thread keep-alive very hacky probably wont cause problems hopefully probably
            Task.Delay(-1).Wait();
        }
    }
} 