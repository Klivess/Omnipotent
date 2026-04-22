using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using Omnipotent.Profiles;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.SeleniumManager;
using Org.BouncyCastle.Asn1.X509.Qualified;
using static Omnipotent.Threading.WindowsInvokes;

namespace Omnipotent.Service_Manager
{

    public class OmniService
    { 
        protected ThreadAnteriority threadAnteriority;
        protected string name;
        public string serviceID;
        private Thread serviceThread;
        private OmniServiceManager serviceManager;
        protected Stopwatch serviceUptime;
        private int fatalExceptionHandled;

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
        public async Task ServiceCreateScheduledTask(DateTime duedateTime, string taskname, string topic = "", string reason = "", bool important = true, object PassableData = null)
        {
            serviceManager.GetTimeManager().CreateNewScheduledTask(duedateTime, taskname, topic, this.name, reason, important, PassableData);
        }
        public ref DataUtil GetDataHandler()
        {
            while (serviceManager.fileHandlerService.IsServiceActive() == false) { Task.Delay(100); }
            return ref serviceManager.fileHandlerService;
        }

        public async Task<object?> ExecuteServiceMethod<T>(string methodName, params object[] args)
        {
            var service = await serviceManager.GetServiceByClassType<T>();
            if (service == null || service.Count() == 0)
            {
                throw new InvalidOperationException($"Service of type {typeof(T).Name} not found.");
            }
            var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            System.Reflection.MethodInfo? method;
            // Ensure args is non-null for downstream resolution
            args ??= Array.Empty<object>();

            try
            {
                // Try the simple resolution first
                method = service[0].GetType().GetMethod(methodName, bindingFlags);
            }
            catch (System.Reflection.AmbiguousMatchException)
            {
                // Fall back to robust resolver which handles null args and overloads
                method = ResolveMethod(service[0].GetType(), methodName, bindingFlags, args);
            }

            // If simple lookup didn't find a method (or returned ambiguous), try the resolver
            if (method == null)
            {
                method = ResolveMethod(service[0].GetType(), methodName, bindingFlags, args);
            }
            if (method == null)
            {
                throw new Exception($"Method {methodName} not found in service of type {typeof(T).Name}.");
            }
            var result = method.Invoke(service[0], args.Length > 0 ? args : null);
            if (result is Task task)
            {
                await task;
                var taskType = task.GetType();
                if (taskType.IsGenericType)
                {
                    return taskType.GetProperty("Result")?.GetValue(task);
                }
                return null;
            }
            return result;
        }

        public async Task<object?> GetServiceObject<T>(string objectName)
        {
            var service = await serviceManager.GetServiceByClassType<T>();
            if (service == null || service.Count() == 0)
            {
                throw new InvalidOperationException($"Service of type {typeof(T).Name} not found.");
            }
            var field = service[0].GetType().GetField(objectName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                return field.GetValue(service[0]);
            }
            var property = service[0].GetType().GetProperty(objectName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null)
            {
                return property.GetValue(service[0]);
            }
            throw new MissingMemberException($"Field or property '{objectName}' not found in service of type {typeof(T).Name}.");
        }

        public async Task<SeleniumManager> GetSeleniumManager()
        {
            while ((await serviceManager.GetServiceByClassType<SeleniumManager>()) == null) { Task.Delay(100); }
            return (SeleniumManager)(await serviceManager.GetServiceByClassType<SeleniumManager>())[0];
        }

        // Service manager internal access helpers
        public TimeSpan GetManagerUptime() => serviceManager.GetOverallUptime();
        public ref OmniLogging GetLoggerService() => ref serviceManager.GetLogger();
        public ref OmniServiceMonitor GetServiceMonitor() => ref serviceManager.GetMonitor();
        public List<OmniService> GetActiveServices() => serviceManager.activeServices;
        public ref TimeManager GetTimeManagerService() => ref serviceManager.GetTimeManager();
        public OmniService GetServiceByName(string serviceName) => serviceManager.GetServiceByName(serviceName);
        public bool CreateAndStartService(OmniService service, bool overrideDuplicate = false) => serviceManager.CreateAndStartNewMonitoredOmniService(service, overrideDuplicate);
        public async Task<OmniService[]> GetServicesByType<T>() => await serviceManager.GetServiceByClassType<T>();

        public event EventHandler<OmniSettingsChangedEventArgs> OnOmniSettingsChanged
        {
            add => GetOmniGlobalSettingsManager().GetAwaiter().GetResult().OnSettingsChanged += value;
            remove => GetOmniGlobalSettingsManager().GetAwaiter().GetResult().OnSettingsChanged -= value;
        }

        public async Task<OmniGlobalSettingsManager> GetOmniGlobalSettingsManager()
        {
            var services = await serviceManager.GetServiceByClassType<OmniGlobalSettingsManager>();
            if (services == null || services.Length == 0)
            {
                throw new InvalidOperationException($"Service of type {typeof(OmniGlobalSettingsManager).Name} not found.");
            }

            return (OmniGlobalSettingsManager)services[0];
        }

        // Common cross-service helper for API route creation (wraps ExecuteServiceMethod)
        public async Task CreateAPIRoute(string path, Func<KliveAPI.UserRequest, Task> handler, HttpMethod method, KMProfileManager.KMPermissions permission)
        {
            await ExecuteServiceMethod<KliveAPI>("CreateRoute", path, handler, method, permission);
        }

        // Typed shortcuts to get omni settings from the global settings manager
        public async Task<bool> GetBoolOmniSetting(string name, bool defaultValue = false, bool sensitive = false, bool askKlivesForFulfillment = false)
        {
            var res = await ExecuteServiceMethod<OmniGlobalSettingsManager>("GetBoolOmniSetting", name, defaultValue, sensitive, askKlivesForFulfillment, this.serviceID, this.name);
            if (res == null) return defaultValue;
            return (bool)res;
        }

        public async Task<bool> GetBoolOmniSetting(bool defaultValue = false, bool sensitive = false, bool askKlivesForFulfillment = false)
        {
            var res = await ExecuteServiceMethod<OmniGlobalSettingsManager>("GetBoolOmniSetting", defaultValue, sensitive, askKlivesForFulfillment, this.serviceID, this.name);
            return (bool)res;
        }

        public async Task<int> GetIntOmniSetting(string name, int defaultValue = 0, bool sensitive = false, bool askKlivesForFulfillment = false)
        {
            var res = await ExecuteServiceMethod<OmniGlobalSettingsManager>("GetIntOmniSetting", name, defaultValue, sensitive, askKlivesForFulfillment, this.serviceID, this.name);
            if (res == null) return defaultValue;
            return (int)res;
        }

        public async Task<int> GetIntOmniSetting(bool sensitive = false, bool askKlivesForFulfillment = false, int defaultValue = 0)
        {
            var res = await ExecuteServiceMethod<OmniGlobalSettingsManager>("GetIntOmniSetting", sensitive, askKlivesForFulfillment, defaultValue, this.serviceID, this.name);
            if (res == null) return defaultValue;
            return (int)res;
        }

        public async Task<string> GetStringOmniSetting(string name, string defaultValue = null, bool sensitive = false, bool askKlivesForFulfillment = false)
        {
            var res = await ExecuteServiceMethod<OmniGlobalSettingsManager>("GetStringOmniSetting", name, defaultValue, sensitive, askKlivesForFulfillment, this.serviceID, this.name);
            return res as string ?? defaultValue;
        }

        public async Task<string> GetStringOmniSetting(bool sensitive = false, bool askKlivesForFulfillment = false, string defaultValue = null)
        {
            var res = await ExecuteServiceMethod<OmniGlobalSettingsManager>("GetStringOmniSetting", sensitive, askKlivesForFulfillment, defaultValue, this.serviceID, this.name);
            return res as string ?? defaultValue;
        }

        // Backwards-compatible generic getter that returns string representation
        public async Task<string> GetOmniSetting(string name, OmniSettingType type, bool sensitive = false, bool askKlivesForFulfillment = false)
        {
            switch (type)
            {
                case OmniSettingType.Bool:
                    var b = await GetBoolOmniSetting(name, defaultValue: false, sensitive: sensitive, askKlivesForFulfillment: askKlivesForFulfillment);
                    return b.ToString();
                case OmniSettingType.Int:
                    var i = await GetIntOmniSetting(name, defaultValue: 0, sensitive: sensitive, askKlivesForFulfillment: askKlivesForFulfillment);
                    return i.ToString();
                case OmniSettingType.String:
                default:
                    return await GetStringOmniSetting(name, defaultValue: null, sensitive: sensitive, askKlivesForFulfillment: askKlivesForFulfillment);
            }
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
            Interlocked.Exchange(ref fatalExceptionHandled, 0);
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
                    var previousContext = SynchronizationContext.Current;
                    var serviceExceptionContext = new ServiceExceptionSynchronizationContext(HandleUnhandledServiceException);
                    SynchronizationContext.SetSynchronizationContext(serviceExceptionContext);
                    try
                    {
                        try
                        {
                            ServiceMain(); Task.Delay(-1).Wait();
                        }
                        catch (ThreadInterruptedException)
                        {
                            ServiceActive = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        HandleUnhandledServiceException(ex);
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(previousContext);
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

        private void HandleUnhandledServiceException(Exception ex)
        {
            if (ex is ThreadInterruptedException)
            {
                ServiceActive = false;
                return;
            }

            if (Interlocked.CompareExchange(ref fatalExceptionHandled, 1, 0) != 0)
            {
                return;
            }

            ServiceActive = false;

            try
            {
                ServiceLogError(ex, $"Unhandled error caught in {name} service.").GetAwaiter().GetResult();
            }
            catch { }

            try
            {
                var discordService = serviceManager?.GetKliveBotDiscordService().GetAwaiter().GetResult();
                discordService?.SendMessageToKlives(KliveBotDiscord.MakeSimpleEmbed($"Unhandled error caught in {name} service!",
                    new ErrorInformation(ex).FullFormattedMessage, DSharpPlus.Entities.DiscordColor.Red)).GetAwaiter().GetResult();
            }
            catch { }

            try
            {
                TerminateService().GetAwaiter().GetResult();
            }
            catch { }

            if (threadAnteriority == ThreadAnteriority.Critical)
            {
                try
                {
                    Task.Run(async () =>
                    {
                        await Task.Delay(2000);
                        await ServiceLog($"Service {name} is Critical, restarting after crash...");
                        ServiceStart();
                    });
                }
                catch { }
            }
        }

        public OmniService()
        {
            cancellationToken = new CancellationTokenSource();
        }

        public async Task<bool> TerminateService()
        {
            try
            {
                if (!ServiceActive)
                {
                    return true;
                }

                ServiceActive = false;
                ServiceQuitRequest?.Invoke();
                await Task.Delay(500);
                await ServiceLog("Ending " + name + " service.");
                GC.Collect();
                if (serviceThread != null && serviceThread.IsAlive && Thread.CurrentThread.ManagedThreadId != serviceThread.ManagedThreadId)
                {
                    serviceThread.Interrupt();
                }
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
                await TerminateService();
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

        MethodInfo? ResolveMethod(Type target, string name, BindingFlags flags, object?[] args)
        {
            var candidates = target.GetMethods(flags).Where(m => m.Name == name && m.GetParameters().Length == args.Length);
            foreach (var m in candidates)
            {
                var ps = m.GetParameters();
                bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    var pType = ps[i].ParameterType;
                    var a = args[i];
                    if (a == null)
                    {
                        if (pType.IsValueType && Nullable.GetUnderlyingType(pType) == null) { ok = false; break; }
                    }
                    else
                    {
                        if (!pType.IsAssignableFrom(a.GetType()))
                        {
                            ok = false; break;
                        }
                    }
                }
                if (ok) return m;
            }
            return null;
        }

        private sealed class ServiceExceptionSynchronizationContext : SynchronizationContext
        {
            private readonly Action<Exception> exceptionHandler;

            public ServiceExceptionSynchronizationContext(Action<Exception> exceptionHandler)
            {
                this.exceptionHandler = exceptionHandler;
            }

            public override void Post(SendOrPostCallback d, object? state)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        d(state);
                    }
                    catch (Exception ex)
                    {
                        exceptionHandler(ex);
                    }
                });
            }

            public override void Send(SendOrPostCallback d, object? state)
            {
                try
                {
                    d(state);
                }
                catch (Exception ex)
                {
                    exceptionHandler(ex);
                }
            }
        }
    }
}
