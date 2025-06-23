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

        public static void UpdateBot()
        {
            //iteratively search backwards for full path of SyncAndStartOmnipotent.bat
            string currentPath = AppDomain.CurrentDomain.BaseDirectory;
            bool found = false;
            while (found == false)
            {
                string updateFilePath = Path.Combine(currentPath, "SyncAndStartOmnipotent.bat");
                if (File.Exists(updateFilePath))
                {
                    Process.Start(updateFilePath);
                    break;
                }
                else
                {
                    var parentDirectory = Directory.GetParent(currentPath);
                    if (parentDirectory != null)
                    {
                        currentPath = parentDirectory.FullName;
                    }
                    else
                    {
                        throw new FileNotFoundException("SyncAndStartOmnipotent.bat not found in any parent directories.");
                    }
                }
            }
        }

        public static void QuitBot()
        {
            Process.Start("Omnipotent.exe");
            Environment.Exit(0);
        }

        public static string SendTerminalCommand(string script, string filename = null)
        {
            // Set up the process start info
            ProcessStartInfo processInfo = new ProcessStartInfo
            {
                FileName = filename,
                Arguments = script,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Verb = "runas" // This ensures the process runs with admin privileges
            };

            try
            {
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
            catch (System.ComponentModel.Win32Exception ex)
            {
                // Handle the case where the user cancels the UAC prompt
                return $"Failed to execute command as admin: {ex.Message}";
            }
        }


        public static List<string> GetPathsRecursively(int depth, bool reverse = false, string rootPath = null)
        {
            if (string.IsNullOrEmpty(rootPath))
            {
                rootPath = AppDomain.CurrentDomain.BaseDirectory;
            }

            List<string> paths = new List<string>();

            if (depth < 0 || !Directory.Exists(rootPath))
                return paths;

            try
            {
                // Get directories and files in the current rootPath  
                var directories = Directory.GetDirectories(rootPath);
                var files = Directory.GetFiles(rootPath);

                if (reverse)
                {
                    directories = directories.Reverse().ToArray();
                    files = files.Reverse().ToArray();
                }

                // Add files and directories to the paths list  
                paths.AddRange(files);
                paths.AddRange(directories);

                // Recursively traverse parent directories  
                if (depth > 0)
                {
                    string parentPath = Directory.GetParent(rootPath)?.FullName;
                    if (!string.IsNullOrEmpty(parentPath))
                    {
                        paths.AddRange(GetPathsRecursively(depth - 1, reverse, parentPath));
                    }
                }

                // Recursively traverse child directories  
                foreach (var dir in directories)
                {
                    paths.AddRange(GetPathsRecursively(depth - 1, reverse, dir));
                }
            }
            catch (Exception)
            {
                // Handle exceptions silently  
            }

            return paths;
        }

        public static string SearchFullPathOfNearbyFile(string filename, int depth = 4, string rootPath = null)
        {
            if (string.IsNullOrEmpty(rootPath))
            {
                rootPath = AppDomain.CurrentDomain.BaseDirectory;
            }

            var paths = GetPathsRecursively(depth, false, rootPath);
            //paths.Concat(GetPathsRecursively(depth, true, rootPath));
            foreach (var path in paths)
            {
                if (Path.GetFileName(path).Equals(filename, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }
            return null;
        }
    }
}
