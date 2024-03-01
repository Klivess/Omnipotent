using Omnipotent.Service_Manager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omnipotent.Logging
{
    public class OmniLogging : OmniService
    {
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
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.Write($"{serviceName}");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($" | Error: "+ex.Message+" - "+specialMessage);
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

        public static string FormatErrorMessage(Exception ex)
        {
            return ex.Message;
        }
    }
}
