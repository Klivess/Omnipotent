using System.Diagnostics;
using System.Security.Cryptography;
using KliveLink.Contracts;

namespace KliveLink.Agent.V2;

public static class V2Updater
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static async Task<V2UpdateResult> UpdateAsync(V2UpdateManifest manifest, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(manifest.Url) || string.IsNullOrWhiteSpace(manifest.Sha256))
            return new V2UpdateResult { Scheduled = false, Message = "Update URL or hash missing.", Version = manifest.Version };
        string exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return new V2UpdateResult { Scheduled = false, Message = "Current executable path unavailable.", Version = manifest.Version };
        try
        {
            byte[] bytes = await Http.GetByteArrayAsync(manifest.Url, ct);
            string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(sha, manifest.Sha256.Replace(":", "").Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                return new V2UpdateResult { Scheduled = false, Message = "Update hash mismatch.", Version = manifest.Version };
            string temp = Path.Combine(Path.GetTempPath(), $"kl_update_{Guid.NewGuid():N}" + Path.GetExtension(exe));
            await File.WriteAllBytesAsync(temp, bytes, ct);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            string script = Path.Combine(Path.GetTempPath(), $"kl_update_{Guid.NewGuid():N}" + (OperatingSystem.IsWindows() ? ".cmd" : ".sh"));
            string content = OperatingSystem.IsWindows()
                ? "@echo off\r\n" +
                  "timeout /t 2 /nobreak >nul\r\n" +
                  $"taskkill /F /PID {Environment.ProcessId} >nul 2>&1\r\n" +
                  "timeout /t 2 /nobreak >nul\r\n" +
                  $"copy /y \"{temp}\" \"{exe}\" >nul 2>&1\r\n" +
                  $"start \"\" \"{exe}\" --installed\r\n" +
                  $"del /f /q \"{temp}\" >nul 2>&1\r\n" +
                  $"del /f /q \"{script}\" >nul 2>&1\r\n"
                : "#!/bin/sh\n" +
                  "sleep 2\n" +
                  $"kill -9 {Environment.ProcessId} 2>/dev/null || true\n" +
                  "sleep 1\n" +
                  $"cp -f \"{temp}\" \"{exe}\"\n" +
                  $"chmod 755 \"{exe}\"\n" +
                  $"nohup \"{exe}\" --installed >>\"{PortablePaths.LogPath}\" 2>&1 &\n" +
                  $"rm -f \"{temp}\" \"{script}\"\n";
            await File.WriteAllTextAsync(script, content, ct);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Process.Start(new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = OperatingSystem.IsWindows() ? $"/d /c \"{script}\"" : $"\"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return new V2UpdateResult { Scheduled = true, Message = "Update scheduled; agent will restart.", Version = manifest.Version };
        }
        catch (Exception ex)
        {
            return new V2UpdateResult { Scheduled = false, Message = ex.Message, Version = manifest.Version };
        }
    }
}
