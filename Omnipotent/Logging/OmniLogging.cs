using Microsoft.ML.Data;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System;
using System.CodeDom;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using static Omnipotent.Logging.OmniLogging;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Omnipotent.Logging
{
    public class OmniLogging : OmniService
    {
        public enum LogType
        {
            Status,
            Error,
            Update
        }

        public struct LoggedMessage
        {
            public string logID;
            public LogType type;
            public string serviceName;
            public string message;
            public string oldMessage;
            public ErrorInformation? errorInfo;
            public int position;
        }

        SynchronizedCollection<LoggedMessage> loggedMessages = new();
        List<LoggedMessage> messages = new();

        public OmniLogging()
        {
            name = "OmniLogging";
            threadAnteriority = ThreadAnteriority.Standard;
        }
        public struct ErrorInformation
        {
            public Exception ErrorException;
            public StackTrace ErrorTrace;
            public int LineOfError;
            public string FullFormattedMessage;
            public string methodName;
            public string className;
            public ErrorInformation(Exception ex)
            {
                var st = new StackTrace(ex, true);
                var thisasm = Assembly.GetExecutingAssembly();
                var method = st.GetFrames().Select(f => f.GetMethod()).First(m => m.Module.Assembly == thisasm);
                ErrorException = ex;
                ErrorTrace = st;
                LineOfError = GetLineOfException(ex);
                FullFormattedMessage = FormatErrorMessage(this);
                methodName = GetMethodOfException(ex);
                className = method.DeclaringType.AssemblyQualifiedName;
            }
        }

        protected override void ServiceMain()
        {
            BeginLogLoop();
            SetupRoutes();
        }

        private async Task SetupRoutes()
        {
            Action<UserRequest> getLogs = async (req) =>
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(messages), "application/json");
            };


            await serviceManager.GetKliveAPIService().CreateRoute("api/logs", getLogs, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Admin);
        }

        private async Task BeginLogLoop()
        {
            if (loggedMessages.Any())
            {
                var message = loggedMessages.Last();
                loggedMessages.Remove(loggedMessages.Last());
                if (message.type == LogType.Status)
                {
                    await WriteStatus(message);
                }
                else if (message.type == LogType.Error)
                {
                    await WriteError(message);
                }
                else if (message.type == LogType.Update)
                {
                    await WriteUpdate(message);
                }
            }

            //Replace this with proper waiting
            while (loggedMessages.Any() == false)
            {
                await Task.Delay(100);
            }
            //Recursive, hopefully this doesnt cause performance issues. (it did, but GC.Collect should hopefully prevents stack overflow)
            //GC.Collect();
            BeginLogLoop();
        }

        public static int GetLineOfException(Exception ex)
        {
            var st = new StackTrace(ex, true);
            var frame = st.GetFrame(st.FrameCount - 1);
            var line = frame.GetFileLineNumber();
            return line;
        }
        public static string GetMethodOfException(Exception ex)
        {
#pragma warning disable CS8602 // Dereference of a possibly null reference.
            var methodFullName = ex.TargetSite.ReflectedType.FullName;
#pragma warning restore CS8602 // Dereference of a possibly null reference.
            return methodFullName;
        }

        public LoggedMessage LogStatus(string serviceName, string message)
        {
            try
            {
                LoggedMessage log = new();
                log.serviceName = serviceName;
                log.message = message;
                log.type = LogType.Status;
                log.logID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
                loggedMessages.Add(log);
                return log;
            }
            catch (ArgumentException ex)
            {
                return LogStatus(serviceName, message);
            }
        }

        public LoggedMessage LogError(string serviceName, Exception ex, string specialMessage = "")
        {
            LoggedMessage log = new();
            log.serviceName = serviceName;
            log.message = specialMessage;
            log.type = LogType.Error;
            log.errorInfo = new ErrorInformation(ex);
            log.logID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            loggedMessages.Add(log);
            return log;
        }

        public void UpdateLogMessage(LoggedMessage loggedmessage, string newMessage)
        {
            loggedmessage.oldMessage = loggedmessage.message;
            loggedmessage.message = newMessage;
            loggedmessage.type = LogType.Update;
            loggedMessages.Add(loggedmessage);
        }

        public LoggedMessage LogError(string serviceName, string error)
        {
            LoggedMessage log = new();
            log.serviceName = serviceName;
            log.message = error;
            log.type = LogType.Error;
            log.errorInfo = null;
            log.logID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            loggedMessages.Add(log);
            return log;
        }

        private async Task WriteStatus(LoggedMessage message, int? position = null)
        {
            if (position != null)
            {
                Console.SetCursorPosition(0, position.Value);
                Console.WriteLine(new string(' ', message.message.Length + 50));
                Console.SetCursorPosition(0, position.Value);
                Console.ForegroundColor = ConsoleColor.DarkBlue;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Blue;
            }
            Console.Write($"{message.serviceName}");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($" | {message.message}");
            Console.ForegroundColor = ConsoleColor.White;
            if (position == null)
            {
                message.position = messages.Count + 1;
                messages.Add(message);
            }
        }

        private async Task WriteUpdate(LoggedMessage message)
        {
            try
            {
                var pos = messages.Find(k => k.logID == message.logID).position;
                //duct tape solution fix before developing omnilogging
                if (message.errorInfo == null)
                {
                    await WriteStatus(message, pos).WaitAsync(TimeSpan.FromSeconds(10));
                }
                else
                {
                    await WriteError(message, pos).WaitAsync(TimeSpan.FromSeconds(10));
                }
                Console.SetCursorPosition(0, messages.Count + 1);
            }
            catch (Exception) { }
        }

        private async Task WriteError(LoggedMessage message, int? position = null)
        {
            if (position != null)
            {
                Console.SetCursorPosition(0, position.Value);
                for (global::System.Int32 i = 0; i < message.oldMessage.Length - 1; i++)
                {
                    Console.Write(" ");
                }
                Console.ForegroundColor = ConsoleColor.DarkBlue;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Blue;
            }
            Console.Write($"{message.serviceName}");
            Console.ForegroundColor = ConsoleColor.Red;
            if (message.errorInfo == null)
            {
                Console.WriteLine($" | Error: {message.message}");
            }
            else if (!string.IsNullOrEmpty(message.message))
            {
                Console.WriteLine($" | Error: {message.errorInfo.Value.FullFormattedMessage} - {message.message}");
            }
            else
            {
                Console.WriteLine($" | Error: {message.errorInfo.Value.FullFormattedMessage}");
            }
            Console.ForegroundColor = ConsoleColor.White;
            if (position == null)
            {
                message.position = messages.Count + 1;
                messages.Add(message);
            }
        }


        //Static variations
        // Q: "Why rename the function instead of creating an overload?" A: Creating an overload will most CERTAINLY cause me 7 hours of agony in the future
        public static void LogStatusStatic(string serviceName, string message)
        {
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.Write($"{serviceName}");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($" | {message}");
            Console.ForegroundColor = ConsoleColor.White;
        }

        public static void LogErrorStatic(string serviceName, Exception ex, string specialMessage = "")
        {
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.Write($"{serviceName}");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($" | Error: " + ex.Message + " - " + specialMessage);
            Console.ForegroundColor = ConsoleColor.White;
        }

        public static string FormatErrorMessage(ErrorInformation ex)
        {
            return $"{ex.ErrorException.Message} |- Line: {ex.LineOfError} Method: {ex.methodName} Class: {ex.className}";
        }

        public static string FormatErrorMessage(Exception e)
        {
            ErrorInformation ex = new ErrorInformation(e);
            return $"{ex.ErrorException.Message} |- Line: {ex.LineOfError} Method: {ex.methodName} Class: {ex.className}";
        }
    }
}
