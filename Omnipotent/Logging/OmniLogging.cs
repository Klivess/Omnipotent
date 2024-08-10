using Omnipotent.Service_Manager;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Omnipotent.Logging
{
    public class OmniLogging : OmniService
    {
        public enum LogType
        {
            Status,
            Error
        }

        public struct LoggedMessage
        {
            public LogType type;
            public string serviceName;
            public string message;
            public ErrorInformation? errorInfo;
        }

        Queue<LoggedMessage> loggedMessages = new();

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
        }

        private async Task BeginLogLoop()
        {
            if (loggedMessages.Any())
            {
                var message = loggedMessages.Dequeue();
                if (message.type == LogType.Status)
                {
                    WriteStatus(message);
                }
                else if (message.type == LogType.Error)
                {
                    WriteError(message);
                }
            }
            //Replace this with proper waiting
            while (loggedMessages.Any() == false) { await Task.Delay(100); }
            //Recursive, hopefully this doesnt cause performance issues. (it did, but GC.Collect should hopefully prevents stack overflow)
            GC.Collect();
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

        public void LogStatus(string serviceName, string message)
        {
            try
            {
                LoggedMessage log = new();
                log.serviceName = serviceName;
                log.message = message;
                log.type = LogType.Status;
                loggedMessages.Enqueue(log);
            }
            catch (ArgumentException ex)
            {
                LogStatus(serviceName, message);
            }
        }

        public void LogError(string serviceName, Exception ex, string specialMessage = "")
        {
            LoggedMessage log = new();
            log.serviceName = serviceName;
            log.message = specialMessage;
            log.type = LogType.Error;
            log.errorInfo = new ErrorInformation(ex);
            loggedMessages.Enqueue(log);
        }

        public void LogError(string serviceName, string error)
        {
            LoggedMessage log = new();
            log.serviceName = serviceName;
            log.message = error;
            log.type = LogType.Error;
            log.errorInfo = null;
            loggedMessages.Enqueue(log);
        }

        private async void WriteStatus(LoggedMessage message)
        {
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.Write($"{message.serviceName}");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($" | {message.message}");
            Console.ForegroundColor = ConsoleColor.White;
        }

        private async void WriteError(LoggedMessage message)
        {
            Console.ForegroundColor = ConsoleColor.Blue;
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
            return $"Error: -| {ex.ErrorException.Message} |- Line: {ex.LineOfError} Method: {ex.methodName} Class: {ex.className}";
        }

        public static string FormatErrorMessage(Exception e)
        {
            ErrorInformation ex = new ErrorInformation(e);
            return $"Error: -| {ex.ErrorException.Message} |- Line: {ex.LineOfError} Method: {ex.methodName} Class: {ex.className}";
        }
    }
}
