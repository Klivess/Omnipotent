using System.Diagnostics;
using System.IO.Compression;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Games.Rust
{
    /// <summary>
    /// Provisions the official Windows Rust dedicated server through SteamCMD (app 258550). SteamCMD is
    /// downloaded once into KliveGames/Runtimes and shared, while every server installs into its own
    /// instance directory.
    /// </summary>
    public sealed class RustInstaller
    {
        private const string SteamCmdUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public RustInstaller()
        {
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = TimeSpan.FromMinutes(30),
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("KliveGames/1.0 (+https://klive.dev)");
        }

        public async Task InstallAsync(GameServerInstance inst, IProgress<string> progress, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                string steamCmd = await EnsureSteamCmdAsync(progress, ct);
                progress.Report("Downloading the latest official Rust dedicated server through SteamCMD…");

                var psi = new ProcessStartInfo
                {
                    FileName = steamCmd,
                    WorkingDirectory = Path.GetDirectoryName(steamCmd)!,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("+force_install_dir");
                psi.ArgumentList.Add(inst.ServerDirectory);
                psi.ArgumentList.Add("+login");
                psi.ArgumentList.Add("anonymous");
                psi.ArgumentList.Add("+app_update");
                psi.ArgumentList.Add("258550");
                psi.ArgumentList.Add("validate");
                psi.ArgumentList.Add("+quit");

                using var process = new Process { StartInfo = psi };
                process.OutputDataReceived += (_, e) => ReportSteamCmdLine(e.Data, progress);
                process.ErrorDataReceived += (_, e) => ReportSteamCmdLine(e.Data, progress);

                if (!process.Start()) throw new Exception("SteamCMD could not be started.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try
                {
                    await process.WaitForExitAsync(ct);
                    process.WaitForExit();
                }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    throw;
                }

                if (process.ExitCode != 0)
                    throw new Exception($"SteamCMD exited with code {process.ExitCode} while installing Rust.");

                string executable = Path.Combine(inst.ServerDirectory, "RustDedicated.exe");
                if (!File.Exists(executable))
                    throw new Exception("SteamCMD completed but RustDedicated.exe was not installed.");

                inst.LaunchTarget = executable;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<string> EnsureSteamCmdAsync(IProgress<string> progress, CancellationToken ct)
        {
            string runtimeDir = OmniPaths.GetPath(Path.Combine(OmniPaths.GlobalPaths.KliveGamesRuntimesDirectory, "steamcmd"));
            string executable = Path.Combine(runtimeDir, "steamcmd.exe");
            if (File.Exists(executable)) return executable;

            Directory.CreateDirectory(runtimeDir);
            string archive = Path.Combine(runtimeDir, "steamcmd.zip");
            progress.Report("Downloading SteamCMD…");
            using (var response = await _http.GetAsync(SteamCmdUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(output, ct);
            }

            progress.Report("Extracting SteamCMD…");
            ZipFile.ExtractToDirectory(archive, runtimeDir, overwriteFiles: true);
            try { File.Delete(archive); } catch { }

            if (!File.Exists(executable))
                throw new Exception("SteamCMD was downloaded but steamcmd.exe could not be found.");
            return executable;
        }

        private static void ReportSteamCmdLine(string? line, IProgress<string> progress)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            string trimmed = line.Trim();
            if (trimmed.StartsWith("Update state", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("Success!", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("ERROR!", StringComparison.OrdinalIgnoreCase))
                progress.Report(trimmed);
        }
    }
}
