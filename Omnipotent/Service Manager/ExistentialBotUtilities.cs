using System.Diagnostics;

namespace Omnipotent.Service_Manager
{
    public class ExistentialBotUtilities
    {
        public static void RestartBot()
        {
            Process.Start("Omnipotent.exe");
            Environment.Exit(-1);
        }
        public static void QuitBot()
        {
            Process.Start("Omnipotent.exe");
            Environment.Exit(0);
        }

        public static string SendTerminalCommand(string filename, string script)
        {
            // Set up the process start info
            ProcessStartInfo processInfo = new ProcessStartInfo
            {
                FileName = filename,
                Arguments = script,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Start the process
            Process process = new Process();
            process.StartInfo = processInfo;
            process.Start();

            // Read the output and errors
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(15));
            return output + error;
        }
    }
}
