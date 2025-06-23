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
            Process.Start(SearchFullPathOfNearbyFile("SyncAndStartOmnipotent.bat", 3));
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
                var directories = Directory.GetDirectories(rootPath);
                var files = Directory.GetFiles(rootPath);

                if (reverse)
                {
                    directories = directories.Reverse().ToArray();
                    files = files.Reverse().ToArray();
                }

                paths.AddRange(files);

                foreach (var dir in directories)
                {
                    paths.Add(dir);
                    paths.AddRange(GetPathsRecursively(depth - 1, reverse, dir));
                }

                if (!reverse)
                {
                    directories = directories.Reverse().ToArray();
                    files = files.Reverse().ToArray();
                }

                foreach (var dir in directories)
                {
                    paths.AddRange(GetPathsRecursively(depth - 1, !reverse, dir));
                }
            }
            catch (Exception ex)
            {
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
            paths.Concat(GetPathsRecursively(depth, true, rootPath));
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
