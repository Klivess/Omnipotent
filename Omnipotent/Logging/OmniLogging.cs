using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omnipotent.Logging
{
    public static class OmniLogging
    {
        public static void LogStatus(string serviceName, string message)
        {
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.Write($"{serviceName}");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($" | {message}");
            Console.ForegroundColor= ConsoleColor.White;
        }

        public static void LogError(string serviceName, Exception ex, string specialMessage = "")
        {
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.Write($"{serviceName}");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($" | Error: "+ex.Message+" - "+specialMessage);
            Console.ForegroundColor = ConsoleColor.White;
        }

        public static string FormatErrorMessage(Exception ex)
        {
            return ex.Message;
        }
    }
}
