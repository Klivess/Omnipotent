using Omnipotent.Service_Manager;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Omnipotent.Logging
{
    public class OmniLogging : OmniService
    {
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
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.Write($"{serviceName}");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($" | {message}");
            Console.ForegroundColor= ConsoleColor.White;
        }

        public void LogError(string serviceName, Exception ex, string specialMessage = "")
        {
            ErrorInformation info = new ErrorInformation(ex);
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.Write($"{serviceName}");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($" | Error: {info.FullFormattedMessage} - {specialMessage}");
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
    }
}
