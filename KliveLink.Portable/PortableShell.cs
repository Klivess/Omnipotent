using System.Diagnostics;
using System.Text;

namespace KliveLink.Agent.V2;

public sealed class V2ShellSession : IDisposable
{
    private readonly object gate = new();
    private readonly Action<string> onOutput;
    private Process? process;

    public V2ShellSession(Action<string> onOutput) => this.onOutput = onOutput;

    public bool Start()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = OperatingSystem.IsWindows() ? "/Q /D" : "-i",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data + "\r\n"); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data + "\r\n"); };
            if (!process.Start()) return false;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return true;
        }
        catch { return false; }
    }

    public void Write(string text)
    {
        lock (gate)
        {
            try
            {
                if (process?.StandardInput.BaseStream.CanWrite != true) return;
                process.StandardInput.Write(text);
                process.StandardInput.Flush();
            }
            catch { }
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            try { process?.Dispose(); } catch { }
            process = null;
        }
    }

    public void Dispose() => Stop();
}
