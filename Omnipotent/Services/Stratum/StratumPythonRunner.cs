using System.Diagnostics;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Manages a per-host Python virtual environment used by Stratum's Mechanical and
    /// Simulation agents to execute CadQuery / OpenFOAM-helper scripts. Bootstrap is
    /// idempotent and lazy: the first Mechanical run that needs it triggers
    /// <see cref="EnsureBootstrappedAsync"/>, which streams progress via the supplied
    /// log callback so it surfaces in the user's run event stream.
    ///
    /// We deliberately do NOT manage a Python install — the host must already have
    /// `python` (3.10+) on PATH, or the bootstrap fails with a clear setup message.
    /// Per the user's hard rule, no reflection is used; we shell out to python.exe and
    /// pip directly.
    /// </summary>
    public class StratumPythonRunner
    {
        private readonly string venvRoot;
        private readonly string scriptsDir;        // venv/Scripts (Windows) or venv/bin
        private readonly string venvPython;
        private readonly string requirementsPath;
        private readonly SemaphoreSlim bootstrapLock = new(1, 1);
        private bool bootstrapped;

        public string VenvRoot => venvRoot;
        public string VenvPython => venvPython;
        public bool IsBootstrapped => bootstrapped;

        public StratumPythonRunner()
        {
            Directory.CreateDirectory(OmniPaths.GlobalPaths.StratumPythonDirectory);
            venvRoot = Path.Combine(OmniPaths.GlobalPaths.StratumPythonDirectory, "venv");
            bool windows = OperatingSystem.IsWindows();
            scriptsDir = Path.Combine(venvRoot, windows ? "Scripts" : "bin");
            venvPython = Path.Combine(scriptsDir, windows ? "python.exe" : "python");
            requirementsPath = Path.Combine(OmniPaths.GlobalPaths.StratumPythonDirectory, "requirements.txt");
        }

        /// <summary>Reports the current bootstrap status without doing any work.</summary>
        public (bool venvExists, bool cadqueryInstalled, string? hostPython) Status()
        {
            bool venvExists = File.Exists(venvPython);
            bool cqInstalled = false;
            if (venvExists)
            {
                try
                {
                    var (exit, stdout, _) = RunSync(venvPython, new[] { "-c", "import cadquery; print(cadquery.__version__)" }, null, TimeSpan.FromSeconds(20));
                    cqInstalled = exit == 0 && !string.IsNullOrWhiteSpace(stdout);
                }
                catch { cqInstalled = false; }
            }
            string? host = null;
            try
            {
                var (exit, stdout, _) = RunSync(ResolveHostPython(), new[] { "--version" }, null, TimeSpan.FromSeconds(10));
                if (exit == 0) host = stdout.Trim();
            }
            catch { }
            bootstrapped = venvExists && cqInstalled;
            return (venvExists, cqInstalled, host);
        }

        /// <summary>
        /// Creates the venv (if missing) and pip-installs CadQuery + supporting deps.
        /// Idempotent. Streams progress via the <paramref name="progress"/> callback so
        /// the agent can forward it to the user's event stream.
        /// </summary>
        public async Task EnsureBootstrappedAsync(Action<string> progress, CancellationToken ct)
        {
            await bootstrapLock.WaitAsync(ct);
            try
            {
                if (Status().cadqueryInstalled) { bootstrapped = true; return; }

                await EnsureVenvLocked(progress, ct);

                File.WriteAllText(requirementsPath, BuildRequirements());

                progress("Upgrading pip…");
                var (pipExit, _, pipErr) = await RunAsync(venvPython, new[] { "-m", "pip", "install", "--upgrade", "pip", "wheel", "setuptools" }, null, TimeSpan.FromMinutes(5), null, ct);
                if (pipExit != 0) throw new Exception($"pip upgrade failed: {pipErr}");

                progress("Installing CadQuery + dependencies (this can take several minutes the first time)…");
                var (instExit, _, instErr) = await RunAsync(venvPython, new[] { "-m", "pip", "install", "-r", requirementsPath }, null, TimeSpan.FromMinutes(20),
                    line => progress($"pip: {line}"), ct);
                if (instExit != 0) throw new Exception($"pip install failed: {instErr}");

                progress("Verifying CadQuery import…");
                var (vExit, vOut, vErr) = await RunAsync(
                    venvPython,
                    new[] { "-c", "import sys, cadquery; print('cq', cadquery.__version__); print('py', sys.version)" },
                    null, TimeSpan.FromMinutes(1),
                    line => progress($"verify: {line}"),
                    line => progress($"verify[stderr]: {line}"),
                    ct);

                // Auto-remediation: cadquery-ocp wheels for Python 3.9 are built against the
                // numpy 1.x C ABI. If pip's solver pulled numpy 2.x anyway (because the user's
                // requirements.txt was stale, or a transitive constraint relaxed it), downgrade
                // numpy in-place and retry the import once before giving up.
                if (vExit != 0)
                {
                    progress("Import failed — checking installed numpy version…");
                    var (npExit, npOut, _) = await RunAsync(
                        venvPython,
                        new[] { "-c", "import numpy; print(numpy.__version__)" },
                        null, TimeSpan.FromSeconds(30), null, null, ct);
                    string npVer = (npOut ?? "").Trim();
                    if (npExit == 0 && npVer.StartsWith("2."))
                    {
                        progress($"Detected numpy {npVer} (incompatible with cadquery-ocp 7.7.x on Python 3.9). Downgrading to numpy<2…");
                        var (fixExit, _, fixErr) = await RunAsync(
                            venvPython,
                            new[] { "-m", "pip", "install", "--upgrade", "--force-reinstall", "numpy<2" },
                            null, TimeSpan.FromMinutes(5),
                            line => progress($"pip: {line}"),
                            line => progress($"pip[stderr]: {line}"),
                            ct);
                        if (fixExit != 0) throw new Exception($"numpy downgrade failed: {fixErr}");

                        progress("Retrying CadQuery import…");
                        (vExit, vOut, vErr) = await RunAsync(
                            venvPython,
                            new[] { "-c", "import sys, cadquery; print('cq', cadquery.__version__); print('py', sys.version)" },
                            null, TimeSpan.FromMinutes(1),
                            line => progress($"verify: {line}"),
                            line => progress($"verify[stderr]: {line}"),
                            ct);
                    }
                }

                if (vExit != 0)
                {
                    string detail = (vErr?.Trim().Length > 0 ? vErr : vOut)?.Trim() ?? "(no output)";
                    throw new Exception(
                        $"CadQuery import failed (exit {vExit}). The venv installed but Python could not load cadquery/cadquery-ocp.\n" +
                        $"Most common causes on Windows: (1) Python 3.9 mixed with numpy 2.x (try Python 3.11/3.12), (2) missing VC++ runtime, (3) cadquery-ocp wheel/Python ABI mismatch.\n" +
                        $"Diagnostics:\n{detail}");
                }
                progress($"CadQuery {vOut.Trim()} ready.");
                bootstrapped = true;
            }
            finally
            {
                bootstrapLock.Release();
            }
        }

        // Caller must hold bootstrapLock.
        private async Task EnsureVenvLocked(Action<string> progress, CancellationToken ct)
        {
            if (File.Exists(venvPython)) return;
            string hostPython = await ResolveOrInstallHostPythonAsync(progress, ct);
            progress($"Using host Python: {hostPython}");
            progress("Creating virtual environment (one-time, ~30 seconds)…");
            var (exit, _, stderr) = await RunAsync(hostPython, new[] { "-m", "venv", venvRoot }, null, TimeSpan.FromMinutes(3), null, ct);
            if (exit != 0) throw new Exception($"Failed to create venv: {stderr}");
        }

        /// <summary>Idempotently installs PlatformIO into the venv and verifies it runs.</summary>
        public async Task EnsurePlatformIOAsync(Action<string> progress, CancellationToken ct)
        {
            await bootstrapLock.WaitAsync(ct);
            try
            {
                await EnsureVenvLocked(progress, ct);
                if (IsPlatformIOInstalledNoLock()) { progress("PlatformIO already installed."); return; }

                progress("Upgrading pip (for PlatformIO install)…");
                var (pipExit, _, pipErr) = await RunAsync(venvPython, new[] { "-m", "pip", "install", "--upgrade", "pip", "wheel", "setuptools" }, null, TimeSpan.FromMinutes(5), null, ct);
                if (pipExit != 0) throw new Exception($"pip upgrade failed: {pipErr}");

                progress("Installing PlatformIO Core (one-time, a few minutes)…");
                var (instExit, _, instErr) = await RunAsync(venvPython, new[] { "-m", "pip", "install", "platformio" }, null, TimeSpan.FromMinutes(15),
                    line => progress($"pip: {line}"), ct);
                if (instExit != 0) throw new Exception($"pip install platformio failed: {instErr}");

                var (vExit, vOut, vErr) = await RunAsync(venvPython, new[] { "-m", "platformio", "--version" }, null, TimeSpan.FromMinutes(1), null, ct);
                if (vExit != 0) throw new Exception($"platformio --version failed: {vErr}");
                progress($"PlatformIO ready: {vOut.Trim()}");
            }
            finally { bootstrapLock.Release(); }
        }

        /// <summary>True if the venv exists AND `python -m platformio --version` succeeds.</summary>
        public bool IsPlatformIOInstalled()
        {
            if (!File.Exists(venvPython)) return false;
            try
            {
                var (exit, _, _) = RunSync(venvPython, new[] { "-m", "platformio", "--version" }, null, TimeSpan.FromSeconds(15));
                return exit == 0;
            }
            catch { return false; }
        }

        private bool IsPlatformIOInstalledNoLock() => IsPlatformIOInstalled();

        /// <summary>Runs `python -m platformio &lt;args&gt;` from the venv and returns exit/stdout/stderr.</summary>
        public Task<(int exit, string stdout, string stderr)> RunPlatformIOAsync(
            string[] args, string? workDir, TimeSpan timeout, Action<string>? onStdoutLine, CancellationToken ct)
        {
            if (!File.Exists(venvPython))
                throw new InvalidOperationException("Venv not bootstrapped; call EnsurePlatformIOAsync first.");
            var full = new List<string> { "-m", "platformio" };
            full.AddRange(args);
            return RunAsync(venvPython, full.ToArray(), workDir, timeout, onStdoutLine, ct);
        }

        /// <summary>
        /// Executes a CadQuery / Python script in an isolated working directory and returns
        /// stdout, stderr and the list of files the script produced. The script file is
        /// written verbatim — it is the agent's responsibility to construct safe code.
        /// </summary>
        public async Task<PythonScriptResult> RunScriptAsync(
            string scriptCode,
            string workDir,
            TimeSpan timeout,
            Action<string>? onStdoutLine,
            CancellationToken ct)
        {
            if (!IsBootstrapped && !File.Exists(venvPython))
                throw new InvalidOperationException("Python runner is not bootstrapped. Call EnsureBootstrappedAsync first.");

            Directory.CreateDirectory(workDir);
            string scriptPath = Path.Combine(workDir, "script.py");
            File.WriteAllText(scriptPath, scriptCode);

            // Snapshot files before run so we can detect new ones.
            var before = new HashSet<string>(Directory.EnumerateFiles(workDir).Select(Path.GetFileName)!);

            var (exit, stdout, stderr) = await RunAsync(venvPython, new[] { "-u", scriptPath }, workDir, timeout, onStdoutLine, ct);

            var producedFiles = Directory.EnumerateFiles(workDir)
                .Where(f => !before.Contains(Path.GetFileName(f)) && Path.GetFileName(f) != "script.py")
                .Select(f => new FileInfo(f))
                .ToList();

            return new PythonScriptResult
            {
                ExitCode = exit,
                Stdout = stdout,
                Stderr = stderr,
                ScriptPath = scriptPath,
                WorkDirectory = workDir,
                ProducedFiles = producedFiles,
            };
        }

        // ─── helpers ───

        // Pinned CPython version we'll download as a last resort. Chosen because CadQuery ships
        // matching OCP wheels for 3.12 on PyPI.
        private const string PinnedPythonVersion = "3.12.7";

        private string LocalCpythonRoot => Path.Combine(OmniPaths.GlobalPaths.StratumPythonDirectory, "cpython-" + PinnedPythonVersion);
        private string LocalCpythonExe => Path.Combine(LocalCpythonRoot, OperatingSystem.IsWindows() ? "python.exe" : "bin/python3");

        private async Task<string> ResolveOrInstallHostPythonAsync(Action<string> progress, CancellationToken ct)
        {
            try { return ResolveHostPython(); }
            catch (Exception ex)
            {
                progress($"No system Python found ({ex.Message}). Installing CPython {PinnedPythonVersion} into {LocalCpythonRoot}…");
            }

            if (File.Exists(LocalCpythonExe) && TryRunVersion(LocalCpythonExe, null))
            {
                progress("Found previously-installed local CPython.");
                return LocalCpythonExe;
            }

            if (OperatingSystem.IsWindows())
            {
                await InstallCPythonWindowsAsync(progress, ct);
            }
            else
            {
                throw new Exception("Automatic Python install is only implemented on Windows. Install Python 3.10+ manually or set STRATUM_PYTHON.");
            }

            if (!File.Exists(LocalCpythonExe))
                throw new Exception($"Python install completed but {LocalCpythonExe} was not produced.");
            if (!TryRunVersion(LocalCpythonExe, null))
                throw new Exception($"Local CPython at {LocalCpythonExe} failed --version check.");
            return LocalCpythonExe;
        }

        private async Task InstallCPythonWindowsAsync(Action<string> progress, CancellationToken ct)
        {
            // Use the official CPython Windows installer with /quiet + TargetDir so the install is
            // self-contained and won't mutate the user's PATH or system Python.
            string arch = Environment.Is64BitOperatingSystem ? "amd64" : "win32";
            string fileName = $"python-{PinnedPythonVersion}-{arch}.exe";
            string url = $"https://www.python.org/ftp/python/{PinnedPythonVersion}/{fileName}";
            string downloadDir = Path.Combine(OmniPaths.GlobalPaths.StratumPythonDirectory, "installer");
            Directory.CreateDirectory(downloadDir);
            string installerPath = Path.Combine(downloadDir, fileName);

            if (!File.Exists(installerPath) || new FileInfo(installerPath).Length < 1_000_000)
            {
                progress($"Downloading {fileName} (~30 MB)…");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(installerPath);
                await resp.Content.CopyToAsync(fs, ct);
            }
            else
            {
                progress("Using cached installer.");
            }

            Directory.CreateDirectory(LocalCpythonRoot);
            progress("Running silent CPython installer (this can take a couple of minutes)…");
            var args = new[]
            {
                "/quiet",
                "InstallAllUsers=0",
                "PrependPath=0",
                "Include_pip=1",
                "Include_test=0",
                "Include_doc=0",
                "Include_launcher=0",
                "Shortcuts=0",
                $"TargetDir={LocalCpythonRoot}",
            };
            var (exit, stdout, stderr) = await RunAsync(installerPath, args, null, TimeSpan.FromMinutes(15), null, ct);
            if (exit != 0)
                throw new Exception($"CPython installer exited {exit}. stderr:\n{stderr}\nstdout:\n{stdout}");
            progress("CPython installed.");
        }

        private string ResolveHostPython()
        {
            // Try in order: STRATUM_PYTHON env var, our previously-installed local CPython,
            // `python`, `python3`, Windows `py -3`.
            string? env = Environment.GetEnvironmentVariable("STRATUM_PYTHON");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

            if (File.Exists(LocalCpythonExe) && TryRunVersion(LocalCpythonExe, null)) return LocalCpythonExe;

            foreach (var candidate in new[] { "python", "python3" })
            {
                if (TryRunVersion(candidate, null)) return candidate;
            }
            if (OperatingSystem.IsWindows() && TryRunVersion("py", new[] { "-3", "--version" })) return "py";

            throw new Exception("No Python interpreter found on PATH.");
        }

        private static bool TryRunVersion(string exe, string[]? args)
        {
            try
            {
                var (exit, _, _) = RunSync(exe, args ?? new[] { "--version" }, null, TimeSpan.FromSeconds(5));
                return exit == 0;
            }
            catch { return false; }
        }

        private static string BuildRequirements() =>
            // CadQuery wheel ships with OCP (OpenCascade) and trame deps. Pin minor versions.
            // Numpy is pinned <2 because CadQuery 2.5.x + cadquery-ocp 7.7.x have ABI
            // incompatibilities with numpy 2.x on Python 3.9 (DLL load / import failure).
            string.Join("\n", new[]
            {
                "cadquery>=2.4,<3.0",
                "numpy>=1.24,<2.0",
                // Newer cadquery uses cadquery-ocp; let pip resolve it.
            }) + "\n";

        private static (int exit, string stdout, string stderr) RunSync(string exe, string[] args, string? workDir, TimeSpan timeout)
        {
            using var p = StartProcess(exe, args, workDir);
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(true); } catch { }
                throw new TimeoutException($"Process {exe} timed out after {timeout.TotalSeconds}s");
            }
            return (p.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
        }

        private static Task<(int exit, string stdout, string stderr)> RunAsync(
            string exe, string[] args, string? workDir, TimeSpan timeout, Action<string>? onStdoutLine, CancellationToken ct)
            => RunAsync(exe, args, workDir, timeout, onStdoutLine, null, ct);

        private static async Task<(int exit, string stdout, string stderr)> RunAsync(
            string exe, string[] args, string? workDir, TimeSpan timeout,
            Action<string>? onStdoutLine, Action<string>? onStderrLine, CancellationToken ct)
        {
            using var p = StartProcess(exe, args, workDir);
            var stdoutSb = new System.Text.StringBuilder();
            var stderrSb = new System.Text.StringBuilder();
            var stdoutDone = new TaskCompletionSource<bool>();
            var stderrDone = new TaskCompletionSource<bool>();

            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) { stdoutDone.TrySetResult(true); return; }
                stdoutSb.AppendLine(e.Data);
                try { onStdoutLine?.Invoke(e.Data); } catch { }
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) { stderrDone.TrySetResult(true); return; }
                stderrSb.AppendLine(e.Data);
                try { onStderrLine?.Invoke(e.Data); } catch { }
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException($"Process {exe} exceeded timeout {timeout.TotalSeconds}s");
            }

            await Task.WhenAny(Task.WhenAll(stdoutDone.Task, stderrDone.Task), Task.Delay(2000));
            return (p.ExitCode, stdoutSb.ToString(), stderrSb.ToString());
        }

        private static Process StartProcess(string exe, string[] args, string? workDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir ?? Environment.CurrentDirectory,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            // Ensure venv-activated PATH-like behaviour: prepend Scripts/bin so any
            // cadquery sub-tools the script invokes resolve to the venv.
            return Process.Start(psi) ?? throw new Exception($"Failed to start {exe}");
        }
    }

    public class PythonScriptResult
    {
        public int ExitCode;
        public string Stdout = "";
        public string Stderr = "";
        public string ScriptPath = "";
        public string WorkDirectory = "";
        public List<FileInfo> ProducedFiles = new();
        public bool Success => ExitCode == 0;
    }
}
