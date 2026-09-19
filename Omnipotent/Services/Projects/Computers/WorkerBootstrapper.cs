using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Projects.Computers;

/// <summary>Single-flight, restartable host setup. Owns no project lifecycle decisions.</summary>
public sealed class WorkerBootstrapper
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Omnipotent", "AgentWorker");
    public static string DefaultConfigPath => Path.Combine(DefaultRoot, "client.json");
    private readonly string root;
    private readonly string script;
    private readonly Action<WorkerClient> connected;
    private readonly Action<string> log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int started;
    private volatile bool ready;
    private WorkerClient? activeClient;
    private JObject? localFailure;

    public WorkerBootstrapper(Action<WorkerClient> connected, Action<string> log, string? stateRoot = null, string? scriptPath = null)
    {
        this.connected = connected; this.log = log;
        root = stateRoot ?? DefaultRoot;
        script = scriptPath ?? Path.Combine(AppContext.BaseDirectory, "AgentWorker", "deploy", "Ensure-KAWorker.ps1");
    }

    public JObject Status()
    {
        if (ready) return new JObject { ["state"] = "ready", ["reason"] = "Linux worker is connected. Existing Docker projects require verified migration before cutover." };
        if (localFailure != null) return (JObject)localFailure.DeepClone();
        try
        {
            string file = Path.Combine(root, "setup.json");
            if (File.Exists(file)) return JObject.Parse(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        { return new JObject { ["state"] = "status_unavailable", ["reason"] = "Worker setup state could not be read. Existing state is preserved." }; }
        return new JObject { ["state"] = "starting", ["reason"] = "Preparing automatic Linux worker setup." };
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try { await ReconcileAsync(); }
                catch (Exception ex)
                {
                    localFailure = new JObject { ["state"] = "retrying", ["reason"] = "Automatic worker setup is waiting: " + ex.GetType().Name };
                    log("Worker setup will retry: " + ex.GetType().Name);
                }
                await Task.Delay(TimeSpan.FromMinutes(2));
            }
        });
    }

    public async Task ReconcileAsync()
    {
        if (!await gate.WaitAsync(0)) return;
        try
        {
            localFailure = null;
            string config = Path.Combine(root, "client.json");
            if (await TryConnectAsync(config)) return;
            ready = false;
            // The privileged installer persists independently through app exits/reboots.
            // Do not compete with it or repeatedly ask for UAC while Linux is installing.
            if (File.Exists(Path.Combine(root, "managed-task.json"))) return;
            if (!OperatingSystem.IsWindows())
            {
                localFailure = new JObject { ["state"] = "unsupported_host", ["reason"] = "Automatic Hyper-V provisioning requires Windows. An explicitly configured remote Linux worker is supported." };
                return;
            }
            if (!File.Exists(script))
            {
                localFailure = new JObject { ["state"] = "package_incomplete", ["reason"] = "This Omnipotent release is missing its bundled worker installer. Install a complete release; no manual Ubuntu configuration is required." };
                return;
            }
            using var process = Process.Start(StartInfo(false)) ?? throw new IOException("Worker installer could not start");
            // Installer has its own durable status and cross-process lock. An API timeout
            // or an Omnipotent restart must not kill a download or partially provisioned VM.
            await process.WaitForExitAsync();
            await TryConnectAsync(config);
        }
        finally { gate.Release(); }
    }

    private async Task<bool> TryConnectAsync(string config)
    {
        if (!File.Exists(config)) return false;
        WorkerClient? candidate = null;
        try
        {
            candidate = activeClient ?? WorkerClient.FromFile(config);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var health = await candidate.SendAsync(HttpMethod.Get, "/health", ct: timeout.Token);
            if (health.Value<bool?>("available") != true) return false;
            if (activeClient == null) { connected(candidate); activeClient = candidate; }
            candidate = null;
            if (!ready) log("Persistent Linux worker connected after automatic setup.");
            ready = true;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or System.Security.Cryptography.CryptographicException or Newtonsoft.Json.JsonException)
        { return false; }
        finally { if (candidate != activeClient) candidate?.Dispose(); }
    }

    private ProcessStartInfo StartInfo(bool elevate)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = elevate, CreateNoWindow = !elevate, WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (elevate) info.Verb = "runas";
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-StateRoot", root }) info.ArgumentList.Add(arg);
        // Omnipotent already requests admin at launch. If that permission is present,
        // finish prerequisites automatically; the script still never reboots Windows.
        info.ArgumentList.Add("-EnablePrerequisites");
        return info;
    }

    public void RequestPrerequisiteSetup()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Hyper-V requires Windows");
        if (!File.Exists(script)) throw new FileNotFoundException("Worker installer was not included in this release");
        // Owner invokes this only when ready for Windows feature installation/UAC.
        // No automatic reboot and no Docker/WSL reset is performed.
        using var process = Process.Start(StartInfo(true)) ?? throw new IOException("Windows setup could not start");
        localFailure = null;
    }
}
