using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OmnipotentProcessMonitor
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            while (true)
            {
                Task.Delay(5000).Wait();
                try
                {
                    // Check if the Omnipotent process is running  
                    var omnipotentProcess = System.Diagnostics.Process.GetProcessesByName("Omnipotent").FirstOrDefault();
                    if (omnipotentProcess == null)
                    {
                        // If not running, start it  
                        var processStartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "Omnipotent.exe",
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        var process = System.Diagnostics.Process.Start(processStartInfo);

                        // Read error output if the process fails to start  
                        if (process != null)
                        {
                            string errorOutput = process.StandardError.ReadToEnd();
                            if (!string.IsNullOrEmpty(errorOutput))
                            {
                                Console.WriteLine($"Omnipotent process error: {errorOutput}");
                                //Go to path of Omnipotent exe, then go to the SavedData/ProcessMonitorLogs directory and write the error output to a log file
                                string logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SavedData", "ProcessMonitorLogs");
                                if (!Directory.Exists(logDirectory))
                                {
                                    Directory.CreateDirectory(logDirectory);
                                }
                                string logFilePath = Path.Combine(logDirectory, $"OmnipotentErrorLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                                using (StreamWriter writer = new StreamWriter(logFilePath, true))
                                {
                                    writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Omnipotent process error: {errorOutput}");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log the error (you can replace this with your logging mechanism)  
                    Console.WriteLine($"Error checking or starting Omnipotent: {ex.Message}");
                }

                // Nothing past this line will execute.  
                Application.Run();
            }
        }
    }
}
