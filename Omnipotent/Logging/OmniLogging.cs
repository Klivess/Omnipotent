using Microsoft.ML.Data;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Spectre.Console;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using static Omnipotent.Logging.OmniLogging;

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

        public ConcurrentQueue<LoggedMessage> messagesToLog = new();
        public ConcurrentQueue<LoggedMessage> overallMessages = new();

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
            _ = Task.Run(BeginLogLoop);
            _ = SetupRoutes();
        }

        private async Task SetupRoutes()
        {
            Func<UserRequest, Task> getLogs = async (req) =>
            {
                var copy = new List<LoggedMessage>(overallMessages.ToList());
                await req.ReturnResponse(JsonConvert.SerializeObject(copy, Formatting.Indented), "application/json");
            };


            await CreateAPIRoute("api/logs", getLogs, HttpMethod.Get, Profiles.KMProfileManager.KMPermissions.Admin);
        }

        private async Task BeginLogLoop()
        {
            while (true)
            {
                if (messagesToLog.TryDequeue(out var message))
                {
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
                        }
                        else
                        {
                            message.position = overallMessages.Count + 1;
                            overallMessages.Enqueue(message);
                        }
                    }
                    catch (Exception)
                    {
                        // Ignore any write exceptions to prevent breaking the loop
                    }
                }
                else
                {
                    await Task.Delay(10);
                }
            }
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
            LoggedMessage log = new();
            log.serviceName = serviceName;
            log.message = message;
            log.type = LogType.Status;
            try
            {
                log.logID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            }
            catch
            {
                log.logID = Guid.NewGuid().ToString();
            }
            log.TimeOfLog = DateTime.Now;
            log.appearInConsole = appearInConsole;
            messagesToLog.Enqueue(log);
            return log;
        }

        public LoggedMessage LogError(string serviceName, Exception ex, string specialMessage = "", bool appearInConsole = true)
        {
            LoggedMessage log = new();
            log.serviceName = serviceName;
            log.message = specialMessage;
            log.type = LogType.Error;
            log.errorInfo = new ErrorInformation(ex);
            try
            {
                log.logID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            }
            catch
            {
                log.logID = Guid.NewGuid().ToString();
            }
            log.TimeOfLog = DateTime.Now;
            log.appearInConsole = appearInConsole;
            messagesToLog.Enqueue(log);
            return log;
        }

        public void UpdateLogMessage(LoggedMessage loggedmessage, string newMessage)
        {
            loggedmessage.oldMessage = loggedmessage.message;
            loggedmessage.message = newMessage;
            loggedmessage.type = LogType.Update;
            messagesToLog.Enqueue(loggedmessage);
        }

        public LoggedMessage LogError(string serviceName, string error, bool appearInConsole = true)
        {
            LoggedMessage log = new();
            log.serviceName = serviceName;
            log.message = error;
            log.type = LogType.Error;
            log.errorInfo = null;
            try
            {
                log.logID = RandomGeneration.GenerateRandomLengthOfNumbers(20);
            }
            catch
            {
                log.logID = Guid.NewGuid().ToString();
            }
            log.TimeOfLog = DateTime.Now;
            log.appearInConsole = appearInConsole;
            messagesToLog.Enqueue(log);
            return log;
        }

        private Task WriteStatus(LoggedMessage message, int? position = null)
        {
            var timeStamp = $"[grey][[[/][bold blue]{message.TimeOfLog:HH:mm:ss}[/][grey]]][/]";
            if (position != null)
            {
                AnsiConsole.MarkupLine($"{timeStamp} [blue]{Markup.Escape(message.serviceName)}[/] [green]|[/] [yellow](Update)[/] [green]{Markup.Escape(message.message ?? string.Empty)}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"{timeStamp} [blue]{Markup.Escape(message.serviceName)}[/] [green]| {Markup.Escape(message.message ?? string.Empty)}[/]");
                message.position = overallMessages.Count + 1;
                overallMessages.Enqueue(message);
            }
            return Task.CompletedTask;
        }

        private Task WriteUpdate(LoggedMessage message)
        {
            try
            {
                if (message.errorInfo == null)
                {
                    WriteStatus(message, 1);
                }
                else
                {
                    WriteError(message, 1);
                }
            }
            catch (Exception) { }
            return Task.CompletedTask;
        }

        private Task WriteError(LoggedMessage message, int? position = null)
        {
            var timeStamp = $"[grey][[[/][bold red]{message.TimeOfLog:HH:mm:ss}[/][grey]]][/]";
            string updateLabel = position.HasValue ? "[yellow](Update)[/] " : "";

            AnsiConsole.Markup($"{timeStamp} [blue]{Markup.Escape(message.serviceName)}[/] [red]|[/] {updateLabel}[bold red]Error:[/] ");

            if (message.errorInfo == null)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(message.message ?? string.Empty)}[/]");
            }
            else if (!string.IsNullOrEmpty(message.message))
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(message.errorInfo.Value.FullFormattedMessage)} - {Markup.Escape(message.message)}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(message.errorInfo.Value.FullFormattedMessage)}[/]");
            }

            if (position == null)
            {
                message.position = overallMessages.Count + 1;
                overallMessages.Enqueue(message);
            }
            return Task.CompletedTask;
        }


        //Static variations
        // Q: "Why rename the function instead of creating an overload?" A: Creating an overload will most CERTAINLY cause me 7 hours of agony in the future
        public static void LogStatusStatic(string serviceName, string message)
        {
            var timeStamp = $"[grey][[[/][bold blue]{DateTime.Now:HH:mm:ss}[/][grey]]][/]";
            AnsiConsole.MarkupLine($"{timeStamp} [blue]{Markup.Escape(serviceName)}[/] [green]| {Markup.Escape(message ?? string.Empty)}[/]");
        }

        public static void LogErrorStatic(string serviceName, Exception ex, string specialMessage = "")
        {
            var timeStamp = $"[grey][[[/][bold red]{DateTime.Now:HH:mm:ss}[/][grey]]][/]";
            AnsiConsole.MarkupLine($"{timeStamp} [blue]{Markup.Escape(serviceName)}[/] [red]| Error: {Markup.Escape(ex.Message)} - {Markup.Escape(specialMessage)}[/]");
            AnsiConsole.WriteException(ex);
        }
    }
}
