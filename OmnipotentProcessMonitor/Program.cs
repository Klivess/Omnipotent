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
