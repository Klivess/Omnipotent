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
            public bool appearInConsole;
            public ErrorInformation? errorInfo;
            public int position;
            public DateTime TimeOfLog;
        }

        public SynchronizedCollection<LoggedMessage> messagesToLog = new();
        public List<LoggedMessage> overallMessages = new();

        public OmniLogging()
        {
            name = "OmniLogging";
            threadAnteriority = ThreadAnteriority.Standard;
        }
        public struct ErrorInformation
        {
            /// <summary>
            /// The message describing the exception.
            /// </summary>
            public string Message { get; }

            /// <summary>
            /// The full stack trace at the point the exception was thrown.
            /// </summary>
            public string StackTrace { get; }

            /// <summary>
            /// The name of the application or object that caused the error.
            /// </summary>
            public string Source { get; }

            /// <summary>
            /// The method that threw the exception.
            /// </summary>
            public string TargetSite { get; }

            /// <summary>
            /// The full type name of the exception.
            /// </summary>
            public string ExceptionType { get; }

            /// <summary>
            /// The message of the inner exception, if it exists.
            /// </summary>
            public string InnerExceptionMessage { get; }

            /// <summary>
            /// The stack trace of the inner exception, if it exists.
            /// </summary>
            public string InnerStackTrace { get; }

            /// <summary>
            /// Any key-value data entries attached to the exception.
            /// </summary>
            public Dictionary<string, string> Data { get; }

            /// <summary>
            /// A complete formatted string combining all debugging information.
            /// </summary>
            public string FullFormattedMessage { get; }

            /// <summary>
            /// Original Error Exception that caused this
            /// <summary>
            public Exception ErrorException { get; }

            public ErrorInformation(Exception ex)
            {
                ErrorException = ex;
                Message = ex.Message;
                StackTrace = ex.StackTrace ?? string.Empty;
                Source = ex.Source ?? string.Empty;
                TargetSite = ex.TargetSite?.ToString() ?? string.Empty;
                ExceptionType = ex.GetType().FullName ?? string.Empty;
                InnerExceptionMessage = ex.InnerException?.Message ?? string.Empty;
                InnerStackTrace = ex.InnerException?.StackTrace ?? string.Empty;

                Data = new Dictionary<string, string>();
                foreach (var key in ex.Data.Keys)
                {
                    if (key != null && ex.Data[key] != null)
                        Data[key.ToString()] = ex.Data[key].ToString();
                }

                var formattedData = Data.Count > 0
                    ? string.Join("\n", Data.Select(kvp => $"    {kvp.Key}: {kvp.Value}"))
                    : "    None";

                FullFormattedMessage = $@"
Exception Type: {ExceptionType}
Message       : {Message}
Source        : {Source}
Target Site   : {TargetSite}
Stack Trace   :
{StackTrace}

Inner Exception Message:
{InnerExceptionMessage}

Inner Stack Trace:
{InnerStackTrace}

Data:
{formattedData}
".Trim();
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
                var copy = new List<LoggedMessage>(overallMessages);
                await req.ReturnResponse(JsonConvert.SerializeObject(copy), "application/json");
            };


            (await serviceManager.GetKliveAPIService()).CreateRoute("api/logs", getLogs, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Admin);
        }

        private async Task BeginLogLoop()
        {
            if (messagesToLog.Any())
            {
                var message = messagesToLog.First();
                messagesToLog.Remove(messagesToLog.First());
                try
                {
                    if (message.appearInConsole)
                    {
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
                        await Task.Delay(1);
                    }
                    else
                    {
                        overallMessages.Add(message);
                    }
                }
                catch (Exception ex) { }
            }

            await Task.Delay(10);
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
            try
            {
                var methodFullName = ex.TargetSite.ReflectedType.FullName;
                return methodFullName;
            }
            catch (Exception e)
            {
                return "NotFound";
            }
        }

        public LoggedMessage LogStatus(string serviceName, string message, bool appearInConsole = true)
        {
            try
            {
                LoggedMessage log = new();
                log.serviceName = serviceName;
                log.message = message;
                log.type = LogType.Status;
                log.logID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
                log.TimeOfLog = DateTime.Now;
                log.appearInConsole = appearInConsole;
                messagesToLog.Add(log);
                return log;
            }
            catch (ArgumentException ex)
            {
                return LogStatus(serviceName, message);
            }
        }

        public LoggedMessage LogError(string serviceName, Exception ex, string specialMessage = "", bool appearInConsole = true)
        {
            LoggedMessage log = new();
            log.serviceName = serviceName;
            log.message = specialMessage;
            log.type = LogType.Error;
            log.errorInfo = new ErrorInformation(ex);
            log.logID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            log.TimeOfLog = DateTime.Now;
            log.appearInConsole = appearInConsole;
            messagesToLog.Add(log);
            return log;
        }

        public void UpdateLogMessage(LoggedMessage loggedmessage, string newMessage)
        {
            loggedmessage.oldMessage = loggedmessage.message;
            loggedmessage.message = newMessage;
            loggedmessage.type = LogType.Update;
            messagesToLog.Add(loggedmessage);
        }

        public LoggedMessage LogError(string serviceName, string error, bool appearInConsole = true)
        {
            LoggedMessage log = new();
            log.serviceName = serviceName;
            log.message = error;
            log.type = LogType.Error;
            log.errorInfo = null;
            log.logID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            log.TimeOfLog = DateTime.Now;
            log.appearInConsole = appearInConsole;
            messagesToLog.Add(log);
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
                message.position = overallMessages.Count + 1;
                overallMessages.Add(message);
            }
        }

        private async Task WriteUpdate(LoggedMessage message)
        {
            try
            {
                var pos = overallMessages.Find(k => k.logID == message.logID).position;
                //duct tape solution fix before developing omnilogging
                if (message.errorInfo == null)
                {
                    await WriteStatus(message, pos).WaitAsync(TimeSpan.FromSeconds(10));
                }
                else
                {
                    await WriteError(message, pos).WaitAsync(TimeSpan.FromSeconds(10));
                }
                Console.SetCursorPosition(0, overallMessages.Count + 1);
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
                message.position = overallMessages.Count + 1;
                overallMessages.Add(message);
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
    }
}
