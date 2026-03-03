using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using OpenQA.Selenium.Chrome;
using SteamKit2;
using SteamKit2.GC.CSGO.Internal;
using System.Collections.Concurrent;

namespace Omnipotent.Services.SeleniumManager
{
    public class SeleniumManager : OmniService
    {
        private SeleniumManagerRoutes routes;
        private ConcurrentBag<SeleniumObject> currentActiveSeleniumInstances;

        public class SeleniumObject
        {
            public ulong objectID;
            public string name;
            public DateTime createdAt;

            [JsonIgnore]            
            private ChromeOptions options;
            [JsonIgnore]
            private ChromeDriver? driver;
            [JsonIgnore]
            private DateTime lastActivity;
            [JsonIgnore]
            private TimeSpan worstCaseSessionDuration;
            [JsonIgnore]
            private CancellationTokenSource? inactivityCts;

            public SeleniumObject(TimeSpan? worstCaseSessionDuration = null)
            {
                this.worstCaseSessionDuration = worstCaseSessionDuration ?? TimeSpan.FromHours(3);
                options = new ChromeOptions();
                createdAt = DateTime.Now;
                lastActivity = DateTime.Now;
                options.SetLoggingPreference(OpenQA.Selenium.LogType.Browser, OpenQA.Selenium.LogLevel.Off);
                options.AddArguments("--disable-logging");
                options.AddArguments("--silent");
                options.AddArguments("--log-level=3");
                //hopefully reduce CPU load
                options.AddArguments("--no-sandbox");

                if (OmniPaths.CheckIfOnServer())
                {
                    options.AddArguments("--headless");
                }

                StartInactivityMonitor();
            }

            public void AddArgumentToOptions(string argument)
            {
                options.AddArgument(argument);
            }

            public ChromeDriver UseChromeDriver()
            {
                lastActivity = DateTime.Now;
                if(driver is null)
                {
                    driver = new ChromeDriver(options);
                }
                return driver;
            }

            internal async void CloseDriver()
            {
                inactivityCts?.Cancel();
                inactivityCts?.Dispose();
                inactivityCts = null;
                if (driver is not null)
                {
                    driver.Quit();
                    driver.Dispose();
                    driver = null;
                }
            }

            private void StartInactivityMonitor()
            {
                inactivityCts = new CancellationTokenSource();
                var token = inactivityCts.Token;
                Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
                        if (DateTime.Now - lastActivity >= worstCaseSessionDuration)
                        {
                            CloseDriver();
                            break;
                        }
                    }
                }, token);
            }
        }

        public SeleniumManager()
        {
            name = "SeleniumManager";
            threadAnteriority = ThreadAnteriority.High;
        }

        protected override async void ServiceMain()
        {
            routes = new SeleniumManagerRoutes(this);
            currentActiveSeleniumInstances = new ConcurrentBag<SeleniumObject>();
            routes.CreateRoutes();
        }

        public SeleniumObject CreateSeleniumObject(string name, TimeSpan? worstCaseSessionDuration = null)
        {
            var newSeleniumObject = new SeleniumObject(worstCaseSessionDuration);
            newSeleniumObject.objectID = (ulong)DateTime.Now.Ticks;
            newSeleniumObject.name = name;
            currentActiveSeleniumInstances.Add(newSeleniumObject);
            return newSeleniumObject;
        }

        public List<SeleniumObject> GetCurrentActiveSeleniumInstances()
        {
            return currentActiveSeleniumInstances.ToList();
        }

        public List<SeleniumObject> GetSeleniumInstancesByID(ulong objectID)
        {
            return currentActiveSeleniumInstances.Where(x => x.objectID == objectID).ToList();
        }

        public List<SeleniumObject> GetSeleniumInstancesByName(string name)
        {
            return currentActiveSeleniumInstances.Where(x => x.name == name).ToList();
        }

        public void StopUsingSeleniumObject(SeleniumObject seleniumObject)
        {
            var selenium = currentActiveSeleniumInstances.TryTake(out seleniumObject);
            if (selenium)
            {
                seleniumObject.CloseDriver();
            }
        }
    }
}
