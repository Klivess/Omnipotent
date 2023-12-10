using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omnipotent.Logging
{
    public static class OmniLogging
    {
        public static void LogStatus(string message)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(message);
            Console.ForegroundColor= ConsoleColor.White;
        }
        public static void LogError(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(message);
            Console.ForegroundColor = ConsoleColor.White;
        }

        public static void LogError(Exception ex, string specialMessage = "")
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: "+ex.Message+" - "+specialMessage);
            Console.ForegroundColor = ConsoleColor.White;
        }
    }
}
