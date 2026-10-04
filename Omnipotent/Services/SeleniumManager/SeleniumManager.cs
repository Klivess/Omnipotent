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
        // Keyed by reference so StopUsingSeleniumObject removes exactly the instance it was given.
        private ConcurrentDictionary<SeleniumObject, byte> currentActiveSeleniumInstances = new();

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

            /// <summary>Full access to the options (excluded switches, page-load strategy, prefs) before the driver starts.</summary>
            public void ConfigureOptions(Action<ChromeOptions> configure)
            {
                configure(options);
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

            internal void CloseDriver()
            {
                try { inactivityCts?.Cancel(); } catch (ObjectDisposedException) { }
                inactivityCts?.Dispose();
                inactivityCts = null;
                var closing = Interlocked.Exchange(ref driver, null);
                if (closing is not null)
                {
                    // Quit throws when Chrome/chromedriver already died; this was async void, so that
                    // throw was unobservable and could take the process down.
                    try { closing.Quit(); } catch { }
                    try { closing.Dispose(); } catch { }
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
                        routes.CreateRoutes();
        }

        public SeleniumObject CreateSeleniumObject(string name, TimeSpan? worstCaseSessionDuration = null)
        {
            var newSeleniumObject = new SeleniumObject(worstCaseSessionDuration);
            newSeleniumObject.objectID = (ulong)DateTime.Now.Ticks;
            newSeleniumObject.name = name;
            currentActiveSeleniumInstances[newSeleniumObject] = 0;
            return newSeleniumObject;
        }

        public List<SeleniumObject> GetCurrentActiveSeleniumInstances()
        {
            return currentActiveSeleniumInstances.Keys.ToList();
        }

        public List<SeleniumObject> GetSeleniumInstancesByID(ulong objectID)
        {
            return currentActiveSeleniumInstances.Keys.Where(x => x.objectID == objectID).ToList();
        }

        public List<SeleniumObject> GetSeleniumInstancesByName(string name)
        {
            return currentActiveSeleniumInstances.Keys.Where(x => x.name == name).ToList();
        }

        public void StopUsingSeleniumObject(SeleniumObject seleniumObject)
        {
            // This used ConcurrentBag.TryTake(out seleniumObject), which takes an ARBITRARY bag item and
            // overwrites the argument: it closed some other caller's browser and leaked the one passed in.
            if (seleniumObject == null) return;
            currentActiveSeleniumInstances.TryRemove(seleniumObject, out _);
            seleniumObject.CloseDriver();
        }
    }
}
