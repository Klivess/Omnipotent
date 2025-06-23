using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
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
            string processExecutablePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Omnipotent.exe");

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
                            FileName = processExecutablePath,
                            RedirectStandardError = true,
                            UseShellExecute = false,
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
                                File.Create(logFilePath).Dispose(); // Create the file and close it immediately to avoid locking it
                                File.AppendAllText(logFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Omnipotent process error: {errorOutput}\n");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log the error (you can replace this with your logging mechanism)  
                    Console.WriteLine($"Error checking or starting Omnipotent: {ex.Message}");
                    MessageBox.Show($"Error checking or starting Omnipotent: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Environment.Exit(0);
                }
            }

            // Nothing past this line will execute.  
            Application.Run();
        }
    }
}
